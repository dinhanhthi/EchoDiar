using System.Diagnostics;
using System.Text;
using EchoDiar.AI;
using EchoDiar.App.Views;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;
using EchoDiar.Transcribe;
using Microsoft.UI.Dispatching;

namespace EchoDiar.App.ViewModels;

/// <summary>Where a <see cref="LiveChatRun"/> is (macOS <c>LiveChatRun.Status</c>).</summary>
public enum LiveChatStatus { Starting, Streaming, Failed }

/// <summary>
/// A live chat question of the current recording (<see cref="AppViewModel.AskLive"/>; macOS <c>AppModel.LiveChatRun</c>):
/// its <see cref="AIService"/> job id, for <see cref="AIService.Stop(Guid)"/>, and what its answer shows. Changed in place
/// on the UI thread: <see cref="AppViewModel.LiveChatRun"/> is raised when it starts, gets its first text, is stopping,
/// fails or ends; <see cref="AppViewModel.LiveChatTextChanged"/> when its text or status line changes.
/// </summary>
public sealed class LiveChatRun(Guid jobId, string question)
{
    public Guid JobId { get; } = jobId;

    public string Question { get; } = question;

    public LiveChatStatus Status { get; internal set; } = LiveChatStatus.Starting;

    /// <summary>The answer streamed so far (every 120 ms), Markdown.</summary>
    public string Text { get; internal set; } = "";

    /// <summary>What the job is doing before its text starts.</summary>
    public string? StatusLine { get; internal set; }

    public string? Error { get; internal set; }

    /// <summary>Stop was asked for: the question and the answer streamed until then are saved.</summary>
    public bool Stopping { get; internal set; }

    public bool IsRunning => Status != LiveChatStatus.Failed;

    /// <summary>Streamed text not in <see cref="Text"/> yet, and its scheduled flush.</summary>
    internal StringBuilder PendingText { get; } = new();

    internal DispatcherQueueTimer? FlushTimer { get; set; }
}

/// <summary>
/// The recording page's tabs and live chat (macOS AppModel's <c>recordingTab</c>, sidebar auto-hide and
/// <c>// MARK: - Live chat</c>): the tab shown, the sidebar the side-by-side layout hides for the recording, and questions
/// about the meeting so far, on the catch-up provider, streamed into <see cref="LiveChatRun"/>, then saved in the
/// recording's <c>chat.json</c> (marked live) and listed in <see cref="LiveChatMessages"/>. On the UI thread; files, the
/// client and the plan are made off it.
/// </summary>
public sealed partial class AppViewModel
{
    private RecordingTab _recordingTab = RecordingTab.Transcript;
    private LiveChatRun? _liveChatRun;
    private IReadOnlyList<ChatLog.Message> _liveChatMessages = [];

    /// <summary>
    /// The side-by-side layout hid the sidebar at this recording's start and the user has not toggled it since: the end
    /// of the recording shows it again.
    /// </summary>
    private bool _sidebarAutoHidden;

    /// <summary>
    /// The recording page's tab (Tabs layout), or the right panel's Catchup | Chat (side by side, which shows
    /// Transcript as Catchup): Transcript at start (Catchup side by side); the finished
    /// recording opens on the matching <see cref="DetailTab"/> when its page was showing at Stop.
    /// </summary>
    public RecordingTab RecordingTab
    {
        get => _recordingTab;
        set => SetProperty(ref _recordingTab, value);
    }

    /// <summary>
    /// The current recording's live chat answer being written (or the last one that failed); null otherwise. Cleared when
    /// a new recording starts: a run still writing for the previous one finishes and saves in its folder, out of sight.
    /// </summary>
    public LiveChatRun? LiveChatRun
    {
        get => _liveChatRun;
        private set => SetProperty(ref _liveChatRun, value);
    }

