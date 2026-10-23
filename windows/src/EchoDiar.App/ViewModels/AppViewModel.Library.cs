using System.Diagnostics;
using System.Text.Json;
using EchoDiar.App.Services;
using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.SystemServices;

namespace EchoDiar.App.ViewModels;

/// <summary>Swift <c>DayGroup</c>: the conversations of one local day, newest first.</summary>
/// <param name="Day">The day's start, in local time.</param>
public sealed record DayGroup(DateTime Day, IReadOnlyList<Conversation> Conversations)
{
    /// <summary>Groups by local day, newest day first, each group newest first.</summary>
    public static IReadOnlyList<DayGroup> Groups(IEnumerable<Conversation> conversations) =>
        conversations
            .GroupBy(c => c.CreatedAt.ToLocalTime().Date)
            .Select(g => new DayGroup(g.Key, g.OrderByDescending(c => c.CreatedAt).ToList()))
            .OrderByDescending(g => g.Day)
            .ToList();
}

/// <summary>Asks the user with a dialog (a title, a message and the confirming button's text, with Cancel); true when confirmed.</summary>
public delegate Task<bool> ConfirmDialog(string title, string message, string confirm);

/// <summary>
/// The library list's state and actions (macOS MainView.swift search, AppModel rename and the card's Reveal): the
/// search over the conversations, rename and Reveal in Explorer; and the library's upkeep (macOS AppModel auto-delete
/// and the Storage tab's deletions): the daily auto-delete run, its preview, and deleting by kind.
/// </summary>
public sealed partial class AppViewModel
{
    private string _searchTerm = "";

    /// <summary>
    /// Searchable text by conversation (speakers, transcript, summary, chat), read off the UI thread only while a term
    /// is searched; read again when the conversation's size or files change (a new transcript or summary) or its
    /// transcript is edited.
    /// </summary>
    private readonly Dictionary<Guid, SearchDocument> _searchDocuments = [];

    /// <summary>
    /// macOS <c>transcriptEdits</c>: transcript edits by conversation. A speaker renamed to a name of the same length
    /// leaves the size unchanged, so the count marks the document stale instead.
    /// </summary>
    private readonly Dictionary<Guid, int> _transcriptEdits = [];

    /// <summary>A read is under way; a change meanwhile reads again after it.</summary>
    private bool _searchLoading;
    private bool _searchReloadPending;

    /// <summary>What a document was read for: it is stale once one of these changes.</summary>
    private sealed record SearchDocument(long Size, bool HasTranscript, bool HasSummary, int Edits, ConversationSearch.Document Document)
    {
        public bool IsFresh(Conversation c, int edits) =>
            Size == c.SizeBytes && HasTranscript == c.HasTranscript && HasSummary == c.HasSummary && Edits == edits;
    }

    private int TranscriptEdits(Guid id) => _transcriptEdits.GetValueOrDefault(id);

    /// <summary>The toolbar's search, trimmed (the toolbar debounces typing); empty shows every conversation.</summary>
    public string SearchTerm
    {
        get => _searchTerm;
        set
        {
            if (!SetProperty(ref _searchTerm, value.Trim())) return;
            OnPropertyChanged(nameof(SearchResults));
            _ = LoadSearchDocumentsAsync();
        }
    }

    /// <summary>
    /// The conversations whose title, speakers, transcript, summary or chat contain <see cref="SearchTerm"/> (case and
    /// accent insensitive), newest first; all without a term. The title only until a document is read.
    /// </summary>
    public IReadOnlyList<Conversation> SearchResults
    {
        get
        {
            var term = SearchTerm;
            if (term.Length == 0) return Conversations;
            return Conversations.Where(c =>
            {
                // A stale document still serves until its new one replaces it, as on macOS.
                var document = _searchDocuments.TryGetValue(c.Id, out var cached)
                    // The current title: a rename does not change the size.
                    ? cached.Document with { Title = c.Title }
                    : new ConversationSearch.Document(c.Title, [], "", "", "");
                return document.Matches(term);
            }).ToList();
        }
    }

