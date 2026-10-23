using System.ComponentModel;
using System.Diagnostics;
using EchoDiar.AI;
using EchoDiar.Core.Model;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// The Summary and Chat tabs of one conversation (macOS AIPanelView.swift state): its transcript, kept summaries (the
/// version shown), open chat with its title and archived chats, read off the UI thread when the conversation gets a
/// transcript, when an AI job ends and when the chat history changes; the job of this conversation; and the actions,
/// which go through <see cref="AppViewModel"/>. A job that just ended stays shown until the files it wrote are read, so
/// the answer does not flash back to the earlier state. On the UI thread.
/// </summary>
public sealed class AIPanelViewModel
{
    private readonly AppViewModel _model;

    private (bool Listed, bool HasTranscript, int JobsEnded)? _loadedFor;
    private (bool Listed, int Changes)? _chatLoadedFor;
    private int _loadGeneration;
    private int _chatGeneration;
    private bool _attached;

    /// <summary>This conversation's job as last seen, and the one that just ended until its files are read.</summary>
    private AIJob? _job;
    private AIJob? _endedJob;

    public AIPanelViewModel(AppViewModel model, Guid id)
    {
        _model = model;
        ConversationId = id;
    }

    public Guid ConversationId { get; }

    /// <summary>Raised on the UI thread when what the tabs show changed (files read, a job update, a provider, an error).</summary>
    public event Action? Changed;

    public Transcript? Transcript { get; private set; }

    /// <summary>Every kept summary, oldest first.</summary>
    public IReadOnlyList<SummaryVersion> Summaries { get; private set; } = [];

    /// <summary>The summary version shown; null = the newest.</summary>
    public int? ShownVersion { get; private set; }

    public IReadOnlyList<ChatLog.Message> Chat { get; private set; } = [];

    /// <summary>The open chat's title, when it was resumed from the history with one.</summary>
    public string? ChatTitle { get; private set; }

    /// <summary>The earlier chats, newest first.</summary>
    public IReadOnlyList<ChatArchive.Entry> Archives { get; private set; } = [];

    public ProvidersViewModel Providers => _model.ProvidersModel;

    public Conversation? Conversation => _model.Conversations.FirstOrDefault(c => c.Id == ConversationId);

    /// <summary>The running job of this conversation; null when none runs or another conversation's runs.</summary>
    public AIJob? RunningJob => _model.AIJob is { } job && job.ConversationId == ConversationId ? job : null;

    /// <summary>The summary being written (shown while it runs and until it is read back).</summary>
    public AIJob? SummaryJob => ShownJob is { Kind: AIJobKind.Summary } job ? job : null;

    /// <summary>The question being answered (shown while it runs and until it is read back).</summary>
    public AIJob? QuestionJob => ShownJob is { Kind: AIJobKind.Question } job ? job : null;

    private AIJob? ShownJob => RunningJob ?? _endedJob;

    /// <summary>A job of another conversation, or of the other tab, which blocks this one's (macOS <c>busyElsewhere</c>).</summary>
    public AIJob? BusyElsewhere(DetailTab tab) =>
        _model.AIJob is { } job && (tab == DetailTab.Chat ? RunningQuestion : RunningSummary) is null ? job : null;

    public AIJob? RunningSummary => RunningJob is { Kind: AIJobKind.Summary } job ? job : null;

    public AIJob? RunningQuestion => RunningJob is { Kind: AIJobKind.Question } job ? job : null;

    /// <summary>The summary shown: the picked version, else the newest.</summary>
    public SummaryVersion? ShownSummary => Summaries.LastOrDefault(v => v.Number == ShownVersion) ?? Summaries.LastOrDefault();

    /// <summary>Why nothing can run for this conversation.</summary>
    public string? EmptyNote => Conversation is { HasTranscript: true } ? null : Loc.T("This conversation has no transcript yet.");

    /// <summary>A summary or a question can start now (macOS <c>canRun</c>).</summary>
    public bool CanRun => _model.AIJob is null && !_model.ChatChanging && Transcript is not null && Providers.CurrentAIProvider is not null
        && !_model.Updater.IsInstalling;

    /// <summary>Why this conversation's last job or chat change failed; null when none did.</summary>
    public string? Error => _model.AIError is { } error && error.ConversationId == ConversationId ? error.Message : null;

    /// <summary>The current provider's picked model ("" = a CLI's own default).</summary>
    public string? CurrentModel => Providers.CurrentAIProvider is { } provider ? Providers.AIModelFor(provider) : null;

    // Versions

    /// <summary>The model, else the provider's name, of a kept summary; null for one saved before versions (macOS <c>versionModel</c>).</summary>
    public string? VersionModel(SummaryVersion version)
    {
        if (version.Model is { Length: > 0 } model) return model;
        if (version.Provider is not { } provider) return null;
        return Providers.AIProviders.FirstOrDefault(p => p.Id == provider)?.Name ?? provider;
    }

    /// <summary>"Version N · model", the version menu's label.</summary>
    public string VersionLabel(SummaryVersion version) =>
        string.Join(" · ", new[] { Loc.Format("Version %lld", version.Number), VersionModel(version) }.OfType<string>());

    /// <summary>Its model, date and whether it was stopped (macOS <c>versionDetails</c>).</summary>
    public string VersionDetails(SummaryVersion version) =>
        string.Join(" · ", new[]
        {
            VersionModel(version),
            version.CreatedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
            version.Stopped ? Loc.T("Stopped") : null,
        }.OfType<string>());

