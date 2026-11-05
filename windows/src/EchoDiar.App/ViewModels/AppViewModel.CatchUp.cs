using System.Diagnostics;
using System.Globalization;
using System.Text;
using EchoDiar.AI;
using EchoDiar.App.Views;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;
using EchoDiar.Transcribe;
using Microsoft.UI.Dispatching;

namespace EchoDiar.App.ViewModels;

/// <summary>Where a <see cref="CatchUpRun"/> is (macOS <c>CatchUpRun.Status</c>).</summary>
public enum CatchUpStatus { Starting, Streaming, Failed }

/// <summary>
/// A catch-up of the current recording (<see cref="AppViewModel.StartCatchUp"/>; macOS <c>AppModel.CatchUpRun</c>): its
/// <see cref="AIService"/> job id, for <see cref="AIService.Stop(Guid)"/>, and what its card shows. Changed in place on
/// the UI thread: <see cref="AppViewModel.CatchUpRun"/> is raised when it starts, gets its first text, is stopping, fails
/// or ends; <see cref="AppViewModel.CatchUpTextChanged"/> when its text or status line changes.
/// </summary>
public sealed class CatchUpRun(Guid jobId, double elapsed, string providerName, bool providerLeavesPC, string model)
{
    public Guid JobId { get; } = jobId;

    /// <summary>Seconds of recording when it was asked for, for its card.</summary>
    public double Elapsed { get; } = elapsed;

    /// <summary>The provider's name and the model it runs, for its card's header.</summary>
    public string ProviderName { get; } = providerName;

    /// <summary>What it sends leaves this PC (<see cref="AIProviderInfo.LeavesPC"/>), which its card's header says.</summary>
    public bool ProviderLeavesPC { get; } = providerLeavesPC;

    public string Model { get; } = model;

    public CatchUpStatus Status { get; internal set; } = CatchUpStatus.Starting;

    /// <summary>The text streamed so far (every 120 ms), Markdown.</summary>
    public string Text { get; internal set; } = "";

    /// <summary>What the job is doing before its text starts.</summary>
    public string? StatusLine { get; internal set; }

    public string? Error { get; internal set; }

    /// <summary>Stop was asked for: the text streamed until then is saved, marked stopped.</summary>
    public bool Stopping { get; internal set; }

    public bool IsRunning => Status != CatchUpStatus.Failed;

    /// <summary>Streamed text not in <see cref="Text"/> yet, and its scheduled flush.</summary>
    internal StringBuilder PendingText { get; } = new();

    internal DispatcherQueueTimer? FlushTimer { get; set; }
}

/// <summary>
/// Catch-ups while recording (macOS AppModel's <c>// MARK: - Catch-up</c>): a quick summary of the meeting so far, on a
/// provider outside the app, streamed into <see cref="CatchUpRun"/>, then appended to the recording's
/// <c>catchups.json</c> and listed in <see cref="RecordingCatchUps"/>. Apart from <see cref="AIJobs"/>, so the summary
/// and a catch-up never stop each other. On the UI thread; files, the client and the plan are made off it.
/// </summary>
public sealed partial class AppViewModel
{
    private CatchUpRun? _catchUpRun;
    private IReadOnlyList<CatchUp> _recordingCatchUps = [];

    /// <summary>Catch-up jobs not ended yet, also of an earlier recording: quit stops them (<see cref="ShutdownAI"/>).</summary>
    private int _runningCatchUps;

    /// <summary>
    /// The current recording's catch-up being written (or the last one that failed); null otherwise. Cleared when a new
    /// recording starts: a run still writing for the previous one finishes and saves in its folder, out of sight.
    /// </summary>
    public CatchUpRun? CatchUpRun
    {
        get => _catchUpRun;
        private set => SetProperty(ref _catchUpRun, value);
    }

    /// <summary>The current recording's saved catch-ups (its <c>catchups.json</c>), oldest first.</summary>
    public IReadOnlyList<CatchUp> RecordingCatchUps
    {
        get => _recordingCatchUps;
        private set => SetProperty(ref _recordingCatchUps, value);
    }

    /// <summary>Raised on the UI thread when <see cref="CatchUpRun"/>'s text or status line changed (at most every 120 ms).</summary>
    public event Action? CatchUpTextChanged;

    /// <summary>
    /// Where a catch-up runs: the "Catch-up provider" setting when it is a usable provider outside the app, else
    /// <see cref="AutomaticCatchUpProvider"/>; null when there is none.
    /// Never an in-app model: the live transcriber holds the model gate for the whole recording.
    /// </summary>
    public AIProviderInfo? CatchUpProvider
    {
        get
        {
            var external = ProvidersModel.AIProviders
                .Where(p => p.Kind != AIProviderKind.InApp && p.UnavailableReason is null).ToList();
            if (external.FirstOrDefault(p => p.Id == Settings.CatchUpProviderID) is { } picked) return picked;
            return AutomaticCatchUpProvider;
        }
    }

