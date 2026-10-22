using EchoDiar.Core.Audio;

namespace EchoDiar.Tests.Core;

// Guards the "one app" root rules: the root pid is what process loopback captures, so a wrong root records the wrong app.
public sealed class AudioProcessGroupingTests
{
    [Fact]
    public void HelpersFoldIntoTheirHostAppAndSystemProcessesAreNeverRoots()
    {
        const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        const string WebView = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe";
        ProcessRow[] rows =
        [
            new(Pid: 4, ParentPid: 0, Exe: "system", Path: "", HasRenderSession: false),
            new(Pid: 50, ParentPid: 4, Exe: "services.exe", Path: @"C:\Windows\System32\services.exe", HasRenderSession: false),
            new(Pid: 10, ParentPid: 8, Exe: "explorer.exe", Path: @"C:\Windows\explorer.exe", HasRenderSession: true),
            // Chrome: the browser process has no session, its audio utility child does.
            new(Pid: 100, ParentPid: 10, Exe: "chrome.exe", Path: Chrome, HasRenderSession: false),
            new(Pid: 101, ParentPid: 100, Exe: "chrome.exe", Path: Chrome, HasRenderSession: true),
            new(Pid: 102, ParentPid: 100, Exe: "chrome.exe", Path: Chrome, HasRenderSession: false),
            // New Teams plays through its WebView2 children.
            new(Pid: 200, ParentPid: 10, Exe: "ms-teams.exe", Path: @"C:\Program Files\WindowsApps\MSTeams\ms-teams.exe", HasRenderSession: false),
            new(Pid: 201, ParentPid: 200, Exe: "msedgewebview2.exe", Path: WebView, HasRenderSession: false),
            new(Pid: 202, ParentPid: 201, Exe: "msedgewebview2.exe", Path: WebView, HasRenderSession: true),
            // svchost with sessions is dropped; an app it launched becomes its own group.
            new(Pid: 300, ParentPid: 50, Exe: "svchost.exe", Path: @"C:\Windows\System32\svchost.exe", HasRenderSession: true),
            new(Pid: 301, ParentPid: 300, Exe: "svchost.exe", Path: @"C:\Windows\System32\svchost.exe", HasRenderSession: true),
            new(Pid: 302, ParentPid: 300, Exe: "foo.exe", Path: @"C:\Tools\foo.exe", HasRenderSession: true),
            // Running but silent: not offered.
            new(Pid: 400, ParentPid: 10, Exe: "zoom.exe", Path: @"C:\Users\me\AppData\Roaming\Zoom\bin\zoom.exe", HasRenderSession: false),
        ];

        var groups = AudioProcessGrouping.Group(rows);

        Assert.Equal(
            [
                ("Google Chrome", "chrome.exe", 100, "100,101,102", false),
                ("Microsoft Teams", "ms-teams.exe", 200, "200,201,202", false),
                ("foo", "foo.exe", 302, "302", false),
            ],
            groups.Select(g => (g.Name, g.Exe, g.RootPid, string.Join(",", g.Pids), g.NeedsGlobalTap)));
    }
}
