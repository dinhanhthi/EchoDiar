using System.Diagnostics;
using EchoDiar.AI.Cloud;
using EchoDiar.App.Services;
using EchoDiar.App.Views;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Transcribe;

namespace EchoDiar.App.ViewModels;

/// <summary>An engine of the Retry and Re-transcribe menus: a catalog id or <c>cloud:&lt;provider id&gt;</c>, and its name.</summary>
public sealed record EngineChoice(string Id, string DisplayName);

/// <summary>
/// A transcription waiting for its destination's one-time "audio leaves this PC" confirmation (macOS
/// <c>PendingCloudRun</c>), shown by the main window. <see cref="EngineId"/>: the engine it is submitted with once
/// confirmed (<c>cloud:&lt;id&gt;</c> when its cloud ASR asks; the job's engine, null = the default resolution, when its
/// cloud diarizer asks). <see cref="ConsentKey"/>: <see cref="CloudDestination.ConsentKey"/> when it was queued.
/// </summary>
public sealed record PendingCloudRun(
    string? EngineId, string ProviderName, string ConsentKey, string Host, Guid ConversationId, string Folder, string? Language);

/// <summary>
/// Transcription while recording (macOS AppModel <c>startRecordingTranscriber</c>, <c>attach</c>,
/// <c>recordingCaptions</c>): one <see cref="RecordingTranscriber"/> per recording, fed by the writer's live sink, whose
/// captions the recording page shows; at Stop it hands what it transcribed to the recording's transcription job. And the
/// cloud engines (macOS AppModel <c>cloudEngineConfigs</c>, <c>cloudDiarizerConfigs</c>, <c>pendingCloudRuns</c>): which
/// providers can transcribe or diarize now, the engines the menus offer, and the one-time confirmation per destination
/// before any audio is uploaded.
/// </summary>
public sealed partial class AppViewModel
{
    /// <summary>The current recording's transcriber, from its start until the end snapshot; one per recording.</summary>
    private RecordingTranscriber? _recordingTranscriber;

    /// <summary>Transcribers not done yet (recording, or draining after Stop); disposed on quit. UI thread only.</summary>
    private readonly HashSet<RecordingTranscriber> _liveTranscribers = [];

    /// <summary>The recording's current marker language, read for every block on the writer's thread.</summary>
    private volatile string _liveLanguage = LanguageOptions.Auto;

    private RecordingTranscriber.Snapshot? _recordingCaptions;

    private IReadOnlyList<PendingCloudRun> _pendingCloudRuns = [];
    private string? _settingsExpandedProvider;

    /// <summary>
    /// Captions of the current recording's transcriber, kept until its Stop drain ends; null when no transcriber runs
    /// (the recording page then shows the wave and why).
    /// </summary>
    public RecordingTranscriber.Snapshot? RecordingCaptions
    {
        get => _recordingCaptions;
        private set => SetProperty(ref _recordingCaptions, value);
    }

    /// <summary>
    /// The card's "Transcribe now" (macOS <c>processNow</c>): the conversation's job waiting while energy saver is on
    /// starts now; the pipeline then reports its next step.
    /// </summary>
    public void TranscribeNow(Guid id)
    {
        if (_closing || !TranscriptionProgress.TryGetValue(id, out var progress) || progress.Step != JobStep.Deferred) return;
        _services.Pipeline.ProcessNow(id);
    }

    /// <summary>
    /// Before the engine starts (it reads the sink once at start; the model loads meanwhile): a transcriber for a
    /// recording in <paramref name="language"/>, connected to the engine's live sink, when auto-transcribe and
    /// "Transcribe while recording" are on and the engine the Stop job resolves to is an installed Whisper or Parakeet
    /// entry. Otherwise the sink is cleared, and a previous recording's captions are not this one's.
    /// </summary>
    private async Task AttachRecordingTranscriberAsync(string language)
    {
        var settings = Settings;
        RecordingTranscriber? transcriber = null;
        // macOS: a cloud default engine, or a destination still to confirm, transcribes after Stop only.
        if (settings.AutoTranscribe && settings.TranscribeWhileRecording && AutoEngine() == AutoEngineKind.Local)
        {
            try
            {
                // Reads the installed models: off the UI thread.
                transcriber = await Task.Run(() => CreateRecordingTranscriber(language, settings));
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not create the live transcriber: {e}");
            }
        }
        if (transcriber is null || _closing)
        {
            _services.Engine.LiveSink = null;
            RecordingCaptions = null;
            return;
        }
        _liveLanguage = language;
        _recordingTranscriber = transcriber;
        _liveTranscribers.Add(transcriber);
        _services.Engine.LiveSink = transcriber.AppendInterleaved;
        RecordingCaptions = new RecordingTranscriber.Snapshot(RecordingTranscriber.TranscriberState.Loading, [], 0);
        // Raised inside the transcriber's lock on its threads: only queued here. A late one (after Stop) is dropped.
        transcriber.Updated += snapshot => Post(() =>
        {
            if (ReferenceEquals(_recordingTranscriber, transcriber)) RecordingCaptions = snapshot;
        });
        transcriber.Start();
    }

