using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using EchoDiar.Platform.SystemServices;
using Microsoft.UI.Dispatching;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// Meeting detection and the end monitor (macOS AppModel, "Meetings"): the detector's prompt or automatic start, the
/// end monitor of the recording in progress (its feeds, its 1 s evaluation, the end prompt's countdown), the lost-mic
/// question, and screen lock and sleep. Started at launch (<see cref="StartMeetingServices"/>), stopped first thing
/// at <see cref="Shutdown"/>. UI thread only; the detector's and the watches' events are posted to it.
/// </summary>
public sealed partial class AppViewModel
{
    /// <summary>"Recording &lt;App&gt;" stays on the pill this long after an automatic start.</summary>
    private static readonly TimeSpan AutoStartNoticeDuration = TimeSpan.FromSeconds(10);

    private MeetingDetector? _meetingDetector;
    private SystemEvents? _systemEvents;
    private bool _detectorRunning;

    /// <summary>Group exes of the meeting apps whose mic is on, per the detector (every known app, ignored ones too).</summary>
    private readonly HashSet<string> _meetingMics = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Group exes the detector reports in use, updated as it raises its events (its thread), before they are posted:
    /// a Detected whose Ended is already queued behind it neither starts a recording nor asks. Guarded by itself.
    /// </summary>
    private readonly HashSet<string> _raisedMeetings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The meeting app whose mic use was detected last; null once its mic is off.</summary>
    private DetectedMeeting? _detectedMeeting;

    private EndWatch? _endWatch;
    private int _endWatchGeneration;
    private DispatcherQueueTimer? _autoStartNoticeTimer;

    /// <summary>The end monitor and its feeds for the recording in progress (macOS <c>EndWatch</c>).</summary>
    private sealed class EndWatch(MeetingEndMonitor monitor, DispatcherQueueTimer timer)
    {
        public MeetingEndMonitor Monitor { get; } = monitor;

        /// <summary>The 1 s evaluation.</summary>
        public DispatcherQueueTimer Timer { get; } = timer;

        /// <summary>Cancels the source's exit watch.</summary>
        public CancellationTokenSource ExitWatch { get; } = new();

        public ProcessActivityWatch? Activity { get; set; }

        /// <summary>The last <c>Audible</c> fed (the monitor starts silent).</summary>
        public bool Audible { get; set; }

        /// <summary>The last <c>SystemAudible</c> fed.</summary>
        public bool SystemAudible { get; set; }

        /// <summary>The last <c>Paused</c> fed.</summary>
        public bool Paused { get; set; }

        /// <summary>The meeting app whose mic feeds <c>SourceInputActive</c>: the recorded app's exe, null (any known one) for all system audio.</summary>
        public string? MeetingExe { get; set; }

        /// <summary>The last <c>SourceInputActive</c> fed; off is fed only after on.</summary>
        public bool MeetingMic { get; set; }
    }

    /// <summary>
    /// The apps Settings › Recording › Meetings lists, one per <see cref="MeetingApp.AppExe"/>: the detector's table (with
    /// the DEBUG <c>--meeting-app</c> stand-in), else the known apps.
    /// </summary>
    public IReadOnlyList<MeetingApp> DetectableMeetingApps =>
        [.. (_meetingDetector?.Apps ?? MeetingApps.Known).DistinctBy(a => a.AppExe, StringComparer.OrdinalIgnoreCase)];

    /// <summary>"Record" and "Always for &lt;App&gt;" on the detection prompt: records the detected app's processes.</summary>
    public void RecordDetectedMeeting(bool always = false)
    {
        if (_closing || PillPrompt is not PillPrompt.MeetingDetected asking) return;
        PillPrompt = null;
        var app = asking.Meeting.App;
        // Never for browsers: any web page using the mic would start a recording.
        if (always && !app.IsBrowser)
        {
            SaveSettings(Settings with { PerAppBehavior = new(Settings.PerAppBehavior) { [app.AppExe] = MeetingBehavior.Always } });
        }
        _ = StartRecordingAsync(asking.Meeting.Group);
    }

    /// <summary>"Not now" on the detection prompt.</summary>
    public void DismissMeetingPrompt()
    {
        if (PillPrompt is PillPrompt.MeetingDetected) PillPrompt = null;
    }

    /// <summary>"Keep recording" on the end prompt: that trigger stays quiet for 10 min.</summary>
    public void KeepRecording()
    {
        if (_closing || PillPrompt is not PillPrompt.MeetingEnded ended) return;
        StartupTrace.Write($"end prompt kept: {ended.Reason}");
        PillPrompt = null;
        _endWatch?.Monitor.KeepRecording(ended.Reason, DateTimeOffset.Now);
        EvaluateEnd();
    }

