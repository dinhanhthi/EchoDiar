using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;

namespace EchoDiar.Transcribe;

/// <summary>
/// Batch transcription of recording folders, one job at a time (Swift <c>TranscriptionPipeline</c>): extract each
/// channel to 16 kHz mono, skip silent channels, ASR mic, ASR system, diarize system, merge, write
/// <c>transcript.json</c>. Models are loaded inside each step and unloaded before the next, while the job holds the
/// <see cref="ModelLoadGate"/> (it waits while a recording transcriber holds it).
/// </summary>
/// <remarks>
/// Not ported yet: cloud engines and diarizers (Phase 25), prior reuse (Phase 16), and the macOS thermal / Low Power
/// Mode deferral (no Windows equivalent yet).
/// </remarks>
/// <param name="engines">Batch engine factories by catalog runtime.</param>
/// <param name="diarizer">The local diarizer factory; null = none.</param>
/// <param name="isInstalled">Whether a catalog entry is installed (<see cref="ModelStore.IsInstalled"/>).</param>
/// <param name="modelFolder">An entry's <c>Models/&lt;id&gt;/</c> folder (<see cref="ModelStore.Folder"/>).</param>
/// <param name="speakerCountHint">Speakers expected on the system channel when the job sets none.</param>
/// <param name="decoder">Decodes a non-CAF file's channel to 16 kHz mono (Phase 14); null = CAF only.</param>
public sealed class TranscriptionPipeline(
    IReadOnlyDictionary<ModelRuntime, TranscriptionPipeline.EngineFactory> engines,
    TranscriptionPipeline.DiarizerFactory? diarizer,
    Func<ModelEntry, bool> isInstalled,
    Func<ModelEntry, string> modelFolder,
    ModelLoadGate gate,
    int? speakerCountHint = null,
    Func<string, int, float[]>? decoder = null)
{
    public delegate ITranscriptionEngine EngineFactory(ModelEntry entry, string folder);

    public delegate ISpeakerDiarizer DiarizerFactory(ModelEntry entry, string folder, int? speakerCountHint);

    private sealed record ChannelSource(string Path, int Channel);

    private readonly SemaphoreSlim _queue = new(1, 1);

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
                return ProcessAsync(job, session, sources, engine, ranges, entry.Id, ModelID(entry), engineFallback, localDiarizer, token);
            }, cancellationToken).ConfigureAwait(false);

            UpdateConversation(job.Folder, session, c =>
            {
                c.Status = ConversationStatus.Transcribed;
                c.HasTranscript = true;
                c.SizeBytes = LibraryStore.SizeOnDisk(job.Folder);
            });
            Emit(job, JobStep.Finished, 1);
            return path;
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

    /// <summary>
    /// Extracts each channel (skipping silent ones), transcribes it, diarizes the system channel unless the engine
    /// returned speaker labels, merges and writes <c>transcript.json</c> (keeping the one it replaces).
    /// </summary>
    private async Task<string> ProcessAsync(
        TranscriptionJob job, RecordingSession session, (ChannelSource? Mic, ChannelSource? System) sources,
        ITranscriptionEngine engine, IReadOnlyList<LanguageRange> ranges, string engineID, string? modelID,
        string? engineFallback, ISpeakerDiarizer? localDiarizer, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), "EchoDiar-transcribe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var mic = new List<RawSegment>();
            if (sources.Mic is { } micSource)
            {
                Emit(job, JobStep.Extracting, 0);
                if (await ExtractAsync(micSource, Path.Combine(work, "mic.caf"), cancellationToken).ConfigureAwait(false) is { } path)
                {
                    Emit(job, JobStep.TranscribingMic, 0.05);
                    mic.AddRange(await engine.TranscribeAsync(
                        path, ranges, f => Emit(job, JobStep.TranscribingMic, 0.05 + 0.35 * f), cancellationToken).ConfigureAwait(false));
                    TryDelete(path);
                }
            }

            var system = new List<RawSegment>();
            var turns = new List<DiarizationTurn>();
            if (sources.System is { } systemSource)
            {
                Emit(job, JobStep.Extracting, 0.4);
                if (await ExtractAsync(systemSource, Path.Combine(work, "system.caf"), cancellationToken).ConfigureAwait(false) is { } path)
                {
                    Emit(job, JobStep.TranscribingSystem, 0.45);
                    system.AddRange(await engine.TranscribeAsync(
                        path, ranges, f => Emit(job, JobStep.TranscribingSystem, 0.45 + 0.35 * f), cancellationToken).ConfigureAwait(false));
                    if (system.Count > 0 && !system.Any(s => s.Speaker is not null) && localDiarizer is not null)
                    {
                        Emit(job, JobStep.Diarizing, 0.8);
                        turns.AddRange(await localDiarizer.DiarizeAsync(
                            path, f => Emit(job, JobStep.Diarizing, 0.8 + 0.15 * f), cancellationToken).ConfigureAwait(false));
                    }
                }
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
        var peak = await Task.Run(() => MonoAudio.Extract(source.Channel, source.Path, path, decoder), cancellationToken).ConfigureAwait(false);
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
        if (MonoAudio.ChannelCount(audio) < 2) return (null, new(audio, 0));
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
    private static void UpdateConversation(string folder, RecordingSession? session, Action<Conversation> change)
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
                try { duration = MonoAudio.Duration(audio); }
                catch (TranscriptionException) { }
            }
            conversation = Conversation.FromSession(session, folder, duration);
        }
        change(conversation);
        try { conversation.Write(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