    /// <summary>The start failed: the recording's transcriber stops, keeping nothing.</summary>
    private void CancelRecordingTranscriber()
    {
        if (TakeRecordingTranscriber() is { } transcriber) CancelRecordingTranscriber(transcriber);
        RecordingCaptions = null;
    }

    /// <summary>
    /// A transcriber taken from the engine stops, keeping nothing (no recording folder to finish); on the thread pool,
    /// since its cancellation may run the decode task's steps inline.
    /// </summary>
    private void CancelRecordingTranscriber(RecordingTranscriber transcriber)
    {
        _liveTranscribers.Remove(transcriber);
        _ = Task.Run(transcriber.Cancel);
        if (_recordingTranscriber is null) RecordingCaptions = null;
    }

    /// <summary>
    /// The end snapshot: the transcriber leaves the engine (the writer flushed its last frames through the sink
    /// before the stop returned). Null when none ran.
    /// </summary>
    private RecordingTranscriber? TakeRecordingTranscriber()
    {
        _services.Engine.LiveSink = null;
        var transcriber = _recordingTranscriber;
        _recordingTranscriber = null;
        return transcriber;
    }

    /// <summary>
    /// Decodes what the transcriber has left and unloads its model (before the job may load one), on the thread pool;
    /// null when it transcribed nothing. The captions stay until then, unless a new recording replaced them.
    /// </summary>
    private async Task<PriorTranscription?> FinishRecordingTranscriberAsync(RecordingTranscriber transcriber)
    {
        try
        {
            var prior = await Task.Run(transcriber.FinishAsync);
            StartupTrace.Write(prior is null
                ? $"live transcript: no prior ({transcriber.Stats})"
                : $"live transcript: prior {prior.Duration:0.000} s, engine {prior.EngineID}, {prior.Mic.Count} mic and "
                    + $"{prior.System.Count} system segments, gaps {prior.MicGaps.Count} mic {prior.SystemGaps.Count} system ({transcriber.Stats})");
            return prior;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: the live transcriber could not finish: {e}");
            return null;
        }
        finally
        {
            _liveTranscribers.Remove(transcriber);
            if (_recordingTranscriber is null) RecordingCaptions = null;
        }
    }

    /// <summary>
    /// Quit: every transcriber stops, keeping nothing, and unloads its model, on the thread pool (its cancellation may
    /// run the decode task's steps inline). The recording's batch job runs at the next launch.
    /// </summary>
    private Task DisposeRecordingTranscribers()
    {
        _recordingTranscriber = null;
        var transcribers = _liveTranscribers.ToList();
        _liveTranscribers.Clear();
        if (transcribers.Count == 0) return Task.CompletedTask;
        return Task.Run(() => Task.WhenAll(transcribers.Select(t => t.DisposeAsync().AsTask())));
    }

