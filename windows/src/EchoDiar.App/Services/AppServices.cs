using System.Diagnostics;
using EchoDiar.AI;
using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.LocalLLM;
using EchoDiar.Platform.AI;
using EchoDiar.Platform.Audio;
using EchoDiar.Platform.SystemServices;
using EchoDiar.Transcribe;
using EchoDiar.Transcribe.Engines;

namespace EchoDiar.App.Services;

/// <summary>
/// The app's long-lived services over one library root: settings, library, models, the model load gate, the
/// transcription pipeline (reading m4a through the Media Foundation decoder) and its engine factories, the audio
/// compressor and the recording engine with the Platform capture sources and route monitor. Created once, on launch.
/// The pipeline's jobs wait while energy saver is on and "wait" is set (<see cref="Deferral"/>). The AI pieces: the API
/// keys (<see cref="Secrets"/>), Windows AI, the AI jobs (sharing the load gate with transcription), local host
/// discovery, <see cref="MakeProvider"/> and <see cref="MakeLocalProvider"/>; every child process they start joins
/// <see cref="ChildProcessJob.Shared"/>.
/// </summary>
internal sealed class AppServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly AudioRouteMonitor _routes = new();
    private readonly PowerState _power = new();
    private readonly Lazy<WindowsAIProvider> _windowsAI = new(() => new WindowsAIProvider());

    public AppServices(AppPaths paths)
    {
        Paths = paths;
        Settings = new SettingsStore(paths);
        Library = new LibraryStore(paths.Conversations, new ShellFiles());
        // No overall timeout: large models take longer than the 100 s default; cancellation stops a download.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Models = new ModelStore(paths.Models, _http);
        Gate = new ModelLoadGate();
        AI = new AIService(Gate);
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
            // Checked again before each load (a planted or changed file is never loaded).
            entry => Models.VerifiedFolder(entry) ?? throw new TranscriptionException(TranscriptionErrorKind.NeedsModel, entry.Id),
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

    /// <summary>Launch at login: the package's startup task.</summary>
    public ILaunchAtLogin LaunchAtLogin { get; } = new LaunchAtLogin();

    /// <summary>
    /// The provider API keys, in Credential Manager only (never in settings.json). Every call reads or writes the
    /// credential store: call it off the UI thread. A provider has a key when <c>ApiKey(id) is not null</c>.
    /// </summary>
    public ISecretStore Secrets { get; } = new CredentialStore();

    /// <summary>Summaries and questions (Phase 24.3), in <see cref="Gate"/> with transcription: one heavy model at a time.</summary>
    public AIService AI { get; }

    /// <summary>
    /// Phi Silica, made on first use; <see cref="WindowsAIProvider.Availability"/> asks Windows AI, so call it off the UI
    /// thread.
    /// </summary>
    public WindowsAIProvider WindowsAI => _windowsAI.Value;

    /// <summary>
    /// The <c>onStarted</c> hook of every CLI run and of the Foundry Local status check: the process joins the app's job,
    /// so it dies with EchoDiar. Throws when it cannot (the caller then kills the process tree).
    /// </summary>
    public static void OnChildStarted(Process process) => ChildProcessJob.Shared.Assign(process);

    /// <summary>The local hosts that answer now, Foundry Local included, scanned off the UI thread.</summary>
    public static Task<IReadOnlyList<DiscoveredHost>> ScanLocalHostsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LocalHostDiscovery.ScanAsync(OnChildStarted, cancellationToken), cancellationToken);

    /// <summary>
    /// The client for <paramref name="config"/> with its saved key (macOS <c>AppModel.makeProvider</c>); null for an
    /// unknown CLI and for speech-to-text APIs. Throws when the store refuses the key, or when a preset that needs one has
    /// none: sent without it, the provider only answers HTTP 401. CLIs join the app's job (<see cref="OnChildStarted"/>).
    /// Reads Credential Manager: call it off the UI thread. The client gets its own copy of the config.
    /// </summary>
    public ILLMProvider? MakeProvider(ProviderConfig config)
    {
        var apiKey = config.AuthStyle == AuthStyle.None ? null : Secrets.ApiKey(config.Id);
        if (config.Preset is not null && config.AuthStyle != AuthStyle.None && string.IsNullOrEmpty(apiKey))
            throw LLMException.Provider(Loc.T("Save a key first."));
        var copy = config with { ExtraHeaders = new(config.ExtraHeaders), ShownModels = [.. config.ShownModels] };
        return config.Kind switch
        {
            ProviderKind.Cli => CLIPresets.FromSlug(config.Preset) is { } preset
                ? new CLIRunner(preset, config.BaseURL, onStarted: OnChildStarted)
                : null,
            ProviderKind.Anthropic => new AnthropicClient(copy, apiKey),
            ProviderKind.OpenAICompatible or ProviderKind.Local => new OpenAICompatibleClient(copy, apiKey),
            _ => null,
        };
    }

    /// <summary>
    /// The in-app model of LLM catalog entry <paramref name="entryID"/> (macOS <c>AppModel.makeLocalProvider</c>): Windows
    /// AI, or a downloaded GGUF model run with llama.cpp; null when it is not installed or not an LLM. Both are
    /// <see cref="ILocalLLMProvider"/>s, which <see cref="AIService"/> runs inside <see cref="Gate"/> (owner LLM), loaded
    /// for the job and freed when it ends. A GGUF file is checked against its catalog SHA-256 first
    /// (<see cref="ModelStore.VerifiedFolder"/>, which hashes it once when it has no marker): call it off the UI thread.
    /// </summary>
    public ILLMProvider? MakeLocalProvider(string entryID)
    {
        if (ModelCatalog.Entry(entryID) is not { Kind: ModelKind.Llm } entry) return null;
        return entry.Runtime switch
        {
            ModelRuntime.WindowsAI => WindowsAI,
            ModelRuntime.Gguf when entry.Files is [var file] && Models.VerifiedFolder(entry) is { } folder =>
                new LlamaProvider(entry.Id, Path.Combine(folder, file)),
            _ => null,
        };
    }

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
