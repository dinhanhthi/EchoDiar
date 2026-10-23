using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;

namespace EchoDiar.Transcribe;

/// <summary>
/// Batch transcription of recording folders, one job at a time (Swift <c>TranscriptionPipeline</c>): extract each
/// channel to 16 kHz mono, skip silent channels, ASR mic, ASR system, diarize system, merge, write
/// <c>transcript.json</c>. Models are loaded inside each step and unloaded before the next, while the job holds the
/// <see cref="ModelLoadGate"/> (it waits while a recording transcriber holds it). A job with a usable
/// <see cref="TranscriptionJob.Prior"/> keeps its segments and transcribes only its gaps, taking the gate only for gap
/// fill or diarization. While <paramref name="deferral"/> says so, a job waits before it takes a model (macOS
/// <c>waitWhileConstrained</c>), unless <see cref="TranscriptionJob.ProcessNow"/> or <see cref="ProcessNow"/>.
/// </summary>
/// <remarks>
/// Not ported yet: cloud engines and diarizers (Phase 26). The macOS thermal deferral has no Windows API; energy saver
/// stands in for Low Power Mode.
/// </remarks>
/// <param name="engines">Batch engine factories by catalog runtime.</param>
/// <param name="diarizer">The local diarizer factory; null = none.</param>
/// <param name="isInstalled">Whether a catalog entry is installed (<see cref="ModelStore.IsInstalled"/>).</param>
/// <param name="modelFolder">An entry's <c>Models/&lt;id&gt;/</c> folder (<see cref="ModelStore.Folder"/>).</param>
/// <param name="speakerCountHint">Speakers expected on the system channel when the job sets none.</param>
/// <param name="decoder">Reads non-CAF (compressed m4a) audio; null = CAF only.</param>
/// <param name="deferral">Holds jobs back while it says so (energy saver on); null = never.</param>
public sealed class TranscriptionPipeline(
    IReadOnlyDictionary<ModelRuntime, TranscriptionPipeline.EngineFactory> engines,
    TranscriptionPipeline.DiarizerFactory? diarizer,
    Func<ModelEntry, bool> isInstalled,
    Func<ModelEntry, string> modelFolder,
    ModelLoadGate gate,
    int? speakerCountHint = null,
    IAudioDecoder? decoder = null,
    IDeferralSignal? deferral = null)
{
    public delegate ITranscriptionEngine EngineFactory(ModelEntry entry, string folder);

    public delegate ISpeakerDiarizer DiarizerFactory(ModelEntry entry, string folder, int? speakerCountHint);

    private sealed record ChannelSource(string Path, int Channel);

    /// <summary>Transcribes one channel's 16 kHz extract; <paramref name="isMic"/> tells which channel it is.</summary>
    private delegate Task<IReadOnlyList<RawSegment>> ChannelAsr(
        string extracted, bool isMic, Action<double> progress, CancellationToken cancellationToken);

    private readonly SemaphoreSlim _queue = new(1, 1);

    /// <summary>The jobs waiting for <c>deferral</c> by conversation id, each with its "process now" release.</summary>
    private readonly Dictionary<Guid, TaskCompletionSource> _waiting = [];

    /// <summary>Every job's progress; raised on the job's thread.</summary>
    public event Action<JobProgress>? Progress;

    /// <summary>Runs after every job queued before it; returns the <c>transcript.json</c> path.</summary>
    public async Task<string> TranscribeAsync(TranscriptionJob job, CancellationToken cancellationToken = default)
    {
        Emit(job, JobStep.Queued, 0);
        await _queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync(job, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _queue.Release();
        }
    }

    // Job

    private async Task<string> RunAsync(TranscriptionJob job, CancellationToken cancellationToken)
    {
        var started = false;
        // Creates conversation.json when missing (transcribed before compression); null while unreadable.
        RecordingSession? stored = null;
        try
        {
            RecordingSession session;
            try
            {
                session = RecordingSession.Read(job.Folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                throw new TranscriptionException(TranscriptionErrorKind.SessionUnreadable, inner: e);
            }
            stored = session;
            if (job.Prior is { } prior && PriorSources(session, job) is { } priorSources
                && UsableEntry(prior, job, session) is { } priorEntry)
            {
                // Only gap fill and diarization need a model, so only they take the gate or wait for the deferral.
                var fillsGaps = prior.MicGaps.Count > 0 || (priorSources.System is not null && prior.SystemGaps.Count > 0);
                var priorDiarizer = Diarizer(job);
                var diarizes = priorSources.System is not null && prior.System.Count > 0 && priorDiarizer is not null;
                ISessionTranscriptionEngine? gapEngine = null;
                if (fillsGaps)
                {
                    if (!isInstalled(priorEntry)) throw new TranscriptionException(TranscriptionErrorKind.NeedsModel, priorEntry.Id);
                    gapEngine = engines.TryGetValue(priorEntry.Runtime, out var gapFactory)
                        && gapFactory(priorEntry, modelFolder(priorEntry)) is ISessionTranscriptionEngine sessions
                        ? sessions
                        : throw new TranscriptionException(TranscriptionErrorKind.UnknownEngine, priorEntry.Id);
                }
                if (fillsGaps || diarizes) await WaitWhileDeferredAsync(job, cancellationToken).ConfigureAwait(false);
                started = true;
                var priorPath = await TranscribePriorAsync(
                    job, prior, session, priorSources, priorEntry, gapEngine, priorDiarizer, cancellationToken).ConfigureAwait(false);
                Finished(job, session);
                return priorPath;
            }
            await WaitWhileDeferredAsync(job, cancellationToken).ConfigureAwait(false);
            // Never written back: the override only applies to this run.
            if (job.Language is { } language) session = session with { LanguageMarkers = [new() { FileTime = 0, Language = language }] };
            var sources = ChannelSources(session, job);
            var ranges = LanguageRange.Ranges(session.LanguageMarkers);
            var localDiarizer = Diarizer(job);

            var firstLanguage = session.LanguageMarkers.MinBy(m => m.FileTime)?.Language ?? "auto";
            var selected = ResolveEngine(job.EngineID, firstLanguage);
            var (entry, engineFallback) = Fallback(selected, session.LanguageMarkers, firstLanguage);
            if (!engines.TryGetValue(entry.Runtime, out var factory))
            {
                throw new TranscriptionException(TranscriptionErrorKind.UnknownEngine, entry.Id);
            }

            started = true;
            var engine = factory(entry, modelFolder(entry));
            var path = await gate.WithExclusiveModelAsync(ModelLoadGate.Owner.TranscriptionJob, token =>
            {
                UpdateConversation(job.Folder, session, c => c.Status = ConversationStatus.Transcribing);
                return ProcessAsync(
                    job, session, sources, entry.Id, ModelID(entry), engineFallback, localDiarizer, gateDiarization: false,
                    prior: null, (extracted, _, progress, t) => engine.TranscribeAsync(extracted, ranges, progress, t), token);
            }, cancellationToken).ConfigureAwait(false);

            Finished(job, session);
            return path;
        }
        catch (OperationCanceledException) when (!started)
        {
            // Cancelled while it waited: nothing ran, so the conversation keeps its status.
            throw;
        }
        catch (TranscriptionException e) when (e.Kind == TranscriptionErrorKind.NeedsModel)
        {
            if (started) UpdateConversation(job.Folder, stored, c => c.Status = ConversationStatus.Ready);
            Emit(job, JobStep.NeedsModel, 0, e.Detail);
            throw;
        }
        catch (Exception e)
        {
            // Also before "started", so an unreadable session or audio shows "Failed — Retry" on the card.
            UpdateConversation(job.Folder, stored, c => c.Status = ConversationStatus.TranscriptionFailed);
            Emit(job, JobStep.Failed, 0, e.Message);
            throw;
        }
    }

    // Deferral

    /// <summary>
    /// "Transcribe now" (macOS <c>processNow</c>, here for a job already queued): the conversation's job waiting for the
    /// deferral starts now. False when none of its jobs is waiting.
    /// </summary>
    public bool ProcessNow(Guid conversationID)
    {
        lock (_waiting)
        {
            return _waiting.TryGetValue(conversationID, out var now) && now.TrySetResult();
        }
    }

    /// <summary>
    /// Waits for the deferral's notifications while it says so (no polling), reporting <see cref="JobStep.Deferred"/>;
    /// returns at once for a "process now" job, ends on <see cref="ProcessNow"/>, and throws on cancellation. A job that
    /// waited reports <see cref="JobStep.Queued"/> again, until its next step (it may still wait for the gate).
    /// </summary>
    private async Task WaitWhileDeferredAsync(TranscriptionJob job, CancellationToken cancellationToken)
    {
        if (deferral is null || job.ProcessNow) return;
        // Run asynchronously: the signal and ProcessNow are raised on other threads, which must not run the job.
        var now = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiting) _waiting[job.ConversationID] = now;
        var waited = false;
        try
        {
            while (!now.Task.IsCompleted)
            {
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnChanged() => changed.TrySetResult();
                deferral.Changed += OnChanged;
                try
                {
                    // Read after subscribing, so a change in between is not lost.
                    if (!deferral.ShouldDefer) break;
                    Emit(job, JobStep.Deferred, 0);
                    waited = true;
                    await Task.WhenAny(changed.Task, now.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    deferral.Changed -= OnChanged;
                }
            }
        }
        finally
        {
            lock (_waiting) _waiting.Remove(job.ConversationID);
        }
        if (waited) Emit(job, JobStep.Queued, 0);
    }

    /// <summary>
    /// <paramref name="prior"/>'s segments plus its gaps, transcribed with one session of <paramref name="entry"/> loaded
    /// on the first chunk of speech and disposed before the gate is released; <paramref name="engine"/> null = no gaps,
    /// so no ASR model and no gate (a local diarizer takes it itself).
    /// </summary>
    private async Task<string> TranscribePriorAsync(
        TranscriptionJob job, PriorTranscription prior, RecordingSession session,
        (ChannelSource? Mic, ChannelSource? System) sources, ModelEntry entry, ISessionTranscriptionEngine? engine,
        ISpeakerDiarizer? localDiarizer, CancellationToken cancellationToken)
    {
        var modelID = ModelID(entry);
        UpdateConversation(job.Folder, session, c => c.Status = ConversationStatus.Transcribing);
        if (engine is null)
        {
            return await ProcessAsync(
                job, session, sources, entry.Id, modelID, null, localDiarizer, gateDiarization: true, prior,
                (_, _, _, _) => Task.FromResult<IReadOnlyList<RawSegment>>([]), cancellationToken).ConfigureAwait(false);
        }
        return await gate.WithExclusiveModelAsync(ModelLoadGate.Owner.TranscriptionJob, async token =>
        {
            var ranges = LanguageRange.Ranges(session.LanguageMarkers);
            // Loaded on the first chunk of speech, used for both channels, disposed here only.
            ITranscriptionSession? model = null;
            async Task<ITranscriptionSession> Session(CancellationToken t) =>
                model ??= await engine.OpenSessionAsync(t).ConfigureAwait(false);
            try
            {
                return await ProcessAsync(
                    job, session, sources, entry.Id, modelID, null, localDiarizer, gateDiarization: false, prior,
                    async (extracted, isMic, progress, t) => await FillGapsAsync(
                        isMic ? prior.MicGaps : prior.SystemGaps, extracted, entry, ranges, Session, progress, t).ConfigureAwait(false),
                    token).ConfigureAwait(false);
            }
            finally
            {
                if (model is not null) await model.DisposeAsync().ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The gaps of one channel's 16 kHz extract, in file time (Swift <c>fillGaps</c>): each gap is read in slabs of at
    /// most <see cref="MonoAudio.SlabSeconds"/> into one <see cref="SpeechChunker"/>, so silence is not decoded; a chunk
    /// below <see cref="MonoAudio.SilencePeak"/> is skipped, and each chunk gets the language of
    /// <paramref name="ranges"/> at its start. <paramref name="session"/> is called for every chunk to decode (the
    /// caller loads it once, on the first call). Only for a prior's gaps: the batch engines decode all the audio.
    /// </summary>
    public static async Task<List<RawSegment>> FillGapsAsync(
        IReadOnlyList<Span> gaps, string extract, ModelEntry entry, IReadOnlyList<LanguageRange> ranges,
        Func<CancellationToken, Task<ITranscriptionSession>> session, Action<double> progress,
        CancellationToken cancellationToken)
    {
        var duration = MonoAudio.Duration(extract);
        var total = Math.Max(1, gaps.Sum(g => Math.Max(0, Math.Min(g.End, duration) - Math.Max(0, g.Start))));
        var done = 0.0;
        var segments = new List<RawSegment>();
        foreach (var gap in gaps)
        {
            var gapStart = Math.Max(0, gap.Start);
            var gapEnd = Math.Min(gap.End, duration);
            if (gapEnd <= gapStart) continue;
            // One chunker per gap: chunk starts count samples from gapStart, and slab cuts never cut a chunk.
            var chunker = new SpeechChunker(entry.WindowSeconds ?? DefaultWindowSeconds);
            async Task Decode(List<SpeechChunker.Chunk> chunks)
            {
                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Peak(chunk.Samples) < MonoAudio.SilencePeak) continue;
                    var start = gapStart + chunk.StartSample / MonoAudio.SampleRate;
                    var language = ranges.LastOrDefault(r => r.Start <= start)?.Language;
                    var loaded = await session(cancellationToken).ConfigureAwait(false);
                    var decoded = await loaded.TranscribeAsync(chunk.Samples, language, cancellationToken).ConfigureAwait(false);
                    segments.AddRange(decoded.Select(s => s with { Start = s.Start + start, End = s.End + start }));
                    var end = start + chunk.Samples.Length / MonoAudio.SampleRate;
                    progress(Math.Min(1, (done + end - gapStart) / total));
                }
            }
            for (var slabStart = gapStart; slabStart < gapEnd;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slabEnd = Math.Min(slabStart + MonoAudio.SlabSeconds, gapEnd);
                await Decode(chunker.Append(MonoAudio.Read(extract, slabStart, slabEnd), null)).ConfigureAwait(false);
                progress(Math.Min(1, (done + slabEnd - gapStart) / total));
                slabStart = slabEnd;
            }
            await Decode(chunker.Flush()).ConfigureAwait(false);
            done += gapEnd - gapStart;
        }
        return segments;
    }

    /// <summary>The chunk length when the catalog entry sets no window (Whisper's 30 s).</summary>
    public const int DefaultWindowSeconds = 30;

    /// <summary>
    /// The entry that keeps <paramref name="prior"/>: the job's engine, else the prior's, when
    /// <see cref="PriorTranscription.IsUsable"/> for it, the session's language fallback, the job's language and the
    /// session audio's duration; null runs the whole batch.
    /// </summary>
    private ModelEntry? UsableEntry(PriorTranscription prior, TranscriptionJob job, RecordingSession session)
    {
        if (ModelCatalog.Entry(job.EngineID ?? prior.EngineID) is not { } entry || !TranscribesChunks(entry)) return null;
        if (ConversationFiles.AudioPath(job.Folder, session.AudioFileName) is not { } audio) return null;
        double duration;
        try
        {
            duration = MonoAudio.Duration(audio, decoder);
        }
        catch (TranscriptionException)
        {
            return null;
        }
        var firstLanguage = session.LanguageMarkers.MinBy(m => m.FileTime)?.Language ?? "auto";
        var (resolved, engineFallback) = Fallback(entry, session.LanguageMarkers, firstLanguage);
        return prior.IsUsable(resolved.Id, engineFallback, job.Language, duration) ? resolved : null;
    }

    /// <summary>The channel sources when the recording has a mic channel (a prior needs one); null otherwise.</summary>
    private (ChannelSource? Mic, ChannelSource? System)? PriorSources(RecordingSession session, TranscriptionJob job)
    {
        try
        {
            var sources = ChannelSources(session, job);
            return sources.Mic is null ? null : sources;
        }
        catch (TranscriptionException)
        {
            return null;
        }
    }

    /// <summary>A batch ASR entry that also loads as an <see cref="ITranscriptionSession"/>: Whisper or Parakeet.</summary>
    public static bool TranscribesChunks(ModelEntry entry) =>
        entry.Kind == ModelKind.Asr && entry.Runtime is ModelRuntime.WhisperNet or ModelRuntime.SherpaOnnx;

    /// <summary>
    /// Extracts each channel (skipping silent ones), runs <paramref name="asr"/> on it, diarizes the system channel
    /// unless <paramref name="asr"/> returned speaker labels, merges and writes <c>transcript.json</c> (keeping the one
    /// it replaces). <paramref name="gateDiarization"/>: the caller does not hold the <see cref="ModelLoadGate"/>, so the
    /// local diarizer takes it. With <paramref name="prior"/>, each channel is its segments plus <paramref name="asr"/>
    /// of its gaps: a channel is extracted only when it has gaps, or for the system channel's diarization.
    /// </summary>
    private async Task<string> ProcessAsync(
        TranscriptionJob job, RecordingSession session, (ChannelSource? Mic, ChannelSource? System) sources,
        string engineID, string? modelID, string? engineFallback, ISpeakerDiarizer? localDiarizer, bool gateDiarization,
        PriorTranscription? prior, ChannelAsr asr, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), "EchoDiar-transcribe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var mic = new List<RawSegment>(prior?.Mic ?? []);
            if (sources.Mic is { } micSource && (prior is null || prior.MicGaps.Count > 0))
            {
                Emit(job, JobStep.Extracting, 0);
                if (await ExtractAsync(micSource, Path.Combine(work, "mic.caf"), cancellationToken).ConfigureAwait(false) is { } path)
                {
                    Emit(job, JobStep.TranscribingMic, 0.05);
                    mic.AddRange(await asr(
                        path, true, f => Emit(job, JobStep.TranscribingMic, 0.05 + 0.35 * f), cancellationToken).ConfigureAwait(false));
                    TryDelete(path);
                }
            }

            var system = new List<RawSegment>(prior?.System ?? []);
            var turns = new List<DiarizationTurn>();
            var fillsSystem = prior is null || prior.SystemGaps.Count > 0;
            var diarizes = system.Count > 0 && localDiarizer is not null;
            if (sources.System is { } systemSource && (fillsSystem || diarizes))
            {
                Emit(job, JobStep.Extracting, 0.4);
                if (await ExtractAsync(systemSource, Path.Combine(work, "system.caf"), cancellationToken).ConfigureAwait(false) is { } path)
                {
                    if (fillsSystem)
                    {
                        Emit(job, JobStep.TranscribingSystem, 0.45);
                        system.AddRange(await asr(
                            path, false, f => Emit(job, JobStep.TranscribingSystem, 0.45 + 0.35 * f), cancellationToken).ConfigureAwait(false));
                    }
                    if (system.Count > 0 && !system.Any(s => s.Speaker is not null) && localDiarizer is not null)
                    {
                        Emit(job, JobStep.Diarizing, 0.8);
                        Task<IReadOnlyList<DiarizationTurn>> Diarize(CancellationToken t) =>
                            localDiarizer.DiarizeAsync(path, f => Emit(job, JobStep.Diarizing, 0.8 + 0.15 * f), t);
                        turns.AddRange(gateDiarization
                            ? await gate.WithExclusiveModelAsync(ModelLoadGate.Owner.TranscriptionJob, Diarize, cancellationToken).ConfigureAwait(false)
                            : await Diarize(cancellationToken).ConfigureAwait(false));
                    }
                }
            }

            if (prior is not null)
            {
                mic.Sort((a, b) => a.Start.CompareTo(b.Start));
                system.Sort((a, b) => a.Start.CompareTo(b.Start));
            }
            Emit(job, JobStep.Merging, 0.95);
            var notes = new List<string>();
            var providerSpeakers = system.Any(s => s.Speaker is not null);
            if (job.Diarize && localDiarizer is null && turns.Count == 0 && system.Count > 0 && !providerSpeakers)
            {
                notes.Add(Transcript.DiarizationMissingNote);
            }
            var transcript = new Transcript
            {
                Segments = TranscriptMerger.Merge(mic, system, turns, session.LanguageMarkers),
                EngineID = engineID,
                ModelID = modelID,
                EngineFallback = engineFallback,
                Notes = notes,
            };
            // A re-transcription keeps the transcript it replaces as a numbered version.
            transcript.Write(job.Folder, keepingPrevious: true);
            return Path.Combine(job.Folder, Transcript.FileName);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The extracted file, or null when the channel is silent.</summary>
    private async Task<string?> ExtractAsync(ChannelSource source, string path, CancellationToken cancellationToken)
    {
        var peak = await Task.Run(() => MonoAudio.Extract(source.Channel, source.Path, path, decoder is null ? null : decoder.DecodeToMono16k), cancellationToken).ConfigureAwait(false);
        if (peak >= MonoAudio.SilencePeak) return path;
        TryDelete(path);
        return null;
    }

    /// <summary>
    /// Stereo: ch0 mic, ch1 system. Two mono files: <c>audioFileName</c> mic, <see cref="TranscriptionJob.SystemFileName"/>
    /// system. A single mono file is the system channel.
    /// </summary>
    private (ChannelSource? Mic, ChannelSource? System) ChannelSources(RecordingSession session, TranscriptionJob job)
    {
        string Existing(string name) =>
            ConversationFiles.AudioPath(job.Folder, name) is { } path && File.Exists(path)
                ? path
                : throw new TranscriptionException(TranscriptionErrorKind.AudioUnreadable, $"{name} is missing");

        var audio = Existing(session.AudioFileName);
        if (job.SystemFileName is { } systemName) return (new(audio, 0), new(Existing(systemName), 0));
        if (MonoAudio.ChannelCount(audio, decoder) < 2) return (null, new(audio, 0));
        return (new(audio, RecordingSession.Channel.Mic), new(audio, RecordingSession.Channel.System));
    }

    /// <summary>The job's local diarizer; null when <see cref="TranscriptionJob.Diarize"/> is false or none is installed.</summary>
    private ISpeakerDiarizer? Diarizer(TranscriptionJob job)
    {
        if (!job.Diarize || diarizer is null) return null;
        var id = LocalDiarizerID(job.DiarizerID, id => ModelCatalog.Entry(id) is { } e && isInstalled(e));
        if (id is null || ModelCatalog.Entry(id) is not { } entry) return null;
        return diarizer(entry, modelFolder(entry), job.SpeakerCountHint ?? speakerCountHint);
    }

    // Routing

    /// <summary>A registered runtime and installed.</summary>
    private bool IsUsable(ModelEntry entry) => engines.ContainsKey(entry.Runtime) && isInstalled(entry);

    /// <summary>Explicit engine, else <see cref="DefaultEngine"/>, else "Needs model" (the default).</summary>
    private ModelEntry ResolveEngine(string? requested, string language)
    {
        if (requested is not null)
        {
            if (ModelCatalog.Entry(requested) is not { Kind: ModelKind.Asr } entry || !engines.ContainsKey(entry.Runtime))
            {
                throw new TranscriptionException(TranscriptionErrorKind.UnknownEngine, requested);
            }
            if (!isInstalled(entry)) throw new TranscriptionException(TranscriptionErrorKind.NeedsModel, entry.Id);
            return entry;
        }
        return DefaultEngine(language, IsUsable)
            ?? throw new TranscriptionException(TranscriptionErrorKind.NeedsModel, ModelCatalog.DefaultASR.Id);
    }

    /// <summary><paramref name="entry"/>'s supported languages, then <see cref="LanguageFallback"/>.</summary>
    private (ModelEntry Entry, string? EngineFallback) Fallback(
        ModelEntry entry, IReadOnlyList<RecordingSession.LanguageMarker> markers, string language)
    {
        var languages = LanguageRange.Ranges(markers).Select(r => r.Language).OfType<string>().Select(WhisperLanguages.Primary).ToList();
        return LanguageFallback(
            entry, SupportedLanguages(entry), languages, language, id => ModelCatalog.Entry(id) is { } e && IsUsable(e));
    }

    /// <summary>Written as the transcript's <c>modelID</c>: the repo, then its subfolder or single file.</summary>
    private static string? ModelID(ModelEntry entry)
    {
        var inside = entry.Subfolder ?? (entry.Files is [var file] ? file : null);
        string?[] parts = [entry.Repo, inside];
        var id = string.Join('/', parts.OfType<string>());
        return id.Length > 0 ? id : null;
    }

    /// <summary>Language codes an engine accepts: the catalog list, or Whisper's for multilingual Whisper entries; empty when unknown.</summary>
    public static IReadOnlyList<string> SupportedLanguages(ModelEntry entry) =>
        entry.Languages ?? (entry.Runtime == ModelRuntime.WhisperNet ? WhisperLanguages.All : []);

    /// <summary>
    /// Selectable for <paramref name="language"/> and, for a code, listed in <see cref="SupportedLanguages"/> by its
    /// primary code. An empty list is unknown and counts as supported; "auto" needs no code.
    /// </summary>
    public static bool Supports(ModelEntry entry, string language)
    {
        if (!entry.IsSelectable(language)) return false;
        var supported = SupportedLanguages(entry);
        return language == "auto" || supported.Count == 0 || supported.Contains(WhisperLanguages.Primary(language));
    }

    /// <summary>
    /// With no engine chosen: the catalog default if usable, then the smallest usable Whisper entry selectable for
    /// <paramref name="language"/>, then the first usable batch entry that <see cref="Supports"/> it, then the first
    /// selectable for it, in catalog order. <paramref name="isUsable"/> includes "its runtime is registered".
    /// </summary>
    public static ModelEntry? DefaultEngine(string language, Func<ModelEntry, bool> isUsable)
    {
        var installed = ModelCatalog.Asr.Where(isUsable).ToList();
        return installed.FirstOrDefault(e => e.IsDefault)
            ?? installed.Where(e => e.Runtime == ModelRuntime.WhisperNet && e.IsSelectable(language)).MinBy(e => e.DiskBytes)
            ?? installed.FirstOrDefault(e => Supports(e, language))
            ?? installed.FirstOrDefault(e => e.IsSelectable(language));
    }

    /// <summary>
    /// The local diarizer: <paramref name="preferred"/> when it is an installed catalog diarizer, else the first
    /// installed one in catalog order; null when none is installed. (macOS's Nemotron speaker-limit rule has no
    /// Windows entry.)
    /// </summary>
    public static string? LocalDiarizerID(string? preferred, Func<string, bool> isInstalled)
    {
        var installed = ModelCatalog.Diarization.Select(e => e.Id).Where(isInstalled).ToList();
        return preferred is not null && installed.Contains(preferred) ? preferred : installed.FirstOrDefault();
    }

    /// <summary>
    /// <paramref name="entry"/>, or an installed Whisper stand-in (the default first, else the smallest) when one of
    /// <paramref name="markerLanguages"/> (primary codes) is not in <paramref name="supported"/>, with
    /// <c>entry.Id</c> as the fallback note. An empty <paramref name="supported"/> list is unknown and counts as
    /// supported. The stand-in transcribes the whole job, so it must support every marker language and be
    /// selectable for <paramref name="language"/>; with none, <paramref name="entry"/> runs anyway.
    /// </summary>
    public static (ModelEntry Entry, string? EngineFallback) LanguageFallback(
        ModelEntry entry, IReadOnlyList<string> supported, IReadOnlyList<string> markerLanguages, string language,
        Func<string, bool> isInstalled)
    {
        if (supported.Count == 0 || markerLanguages.All(supported.Contains)) return (entry, null);
        var standIns = ModelCatalog.Asr.Where(candidate =>
        {
            var candidateLanguages = SupportedLanguages(candidate);
            return candidate.Runtime == ModelRuntime.WhisperNet && candidate.Id != entry.Id && isInstalled(candidate.Id)
                && candidate.IsSelectable(language)
                && (candidateLanguages.Count == 0 || markerLanguages.All(candidateLanguages.Contains));
        }).ToList();
        var standIn = standIns.FirstOrDefault(e => e.IsDefault) ?? standIns.MinBy(e => e.DiskBytes);
        return standIn is null ? (entry, null) : (standIn, entry.Id);
    }

    // Helpers

    /// <summary>The job succeeded: conversation.json says transcribed, then the Finished event.</summary>
    private void Finished(TranscriptionJob job, RecordingSession session)
    {
        UpdateConversation(job.Folder, session, c =>
        {
            c.Status = ConversationStatus.Transcribed;
            c.HasTranscript = true;
            c.SizeBytes = LibraryStore.SizeOnDisk(job.Folder);
        });
        Emit(job, JobStep.Finished, 1);
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    private void Emit(TranscriptionJob job, JobStep step, double fraction, string? detail = null) =>
        Progress?.Invoke(new JobProgress(job.ConversationID, step, fraction, detail));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Read-modify-write of <c>conversation.json</c>. A missing one (transcribed before compression) is created from
    /// <paramref name="session"/> with the session audio's duration; nothing is written without a session, or when
    /// the existing file is unreadable.
    /// </summary>
    private void UpdateConversation(string folder, RecordingSession? session, Action<Conversation> change)
    {
        Conversation conversation;
        if (File.Exists(Path.Combine(folder, Conversation.FileName)))
        {
            try
            {
                conversation = Conversation.Read(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return;
            }
        }
        else
        {
            if (session is null) return;
            var duration = 0.0;
            if (ConversationFiles.AudioPath(folder, session.AudioFileName) is { } audio)
            {
                try { duration = MonoAudio.Duration(audio, decoder); }
                catch (TranscriptionException) { }
            }
            conversation = Conversation.FromSession(session, folder, duration);
        }
        change(conversation);
        try { conversation.Write(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Whether transcription jobs should wait (macOS <c>TranscriptionPipeline.isConstrained</c> with "wait" on); the app
/// adapts the Platform's energy saver state and the setting into it.
/// </summary>
public interface IDeferralSignal
{
    bool ShouldDefer { get; }

    /// <summary>Raised on any thread when <see cref="ShouldDefer"/> may have changed.</summary>
    event Action? Changed;
}
