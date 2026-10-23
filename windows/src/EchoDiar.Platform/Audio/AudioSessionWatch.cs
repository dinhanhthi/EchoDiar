using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace EchoDiar.Platform.Audio;

/// <summary>An app audio session on an active endpoint: its process, direction and whether it is active (streaming).</summary>
internal readonly record struct AppSession(int Pid, EDataFlow Flow, bool Active);

/// <summary>
/// The audio sessions of the active capture (and optionally render) endpoints, for <see cref="MeetingDetector"/> and
/// <see cref="ProcessActivityWatch"/> (the counterpart of their macOS process-object listeners). Each endpoint has an
/// IAudioSessionManager2 with an IAudioSessionNotification (new sessions), each session an IAudioSessionEvents (state
/// changes). A notification schedules one refresh 500 ms later (later ones meanwhile are absorbed); a 2 s poll
/// schedules one too, as the safety net, and is also what picks up added and removed endpoints. A refresh reconciles
/// the registrations with the endpoints and sessions now, then hands the session states to the owner's refresh.
/// <para>
/// Threading: COM work runs on MTA threads only (session notifications are not delivered otherwise): the thread
/// pool, or <see cref="OnMta"/> from <see cref="Stop"/> and <see cref="Dispose"/>. The owner's refresh runs inside the
/// watch's lock on a thread-pool thread; the actions it returns run afterwards outside that lock on the same thread,
/// in order (one refresh's actions never interleave with another's), and are skipped once a <see cref="Stop"/> has
/// begun (one already running may still finish after Stop returns). Notification callbacks only arm the debounce
/// timer: no COM call, no lock, no throw.
/// </para>
/// <para>
/// A listener whose unregistration failed (the endpoint vanished) stays referenced by the audio service, which keeps
/// it alive; its owner is cleared on <see cref="Dispose"/>, so it no longer keeps the watch alive or refreshes.
/// </para>
/// </summary>
internal sealed class AudioSessionWatch : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly EDataFlow[] _flows;
    private readonly Func<IReadOnlyList<AppSession>, IReadOnlyList<Action>> _refresh;
    private readonly Action _stopped;
    private readonly Action<string>? _log;
    private readonly object _lock = new(); // the registrations, the owner's state and _running
    private readonly object _raise = new(); // keeps each refresh's actions in order, outside _lock
    private readonly Dictionary<string, Endpoint> _endpoints = [];
    private readonly Timer _debounce;
    private readonly Timer _poll;
    private readonly Timer _deadline;
    private readonly Listener _listener;
    private int _pending;
    private volatile bool _running;
    private long _generation;
    private bool _disposed;

    /// <param name="includeRender">Also watch the render endpoints (capture endpoints are always watched).</param>
    /// <param name="refresh">The owner's refresh, inside the watch's lock: the session states now, and the events it
    /// raises (run after the lock is released).</param>
    /// <param name="stopped">The owner forgets its state, inside the watch's lock, when a run stops.</param>
    /// <param name="log">Diagnostics: quick, never throws (called inside the lock).</param>
    public AudioSessionWatch(
        bool includeRender,
        Func<IReadOnlyList<AppSession>, IReadOnlyList<Action>> refresh,
        Action stopped,
        Action<string>? log)
    {
        _flows = includeRender ? [EDataFlow.eCapture, EDataFlow.eRender] : [EDataFlow.eCapture];
        _refresh = refresh;
        _stopped = stopped;
        _log = log;
        _listener = new Listener(this);
        _debounce = new Timer(_ => Refresh(debounced: true));
        _poll = new Timer(_ => Schedule());
        _deadline = new Timer(_ => Refresh(debounced: false));
    }

    /// <summary>
    /// Starts the poll and the first refresh (at once, on the thread pool, which registers everything). Does not
    /// block. Safe to call repeatedly.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            _running = true;
            Interlocked.Increment(ref _generation);
            _poll.Change(PollInterval, PollInterval);
            Volatile.Write(ref _pending, 1);
            _debounce.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Unregisters every notification, releases every session manager and session, and calls the owner's stopped
    /// action; no owner refresh or action starts afterwards (one already running may finish). Safe to call
    /// repeatedly, and from an owner's action.
    /// </summary>
    public void Stop() => OnMta(() =>
    {
        lock (_lock) StopLocked();
    });

    /// <summary>Stops, and disposes the timers.</summary>
    public void Dispose()
    {
        OnMta(() =>
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                StopLocked();
                _listener.Owner = null; // a registration that failed to unregister no longer keeps this watch alive
            }
        });
        _debounce.Dispose();
        _poll.Dispose();
        _deadline.Dispose();
    }

    /// <summary>A refresh (not debounced) after <paramref name="delay"/>, replacing an earlier request: the owner's own deadline.</summary>
    public void RefreshAfter(TimeSpan delay)
    {
        lock (_lock)
        {
            if (_running) _deadline.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs COM work on an MTA thread: inline on one, else on the thread pool (blocking until it ends).</summary>
    private static void OnMta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) action();
        else Task.Run(action).GetAwaiter().GetResult();
    }

    /// <summary>A notification or a poll tick: the first one arms the debounce timer, the others until it fires are absorbed.</summary>
    private void Schedule()
    {
        try
        {
            if (_running && Interlocked.Exchange(ref _pending, 1) == 0) _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile: nothing to refresh.
        }
    }

    /// <summary>A timer's callback (thread pool, MTA).</summary>
    private void Refresh(bool debounced)
    {
        // Taken before _lock: a later refresh waits here until this one's actions ran, so events stay in order.
        lock (_raise)
        {
            IReadOnlyList<Action> actions;
            long generation;
            try
            {
                lock (_lock)
                {
                    if (debounced) Volatile.Write(ref _pending, 0); // a notification from now on schedules another refresh
                    if (!_running) return;
                    generation = Interlocked.Read(ref _generation);
                    actions = _refresh(Sync());
                }
            }
            catch (Exception e)
            {
                // An exception out of a timer callback ends the process; the next poll refreshes again.
                _log?.Invoke($"session watch: refresh failed, {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
                return;
            }
            Raise(actions, generation);
        }
    }

    private void Raise(IReadOnlyList<Action> actions, long generation)
    {
        foreach (var action in actions)
        {
            if (!_running || Interlocked.Read(ref _generation) != generation) return; // stopped meanwhile
            try
            {
                action();
            }
            catch (Exception e)
            {
                // A handler that throws must not end the process (timer thread) or skip the next event.
                _log?.Invoke($"session watch: a handler threw, {e.GetType().Name} {e.Message}");
            }
        }
    }

    private void StopLocked()
    {
        if (!_running) return;
        _running = false;
        Interlocked.Increment(ref _generation);
        _poll.Change(Timeout.Infinite, Timeout.Infinite);
        _debounce.Change(Timeout.Infinite, Timeout.Infinite);
        _deadline.Change(Timeout.Infinite, Timeout.Infinite);
        Volatile.Write(ref _pending, 0);
        foreach (var endpoint in _endpoints.Values) endpoint.Release(_listener, _log);
        _endpoints.Clear();
        _stopped();
    }

    /// <summary>
    /// Registers new endpoints and sessions, releases those that are gone or expired, and returns the session state of
    /// every app session (not the system sounds session) now. Endpoints or sessions that cannot be read are skipped.
    /// </summary>
    private List<AppSession> Sync()
    {
        var seen = new HashSet<string>();
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            foreach (var flow in _flows) AddEndpoints(enumerator, flow, seen);
        }
        catch (COMException e)
        {
            _log?.Invoke($"session watch: no endpoint list, 0x{e.HResult:X8}");
            // No audio service: keep what is registered; the next poll tries again.
            foreach (var id in _endpoints.Keys) seen.Add(id);
        }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }

        foreach (var gone in _endpoints.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _endpoints[gone].Release(_listener, _log);
            _endpoints.Remove(gone);
            _log?.Invoke($"session watch: endpoint removed {gone}");
        }

        var states = new List<AppSession>();
        foreach (var endpoint in _endpoints.Values) endpoint.Sync(_listener, _log, states);
        return states;
    }

    /// <summary>Adds the active endpoints of <paramref name="flow"/> to <paramref name="seen"/>, registering new ones.</summary>
    private void AddEndpoints(IMMDeviceEnumerator enumerator, EDataFlow flow, HashSet<string> seen)
    {
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator.EnumAudioEndpoints(flow, DEVICE_STATE.DEVICE_STATE_ACTIVE, out devices);
            devices.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    devices.Item(i, out device);
                    var id = Id(device);
                    seen.Add(id);
                    if (_endpoints.ContainsKey(id)) continue;
                    _endpoints[id] = Endpoint.Register(id, flow, device, _listener);
                    _log?.Invoke($"session watch: endpoint added {flow} {id}");
                }
                catch (Exception e) when (e is COMException or InvalidCastException)
                {
                    // The endpoint went away meanwhile (or has no session manager): the next poll tries again.
                }
                finally
                {
                    if (device is not null) Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            if (devices is not null) Marshal.ReleaseComObject(devices);
        }
    }

    private static unsafe string Id(IMMDevice device)
    {
        PWSTR id;
        device.GetId(&id);
        try
        {
            return id.ToString();
        }
        finally
        {
            PInvoke.CoTaskMemFree(id.Value);
        }
    }

    /// <summary>
    /// A wrapper of its own for a COM object this watch keeps: the runtime shares one wrapper per COM object, so a
    /// release elsewhere (<see cref="AudioSessions"/>, called by the recording engine meanwhile) could otherwise end
    /// it. Releases <paramref name="shared"/>.
    /// </summary>
    private static T Unique<T>(object shared) where T : class
    {
        var unknown = Marshal.GetIUnknownForObject(shared);
        try
        {
            return (T)Marshal.GetUniqueObjectForIUnknown(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
            Marshal.ReleaseComObject(shared);
        }
    }

    /// <summary>One endpoint's session manager, with its notification, and the sessions with events registered.</summary>
    private sealed class Endpoint
    {
        private readonly string _id;
        private readonly EDataFlow _flow;
        private readonly IAudioSessionManager2 _manager;
        private readonly Dictionary<string, IAudioSessionControl2> _sessions = [];

        private Endpoint(string id, EDataFlow flow, IAudioSessionManager2 manager)
        {
            _id = id;
            _flow = flow;
            _manager = manager;
        }

        /// <summary>Activates the session manager and registers for new sessions; throws (registering nothing) on failure.</summary>
        public static Endpoint Register(string id, EDataFlow flow, IMMDevice device, Listener listener)
        {
            device.Activate(typeof(IAudioSessionManager2).GUID, CLSCTX.CLSCTX_ALL, null, out var activated);
            var manager = Unique<IAudioSessionManager2>(activated);
            try
            {
                manager.RegisterSessionNotification(listener);
            }
            catch
            {
                Marshal.ReleaseComObject(manager);
                throw;
            }
            // Sync calls GetSessionEnumerator next: the manager notifies only once it has been called.
            return new Endpoint(id, flow, manager);
        }

        /// <summary>Registers new sessions, releases expired and vanished ones, and adds the live ones' states.</summary>
        public void Sync(Listener listener, Action<string>? log, List<AppSession> states)
        {
            var seen = new HashSet<string>();
            IAudioSessionEnumerator? sessions = null;
            try
            {
                sessions = _manager.GetSessionEnumerator();
                sessions.GetCount(out var count);
                for (var i = 0; i < count; i++) AddSession(sessions, i, listener, seen, states);
            }
            catch (Exception e) when (e is COMException or InvalidCastException)
            {
                // The endpoint is going away: the next poll removes it. Keep what is registered until then.
                foreach (var key in _sessions.Keys) seen.Add(key);
            }
            finally
            {
                if (sessions is not null) Marshal.ReleaseComObject(sessions);
            }
            foreach (var gone in _sessions.Keys.Where(key => !seen.Contains(key)).ToList())
            {
                ReleaseSession(_sessions[gone], listener, log);
                _sessions.Remove(gone);
            }
        }

        /// <summary>Unregisters and releases the sessions and the manager.</summary>
        public void Release(Listener listener, Action<string>? log)
        {
            foreach (var session in _sessions.Values) ReleaseSession(session, listener, log);
            _sessions.Clear();
            try
            {
                _manager.UnregisterSessionNotification(listener);
            }
            catch (Exception e)
            {
                log?.Invoke($"session watch: unregistering {_flow} {_id} failed, 0x{e.HResult:X8}");
            }
            Marshal.ReleaseComObject(_manager);
        }

        private void AddSession(
            IAudioSessionEnumerator sessions, int index, Listener listener, HashSet<string> seen, List<AppSession> states)
        {
            IAudioSessionControl? session = null;
            try
            {
                sessions.GetSession(index, out session);
                var control = (IAudioSessionControl2)session; // the same wrapper: released once below
                if (control.IsSystemSoundsSession().Value == 0) return; // S_OK: system sounds, no app
                control.GetState(out var state);
                control.GetProcessId(out var pid);
                if (state == AudioSessionState.AudioSessionStateExpired || pid == 0 || pid > int.MaxValue) return;
                var key = InstanceId(control);
                seen.Add(key);
                states.Add(new AppSession((int)pid, _flow, state == AudioSessionState.AudioSessionStateActive));
                if (_sessions.ContainsKey(key)) return;
                var held = Unique<IAudioSessionControl2>(session);
                session = null; // released by Unique
                try
                {
                    held.RegisterAudioSessionNotification(listener);
                }
                catch
                {
                    Marshal.ReleaseComObject(held);
                    throw;
                }
                _sessions[key] = held;
            }
            catch (Exception e) when (e is COMException or InvalidCastException)
            {
                // The session expired meanwhile: skip it.
            }
            finally
            {
                if (session is not null) Marshal.ReleaseComObject(session);
            }
        }

        private static void ReleaseSession(IAudioSessionControl2 session, Listener listener, Action<string>? log)
        {
            try
            {
                session.UnregisterAudioSessionNotification(listener);
            }
            catch (Exception e)
            {
                log?.Invoke($"session watch: unregistering a session failed, 0x{e.HResult:X8}");
            }
            Marshal.ReleaseComObject(session);
        }

        private static unsafe string InstanceId(IAudioSessionControl2 control)
        {
            control.GetSessionInstanceIdentifier(out var id);
            try
            {
                return id.ToString();
            }
            finally
            {
                PInvoke.CoTaskMemFree(id.Value);
            }
        }
    }

    /// <summary>
    /// The notification client of the managers (new sessions) and the sessions (state changes, disconnects). Called on
    /// audio service threads: it only schedules a refresh, through a clearable owner so a lingering registration does not keep
    /// the watch alive.
    /// </summary>
    private sealed class Listener(AudioSessionWatch owner) : IAudioSessionNotification, IAudioSessionEvents
    {
        public volatile AudioSessionWatch? Owner = owner;

        public void OnSessionCreated(IAudioSessionControl NewSession)
        {
            // Not kept: the refresh enumerates it. Releasing our wrapper here is a Release, not an unregistration.
            try
            {
                Marshal.ReleaseComObject(NewSession);
            }
            catch (Exception)
            {
                // Not a wrapper (null): nothing to release.
            }
            Owner?.Schedule();
        }

        public void OnStateChanged(AudioSessionState NewState) => Owner?.Schedule();

        public void OnSessionDisconnected(AudioSessionDisconnectReason DisconnectReason) => Owner?.Schedule();

        public unsafe void OnDisplayNameChanged(PCWSTR NewDisplayName, Guid* EventContext) { }

        public unsafe void OnIconPathChanged(PCWSTR NewIconPath, Guid* EventContext) { }

        public unsafe void OnSimpleVolumeChanged(float NewVolume, BOOL NewMute, Guid* EventContext) { }

        public unsafe void OnChannelVolumeChanged(uint ChannelCount, float[] NewChannelVolumeArray, uint ChangedChannel, Guid* EventContext) { }

        public unsafe void OnGroupingParamChanged(Guid* NewGroupingParam, Guid* EventContext) { }
    }

    /// <summary>Milliseconds-precise monotonic time, for the owners' deadlines.</summary>
    public static TimeSpan Now => Stopwatch.GetElapsedTime(0);
}