    /// <summary>
    /// Reads the documents not read yet or stale, while a term is searched; then the results change. Not on Settings,
    /// whose search filters the settings.
    /// </summary>
    private async Task LoadSearchDocumentsAsync()
    {
        if (SearchTerm.Length == 0 || _closing || Route.Kind == RouteKind.Settings) return;
        if (_searchLoading)
        {
            _searchReloadPending = true;
            return;
        }
        var listed = Conversations.Select(c => c.Id).ToHashSet();
        foreach (var id in _searchDocuments.Keys.Where(id => !listed.Contains(id)).ToList()) _searchDocuments.Remove(id);
        var stale = Conversations
            .Where(c => c.HasTranscript && !(_searchDocuments.TryGetValue(c.Id, out var cached) && cached.IsFresh(c, TranscriptEdits(c.Id))))
            // The edit count read now: an edit during the read leaves the document stale.
            .Select(c => (Conversation: c, Edits: TranscriptEdits(c.Id)))
            .ToList();
        if (stale.Count == 0) return;
        _searchLoading = true;
        List<(Guid Id, SearchDocument Document)>? loaded = null;
        try
        {
            loaded = await Task.Run(() => stale
                .Select(s => (s.Conversation.Id, Document: new SearchDocument(
                    s.Conversation.SizeBytes, s.Conversation.HasTranscript, s.Conversation.HasSummary, s.Edits,
                    ReadSearchDocument(s.Conversation))))
                .ToList());
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read the search documents: {e}");
        }
        finally
        {
            _searchLoading = false;
        }
        if (_closing) return;
        if (loaded is not null)
        {
            foreach (var (id, document) in loaded) _searchDocuments[id] = document;
            // Outside the try: a failure while the views update is not a read failure, and must not leave them half done.
            OnPropertyChanged(nameof(SearchResults));
        }
        if (!_searchReloadPending) return;
        _searchReloadPending = false;
        await LoadSearchDocumentsAsync();
    }

