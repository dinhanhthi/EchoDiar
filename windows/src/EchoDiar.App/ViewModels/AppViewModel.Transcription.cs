using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.Views;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Transcribe;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// Transcription while recording (macOS AppModel <c>startRecordingTranscriber</c>, <c>attach</c>,
/// <c>recordingCaptions</c>): one <see cref="RecordingTranscriber"/> per recording, fed by the writer's live sink, whose
/// captions the recording page shows; at Stop it hands what it transcribed to the recording's transcription job.
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
    /// Before the engine starts (it reads the sink once at start; the model loads meanwhile): a transcriber for a
    /// recording in <paramref name="language"/>, connected to the engine's live sink, when auto-transcribe and
    /// "Transcribe while recording" are on and the engine the Stop job resolves to is an installed Whisper or Parakeet
    /// entry. Otherwise the sink is cleared, and a previous recording's captions are not this one's.
    /// </summary>
    private async Task AttachRecordingTranscriberAsync(string language)
    {
        var settings = Settings;
        RecordingTranscriber? transcriber = null;
        if (settings.AutoTranscribe && settings.TranscribeWhileRecording)
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
    /// the Stop job picks, loaded as a session; idle release after "Unload models after" minutes.
    /// </summary>
    private RecordingTranscriber? CreateRecordingTranscriber(string language, AppSettings settings)
    {
        if (IncrementalEntry(PreferredEngine(language), language) is not { } entry
            || _services.Engines[entry.Runtime](entry, _services.Models.Folder(entry)) is not ISessionTranscriptionEngine engine)
        {
            return null;
        }
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
}
