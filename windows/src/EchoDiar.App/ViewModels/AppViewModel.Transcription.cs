using System.Diagnostics;
using EchoDiar.AI.Cloud;
using EchoDiar.App.Services;
using EchoDiar.App.Views;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Transcribe;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// An engine of the Retry and Re-transcribe menus: a catalog id or <c>cloud:&lt;provider id&gt;</c>, its name, and
/// whether it is the default engine.
/// </summary>
public sealed record EngineChoice(string Id, string DisplayName, bool IsDefault = false)
{
    /// <summary>The menu item's text: the name, with "(default)" for the default engine (macOS).</summary>
    public string MenuText => IsDefault ? Loc.Format("%@ (default)", DisplayName) : DisplayName;
}

/// <summary>
/// A transcription waiting for its destination's one-time "audio leaves this PC" confirmation (macOS
/// <c>PendingCloudRun</c>), shown by the main window. <see cref="EngineId"/>: the engine it is submitted with once
/// confirmed (<c>cloud:&lt;id&gt;</c> when its cloud ASR asks; the job's engine, null = the default resolution, when its
/// cloud diarizer asks). <see cref="ConsentKey"/>: <see cref="CloudDestination.ConsentKey"/> when it was queued.
/// <see cref="Prior"/>: the recording transcriber's prior (a cloud realtime transcript), submitted with the run, so it is
/// not lost to the wait.
/// </summary>
public sealed record PendingCloudRun(
    string? EngineId, string ProviderName, string ConsentKey, string Host, Guid ConversationId, string Folder, string? Language,
    PriorTranscription? Prior = null);

