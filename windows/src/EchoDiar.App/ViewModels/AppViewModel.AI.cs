using System.Diagnostics;
using System.Text;
using EchoDiar.AI;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;
using EchoDiar.Transcribe;
using Microsoft.UI.Dispatching;

namespace EchoDiar.App.ViewModels;

/// <summary>What an AI job of one conversation does (macOS <c>AppModel.AIJob.Kind</c>).</summary>
public enum AIJobKind { Summary, Question }

/// <summary>
/// The running summary or question (macOS <c>AppModel.AIJob</c>): its conversation (null for a question across the
/// library, Phase 27), what it does (<see cref="Question"/> is the question asked), what a long summary does before its
/// text starts, and the answer so far; <see cref="UsesLocalModel"/> when an in-app model runs it (one that loads model
/// files). Replaced at each update, so a change is a new instance.
/// </summary>
public sealed record AIJob(
    Guid? ConversationId, AIJobKind Kind, string? Question, string? Status = null, string Text = "", bool UsesLocalModel = false);

/// <summary>Why the last AI job, or chat archive or resume, of a conversation (null: the library chat) failed.</summary>
public sealed record AIErrorInfo(Guid? ConversationId, string Message);

/// <summary>
/// The AI state of the app (macOS AppModel's AI tab): the providers model Settings › AI and the AI pickers share, the
/// running summary or question (one at a time; started off the UI thread, its updates applied on it), its error per
/// conversation, the chat history changes (archive, resume, delete, titles) and the cards' AI and Summary buttons.
/// </summary>
public sealed partial class AppViewModel
{
    /// <summary>Streamed text reaches <see cref="AIJob"/> at most this often: each change renders the whole Markdown again.</summary>
    private static readonly TimeSpan AITextFlushInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>A deleted conversation's job is waited for at most this long before its folder goes to the Recycle Bin.</summary>
    private static readonly TimeSpan AICancelWait = TimeSpan.FromSeconds(5);

    private ProvidersViewModel? _providersModel;
    private AIJob? _aiJob;
    private AIErrorInfo? _aiError;
    private int _aiJobsEnded;
    private int _chatHistoryChanges;
    private bool _chatChanging;

    /// <summary>The running job's cancellation: a deleted conversation's job saves nothing.</summary>
    private CancellationTokenSource? _aiCancel;

    /// <summary>The running job, from its start to its last update.</summary>
    private Task _aiTask = Task.CompletedTask;

    /// <summary>Stop was asked before the job's stream started: it is stopped as soon as it is registered.</summary>
    private volatile bool _aiStopRequested;

    /// <summary>Streamed text not in <see cref="AIJob"/> yet, and its scheduled flush (macOS <c>pendingAIText</c>).</summary>
    private readonly StringBuilder _aiPendingText = new();
    private DispatcherQueueTimer? _aiFlushTimer;

    /// <summary>Cancelled at Shutdown: the chat titles asked outside <see cref="AIJob"/>.</summary>
    private readonly CancellationTokenSource _aiLifetime = new();

    /// <summary>One change of a conversation's archived chats at a time, titles included (they rewrite <c>chats.json</c>).</summary>
    private readonly SemaphoreSlim _chatFiles = new(1, 1);

    /// <summary>What Settings › AI binds to, and the AI pickers' providers; one for the app's lifetime.</summary>
    public ProvidersViewModel ProvidersModel => _providersModel ??= new ProvidersViewModel(this, _services);

    /// <summary>The running summary or question; one at a time (macOS <c>aiJob</c>).</summary>
    public AIJob? AIJob
    {
        get => _aiJob;
        private set => SetProperty(ref _aiJob, value);
    }

    /// <summary>Why the last AI job or chat change failed, with its conversation (macOS <c>aiError</c>).</summary>
    public AIErrorInfo? AIError
    {
        get => _aiError;
        private set => SetProperty(ref _aiError, value);
    }

    /// <summary>Bumped when an AI job ends, so the AI tab reads the summaries and the chat again.</summary>
    public int AIJobsEnded
    {
        get => _aiJobsEnded;
        private set => SetProperty(ref _aiJobsEnded, value);
    }

    /// <summary>Bumped when a chat or the archived chats change outside a question job, so the AI tab reads them again.</summary>
    public int ChatHistoryChanges
    {
        get => _chatHistoryChanges;
        private set => SetProperty(ref _chatHistoryChanges, value);
    }

    /// <summary>A chat is being archived, resumed or deleted: no question starts until it is done.</summary>
    public bool ChatChanging
    {
        get => _chatChanging;
        private set => SetProperty(ref _chatChanging, value);
    }

    // The cards' buttons

