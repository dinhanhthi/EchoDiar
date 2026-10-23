using System.Diagnostics;
using EchoDiar.Platform.SystemServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace EchoDiar.Platform.Tests;

// The sourceExited watch on short-lived child processes, and the message window's dispatch through same-thread
// SendMessage (no message loop needed; lock and sleep cannot be triggered on CI). The window tests stay synchronous:
// the window must be disposed on the thread that created it.
public sealed class SystemEventsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Process Ping(int count) => Process.Start(new ProcessStartInfo("ping.exe", $"-n {count} 127.0.0.1")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    })!;

    [Fact]
    public async Task WatchCompletesWhenTheProcessExits()
    {
        var startedBefore = DateTimeOffset.UtcNow;
        using var process = Ping(4); // about 3 s

        var watch = SystemEvents.WatchProcessExitAsync(process.Id, startedBefore, TestContext.Current.CancellationToken);
        Assert.False(watch.IsCompleted);
        await watch.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task ProcessStartedAfterTheSourceCountsAsExited()
    {
        using var process = Ping(60);
        try
        {
            // The pid runs a process started after the source was picked: the source's pid reused.
            await SystemEvents.WatchProcessExitAsync(process.Id, DateTimeOffset.UtcNow.AddHours(-1), TestContext.Current.CancellationToken)
                .WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.False(process.HasExited);
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public async Task CancellingTheWatchThrows()
    {
        var startedBefore = DateTimeOffset.UtcNow;
        using var process = Ping(60);
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => SystemEvents.WatchProcessExitAsync(process.Id, startedBefore, cancel.Token).WaitAsync(Timeout, TestContext.Current.CancellationToken));
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public void SessionAndPowerMessagesRaiseTheEvents()
    {
        using var events = new SystemEvents();
        var raised = new List<string>();
        events.ScreenLocked += locked => raised.Add(locked ? "locked" : "unlocked");
        events.SystemSleep += () => raised.Add("sleep");
        events.SystemResumed += () => raised.Add("resumed");

        PInvoke.SendMessage(events.Handle, PInvoke.WM_WTSSESSION_CHANGE, PInvoke.WTS_SESSION_LOCK, 0);
        PInvoke.SendMessage(events.Handle, PInvoke.WM_WTSSESSION_CHANGE, PInvoke.WTS_SESSION_UNLOCK, 0);
        var suspend = PInvoke.SendMessage(events.Handle, PInvoke.WM_POWERBROADCAST, PInvoke.PBT_APMSUSPEND, 0);
        PInvoke.SendMessage(events.Handle, PInvoke.WM_POWERBROADCAST, PInvoke.PBT_APMRESUMEAUTOMATIC, 0);

        Assert.Equal(["locked", "unlocked", "sleep", "resumed"], raised);
        Assert.Equal(1, suspend.Value);
    }

    [Fact]
    public void AThrowingHandlerIsLoggedAndTheNextOneRuns()
    {
        var log = new List<string>();
        var window = new MessageWindow(log.Add);
        var handle = window.Handle;
        window.AddHandler(PInvoke.WM_APP, (_, _) => throw new InvalidOperationException("boom"));
        window.AddHandler(PInvoke.WM_APP, (wParam, lParam) => (LRESULT)(nint)(wParam.Value + (nuint)lParam.Value));

        var result = PInvoke.SendMessage(handle, PInvoke.WM_APP, 40, 2);
        window.Dispose();

        Assert.Equal(42, result.Value);
        Assert.Contains(log, line => line.Contains("boom"));
        Assert.False(PInvoke.IsWindow(handle));
    }
}