    /// <summary>"Stop now" on the end prompt, the end of its countdown, and a lost mic with the "stop" route setting.</summary>
    public void StopFromEndPrompt()
    {
        StartupTrace.Write($"end prompt stop: {(PillPrompt as PillPrompt.MeetingEnded)?.Reason.ToString() ?? "no prompt"}");
        EndEndWatch();
        _ = StopRecordingAsync();
    }

    /// <summary>
    /// Sleep and lock events and meeting detection, once the settings are loaded; on the normal launch path only (the
    /// DEBUG harnesses never create the model). UI thread: <see cref="SystemEvents"/> lives on it.
    /// </summary>
    private void StartMeetingServices()
    {
        if (_closing || _meetingDetector is not null) return;
        try
        {
            var events = new SystemEvents(Trace);
            events.ScreenLocked += locked => RecordEnd(new MeetingEndMonitor.Input.ScreenLocked(locked));
            events.SystemSleep += OnSystemSleep;
            _systemEvents = events;
        }
        catch (Exception e)
        {
            // The app runs on without lock and sleep events.
            Debug.WriteLine($"EchoDiar: could not listen to system events: {e}");
        }
        var detector = new MeetingDetector();
#if DEBUG
        detector.Log = message => StartupTrace.Write(message);
#endif
        detector.Detected += meeting =>
        {
            lock (_raisedMeetings) _raisedMeetings.Add(meeting.Exe);
            Post(() => OnMeetingDetected(meeting));
        };
        detector.Ended += meeting =>
        {
            lock (_raisedMeetings) _raisedMeetings.Remove(meeting.Exe);
            Post(() => OnMeetingEnded(meeting));
        };
        _meetingDetector = detector;
        SyncMeetingDetection();
    }

    /// <summary>Stops every watcher (detector, system events, the end watch); first thing at quit, on the UI thread.</summary>
    private void StopMeetingServices()
    {
        EndEndWatch();
        _autoStartNoticeTimer?.Stop();
        try
        {
            _meetingDetector?.Dispose();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not stop meeting detection: {e}");
        }
        _meetingDetector = null;
        _detectorRunning = false;
        try
        {
            _systemEvents?.Dispose();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not stop the system events: {e}");
        }
        _systemEvents = null;
    }

    /// <summary>Runs the detector while <c>meetingDetection</c> is on; off forgets the detected app and its prompt.</summary>
    private void SyncMeetingDetection()
    {
        if (_meetingDetector is not { } detector || _closing) return;
        var wanted = Settings.MeetingDetection;
        if (wanted == _detectorRunning) return;
        try
        {
            if (wanted) detector.Start();
            else detector.Stop();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not {(wanted ? "start" : "stop")} meeting detection: {e}");
            return;
        }
        _detectorRunning = wanted;
        if (wanted) return;
        // The stopped detector raises no Ended: the monitor keeps the last mic state.
        _meetingMics.Clear();
        lock (_raisedMeetings) _raisedMeetings.Clear();
        _detectedMeeting = null;
        DismissMeetingPrompt();
    }

    /// <summary>
    /// "Ignore" apps are skipped; "Always" starts recording them (browsers still ask), else the pill asks. Nothing
    /// starts while recording.
    /// </summary>
    private void OnMeetingDetected(DetectedMeeting meeting)
    {
        if (!_detectorRunning) return;
        StartupTrace.Write($"meeting detected: {meeting.Exe} ({meeting.Name})");
        _meetingMics.Add(meeting.Exe);
        SyncMeetingMic();
        var behavior = Settings.PerAppBehavior.GetValueOrDefault(meeting.App.AppExe, MeetingBehavior.Ask);
        if (behavior == MeetingBehavior.Ignore) return;
        if (behavior == MeetingBehavior.Always && meeting.App.IsBrowser) behavior = MeetingBehavior.Ask;
        _detectedMeeting = meeting;
        if (IsRecording || IsStarting) return;
        // Its mic went off before this post ran (its Ended is queued): nothing to record.
        lock (_raisedMeetings)
        {
            if (!_raisedMeetings.Contains(meeting.Exe)) return;
        }
        if (behavior == MeetingBehavior.Always)
        {
            AutoStartedAppName = meeting.Name;
            _ = StartRecordingAsync(meeting.Group);
        }
        else
        {
            PillPrompt = new PillPrompt.MeetingDetected(meeting);
        }
    }

