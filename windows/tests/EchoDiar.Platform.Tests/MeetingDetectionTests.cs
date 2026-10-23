using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Platform.Audio;

namespace EchoDiar.Platform.Tests;

// The detector's decisions (MeetingDetection) on fake process rows and session pids with a fake clock: the 3 s
// confirmation, ended, and the grouping of the capturing process under its app. No COM, so deterministic.
public sealed class MeetingDetectionTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    // New Teams captures through a WebView2 child; Chrome through an audio utility child; notepad is unknown.
    private static readonly ProcessRow[] Rows =
    [
        new(Pid: 10, ParentPid: 1, Exe: "explorer.exe", Path: "", HasRenderSession: false),
        new(Pid: 200, ParentPid: 10, Exe: "ms-teams.exe", Path: "", HasRenderSession: false),
        new(Pid: 201, ParentPid: 200, Exe: "msedgewebview2.exe", Path: "", HasRenderSession: true),
        new(Pid: 300, ParentPid: 10, Exe: "chrome.exe", Path: "", HasRenderSession: false),
        new(Pid: 301, ParentPid: 300, Exe: "chrome.exe", Path: "", HasRenderSession: true),
        new(Pid: 400, ParentPid: 10, Exe: "notepad.exe", Path: "", HasRenderSession: true),
    ];

    private readonly MeetingDetection _state = new();

    private List<MeetingDetection.Event> Read(TimeSpan at, params int[] activePids) =>
        _state.Update(MeetingDetection.Candidates(MeetingApps.Known, Rows, activePids.ToHashSet()), at);

    [Fact]
    public void CaptureActiveForThreeSecondsDetectsTheAppWithItsRoot()
    {
        Assert.Empty(Read(TimeSpan.Zero, 201, 400));
        Assert.Equal(3 * Second, _state.NextDue);
        Assert.Empty(Read(2 * Second, 201, 400));

        var events = Read(3 * Second, 201, 400);

        var detected = Assert.Single(events);
        Assert.True(detected.Detected);
        Assert.Equal(("Microsoft Teams", "ms-teams.exe", 201, 200), (detected.Meeting.Name, detected.Meeting.Exe, detected.Meeting.Pid, detected.Meeting.RootPid));
        Assert.Equal(new[] { 200, 201 }, detected.Meeting.Group.Pids);
        Assert.Null(_state.NextDue);
        Assert.Empty(Read(10 * Second, 201, 400)); // once
    }

    [Fact]
    public void InactiveBeforeThreeSecondsCancelsAndActiveAgainRestartsTheDelay()
    {
        Read(TimeSpan.Zero, 301);
        Assert.Empty(Read(1 * Second));
        Assert.Null(_state.NextDue);

        Read(2 * Second, 301);
        Assert.Empty(Read(4 * Second, 301));
        Assert.True(Assert.Single(Read(5 * Second, 301)).Detected);
    }

    [Fact]
    public void EndedWhenTheDetectedAppsCaptureStops()
    {
        Read(TimeSpan.Zero, 201, 301);
        Assert.Equal(2, Read(3 * Second, 201, 301).Count);

        var ended = Assert.Single(Read(4 * Second, 301));

        Assert.False(ended.Detected);
        Assert.Equal("ms-teams.exe", ended.Meeting.Exe);
        Assert.Equal("chrome.exe", Assert.Single(_state.Update([], 5 * Second)).Meeting.Exe); // processes gone
    }

    [Fact]
    public void ResetForgetsWithoutEnded()
    {
        Read(TimeSpan.Zero, 201);
        Read(3 * Second, 201);

        _state.Reset();

        Assert.Empty(Read(4 * Second));
    }

    [Fact]
    public void DebugMeetingAppTakesAGetProcessName()
    {
        var app = MeetingDetector.DebugApp(["echodiar.exe", "--meeting-app", "SoundRec"]);

        Assert.Equal(("SoundRec", "soundrec.exe"), (app?.Name, app?.AppExe));
        Assert.Null(MeetingDetector.DebugApp(["echodiar.exe", "--meeting-app"]));
    }
}
