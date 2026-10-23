using System.ComponentModel;
using EchoDiar.Core.Model;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// The chat across the library (macOS GlobalChatView.swift state): its open chat with its title and archived chats in
/// <see cref="AppViewModel.GlobalChatFolder"/>, read off the UI thread when a job ends and when the chat history changes;
/// the library question running; the index's progress; and the actions, which go through <see cref="AppViewModel"/>. A
/// question that just ended stays shown until the files it wrote are read, so the answer does not flash away. On the UI
/// thread.
/// </summary>
public sealed class LibraryChatViewModel(AppViewModel model)
{
    private (int JobsEnded, int Changes)? _loadedFor;
    private int _generation;
    private bool _attached;

    /// <summary>The library question as last seen, and the one that just ended until its files are read.</summary>
    private AIJob? _job;
    private AIJob? _endedJob;

    /// <summary>Raised on the UI thread when what the chat shows changed (files read, a job update, a provider, an error).</summary>
    public event Action? Changed;

    public AppViewModel Model => model;

    public ProvidersViewModel Providers => model.ProvidersModel;

    public IReadOnlyList<ChatLog.Message> Chat { get; private set; } = [];

    /// <summary>The open chat's title, when it was resumed from the history with one.</summary>
    public string? ChatTitle { get; private set; }

    /// <summary>The earlier chats, newest first.</summary>
    public IReadOnlyList<ChatArchive.Entry> Archives { get; private set; } = [];

    public bool RagEnabled => model.Settings.RagEnabled;

    public RAGStatus Status => model.RAGStatus;

    /// <summary>The library question running now (macOS <c>libraryJob</c>).</summary>
    public AIJob? RunningJob => model.AIJob is { ConversationId: null, Kind: AIJobKind.Question } job ? job : null;

    /// <summary>The question being answered, shown while it runs and until it is read back.</summary>
    public AIJob? ShownJob => RunningJob ?? _endedJob;

    /// <summary>A job of a conversation: it blocks asking until it ends (macOS <c>busyElsewhere</c>).</summary>
    public AIJob? BusyElsewhere => model.AIJob is { ConversationId: not null } job ? job : null;

    /// <summary>As <see cref="AppViewModel.AskLibrary"/>'s guard: one AI job at a time (macOS <c>canAsk</c>).</summary>
    public bool CanAsk => model.AIJob is null && !model.ChatChanging && RagEnabled && Providers.CurrentAIProvider is not null;

    /// <summary>Why the last library question or library chat change failed; null when none did.</summary>
    public string? Error => model.AIError is { ConversationId: null } error ? error.Message : null;

    /// <summary>The current provider's picked model ("" = a CLI's own default).</summary>
    public string? CurrentModel => Providers.CurrentAIProvider is { } provider ? Providers.AIModelFor(provider) : null;

    /// <summary>The conversation of a source, while it is in the library.</summary>
    public Conversation? Conversation(Guid id) => model.Conversations.FirstOrDefault(c => c.Id == id);

    // Lifetime

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        model.PropertyChanged += OnModelChanged;
        Providers.Changed += OnProvidersChanged;
        _job = RunningJob;
        Refresh();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        model.PropertyChanged -= OnModelChanged;
        Providers.Changed -= OnProvidersChanged;
        _generation++;
        _loadedFor = null;
    }

    private void OnProvidersChanged() => Changed?.Invoke();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.AIJob):
                var job = RunningJob;
                // The library question ended: shown until LoadAsync reads what it saved.
                if (_job is { } ended && job is null && ended.Text.Length > 0) _endedJob = ended;
                _job = job;
                Changed?.Invoke();
                break;
            case nameof(AppViewModel.AIJobsEnded):
            case nameof(AppViewModel.ChatHistoryChanges):
                Refresh();
                break;
            case nameof(AppViewModel.Conversations):
            case nameof(AppViewModel.AIError):
            case nameof(AppViewModel.ChatChanging):
            case nameof(AppViewModel.Settings):
            case nameof(AppViewModel.RAGStatus):
                Changed?.Invoke();
                break;
        }
    }

    /// <summary>Reads the chat again when an answer was saved or the history changed (macOS <c>LoadKey</c>).</summary>
    private void Refresh()
    {
        var key = (model.AIJobsEnded, model.ChatHistoryChanges);
        if (_loadedFor != key)
        {
            _loadedFor = key;
            _ = LoadAsync();
        }
        Changed?.Invoke();
    }

    /// <summary>The open chat with its title and the earlier chats, off the UI thread (macOS <c>loadChat</c>).</summary>
    private async Task LoadAsync()
    {
        var generation = ++_generation;
        var folder = model.GlobalChatFolder;
        var (log, entries) = await Task.Run(() => (ChatLog.Read(folder), ChatArchive.Entries(folder)));
        if (generation != _generation || !_attached || model.IsClosing) return;
        Chat = log.Messages;
        ChatTitle = log.Title;
        Archives = entries;
        _endedJob = null;
        Changed?.Invoke();
    }

    // Actions

    /// <summary>Asks <paramref name="question"/> across the library; false when it cannot run now (macOS <c>ask</c>).</summary>
    public bool Ask(string question)
    {
        var text = question.Trim();
        if (!CanAsk || text.Length == 0) return false;
        model.AskLibrary(text);
        return true;
    }

    public void Stop() => model.StopAIJob();

    public void StartFreshChat() => model.StartFreshLibraryChat();

    public void Resume(Guid entryId) => model.ResumeLibraryChat(entryId);

    public void DeleteChat(Guid entryId) => model.DeleteLibraryChat(entryId);

    public void ClearChats() => model.ClearLibraryChats();

    /// <summary>A source or a citation: its conversation at that time, or its summary (macOS <c>openReference</c>).</summary>
    public void OpenReference(Guid id, double? seconds) => model.OpenReference(id, seconds);
}
