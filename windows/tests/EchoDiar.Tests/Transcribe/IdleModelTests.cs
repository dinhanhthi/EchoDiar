using EchoDiar.Transcribe;

namespace EchoDiar.Tests.Transcribe;

// Guards the paused-recording release (Swift IdleModelTests): the live model is freed once it went unused for the
// idle time, the next use loads it again, and no idle time keeps it.
public sealed class IdleModelTests
{
    [Fact]
    public async Task FreesTheModelAfterTheIdleTimeAndLoadsItAgainOnUse()
    {
        var clock = new ManualClock();
        var events = new List<string>();
        var model = new IdleModel<string>(
            "1", TimeSpan.FromSeconds(60), _ => { events.Add("load"); return Task.FromResult("2"); },
            m => { events.Add("unload " + m); return ValueTask.CompletedTask; }, clock);

        clock.Seconds = 59;
        Assert.False(await model.ReleaseIfIdleAsync());
        clock.Seconds = 60;
        Assert.True(await model.ReleaseIfIdleAsync());
        clock.Seconds = 90;
        Assert.Equal("2", await model.UseAsync((m, _) => Task.FromResult(m), TestContext.Current.CancellationToken));
        clock.Seconds = 149;
        Assert.False(await model.ReleaseIfIdleAsync());
        await model.DisposeAsync();

        Assert.Equal(["unload 1", "load", "unload 2"], events);
    }

    [Fact]
    public async Task NeverFreesTheModelWithoutAnIdleTime()
    {
        var clock = new ManualClock();
        var model = new IdleModel<string>("1", null, _ => Task.FromResult("2"), _ => ValueTask.CompletedTask, clock);

        clock.Seconds = 86_400;
        Assert.False(await model.ReleaseIfIdleAsync());
    }

    /// <summary>A monotonic clock that moves only when the test sets <see cref="Seconds"/>.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public long Seconds { get; set; }

        public override long TimestampFrequency => 1;

        public override long GetTimestamp() => Seconds;
    }
}