    /// <summary>
    /// The app's mic is off (call over, or a muted web client): forget it and drop its prompt. Never an end signal
    /// alone: the call-ended trigger also needs the system channel silent.
    /// </summary>
    private void OnMeetingEnded(DetectedMeeting meeting)
    {
        if (!_detectorRunning) return;
        StartupTrace.Write($"meeting ended: {meeting.Exe}");
        _meetingMics.Remove(meeting.Exe);
        SyncMeetingMic();
        if (!string.Equals(_detectedMeeting?.Exe, meeting.Exe, StringComparison.OrdinalIgnoreCase)) return;
        _detectedMeeting = null;
        if (PillPrompt is PillPrompt.MeetingDetected shown && string.Equals(shown.Meeting.Exe, meeting.Exe, StringComparison.OrdinalIgnoreCase))
        {
            PillPrompt = null;
        }
    }

    /// <summary>
    /// Feeds the call-ended trigger whether the recorded meeting app's mic is on (any known app's for all system
    /// audio). Off is fed only after on, so a recording that never saw a meeting mic never gets the prompt.
    /// </summary>
    private void SyncMeetingMic()
    {
        if (_endWatch is not { } watch) return;
        var active = watch.MeetingExe is { } exe ? _meetingMics.Contains(exe) : _meetingMics.Count > 0;
        if (active == watch.MeetingMic) return;
        watch.MeetingMic = active;
        RecordEnd(new MeetingEndMonitor.Input.SourceInputActive(active));
    }

    /// <summary>
    /// Starts the end monitor of the recording that just started on <paramref name="target"/>: a 1 s evaluation timer
    /// and, for an app, its root process's exit and its audio activity.
    /// </summary>
    private void BeginEndWatch(CaptureTarget target)
    {
        var autoStarted = AutoStartedAppName;
        // The engine may already have stopped (disk full), its end snapshot handled.
        if (_closing || _services.Engine.CurrentSnapshot().State == RecordingEngine.State.Idle)
        {
            AutoStartedAppName = null;
            return;
        }
        EndEndWatch();
        DismissMeetingPrompt();
        var generation = ++_endWatchGeneration;
        var now = DateTimeOffset.Now;
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => EndTick();
        var watch = new EndWatch(new MeetingEndMonitor(Settings.EndMonitor, now), timer);
        if (target.App is { } group)
        {
            watch.MeetingExe = group.Exe;
            _ = WatchSourceExitAsync(group.RootPid, now, generation, watch.ExitWatch.Token);
            try
            {
                var activity = new ProcessActivityWatch(group.RootPid);
                activity.Changed += changed =>
                {
                    var running = changed.IsRunning;
                    Post(() => RecordEnd(new MeetingEndMonitor.Input.SourceActive(running), generation));
                };
                activity.Start();
                watch.Activity = activity;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not watch the source's audio activity: {e}");
            }
        }
        _endWatch = watch;
        timer.Start();
        SyncMeetingMic();
        if (autoStarted is not null) ShowAutoStartNotice(autoStarted);
    }

    /// <summary>The source's root process exited (at once when it is already gone): <c>SourceExited</c>.</summary>
    private async Task WatchSourceExitAsync(int rootPid, DateTimeOffset startedAt, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await SystemEvents.WatchProcessExitAsync(rootPid, startedAt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not watch the source's exit: {e}");
            return;
        }
        Post(() => RecordEnd(new MeetingEndMonitor.Input.SourceExited(), generation));
    }

    /// <summary>Stops the timer and the feeds, and drops the end prompt. Safe to call repeatedly.</summary>
    private void EndEndWatch()
    {
        if (_endWatch is { } watch)
        {
            _endWatch = null;
            watch.Timer.Stop();
            // Not disposed: the exit watch may still be reading its token.
            watch.ExitWatch.Cancel();
            try
            {
                watch.Activity?.Dispose();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not stop the activity watch: {e}");
            }
        }
        _autoStartNoticeTimer?.Stop();
        AutoStartedAppName = null;
        if (PillPrompt is PillPrompt.MeetingEnded) PillPrompt = null;
    }

    /// <summary>Keeps "Recording &lt;App&gt;" on the pill for 10 s after an automatic start.</summary>
    private void ShowAutoStartNotice(string name)
    {
        AutoStartedAppName = name;
        if (_autoStartNoticeTimer is null)
        {
            _autoStartNoticeTimer = _dispatcher.CreateTimer();
            _autoStartNoticeTimer.Interval = AutoStartNoticeDuration;
            _autoStartNoticeTimer.IsRepeating = false;
            _autoStartNoticeTimer.Tick += (_, _) =>
            {
                if (!_closing) AutoStartedAppName = null;
            };
        }
        _autoStartNoticeTimer.Stop();
        _autoStartNoticeTimer.Start();
    }

