using System.ComponentModel;
using System.Diagnostics;

// Not EchoDiar.Platform.System: that namespace would hide System for every System.-qualified name under EchoDiar.Platform.
namespace EchoDiar.Platform.SystemServices;

/// <summary>Process liveness, for SessionRecovery.Recover's <c>isOwnerAlive</c>.</summary>
public static class ProcessInfo
{
    /// <summary>
    /// Slack for the session's start time against the process's: session.json dates may be rounded, and the clock
    /// may have been adjusted between the process start and the session start.
    /// </summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether process <paramref name="pid"/> runs and started no later than <paramref name="sessionStartedAt"/>
    /// (plus 2 s of tolerance): a process that started later has reused the pid of a dead owner. Total: never throws,
    /// whatever the pid or date (session.json is untrusted); pid ≤ 0 is not alive. A process whose start time cannot
    /// be read (access denied: another user's or a protected process) counts as alive, so recovery never takes over
    /// a session a live process may still be writing.
    /// </summary>
    public static bool IsAlive(int pid, DateTimeOffset sessionStartedAt)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            try
            {
                if (process.HasExited) return false;
                // Subtract from the start time: adding to a hostile DateTimeOffset.MaxValue would overflow.
                return process.StartTime.ToUniversalTime() - StartTolerance <= sessionStartedAt.UtcDateTime;
            }
            catch (Exception e) when (e is Win32Exception or NotSupportedException)
            {
                return true;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false; // not running, or exited meanwhile
        }
    }
}
