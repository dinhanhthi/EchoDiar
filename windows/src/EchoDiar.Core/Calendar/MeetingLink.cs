using System.Text.RegularExpressions;

namespace EchoDiar.Core.Calendar;

/// <summary>A video-call link in a calendar event (port of macOS MeetingLink). Event content is untrusted: only
/// <c>https</c> links on allowlisted hosts count, without credentials or a non-default port.</summary>
/// <param name="ServiceName">Brand name, not localized.</param>
public sealed partial record MeetingLink(string ServiceName, Uri Url)
{
    /// <summary>Host (or parent domain) → service.</summary>
    private static readonly (string Host, string Name)[] Services =
    [
        ("zoom.us", "Zoom"),
        ("meet.google.com", "Google Meet"),
        ("teams.microsoft.com", "Microsoft Teams"),
        ("teams.live.com", "Microsoft Teams"),
        ("webex.com", "Webex"),
    ];

    private const string TrailingPunctuation = ".,;:!?)]}'";

    /// <summary>The first allowlisted link in the event URL, then its online meeting link, then its location, then
    /// its notes.</summary>
    public static MeetingLink? Find(Uri? uri, string? onlineMeetingLink, string? location, string? notes)
    {
        IEnumerable<Uri> candidates = new[] { uri, Parse(onlineMeetingLink?.Trim()) }.OfType<Uri>()
            .Concat(Links(location))
            .Concat(Links(notes));
        return candidates.Select(ForUri).FirstOrDefault(link => link is not null);
    }

    private static MeetingLink? ForUri(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps) return null;
        if (url.UserInfo.Length > 0 || !url.IsDefaultPort) return null;
        var host = url.Host.ToLowerInvariant();
        foreach (var (serviceHost, name) in Services)
            if (host == serviceHost || host.EndsWith("." + serviceHost, StringComparison.Ordinal))
                return new MeetingLink(name, url);
        return null;
    }

    private static IEnumerable<Uri> Links(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match match in HttpsUrl().Matches(text))
            if (Parse(match.Value.TrimEnd(TrailingPunctuation.ToCharArray())) is { } url)
                yield return url;
    }

    private static Uri? Parse(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : null;

    [GeneratedRegex("""https://[^\s<>"]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpsUrl();
}
