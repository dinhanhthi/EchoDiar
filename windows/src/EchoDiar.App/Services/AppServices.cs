using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using EchoDiar.Platform.SystemServices;
using EchoDiar.Transcribe;
using EchoDiar.Transcribe.Engines;

namespace EchoDiar.App.Services;

/// <summary>
/// The app's long-lived services over one library root: settings, library, models, the model load gate, the
/// transcription pipeline (reading m4a through the Media Foundation decoder) and its engine factories, the audio
/// compressor and the recording engine with the Platform capture sources and route monitor. Created once, on launch.
/// The pipeline's jobs wait while energy saver is on and "wait" is set (<see cref="Deferral"/>).
/// </summary>
internal sealed class AppServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly AudioRouteMonitor _routes = new();
    private readonly PowerState _power = new();

    public AppServices(AppPaths paths)
    {
        Paths = paths;
        Settings = new SettingsStore(paths);
        Library = new LibraryStore(paths.Conversations, new ShellFiles());
        // No overall timeout: large models take longer than the 100 s default; cancellation stops a download.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Models = new ModelStore(paths.Models, _http);
        Gate = new ModelLoadGate();
        Deferral = new EnergySaverDeferral(_power);
        var decoder = new MediaFoundationAudio();
        Compressor = new AudioCompressor(decoder: decoder);
        Engines = new Dictionary<ModelRuntime, TranscriptionPipeline.EngineFactory>
        {
            [ModelRuntime.WhisperNet] = WhisperEngine.Create,
            [ModelRuntime.SherpaOnnx] = ParakeetEngine.Create,
        };
        Pipeline = new TranscriptionPipeline(
            Engines,
            SherpaDiarizer.Create,
            Models.IsInstalled,
            Models.Folder,
            Gate,
            decoder: decoder,
            deferral: Deferral);
        Engine = new RecordingEngine(OpenSystem, OpenMic, routes: _routes)
        {
            IsTargetPlayingOutput = IsTargetPlayingOutput,
        };
#if DEBUG
        // Route changes in Logs\startup.log; Release builds leave both null and format nothing.
        _routes.Log = message => StartupTrace.Write(message);
        Engine.Log = message => StartupTrace.Write(message);
#endif
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public LibraryStore Library { get; }
    public ModelStore Models { get; }
    public ModelLoadGate Gate { get; }
    public TranscriptionPipeline Pipeline { get; }

    /// <summary>The pipeline's deferral: energy saver on while the app's "wait" setting is on.</summary>
    public EnergySaverDeferral Deferral { get; }

    /// <summary>The pipeline's batch engine factories by runtime; the recording transcriber loads its session from them.</summary>
    public IReadOnlyDictionary<ModelRuntime, TranscriptionPipeline.EngineFactory> Engines { get; }
    public AudioCompressor Compressor { get; }
    public RecordingEngine Engine { get; }

    /// <summary>Launch at login: a stand-in until task 14.3 adds the startup task.</summary>
    public ILaunchAtLogin LaunchAtLogin { get; } = new LaunchAtLogin();

    /// <summary>
    /// Stops a running recording (its CAF is finished), then releases the route monitor, the energy saver
    /// notifications and the HTTP client.
    /// </summary>
    public void Dispose()
    {
        Engine.Dispose();
        _routes.Dispose();
        _power.Dispose();
        _http.Dispose();
    }

    /// <summary>
    /// All system audio except EchoDiar's own process tree, or the picked app's process tree (named after the app in
    /// <c>session.json</c>); an app that cannot be isolated (<see cref="AudioProcessGroup.NeedsGlobalTap"/>) is
    /// recorded with all system audio.
    /// </summary>
    private static ICaptureSource OpenSystem(CaptureTarget target) =>
        target.TapMode == TapMode.PerApp && target.App is { } app
            ? LoopbackCapture.ForProcessTree(app.RootPid, app.Name)
            : LoopbackCapture.AllExcept(Environment.ProcessId);

    /// <summary>The input with this WASAPI endpoint id, null for the default input.</summary>
    private static ICaptureSource OpenMic(string? endpointId) => new MicCapture(endpointId);

    /// <summary>
    /// The silent-tap check for a picked app: whether its process tree plays. All system audio has no single process
    /// to ask, so the check is skipped there (false: a silent tap is never suspected).
    /// </summary>
    private static bool IsTargetPlayingOutput(CaptureTarget target) =>
        target.App is { } app && AudioSessions.IsPlayingOutput(app.RootPid);

    /// <summary>
    /// Adapts the Platform's <see cref="PowerState"/> into the pipeline's <see cref="IDeferralSignal"/> (Platform does not
    /// reference Transcribe): jobs wait while energy saver is on and <see cref="Enabled"/> ("Wait while energy saver is
    /// on"); turning either off releases them.
    /// </summary>
    internal sealed class EnergySaverDeferral : IDeferralSignal
    {
        private readonly PowerState _power;
        private volatile bool _enabled = true;

        public EnergySaverDeferral(PowerState power)
        {
            _power = power;
            _power.Changed += () => Changed?.Invoke();
        }

        /// <summary>The settings' <c>deferTranscriptionWhenConstrained</c>; a change wakes the waiting job.</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                Changed?.Invoke();
            }
        }

        public bool ShouldDefer => _enabled && _power.IsEnergySaverOn;

        public event Action? Changed;
    }
}