    /// <summary>Shows the conversation on <paramref name="tab"/> (macOS <c>open(_:tab:)</c>).</summary>
    public void Open(Guid id, DetailTab tab)
    {
        DetailTab = tab;
        Route = Route.ForConversation(id);
    }

    /// <summary>The card's AI button: the conversation's Chat tab.</summary>
    public void OpenChat(Guid id) => Open(id, DetailTab.Chat);

    /// <summary>The card's Summary button (shown when it has a summary): the conversation's Summary tab.</summary>
    public void OpenSummary(Guid id) => Open(id, DetailTab.Summary);

    // Jobs

    /// <summary>
    /// Starts a summary or the question <paramref name="question"/> about <paramref name="conversation"/> with
    /// <paramref name="provider"/> and <paramref name="model"/>, saved as the AI tab's default (macOS <c>startAIJob</c>).
    /// <paramref name="summary"/> and <paramref name="history"/> are the saved summary and chat, sent with a question.
    /// The client is made off the UI thread; the job's updates come back on it.
    /// </summary>
    public void StartAIJob(
        AIJobKind kind, string? question, Conversation conversation, Transcript transcript, string? summary,
        IReadOnlyList<ChatLog.Message> history, AIProviderInfo provider, string model)
    {
        if (_closing || AIJob is not null || kind == AIJobKind.Question && (ChatChanging || string.IsNullOrWhiteSpace(question))) return;
        if (Updater.IsInstalling)
        {
            // Windows closes the app once the update is downloaded: the answer would be cut off.
            AIError = new AIErrorInfo(conversation.Id, InstallingNotice);
            return;
        }
        model = model.Trim();
        ProvidersModel.SetAIModel(model, provider);
        AIError = null;
        _aiStopRequested = false;
        _aiPendingText.Clear();
        var cancel = new CancellationTokenSource();
        _aiCancel = cancel;
        AIJob = new AIJob(conversation.Id, kind, question, UsesLocalModel: provider.Kind == AIProviderKind.InApp);
        // Read on the UI thread: the settings and the provider configs belong to it.
        var config = provider.Kind == AIProviderKind.InApp ? null : ProvidersModel.Config(provider.Id);
        var settings = Settings;
        _aiTask = RunAIJobAsync(kind, question, conversation, transcript, summary, history, provider, config, model,
            settings.SummaryLanguage, settings.ChatContextMessages, cancel);
    }

