using System.Diagnostics;
using EchoDiar.Platform.SystemServices;

namespace EchoDiar.Platform.Tests;

// Real processes on the runner. Kill tests use their own job: closing Shared would never happen in the app.
public sealed class ChildProcessJobTests
{
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "ping.exe");

    // Bounded, so a process left behind by a failed test still ends on its own.
    private static Process StartPing(int count = 60)
    {
        var info = new ProcessStartInfo(Ping) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("-n");
        info.ArgumentList.Add(count.ToString());
        info.ArgumentList.Add("127.0.0.1");
        return Process.Start(info)!;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        process.Dispose();
    }

    [Fact]
    public void TheSharedJobTakesAStartedProcess()
    {
        var ping = StartPing();
        try
        {
            ChildProcessJob.Shared.Assign(ping);
            Assert.True(ChildProcessJob.Shared.Contains(ping));
        }
        finally
        {
            Kill(ping);
        }
    }

    [Fact]
    public void ClosingTheJobKillsItsProcesses()
    {
        var first = StartPing();
        var second = StartPing();
        try
        {
            using (var job = new ChildProcessJob())
            {
                job.Assign(first);
                job.Assign(second); // a job that already has members (the app assigns many CLIs to one job)
                Assert.True(job.Contains(first));
                Assert.True(job.Contains(second));
                Assert.False(first.HasExited);
            }
            Assert.True(first.WaitForExit(5000));
            Assert.True(second.WaitForExit(5000));
        }
        finally
        {
            Kill(first);
            Kill(second);
        }
    }

    [Fact]
    public void AnExitedProcessIsIgnored()
    {
        using var job = new ChildProcessJob();
        var ping = StartPing(1);
        try
        {
            ping.StandardOutput.ReadToEnd();
            Assert.True(ping.WaitForExit(10_000));
            job.Assign(ping);
        }
        finally
        {
            Kill(ping);
        }
    }

    [Fact]
    public async Task AGrandchildJoinsTheJobAndDiesWithIt()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", $"& '{Ping}' -n 60 127.0.0.1" })
            info.ArgumentList.Add(argument);
        var parent = Process.Start(info)!;
        Process? grandchild = null;
        try
        {
            using (var job = new ChildProcessJob())
            {
                job.Assign(parent);
                // PowerShell is slow to start on the ARM runner. Only its ping.exe can be in this new job.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while ((grandchild = PingIn(job)) is null && DateTime.UtcNow < deadline)
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                Assert.NotNull(grandchild);
            }
            Assert.True(grandchild.WaitForExit(5000));
            Assert.True(parent.WaitForExit(5000));
        }
        finally
        {
            if (grandchild is not null) Kill(grandchild);
            Kill(parent);
        }
    }

    private static Process? PingIn(ChildProcessJob job)
    {
        Process? found = null;
        foreach (var process in Process.GetProcessesByName("PING"))
        {
            try
            {
                if (found is null && job.Contains(process))
                {
                    found = process;
                    continue;
                }
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // exited meanwhile, or not ours
            }
            process.Dispose();
        }
        return found;
    }
}
