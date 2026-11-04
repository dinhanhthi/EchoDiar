using System.Diagnostics;
using System.Text;
using EchoDiar.AI;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;
using EchoDiar.Transcribe;
using Microsoft.UI.Dispatching;

namespace EchoDiar.App.ViewModels;

/// <summary>What an AI job of one conversation does (macOS AppModel's <c>AIJob.Kind</c>).</summary>
public enum AIJobKind { Summary, Question }

/// <summary>
/// What <see cref="AppViewModel.AIJobs"/> keeps of a running summary or question, beside its key, kind and status (macOS
/// AppModel's <c>AIJob</c> with its <c>aiJobText</c> and <c>aiJobStatus</c>): what it does (<see cref="Question"/> is the
/// question asked), its <see cref="AIService"/> job id, what a long job does before its text starts and the answer so
/// far (changed in place: <see cref="AppViewModel.AIJobTextChanged"/> says when), and the app's handles on it. On the UI
/// thread.
/// </summary>
public sealed class AIJob(AIJobKind kind, string? question)
{
    public AIJobKind Kind { get; } = kind;

    public string? Question { get; } = question;

    /// <summary>Its job id, for <see cref="AIService.Stop(Guid)"/> and <see cref="AIService.CancelAsync"/>.</summary>
    public Guid JobId { get; } = Guid.NewGuid();

    /// <summary>What it does before its first text; null once the text starts.</summary>
    public string? Status { get; internal set; }

    /// <summary>The answer streamed so far; kept once the job ends, so its view shows it until the saved answer is read.</summary>
    public string Text { get; internal set; } = "";

    /// <summary>Its cancellation: a deleted conversation's job, a library question stopped during its search.</summary>
    internal CancellationTokenSource Cancel { get; } = new();

    /// <summary>The job, from its start to its last update and its conversation's reload.</summary>
    internal Task Work { get; set; } = Task.CompletedTask;

    /// <summary>Stop was asked before its stream started: it is stopped as soon as it is registered.</summary>
    internal bool StopRequested { get; set; }

    /// <summary>Streamed text not in <see cref="Text"/> yet, and its scheduled flush (macOS <c>pendingAIText</c>).</summary>
    internal StringBuilder PendingText { get; } = new();

    internal DispatcherQueueTimer? FlushTimer { get; set; }
}

/// <summary>Why the last AI job, or chat archive or resume, of a conversation (null: the library chat) failed.</summary>
public sealed record AIErrorInfo(Guid? ConversationId, string Message);

/// <summary>
/// The AI state of the app (macOS AppModel's AI tab): the providers model Settings › AI and the AI pickers share, the
/// running summaries and questions (one per conversation, the library chat beside them; started off the UI thread, their
/// updates applied on it), the error per conversation, the chat history changes (archive, resume, delete, titles) and
/// the cards' AI and Summary buttons.
/// </summary>
public sealed partial class AppViewModel
{
    /// <summary>Streamed text reaches <see cref="AIJob.Text"/> at most this often: each change renders the whole Markdown again.</summary>
    private static readonly TimeSpan AITextFlushInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// A deleted conversation's job (its client and its conversation's reload) is waited for at most this long, once its
    /// write has ended, before its folder goes to the Recycle Bin.
    /// </summary>
    private static readonly TimeSpan AICancelWait = TimeSpan.FromSeconds(5);

    private ProvidersViewModel? _providersModel;
    private AIErrorInfo? _aiError;
    private int _chatHistoryChanges;
    private bool _chatChanging;

    /// <summary>How many jobs of each conversation, and of the library chat, have ended (<see cref="AIJobEnds"/>).</summary>
    private readonly Dictionary<Guid, int> _aiJobEnds = [];
    private int _libraryJobEnds;

    /// <summary>
    /// Conversations <see cref="DeleteConversationAsync"/> moves to the Recycle Bin: their AI job's error is not shown,
    /// and no new job starts for them.
    /// </summary>
    private readonly HashSet<Guid> _deletingConversationIds = [];

    /// <summary>Cancelled at Shutdown: the chat titles asked outside <see cref="AIJobs"/>.</summary>
    private readonly CancellationTokenSource _aiLifetime = new();

