using System.Diagnostics;
using EchoDiar.AI.Cloud;
using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;

namespace EchoDiar.Transcribe;

/// <summary>
/// Batch transcription of recording folders, one local job at a time (Swift <c>TranscriptionPipeline</c>): extract each
/// channel to 16 kHz mono, skip silent channels, ASR mic, ASR system, diarize system, merge, write
/// <c>transcript.json</c>. Models are loaded inside each step and unloaded before the next, while the job holds the
/// <see cref="ModelLoadGate"/> (it waits while a recording transcriber holds it). A job with a usable
/// <see cref="TranscriptionJob.Prior"/> keeps its segments and transcribes only its gaps, taking the gate only for gap
/// fill or diarization. While <paramref name="deferral"/> says so, a job waits before it takes a model (macOS
/// <c>waitWhileConstrained</c>), unless <see cref="TranscriptionJob.ProcessNow"/> or <see cref="ProcessNow"/>.
/// A job with a <see cref="CloudEngine"/> sends each channel to its API instead (no ASR model, so no gate) and skips
/// the queue: cloud jobs run at once, beside each other and a local job (the deferral wait and the gate around a local
/// diarization still apply). A cloud diarizer labels the system channel first, the local diarizer after its failure.
/// The app resolves both, keys included, and asks the one-time confirmation before it submits the job.
/// </summary>
/// <remarks>
/// The macOS thermal deferral has no Windows API; energy saver stands in for Low Power Mode.
/// </remarks>
/// <param name="engines">Batch engine factories by catalog runtime.</param>
/// <param name="diarizer">The local diarizer factory; null = none.</param>
/// <param name="isInstalled">Whether a catalog entry is installed (<see cref="ModelStore.IsInstalled"/>).</param>
/// <param name="modelFolder">
/// An entry's <c>Models/&lt;id&gt;/</c> folder, asked for right before its model is loaded (the app passes
/// <see cref="ModelStore.VerifiedFolder"/> and throws <see cref="TranscriptionErrorKind.NeedsModel"/> when the files fail their check).
/// </param>
/// <param name="speakerCountHint">Speakers expected on the system channel when the job sets none.</param>
/// <param name="decoder">Reads non-CAF (compressed m4a) audio; null = CAF only.</param>
/// <param name="deferral">Holds jobs back while it says so (energy saver on); null = never.</param>
/// <param name="exporter">Writes a channel as the mono m4a a cloud API gets; null = cloud jobs fail.</param>
public sealed class TranscriptionPipeline(
    IReadOnlyDictionary<ModelRuntime, TranscriptionPipeline.EngineFactory> engines,
    TranscriptionPipeline.DiarizerFactory? diarizer,
    Func<ModelEntry, bool> isInstalled,
    Func<ModelEntry, string> modelFolder,
    ModelLoadGate gate,
    int? speakerCountHint = null,
    IAudioDecoder? decoder = null,
    IDeferralSignal? deferral = null,
    IAudioExporter? exporter = null)
{
    public delegate ITranscriptionEngine EngineFactory(ModelEntry entry, string folder);

    public delegate ISpeakerDiarizer DiarizerFactory(ModelEntry entry, string folder, int? speakerCountHint);

    private sealed record ChannelSource(string Path, int Channel);

    /// <summary>
    /// Transcribes one channel: its 16 kHz extract, or its <paramref name="source"/> (a cloud API gets the compressed
    /// audio, not the extract); <paramref name="isMic"/> tells which channel it is.
    /// </summary>
    private delegate Task<IReadOnlyList<RawSegment>> ChannelAsr(
        string extracted, ChannelSource source, bool isMic, Action<double> progress, CancellationToken cancellationToken);

    private readonly SemaphoreSlim _queue = new(1, 1);

    /// <summary>The jobs waiting for <c>deferral</c> by conversation id, each with its "process now" release.</summary>
    private readonly Dictionary<Guid, TaskCompletionSource> _waiting = [];

    /// <summary>Every job's progress; raised on the job's thread.</summary>
    public event Action<JobProgress>? Progress;

    /// <summary>Runs after every local job queued before it; returns the <c>transcript.json</c> path.</summary>
    public Task<string> TranscribeAsync(TranscriptionJob job, CancellationToken cancellationToken = default) =>
        TranscribeAsync(job, null, null, cancellationToken);

    /// <summary>
    /// Runs after every local job queued before it, or at once with <paramref name="cloud"/>; returns the
    /// <c>transcript.json</c> path. <paramref name="cloud"/>: the job's cloud engine, which replaces
    /// <see cref="TranscriptionJob.EngineID"/>'s resolution.
    /// <paramref name="cloudDiarizer"/>: labels the system channel's speakers when the ASR does not (its text is
    /// dropped); unused when <see cref="TranscriptionJob.Diarize"/> is false. Both are resolved by the app, whose
    /// one-time confirmation of their destination comes first: the pipeline uploads to whatever it is given.
    /// </summary>
    public async Task<string> TranscribeAsync(
        TranscriptionJob job, CloudEngine? cloud, CloudEngine? cloudDiarizer, CancellationToken cancellationToken = default)
    {
        Emit(job, JobStep.Queued, 0);
        // Only local jobs queue: a cloud job loads no ASR model (a local diarization takes the gate on its own).
        var queued = cloud is null;
        if (queued) await _queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync(job, cloud, job.Diarize ? cloudDiarizer : null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (queued) _queue.Release();
        }
    }

    // Job

    private async Task<string> RunAsync(
        TranscriptionJob job, CloudEngine? cloud, CloudEngine? cloudDiarizer, CancellationToken cancellationToken)
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
            if (cloud is not null && job.Prior is { } cloudPrior && cloudPrior.EngineID == cloud.EngineID
                && PriorSources(session, job) is { } cloudSources && AudioDuration(job, session) is { } audioDuration
                && cloudPrior.IsUsable(cloud.EngineID, null, job.Language, audioDuration))
            {
                // A cloud realtime stream as the transcript: only its gaps are uploaded, and a diarizer runs only when the
                // stream labelled no speakers.
                var streamLabels = job.Diarize && cloudPrior.System.Any(s => !string.IsNullOrEmpty(s.Speaker));
                var streamDiarizer = streamLabels ? null : Diarizer(job);
                var streamCloudDiarizer = streamLabels ? null : cloudDiarizer;
                var fillsGaps = cloudPrior.MicGaps.Count > 0
                    || (cloudSources.System is not null && cloudPrior.SystemGaps.Count > 0)
                    || (cloudSources.System is not null && streamLabels && cloudPrior.SystemBridges.Count > 0);
                var diarizes = cloudSources.System is not null && (streamDiarizer is not null || streamCloudDiarizer is not null);
                if (fillsGaps || diarizes) await WaitWhileDeferredAsync(job, cancellationToken).ConfigureAwait(false);
                started = true;
                var cloudPriorPath = await TranscribeCloudPriorAsync(
                    job, cloudPrior, session, cloudSources, cloud, streamLabels, audioDuration, streamDiarizer,
                    streamCloudDiarizer, cancellationToken).ConfigureAwait(false);
                Finished(job, session);
                return cloudPriorPath;
            }
            if (cloud is null && job.Prior is { } prior && PriorSources(session, job) is { } priorSources
                && UsableEntry(prior, job, session) is { } priorEntry)
            {
                // Only gap fill and diarization need a model, so only they take the gate or wait for the deferral.
                var fillsGaps = prior.MicGaps.Count > 0 || (priorSources.System is not null && prior.SystemGaps.Count > 0);
                var priorDiarizer = Diarizer(job);
                var diarizes = priorSources.System is not null && prior.System.Count > 0
                    && (priorDiarizer is not null || cloudDiarizer is not null);
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
                    job, prior, session, priorSources, priorEntry, gapEngine, priorDiarizer, cloudDiarizer, cancellationToken)
                    .ConfigureAwait(false);
                Finished(job, session);
                return priorPath;
            }
            await WaitWhileDeferredAsync(job, cancellationToken).ConfigureAwait(false);
            // Never written back: the override only applies to this run.
            if (job.Language is { } language) session = session with { LanguageMarkers = [new() { FileTime = 0, Language = language }] };
            var sources = ChannelSources(session, job);
            var ranges = LanguageRange.Ranges(session.LanguageMarkers);
            var localDiarizer = Diarizer(job);

            if (cloud is not null)
            {
                // No local ASR model: only a local diarization takes the gate (inside ProcessAsync).
                started = true;
                UpdateConversation(job.Folder, session, c => c.Status = ConversationStatus.Transcribing);
                var cloudLanguage = CloudLanguage(ranges);
                var cloudPath = await ProcessAsync(
                    job, session, sources, cloud.EngineID, cloud.Model.Length == 0 ? null : cloud.Model, null, localDiarizer,
                    cloudDiarizer, gateDiarization: true, prior: null,
                    (_, source, isMic, _, t) => CloudSegmentsAsync(cloud, source, cloudLanguage, job.Diarize && !isMic, t),
                    cancellationToken).ConfigureAwait(false);
                Finished(job, session);
                return cloudPath;
            }

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
                    job, session, sources, entry.Id, ModelID(entry), engineFallback, localDiarizer, cloudDiarizer,
                    gateDiarization: false, prior: null,
                    (extracted, _, _, progress, t) => engine.TranscribeAsync(extracted, ranges, progress, t), token);
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
        ISpeakerDiarizer? localDiarizer, CloudEngine? cloudDiarizer, CancellationToken cancellationToken)
    {
        var modelID = ModelID(entry);
        UpdateConversation(job.Folder, session, c => c.Status = ConversationStatus.Transcribing);
        if (engine is null)
        {
            return await ProcessAsync(
                job, session, sources, entry.Id, modelID, null, localDiarizer, cloudDiarizer, gateDiarization: true, prior,
                (_, _, _, _, _) => Task.FromResult<IReadOnlyList<RawSegment>>([]), cancellationToken).ConfigureAwait(false);
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
                    job, session, sources, entry.Id, modelID, null, localDiarizer, cloudDiarizer, gateDiarization: false, prior,
                    async (extracted, _, isMic, progress, t) => await FillGapsAsync(
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
    /// Covered audio a gap upload takes on each side of the gap on a channel without stream speaker labels (mic, or a
    /// system stream with none); a labelled channel takes <see cref="SpeakerLabelReconciler.LabelOverlapSeconds"/>.
    /// </summary>
    internal const double GapPaddingSeconds = 0.5;

    /// <summary>
    /// A cloud realtime <paramref name="prior"/> as the transcript (Swift <c>transcribeCloudPrior</c>): each channel's
    /// gaps go to <paramref name="cloud"/> (<see cref="FillCloudGapsAsync"/>), then <see cref="ProcessAsync"/> merges,
    /// with no gaps left, so nothing else is uploaded unless a diarizer is given. <paramref name="streamLabels"/>:
    /// <see cref="TranscriptionJob.Diarize"/> is on and the stream labelled the system channel's speakers, so its gaps are
    /// diarized and bridged onto those labels and the caller passes no diarizer, and the audio around each resume with
    /// scoped labels is uploaded only to map them back (the bridges). With <see cref="TranscriptionJob.Diarize"/> off the
    /// stream labels are dropped (one speaker). The transcript records the realtime model.
    /// </summary>
    private async Task<string> TranscribeCloudPriorAsync(
        TranscriptionJob job, PriorTranscription prior, RecordingSession session,
        (ChannelSource? Mic, ChannelSource? System) sources, CloudEngine cloud, bool streamLabels, double duration,
        ISpeakerDiarizer? localDiarizer, CloudEngine? cloudDiarizer, CancellationToken cancellationToken)
    {
        UpdateConversation(job.Folder, session, c => c.Status = ConversationStatus.Transcribing);
        var language = CloudLanguage(LanguageRange.Ranges(session.LanguageMarkers));
        IReadOnlyList<RawSegment> mic = prior.Mic;
        IReadOnlyList<RawSegment> system = job.Diarize ? prior.System : [.. prior.System.Select(s => s with { Speaker = null })];
        if (sources.Mic is { } micSource && prior.MicGaps.Count > 0)
        {
            Emit(job, JobStep.TranscribingMic, 0.05);
            mic = await FillCloudGapsAsync(
                prior.MicGaps, [], mic, micSource, cloud, language, labels: false, duration,
                f => Emit(job, JobStep.TranscribingMic, 0.05 + 0.35 * f), cancellationToken).ConfigureAwait(false);
        }
        if (sources.System is { } systemSource && (prior.SystemGaps.Count > 0 || (streamLabels && prior.SystemBridges.Count > 0)))
        {
            Emit(job, JobStep.TranscribingSystem, 0.45);
            system = await FillCloudGapsAsync(
                prior.SystemGaps, prior.SystemBridges, system, systemSource, cloud, language, labels: streamLabels, duration,
                f => Emit(job, JobStep.TranscribingSystem, 0.45 + 0.35 * f), cancellationToken).ConfigureAwait(false);
        }
        var filled = prior with { Mic = mic, System = system, MicGaps = [], SystemGaps = [], SystemBridges = [] };
        return await ProcessAsync(
            job, session, sources, cloud.EngineID, prior.ModelId ?? (cloud.Model.Length == 0 ? null : cloud.Model), null,
            localDiarizer, cloudDiarizer, gateDiarization: true, filled,
            (_, _, _, _, _) => Task.FromResult<IReadOnlyList<RawSegment>>([]), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One channel's <paramref name="segments"/> with each of its <paramref name="gaps"/> transcribed by
    /// <paramref name="cloud"/> from the compressed source (Swift <c>fillCloudGaps</c>): the gap padded into covered audio
    /// (<see cref="SpeakerLabelReconciler.LabelOverlapSeconds"/> each side with <paramref name="labels"/>, else
    /// <see cref="GapPaddingSeconds"/>), clamped to <c>0...duration</c>, sent through the range API (split under the
    /// provider's cap) and joined by <see cref="JoinGap"/>. <paramref name="labels"/>: the upload asks for speaker labels
    /// and they are bridged onto the stream's. With <paramref name="labels"/>, each of <paramref name="bridges"/> (file
    /// seconds of a resume with session-scoped labels and no gap) is a zero-length gap: its upload adds no segment and
    /// only maps the labels first heard after it back; none is uploaded when no label is new after it (a gap's padding
    /// bridged it) or the batch model only streams. In file order, so each later span bridges onto the earlier ones.
    /// Errors other than a cancellation become <see cref="TranscriptionErrorKind.Cloud"/> with a short, key-free reason.
    /// </summary>
    private async Task<IReadOnlyList<RawSegment>> FillCloudGapsAsync(
        IReadOnlyList<Span> gaps, IReadOnlyList<double> bridges, IReadOnlyList<RawSegment> segments, ChannelSource source,
        CloudEngine cloud, string? language, bool labels, double duration, Action<double> progress,
        CancellationToken cancellationToken)
    {
        if (CloudTranscription.IsStreamingOnly(cloud.Model))
        {
            if (gaps.Count == 0) return segments;
            throw new TranscriptionException(
                TranscriptionErrorKind.Cloud, $"{cloud.Name}: {cloud.Model} only streams live audio; pick another model in Settings");
        }
        var padding = labels ? SpeakerLabelReconciler.LabelOverlapSeconds : GapPaddingSeconds;
        var spans = gaps.Concat(labels ? bridges.Select(t => new Span(t, t)) : []).OrderBy(s => s.Start).ToList();
        var joined = segments.OrderBy(s => s.Start).ToList();
        for (var index = 0; index < spans.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gap = spans[index];
            var lower = Math.Max(0, gap.Start - padding);
            var upper = Math.Min(duration, gap.End + padding);
            if (gap.End < gap.Start || !(upper > lower)) continue;
            if (gap.End == gap.Start && !HasNewLabels(joined, gap.Start)) continue;
            var upload = new TimeRange(lower, upper);
            List<CloudSegment> response;
            try
            {
                response = await CloudTranscription.TranscribeAsync(
                    source.Path, source.Channel, upload, cloud.Transcriber, Exporter, language, labels, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // Not kept as the inner exception: a provider message can carry the request or response body.
                throw new TranscriptionException(TranscriptionErrorKind.Cloud, $"{cloud.Name}: {CloudTranscription.FailureReason(e)}");
            }
            joined = JoinGap(response, gap, upload, joined, labels, index + 1);
            progress((index + 1.0) / spans.Count);
        }
        return joined;
    }

    /// <summary>Whether a speaker label of <paramref name="segments"/> is first heard at or after <paramref name="time"/>.</summary>
    private static bool HasNewLabels(IEnumerable<RawSegment> segments, double time)
    {
        var before = new HashSet<string>(StringComparer.Ordinal);
        var after = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment.Speaker)) continue;
            (segment.Start < time ? before : after).Add(segment.Speaker);
        }
        return after.Except(before).Any();
    }

    /// <summary>
    /// <paramref name="segments"/> (one channel, file time) with a gap's upload <paramref name="response"/> (file time,
    /// covering <paramref name="upload"/> = the gap and its padding) joined, sorted by start (Swift <c>joinGap</c>). The
    /// response segment that straddles an edge of <paramref name="gap"/> is cut into its sentences first
    /// (<see cref="SentencePieces"/>; one without sentence ends stays whole); a piece is kept when its overlap with the gap
    /// is at least half of the shorter of the two (<see cref="GapKeeps"/>), so a long word-grouped segment neither loses
    /// its speech inside the gap nor repeats what the stream has around it; the padding's only serve the labels. Without <paramref name="labels"/> every speaker label is dropped. With
    /// <paramref name="labels"/>, two steps: the response's labels map onto the labels before the gap by the left padding
    /// (<see cref="SpeakerLabelReconciler.Labels"/>; a label with no match gets its own <c>"p&lt;N&gt;:"</c> scope,
    /// <paramref name="partIndex"/> and up, so it never merges); then each label first heard after the gap (the session
    /// after an unplanned disconnect, <c>"r&lt;N&gt;:"</c>) takes the label of (before + response) it matches in the
    /// right padding, unless that label is already in use after the gap; a label with no match keeps its own. Pure, for
    /// tests.
    /// </summary>
    internal static List<RawSegment> JoinGap(
        IReadOnlyList<CloudSegment> response, Span gap, TimeRange upload, IReadOnlyList<RawSegment> segments, bool labels,
        int partIndex)
    {
        bool InGap(CloudSegment segment) => GapKeeps(segment, gap);
        static string? Label(string? speaker) => string.IsNullOrEmpty(speaker) ? null : speaker;
        RawSegment Raw(CloudSegment segment) =>
            new(segment.Start, segment.End, segment.Text, segment.Language, labels ? Label(segment.Speaker) : null);
        static HashSet<string> LabelSet(IEnumerable<CloudSegment> list) =>
            list.Select(s => Label(s.Speaker)).OfType<string>().ToHashSet(StringComparer.Ordinal);

        IEnumerable<RawSegment> Kept(IEnumerable<CloudSegment> list) =>
            list.SelectMany(segment => SentencePieces(segment, gap)).Where(InGap).Select(Raw);

        if (!labels) return [.. segments.Concat(Kept(response)).OrderBy(s => s.Start)];
        var existing = segments.Select(s => new CloudSegment(s.Start, s.End, s.Text, s.Speaker, s.DetectedLanguage)).ToList();
        // Step 1: the gap onto the session before. Every existing label counts for the scope; only the left padding
        // counts for the matches.
        var left = new TimeRange(upload.Start, Math.Max(upload.Start, gap.Start));
        var gapMapping = SpeakerLabelReconciler.Labels(response, existing, left, partIndex);
        var mapped = response.Select(segment => segment with
        {
            Speaker = Label(segment.Speaker) is { } label && gapMapping.TryGetValue(label, out var target) ? target : null,
        }).ToList();
        // Step 2: the session after onto (before + gap).
        var before = existing.Where(s => s.Start < gap.Start).ToList();
        var after = existing.Where(s => s.Start >= gap.Start).ToList();
        var afterLabels = LabelSet(after);
        var newLabels = afterLabels.Except(LabelSet(before)).ToHashSet(StringComparer.Ordinal);
        var earlier = before.Concat(mapped).ToList();
        var earlierLabels = LabelSet(earlier);
        var right = new TimeRange(Math.Min(Math.Max(gap.End, upload.Start), upload.End), upload.End);
        var afterMapping = newLabels.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : SpeakerLabelReconciler.Labels(
                    [.. after.Where(s => Label(s.Speaker) is { } label && newLabels.Contains(label))], earlier, right, partIndex)
                .Where(pair => earlierLabels.Contains(pair.Value) && !afterLabels.Contains(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var bridged = segments.Select(segment =>
            Label(segment.Speaker) is { } speaker && afterMapping.TryGetValue(speaker, out var target)
                ? segment with { Speaker = target }
                : segment);
        return [.. bridged.Concat(Kept(mapped)).OrderBy(s => s.Start)];
    }

    /// <summary>
    /// Whether a gap upload's <paramref name="segment"/> belongs to <paramref name="gap"/>: it overlaps it by at least half
    /// of the shorter of the two (a zero-length segment: its time is inside the gap). A zero-length gap (a resume bridge)
    /// keeps nothing. Chosen trade-off: next to a hole shorter than a sentence, the one sentence over it is kept whole and
    /// may repeat words the stream has around the hole, rather than losing the hole's speech; a segment without sentence
    /// ends (not cut) can repeat more (Swift <c>gapKeeps</c>). Pure, for tests.
    /// </summary>
    internal static bool GapKeeps(CloudSegment segment, Span gap)
    {
        var length = segment.End - segment.Start;
        if (length <= 0) return segment.Start >= gap.Start && segment.Start < gap.End;
        var overlap = Math.Min(segment.End, gap.End) - Math.Max(segment.Start, gap.Start);
        return overlap > 0 && overlap >= Math.Min(length, gap.End - gap.Start) / 2;
    }

    /// <summary>
    /// <paramref name="segment"/> cut after each sentence end (<c>.?!</c> before a space, <c>。？！</c>, or the end) when it straddles
    /// the start or the end of <paramref name="gap"/>, each piece timed by its share of the text (approximate: the
    /// providers' segments carry no word times); else <paramref name="segment"/> itself (Swift <c>sentencePieces</c>).
    /// Pure, for tests.
    /// </summary>
    internal static List<CloudSegment> SentencePieces(CloudSegment segment, Span gap)
    {
        bool Straddles(double edge) => segment.Start < edge && edge < segment.End;
        var text = segment.Text;
        if (!(Straddles(gap.Start) || Straddles(gap.End)) || text.Length == 0) return [segment];
        double Time(int offset) => segment.Start + (segment.End - segment.Start) * offset / text.Length;
        var pieces = new List<CloudSegment>();
        var from = 0;
        var last = text.Length - 1;
        for (var index = 0; index <= last; index++)
        {
            var ends = index == last || "\u3002\uFF1F\uFF01".Contains(text[index])
                || (".?!".Contains(text[index]) && char.IsWhiteSpace(text[index + 1]));
            if (!ends) continue;
            var start = from;
            while (start <= index && char.IsWhiteSpace(text[start])) start++;
            if (start <= index)
            {
                pieces.Add(segment with
                {
                    Start = Time(start), End = Time(index + 1), Text = text[start..(index + 1)].Trim(),
                });
            }
            from = index + 1;
        }
        return pieces.Count == 0 ? [segment] : pieces;
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

    /// <summary>The session audio's duration; null when it is missing or unreadable.</summary>
    private double? AudioDuration(TranscriptionJob job, RecordingSession session)
    {
        if (ConversationFiles.AudioPath(job.Folder, session.AudioFileName) is not { } audio) return null;
        try
        {
            return MonoAudio.Duration(audio, decoder);
        }
        catch (TranscriptionException)
        {
            return null;
        }
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
    /// The local models a recording transcriber can run, in catalog order, for the cloud live-transcript picker (Swift
    /// <c>liveEntries</c>): the usable Whisper and Parakeet entries (<see cref="TranscribesChunks"/>; no Apple Speech on
    /// Windows). <paramref name="isUsable"/> includes "its runtime is registered". Any language: the picked id still
    /// goes through the incremental-entry check at Start.
    /// </summary>
    public static IReadOnlyList<ModelEntry> LiveEntries(Func<ModelEntry, bool> isUsable) =>
        [.. ModelCatalog.Asr.Where(e => isUsable(e) && TranscribesChunks(e))];

    /// <summary>
    /// Extracts each channel (skipping silent ones), runs <paramref name="asr"/> on it, diarizes the system channel
    /// unless <paramref name="asr"/> returned speaker labels, merges and writes <c>transcript.json</c> (keeping the one
    /// it replaces). Diarization: <paramref name="cloudDiarizer"/> first (it never takes the gate); the local diarizer
    /// when there is none, or after it failed or labeled no speaker (<see cref="Transcript.CloudDiarizationFailedNote"/>).
    /// <paramref name="gateDiarization"/>: the caller does not hold the <see cref="ModelLoadGate"/> (cloud ASR, or a
    /// prior without gaps), so the local diarizer takes it. With <paramref name="prior"/>, each channel is its segments
    /// plus <paramref name="asr"/> of its gaps: a channel is extracted only when it has gaps, or for the system
    /// channel's diarization.
    /// </summary>
    private async Task<string> ProcessAsync(
        TranscriptionJob job, RecordingSession session, (ChannelSource? Mic, ChannelSource? System) sources,
        string engineID, string? modelID, string? engineFallback, ISpeakerDiarizer? localDiarizer, CloudEngine? cloudDiarizer,
        bool gateDiarization, PriorTranscription? prior, ChannelAsr asr, CancellationToken cancellationToken)
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
                        path, micSource, true, f => Emit(job, JobStep.TranscribingMic, 0.05 + 0.35 * f), cancellationToken).ConfigureAwait(false));
                    TryDelete(path);
                }
            }

            var system = new List<RawSegment>(prior?.System ?? []);
            var turns = new List<DiarizationTurn>();
            var notes = new List<string>();
            var fillsSystem = prior is null || prior.SystemGaps.Count > 0;
            var diarizes = system.Count > 0 && (localDiarizer is not null || cloudDiarizer is not null);
            if (sources.System is { } systemSource && (fillsSystem || diarizes))
            {
                Emit(job, JobStep.Extracting, 0.4);
                if (await ExtractAsync(systemSource, Path.Combine(work, "system.caf"), cancellationToken).ConfigureAwait(false) is { } path)
                {
                    if (fillsSystem)
                    {
                        Emit(job, JobStep.TranscribingSystem, 0.45);
                        system.AddRange(await asr(
                            path, systemSource, false, f => Emit(job, JobStep.TranscribingSystem, 0.45 + 0.35 * f), cancellationToken).ConfigureAwait(false));
                    }
                    var unlabeled = system.Count > 0 && !system.Any(s => s.Speaker is not null);
                    if (unlabeled && cloudDiarizer is not null)
                    {
                        Emit(job, JobStep.Diarizing, 0.8);
                        try
                        {
                            turns.AddRange(await CloudTurnsAsync(
                                cloudDiarizer, systemSource, CloudLanguage(LanguageRange.Ranges(session.LanguageMarkers)),
                                cancellationToken).ConfigureAwait(false));
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            // The type only: a provider message can carry the request or response body.
                            Debug.WriteLine($"EchoDiar: cloud diarization ({cloudDiarizer.Name}) failed: {e.GetType().Name}");
                        }
                        if (turns.Count == 0) notes.Add(Transcript.CloudDiarizationFailedNote);
                    }
                    if (unlabeled && turns.Count == 0 && localDiarizer is not null)
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

    /// <summary>
    /// One channel through a cloud API, from its source (the compressed audio, not the 16 kHz extract); errors other
    /// than a cancellation become <see cref="TranscriptionErrorKind.Cloud"/> with a short, key-free reason. A
    /// streaming-only model fails before any upload (the file endpoints refuse it).
    /// </summary>
    private async Task<IReadOnlyList<RawSegment>> CloudSegmentsAsync(
        CloudEngine cloud, ChannelSource source, string? language, bool diarize, CancellationToken cancellationToken)
    {
        if (CloudTranscription.IsStreamingOnly(cloud.Model))
        {
            throw new TranscriptionException(
                TranscriptionErrorKind.Cloud, $"{cloud.Name}: {cloud.Model} only streams live audio; pick another model in Settings");
        }
        List<CloudSegment> segments;
        try
        {
            segments = await CloudTranscription.TranscribeAsync(
                source.Path, source.Channel, cloud.Transcriber, Exporter, language, diarize, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // Not kept as the inner exception: a provider message can carry the request or response body.
            throw new TranscriptionException(TranscriptionErrorKind.Cloud, $"{cloud.Name}: {CloudTranscription.FailureReason(e)}");
        }
        return [.. segments.Select(s => new RawSegment(s.Start, s.End, s.Text, s.Language, diarize ? s.Speaker : null))];
    }

    /// <summary>The system channel's speaker turns from a cloud diarizer, from its source; the text it returns is dropped.</summary>
    private async Task<IReadOnlyList<DiarizationTurn>> CloudTurnsAsync(
        CloudEngine cloud, ChannelSource source, string? language, CancellationToken cancellationToken)
    {
        var segments = await CloudTranscription.TranscribeAsync(
            source.Path, source.Channel, cloud.Transcriber, Exporter, language, diarize: true, cancellationToken).ConfigureAwait(false);
        return CloudTranscription.Turns(segments);
    }

    private IAudioExporter Exporter =>
        exporter ?? throw new InvalidOperationException("No audio exporter: cloud transcription is not available.");

    /// <summary>
    /// The language sent to a cloud API for the whole recording: the single language of <paramref name="ranges"/>,
    /// null (detect) when it is Auto or the recording switches languages.
    /// </summary>
    public static string? CloudLanguage(IReadOnlyList<LanguageRange> ranges)
    {
        var languages = ranges.Select(r => r.Language).Distinct().ToList();
        return languages.Count == 1 ? languages[0] : null;
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

/// <summary>A cloud transcription engine for one job, resolved by the app from its settings and Credential Manager (Swift <c>CloudEngine</c>).</summary>
/// <param name="EngineID"><c>cloud:&lt;provider id&gt;</c>, written as the transcript's <c>engineID</c>.</param>
/// <param name="Name">The provider name, for error reasons ("Deepgram: invalid API key").</param>
/// <param name="Model">Written as the transcript's <c>modelID</c>; empty when the provider picks it.</param>
public sealed record CloudEngine(string EngineID, string Name, string Model, ICloudTranscriber Transcriber);
