namespace EchoDiar.Core.Audio;

/// <summary>One process from a process snapshot, with whether it has an active render (playback) session.</summary>
/// <param name="Exe">Lowercase executable file name (<c>chrome.exe</c>); Windows source identifiers are these names.</param>
/// <param name="Path">Full image path. <see cref="AudioProcessGrouping.Group"/> does not use it; it is for callers (icons).</param>
public sealed record ProcessRow(int Pid, int ParentPid, string Exe, string Path, bool HasRenderSession);

/// <summary>The processes of one app, as offered for "one app" capture.</summary>
/// <param name="Exe">The root's lowercase exe name, the source identifier.</param>
/// <param name="RootPid">The process that process loopback (INCLUDE_TARGET_PROCESS_TREE) targets.</param>
/// <param name="Pids">Every row that folds into this group, with or without a session, ascending; contains <paramref name="RootPid"/>.</param>
/// <param name="NeedsGlobalTap">Always false for now: process loopback captures a whole process tree (verified in 2.6).
/// It becomes true only if capture has to fall back to endpoint loopback for this app.</param>
public sealed record AudioProcessGroup(string Name, string Exe, int RootPid, IReadOnlyList<int> Pids, bool NeedsGlobalTap);

/// <summary>Groups audio processes by the app they belong to (the Windows counterpart of macOS AudioProcessList.groups).</summary>
public static class AudioProcessGrouping
{
    private const string WebView2Exe = "msedgewebview2.exe";

    /// <summary>Shell and system hosts: never an app of their own; their children with another exe are.</summary>
    private static readonly HashSet<string> NeverRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "svchost.exe", "audiodg.exe", "system", "services.exe", "csrss.exe",
    };

    /// <summary>Display names of common apps. Phase 19's MeetingApps table supersedes this one.</summary>
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ms-teams.exe"] = "Microsoft Teams",
        ["teams.exe"] = "Microsoft Teams (classic)",
        ["zoom.exe"] = "Zoom",
        ["chrome.exe"] = "Google Chrome",
        ["msedge.exe"] = "Microsoft Edge",
        ["firefox.exe"] = "Firefox",
        ["brave.exe"] = "Brave",
        ["slack.exe"] = "Slack",
        ["discord.exe"] = "Discord",
        ["webex.exe"] = "Webex",
        ["ciscocollabhost.exe"] = "Webex",
        ["whatsapp.exe"] = "WhatsApp",
        ["skype.exe"] = "Skype",
    };

    /// <summary>
    /// Rules: the root is the top ancestor with the same exe, so browser audio utility children (same exe as the
    /// browser; rows carry no command line) fold into their browser. <c>msedgewebview2.exe</c> folds into its nearest
    /// non-WebView2 ancestor app. Never-root processes are dropped. Only groups with at least one render session are
    /// returned, ordered by name (ordinal), then root pid. The caller filters out its own process.
    /// </summary>
    public static List<AudioProcessGroup> Group(IReadOnlyList<ProcessRow> rows)
    {
        var byPid = new Dictionary<int, ProcessRow>();
        foreach (var row in rows) byPid.TryAdd(row.Pid, row);

        var members = new Dictionary<int, List<ProcessRow>>();
        foreach (var row in byPid.Values)
        {
            if (Root(row, byPid) is not { } root) continue;
            if (!members.TryGetValue(root.Pid, out var list)) members[root.Pid] = list = [];
            list.Add(row);
        }

        return members
            .Where(m => m.Value.Any(r => r.HasRenderSession))
            .Select(m => ToGroup(byPid[m.Key], m.Value))
            .OrderBy(g => g.Name, StringComparer.Ordinal)
            .ThenBy(g => g.RootPid)
            .ToList();
    }

    private static ProcessRow? Root(ProcessRow row, Dictionary<int, ProcessRow> byPid)
    {
        var app = row;
        if (Is(row, WebView2Exe)
            && Ancestors(row, byPid).FirstOrDefault(a => !Is(a, WebView2Exe)) is { } host
            && !NeverRoots.Contains(host.Exe))
        {
            app = host;
        }
        if (NeverRoots.Contains(app.Exe)) return null;
        return Ancestors(app, byPid).TakeWhile(a => Is(a, app.Exe)).LastOrDefault() ?? app;
    }

    /// <summary>Parent, grandparent, ...; stops at a missing parent or a cycle (pid 0 is its own parent, pids get reused).</summary>
    private static IEnumerable<ProcessRow> Ancestors(ProcessRow row, Dictionary<int, ProcessRow> byPid)
    {
        var seen = new HashSet<int> { row.Pid };
        while (byPid.TryGetValue(row.ParentPid, out var parent) && seen.Add(parent.Pid))
        {
            yield return parent;
            row = parent;
        }
    }

    private static bool Is(ProcessRow row, string exe) => string.Equals(row.Exe, exe, StringComparison.OrdinalIgnoreCase);

    private static AudioProcessGroup ToGroup(ProcessRow root, List<ProcessRow> rows)
    {
        var exe = root.Exe.ToLowerInvariant();
        var name = DisplayNames.TryGetValue(exe, out var known) ? known : Path.GetFileNameWithoutExtension(exe);
        return new AudioProcessGroup(name, exe, root.Pid, rows.Select(r => r.Pid).Order().ToList(), NeedsGlobalTap: false);
    }
}