    /// <summary>One change of a conversation's archived chats at a time, titles included (they rewrite <c>chats.json</c>).</summary>
    private readonly SemaphoreSlim _chatFiles = new(1, 1);

    /// <summary>What Settings › AI binds to, and the AI pickers' providers; one for the app's lifetime.</summary>
    public ProvidersViewModel ProvidersModel => _providersModel ??= new ProvidersViewModel(this, _services);

    /// <summary>
    /// The running summaries and questions, at most one per conversation id; the library chat (AppViewModel.RAG.cs) is the
    /// null key (macOS <c>aiJobs</c>). Raised when a job starts, gets its first text or ends: cards follow it, so a job's
    /// streamed text and status are raised by <see cref="AIJobTextChanged"/> instead. On the UI thread only (the table is
    /// not thread-safe).
    /// </summary>
    public AIJobTable<AIJob> AIJobs { get; } = new();

    /// <summary>
    /// Raised on the UI thread when the text or the status of the job of a key (null: the library chat) changed; for the
    /// answer views only, as it comes every 120 ms while a job streams.
    /// </summary>
    public event Action<Guid?>? AIJobTextChanged;

    /// <summary>Why the last AI job or chat change failed, with its conversation (macOS <c>aiError</c>).</summary>
    public AIErrorInfo? AIError
    {
        get => _aiError;
        private set => SetProperty(ref _aiError, value);
    }

    /// <summary>
    /// How many AI jobs of <paramref name="key"/> (null: the library chat) have ended, raised as <c>AIJobEnds</c> (macOS
    /// <c>aiJobEnds</c>): its AI tab or chat reads the summaries and the chat again, while other keys' views keep their
    /// state (a summary version picked stays shown).
    /// </summary>
    public int AIJobEnds(Guid? key) => key is { } id ? _aiJobEnds.GetValueOrDefault(id) : _libraryJobEnds;

    /// <summary>What the job of <paramref name="key"/> does before its first text: "Queued" behind another in-app job, else its status.</summary>
    public string? AIJobStatus(Guid? key) =>
        AIJobs.IsQueued(key) ? Loc.T("Queued") : AIJobs.EntryFor(key)?.Payload.Status;

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
    /// <paramref name="provider"/> and <paramref name="model"/>, saved as the AI tab's default (macOS <c>startAIJob</c>),
    /// beside the jobs of the other conversations; does nothing while the conversation has a job or is being deleted
    /// (its answer would recreate the folder going to the Recycle Bin).
    /// <paramref name="summary"/> and <paramref name="history"/> are the saved summary and chat, sent with a question.
    /// The client is made off the UI thread; the job's updates come back on it.
    /// </summary>
    public void StartAIJob(
        AIJobKind kind, string? question, Conversation conversation, Transcript transcript, string? summary,
        IReadOnlyList<ChatLog.Message> history, AIProviderInfo provider, string model)
    {
        if (_closing || AIJobs.EntryFor(conversation.Id) is not null
            || _deletingConversationIds.Contains(conversation.Id)
            || kind == AIJobKind.Question && (ChatChanging || string.IsNullOrWhiteSpace(question))) return;
        if (Updater.IsInstalling)
        {
            // Windows closes the app once the update is downloaded: the answer would be cut off.
            AIError = new AIErrorInfo(conversation.Id, InstallingNotice);
            return;
        }
        model = model.Trim();
        ProvidersModel.SetAIModel(model, provider);
        AIError = null;
        var job = new AIJob(kind, question);
        // In the table before the job's task starts: its first steps look it up there.
        AddAIJob(conversation.Id, kind == AIJobKind.Summary ? AIJobTable.Kind.Summary : AIJobTable.Kind.Question,
            provider.Kind == AIProviderKind.InApp, job);
        // Read on the UI thread: the settings and the provider configs belong to it.
        var config = provider.Kind == AIProviderKind.InApp ? null : ProvidersModel.Config(provider.Id);
        var settings = Settings;
        job.Work = RunAIJobAsync(kind, question, conversation, transcript, summary, history, provider, config, model,
            settings.SummaryLanguage, settings.ChatContextMessages, job);
    }

