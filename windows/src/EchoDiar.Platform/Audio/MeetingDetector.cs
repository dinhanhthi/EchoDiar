using EchoDiar.Core;
using EchoDiar.Core.Audio;
using Windows.Win32.Media.Audio;

namespace EchoDiar.Platform.Audio;

/// <summary>A known meeting app whose mic capture has been active for at least 3 s (port of Swift <c>DetectedMeeting</c>).</summary>
/// <param name="App">The matched table entry: its name, <see cref="MeetingApp.AppExe"/> (the perAppBehavior key) and
/// <see cref="MeetingApp.IsBrowser"/>. Use it rather than <see cref="MeetingApps.ForExe"/>: the DEBUG
/// <c>--meeting-app</c> stand-in is only in the detector's table.</param>
/// <param name="Group">The app's processes when it was detected: <c>CaptureTarget.ForApp(Group)</c> records them.</param>
/// <param name="Pid">The process whose capture session is active (a browser's audio utility process, a WebView2
/// child, or the app itself).</param>
public sealed record DetectedMeeting(MeetingApp App, AudioProcessGroup Group, int Pid)
{
    public string Name => App.Name;

    /// <summary>The matched exe (one of <c>App.Exes</c>): the group's root exe.</summary>
    public string Exe => Group.Exe;

    /// <summary>The group's root process, the target of process loopback.</summary>
    public int RootPid => Group.RootPid;
}

/// <summary>
/// Watches known meeting apps' mic use (port of Swift <c>MeetingDetector</c>): the capture sessions of every active
/// capture endpoint (<see cref="AudioSessionWatch"/>: session notifications, state-change events and a 2 s poll,
/// refreshes debounced by 500 ms). Session processes are grouped by app as for "one app" capture
/// (<see cref="AudioProcessGrouping"/>, so WebView2 and browser utility processes count for their app); EchoDiar's
/// own process tree is left out. An app whose capture turns active is confirmed by a fresh read 3 s later:
/// <see cref="Detected"/>. <see cref="Ended"/> follows when none of its capture sessions is active any more, or its
/// processes are gone.
/// <para>
/// Events are raised on a thread-pool thread, one at a time and in order, outside the detector's lock. A handler
/// must not block (dispatch to the UI thread with TryEnqueue); it may call <see cref="Stop"/>. None is raised once
/// <see cref="Stop"/> has begun (one already running may still finish).
/// </para>
/// </summary>
public sealed class MeetingDetector : IDisposable
{
    private const string MeetingAppFlag = "--meeting-app";

    private readonly IReadOnlyList<MeetingApp> _apps;
    private readonly MeetingDetection _state;
    private readonly AudioSessionWatch _watch;

    /// <param name="apps">The table (Swift <c>init(apps:)</c>); null is <see cref="MeetingApps.Known"/>. In DEBUG
    /// builds, <c>--meeting-app &lt;exe&gt;</c> on the command line adds one more app.</param>
    public MeetingDetector(IReadOnlyList<MeetingApp>? apps = null)
    {
        _apps = apps ?? MeetingApps.Known;
#if DEBUG
        if (DebugApp(Environment.GetCommandLineArgs()) is { } extra) _apps = [.. _apps, extra];
#endif
        _state = new MeetingDetection();
        _watch = new AudioSessionWatch(includeRender: false, Refresh, _state.Reset, log => Log?.Invoke(log));
    }

    /// <summary>A known app's capture has stayed active for 3 s.</summary>
    public event Action<DetectedMeeting>? Detected;

    /// <summary>The detected app's capture stopped, or its processes are gone. Not raised by <see cref="Stop"/>.</summary>
    public event Action<DetectedMeeting>? Ended;

    /// <summary>Diagnostics (null: none): quick, never throws; called on thread-pool threads inside the lock.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The detector's table, with the DEBUG stand-in.</summary>
    public IReadOnlyList<MeetingApp> Apps => _apps;

    /// <summary>
    /// Starts watching; apps already capturing are reported after the 3 s confirmation. Does not block (the first
    /// read runs on the thread pool). Safe to call repeatedly.
    /// </summary>
    public void Start() => _watch.Start();

    /// <summary>
    /// Unregisters everything and forgets detected apps without raising <see cref="Ended"/>. Blocks while the COM
    /// objects are released (a few ms). Safe to call repeatedly.
    /// </summary>
    public void Stop() => _watch.Stop();

    public void Dispose() => _watch.Dispose();