    /// <summary>
    /// What <see cref="CatchUpProvider"/> runs with no usable provider picked (macOS <c>automaticCatchUpProvider</c>): the
    /// AI tab's provider when it is not in-app, else the first usable provider outside the app not in
    /// <see cref="AppSettings.AutoExcludedProviderIDs"/>. <see cref="ProvidersViewModel.CurrentAIProvider"/> is never an
    /// excluded one by accident: the migration clears <c>DefaultAIProviderID</c> for those, and picking one un-excludes it.
    /// </summary>
    public AIProviderInfo? AutomaticCatchUpProvider
    {
        get
        {
            if (ProvidersModel.CurrentAIProvider is { Kind: not AIProviderKind.InApp } main) return main;
            return ProvidersModel.AIProviders.FirstOrDefault(p => p.Kind != AIProviderKind.InApp
                && p.UnavailableReason is null && !Settings.AutoExcludedProviderIDs.Contains(p.Id));
        }
    }

    /// <summary>The "Catch-up model" setting when <paramref name="provider"/> is the picked catch-up provider, else its AI tab model.</summary>
    public string CatchUpModel(AIProviderInfo provider)
    {
        var picked = Settings.CatchUpModel.Trim();
        if (picked.Length > 0 && provider.Id == Settings.CatchUpProviderID) return picked;
        return ProvidersModel.AIModelFor(provider);
    }

    /// <summary>
    /// Why "Catch up" cannot run now; null when it can. "Nothing new" follows <see cref="CatchUpPlan.Make"/>: no live line
    /// after the newest catch-up that was not stopped. Computed: read it again when <see cref="RecordingCaptions"/>,
    /// <see cref="Settings"/>, <see cref="CatchUpRun"/> or <see cref="RecordingCatchUps"/> change, and on
    /// <see cref="ProvidersViewModel.Changed"/>.
    /// </summary>
    public string? CatchUpDisabledReason
    {
        get
        {
            if (RecordingCaptions is not { } captions || _recordingTranscriber is null)
                return RecordingView.NoCaptionsReason(this);
            if (CatchUpRun is { IsRunning: true }) return Loc.T("A catch-up is being written.");
            if (CatchUpProvider is null)
            {
                return Loc.T("Catch up needs an AI provider outside the app: a cloud API, a local server or a command-line tool. Add one in Settings › AI.");
            }
            var @base = RecordingCatchUps.LastOrDefault(c => !c.Stopped);
            var newest = captions.Lines.Count > 0 ? captions.Lines[captions.Lines.Count - 1].Id : -1;
            if (newest <= (@base?.ThroughLineID ?? -1))
            {
                return RecordingCatchUps.Count == 0
                    ? Loc.T("Nothing to catch up on yet.")
                    : Loc.T("Nothing new since the last catch-up");
            }
            return null;
        }
    }