    /// <summary>
    /// The transcriber for a recording in <paramref name="language"/>: the <see cref="IncrementalEntry"/> of the engine
    /// the Stop job picks, loaded as a session when its marker vouches for its files (<see cref="ModelStore.MarkedFolder"/>);
    /// idle release after "Unload models after" minutes. An install without a valid marker is not hashed here, which
    /// would hold Record up to a whole model's read: the recording starts without live captions, the model is verified
    /// in the background, and the Stop job still checks it (<see cref="ModelStore.VerifiedFolder"/>).
    /// </summary>
    private RecordingTranscriber? CreateRecordingTranscriber(string language, AppSettings settings)
    {
        if (IncrementalEntry(PreferredEngine(language), language) is not { } entry) return null;
        if (_services.Models.MarkedFolder(entry) is not { } folder)
        {
            _ = Task.Run(() => _services.Models.VerifiedFolder(entry)).ContinueWith(
                t => Debug.WriteLine($"EchoDiar: could not verify model {entry.Id}: {t.Exception?.InnerException?.GetType().Name}"),
                TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }
        if (_services.Engines[entry.Runtime](entry, folder) is not ISessionTranscriptionEngine engine) return null;
        return new RecordingTranscriber(
            entry, engine, _services.Gate, TimeSpan.FromMinutes(settings.ModelIdleMinutes), () => _liveLanguage);
    }

    /// <summary>
    /// The entry a recording transcriber runs so that the pipeline keeps its segments at Stop (Swift
    /// <c>TranscriptionPipeline.incrementalEntry</c>): the batch resolution for <paramref name="engineId"/> (explicit,
    /// else <see cref="TranscriptionPipeline.DefaultEngine"/>) and the recording's <paramref name="language"/>, when it is
    /// a usable Whisper or Parakeet entry that needs no language fallback; null otherwise.
    /// </summary>
    private ModelEntry? IncrementalEntry(string? engineId, string language)
    {
        bool IsUsable(ModelEntry entry) => _services.Engines.ContainsKey(entry.Runtime) && _services.Models.IsInstalled(entry);
        var selected = engineId is null
            ? TranscriptionPipeline.DefaultEngine(language, IsUsable)
            : ModelCatalog.Entry(engineId) is { Kind: ModelKind.Asr } entry && IsUsable(entry) ? entry : null;
        if (selected is null || !TranscriptionPipeline.TranscribesChunks(selected)) return null;
        var markers = LanguageRange.Ranges([new RecordingSession.LanguageMarker { FileTime = 0, Language = language }])
            .Select(r => r.Language).OfType<string>().Select(WhisperLanguages.Primary).ToList();
        var (_, engineFallback) = TranscriptionPipeline.LanguageFallback(
            selected, TranscriptionPipeline.SupportedLanguages(selected), markers, language,
            id => ModelCatalog.Entry(id) is { } candidate && IsUsable(candidate));
        return engineFallback is null ? selected : null;
    }

    // Cloud engines

    /// <summary>
    /// Transcriptions waiting for their destination's confirmation, oldest first (macOS <c>pendingCloudRuns</c>); the
    /// main window asks about the first one. Nothing is uploaded meanwhile.
    /// </summary>
    public IReadOnlyList<PendingCloudRun> PendingCloudRuns
    {
        get => _pendingCloudRuns;
        private set => SetProperty(ref _pendingCloudRuns, value);
    }

    /// <summary>A cloud confirmation waits: the main window must show (it may be hidden in the tray).</summary>
    public event Action? MainWindowRequested;

    /// <summary>
    /// A provider whose row Settings opens and brings into view (macOS <c>settingsExpandedProvider</c>: a cloud row's Set
    /// Up); the row clears it once expanded.
    /// </summary>
    public string? SettingsExpandedProvider
    {
        get => _settingsExpandedProvider;
        set => SetProperty(ref _settingsExpandedProvider, value);
    }

    /// <summary>
    /// Enabled cloud transcription providers with a model, a valid destination and a saved key (macOS
    /// <c>cloudEngineConfigs</c>); the key presence is the one <see cref="ProvidersViewModel"/> last read off the UI
    /// thread. <see cref="OnPropertyChanged(string)"/> is raised with this name when the configs or the keys change.
    /// </summary>
    public IReadOnlyList<ProviderConfig> CloudEngineConfigs =>
        [.. ProvidersModel.ProviderConfigs.Where(c => c.Enabled && CloudTranscription.IsComplete(c) && HasKeyIfNeeded(c))];

    /// <summary>
    /// Enabled providers that can be the default cloud diarizer, with a valid destination and a saved key (macOS
    /// <c>cloudDiarizerConfigs</c>). Their diarization model is not checked here (Settings' model field would vanish
    /// mid-typing): <see cref="DefaultCloudDiarizer"/> does.
    /// </summary>
    public IReadOnlyList<ProviderConfig> CloudDiarizerConfigs =>
    [
        .. ProvidersModel.ProviderConfigs.Where(c =>
            c.Enabled && CloudTranscription.DiarizationPresets.Contains(c.Preset ?? "") && CloudTranscription.Supports(c)
            && CloudTranscription.Destination(c) is not null && HasKeyIfNeeded(c)),
    ];

    /// <summary>The default diarizer is a configured cloud provider (macOS <c>diarizerAvailable</c>'s cloud half).</summary>
    public bool CloudDiarizerAvailable =>
        CloudTranscription.ProviderId(Settings.DefaultDiarizerModelID) is { } id
        && CloudDiarizerConfigs.Any(c => ProvidersViewModel.SameId(c.Id, id));

    /// <summary>"Deepgram (nova-3)"; the provider name alone when it picks the model (macOS <c>cloudEngineName</c>).</summary>
    public static string CloudEngineName(ProviderConfig config)
    {
        var model = CloudTranscription.Model(config);
        return model.Length == 0 ? config.DisplayName : $"{config.DisplayName} ({model})";
    }

    /// <summary>
    /// The engines of "Retry with another engine" and "Transcribe again with" (macOS <c>EngineMenuItems</c>): the
    /// installed local speech engines in catalog order, then the configured cloud engines.
    /// </summary>
    public IReadOnlyList<EngineChoice> InstalledBatchEngines =>
    [
        .. ModelCatalog.Asr.Where(e => InstalledModelIds.Contains(e.Id)).Select(e => new EngineChoice(e.Id, e.DisplayName)),
        .. CloudEngineConfigs.Select(c => new EngineChoice(CloudTranscription.EngineId(c.Id), CloudEngineName(c))),
    ];

    /// <summary>
    /// The main window's answer to the first pending run's confirmation (macOS <c>resolvePendingCloudRuns</c>): every
    /// pending run for that destination is submitted (and the destination remembered in <c>cloudSTTConfirmed</c>) or
    /// dropped, uploading nothing (the conversation keeps its Transcribe button). Runs whose conversation got a job
    /// meanwhile are skipped, so no second paid job starts. Submitting reads the config again, so a base URL changed
    /// since the dialog asks again, and a run whose ASR and diarizer providers differ then asks for the second one.
    /// </summary>
    public void ResolvePendingCloudRuns(bool allowed)
    {
        if (PendingCloudRuns.FirstOrDefault() is not { } first) return;
        var runs = PendingCloudRuns.Where(r => r.ConsentKey == first.ConsentKey).ToList();
        PendingCloudRuns = [.. PendingCloudRuns.Where(r => r.ConsentKey != first.ConsentKey)];
        if (!allowed || _closing) return;
        if (!Settings.CloudSTTConfirmed.Contains(first.ConsentKey))
        {
            SaveSettings(Settings with { CloudSTTConfirmed = [.. Settings.CloudSTTConfirmed, first.ConsentKey] });
        }
        foreach (var run in runs.Where(r => !IsTranscribing(r.ConversationId))) _ = ResubmitAsync(run);
    }

    private async Task ResubmitAsync(PendingCloudRun run)
    {
        var session = await Task.Run(() => ReadSession(run.Folder));
        if (session is not null && !IsTranscribing(run.ConversationId)) _ = SubmitTranscription(run.Folder, session, run.EngineId, run.Language);
    }

    /// <summary>The cloud engines of one job, copies made on the UI thread; built with their keys off it.</summary>
    private sealed record CloudPlan(string? EngineId, ProviderConfig? Asr, ProviderConfig? Diarizer);

    /// <summary>
    /// The cloud part of <c>submitTranscription</c> (macOS): a cloud engine (picked, or the default) runs only while its
    /// provider is configured, else the local resolution applies (<see cref="CloudPlan.EngineId"/> null for a picked
    /// one). The default cloud diarizer runs when "Tell speakers apart" is on, the cloud ASR (if any) labels no speakers
    /// and its diarization model labels them. Null when a destination is not confirmed yet: the run then waits in
    /// <see cref="PendingCloudRuns"/> and nothing is submitted.
    /// </summary>
    private CloudPlan? PlanCloud(string? engineId, Guid conversationId, string folder, string? language)
    {
        ProviderConfig? asr = null;
        var asrLabelsSpeakers = false;
        if (CloudTranscription.ProviderId(engineId ?? Settings.DefaultASRModelID) is { } providerId)
        {
            if (CloudEngineConfigs.FirstOrDefault(c => ProvidersViewModel.SameId(c.Id, providerId)) is { } config)
            {
                var cloudId = CloudTranscription.EngineId(config.Id);
                if (!CloudDestinationConfirmed(config, cloudId, conversationId, folder, language)) return null;
                asr = ProvidersViewModel.Copy(config);
                engineId = cloudId;
                asrLabelsSpeakers = CloudTranscription.Diarizes(config, CloudTranscription.Model(config));
            }
            else if (CloudTranscription.ProviderId(engineId) is not null)
            {
                engineId = null;
            }
        }
        ProviderConfig? diarizer = null;
        if (Settings.Diarization && !asrLabelsSpeakers && DefaultCloudDiarizer() is { } cloudDiarizer)
        {
            if (!CloudDestinationConfirmed(cloudDiarizer, engineId, conversationId, folder, language)) return null;
            diarizer = ProvidersViewModel.Copy(cloudDiarizer);
        }
        return new CloudPlan(engineId, asr, diarizer);
    }

    /// <summary>
    /// The default diarizer when it is a configured cloud provider whose diarization model labels speakers (a model that
    /// labels none would bill a transcription for nothing: the local diarizer runs instead); null otherwise.
    /// </summary>
    internal ProviderConfig? DefaultCloudDiarizer() =>
        CloudTranscription.ProviderId(Settings.DefaultDiarizerModelID) is { } id
        && CloudDiarizerConfigs.FirstOrDefault(c => ProvidersViewModel.SameId(c.Id, id)) is { } config
        && CloudTranscription.Diarizes(config, CloudTranscription.DiarizationModel(config))
            ? config
            : null;

    /// <summary>
    /// Whether <paramref name="config"/>'s destination is confirmed, for the cloud ASR and the cloud diarizer alike;
    /// else the run is queued for the main window's confirmation (submitted again with <paramref name="engineId"/>)
    /// unless its folder already waits. Bound to the destination, so an edited base URL asks again; an invalid one
    /// never counts as confirmed.
    /// </summary>
    private bool CloudDestinationConfirmed(
        ProviderConfig config, string? engineId, Guid conversationId, string folder, string? language)
    {
        if (CloudConsent.IsConfirmed(Settings, config)) return true;
        if (CloudTranscription.Destination(config) is { } destination && PendingCloudRuns.All(r => r.Folder != folder))
        {
            if (PendingCloudRuns.Count == 0) MainWindowRequested?.Invoke();
            PendingCloudRuns =
            [
                .. PendingCloudRuns,
                new PendingCloudRun(engineId, config.DisplayName, destination.ConsentKey, destination.Host, conversationId, folder, language),
            ];
        }
        return false;
    }

    /// <summary>What the default engines of an automatic transcription resolve to, as <see cref="PlanCloud"/> decides it.</summary>
    private enum AutoEngineKind { Local, CloudConfirmed, ConsentPending }

    /// <summary>
    /// Side-effect free (reads <c>cloudSTTConfirmed</c>, never queues a confirmation; macOS <c>autoEngine</c>): mirrors
    /// <see cref="PlanCloud"/> with the default engines. A pending confirmation compresses a stopped recording first
    /// rather than holding its CAF behind the dialog, and a cloud default runs no live transcriber.
    /// </summary>
    private AutoEngineKind AutoEngine()
    {
        var engine = AutoEngineKind.Local;
        var asrLabelsSpeakers = false;
        if (CloudTranscription.ProviderId(Settings.DefaultASRModelID) is { } id
            && CloudEngineConfigs.FirstOrDefault(c => ProvidersViewModel.SameId(c.Id, id)) is { } config)
        {
            if (!CloudConsent.IsConfirmed(Settings, config)) return AutoEngineKind.ConsentPending;
            engine = AutoEngineKind.CloudConfirmed;
            asrLabelsSpeakers = CloudTranscription.Diarizes(config, CloudTranscription.Model(config));
        }
        if (Settings.Diarization && !asrLabelsSpeakers && DefaultCloudDiarizer() is { } diarizer
            && !CloudConsent.IsConfirmed(Settings, diarizer))
        {
            return AutoEngineKind.ConsentPending;
        }
        return engine;
    }

    private bool HasKeyIfNeeded(ProviderConfig config) => !CloudTranscription.NeedsKey(config) || ProvidersModel.HasApiKey(config.Id);

    /// <summary>The provider configs or their keys changed: the cloud engines, the menus and the diarizer notices follow.</summary>
    private void OnProvidersChanged()
    {
        if (_closing) return;
        OnPropertyChanged(nameof(CloudEngineConfigs));
        OnPropertyChanged(nameof(NeedsDiarizer));
    }
}
