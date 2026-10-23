namespace EchoDiar.Core;

/// <summary>An app whose microphone use suggests a call. Browsers are weaker evidence (any web page can use the mic).</summary>
/// <param name="Exes">Lowercase exe names, matched against <c>AudioProcessGroup.Exe</c> (helpers are already grouped
/// under their app).</param>
public sealed record MeetingApp(string Name, IReadOnlyList<string> Exes, bool IsBrowser = false)
{
    /// <summary>The app the user sees (<c>Exes[0]</c>): the <c>perAppBehavior</c> key of its "Always"/"Ignore" choice.</summary>
    public string AppExe => Exes[0];
}

/// <summary>The meeting apps detected on Windows (port of Swift <c>MeetingApps</c>, with exe names for bundle IDs).</summary>
public static class MeetingApps
{
    /// <summary>Meeting apps, then browsers (Meet, Teams web, Zoom web).</summary>
    public static IReadOnlyList<MeetingApp> Known { get; } =
    [
        new("Zoom", ["zoom.exe"]),
        new("Microsoft Teams", ["ms-teams.exe", "teams.exe"]),
        new("Slack", ["slack.exe"]),
        new("Webex", ["ciscocollabhost.exe", "webexmta.exe"]),
        new("Discord", ["discord.exe"]),
        new("Skype", ["skype.exe"]),

        new("Google Chrome", ["chrome.exe"], IsBrowser: true),
        new("Arc", ["arc.exe"], IsBrowser: true),
        new("Microsoft Edge", ["msedge.exe"], IsBrowser: true),
        new("Brave", ["brave.exe"], IsBrowser: true),
        new("Firefox", ["firefox.exe"], IsBrowser: true),
        new("Opera", ["opera.exe"], IsBrowser: true),
        new("Vivaldi", ["vivaldi.exe"], IsBrowser: true),
    ];

    /// <summary>The known app with <paramref name="exe"/> (case-insensitive), or null.</summary>
    public static MeetingApp? ForExe(string exe) =>
        Known.FirstOrDefault(app => app.Exes.Contains(exe, StringComparer.OrdinalIgnoreCase));
}
