using Windows.Win32.Media.Audio;

namespace EchoDiar.Platform.Audio;

/// <summary>
/// Whether the recorded app's process tree (the group's root and its descendants now) has an active capture session
/// (<see cref="IsRunningInput"/>) or render session (<see cref="IsRunningOutput"/>), on every active endpoint (port of
/// Swift <c>ProcessActivityWatch</c>; <see cref="AudioSessionWatch"/>: session notifications, state-change events and
/// a 2 s poll, refreshes debounced by 500 ms). Feeds the end monitor: <c>SourceActive(IsRunning)</c>.
/// <para>
/// <see cref="Changed"/> is raised once after the first read, then whenever either flag changes, on a thread-pool
/// thread, one at a time, outside the watch's lock. A handler must not block (dispatch to the UI thread with
/// TryEnqueue); it may call <see cref="Stop"/>. None is raised once <see cref="Stop"/> has begun (one already running
/// may still finish).
/// </para>
/// </summary>
public sealed class ProcessActivityWatch : IDisposable
{
    private readonly AudioSessionWatch _watch;
    private (bool Input, bool Output)? _last; // the last read, inside the watch's lock
    private volatile bool _input;
    private volatile bool _output;

    /// <param name="rootPid">The recording's group root (<c>AudioProcessGroup.RootPid</c>).</param>
    public ProcessActivityWatch(int rootPid)
    {
        RootPid = rootPid;
        _watch = new AudioSessionWatch(includeRender: true, Refresh, () => _last = null, log => Log?.Invoke(log));
    }

    public int RootPid { get; }

    /// <summary>A process of the tree has an active capture session (its mic is on). False before the first read.</summary>
    public bool IsRunningInput => _input;

    /// <summary>A process of the tree has an active render session (it plays audio). False before the first read.</summary>
    public bool IsRunningOutput => _output;

    /// <summary>Input or output runs: Swift's single flag, <c>SourceActive</c>.</summary>
    public bool IsRunning => _input || _output;

    /// <summary>The first read, then a change of <see cref="IsRunningInput"/> or <see cref="IsRunningOutput"/>.</summary>
    public event Action<ProcessActivityWatch>? Changed;

    /// <summary>Diagnostics (null: none): quick, never throws; called on thread-pool threads inside the lock.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Starts watching; the first read runs on the thread pool at once. Does not block. Safe to call repeatedly.</summary>
    public void Start() => _watch.Start();

    /// <summary>Unregisters everything; the flags keep their last value. Blocks while the COM objects are released.</summary>
    public void Stop() => _watch.Stop();

    public void Dispose() => _watch.Dispose();

    /// <summary>The watch's refresh (inside its lock).</summary>
    private IReadOnlyList<Action> Refresh(IReadOnlyList<AppSession> sessions)
    {
        var active = sessions.Where(s => s.Active).ToList();
        // A process snapshot only while some session is active; a process that is gone counts as inactive.
        HashSet<int> tree = active.Count == 0 || RootPid <= 0 ? [] : AudioSessions.Tree(RootPid, AudioSessions.Snapshot());
        var now = (
            Input: active.Any(s => s.Flow == EDataFlow.eCapture && tree.Contains(s.Pid)),
            Output: active.Any(s => s.Flow == EDataFlow.eRender && tree.Contains(s.Pid)));
        if (_last == now) return [];
        _last = now;
        _input = now.Input;
        _output = now.Output;
        Log?.Invoke($"activity watch: root {RootPid} input {now.Input} output {now.Output}");
        return [() => Changed?.Invoke(this)];
    }
}
