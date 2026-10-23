using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.Views;
using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using EchoDiar.Transcribe;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// The recording state the card, the recording page and the toolbar share (macOS AppModel: selectedTarget,
/// sourceApp, recordingError, micRoutesLost, recordingLanguage): what Record records, the recorded app, the
/// "Recording problem" answer, the lost-mic route change and the recording language; and the steps after a recording
/// (macOS finishRecording): transcription and compression to m4a.
/// </summary>
public sealed partial class AppViewModel
{
    private AudioProcessGroup? _selectedTarget;
    private string? _recordingSourceName;

    /// <summary>The language picked since the last recording ended; null = the default language.</summary>
    private string? _languageOverride;

    private IReadOnlyList<string>? _supportedLanguages;

    /// <summary>The last language marker and settings writes; each next one runs after it.</summary>
    private Task _languageWrite = Task.CompletedTask;
    private Task _settingsWrite = Task.CompletedTask;

    /// <summary>
    /// Conversations in <see cref="CompressAndTranscribeAsync"/> until its last step returns, or compressed by
    /// <see cref="CompressLeftoversAsync"/>: between the transcription and the compression no status guards the audio,
    /// which the compressor rewrites and deletes (macOS finishingIDs).
    /// </summary>
    private readonly HashSet<Guid> _finishing = [];

    /// <summary>Cancelled on quit: queued compressions never start.</summary>
    private readonly CancellationTokenSource _compressionCancel = new();

    /// <summary>What Record records: null for all system audio, else the app picked in the Record menu.</summary>
    public AudioProcessGroup? SelectedTarget
    {
        get => _selectedTarget;
        set => SetProperty(ref _selectedTarget, value);
    }

    /// <summary>The recorded app's name from the start until the recording ends; null for all system audio.</summary>
    public string? RecordingSourceName
    {
        get => _recordingSourceName;
        private set => SetProperty(ref _recordingSourceName, value);
    }

    /// <summary>The window closed: views stop their frame callbacks, since its controls fail every call.</summary>
    internal bool IsClosing => _closing;

    /// <summary>The "Recording problem" dialog was closed on <paramref name="shown"/>; a newer error stays.</summary>
    public void DismissRecordingError(string shown)
    {
        if (RecordingError == shown) RecordingError = null;
    }

    /// <summary>The language the next recording starts with, or the current one while recording.</summary>
    public string RecordingLanguage => _languageOverride ?? Settings.DefaultLanguage;

