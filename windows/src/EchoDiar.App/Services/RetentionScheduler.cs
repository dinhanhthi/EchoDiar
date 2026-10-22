namespace EchoDiar.App.Services;

/// <summary>
/// The daily auto-delete run (macOS <c>LibraryStore.startRetentionSchedule</c>): every 24 h while started, nothing
/// persisted, so the first tick is 24 h after <see cref="Start"/>. The caller runs it once itself when it starts it
/// (launch, or auto-delete turned on). Started on the UI thread, the run is called there.
/// </summary>
internal sealed class RetentionScheduler(Func<Task> run)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private CancellationTokenSource? _cancel;

    /// <summary>Schedules the run every <see cref="Interval"/>; replaces a previous schedule.</summary>
    public void Start()
    {
        Stop();
        _cancel = new CancellationTokenSource();
        _ = LoopAsync(_cancel.Token);
    }

    /// <summary>No more runs; one under way finishes. (The source needs no disposing: no timer or link.)</summary>
    public void Stop()
    {
        _cancel?.Cancel();
        _cancel = null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token)) await run();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