    /// <summary>The current recording's chat (its <c>chat.json</c>), oldest first; read at start and after each answer.</summary>
    public IReadOnlyList<ChatLog.Message> LiveChatMessages
    {
        get => _liveChatMessages;
        private set => SetProperty(ref _liveChatMessages, value);
    }

    /// <summary>Raised on the UI thread when <see cref="LiveChatRun"/>'s text or status line changed (at most every 120 ms).</summary>
    public event Action? LiveChatTextChanged;

    /// <summary>
    /// Why a live chat question cannot be asked now; null when it can. Same provider as Catch up
    /// (<see cref="CatchUpProvider"/>), so the same reason when there is none. Computed: read it again when
    /// <see cref="RecordingCaptions"/>, <see cref="Settings"/> or <see cref="LiveChatRun"/> change, and on
    /// <see cref="ProvidersViewModel.Changed"/>.
    /// </summary>
    public string? LiveChatDisabledReason
    {
        get
        {
            if (RecordingCaptions is not { } captions || _recordingTranscriber is null)
                return RecordingView.NoCaptionsReason(this);
            if (LiveChatRun is { IsRunning: true }) return Loc.T("An answer is being written.");
            if (CatchUpProvider is null)
            {
                return Loc.T("Catch up needs an AI provider outside the app: a cloud API, a local server or a command-line tool. Add one in Settings › AI Providers.");
            }
            if (captions.Lines.Count == 0) return Loc.T("Nothing to ask about yet.");
            return null;
        }
    }

    /// <summary>
    /// Answers <paramref name="question"/> about the current recording so far with <see cref="CatchUpProvider"/>, streamed
    /// into <see cref="LiveChatRun"/> (shown at once), then saved with the answer in the recording's <c>chat.json</c>
    /// (marked live) and shown in <see cref="LiveChatMessages"/>. The transcript, or the catch-ups and the newest lines,
    /// fit the provider's context window beside the last "Chat context" messages. The client, the live lines, the files
    /// and the plan are made off the UI thread. A run still going at Stop finishes and saves in that recording's folder.
    /// </summary>
    public void AskLive(string question)
    {
        question = question.Trim();
        if (_closing || question.Length == 0 || LiveChatDisabledReason is not null || CatchUpProvider is not { } provider
            || _recordingTranscriber is not { } transcriber || _activeFolder is not { } folder) return;
        var model = CatchUpModel(provider);
        // The last run failed: its card goes with it.
        LiveChatRun?.FlushTimer?.Stop();
        var run = new LiveChatRun(Guid.NewGuid(), question);
        LiveChatRun = run;
        // Read on the UI thread: the settings and the provider configs belong to it.
        var config = ProvidersModel.Config(provider.Id);
        var title = RecordingTitle ?? _recordingEvent?.Title ?? "";
        var elapsed = RecordingSnapshot.Elapsed;
        var language = OutputLanguage.Answer(Settings.SummaryLanguage);
        var history = PromptBuilder.HistoryTurns(LiveChatMessages, Settings.ChatContextMessages);
        _ = RunLiveChatAsync(run, provider, config, transcriber, folder, title, elapsed, model, language, history);
    }

    /// <summary>
    /// Ends the running live chat answer early: the question and the text streamed until then are saved; nothing when no
    /// text came yet.
    /// </summary>
    public void StopLiveChat()
    {
        if (LiveChatRun is not { IsRunning: true, Stopping: false } run) return;
        run.Stopping = true;
        OnPropertyChanged(nameof(LiveChatRun));
        _services.AI.Stop(run.JobId);
    }

    /// <summary>
    /// A recording starts: its page shows none of the previous recording's live chat, on its first tab (Catchup side by
    /// side), and the side-by-side layout hides a shown sidebar until its end, through the user's path, so an open library
    /// chat closes (a sidebar already hidden stays so at the end).
    /// </summary>
    private void ResetRecordingPage()
    {
        LiveChatRun?.FlushTimer?.Stop();
        LiveChatRun = null;
        LiveChatMessages = [];
        var sideBySide = Settings.RecordingLayout == RecordingLayout.SideBySide;
        RecordingTab = sideBySide ? RecordingTab.CatchUp : RecordingTab.Transcript;
        _sidebarAutoHidden = sideBySide && !SidebarHidden;
        if (_sidebarAutoHidden) SetSidebarHidden(true);
    }

