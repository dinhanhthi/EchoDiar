using System.Diagnostics;
using EchoDiar.AI;
using EchoDiar.AI.Cloud;
using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Calendar;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.LocalLLM;
using EchoDiar.Platform.AI;
using EchoDiar.Platform.Audio;
using EchoDiar.Platform.Calendar;
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
/// <see cref="ChildProcessJob.Shared"/>. Cloud transcription: the pipeline exports uploads through the same Media
/// Foundation object, and <see cref="MakeCloudEngine"/> makes a job's cloud engines with their keys. The calendars:
/// <see cref="Calendar"/>.
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
        Index = new LibraryIndex(paths.Rag);
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
            // Checked again before each load: a changed or corrupt file (its catalog SHA-256, size and last write time
            // against the marker) is never loaded. Not a defense against a local writer who also resets the last write
            // time or rewrites the marker (ModelStore's documented trust boundary).
            entry => Models.VerifiedFolder(entry) ?? throw new TranscriptionException(TranscriptionErrorKind.NeedsModel, entry.Id),
            Gate,
            decoder: decoder,
            deferral: Deferral,
            exporter: decoder);
        Engine = new RecordingEngine(OpenSystem, OpenMic, routes: _routes)
        {
            IsTargetPlayingOutput = IsTargetPlayingOutput,
        };
#if DEBUG
        // Route changes in Logs\startup.log; Release builds leave both null and format nothing.
        _routes.Log = message => StartupTrace.Write(message);
        Engine.Log = message => StartupTrace.Write(message);
        Calendar = new AppointmentCalendarStore(message => StartupTrace.Write(message));