    /// <summary>Missing or unreadable transcript, summary or chat files leave their fields empty.</summary>
    private static ConversationSearch.Document ReadSearchDocument(Conversation conversation)
    {
        Transcript? transcript;
        try
        {
            transcript = Transcript.Read(conversation.Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            transcript = null;
        }
        // The open chat and the archived ones: archiving or resuming one changes no search result, deleting one does.
        var chat = new ChatLog
        {
            Messages = [.. ChatLog.Read(conversation.Folder).Messages, .. ChatArchive.Messages(conversation.Folder)],
        };
        return ConversationSearch.Build(conversation.Title, transcript, Conversation.ReadSummary(conversation.Folder), chat);
    }

    /// <summary>Sets a custom title (<c>customTitle: true</c> in conversation.json); a blank title is ignored.</summary>
    public async Task RenameConversationAsync(Guid id, string title)
    {
        try
        {
            await _services.Library.RenameAsync(id, title);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not rename conversation {id}: {e}");
        }
    }

    /// <summary>
    /// macOS <c>transcriptEdited</c>: re-lists the conversation after its <c>transcript.json</c> changed (a speaker
    /// renamed), so its size is fresh, and counts the edit, so its search document is read again even when the size
    /// did not change. The stale document still serves until the new one replaces it.
    /// </summary>
    public Task TranscriptEditedAsync(Guid id)
    {
        _transcriptEdits[id] = TranscriptEdits(id) + 1;
        return ReloadAsync(id);
    }

    /// <summary>Opens File Explorer with the conversation's folder selected.</summary>
    public static void RevealInExplorer(Conversation conversation)
    {
        try
        {
            ShellFiles.Reveal(conversation.Folder);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not reveal {conversation.Folder}: {e}");
        }
    }

    // Dialogs

    /// <summary>
    /// The window's dialogs (the title bar shows one at a time), for Settings pages, which have no window of their own:
    /// a confirmation, and the delete flow of the cards. Set while the title bar is attached.
    /// </summary>
    internal ConfirmDialog? Confirm { get; set; }

    /// <inheritdoc cref="Confirm"/>
    internal Func<IReadOnlyList<Conversation>, bool, Task<bool>>? DeleteFlow { get; set; }

    // Auto-delete

    private RetentionScheduler? _retention;

    /// <summary>The launch scan compressing leftover recordings, which auto-delete waits for (its folders are marked busy one at a time).</summary>
    private Task _launchScan = Task.CompletedTask;

    private bool _retentionRunning;

    /// <summary>
    /// macOS <c>busyConversationIDs</c>: a transcription queued, running or waiting for a model, or still between its
    /// recording's end (or the launch scan) and its last step (compressing). Recording and compressing conversations are
    /// not transcribed, so auto-delete never picks them. A transcription waiting for its cloud confirmation, and an AI
    /// job's conversation, too.
    /// </summary>
    private bool IsBusy(Guid id) =>
        _finishing.Contains(id) || TranscriptionProgress.ContainsKey(id) || PendingCloudRuns.Any(r => r.ConversationId == id)
        || AIJob?.ConversationId == id;

    /// <summary>
    /// Auto-delete on: schedules the daily run and runs it once now (launch, or when turned on). Off: stops it (macOS
    /// <c>syncRetention</c>).
    /// </summary>
    private void SyncRetention()
    {
        if (_closing) return;
        if (!Settings.AutoDeleteEnabled)
        {
            _retention?.Stop();
            return;
        }
        _retention ??= new RetentionScheduler(RunRetentionAsync);
        _retention.Start();
        _ = RunRetentionAsync();
    }

    /// <summary>
    /// Moves what auto-delete selects to the Recycle Bin, skipping busy conversations (asked right before each one); the
    /// open conversation removed whole goes back to the list (macOS <c>runRetention</c>). One run at a time; on the UI
    /// thread.
    /// </summary>
    private async Task RunRetentionAsync()
    {
        if (_retentionRunning) return;
        _retentionRunning = true;
        try
        {
            await _launchScan;
            // Read when it runs: the setting may have changed while the scan ran.
            if (_closing || !Settings.AutoDeleteEnabled) return;
            var done = await _services.Library.ApplyRetentionAsync(Settings, IsBusyBeforeDeletionAsync);
            if (_closing) return;
            if (done.Any(d => d.Mode == RetentionMode.WholeConversation && d.ConversationId == Route.ConversationId)) Route = Route.List;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: auto-delete failed: {e}");
        }
        finally
        {
            _retentionRunning = false;
        }
    }

    /// <summary>
    /// Auto-delete asks right before each deletion, from the store's thread: answered on the UI thread, which owns the
    /// busy state. A conversation that is not busy has its open player let go of the audio first. Busy once the window
    /// closes, so a run under way deletes nothing more.
    /// </summary>
    private Task<bool> IsBusyBeforeDeletionAsync(Guid id)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Ask()
        {
            var busy = true;
            try
            {
                busy = _closing || IsBusy(id);
                if (!busy) ReleasingAudio?.Invoke(id);
            }
            finally
            {
                answer.TrySetResult(busy);
            }
        }
        if (_closing) answer.TrySetResult(true);
        else if (_dispatcher.HasThreadAccess) Ask();
        else if (!_dispatcher.TryEnqueue(Ask)) answer.TrySetResult(true);
        return answer.Task;
    }

    /// <summary>
    /// What auto-delete would move to the Recycle Bin now with <paramref name="settings"/> (the Storage tab's preview),
    /// from the listed conversations that are not busy; off the UI thread, since audio only lists each candidate's folder.
    /// </summary>
    public Task<IReadOnlyList<RetentionPolicy.Deletion>> RetentionPreviewAsync(AppSettings settings)
    {
        var candidates = Conversations.Where(c => !IsBusy(c.Id)).ToList();
        return Task.Run(() => RetentionPolicy.Select(candidates, DateTimeOffset.UtcNow, settings));
    }

    // Deleting by kind (Settings › Storage)

    /// <summary>Moves the audio of these conversations to the Recycle Bin, skipping any that got a job meanwhile.</summary>
    public async Task DeleteAudioAsync(IReadOnlyList<Guid> ids)
    {
        foreach (var id in ids)
        {
            if (_closing) return;
            if (!IsBusy(id)) await DeleteAudioAsync(id);
        }
    }