    /// <summary>
    /// The end of a recording (or its failed start): the sidebar the side-by-side layout hid at its start shows again,
    /// unless the user toggled it since.
    /// </summary>
    private void RestoreAutoHiddenSidebar()
    {
        if (!_sidebarAutoHidden) return;
        _sidebarAutoHidden = false;
        SetSidebarHidden(false);
    }

    /// <summary>The recording's <c>chat.json</c> into <see cref="LiveChatMessages"/>, read off the UI thread, while <paramref name="folder"/> is still the recording's.</summary>
    private async Task LoadLiveChatAsync(string folder)
    {
        IReadOnlyList<ChatLog.Message> messages;
        try
        {
            messages = await Task.Run(() => ChatLog.Read(folder).Messages);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read the live chat of {folder}: {e.GetType().Name}");
            return;
        }
        if (_closing || _activeFolder != folder) return;
        LiveChatMessages = messages;
    }

    private async Task RunLiveChatAsync(
        LiveChatRun run, AIProviderInfo provider, ProviderConfig? config, RecordingTranscriber transcriber, string folder,
        string title, double elapsed, string model, OutputLanguage? language, List<LLMMessage> history)
    {
        // Counted with the catch-ups: quit stops it as it stops them (ShutdownAI).
        _runningCatchUps++;
        try
        {
            ILLMProvider? made;
            try
            {
                // Reads Credential Manager.
                made = await Task.Run(() => MakeClient(provider, config));
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not make the live chat client of {provider.Id}: {e.GetType().Name}");
                FailLiveChat(run, Loc.Format("%@: %@", provider.Name, ProvidersViewModel.ShortMessage(e)));
                return;
            }
            if (made is not { } client)
            {
                FailLiveChat(run, Loc.Format("%@ is not available.", provider.Name));
                return;
            }
            var question = run.Question;
            List<LLMMessage> messages;
            try
            {
                messages = await Task.Run(async () =>
                {
                    var lines = transcriber.FinalizedLines(null).Select(l => new CatchUpLine(
                        l.Id, l.Start, l.Speaker == RecordingTranscriber.Speaker.Me ? CatchUpSpeaker.Me : CatchUpSpeaker.Others,
                        l.Text)).ToList();
                    var catchUps = CatchUpLog.Load(folder).CatchUps;
                    var startedAt = RecordingSession.Read(folder).StartedAt;
                    var context = await client.ContextWindowAsync(model);
                    var maxCharacters = LiveChatPlan.CharacterBudget(
                        context, lines, title, startedAt, elapsed, question, history, language);
                    var plan = LiveChatPlan.Make(lines, catchUps, maxCharacters);
                    return PromptBuilder.LiveQuestionMessages(plan, title, startedAt, elapsed, question, history, language);
                });
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not plan the live chat of {folder}: {e.GetType().Name}");
                FailLiveChat(run, ProvidersViewModel.ShortMessage(e));
                return;
            }
            if (_closing || !ReferenceEquals(LiveChatRun, run)) return;
            // Stopped before anything was sent: nothing to save.
            if (run.Stopping)
            {
                LiveChatRun = null;
                return;
            }
            try
            {
                var stream = _services.AI.AskLiveAsync(question, messages, client, provider.Id, model, folder, jobId: run.JobId);
                await StreamLiveChatAsync(run, stream);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: the live chat of {folder} failed: {e.GetType().Name}");
                FailLiveChat(run, ProvidersViewModel.ShortMessage(e));
                return;
            }
            await FinishLiveChatAsync(run, folder);
        }
        finally
        {
            _runningCatchUps--;
        }
    }