#else
        Calendar = new AppointmentCalendarStore(message => Debug.WriteLine(message));
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

    /// <summary>
    /// The user's calendars through the Appointments API (every calendar read-only, EchoDiar's own calendar read-write).
    /// Its calls are asynchronous and never block the calling thread; its log messages never hold event content.
    /// </summary>
    public ICalendarStore Calendar { get; }

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
    /// The RAG index of the library, in its own folder beside the conversations (it deletes the other folders inside
    /// it). Its calls run one at a time on the thread pool.
    /// </summary>
    public LibraryIndex Index { get; }

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
    /// Throws <see cref="ModelStoreException"/> (<see cref="ModelStoreError.ChecksumMismatch"/>) when the check failed and
    /// the install was deleted.
    /// </summary>
    public ILLMProvider? MakeLocalProvider(string entryID)
    {
        if (ModelCatalog.Entry(entryID) is not { Kind: ModelKind.Llm } entry) return null;
        if (entry.Runtime == ModelRuntime.WindowsAI) return WindowsAI;
        if (entry.Runtime != ModelRuntime.Gguf || entry.Files is not [var file] || !Models.IsInstalled(entry)) return null;
        if (Models.VerifiedFolder(entry) is { } folder) return new LlamaProvider(entry.Id, Path.Combine(folder, file));
        if (!Models.IsInstalled(entry))
            throw new ModelStoreException(ModelStoreError.ChecksumMismatch, $"{entry.Id} failed its integrity check.");
        return null;
    }

    /// <summary>
    /// The embedder picked by <paramref name="settings"/>' <c>embeddingModelID</c> (macOS <c>AppModel.resolveEmbedder</c>):
    /// <c>api:&lt;provider id&gt;</c> is the enabled OpenAI-compatible or local-host config of <paramref name="configs"/>
    /// with <c>embeddingAPIModel</c> and its saved key; anything else is an embedding catalog entry (a macOS MLX id maps to
    /// its GGUF entry), downloaded and checked against its catalog SHA-256 (<see cref="ModelStore.VerifiedFolder"/>), run
    /// with llama.cpp. Null with why when there is none. An in-app embedder loads nothing here: its caller holds
    /// <see cref="Gate"/> (owner Embedder) around <see cref="ITextEmbedder.WithLoadedAsync{T}"/>. Reads Credential Manager
    /// and may hash the model file: call it off the UI thread, with settings and configs the UI no longer changes.
    /// </summary>
    public (ITextEmbedder? Embedder, string? Problem) MakeEmbedder(AppSettings settings, IReadOnlyList<ProviderConfig> configs)
    {
        var id = settings.EmbeddingModelID;
        if (id.StartsWith(ModelCatalog.ApiEmbedderPrefix, StringComparison.Ordinal))
        {
            var providerId = id[ModelCatalog.ApiEmbedderPrefix.Length..];
            if (EmbeddingConfig(settings, configs) is not { } config)
                return (null, Loc.T("Pick an enabled provider in Settings › RAG"));
            var model = settings.EmbeddingAPIModel.Trim();
            if (model.Length == 0)
                return (null, Loc.Format("Enter the embedding model of %@ in Settings › RAG", config.DisplayName));
            string? apiKey = null;
            if (config.AuthStyle != AuthStyle.None)
            {
                try
                {
                    apiKey = Secrets.ApiKey(config.Id);
                }
                catch (Exception e)
                {
                    // As macOS's try?: no key.
                    Debug.WriteLine($"EchoDiar: could not read the key of {providerId}: {e.GetType().Name}");
                }
            }
            // As MakeProvider: sent without a key, a preset only answers HTTP 401.
            if (config.Preset is not null && config.AuthStyle != AuthStyle.None && string.IsNullOrEmpty(apiKey))
                return (null, Loc.Format("Save a key for %@ in Settings › AI", config.DisplayName));
            var copy = config with { ExtraHeaders = new(config.ExtraHeaders), ShownModels = [.. config.ShownModels] };
            return (new APITextEmbedder(copy, apiKey, model), null);
        }
        if (ModelCatalog.EmbeddingEntry(id) is not { Runtime: ModelRuntime.Gguf, Files: [var file] } entry)
            return (null, Loc.T("Pick an embedding model in Settings › RAG"));
        if (!Models.IsInstalled(entry)) return (null, Loc.T("Download the embedding model in Settings › RAG"));
        if (Models.VerifiedFolder(entry) is { } folder) return (new LlamaEmbedder(entry.Id, Path.Combine(folder, file)), null);
        // The check failed and the install was deleted (InstallRemoved refreshes its row).
        return (null, Models.IsInstalled(entry)
            ? Loc.T("Download the embedding model in Settings › RAG")
            : Loc.Format("%@ failed its integrity check. Download it again.", entry.DisplayName));
    }

    /// <summary>
    /// The enabled OpenAI-compatible or local-host config (Ollama included) of an <c>api:&lt;provider id&gt;</c> embedder
    /// (macOS <c>embeddingProviderConfig</c>); null for an in-app embedder. Ids compare as Credential Manager names keys.
    /// </summary>
    public static ProviderConfig? EmbeddingConfig(AppSettings settings, IReadOnlyList<ProviderConfig> configs)
    {
        var id = settings.EmbeddingModelID;
        if (!id.StartsWith(ModelCatalog.ApiEmbedderPrefix, StringComparison.Ordinal)) return null;
        var providerId = id[ModelCatalog.ApiEmbedderPrefix.Length..];
        return configs.FirstOrDefault(c => string.Equals(c.Id, providerId, StringComparison.OrdinalIgnoreCase)
            && c.Enabled && c.Kind is ProviderKind.OpenAICompatible or ProviderKind.Local);
    }

    /// <summary>
    /// A cloud engine of <paramref name="config"/> with its saved key (macOS <c>AppModel.makeCloudEngine</c>);
    /// <paramref name="model"/>: a cloud diarizer's diarization model, null = the transcription model. A key the store
    /// cannot read is sent as none, as macOS's <c>try?</c>: the provider then answers "invalid API key". Reads
    /// Credential Manager: call it off the UI thread, with a config the UI no longer changes (a copy).
    /// </summary>
    public CloudEngine MakeCloudEngine(ProviderConfig config, string? model = null)
    {
        string? apiKey = null;
        if (CloudTranscription.NeedsKey(config))
        {
            try
            {
                apiKey = Secrets.ApiKey(config.Id);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not read the key of {config.Id}: {e.GetType().Name}");
            }
        }
        return new CloudEngine(
            CloudTranscription.EngineId(config.Id), config.DisplayName, model ?? CloudTranscription.Model(config),
            CloudTranscription.MakeTranscriber(config, apiKey, model));
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
