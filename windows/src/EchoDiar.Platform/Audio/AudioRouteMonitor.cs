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
/// The input in use is the picked endpoint while it is active, else <see cref="DefaultMicId"/> (as
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

    // The container id of the devices built into the computer (its own mic and speakers).
    private const string ComputerContainer = "00000000-0000-0000-ffff-ffffffffffff";

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
    /// Diagnostics (null: none): each notification, start and evaluation as a line. Called on audio service and
    /// thread-pool threads, inside the monitor's lock: it must be quick and never throw.
    /// </summary>
    public Action<string>? Log { get; set; }

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
                    var registered = _client is null;
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
                    Log?.Invoke($"route monitor: start, picked {inputEndpointId ?? "none (default)"}, client {(registered ? "registered" : "reused")}, input {input}, output {output}");
                });
                return true;
            }
            catch (COMException e)
            {
                Log?.Invoke($"route monitor: start failed, {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
                return false;
            }
        }
    }

    /// <summary>Stops the events and the coalescing timer; the client stays registered. Safe to call repeatedly.</summary>
    public void Stop()
    {
        lock (_gate) StopLocked();
        Log?.Invoke("route monitor: stop");
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
    private readonly record struct Endpoint(string? Id, uint Rate)
    {
        public override string ToString() => $"{Id ?? "none"} ({Rate} Hz)";
    }

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
                var state = _input.Id is null ? null : StateOf(enumerator, _input.Id);
                var inputLost = _input.Id is not null && state != DEVICE_STATE.DEVICE_STATE_ACTIVE;
                var input = ResolveInput(enumerator, _pinnedId);
                var inputEvent = inputLost ? nameof(InputLost) : input != _input ? nameof(InputChanged) : null;
                if (inputLost) events.Add(InputLost);
                else if (input != _input) events.Add(InputChanged);

                var output = DefaultEndpoint(enumerator, EDataFlow.eRender);
                if (output != _output) events.Add(OutputChanged);
                Log?.Invoke($"route monitor: evaluate, input {_input} state {(state is { } s ? $"0x{(uint)s:X}" : "unreadable")} -> {input}, " +
                    $"output {_output} -> {output}, raises [{string.Join(", ", new[] { inputEvent, output != _output ? nameof(OutputChanged) : null }.OfType<string>())}]");
                _input = input;
                _output = output;
            }
        }
        catch (Exception e)
        {
            // An exception out of a timer callback ends the process; the next notification evaluates again.
            Log?.Invoke($"route monitor: evaluate failed, {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
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

    /// <summary>The picked endpoint while it is active, else <see cref="DefaultMicId"/>.</summary>
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
        IMMDevice? fallback = null;
        try
        {
            var id = DefaultMicId(enumerator);
            enumerator.GetDevice(id, out fallback);
            return new Endpoint(id, Rate(fallback));
        }
        catch (COMException)
        {
            return default; // E_NOTFOUND: no capture endpoint
        }
        finally
        {
            if (fallback is not null) Marshal.ReleaseComObject(fallback);
        }
    }

    /// <summary>
    /// The capture endpoint used when none is picked (the counterpart of macOS <c>MicCapture.inputDevice(uid:)</c>): the
    /// default one (eConsole), unless it is the Bluetooth hands-free mic (enumerator BTHHFENUM) of the headset that is
    /// also the default render endpoint (same container id). Opening that one switches the headset to its hands-free
    /// profile, which drops its playback quality, so <see cref="ReplacementMicId"/> is used instead when there is one.
    /// Throws when there is no capture endpoint, as GetDefaultAudioEndpoint does.
    /// </summary>
    internal static string DefaultMicId(IMMDeviceEnumerator enumerator)
    {
        IMMDevice? input = null;
        IMMDevice? output = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out input);
            var id = Id(input);
            if (!string.Equals(Text(input, PInvoke.PKEY_Device_EnumeratorName), "BTHHFENUM", StringComparison.OrdinalIgnoreCase)) return id;
            var container = Text(input, PInvoke.PKEY_Device_ContainerId);
            if (container is null || container == ComputerContainer) return id;
            try
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out output);
            }
            catch (COMException)
            {
                return id; // no render endpoint
            }
            return container == Text(output, PInvoke.PKEY_Device_ContainerId) ? ReplacementMicId(enumerator) ?? id : id;
        }
        finally
        {
            if (output is not null) Marshal.ReleaseComObject(output);
            if (input is not null) Marshal.ReleaseComObject(input);
        }
    }

    /// <summary>
    /// In place of the headset's mic, skipping Bluetooth and virtual endpoints: the first active capture endpoint in the
    /// computer's own container (built in) whose form factor is a microphone or headset (or unreadable), else the first
    /// other one by friendly name (a USB webcam, a wired mic), else null.
    /// </summary>
    private static string? ReplacementMicId(IMMDeviceEnumerator enumerator)
    {
        IMMDeviceCollection? devices = null;
        string? builtIn = null;
        var others = new List<(string Name, string Id)>();
        try
        {
            enumerator.EnumAudioEndpoints(EDataFlow.eCapture, DEVICE_STATE.DEVICE_STATE_ACTIVE, out devices);
            devices.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    devices.Item(i, out device);
                    // Virtual devices have no physical container, so Windows puts them in the computer's.
                    if (Text(device, PInvoke.PKEY_Device_EnumeratorName) is { } enumeratorName && IsBluetoothOrVirtual(enumeratorName)) continue;
                    var id = Id(device);
                    if (Text(device, PInvoke.PKEY_Device_ContainerId) != ComputerContainer) others.Add((CaptureLoop.FriendlyName(device) ?? id, id));
                    else if (FormFactor(device) is null or 4 or 5) builtIn ??= id; // Microphone, Headset (not Stereo Mix)
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
            // No endpoint list: what was read so far.
        }
        finally
        {
            if (devices is not null) Marshal.ReleaseComObject(devices);
        }
        return builtIn ?? others.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).Select(o => o.Id).FirstOrDefault();
    }

    /// <summary>Bluetooth (BTH*) or software (ROOT, SWD: virtual cables, app mics) enumerators.</summary>
    private static bool IsBluetoothOrVirtual(string enumeratorName) =>
        new[] { "BTH", "ROOT", "SWD" }.Any(prefix => enumeratorName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// The endpoint's state, null when it cannot be read (removed); only an active one is alive (the counterpart of
    /// DeviceIsAlive).
    /// </summary>
    private static DEVICE_STATE? StateOf(IMMDeviceEnumerator enumerator, string id)
    {
        IMMDevice? device = null;
        try
        {
            enumerator.GetDevice(id, out device);
            return State(device);
        }
        catch (COMException)
        {
            return null;
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

    /// <summary>A string (VT_LPWSTR) or GUID (VT_CLSID, lowercase, no braces) endpoint property, or null when it cannot be read.</summary>
    private static unsafe string? Text(IMMDevice device, PROPERTYKEY key)
    {
        IPropertyStore? store = null;
        var value = default(PROPVARIANT);
        try
        {
            device.OpenPropertyStore(STGM.STGM_READ, out store);
            store.GetValue(&key, out value);
            if (value.vt == VARENUM.VT_LPWSTR && value.pwszVal.Value != null) return value.pwszVal.ToString();
            if (value.vt == VARENUM.VT_CLSID && value.puuid != null) return value.puuid->ToString();
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            PInvoke.PropVariantClear(ref value);
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>The endpoint's form factor (PKEY_AudioEndpoint_FormFactor, an EndpointFormFactor), or null when it cannot be read.</summary>
    private static unsafe uint? FormFactor(IMMDevice device)
    {
        IPropertyStore? store = null;
        var value = default(PROPVARIANT);
        try
        {
            device.OpenPropertyStore(STGM.STGM_READ, out store);
            var key = PInvoke.PKEY_AudioEndpoint_FormFactor;
            store.GetValue(&key, out value);
            return value.vt == VARENUM.VT_UI4 ? value.ulVal : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            PInvoke.PropVariantClear(ref value);
            if (store is not null) Marshal.ReleaseComObject(store);
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

        public void OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState) =>
            Schedule(_monitor.Log is null ? null : $"OnDeviceStateChanged {pwstrDeviceId} state 0x{(uint)dwNewState:X}");

        public void OnDeviceAdded(PCWSTR pwstrDeviceId) =>
            Schedule(_monitor.Log is null ? null : $"OnDeviceAdded {pwstrDeviceId}");

        public void OnDeviceRemoved(PCWSTR pwstrDeviceId) =>
            Schedule(_monitor.Log is null ? null : $"OnDeviceRemoved {pwstrDeviceId}");

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId) =>
            Schedule(_monitor.Log is null ? null : $"OnDefaultDeviceChanged {flow} {role} {pwstrDefaultDeviceId.ToString() ?? "none"}");

        public void OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key)
        {
            var format = PInvoke.PKEY_AudioEngine_DeviceFormat;
            if (key.fmtid == format.fmtid && key.pid == format.pid)
                Schedule(_monitor.Log is null ? null : $"OnPropertyValueChanged {pwstrDeviceId} device format");
        }

        /// <summary>
        /// While the monitor runs, the first notification of a burst arms the timer; the others until it fires are
        /// absorbed. <paramref name="trace"/>: the notification, for <see cref="Log"/>.
        /// </summary>
        private void Schedule(string? trace)
        {
            try
            {
                var running = _monitor._running;
                var armed = running && Interlocked.Exchange(ref _pending, 1) == 0;
                if (armed) _timer.Change(Coalesce, Timeout.InfiniteTimeSpan);
                if (trace is not null) _monitor.Log?.Invoke($"route monitor: {trace}, running {running}, {(armed ? "evaluates in 300 ms" : "absorbed")}");
            }
            catch (Exception)
            {
                // Disposed meanwhile (disposing): nothing to evaluate. A log that threw: nothing to report.
            }
        }
    }
}