    /// <summary>
    /// Moves the transcripts, summaries and chats of these conversations to the Recycle Bin (the whole conversation when
    /// its audio is gone, whose page then goes back to the list), skipping any that got a job meanwhile.
    /// </summary>
    public async Task DeleteTranscriptsAndAIAsync(IReadOnlyList<Guid> ids)
    {
        foreach (var id in ids)
        {
            if (_closing) return;
            if (IsBusy(id)) continue;
            if (Conversations.FirstOrDefault(c => c.Id == id) is { HasAudio: false } && Route.ConversationId == id) Route = Route.List;
            try
            {
                await _services.Library.DeleteTranscriptsAndAIAsync(id);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not delete the transcript of conversation {id}: {e}");
            }
        }
    }

    /// <summary>
    /// A model may be loaded: a transcription, the recording's transcriber, an AI job of an in-app model or an in-app
    /// embedder runs (a cloud, CLI or local-host job holds no model file). Model files are not deleted meanwhile: llama.cpp
    /// maps a GGUF file while loaded, which Windows then refuses to move or delete.
    /// </summary>
    public bool ModelsInUse =>
        TranscriptionProgress.Keys.Any(IsTranscribing) || RecordingCaptions is not null || AIJob is { UsesLocalModel: true }
        || IsEmbedderLoaded;

    /// <summary>The downloaded models (not one still downloading), which "Delete all" removes.</summary>
    public IReadOnlyList<ModelEntry> DeletableModels =>
        ModelCatalog.All.Where(e => e.Repo is not null && InstalledModelIds.Contains(e.Id) && !Downloads.ContainsKey(e.Id)).ToList();

    /// <summary>
    /// Removes every downloaded model (macOS <c>deleteAllModels</c>). A default model removed is no longer the default,
    /// or every new recording would stop at "Needs model".
    /// </summary>
    public async Task DeleteAllModelsAsync()
    {
        foreach (var entry in DeletableModels)
        {
            await DeleteModelFilesAsync(entry);
            if (_closing) return;
        }
        await RefreshInstalledModelsAsync();
    }

    /// <summary>
    /// Removes one downloaded model (macOS <c>deleteModel</c>, Settings › Transcription's Delete); a model still
    /// downloading is left alone. A default model removed is no longer the default.
    /// </summary>
    public async Task DeleteModelAsync(string id)
    {
        if (ModelCatalog.Entry(id) is not { Repo: not null } entry || Downloads.ContainsKey(id)) return;
        await DeleteModelFilesAsync(entry);
        if (_closing) return;
        await RefreshInstalledModelsAsync();
    }

    /// <summary>
    /// Deletes the model's folder off the UI thread; a failure is shown on its row. Its default is cleared once the
    /// model is gone (macOS), or every new recording would stop at "Needs model"; a failed delete that left it installed
    /// keeps it.
    /// </summary>
    private async Task DeleteModelFilesAsync(ModelEntry entry)
    {
        try
        {
            await Task.Run(() => _services.Models.Delete(entry));
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not delete model {entry.Id}: {e}");
            DownloadErrors = With(DownloadErrors, entry.Id, e.Message);
            try
            {
                if (await Task.Run(() => _services.Models.IsInstalled(entry))) return;
            }
            catch (IOException)
            {
                // The folder moved during the check (a download replacing it): it is still there, keep the default.
                return;
            }
        }
        if (_closing) return;
        if (Settings.DefaultASRModelID == entry.Id) SaveSettings(Settings with { DefaultASRModelID = null });
        if (Settings.DefaultDiarizerModelID == entry.Id) SaveSettings(Settings with { DefaultDiarizerModelID = null });
    }

    /// <summary>The installed models read again (macOS <c>refreshInstalledModels</c> when the Transcription section shows).</summary>
    internal Task RefreshModelsAsync() => RefreshInstalledModelsAsync();

    private ModelsViewModel? _modelsModel;

    /// <summary>What Settings › Transcription binds to; one for the app's lifetime.</summary>
    public ModelsViewModel ModelsModel =>
        _modelsModel ??= new ModelsViewModel(this, _services.Models, _services.Engines.Keys.ToHashSet());
}