    /// <summary>Applies the updates of <paramref name="run"/> on the UI thread until its stream ends.</summary>
    private async Task StreamLiveChatAsync(LiveChatRun run, IAsyncEnumerable<AIUpdate> stream)
    {
        await using var updates = stream.GetAsyncEnumerator();
        // The first MoveNextAsync registers the job, so Stop sees it from here; a Stop asked before stops it now.
        var next = updates.MoveNextAsync();
        if (run.Stopping) _services.AI.Stop(run.JobId);
        while (await next)
        {
            ApplyLiveChatUpdate(run, updates.Current);
            next = updates.MoveNextAsync();
        }
    }

    /// <summary>Shows an update of <paramref name="run"/> while it is <see cref="LiveChatRun"/>, flushed as <see cref="ApplyCatchUpUpdate"/> does.</summary>
    private void ApplyLiveChatUpdate(LiveChatRun run, AIUpdate update)
    {
        if (_closing || !ReferenceEquals(LiveChatRun, run)) return;
        if (update.Kind == AIUpdateKind.Status)
        {
            run.StatusLine = Loc.T(update.Value);
            LiveChatTextChanged?.Invoke();
            return;
        }
        run.PendingText.Append(update.Value);
        if (run.Status != LiveChatStatus.Streaming)
        {
            run.Status = LiveChatStatus.Streaming;
            run.StatusLine = null;
            FlushLiveChatText(run);
            OnPropertyChanged(nameof(LiveChatRun));
            return;
        }
        if (run.FlushTimer is null)
        {
            var timer = _dispatcher.CreateTimer();
            timer.Interval = AITextFlushInterval;
            timer.IsRepeating = false;
            timer.Tick += (_, _) => FlushLiveChatText(run);
            run.FlushTimer = timer;
        }
        if (!run.FlushTimer.IsRunning) run.FlushTimer.Start();
    }

    /// <summary>Moves the batched text of <paramref name="run"/> into its <see cref="LiveChatRun.Text"/>.</summary>
    private void FlushLiveChatText(LiveChatRun run)
    {
        run.FlushTimer?.Stop();
        if (run.PendingText.Length == 0) return;
        run.Text += run.PendingText.ToString();
        run.PendingText.Clear();
        if (!_closing && ReferenceEquals(LiveChatRun, run)) LiveChatTextChanged?.Invoke();
    }

    /// <summary>
    /// <paramref name="run"/> ended (saved, or stopped before any text): <see cref="LiveChatMessages"/> from its
    /// <c>chat.json</c>, read off the UI thread, replaces its answer in the same update.
    /// </summary>
    private async Task FinishLiveChatAsync(LiveChatRun run, string folder)
    {
        if (_closing) return;
        // Before the guards, which fail once a new recording has started: an answer still writing at Stop reaches the
        // finished conversation's Chat tab.
        ChatHistoryChanges++;
        if (!ReferenceEquals(LiveChatRun, run)) return;
        FlushLiveChatText(run);
        IReadOnlyList<ChatLog.Message>? messages;
        try
        {
            messages = await Task.Run(() => ChatLog.Read(folder).Messages);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read the live chat of {folder}: {e.GetType().Name}");
            messages = null;
        }
        if (_closing || !ReferenceEquals(LiveChatRun, run)) return;
        if (messages is not null) LiveChatMessages = messages;
        LiveChatRun = null;
    }

    /// <summary><paramref name="run"/> failed: its answer keeps the text streamed so far and shows <paramref name="message"/>; asking runs again.</summary>
    private void FailLiveChat(LiveChatRun run, string message)
    {
        if (_closing || !ReferenceEquals(LiveChatRun, run)) return;
        FlushLiveChatText(run);
        run.Status = LiveChatStatus.Failed;
        run.Error = message;
        OnPropertyChanged(nameof(LiveChatRun));
    }
}