    /// <summary>Before recording: the next session's language. While recording: a language marker at the current time.</summary>
    public void SetRecordingLanguage(string language)
    {
        if (language == RecordingLanguage) return;
        _languageOverride = language;
        OnPropertyChanged(nameof(RecordingLanguage));
        if (IsRecording)
        {
            // The live transcriber's next chunks.
            _liveLanguage = language;
            // In pick order, off the UI thread: the engine writes session.json under its lock.
            _languageWrite = _languageWrite.ContinueWith(_ =>
            {
                try
                {
                    _services.Engine.SetLanguage(language);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"EchoDiar: could not add the language marker: {e}");
                }
            }, TaskScheduler.Default);
        }
        RememberLanguage(language);
    }

    /// <summary>Codes the catalog's default engine accepts, sorted by name.</summary>
    public IReadOnlyList<string> SupportedLanguages() =>
        _supportedLanguages ??= [.. TranscriptionPipeline.SupportedLanguages(ModelCatalog.DefaultASR)
            .OrderBy(LanguageOptions.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Most recent first; Auto and the pinned languages are always listed, so they are not kept.</summary>
    private void RememberLanguage(string language)
    {
        if (language == LanguageOptions.Auto || LanguageOptions.Pinned.Contains(language)) return;
        var settings = Settings with
        {
            RecentLanguages = [.. Settings.RecentLanguages.Where(l => l != language).Prepend(language).Take(LanguageOptions.MaxRecent)],
        };
        SaveSettings(settings);
    }

    /// <summary>How transcripts play (macOS Settings › Storage "Playback"): both voices in both ears, or as recorded.</summary>
    public void SetPlaybackChannels(PlaybackChannels channels)
    {
        if (channels == Settings.PlaybackChannels) return;
        SaveSettings(Settings with { PlaybackChannels = channels });
    }

    /// <summary>Applies <paramref name="settings"/> now and saves them off the UI thread, in call order.</summary>
    private void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        QueueSettingsWrite();
    }

    /// <summary>The engine reads the setting when the mic is lost, and reports it here through the UI thread.</summary>
    private void FollowRouteChanges()
    {
        _services.Engine.RouteBehavior = () => _settings.RouteChangeBehavior;
        _services.Engine.MicRouteLost += behavior => Post(() => OnMicRouteLost(behavior));
    }

    /// <summary>
    /// Ask: the end monitor's input-route-lost trigger (the pill asks, with its countdown); Stop: the stop of the end
    /// prompt; Continue: nothing (macOS micRoutesLost).
    /// </summary>
    private void OnMicRouteLost(RouteChangeBehavior behavior)
    {
        StartupTrace.Write($"MicRouteLost on the UI thread: {behavior}, recording {IsRecording}");
        if (!IsRecording) return;
        switch (behavior)
        {
            case RouteChangeBehavior.Ask:
                RecordEnd(new MeetingEndMonitor.Input.InputRouteLost());
                break;
            case RouteChangeBehavior.Stop:
                StopFromEndPrompt();
                break;
        }
    }

    // After a recording

    /// <summary>
    /// The post-stop sequence for one folder (macOS AppModel <c>finishRecording</c>). A recording auto-transcribed now
    /// (usable, not transcribed yet, its CAF has frames, no cloud confirmation pending) is transcribed from the CAF first; then its audio goes to the
    /// Recycle Bin (success with "Delete audio after transcription") or is compressed. Otherwise it is compressed first,
    /// then transcribed when "Transcribe automatically" is on. <paramref name="prior"/> (what the live transcriber
    /// transcribed, once it drained and unloaded its model) goes to the job either way.
    /// </summary>
    private async Task CompressAndTranscribeAsync(string folder, RecordingSession? session, Task<PriorTranscription?>? prior = null)
    {
        if (_closing) return;
        var id = session?.Id;
        if (id is { } added) SetFinishing(added, true);
        try
        {
            var transcribed = prior is null ? null : await prior;
            if (_closing) return;
            var autoTranscribe = Settings.AutoTranscribe;
            var needsNoModel = NeedsNoModel(transcribed);
            // Read here, not when the job starts: a deferred job would hold the CAF uncompressed until energy saver is off,
            // and a cloud confirmation (macOS consentPending) until it is answered.
            var deferred = _services.Deferral.ShouldDefer && !needsNoModel;
            var consentPending = AutoEngine() == AutoEngineKind.ConsentPending;
            if (session is not null && !deferred && !consentPending && await Task.Run(() => TranscribesFirst(folder, session, autoTranscribe))
                && SubmitTranscription(folder, session, deleteAudioAfter: false, prior: transcribed, processNow: needsNoModel) is { } job)
            {
                var succeeded = await job;
                if (succeeded && !_closing && Settings.DeleteAudioAfterTranscription) await DeleteAudioAsync(session.Id);
                else await CompressAsync(folder);
                return;
            }
            if (await CompressAsync(folder) is { } result) await TranscribeAfterAsync(result, transcribed);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not compress or transcribe {folder}: {e}");
        }
        finally
        {
            if (id is { } removed) SetFinishing(removed, false);
        }
    }

    /// <summary>
    /// Launch: folders whose audio is still the CAF of a finished or recovered session (a recording stopped at quit) and
    /// folders left compressing by an interrupted job are compressed one at a time, then transcribed as after a stop
    /// (macOS AppModel <c>start</c>). A folder being transcribed is skipped (its CAF stays for the next launch); the
    /// others are marked finishing on the UI thread, so Transcribe stays off while the compressor rewrites the audio.
    /// </summary>
    private async Task CompressLeftoversAsync()
    {
        try
        {
            var folders = await Task.Run(() => UncompressedFolders(_services.Paths.Conversations));
            foreach (var (folder, id) in folders)
            {
                if (_closing) return;
                if (IsTranscribing(id)) continue;
                SetFinishing(id, true);
                try
                {
                    if (await CompressAsync(folder) is { } result) await TranscribeAfterAsync(result);
                }
                finally
                {
                    SetFinishing(id, false);
                }
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not compress the leftover recordings: {e}");
        }
    }

    /// <summary>
    /// One compression with the current bitrate and keep-CAF settings, queued behind the others; the library is
    /// refreshed once the folder is marked compressing (the card shows it) and when the job ends. Null when the app quit
    /// before it started.
    /// </summary>
    private async Task<CompressionResult?> CompressAsync(string folder)
    {
        if (_closing) return null;
        CompressionResult result;
        try
        {
            result = await _services.Compressor.CompressAsync(
                folder, Settings.CompressionBitrateKbps, Settings.KeepUncompressedAudio, () => _ = RefreshLibraryAsync(),
                _compressionCancel.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        if (result.Failure is { } failure) Debug.WriteLine($"EchoDiar: compression failed for {folder}: {failure} {result.Detail}");
        await RefreshLibraryAsync();
        return _closing ? null : result;
    }

    /// <summary>
    /// Queues the folder once compression is done, when "Transcribe automatically" is on (a failed compression leaves
    /// the CAF, which is used instead). Skipped when the folder has no usable audio or session, already has a
    /// transcript, or its last transcription failed (macOS <c>transcribe(after:)</c>). <paramref name="prior"/> goes to the job.
    /// </summary>
    private async Task TranscribeAfterAsync(CompressionResult result, PriorTranscription? prior = null)
    {
        if (!Settings.AutoTranscribe || _closing) return;
        if (result.Failure is AudioCompressionFailure.SessionUnreadable or AudioCompressionFailure.StillRecording
            or AudioCompressionFailure.UnsafeAudioFileName or AudioCompressionFailure.SourceUnreadable)
        {
            return;
        }
        var (session, conversation) = await Task.Run(() => (ReadSession(result.Folder), ReadConversation(result.Folder)));
        if (session is null || conversation?.HasTranscript == true || conversation?.Status == ConversationStatus.TranscriptionFailed) return;
        _ = SubmitTranscription(result.Folder, session, prior: prior);
    }

    /// <summary>
    /// The job writes <paramref name="prior"/> without loading a model: no gaps to fill and speakers not told apart. It
    /// then runs now even while energy saver is on, and is transcribed before compression (macOS <c>needsNoModel</c>);
    /// should the pipeline find the prior unusable, its full batch runs now too.
    /// </summary>
    private bool NeedsNoModel(PriorTranscription? prior) =>
        prior is not null && prior.MicGaps.Count == 0 && prior.SystemGaps.Count == 0 && !Settings.Diarization;

    /// <summary>
    /// Whether the recording is transcribed from its CAF before compression: auto-transcribe is on, the recording is
    /// usable and not transcribed (or failed) yet, and its CAF has frames (the caller also checks that energy saver
    /// does not defer it). Reads files: off the UI thread.
    /// </summary>
    private static bool TranscribesFirst(string folder, RecordingSession session, bool autoTranscribe)
    {
        if (!autoTranscribe || session.Probe || session.State == SessionState.Failed) return false;
        var conversation = ReadConversation(folder);
        if (conversation?.HasTranscript == true || conversation?.Status == ConversationStatus.TranscriptionFailed) return false;
        if (session.AudioFileName != AudioCompressor.CafFileName
            || ConversationFiles.AudioPath(folder, session.AudioFileName) is not { } caf)
        {
            return false;
        }
        try
        {
            using var reader = new CafFile.Reader(caf);
            return reader.Frames > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Folders of finished or recovered sessions whose audio is still the CAF, and folders left compressing. Hidden
    /// entries and links are skipped (macOS <c>uncompressedFolders</c>). Each with its session id.
    /// </summary>
    private static List<(string Folder, Guid Id)> UncompressedFolders(string root)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        List<string> folders;
        try
        {
            folders = [.. Directory.EnumerateDirectories(root, "*", options)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var leftovers = new List<(string Folder, Guid Id)>();
        foreach (var folder in folders)
        {
            if (ReadSession(folder) is not { Probe: false, State: SessionState.Stopped or SessionState.Recovered } session) continue;
            if (ReadConversation(folder)?.Status == ConversationStatus.Compressing
                || (session.AudioFileName == AudioCompressor.CafFileName
                    && ConversationFiles.AudioPath(folder, session.AudioFileName) is { } caf && File.Exists(caf)))
            {
                leftovers.Add((folder, session.Id));
            }
        }
        return leftovers;
    }

    private static Conversation? ReadConversation(string folder)
    {
        try
        {
            return Conversation.Read(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The screens that read <see cref="IsTranscribing"/> follow <see cref="TranscriptionProgress"/>, so it is raised for them.</summary>
    private void SetFinishing(Guid id, bool finishing)
    {
        if (finishing ? _finishing.Add(id) : _finishing.Remove(id)) OnPropertyChanged(nameof(TranscriptionProgress));
    }

    private async Task RefreshLibraryAsync()
    {
        try
        {
            await _services.Library.RefreshAsync();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not refresh the library: {e}");
        }
    }
}
