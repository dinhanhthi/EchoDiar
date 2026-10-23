using System.ComponentModel;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

// Not EchoDiar.Platform.System: that namespace would hide System for every System.-qualified name under EchoDiar.Platform.
namespace EchoDiar.Platform.SystemServices;

/// <summary>
/// Screen lock, sleep and resume (macOS SystemEventsMonitor), for the meeting end monitor; and the capture source's
/// exit (<see cref="WatchProcessExitAsync"/>). Notification-driven through a <see cref="MessageWindow"/>: nothing polls.
/// (<see cref="PowerState"/> is the separate energy-saver status.)
/// <para>
/// Threading: create and <see cref="Dispose"/> on the UI thread, which pumps the window's messages. The events are
/// raised synchronously on that thread inside the window procedure, so handlers need no dispatch; a handler that
/// throws is logged, not rethrown. <see cref="SystemSleep"/> handlers have about 2 s before the system sleeps.
/// </para>
/// </summary>
public sealed class SystemEvents : IDisposable
{
    /// <summary>Slack for the process's start time against <c>startedBefore</c> (as <see cref="ProcessInfo"/>).</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    /// <summary>The poll interval when the process cannot be waited on (access denied).</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly MessageWindow _window;
    private readonly Action<string>? _log;
    private readonly bool _sessionRegistered;
    private HPOWERNOTIFY _power;
    private bool _disposed;

    /// <summary>
    /// Starts listening. A notification Windows refuses to register is logged and never raised: the app runs on
    /// without it. Throws <see cref="Win32Exception"/> only if the message window cannot be created.
    /// </summary>
    public SystemEvents(Action<string>? log = null)
    {
        _log = log;
        _window = new MessageWindow(log);
        _window.AddHandler(PInvoke.WM_WTSSESSION_CHANGE, OnSessionChange);
        _window.AddHandler(PInvoke.WM_POWERBROADCAST, OnPowerBroadcast);

        // Can fail early in a logon, before the session service is ready (RPC_S_INVALID_BINDING).
        _sessionRegistered = PInvoke.WTSRegisterSessionNotification(_window.Handle, PInvoke.NOTIFY_FOR_THIS_SESSION);
        if (!_sessionRegistered) Log("WTSRegisterSessionNotification");

        // A message-only window gets no broadcast WM_POWERBROADCAST; this registration sends it to the window.
        // The raw handle overload: CsWin32's SafeHandle one releases through UnregisterPowerSettingNotification.
        _power = PInvoke.RegisterSuspendResumeNotification(
            _window.Handle, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_power.IsNull) Log("RegisterSuspendResumeNotification");
    }

    /// <summary>The message window (tests send it messages).</summary>
    internal HWND Handle => _window.Handle;

    /// <summary>The session's screen was locked (true) or unlocked (false). UI thread.</summary>
    public event Action<bool>? ScreenLocked;

    /// <summary>The system is about to sleep or hibernate (PBT_APMSUSPEND). UI thread; stop quickly.</summary>
    public event Action? SystemSleep;

    /// <summary>The system woke from sleep or hibernation (PBT_APMRESUMEAUTOMATIC). UI thread.</summary>
    public event Action? SystemResumed;

    /// <summary>Unregisters and destroys the window; no event follows. UI thread only (the thread that created it).</summary>
    public void Dispose()
    {
        _window.CheckThread();
        if (_disposed) return;
        _disposed = true;
        if (_sessionRegistered && !PInvoke.WTSUnRegisterSessionNotification(_window.Handle)) Log("WTSUnRegisterSessionNotification");
        if (!_power.IsNull)
        {
            if (!PInvoke.UnregisterSuspendResumeNotification(_power)) Log("UnregisterSuspendResumeNotification");
            _power = default;
        }
        _window.Dispose();
    }

    /// <summary>
    /// Completes when process <paramref name="pid"/> has exited: the capture source's root (sourceExited). Completes
    /// at once when the pid is not running, or runs a process started after <paramref name="startedBefore"/> (plus
    /// 2 s): a dead source's pid reused. When the process cannot be waited on (access denied), polls every 2 s for
    /// the pid with the same process name. Cancelled through <paramref name="cancellationToken"/>
    /// (<see cref="OperationCanceledException"/>). Any thread; completes on a thread-pool thread, so an await on the
    /// UI thread resumes there.
    /// </summary>
    /// <param name="startedBefore">When the source was picked (the recording's start): the process ran before it.</param>
    public static async Task WatchProcessExitAsync(int pid, DateTimeOffset startedBefore, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pid <= 0) return;
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return; // not running
        }

        string? name = null;
        using (process)
        {
            try
            {
                name = process.ProcessName;
                // The handle first: Windows reuses no pid while a handle to its process is open, so the start time
                // read next is this process's.
                process.EnableRaisingEvents = true;
                if (process.StartTime.ToUniversalTime() - StartTolerance > startedBefore.UtcDateTime) return; // reused
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException)
            {
                return; // exited meanwhile
            }
            catch (Exception e) when (e is Win32Exception or NotSupportedException)
            {
                // Access denied (an elevated or protected process): poll below.
            }
        }

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                using var current = Process.GetProcessById(pid);
                if (name is not null && !string.Equals(current.ProcessName, name, StringComparison.OrdinalIgnoreCase)) return; // reused
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return; // gone
            }
        }
    }

    private LRESULT? OnSessionChange(WPARAM wParam, LPARAM lParam)
    {
        switch ((uint)wParam.Value)
        {
            case PInvoke.WTS_SESSION_LOCK: ScreenLocked?.Invoke(true); break;
            case PInvoke.WTS_SESSION_UNLOCK: ScreenLocked?.Invoke(false); break;
        }
        return (LRESULT)0;
    }

    private LRESULT? OnPowerBroadcast(WPARAM wParam, LPARAM lParam)
    {
        switch ((uint)wParam.Value)
        {
            case PInvoke.PBT_APMSUSPEND: SystemSleep?.Invoke(); break;
            case PInvoke.PBT_APMRESUMEAUTOMATIC: SystemResumed?.Invoke(); break;
        }
        return (LRESULT)1; // TRUE
    }

    private void Log(string call) => _log?.Invoke($"system events: {call} failed, {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
}
