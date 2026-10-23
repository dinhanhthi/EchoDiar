using EchoDiar.Platform.Audio;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace EchoDiar.Platform.Windowing;

/// <summary>
/// Follows the meeting window for "Attach to meeting window" (macOS WindowAnchor): the largest visible top-level
/// window of the capture source's process tree. Nothing polls: WinEvents report the target moving or resizing
/// (EVENT_OBJECT_LOCATIONCHANGE on the target's process) and windows of the tree being shown, hidden, minimized,
/// restored or destroyed, which picks the target again.
/// <para>
/// Threading: create, <see cref="Stop"/> and <see cref="Dispose"/> on the UI thread. The hooks are out of context
/// (WINEVENT_OUTOFCONTEXT), so Windows delivers them as messages to the thread that set them, which must pump messages:
/// the WinUI dispatcher thread does. <c>onChange</c> therefore runs on the UI thread too; no dispatch is needed.
/// </para>
/// </summary>
public sealed class WindowFollower : IDisposable
{
    /// <summary>Rooted for the process's life: a collected delegate behind a live hook would crash.</summary>
    private static readonly WINEVENTPROC Callback = OnEvent;

    /// <summary>Live hooks by handle, which also keeps each follower alive until it stops. UI thread only.</summary>
    private static readonly Dictionary<HWINEVENTHOOK, WindowFollower> Followers = [];

    /// <summary>Destroy, show and hide (0x8001-0x8003); minimize start and end.</summary>
    private static readonly (uint Min, uint Max)[] LifecycleEvents =
    [
        (PInvoke.EVENT_OBJECT_DESTROY, PInvoke.EVENT_OBJECT_HIDE),
        (PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND),
    ];

    private readonly Action<RectInt32?> _onChange;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly List<HWINEVENTHOOK> _lifecycleHooks = [];
    private HashSet<int> _tree = [];
    private HWND _target;
    private uint _targetPid;
    private HWINEVENTHOOK _locationHook;
    private RectInt32? _reported;
    private bool _hasReported;
    private bool _stopped;

    /// <summary>
    /// Starts following at once. <paramref name="onChange"/> gets the target window's frame (physical screen pixels,
    /// without the invisible resize borders), null while the tree has no visible window: once now, then on each change.
    /// </summary>
    /// <param name="rootPid">The capture source group's root (<c>AudioProcessGroup.RootPid</c>); its descendants count too.</param>
    public WindowFollower(int rootPid, Action<RectInt32?> onChange)
    {
        RootPid = rootPid;
        _onChange = onChange;
        // Process 0: windows of any process in the tree, not only the target's. Low-frequency events only.
        foreach (var (min, max) in LifecycleEvents)
        {
            if (Hook(min, max, 0) is { IsNull: false } hook) _lifecycleHooks.Add(hook);
        }
        Pick();
    }

    public int RootPid { get; }

    /// <summary>Removes the hooks; no <c>onChange</c> call follows. UI thread only (the thread that created it).</summary>
    public void Stop()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException("WindowFollower must be stopped on the thread that created it.");
        }
        if (_stopped) return;
        _stopped = true;
        foreach (var hook in _lifecycleHooks) Unhook(hook);
        _lifecycleHooks.Clear();
        Unhook(_locationHook);
        _locationHook = default;
    }

    public void Dispose() => Stop();

    /// <summary>An out-of-context hook on <paramref name="pid"/> (0 = every process but this one); null on failure.</summary>
    private HWINEVENTHOOK Hook(uint min, uint max, uint pid)
    {
        var hook = PInvoke.SetWinEventHook(min, max, HMODULE.Null, Callback, pid, 0,
            PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        if (!hook.IsNull) Followers[hook] = this;
        return hook;
    }

    private static void Unhook(HWINEVENTHOOK hook)
    {
        if (hook.IsNull) return;
        PInvoke.UnhookWinEvent(hook);
        Followers.Remove(hook);
    }

    private static void OnEvent(
        HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != (int)PInvoke.CHILDID_SELF) return;
        // Events queued before Stop still arrive; their hook is gone from the table.
        if (!Followers.TryGetValue(hook, out var follower) || follower._stopped) return;
        follower.Handle(@event, hwnd);
    }

    private void Handle(uint @event, HWND hwnd)
    {
        if (@event == PInvoke.EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (hwnd != _target) return;
            // Minimizing moves the window off screen before EVENT_SYSTEM_MINIMIZESTART arrives.
            if (Eligible(hwnd)) Report();
            else Pick();
        }
        else if (hwnd == _target || InTree(hwnd))
        {
            // A destroyed window has no process any more, so only the target's destruction is seen.
            Pick();
        }
    }

    /// <summary>A top-level window of a process in the tree (as of the last pick).</summary>
    private bool InTree(HWND hwnd)
    {
        if (PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) != hwnd) return false;
        PInvoke.GetWindowThreadProcessId(hwnd, out var pid);
        return pid is > 0 and <= int.MaxValue && _tree.Contains((int)pid);
    }

    /// <summary>Takes the largest eligible window of the tree, moves the location hook to its process, and reports.</summary>
    private void Pick()
    {
        _tree = AudioSessions.ProcessTree(RootPid);
        var best = HWND.Null;
        long bestArea = 0;
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (Eligible(hwnd) && InTree(hwnd) && Frame(hwnd) is { } frame && (long)frame.Width * frame.Height > bestArea)
            {
                best = hwnd;
                bestArea = (long)frame.Width * frame.Height;
            }
            return true;
        }, default);

        _target = best;
        var pid = 0u;
        if (!best.IsNull) PInvoke.GetWindowThreadProcessId(best, out pid);
        // The hook's process filter takes one pid; a failed hook is retried on the next pick.
        if (pid != _targetPid || (pid != 0 && _locationHook.IsNull))
        {
            Unhook(_locationHook);
            _targetPid = pid;
            _locationHook = pid == 0 ? default : Hook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_LOCATIONCHANGE, pid);
        }
        Report();
    }

    private void Report()
    {
        var frame = _target.IsNull ? null : Frame(_target);
        if (_hasReported && Nullable.Equals(frame, _reported)) return;
        _hasReported = true;
        _reported = frame;
        _onChange(frame);
    }

    /// <summary>Visible, not minimized, not cloaked (another virtual desktop, a suspended UWP app), not a tool window.</summary>
    private static unsafe bool Eligible(HWND hwnd)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd)) return false;
        var style = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (style.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW)) return false;
        uint cloaked = 0;
        return PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Failed
            || cloaked == 0;
    }

    /// <summary>The visible frame (DWM extended frame bounds), else the window rect; null when empty or unreadable.</summary>
    private static unsafe RectInt32? Frame(HWND hwnd)
    {
        RECT rect;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, (uint)sizeof(RECT)).Failed
            && !PInvoke.GetWindowRect(hwnd, out rect))
        {
            return null;
        }
        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;
        return width > 0 && height > 0 ? new RectInt32(rect.left, rect.top, width, height) : null;
    }
}
