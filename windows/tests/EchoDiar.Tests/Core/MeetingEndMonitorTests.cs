using System.Text;
using EchoDiar.Core;
using EchoDiar.Core.Json;
using EchoDiar.Core.Settings;
using static EchoDiar.Core.MeetingEndMonitor;

namespace EchoDiar.Tests.Core;

// Port of macOS MeetingEndMonitorTests: guards when a recording is asked to stop or stopped, and that nothing keeps it
// running past the max duration.
public sealed class MeetingEndMonitorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(978_307_200);

    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    private static MeetingEndMonitor Monitor()
    {
        var monitor = new MeetingEndMonitor(Start);
        monitor.Record(new Input.Audible(true), Start);
        return monitor;
    }

    private static Decision Prompt(Reason reason) => new Decision.PromptStop(reason, 15);

    [Fact]
    public void MeetingAppQuittingPromptsWithCountdownAndKeepRecordingSnoozesTenMinutes()
    {
        var monitor = Monitor();
        monitor.Record(new Input.SourceExited(), At(60));
        Assert.Equal(Prompt(Reason.SourceExited), monitor.Evaluate(At(60)));

        monitor.KeepRecording(Reason.SourceExited, At(70));
        Assert.Equal(Decision.None, monitor.Evaluate(At(70 + 599)));
        Assert.Equal(Prompt(Reason.SourceExited), monitor.Evaluate(At(70 + 600)));
    }

    [Fact]
    public void SilencePastThresholdPrompts()
    {
        var monitor = Monitor();
        monitor.Record(new Input.Audible(false), At(100));
        Assert.Equal(Decision.None, monitor.Evaluate(At(100 + 599)));
        Assert.Equal(Prompt(Reason.Silence), monitor.Evaluate(At(100 + 600)));
    }

    [Fact]
    public void SystemQuietAfterSoundWithTheMeetingMicOffPromptsCallEnded()
    {
        var monitor = Monitor();
        monitor.Record(new Input.SourceInputActive(true), Start);
        monitor.Record(new Input.SystemAudible(true), At(10));
        monitor.Record(new Input.SystemAudible(false), At(100));
        monitor.Record(new Input.SourceInputActive(false), At(110));
        Assert.Equal(Decision.None, monitor.Evaluate(At(110 + 59)));
        Assert.Equal(Prompt(Reason.CallEnded), monitor.Evaluate(At(110 + 60)));
    }

    [Fact]
    public void PausedTimeIsNotCallEndedSilence()
    {
        var monitor = Monitor();
        monitor.Record(new Input.SourceInputActive(true), Start);
        monitor.Record(new Input.SystemAudible(true), At(10));
        monitor.Record(new Input.SystemAudible(false), At(100));
        monitor.Record(new Input.SourceInputActive(false), At(110));
        monitor.Record(new Input.Paused(true), At(120));
        Assert.Equal(Decision.None, monitor.Evaluate(At(250)));
        monitor.Record(new Input.Paused(false), At(300));
        Assert.Equal(Decision.None, monitor.Evaluate(At(300 + 59)));
        Assert.Equal(Prompt(Reason.CallEnded), monitor.Evaluate(At(300 + 60)));
    }

    [Fact]
    public void CallEndedNeedsSoundFirstAndTheMeetingMicOff()
    {
        var neverHeard = Monitor();
        neverHeard.Record(new Input.SystemAudible(false), At(10));
        neverHeard.Record(new Input.SourceInputActive(false), At(10));
        Assert.Equal(Decision.None, neverHeard.Evaluate(At(300)));

        var micStillOn = Monitor();
        micStillOn.Record(new Input.SourceInputActive(true), Start);
        micStillOn.Record(new Input.SystemAudible(true), At(10));
        micStillOn.Record(new Input.SystemAudible(false), At(20));
        Assert.Equal(Decision.None, micStillOn.Evaluate(At(300)));
    }

    [Fact]
    public void SleepStopsNow()
    {
        var monitor = Monitor();
        monitor.Record(new Input.SystemSleep(), At(30));
        Assert.Equal(new Decision.StopNow(Reason.SystemSleep), monitor.Evaluate(At(30)));
    }

    [Fact]
    public void MaxDurationStopsNowEvenWhenSnoozed()
    {
        var monitor = Monitor();
        const double snoozedAt = 4 * 3600 - 300;
        monitor.Record(new Input.SourceExited(), At(snoozedAt));
        monitor.KeepRecording(Reason.SourceExited, At(snoozedAt));
        Assert.Equal(Decision.None, monitor.Evaluate(At(4 * 3600 - 1)));
        Assert.Equal(new Decision.StopNow(Reason.MaxDuration), monitor.Evaluate(At(4 * 3600)));
    }

    // Swift reads Int64.max here; the C# configuration is Int32, so its largest value stands in.
    [Fact]
    public void AbsurdStoredConfigurationIsClampedAndMaxDurationStillStops()
    {
        const string json = """{"sourceInactiveSeconds": -1, "callEndedSeconds": 1, "silenceMinutes": 0, "maxDurationMinutes": 2147483647}""";
        var configuration = SwiftJson.Deserialize<MeetingEndMonitorConfiguration>(Encoding.UTF8.GetBytes(json));
        Assert.Equal(new MeetingEndMonitorConfiguration
        {
            SourceInactiveSeconds = 20, CallEndedSeconds = 30, SilenceMinutes = 1, MaxDurationMinutes = 1440,
        }, configuration);

        var monitor = new MeetingEndMonitor(configuration, Start);
        monitor.Record(new Input.Audible(true), Start);
        Assert.Equal(Decision.None, monitor.Evaluate(At(1440 * 60 - 1)));
        Assert.Equal(new Decision.StopNow(Reason.MaxDuration), monitor.Evaluate(At(1440 * 60)));
    }
}
