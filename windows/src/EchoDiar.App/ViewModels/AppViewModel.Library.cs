using System.Diagnostics;
using System.Text.Json;
using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
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

/// <summary>
/// The library list's state and actions (macOS MainView.swift search, AppModel rename and the card's Reveal): the
/// search over the conversations, rename and Reveal in Explorer.
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

    /// <summary>The installed local speech engines, in catalog order, for the card's "Retry with another engine".</summary>
    public IReadOnlyList<ModelEntry> InstalledBatchEngines => ModelCatalog.Asr.Where(e => InstalledModelIds.Contains(e.Id)).ToList();

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
}
