using System.Runtime.InteropServices;
using EchoDiar.Core.Audio;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace EchoDiar.Platform.Audio;

/// <summary>An active capture endpoint: its endpoint id (for <see cref="MicCapture"/>) and friendly name.</summary>
public readonly record struct AudioInputDevice(string Id, string Name);

/// <summary>
/// Route changes during a recording (the Windows counterpart of macOS AudioRouteMonitor), from an
/// IMMNotificationClient: default device changes, endpoint state changes, added and removed endpoints, and
/// device format (sample rate) changes. Notifications within 300 ms are coalesced and the state is compared as a
/// whole, since a disconnect fires its notifications (state changed, removed, defaults changed) in no guaranteed order.
/// <para>
/// The input in use is the picked endpoint while it is active, else the default capture endpoint (eConsole, as
/// <see cref="MicCapture"/>); the output is the default render endpoint (eConsole, as <see cref="LoopbackCapture.Endpoint"/>).
/// </para>
/// <para>
/// Events are raised on a thread-pool thread, never inside a COM notification and outside any lock; a handler must
/// not throw and should hop to its owner's own serialization. One can still arrive shortly after <see cref="Stop"/>
/// returns, so a handler checks its own session generation.
/// </para>
/// </summary>
public sealed class AudioRouteMonitor : IAudioRouteSource, IDisposable
{
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(300);

    // Clients whose unregistration failed: the audio service may still call them, so they are never collected.
    private static readonly List<Listener> Unreleased = [];

    private readonly object _gate = new(); // serializes Start, Stop and Dispose (held across register/unregister)
    private readonly object _lock = new(); // the state shared with the coalescing timer
    private Listener? _client; // registered once, on the first Start, until Dispose
    private volatile bool _running;
    private string? _pinnedId;
    private Endpoint _input;
    private Endpoint _output;
    private bool _disposed;

    /// <summary>The default output endpoint, or its sample rate, changed.</summary>
    public event Action? OutputChanged;

    /// <summary>The input in use was replaced (a new default, or the picked endpoint is back) or its sample rate changed.</summary>
    public event Action? InputChanged;

    /// <summary>The input in use was removed, unplugged or disabled; input now follows the default.</summary>
    public event Action? InputLost;

    /// <summary>
    /// Starts listening, after stopping any previous run. <paramref name="inputEndpointId"/>: the picked mic (null =
    /// the default input); while it is active it is the input in use. Returns false, staying stopped, when the
    /// endpoint notifications cannot be registered (no audio service).
    /// <para>
    /// The notification client is registered on the first Start and stays registered, rooted by this monitor, until
    /// <see cref="Dispose"/>: Start and Stop only reset the routes and turn the events on or off. Registering a new
    /// client per run left the audio service with a pointer to a client the GC could free.
    /// </para>
    /// </summary>
    public bool Start(string? inputEndpointId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopLocked();
            try
            {
                OnMta(() =>
                {
                    var client = _client ??= Register();
                    var input = ResolveInput(client.Enumerator, inputEndpointId);
                    var output = DefaultEndpoint(client.Enumerator, EDataFlow.eRender);
                    lock (_lock)
                    {
                        _pinnedId = inputEndpointId;
                        _input = input;
                        _output = output;
                        _running = true;
                    }
                });
                return true;
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    /// <summary>Stops the events and the coalescing timer; the client stays registered. Safe to call repeatedly.</summary>
    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    /// <summary>Stops and unregisters the notification client.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopLocked();
            if (_client is { } client) OnMta(client.Unregister);
            _client = null;
        }
    }