    /// <summary><paramref name="generation"/> (from a feed armed by <see cref="BeginEndWatch"/>) drops inputs meant for an earlier recording.</summary>
    private void RecordEnd(MeetingEndMonitor.Input input, int? generation = null)
    {
        if (_closing || _endWatch is not { } watch || (generation is { } g && g != _endWatchGeneration)) return;
        watch.Monitor.Record(input, DateTimeOffset.Now);
        EvaluateEnd();
    }

    /// <summary>Shows or dismisses the end prompt, or stops now. A shown prompt keeps its deadline while its reason holds.</summary>
    private void EvaluateEnd()
    {
        if (_endWatch is not { } watch) return;
        var now = DateTimeOffset.Now;
        switch (watch.Monitor.Evaluate(now))
        {
            case MeetingEndMonitor.Decision.PromptStop prompt:
                if (PillPrompt is PillPrompt.MeetingEnded shown && shown.Reason == prompt.Reason) return;
                PillPrompt = new PillPrompt.MeetingEnded(prompt.Reason, now.AddSeconds(prompt.Countdown));
                StartupTrace.Write($"end prompt: {prompt.Reason}");
                break;
            case MeetingEndMonitor.Decision.StopNow stop:
                StartupTrace.Write($"recording stopped: {stop.Reason}");
                Debug.WriteLine($"EchoDiar: recording stopped: {stop.Reason}");
                EndEndWatch();
                if (stop.Reason == MeetingEndMonitor.Reason.SystemSleep) StopNowForSleep();
                else _ = StopRecordingAsync();
                break;
            default:
                if (PillPrompt is PillPrompt.MeetingEnded dismissed)
                {
                    StartupTrace.Write($"end prompt dismissed: {dismissed.Reason}");
                    PillPrompt = null;
                }
                break;
        }
    }

    /// <summary>
    /// Every second while recording: feeds <c>Audible</c> (paused counts as silent: wall clock), <c>Paused</c> and
    /// <c>SystemAudible</c> from the writer's silence runs, evaluates, then stops once a still-shown end prompt's
    /// countdown is over.
    /// </summary>
    private void EndTick()
    {
        if (_closing || _endWatch is not { } watch) return;
        var now = DateTimeOffset.Now;
        var snapshot = _services.Engine.CurrentSnapshot();
        var recording = snapshot.State == RecordingEngine.State.Recording;
        var quiet = Math.Min(snapshot.MicSilentSeconds, snapshot.SystemSilentSeconds);
        // A silence run as long as the recording means nothing rose above the floor yet.
        var audible = recording && quiet < 2 && quiet + 0.5 < snapshot.Elapsed;
        if (audible != watch.Audible)
        {
            watch.Audible = audible;
            var since = audible || !recording ? now : now.AddSeconds(-quiet);
            watch.Monitor.Record(new MeetingEndMonitor.Input.Audible(audible), since);
        }
        var paused = !recording;
        if (paused != watch.Paused)
        {
            watch.Paused = paused;
            watch.Monitor.Record(new MeetingEndMonitor.Input.Paused(paused), now);
        }
        // Not fed while paused: the call-ended trigger does not count a pause as silence.
        var systemQuiet = snapshot.SystemSilentSeconds;
        var systemAudible = systemQuiet < 2 && systemQuiet + 0.5 < snapshot.Elapsed;
        if (!paused && systemAudible != watch.SystemAudible)
        {
            watch.SystemAudible = systemAudible;
            var since = systemAudible ? now : now.AddSeconds(-systemQuiet);
            watch.Monitor.Record(new MeetingEndMonitor.Input.SystemAudible(systemAudible), since);
        }
        EvaluateEnd();
        // Only a prompt that still holds after the fresh inputs stops the recording.
        if (PillPrompt is PillPrompt.MeetingEnded ended && now >= ended.Deadline) StopFromEndPrompt();
    }

    /// <summary>
    /// The system is about to sleep: the recording stops now, synchronously (little time is left). Before its end
    /// watch is armed, the engine is stopped directly.
    /// </summary>
    private void OnSystemSleep()
    {
        if (_closing) return;
        if (_endWatch is not null)
        {
            RecordEnd(new MeetingEndMonitor.Input.SystemSleep());
        }
        else if (_services.Engine.CurrentSnapshot().State != RecordingEngine.State.Idle)
        {
            StartupTrace.Write($"recording stopped: {MeetingEndMonitor.Reason.SystemSleep}");
            StopNowForSleep();
        }
    }

    /// <summary>The engine stops on this thread, so its audio file is finished before the system sleeps.</summary>
    private void StopNowForSleep()
    {
        try
        {
            _services.Engine.Stop();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not stop recording before sleep: {e}");
        }
    }

    private static void Trace(string message)
    {
#if DEBUG
        StartupTrace.Write(message);
#endif
    }
}