    private async Task RunAIJobAsync(
        AIJobKind kind, string? question, Conversation conversation, Transcript transcript, string? summary,
        IReadOnlyList<ChatLog.Message> history, AIProviderInfo provider, ProviderConfig? config, string model,
        string language, int historyLimit, CancellationTokenSource cancel)
    {
        var id = conversation.Id;
        try
        {
            if (await MakeJobClientAsync(provider, config, id, cancel.Token) is not { } client) return;
            var stream = kind == AIJobKind.Summary
                ? _services.AI.Summarize(conversation, transcript, client, provider.Id, model, language, cancel.Token)
                : _services.AI.Ask(question!, conversation, transcript, summary, history, historyLimit, client, provider.Id,
                    model, language, cancel.Token);
            await StreamAIJobAsync(stream, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // A deleted conversation: nothing was saved.
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: the AI job of {id} failed: {e.GetType().Name}");
            if (!_closing) AIError = new AIErrorInfo(id, e.Message);
        }
        finally
        {
            if (ReferenceEquals(_aiCancel, cancel)) _aiCancel = null;
            cancel.Dispose();
            if (!_closing)
            {
                FlushAIText();
                AIJob = null;
                AIJobsEnded++;
            }
        }
        if (_closing) return;
        // A new summary sets hasSummary: the card shows its Summary button.
        await ReloadAsync(id);
    }

    /// <summary>
    /// The job's client, made off the UI thread; null with the AI error of conversation <paramref name="id"/> (null: the
    /// library chat) when it cannot be made, or when the job was cancelled or the window closed meanwhile.
    /// </summary>
    private async Task<ILLMProvider?> MakeJobClientAsync(
        AIProviderInfo provider, ProviderConfig? config, Guid? id, CancellationToken cancellationToken)
    {
        ILLMProvider? client;
        try
        {
            client = await Task.Run(() => MakeClient(provider, config), cancellationToken);
        }
        catch (ModelStoreException e) when (e.Kind == ModelStoreError.ChecksumMismatch)
        {
            // The install was deleted (InstallRemoved refreshes its row to Download).
            Debug.WriteLine($"EchoDiar: {provider.Id} failed its integrity check");
            if (!_closing) AIError = new AIErrorInfo(id, Loc.Format("%@ failed its integrity check. Download it again.", provider.Name));
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Debug.WriteLine($"EchoDiar: could not make the client of {provider.Id}: {e.GetType().Name}");
            if (!_closing) AIError = new AIErrorInfo(id, Loc.Format("%@: %@", provider.Name, ProvidersViewModel.ShortMessage(e)));
            return null;
        }
        if (_closing || cancellationToken.IsCancellationRequested) return null;
        if (client is null) AIError = new AIErrorInfo(id, Loc.Format("%@ is not available.", provider.Name));
        return client;
    }

    /// <summary>Applies the job's updates on the UI thread until its stream ends.</summary>
    private async Task StreamAIJobAsync(IAsyncEnumerable<AIUpdate> stream, CancellationToken cancellationToken)
    {
        await using var updates = stream.GetAsyncEnumerator(cancellationToken);
        // The first MoveNextAsync registers the job, so Stop sees it from here; a Stop asked before stops it now.
        var next = updates.MoveNextAsync();
        if (_aiStopRequested) _services.AI.Stop();
        while (await next)
        {
            ApplyAIUpdate(updates.Current);
            next = updates.MoveNextAsync();
        }
    }

    /// <summary>
    /// The client for <paramref name="provider"/> (macOS <c>llmClient(for:)</c>): the in-app model of its catalog entry
    /// (Windows AI or a downloaded GGUF model), else the client of its config with its saved key; null when it is gone.
    /// Reads Credential Manager and the model's files: off the UI thread.
    /// </summary>
    private ILLMProvider? MakeClient(AIProviderInfo provider, ProviderConfig? config)
    {
        if (provider.Kind == AIProviderKind.InApp) return _services.MakeLocalProvider(provider.Id);
        return config is null ? null : _services.MakeProvider(config);
    }

    /// <summary>
    /// Shows an update of the running job (macOS <c>applyAIUpdate</c>): a status at once; text at once when it is the
    /// first, then at most every 120 ms, as the answer view renders its whole Markdown again at each change.
    /// </summary>
    private void ApplyAIUpdate(AIUpdate update)
    {
        if (_closing || AIJob is not { } job) return;
        if (update.Kind == AIUpdateKind.Status)
        {
            // The AI project's fixed statuses have catalog keys; "Reading part 2 of 5…" stays English, as on macOS.
            AIJob = job with { Status = Loc.T(update.Value) };
            return;
        }
        _aiPendingText.Append(update.Value);
        if (job.Text.Length == 0)
        {
            FlushAIText();
            return;
        }
        if (_aiFlushTimer is null)
        {
            _aiFlushTimer = _dispatcher.CreateTimer();
            _aiFlushTimer.Interval = AITextFlushInterval;
            _aiFlushTimer.IsRepeating = false;
            _aiFlushTimer.Tick += (_, _) => FlushAIText();
        }
        if (!_aiFlushTimer.IsRunning) _aiFlushTimer.Start();
    }

    /// <summary>Moves the batched text into <see cref="AIJob"/>; by the scheduled flush and when the stream ends.</summary>
    private void FlushAIText()
    {
        _aiFlushTimer?.Stop();
        if (_aiPendingText.Length == 0 || AIJob is not { } job) return;
        AIJob = job with { Status = null, Text = job.Text + _aiPendingText };
        _aiPendingText.Clear();
    }

    /// <summary>
    /// Ends the job early: the answer or summary streamed until then is saved (a summary marked stopped), then it ends as
    /// usual (macOS <c>stopAIJob</c>). Nothing is saved when nothing was streamed yet; a library question without text
    /// yet (still searching) is cancelled outright.
    /// </summary>
    public void StopAIJob()
    {
        if (AIJob is not { } job) return;
        if (job is { ConversationId: null, Text.Length: 0 })
        {
            try
            {
                _aiCancel?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job ended meanwhile.
            }
        }
        _aiStopRequested = true;
        _services.AI.Stop();
    }

    /// <summary>
    /// Cancels the job of conversation <paramref name="id"/> (nothing is saved) and waits for it, at most 5 s, so nothing
    /// writes into its folder once it goes to the Recycle Bin (macOS <c>deleteConversation</c>).
    /// </summary>
    private async Task CancelAIJobAsync(Guid id)
    {
        if (AIJob?.ConversationId != id) return;
        try
        {
            _aiCancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The job ended meanwhile.
        }
        var job = _aiTask;
        await Task.WhenAny(job, Task.Delay(AICancelWait));
    }

    /// <summary>
    /// Quit: the running job stops and keeps what it streamed, a title being asked is cancelled; nothing is waited for
    /// (a CLI child dies with the app's job object, an HTTP request with the process).
    /// </summary>
    private void ShutdownAI()
    {
        _aiFlushTimer?.Stop();
        _aiLifetime.Cancel();
        if (_aiJob is not null) _services.AI.Stop();
    }

    // Chat history

    /// <summary>
    /// Archives the conversation's chat so the next question starts without history, then asks for its title when it has
    /// none (macOS <c>startFreshChat</c>). Not while a question runs for it.
    /// </summary>
    public void StartFreshChat(Conversation conversation) =>
        _ = ChangeChatAsync(conversation.Id, conversation.Folder, ChatArchive.Archive);

    /// <summary>Makes the archived chat <paramref name="entryId"/> the open one, archiving the current one (macOS <c>resumeChat</c>).</summary>
    public void ResumeChat(Guid entryId, Conversation conversation) =>
        _ = ChangeChatAsync(conversation.Id, conversation.Folder, folder => ChatArchive.Resume(entryId, folder));

    /// <summary>Deletes the archived chat <paramref name="entryId"/> (macOS <c>deleteChat</c>).</summary>
    public void DeleteChat(Guid entryId, Conversation conversation) =>
        _ = ChangeChatAsync(conversation.Id, conversation.Folder, folder =>
        {
            ChatArchive.Delete(entryId, folder);
            return null;
        });

    /// <summary>Deletes every archived chat; the open chat stays (macOS <c>clearChats</c>).</summary>
    public void ClearChats(Conversation conversation) =>
        _ = ChangeChatAsync(conversation.Id, conversation.Folder, folder =>
        {
            ChatArchive.DeleteAll(folder);
            return null;
        });

    /// <summary>
    /// Runs <paramref name="change"/> on the chat in <paramref name="folder"/> of conversation <paramref name="id"/> (null:
    /// the library chat, Phase 27) off the UI thread, unless a question runs for it (its answer is appended to
    /// <c>chat.json</c>); a failure shows as its AI error (macOS <c>changeChat</c>). No question starts meanwhile.
    /// </summary>
    internal async Task ChangeChatAsync(Guid? id, string folder, Func<string, ChatArchive.Archived?> change)
    {
        if (_closing || ChatChanging || AIJob is { Kind: AIJobKind.Question } job && job.ConversationId == id) return;
        ChatChanging = true;
        ChatArchive.Archived? archived = null;
        try
        {
            archived = await Task.Run(async () =>
            {
                await _chatFiles.WaitAsync();
                try
                {
                    return change(folder);
                }
                finally
                {
                    _chatFiles.Release();
                }
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not change the chats of {id}: {e.GetType().Name}");
            if (!_closing) AIError = new AIErrorInfo(id, e.Message);
        }
        if (_closing) return;
        ChatChanging = false;
        // Also after a failure: a resume may have archived the open chat first, so the tab reads what is on disk.
        ChatHistoryChanges++;
        if (archived is { NeedsTitle: true }) _ = RequestTitleAsync(archived.Entry, folder);
    }

    /// <summary>
    /// Names the archived chat <paramref name="entry"/> with the current provider and model, outside <see cref="AIJob"/>
    /// (macOS <c>requestTitle</c>). Only with the provider that gave every answer of the chat, so its questions reach no
    /// provider that never saw them, and never with an in-app model, whose gate a hidden job would hold. Without one, or
    /// on error, its fallback title (the first question) stays.
    /// </summary>
    private async Task RequestTitleAsync(ChatArchive.Entry entry, string folder)
    {
        if (ProvidersModel.CurrentAIProvider is not { Kind: not AIProviderKind.InApp } provider) return;
        var model = ProvidersModel.AIModelFor(provider);
        var config = ProvidersModel.Config(provider.Id);
        var token = _aiLifetime.Token;
        bool titled;
        try
        {
            titled = await Task.Run(async () =>
            {
                if (ChatArchive.Log(entry.Id, folder) is not { } chat
                    || !chat.Messages.All(m => m.Role != ChatRole.Assistant || m.Provider == provider.Id)
                    || MakeClient(provider, config) is not { } client) return false;
                var title = new StringBuilder();
                await foreach (var update in _services.AI.Title(chat.Messages, client, model, token))
                {
                    if (update.Kind == AIUpdateKind.Text) title.Append(update.Value);
                }
                if (title.Length == 0 || token.IsCancellationRequested) return false;
                await _chatFiles.WaitAsync(token);
                try
                {
                    ChatArchive.SetTitle(title.ToString(), entry.Id, folder);
                }
                finally
                {
                    _chatFiles.Release();
                }
                return true;
            }, token);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: no title for chat {entry.Id}: {e.GetType().Name}");
            return;
        }
        if (titled && !_closing) ChatHistoryChanges++;
    }
}