    /// <summary>
    /// The active capture endpoints, with their friendly names (the id when it has none). Endpoints that cannot be
    /// read (removed meanwhile) are skipped; no audio service gives an empty list. Blocking: call off the UI thread.
    /// </summary>
    public static IReadOnlyList<AudioInputDevice> ListInputDevices()
    {
        var list = new List<AudioInputDevice>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.EnumAudioEndpoints(EDataFlow.eCapture, DEVICE_STATE.DEVICE_STATE_ACTIVE, out devices);
            devices.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    devices.Item(i, out device);
                    var id = Id(device);
                    list.Add(new AudioInputDevice(id, CaptureLoop.FriendlyName(device) ?? id));
                }
                catch (COMException)
                {
                    // The endpoint went away meanwhile: skip it.
                }
                finally
                {
                    if (device is not null) Marshal.ReleaseComObject(device);
                }
            }
        }
        catch (COMException)
        {
            // No audio service or endpoint list: what was read so far.
        }
        finally
        {
            if (devices is not null) Marshal.ReleaseComObject(devices);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
        return list;
    }

    /// <summary>An endpoint id (null = none) and its device format's sample rate (0 when unknown).</summary>
    private readonly record struct Endpoint(string? Id, uint Rate);

    private void StopLocked()
    {
        lock (_lock) _running = false;
        _client?.Disarm();
    }

    /// <summary>
    /// A new client, registered with its own enumerator. Throws, registering nothing, without an audio service. On an
    /// MTA thread.
    /// </summary>
    private Listener Register()
    {
        var client = new Listener(this, UniqueEnumerator());
        client.Enumerator.RegisterEndpointNotificationCallback(client);
        return client;
    }

    /// <summary>
    /// An enumerator wrapper of its own: the runtime shares one wrapper per COM object, and the enumerator may be a
    /// singleton, so a release elsewhere (ListInputDevices, AudioSessions, the captures) could otherwise end the
    /// wrapper this monitor keeps for its lifetime.
    /// </summary>
    private static IMMDeviceEnumerator UniqueEnumerator()
    {
        var shared = new MMDeviceEnumerator();
        var unknown = Marshal.GetIUnknownForObject(shared);
        try
        {
            return (IMMDeviceEnumerator)Marshal.GetUniqueObjectForIUnknown(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
            Marshal.ReleaseComObject(shared);
        }
    }

    /// <summary>The coalescing timer's callback (thread pool, MTA): compares the routes with the last ones.</summary>
    private void Evaluate(Listener listener)
    {
        var events = new List<Action?>(2);
        try
        {
            lock (_lock)
            {
                listener.ClearPending(); // a notification from now on schedules another evaluation
                if (!_running) return;

                var enumerator = listener.Enumerator;
                var inputLost = _input.Id is not null && !IsActive(enumerator, _input.Id);
                var input = ResolveInput(enumerator, _pinnedId);
                if (inputLost) events.Add(InputLost);
                else if (input != _input) events.Add(InputChanged);
                _input = input;

                var output = DefaultEndpoint(enumerator, EDataFlow.eRender);
                if (output != _output) events.Add(OutputChanged);
                _output = output;
            }
        }
        catch (Exception)
        {
            // An exception out of a timer callback ends the process; the next notification evaluates again.
            return;
        }
        foreach (var handler in events) handler?.Invoke();
    }

    /// <summary>Runs COM work on an MTA thread, so the enumerator never belongs to the app's STA UI thread.</summary>
    private static void OnMta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) action();
        else Task.Run(action).GetAwaiter().GetResult();
    }

    /// <summary>The picked endpoint while it is active, else the default capture endpoint.</summary>
    private static Endpoint ResolveInput(IMMDeviceEnumerator enumerator, string? pinnedId)
    {
        if (pinnedId is not null)
        {
            IMMDevice? device = null;
            try
            {
                enumerator.GetDevice(pinnedId, out device);
                if (State(device) == DEVICE_STATE.DEVICE_STATE_ACTIVE) return new Endpoint(pinnedId, Rate(device));
            }
            catch (COMException)
            {
                // Gone: follow the default.
            }
            finally
            {
                if (device is not null) Marshal.ReleaseComObject(device);
            }
        }
        return DefaultEndpoint(enumerator, EDataFlow.eCapture);
    }

    /// <summary>The default endpoint (eConsole) for <paramref name="flow"/>; no id when there is none.</summary>
    private static Endpoint DefaultEndpoint(IMMDeviceEnumerator enumerator, EDataFlow flow)
    {
        IMMDevice? device = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(flow, ERole.eConsole, out device);
            return new Endpoint(Id(device), Rate(device));
        }
        catch (COMException)
        {
            return default; // E_NOTFOUND: no endpoint of that flow
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
        }
    }

    /// <summary>Whether the endpoint exists and is active (the counterpart of DeviceIsAlive); one that cannot be read is not.</summary>
    private static bool IsActive(IMMDeviceEnumerator enumerator, string id)
    {
        IMMDevice? device = null;
        try
        {
            enumerator.GetDevice(id, out device);
            return State(device) == DEVICE_STATE.DEVICE_STATE_ACTIVE;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
        }
    }

    private static unsafe DEVICE_STATE State(IMMDevice device)
    {
        DEVICE_STATE state;
        device.GetState(&state);
        return state;
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

    /// <summary>The device format's sample rate (PKEY_AudioEngine_DeviceFormat), or 0 when it cannot be read.</summary>
    private static unsafe uint Rate(IMMDevice device)
    {
        IPropertyStore? store = null;
        var value = default(PROPVARIANT);
        try
        {
            device.OpenPropertyStore(STGM.STGM_READ, out store);
            var key = PInvoke.PKEY_AudioEngine_DeviceFormat;
            store.GetValue(&key, out value);
            if (value.vt != VARENUM.VT_BLOB || value.blob.pBlobData == null || value.blob.cbSize < sizeof(WAVEFORMATEX)) return 0;
            return ((WAVEFORMATEX*)value.blob.pBlobData)->nSamplesPerSec;
        }
        catch (COMException)
        {
            return 0;
        }
        finally
        {
            PInvoke.PropVariantClear(ref value);
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>
    /// The registered notification client: the enumerator it is registered with and the coalescing timer, for the
    /// monitor's lifetime. Its callbacks run on an audio service thread and only arm the timer: no COM call, no lock,
    /// no throw.
    /// </summary>
    private sealed class Listener : IMMNotificationClient
    {
        private readonly AudioRouteMonitor _monitor;
        private readonly Timer _timer;
        private int _pending;

        public Listener(AudioRouteMonitor monitor, IMMDeviceEnumerator enumerator)
        {
            _monitor = monitor;
            Enumerator = enumerator;
            _timer = new Timer(_ => monitor.Evaluate(this), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Never released by hand: the runtime owns the wrapper, which lives as long as this client.</summary>
        public IMMDeviceEnumerator Enumerator { get; }

        public void ClearPending() => Volatile.Write(ref _pending, 0);

        /// <summary>Cancels a scheduled evaluation; one already running finds the monitor stopped.</summary>
        public void Disarm()
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            ClearPending();
        }

        /// <summary>
        /// Unregisters (not from a notification) and stops the timer. On an MTA thread. When unregistering fails the
        /// audio service may still call this client: it is then kept alive for the process's lifetime.
        /// </summary>
        public void Unregister()
        {
            try
            {
                Enumerator.UnregisterEndpointNotificationCallback(this);
            }
            catch (Exception)
            {
                lock (Unreleased) Unreleased.Add(this);
            }
            _timer.Dispose();
        }

        public void OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState) => Schedule();

        public void OnDeviceAdded(PCWSTR pwstrDeviceId) => Schedule();

        public void OnDeviceRemoved(PCWSTR pwstrDeviceId) => Schedule();

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId) => Schedule();

        public void OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key)
        {
            var format = PInvoke.PKEY_AudioEngine_DeviceFormat;
            if (key.fmtid == format.fmtid && key.pid == format.pid) Schedule();
        }

        /// <summary>While the monitor runs, the first notification of a burst arms the timer; the others until it fires are absorbed.</summary>
        private void Schedule()
        {
            try
            {
                if (_monitor._running && Interlocked.Exchange(ref _pending, 1) == 0) _timer.Change(Coalesce, Timeout.InfiniteTimeSpan);
            }
            catch (Exception)
            {
                // Disposed meanwhile (disposing): nothing to evaluate.
            }
        }
    }
}