    /// <summary>
    /// The help of "Catch up" when it can run: what it does, the provider it runs on and, as the AI pane's data notes,
    /// whether the data leaves this PC and how far a CLI agent is contained (macOS <c>CatchUpButton.help</c>). Computed,
    /// as <see cref="CatchUpDisabledReason"/>.
    /// </summary>
    public string CatchUpHelp
    {
        get
        {
            List<string> lines = [Loc.T("Summarize the meeting so far, mostly its last 5 minutes")];
            if (CatchUpProvider is { } provider)
            {
                lines.Add(Loc.Format("%@: %@", Loc.T("Catch-up provider"), provider.Name));
                if (provider.LeavesPCNote is { } leaves) lines.Add(leaves);
                if (provider.ContainmentNote is { } containment) lines.Add(containment);
            }
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Summarizes the current recording so far with <see cref="CatchUpProvider"/>, streamed into <see cref="CatchUpRun"/>
    /// (its card shows at once), then appended to the recording's <c>catchups.json</c> and shown in
    /// <see cref="RecordingCatchUps"/>. Incremental unless "from the start" is on (<see cref="CatchUpPlan.Make"/>), within
    /// the provider's context window. The client, the live lines, the files and the plan are made off the UI thread. A
    /// run still going at Stop finishes and saves in that recording's folder.
    /// </summary>
    public void StartCatchUp()
    {
        if (_closing || CatchUpDisabledReason is not null || CatchUpProvider is not { } provider
            || _recordingTranscriber is not { } transcriber || _activeFolder is not { } folder) return;
        var model = CatchUpModel(provider);
        var run = new CatchUpRun(Guid.NewGuid(), RecordingSnapshot.Elapsed, provider.Name, provider.LeavesPC, model);
        CatchUpRun = run;
        // Read on the UI thread: the settings and the provider configs belong to it.
        var config = ProvidersModel.Config(provider.Id);
        var title = RecordingTitle ?? _recordingEvent?.Title ?? "";
        var language = OutputLanguage.CatchUp(Settings.SummaryLanguage, RecordingLanguage);
        var fromStart = Settings.CatchUpFromStart;
        _ = RunCatchUpAsync(run, provider, config, transcriber, folder, title, language, fromStart);
    }

    /// <summary>
    /// Ends the running catch-up early: the text streamed until then is saved, marked stopped; nothing when no text came
    /// yet. Only its own job: a summary keeps running.
    /// </summary>
    public void StopCatchUp()
    {
        if (CatchUpRun is not { IsRunning: true, Stopping: false } run) return;
        run.Stopping = true;
        OnPropertyChanged(nameof(CatchUpRun));
        _services.AI.Stop(run.JobId);
    }

    /// <summary>A new recording starts: its page shows none of the previous recording's catch-ups.</summary>
    private void ResetCatchUps()
    {
        CatchUpRun?.FlushTimer?.Stop();
        CatchUpRun = null;
        RecordingCatchUps = [];
    }

    private async Task RunCatchUpAsync(
        CatchUpRun run, AIProviderInfo provider, ProviderConfig? config, RecordingTranscriber transcriber, string folder,
        string title, OutputLanguage? language, bool fromStart)
    {
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
                Debug.WriteLine($"EchoDiar: could not make the catch-up client of {provider.Id}: {e.GetType().Name}");
                FailCatchUp(run, Loc.Format("%@: %@", provider.Name, ProvidersViewModel.ShortMessage(e)));
                return;
            }
            if (made is not { } client)
            {
                FailCatchUp(run, Loc.Format("%@ is not available.", provider.Name));
                return;
            }
            var elapsed = run.Elapsed;
            var model = run.Model;
            CatchUpLog log;
            CatchUpPlan? plan;
            List<LLMMessage>? messages;
            try
            {
                (log, plan, messages) = await Task.Run(async () =>
                {
                    var finalized = transcriber.FinalizedLines(null).Select(l => new CatchUpLine(
                        l.Id, l.Start, l.Speaker == RecordingTranscriber.Speaker.Me ? CatchUpSpeaker.Me : CatchUpSpeaker.Others,
                        l.Text)).ToList();
                    var saved = CatchUpLog.Load(folder);
                    var startedAt = RecordingSession.Read(folder).StartedAt;
                    var context = await client.ContextWindowAsync(model);
                    var maxCharacters = CatchUpCharacters(context, finalized, title, startedAt, elapsed, language);
                    var planned = CatchUpPlan.Make(finalized, saved, fromStart, maxCharacters);
                    var sent = planned is null ? null : PromptBuilder.CatchUpMessages(planned, title, startedAt, elapsed, language);
                    return (saved, planned, sent);
                });
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not plan the catch-up of {folder}: {e.GetType().Name}");
                FailCatchUp(run, ProvidersViewModel.ShortMessage(e));
                return;
            }
            if (_closing || !ReferenceEquals(CatchUpRun, run)) return;
            // Stopped before anything was sent: nothing to save.
            if (run.Stopping)
            {
                CatchUpRun = null;
                return;
            }
            if (plan is null || messages is null)
            {
                RecordingCatchUps = log.CatchUps;
                FailCatchUp(run, Loc.T("Nothing new since the last catch-up"));
                return;
            }
            try
            {
                var stream = _services.AI.CatchUpAsync(
                    plan, messages, client, provider.Id, model, elapsed, folder, jobId: run.JobId);
                await StreamCatchUpAsync(run, stream);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: the catch-up of {folder} failed: {e.GetType().Name}");
                FailCatchUp(run, ProvidersViewModel.ShortMessage(e));
                return;
            }
            await FinishCatchUpAsync(run, folder);
        }
        finally
        {
            _runningCatchUps--;
        }
    }

    /// <summary>Applies the updates of <paramref name="run"/> on the UI thread until its stream ends.</summary>
    private async Task StreamCatchUpAsync(CatchUpRun run, IAsyncEnumerable<AIUpdate> stream)
    {
        await using var updates = stream.GetAsyncEnumerator();
        // The first MoveNextAsync registers the job, so Stop sees it from here; a Stop asked before stops it now.
        var next = updates.MoveNextAsync();
        if (run.Stopping) _services.AI.Stop(run.JobId);
        while (await next)
        {
            ApplyCatchUpUpdate(run, updates.Current);
            next = updates.MoveNextAsync();
        }
    }