    /// <summary>
    /// The app of <c>--meeting-app &lt;exe&gt;</c>, or null: the exe in lowercase, with <c>.exe</c> added when missing
    /// (Get-Process lists names without it), named as given without <c>.exe</c>.
    /// </summary>
    internal static MeetingApp? DebugApp(IReadOnlyList<string> args)
    {
        var index = args.ToList().IndexOf(MeetingAppFlag);
        if (index < 0 || index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1])) return null;
        var name = args[index + 1].Trim();
        var exe = name.ToLowerInvariant();
        if (exe.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];
        else exe += ".exe";
        return new MeetingApp(name, [exe]);
    }

    /// <summary>The watch's refresh (inside its lock): groups the capture sessions and updates the state.</summary>
    private IReadOnlyList<Action> Refresh(IReadOnlyList<AppSession> sessions)
    {
        var now = AudioSessionWatch.Now;
        var events = _state.Update(Candidates(sessions), now);
        if (_state.NextDue is { } due) _watch.RefreshAfter(due - now);
        return events
            .Select(e =>
            {
                Log?.Invoke($"meeting detector: {(e.Detected ? "detected" : "ended")} {e.Meeting.Exe} pid {e.Meeting.Pid} root {e.Meeting.RootPid}");
                return e.Detected ? (Action)(() => Detected?.Invoke(e.Meeting)) : () => Ended?.Invoke(e.Meeting);
            })
            .ToList();
    }

    private IReadOnlyList<MeetingDetection.Candidate> Candidates(IReadOnlyList<AppSession> sessions)
    {
        var capture = sessions.Where(s => s.Flow == EDataFlow.eCapture).ToList();
        if (capture.Count == 0) return []; // the common case: no process snapshot
        var processes = AudioSessions.Snapshot();
        var own = AudioSessions.Tree(Environment.ProcessId, processes);
        var withSession = capture.Select(s => s.Pid).ToHashSet();
        var rows = processes.Values
            .Where(p => !own.Contains(p.Pid))
            .Select(p => new ProcessRow(p.Pid, p.ParentPid, p.Exe, "", withSession.Contains(p.Pid)))
            .ToList();
        return MeetingDetection.Candidates(_apps, rows, capture.Where(s => s.Active).Select(s => s.Pid).ToHashSet());
    }
}

/// <summary>
/// The detector's decisions, without COM or timers (port of Swift <c>MeetingDetector.evaluate</c> and
/// <c>armConfirmation</c>): fed a fresh read with its time, it returns the events. Not thread-safe (the watch's lock).
/// </summary>
internal sealed class MeetingDetection
{
    public static readonly TimeSpan ConfirmDelay = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, TimeSpan> _confirming = []; // group exe → when a fresh read confirms it
    private readonly Dictionary<string, DetectedMeeting> _detected = [];

    /// <summary>A known app's group in a read: <see cref="Pid"/> is its first process with an active capture session, 0 if none.</summary>
    public sealed record Candidate(MeetingApp App, AudioProcessGroup Group, int Pid)
    {
        public bool Active => Pid != 0;
    }

    /// <summary>A Detected (true) or Ended (false) event.</summary>
    public readonly record struct Event(bool Detected, DetectedMeeting Meeting);

    /// <summary>The earliest pending confirmation: the owner reads again then. Null when none is pending.</summary>
    public TimeSpan? NextDue => _confirming.Count == 0 ? null : _confirming.Values.Min();

    /// <summary>
    /// The known apps' groups among <paramref name="rows"/> (each row's <c>HasRenderSession</c> here means it has a
    /// capture session, which is what <see cref="AudioProcessGrouping.Group"/> keeps groups by), by group exe. Of
    /// several groups with one exe (separate root processes), an active one wins.
    /// </summary>
    public static IReadOnlyList<Candidate> Candidates(
        IReadOnlyList<MeetingApp> apps, IReadOnlyList<ProcessRow> rows, IReadOnlySet<int> activePids)
    {
        var candidates = new Dictionary<string, Candidate>();
        foreach (var group in AudioProcessGrouping.Group(rows))
        {
            if (apps.FirstOrDefault(a => a.Exes.Contains(group.Exe, StringComparer.OrdinalIgnoreCase)) is not { } app) continue;
            var candidate = new Candidate(app, group, group.Pids.FirstOrDefault(activePids.Contains));
            if (!candidates.TryGetValue(group.Exe, out var known) || (!known.Active && candidate.Active)) candidates[group.Exe] = candidate;
        }
        return candidates.Values.ToList();
    }

    /// <summary>
    /// A fresh read at <paramref name="now"/>: an app that turns active waits <see cref="ConfirmDelay"/>, then a read
    /// that still finds it active detects it; a read that finds it inactive (or gone) cancels its confirmation, or ends
    /// its meeting.
    /// </summary>
    public List<Event> Update(IReadOnlyList<Candidate> candidates, TimeSpan now)
    {
        var events = new List<Event>();
        var byExe = candidates.ToDictionary(c => c.Group.Exe);
        foreach (var key in byExe.Keys.Union(_confirming.Keys).Union(_detected.Keys).ToList())
        {
            if (byExe.TryGetValue(key, out var candidate) && candidate.Active)
            {
                if (_detected.ContainsKey(key)) continue;
                if (!_confirming.TryGetValue(key, out var due))
                {
                    _confirming[key] = now + ConfirmDelay;
                }
                else if (now >= due)
                {
                    _confirming.Remove(key);
                    var meeting = new DetectedMeeting(candidate.App, candidate.Group, candidate.Pid);
                    _detected[key] = meeting;
                    events.Add(new Event(true, meeting));
                }
            }
            else
            {
                _confirming.Remove(key);
                if (_detected.Remove(key, out var meeting)) events.Add(new Event(false, meeting));
            }
        }
        return events;
    }

    /// <summary>Forgets every confirmation and detected app (Stop: no Ended).</summary>
    public void Reset()
    {
        _confirming.Clear();
        _detected.Clear();
    }
}