    private async Task RunAIJobAsync(
        AIJobKind kind, string? question, Conversation conversation, Transcript transcript, string? summary,
        IReadOnlyList<ChatLog.Message> history, AIProviderInfo provider, ProviderConfig? config, string model,
        string language, int historyLimit, AIJob job)
    {
        var id = conversation.Id;
        var cancel = job.Cancel;
        try
        {
            if (await MakeJobClientAsync(provider, config, id, cancel.Token) is not { } client) return;
            var stream = kind == AIJobKind.Summary
                ? _services.AI.Summarize(conversation, transcript, client, provider.Id, model, language, cancel.Token,
                    job.JobId)
                : _services.AI.Ask(question!, conversation, transcript, summary, history, historyLimit, client, provider.Id,
                    model, language, cancel.Token, job.JobId);
            await StreamAIJobAsync(id, job, stream, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // A deleted conversation: nothing was saved.
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: the AI job of {id} failed: {e.GetType().Name}");
            // A conversation being deleted: its job was cancelled before the app's token, which its stream reports.
            if (!_closing && !_deletingConversationIds.Contains(id)) AIError = new AIErrorInfo(id, e.Message);
        }
        finally
        {
            cancel.Dispose();
            if (!_closing) EndAIJob(id, job);
        }
        if (_closing) return;
        // A new summary sets hasSummary: the card shows its Summary button.
        await ReloadAsync(id);
    }

    /// <summary>Puts <paramref name="job"/> in <see cref="AIJobs"/> on <paramref name="key"/>, free (the callers' guard).</summary>
    private void AddAIJob(Guid? key, AIJobTable.Kind kind, bool usesInAppModel, AIJob job)
    {
        AIJobs.Start(key, kind, usesInAppModel, job);
        OnPropertyChanged(nameof(AIJobs));
    }

    /// <summary>
    /// Removes the job of <paramref name="key"/> from <see cref="AIJobs"/>, its text flushed first (and kept in
    /// <paramref name="job"/>), and bumps the key's <see cref="AIJobEnds"/> (macOS
    /// <c>endAIJob</c>).
    /// </summary>
    private void EndAIJob(Guid? key, AIJob job)
    {
        FlushAIText(key, job);
        if (!ReferenceEquals(AIJobs.EntryFor(key)?.Payload, job)) return;
        AIJobs.Finish(key);
        OnPropertyChanged(nameof(AIJobs));
        if (key is { } id) _aiJobEnds[id] = _aiJobEnds.GetValueOrDefault(id) + 1;
        else _libraryJobEnds++;
        OnPropertyChanged(nameof(AIJobEnds));
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

    /// <summary>Applies the updates of <paramref name="job"/> (of <paramref name="key"/>) on the UI thread until its stream ends.</summary>
    private async Task StreamAIJobAsync(
        Guid? key, AIJob job, IAsyncEnumerable<AIUpdate> stream, CancellationToken cancellationToken)
    {
        await using var updates = stream.GetAsyncEnumerator(cancellationToken);
        // The first MoveNextAsync registers the job, so Stop sees it from here; a Stop asked before stops it now.
        var next = updates.MoveNextAsync();
        if (job.StopRequested) _services.AI.Stop(job.JobId);
        while (await next)
        {
            ApplyAIUpdate(key, job, updates.Current);
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
    /// Shows an update of <paramref name="job"/>, the job of <paramref name="key"/> (macOS <c>applyAIUpdate</c>): a status
    /// at once; text at once when it is the first (the job is then streaming), then at most every 120 ms, as the answer
    /// view renders its whole Markdown again at each change.
    /// </summary>
    private void ApplyAIUpdate(Guid? key, AIJob job, AIUpdate update)
    {
        if (_closing || AIJobs.EntryFor(key) is not { } entry || !ReferenceEquals(entry.Payload, job)) return;
        if (update.Kind == AIUpdateKind.Status)
        {
            // The AI project's fixed statuses have catalog keys; "Reading part 2 of 5…" stays English, as on macOS.
            job.Status = Loc.T(update.Value);
            AIJobTextChanged?.Invoke(key);
            return;
        }
        job.PendingText.Append(update.Value);
        if (job.Text.Length == 0)
        {
            if (entry.Status == AIJobTable.Status.Running)
            {
                AIJobs.MarkStreaming(key);
                OnPropertyChanged(nameof(AIJobs));
            }
            FlushAIText(key, job);
            return;
        }
        if (job.FlushTimer is null)
        {
            var timer = _dispatcher.CreateTimer();
            timer.Interval = AITextFlushInterval;
            timer.IsRepeating = false;
            timer.Tick += (_, _) => FlushAIText(key, job);
            job.FlushTimer = timer;
        }
        if (!job.FlushTimer.IsRunning) job.FlushTimer.Start();
    }

    /// <summary>
    /// Moves the batched text of <paramref name="job"/> into its <see cref="AIJob.Text"/>; by the scheduled flush and when
    /// the stream ends.
    /// </summary>
    private void FlushAIText(Guid? key, AIJob job)
    {
        job.FlushTimer?.Stop();
        if (job.PendingText.Length == 0) return;
        job.Status = null;
        job.Text += job.PendingText.ToString();
        job.PendingText.Clear();
        if (!_closing) AIJobTextChanged?.Invoke(key);
    }

    /// <summary>
    /// Ends the job of <paramref name="key"/> (null: the library chat) early, the others keep running: the answer or
    /// summary streamed until then is saved (a summary marked stopped), then it ends as usual (macOS <c>stopAIJob</c>).
    /// Nothing is saved when nothing was streamed yet; a library question without text yet (still searching) is
    /// cancelled outright.
    /// </summary>
    public void StopAIJob(Guid? key)
    {
        if (AIJobs.EntryFor(key)?.Payload is not { } job) return;
        if (key is null && job.Text.Length == 0)
        {
            try
            {
                job.Cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job ended meanwhile.
            }
        }
        job.StopRequested = true;
        _services.AI.Stop(job.JobId);
    }

    /// <summary>
    /// Cancels the job of conversation <paramref name="id"/> without saving, also after a Stop, so nothing writes into its
    /// folder once it goes to the Recycle Bin (macOS <c>deleteConversation</c>): waits, without a limit, for the summary
    /// or chat write it had already started (none starts once it is cancelled), then at most 5 s for the rest of the job
    /// (its client and its conversation's reload), so a slow reload does not hold the delete but rarely lists the
    /// conversation again.
    /// </summary>
    private async Task CancelAIJobAsync(Guid id)
    {
        if (AIJobs.EntryFor(id)?.Payload is not { } job) return;
        // Before the app's token: cancelling that one would end the job while still marked stopped, which saves.
        await _services.AI.CancelAsync(job.JobId);
        try
        {
            job.Cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The job ended meanwhile.
        }
        await Task.WhenAny(job.Work, Task.Delay(AICancelWait));
    }

    /// <summary>
    /// Quit: the running jobs stop and keep what they streamed, a title being asked is cancelled; nothing is waited for
    /// (a CLI child dies with the app's job object, an HTTP request with the process).
    /// </summary>
    private void ShutdownAI()
    {
        foreach (var key in AIJobs.BusyIds.Select(id => (Guid?)id).Append(null))
        {
            AIJobs.EntryFor(key)?.Payload.FlushTimer?.Stop();
        }
        CatchUpRun?.FlushTimer?.Stop();
        _aiLifetime.Cancel();
        if (!AIJobs.IsEmpty || _runningCatchUps > 0) _services.AI.Stop();
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
    /// <c>chat.json</c>) or the conversation is being deleted (its chats would recreate the folder going to the Recycle
    /// Bin); a failure shows as its AI error (macOS <c>changeChat</c>). No question starts meanwhile.
    /// </summary>
    internal async Task ChangeChatAsync(Guid? id, string folder, Func<string, ChatArchive.Archived?> change)
    {
        if (_closing || ChatChanging || AIJobs.EntryFor(id) is { Kind: not AIJobTable.Kind.Summary }
            || id is { } conversationId && _deletingConversationIds.Contains(conversationId)) return;
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
    /// Names the archived chat <paramref name="entry"/> with the current provider and model, outside <see cref="AIJobs"/>
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