    /// <summary>
    /// The characters of live lines that fit <paramref name="contextWindow"/> beside the catch-up's instructions and
    /// header, as the summaries count them (<see cref="TranscriptChunker"/>: UTF-8 bytes / 4 per token, 20% kept for the
    /// answer); characters (text elements, as <see cref="CatchUpPlan.Make"/> counts them) per byte of
    /// <paramref name="lines"/>' text, so accented and CJK text count as many tokens as for a summary.
    /// </summary>
    private static int CatchUpCharacters(
        int contextWindow, IReadOnlyList<CatchUpLine> lines, string title, DateTimeOffset startedAt, double elapsed,
        OutputLanguage? language)
    {
        var empty = new CatchUpPlan(null, [], [], Omitted: true, FullPass: true, ThroughLineID: -1, ThroughTime: 0);
        var instructions = PromptBuilder.CatchUpMessages(empty, title, startedAt, elapsed, language)
            .Sum(m => TranscriptChunker.EstimateTokens(m.Content));
        var bytes = TranscriptChunker.Budget(contextWindow, instructions) * 4;
        var textBytes = lines.Sum(l => Encoding.UTF8.GetByteCount(l.Text));
        if (textBytes == 0) return bytes;
        var textCharacters = lines.Sum(l => new StringInfo(l.Text).LengthInTextElements);
        return (int)((double)bytes * textCharacters / textBytes);
    }

    /// <summary>
    /// Shows an update of <paramref name="run"/> while it is <see cref="CatchUpRun"/>: a status at once; the first text
    /// at once (the run is then streaming), then at most every 120 ms, as the card renders its whole Markdown again at
    /// each change (<see cref="ApplyAIUpdate"/>).
    /// </summary>
    private void ApplyCatchUpUpdate(CatchUpRun run, AIUpdate update)
    {
        if (_closing || !ReferenceEquals(CatchUpRun, run)) return;
        if (update.Kind == AIUpdateKind.Status)
        {
            run.StatusLine = Loc.T(update.Value);
            CatchUpTextChanged?.Invoke();
            return;
        }
        run.PendingText.Append(update.Value);
        if (run.Status != CatchUpStatus.Streaming)
        {
            run.Status = CatchUpStatus.Streaming;
            run.StatusLine = null;
            FlushCatchUpText(run);
            OnPropertyChanged(nameof(CatchUpRun));
            return;
        }
        if (run.FlushTimer is null)
        {
            var timer = _dispatcher.CreateTimer();
            timer.Interval = AITextFlushInterval;
            timer.IsRepeating = false;
            timer.Tick += (_, _) => FlushCatchUpText(run);
            run.FlushTimer = timer;
        }
        if (!run.FlushTimer.IsRunning) run.FlushTimer.Start();
    }

    /// <summary>Moves the batched text of <paramref name="run"/> into its <see cref="CatchUpRun.Text"/>.</summary>
    private void FlushCatchUpText(CatchUpRun run)
    {
        run.FlushTimer?.Stop();
        if (run.PendingText.Length == 0) return;
        run.Text += run.PendingText.ToString();
        run.PendingText.Clear();
        if (!_closing && ReferenceEquals(CatchUpRun, run)) CatchUpTextChanged?.Invoke();
    }

    /// <summary>
    /// <paramref name="run"/> ended (saved, or stopped before any text): <see cref="RecordingCatchUps"/> from its
    /// <c>catchups.json</c>, read off the UI thread, replaces its card in the same update.
    /// </summary>
    private async Task FinishCatchUpAsync(CatchUpRun run, string folder)
    {
        if (_closing || !ReferenceEquals(CatchUpRun, run)) return;
        FlushCatchUpText(run);
        CatchUpLog? log;
        try
        {
            log = await Task.Run(() => CatchUpLog.Load(folder));
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read the catch-ups of {folder}: {e.GetType().Name}");
            log = null;
        }
        if (_closing || !ReferenceEquals(CatchUpRun, run)) return;
        if (log is not null) RecordingCatchUps = log.CatchUps;
        CatchUpRun = null;
    }

    /// <summary><paramref name="run"/> failed: its card keeps the text streamed so far and shows <paramref name="message"/>; "Catch up" runs again.</summary>
    private void FailCatchUp(CatchUpRun run, string message)
    {
        if (_closing || !ReferenceEquals(CatchUpRun, run)) return;
        FlushCatchUpText(run);
        run.Status = CatchUpStatus.Failed;
        run.Error = message;
        OnPropertyChanged(nameof(CatchUpRun));
    }
}