    public void ShowVersion(int number)
    {
        if (Summaries.All(v => v.Number != number)) return;
        ShownVersion = number;
        Changed?.Invoke();
    }

    // Lifetime

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _model.PropertyChanged += OnModelChanged;
        _model.Updater.PropertyChanged += OnUpdaterChanged;
        Providers.Changed += OnProvidersChanged;
        _job = RunningJob;
        Refresh();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _model.PropertyChanged -= OnModelChanged;
        _model.Updater.PropertyChanged -= OnUpdaterChanged;
        Providers.Changed -= OnProvidersChanged;
        _loadGeneration++;
        _chatGeneration++;
        _loadedFor = null;
        _chatLoadedFor = null;
    }

    private void OnProvidersChanged() => Changed?.Invoke();

    /// <summary>The update's install starts or fails: <see cref="CanRun"/> changes.</summary>
    private void OnUpdaterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.Updater.IsInstalling)) Changed?.Invoke();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.AIJob):
                var job = RunningJob;
                // This conversation's job ended: shown until LoadAsync reads what it saved.
                if (_job is { } ended && job is null && ended.Text.Length > 0) _endedJob = ended;
                _job = job;
                Changed?.Invoke();
                break;
            case nameof(AppViewModel.Conversations):
            case nameof(AppViewModel.AIJobsEnded):
            case nameof(AppViewModel.ChatHistoryChanges):
                Refresh();
                break;
            case nameof(AppViewModel.AIError):
            case nameof(AppViewModel.ChatChanging):
            case nameof(AppViewModel.Settings):
                Changed?.Invoke();
                break;
        }
    }

    /// <summary>Reads the files again when their keys changed (macOS <c>LoadKey</c> and <c>HistoryKey</c>).</summary>
    private void Refresh()
    {
        var conversation = Conversation;
        var key = (conversation is not null, conversation?.HasTranscript ?? false, _model.AIJobsEnded);
        if (_loadedFor != key)
        {
            _loadedFor = key;
            _ = LoadAsync();
        }
        var chatKey = (conversation is not null, _model.ChatHistoryChanges);
        if (_chatLoadedFor != chatKey)
        {
            _chatLoadedFor = chatKey;
            _ = LoadChatAsync();
        }
        Changed?.Invoke();
    }

    /// <summary>The transcript, the summaries and the chat, off the UI thread (macOS <c>load</c>); shows the newest summary.</summary>
    private async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        if (Conversation is not { } conversation) return;
        var folder = conversation.Folder;
        var (transcript, versions, chat) = await Task.Run(() =>
        {
            Transcript? read = null;
            try
            {
                if (conversation.HasTranscript) read = Transcript.Read(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Debug.WriteLine($"EchoDiar: could not read the transcript of {folder}: {e.GetType().Name}");
            }
            return (read, Conversation.ReadSummaryVersions(folder), ChatLog.Read(folder));
        });
        if (generation != _loadGeneration || !_attached || _model.IsClosing) return;
        Transcript = transcript;
        Summaries = versions;
        Chat = chat.Messages;
        ShownVersion = null;
        _endedJob = null;
        Changed?.Invoke();
    }

    /// <summary>The open chat with its title and the earlier chats, off the UI thread (macOS <c>loadChat</c>).</summary>
    private async Task LoadChatAsync()
    {
        var generation = ++_chatGeneration;
        if (Conversation is not { } conversation) return;
        var folder = conversation.Folder;
        var (log, entries) = await Task.Run(() => (ChatLog.Read(folder), ChatArchive.Entries(folder)));
        if (generation != _chatGeneration || !_attached || _model.IsClosing) return;
        Chat = log.Messages;
        ChatTitle = log.Title;
        Archives = entries;
        Changed?.Invoke();
    }

    // Actions

    /// <summary>Summarize and Re-summarize (macOS <c>start(.summary)</c>).</summary>
    public void Summarize() => Start(AIJobKind.Summary, null);

    /// <summary>Asks <paramref name="question"/>; false when it cannot run now (macOS <c>ask</c>).</summary>
    public bool Ask(string question)
    {
        var text = question.Trim();
        if (!CanRun || text.Length == 0) return false;
        Start(AIJobKind.Question, text);
        return true;
    }

    private void Start(AIJobKind kind, string? question)
    {
        if (!CanRun || Conversation is not { } conversation || Transcript is not { } transcript
            || Providers.CurrentAIProvider is not { } provider) return;
        _model.StartAIJob(kind, question, conversation, transcript, Summaries.LastOrDefault()?.Text, Chat, provider,
            Providers.AIModelFor(provider));
    }

    public void Stop() => _model.StopAIJob();

    public void StartFreshChat()
    {
        if (Conversation is { } conversation) _model.StartFreshChat(conversation);
    }

    public void Resume(Guid entryId)
    {
        if (Conversation is { } conversation) _model.ResumeChat(entryId, conversation);
    }

    public void DeleteChat(Guid entryId)
    {
        if (Conversation is { } conversation) _model.DeleteChat(entryId, conversation);
    }

    public void ClearChats()
    {
        if (Conversation is { } conversation) _model.ClearChats(conversation);
    }

    /// <summary>
    /// The transcript as a prompt for any chat app (macOS <c>copyPrompt</c>): a summary prompt, or with
    /// <paramref name="question"/> (Chat) a question prompt, in the summary language setting; null without a transcript.
    /// </summary>
    public string? CopyPrompt(string? question) =>
        Conversation is { } conversation && Transcript is { } transcript
            ? PromptBuilder.CopyPrompt(conversation, transcript, question, _model.Settings.SummaryLanguage)
            : null;
}