/// <summary>
/// Why the cloud live transcript the settings ask for does not run, for the recording page and Settings (macOS
/// <c>AppModel.CloudLiveBlock</c>).
/// </summary>
public enum CloudLiveBlock
{
    /// <summary>"Live with a local model", but no installed live model accepts the recording's language.</summary>
    NoLiveModel,
    /// <summary>"Live with &lt;Provider&gt; realtime", but the default provider has no realtime API (or no realtime model).</summary>
    RealtimeUnsupported,
    /// <summary>The default cloud transcription (or its cloud diarizer) waits for its one-time confirmation.</summary>
    CloudConsentPending,
    /// <summary>The realtime destination is not confirmed (<see cref="AppViewModel.ConfirmRealtimeDestination"/>): nothing is streamed.</summary>
    RealtimeConsentPending,
}

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
    /// "Transcribe while recording" are on and either
    /// <list type="bullet">
    /// <item>the default engines are local and the engine the Stop job resolves to is an installed Whisper or Parakeet
    /// entry;</item>
    /// <item>or the default ASR is a cloud engine (confirmed or not: nothing leaves the PC) with the live source "local
    /// model": <see cref="CloudLiveEntry"/>, display only (its finish returns null at once, so the cloud batch writes
    /// the transcript with no prior);</item>
    /// <item>or the default ASR is a cloud engine with the live source "realtime" and <see cref="RealtimeBlock"/> finds
    /// nothing in the way: <see cref="CreateRealtimeTranscriber"/>. When something is, nothing is streamed and there is
    /// no live transcript.</item>
    /// </list>
    /// Otherwise the sink is cleared, and a previous recording's captions are not this one's. Settings are read here, so
    /// a change during a recording applies from the next one.
    /// </summary>
    private async Task AttachRecordingTranscriberAsync(string language)
    {
        var settings = Settings;
        RecordingTranscriber? transcriber = null;
        // On the UI thread (the provider configs live here). A local ASR whose cloud diarizer waits for its
        // confirmation transcribes after Stop only, as on macOS.
        bool? displayOnly = !settings.AutoTranscribe || !settings.TranscribeWhileRecording ? null
            : AutoEngine() == AutoEngineKind.Local ? false
            : DefaultASRIsCloud && settings.CloudLiveSource == CloudLiveSource.Local ? true
            : null;
        var source = RecordingTranscriber.TranscriberSource.Chunks;
        if (displayOnly is null && settings.AutoTranscribe && settings.TranscribeWhileRecording && DefaultASRIsCloud
            && settings.CloudLiveSource == CloudLiveSource.Realtime)
        {
            // Builds nothing slow: the key is read in each session's task, off the UI thread.
            transcriber = CreateRealtimeTranscriber(settings);
            source = RecordingTranscriber.TranscriberSource.CloudRealtime;
            displayOnly = !settings.RealtimeAsFinal;
        }
        else if (displayOnly is { } display)
        {
            try
            {
                // Reads the installed models: off the UI thread.
                transcriber = await Task.Run(() => CreateRecordingTranscriber(language, settings, display));
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
        RecordingCaptions = new RecordingTranscriber.Snapshot(RecordingTranscriber.TranscriberState.Loading, [], 0)
        {
            Source = source,
            DisplayOnly = displayOnly == true,
        };
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
    /// the Stop job picks (<paramref name="displayOnly"/>: the <see cref="CloudLiveEntry"/>), loaded as a session when its
    /// marker vouches for its files (<see cref="ModelStore.MarkedFolder"/>); idle release after "Unload models after"
    /// minutes. An install without a valid marker is not hashed here, which
    /// would hold Record up to a whole model's read: the recording starts without live captions, the model is verified
    /// in the background, and the Stop job still checks it (<see cref="ModelStore.VerifiedFolder"/>).
    /// </summary>
    private RecordingTranscriber? CreateRecordingTranscriber(string language, AppSettings settings, bool displayOnly)
    {
        var found = displayOnly ? CloudLiveEntry(language, settings) : IncrementalEntry(PreferredEngine(language), language);
        if (found is not { } entry) return null;
        if (_services.Models.MarkedFolder(entry) is not { } folder)
        {
            _ = Task.Run(() => _services.Models.VerifiedFolder(entry)).ContinueWith(
                t => Debug.WriteLine($"EchoDiar: could not verify model {entry.Id}: {t.Exception?.InnerException?.GetType().Name}"),
                TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }
        if (_services.Engines[entry.Runtime](entry, folder) is not ISessionTranscriptionEngine engine) return null;
        return new RecordingTranscriber(
            entry, engine, _services.Gate, TimeSpan.FromMinutes(settings.ModelIdleMinutes), () => _liveLanguage,
            displayOnly);
    }

    /// <summary>
    /// A cloud realtime transcriber for the default cloud engine (macOS <c>startRealtimeTranscriber</c>); null when
    /// <see cref="RealtimeBlock"/> finds something in the way (nothing is streamed then). The key is read in each
    /// session's task, on the thread pool (<see cref="RealtimeLiveStream.Make"/>), never here. Speaker labels are asked
    /// of the stream (system channel only) only with "Tell speakers apart". Display only unless "Use the realtime result
    /// as the transcript" is on.
    /// </summary>
    private RecordingTranscriber? CreateRealtimeTranscriber(AppSettings settings)
    {
        if (DefaultCloudConfig is not { } config || RealtimeBlock(config) is not null) return null;
        var providerId = config.Id;
        var stream = RealtimeLiveStream.Make(
            ProvidersViewModel.Copy(config), settings.Diarization,
            cancellationToken => Task.Run(() => RealtimeKey(providerId), cancellationToken));
        if (stream is null) return null;
        return new RecordingTranscriber(
            stream, CloudTranscription.EngineId(providerId), displayOnly: !settings.RealtimeAsFinal, () => _liveLanguage);
    }

    /// <summary>
    /// <paramref name="providerId"/>'s saved key, "" when none or unreadable (the stream then stops with "config"); reads
    /// Credential Manager, so only on the thread pool. Only the error type is logged.
    /// </summary>
    private string RealtimeKey(string providerId)
    {
        try
        {
            return _services.Secrets.ApiKey(providerId) ?? "";
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read the key of {providerId}: {e.GetType().Name}");
            return "";
        }
    }

    /// <summary>The default ASR is an available cloud engine: the Stop job uploads (after a confirmation when pending).</summary>
    private bool DefaultASRIsCloud =>
        CloudTranscription.ProviderId(Settings.DefaultASRModelID) is { } id
        && CloudEngineConfigs.Any(c => ProvidersViewModel.SameId(c.Id, id));

    /// <summary>
    /// The local model of a display-only live transcript in cloud mode (macOS <c>cloudLiveEntry</c>): the picked
    /// <see cref="AppSettings.CloudLiveModelID"/> (null = the catalog default) when <see cref="IncrementalEntry"/>
    /// accepts it for <paramref name="language"/>; else (uninstalled, a language fallback, or a default that is no live
    /// model) the first <see cref="TranscriptionPipeline.LiveEntries"/> entry it accepts; null when none is.
    /// <paramref name="isUsable"/>: null = <see cref="IsUsableLocally"/> (reads the disk).
    /// </summary>
    private ModelEntry? CloudLiveEntry(string language, AppSettings settings, Func<ModelEntry, bool>? isUsable = null)
    {
        isUsable ??= IsUsableLocally;
        var picked = string.IsNullOrEmpty(settings.CloudLiveModelID) ? null : settings.CloudLiveModelID;
        if (IncrementalEntry(picked, language, isUsable) is { } entry) return entry;
        foreach (var candidate in TranscriptionPipeline.LiveEntries(isUsable))
        {
            if (IncrementalEntry(candidate.Id, language, isUsable) is { } accepted) return accepted;
        }
        return null;
    }

    /// <summary>A registered runtime and installed.</summary>
    private bool IsUsableLocally(ModelEntry entry) =>
        _services.Engines.ContainsKey(entry.Runtime) && _services.Models.IsInstalled(entry);

    /// <summary>
    /// The entry a recording transcriber runs so that the pipeline keeps its segments at Stop (Swift
    /// <c>TranscriptionPipeline.incrementalEntry</c>): the batch resolution for <paramref name="engineId"/> (explicit,
    /// else <see cref="TranscriptionPipeline.DefaultEngine"/>) and the recording's <paramref name="language"/>, when it is
    /// a usable Whisper or Parakeet entry that needs no language fallback; null otherwise. <paramref name="isUsable"/>:
    /// null = <see cref="IsUsableLocally"/> (reads the disk).
    /// </summary>
    private ModelEntry? IncrementalEntry(string? engineId, string language, Func<ModelEntry, bool>? isUsable = null)
    {
        isUsable ??= IsUsableLocally;
        var selected = engineId is null
            ? TranscriptionPipeline.DefaultEngine(language, isUsable)
            : ModelCatalog.Entry(engineId) is { Kind: ModelKind.Asr } entry && isUsable(entry) ? entry : null;
        if (selected is null || !TranscriptionPipeline.TranscribesChunks(selected)) return null;
        var markers = LanguageRange.Ranges([new RecordingSession.LanguageMarker { FileTime = 0, Language = language }])
            .Select(r => r.Language).OfType<string>().Select(WhisperLanguages.Primary).ToList();
        var (_, engineFallback) = TranscriptionPipeline.LanguageFallback(
            selected, TranscriptionPipeline.SupportedLanguages(selected), markers, language,
            id => ModelCatalog.Entry(id) is { } candidate && isUsable(candidate));
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

    /// <summary>
    /// A cloud confirmation waits, or a recording starts from the user: the main window must show (it may be hidden in
    /// the tray).
    /// </summary>
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
    /// Available cloud transcription providers with a model, a valid destination and a saved key (macOS
    /// <c>cloudEngineConfigs</c>); the key presence is the one <see cref="ProvidersViewModel"/> last read off the UI
    /// thread. <see cref="OnPropertyChanged(string)"/> is raised with this name when the configs or the keys change.
    /// </summary>
    public IReadOnlyList<ProviderConfig> CloudEngineConfigs =>
    [
        .. ProvidersModel.ProviderConfigs.Where(c =>
            c.IsAvailable(ProvidersModel.HasApiKey(c.Id)) && CloudTranscription.IsComplete(c) && HasKeyIfNeeded(c)),
    ];

    /// <summary>
    /// Available providers that can be the default cloud diarizer, with a valid destination and a saved key (macOS
    /// <c>cloudDiarizerConfigs</c>). Their diarization model is not checked here (Settings' model field would vanish
    /// mid-typing): <see cref="DefaultCloudDiarizer"/> does.
    /// </summary>
    public IReadOnlyList<ProviderConfig> CloudDiarizerConfigs =>
    [
        .. ProvidersModel.ProviderConfigs.Where(c =>
            c.IsAvailable(ProvidersModel.HasApiKey(c.Id)) && CloudTranscription.DiarizationPresets.Contains(c.Preset ?? "")
            && CloudTranscription.Supports(c)
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
    /// default engine (the settings' <c>DefaultASRModelID</c>, else the catalog default, as macOS
    /// <c>defaultASRModelID</c>) first, marked <see cref="EngineChoice.IsDefault"/>, when it is installed or
    /// configured; then the other installed local speech engines in catalog order, then the other configured cloud
    /// engines.
    /// </summary>
    public IReadOnlyList<EngineChoice> InstalledBatchEngines
    {
        get
        {
            List<EngineChoice> engines =
            [
                .. ModelCatalog.Asr.Where(e => InstalledModelIds.Contains(e.Id))
                    .Select(e => new EngineChoice(e.Id, e.DisplayName)),
                .. CloudEngineConfigs
                    .Select(c => new EngineChoice(CloudTranscription.EngineId(c.Id), CloudEngineName(c))),
            ];
            var defaultId = Settings.DefaultASRModelID ?? ModelCatalog.DefaultASR.Id;
            var index = engines.FindIndex(e => ProvidersViewModel.SameId(e.Id, defaultId));
            if (index < 0) return engines;
            var first = engines[index] with { IsDefault = true };
            engines.RemoveAt(index);
            engines.Insert(0, first);
            return engines;
        }
    }

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
        if (session is not null && !IsTranscribing(run.ConversationId))
        {
            _ = SubmitTranscription(run.Folder, session, run.EngineId, run.Language, prior: run.Prior);
        }
    }

    /// <summary>
    /// The cloud engines of one job, copies made on the UI thread; built with their keys off it. <see cref="AsrModel"/>:
    /// the cloud ASR's model when it is not its transcription model (<see cref="GapModel"/>); null = the transcription model.
    /// </summary>
    private sealed record CloudPlan(string? EngineId, ProviderConfig? Asr, ProviderConfig? Diarizer, string? AsrModel = null);

    /// <summary>
    /// The cloud part of <c>submitTranscription</c> (macOS): a cloud engine (picked, or the default) runs only while its
    /// provider is configured, else the local resolution applies (<see cref="CloudPlan.EngineId"/> null for a picked
    /// one). The default cloud diarizer runs when "Tell speakers apart" is on, the cloud ASR (if any) labels no speakers
    /// and its diarization model labels them. Null when a destination is not confirmed yet: the run then waits in
    /// <see cref="PendingCloudRuns"/> (with <paramref name="prior"/>) and nothing is submitted. With a cloud realtime
    /// <paramref name="prior"/> of the cloud ASR (the transcript, only its gaps uploaded), whether the ASR labels speakers
    /// follows the realtime model's streaming labels, and its gaps may go to the diarization model (<see cref="GapModel"/>).
    /// </summary>
    private CloudPlan? PlanCloud(
        string? engineId, Guid conversationId, string folder, string? language, PriorTranscription? prior = null)
    {
        ProviderConfig? asr = null;
        string? asrModel = null;
        var asrLabelsSpeakers = false;
        if (CloudTranscription.ProviderId(engineId ?? Settings.DefaultASRModelID) is { } providerId)
        {
            if (CloudEngineConfigs.FirstOrDefault(c => ProvidersViewModel.SameId(c.Id, providerId)) is { } config)
            {
                var cloudId = CloudTranscription.EngineId(config.Id);
                if (!CloudDestinationConfirmed(config, cloudId, conversationId, folder, language, prior)) return null;
                asr = ProvidersViewModel.Copy(config);
                engineId = cloudId;
                var realtimeModel = RealtimeModelOf(prior, config);
                asrLabelsSpeakers = AsrLabelsSpeakers(config, realtimeModel);
                asrModel = GapModel(config, prior, realtimeModel);
            }
            else if (CloudTranscription.ProviderId(engineId) is not null)
            {
                engineId = null;
            }
        }
        ProviderConfig? diarizer = null;
        if (Settings.Diarization && !asrLabelsSpeakers && DefaultCloudDiarizer() is { } cloudDiarizer)
        {
            if (!CloudDestinationConfirmed(cloudDiarizer, engineId, conversationId, folder, language, prior)) return null;
            diarizer = ProvidersViewModel.Copy(cloudDiarizer);
        }
        return new CloudPlan(engineId, asr, diarizer, asrModel);
    }

    /// <summary>
    /// The batch model of a cloud realtime prior's gap uploads (macOS <c>gapModel</c>): <paramref name="config"/>'s
    /// diarization model when "Tell speakers apart" is on, the stream labelled the system channel, a system gap (or a
    /// resume to bridge) is to fill and the transcription model labels no speakers, so the upload gets labels the
    /// pipeline bridges onto the stream's; null = the transcription model.
    /// </summary>
    private string? GapModel(ProviderConfig config, PriorTranscription? prior, string? realtimeModel)
    {
        if (prior is null || realtimeModel is null || !Settings.Diarization
            || (prior.SystemGaps.Count == 0 && prior.SystemBridges.Count == 0)
            || !prior.System.Any(s => !string.IsNullOrEmpty(s.Speaker))
            || CloudTranscription.Diarizes(config, CloudTranscription.Model(config)))
        {
            return null;
        }
        var model = CloudTranscription.DiarizationModel(config);
        return CloudTranscription.Diarizes(config, model) ? model : null;
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
    /// never counts as confirmed. <paramref name="prior"/> stays with the queued run, so a realtime transcript is not
    /// lost to the wait.
    /// </summary>
    private bool CloudDestinationConfirmed(
        ProviderConfig config, string? engineId, Guid conversationId, string folder, string? language,
        PriorTranscription? prior)
    {
        if (CloudConsent.IsConfirmed(Settings, config)) return true;
        if (CloudTranscription.Destination(config) is { } destination && PendingCloudRuns.All(r => r.Folder != folder))
        {
            if (PendingCloudRuns.Count == 0) MainWindowRequested?.Invoke();
            PendingCloudRuns =
            [
                .. PendingCloudRuns,
                new PendingCloudRun(
                    engineId, config.DisplayName, destination.ConsentKey, destination.Host, conversationId, folder, language, prior),
            ];
        }
        return false;
    }

    /// <summary>What the default engines of an automatic transcription resolve to, as <see cref="PlanCloud"/> decides it.</summary>
    private enum AutoEngineKind { Local, CloudConfirmed, ConsentPending }

    /// <summary>
    /// Side-effect free (reads <c>cloudSTTConfirmed</c>, never queues a confirmation; macOS <c>autoEngine</c>): mirrors
    /// <see cref="PlanCloud"/> with the default engines. A pending confirmation compresses a stopped recording first
    /// rather than holding its CAF behind the dialog, and a cloud default runs no live transcriber that the job keeps
    /// unless it is a cloud realtime one. With a cloud realtime <paramref name="prior"/> (or
    /// <paramref name="realtimeFinal"/>: a realtime transcript about to start that will be the transcript), whether a
    /// diarizer is needed follows the realtime model's streaming labels, not the batch model's.
    /// </summary>
    private AutoEngineKind AutoEngine(PriorTranscription? prior = null, bool realtimeFinal = false)
    {
        var engine = AutoEngineKind.Local;
        var asrLabelsSpeakers = false;
        if (CloudTranscription.ProviderId(Settings.DefaultASRModelID) is { } id
            && CloudEngineConfigs.FirstOrDefault(c => ProvidersViewModel.SameId(c.Id, id)) is { } config)
        {
            if (!CloudConsent.IsConfirmed(Settings, config)) return AutoEngineKind.ConsentPending;
            engine = AutoEngineKind.CloudConfirmed;
            var realtimeModel = realtimeFinal ? CloudRealtime.Model(config) : RealtimeModelOf(prior, config);
            asrLabelsSpeakers = AsrLabelsSpeakers(config, realtimeModel);
        }
        if (Settings.Diarization && !asrLabelsSpeakers && DefaultCloudDiarizer() is { } diarizer
            && !CloudConsent.IsConfirmed(Settings, diarizer))
        {
            return AutoEngineKind.ConsentPending;
        }
        return engine;
    }

    /// <summary>
    /// Whether <paramref name="config"/>'s realtime <paramref name="model"/> streams speaker labels when asked. Pure: the
    /// adapter is only built to read its flag, with no key.
    /// </summary>
    private static bool RealtimeStreamsSpeakers(ProviderConfig config, string model)
    {
        IRealtimeAdapter? adapter = CloudRealtime.Make(config with { RealtimeModel = model }, "", null, diarize: true);
        return adapter?.StreamsSpeakers ?? false;
    }

    /// <summary>
    /// The realtime model of <paramref name="prior"/> when it is a realtime prior of <paramref name="config"/> (the
    /// pipeline's cloud-prior branch takes it as the transcript); null otherwise.
    /// </summary>
    private static string? RealtimeModelOf(PriorTranscription? prior, ProviderConfig config) =>
        prior is not null && prior.EngineID == CloudTranscription.EngineId(config.Id)
            ? prior.ModelId ?? CloudRealtime.Model(config)
            : null;

    /// <summary>
    /// Whether the cloud ASR <paramref name="config"/> labels the speakers itself, so no diarizer is built: the
    /// <paramref name="realtimeModel"/>'s streaming labels when a realtime prior is the transcript, else the batch model's.
    /// </summary>
    private static bool AsrLabelsSpeakers(ProviderConfig config, string? realtimeModel) =>
        realtimeModel is null
            ? CloudTranscription.Diarizes(config, CloudTranscription.Model(config))
            : RealtimeStreamsSpeakers(config, realtimeModel);

    // Cloud live transcript settings

    /// <summary>The default ASR's cloud config when it is an available cloud engine.</summary>
    public ProviderConfig? DefaultCloudConfig =>
        CloudTranscription.ProviderId(Settings.DefaultASRModelID) is { } id
            ? CloudEngineConfigs.FirstOrDefault(c => ProvidersViewModel.SameId(c.Id, id))
            : null;

    /// <summary>
    /// Why the next (or current) recording has no cloud live transcript although the default ASR is a cloud engine,
    /// auto-transcribe and "Transcribe while recording" are on, and a live source is picked (macOS
    /// <c>cloudLiveBlock</c>); null when it runs, when the source is "off", or when the default engines are local. The
    /// same checks as <see cref="AttachRecordingTranscriberAsync"/>, with the current settings and the installed models
    /// as last read (<see cref="InstalledModelIds"/>, no disk read).
    /// </summary>
    public CloudLiveBlock? CurrentCloudLiveBlock
    {
        get
        {
            var settings = Settings;
            if (!settings.AutoTranscribe || !settings.TranscribeWhileRecording || DefaultCloudConfig is not { } config) return null;
            return settings.CloudLiveSource switch
            {
                CloudLiveSource.Local => CloudLiveEntry(RecordingLanguage, settings, IsInstalledAsRead) is null
                    ? CloudLiveBlock.NoLiveModel
                    : null,
                CloudLiveSource.Realtime => RealtimeBlock(config),
                _ => null,
            };
        }
    }

    /// <summary>
    /// What keeps <paramref name="config"/>'s realtime stream from starting now; null when it can. Realtime starts only
    /// once the cloud transcription is confirmed (with the diarizer the Stop job would need) and the realtime
    /// destination too.
    /// </summary>
    private CloudLiveBlock? RealtimeBlock(ProviderConfig config)
    {
        if (!RealtimeAvailable(config)) return CloudLiveBlock.RealtimeUnsupported;
        if (AutoEngine(realtimeFinal: Settings.RealtimeAsFinal) != AutoEngineKind.CloudConfirmed) return CloudLiveBlock.CloudConsentPending;
        if (!RealtimeDestinationConfirmed(config)) return CloudLiveBlock.RealtimeConsentPending;
        return null;
    }

    /// <summary>Whether <paramref name="config"/> can stream realtime: a realtime API, a valid realtime destination and a model.</summary>
    public static bool RealtimeAvailable(ProviderConfig config) =>
        CloudRealtime.Supports(config) && CloudRealtime.Destination(config) is not null && CloudRealtime.Model(config).Length > 0;

    /// <summary>Known realtime models of <paramref name="config"/>, the default first; empty when it has no realtime API.</summary>
    public static IReadOnlyList<string> RealtimeModels(ProviderConfig config) => CloudRealtime.Models(config);

    public static string RealtimeDefaultModel(ProviderConfig config) => CloudRealtime.DefaultModel(config);

    /// <summary>The realtime model streamed: <see cref="ProviderConfig.RealtimeModel"/>, else the default.</summary>
    public static string RealtimeModel(ProviderConfig config) => CloudRealtime.Model(config);

    /// <summary>The host the realtime audio and key go to, for the consent dialog and the settings row; null when unsupported.</summary>
    public static string? RealtimeHost(ProviderConfig config) => CloudRealtime.Destination(config)?.Host;

    /// <summary>
    /// Whether the realtime destination of <paramref name="config"/> is confirmed: its realtime consent
    /// (<c>rt:&lt;consent key&gt;</c>) was confirmed, or the batch destination on the same provider and origin was (the
    /// batch upload implies the stream, never the reverse). Never queues a confirmation.
    /// </summary>
    public bool RealtimeDestinationConfirmed(ProviderConfig config) =>
        CloudRealtime.Destination(config) is { } destination
        && (Settings.CloudSTTConfirmed.Contains(RealtimeConsentKey(destination.ConsentKey))
            || Settings.CloudSTTConfirmed.Contains(destination.ConsentKey));

    /// <summary>
    /// Remembers the realtime destination of <paramref name="config"/> as confirmed, after Settings' consent dialog.
    /// Stored under its own <c>rt:</c> key, so it never confirms the batch upload after Stop, even on a shared origin.
    /// </summary>
    public void ConfirmRealtimeDestination(ProviderConfig config)
    {
        if (CloudRealtime.Destination(config) is not { } destination) return;
        var key = RealtimeConsentKey(destination.ConsentKey);
        if (Settings.CloudSTTConfirmed.Contains(key)) return;
        SaveSettings(Settings with { CloudSTTConfirmed = [.. Settings.CloudSTTConfirmed, key] });
    }

    /// <summary>The realtime consent key stored in <c>cloudSTTConfirmed</c>, apart from the batch key it is made from.</summary>
    private static string RealtimeConsentKey(string consentKey) => "rt:" + consentKey;

    /// <summary>
    /// The local models the "Live with a local model" picker offers (<see cref="AppSettings.CloudLiveModelID"/>): the
    /// installed Whisper and Parakeet entries in catalog order, as last read (<see cref="InstalledModelIds"/>).
    /// </summary>
    public IReadOnlyList<ModelEntry> CloudLiveModelChoices => TranscriptionPipeline.LiveEntries(IsInstalledAsRead);

    /// <summary>A registered runtime and installed as <see cref="InstalledModelIds"/> last read (no disk read, for the UI thread).</summary>
    private bool IsInstalledAsRead(ModelEntry entry) =>
        _services.Engines.ContainsKey(entry.Runtime) && InstalledModelIds.Contains(entry.Id);

    /// <summary>
    /// Picks one live source in cloud mode (off, local model, realtime); leaving realtime turns "Use the realtime result
    /// as the transcript" off. Confirms nothing: Settings calls <see cref="ConfirmRealtimeDestination"/> after its dialog.
    /// </summary>
    public void SetCloudLiveSource(CloudLiveSource source)
    {
        var asFinal = source == CloudLiveSource.Realtime && Settings.RealtimeAsFinal;
        if (Settings.CloudLiveSource == source && Settings.RealtimeAsFinal == asFinal) return;
        SaveSettings(Settings with { CloudLiveSource = source, RealtimeAsFinal = asFinal });
    }

    /// <summary>"Use the realtime result as the transcript"; turning it on is ignored unless the live source is realtime.</summary>
    public void SetRealtimeAsFinal(bool on)
    {
        if ((on && Settings.CloudLiveSource != CloudLiveSource.Realtime) || Settings.RealtimeAsFinal == on) return;
        SaveSettings(Settings with { RealtimeAsFinal = on });
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
