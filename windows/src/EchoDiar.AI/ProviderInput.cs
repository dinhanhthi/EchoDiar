using EchoDiar.Core.Settings;

namespace EchoDiar.AI;

/// <summary>
/// The checks Settings › AI makes on what the user types in a provider's row: its base URL (this PC or not, the key sent
/// in clear, the host shown, the <c>/v1</c> hint) and its extra headers.
/// </summary>
public static class ProviderInput
{
    /// <summary>
    /// Header names that carry a key: refused in the extra headers, which are saved in settings.json (keys go to
    /// Credential Manager through the API key field).
    /// </summary>
    public static readonly IReadOnlySet<string> KeyHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "x-api-key", "api-key", "x-goog-api-key", "xi-api-key",
    };

    /// <summary>
    /// Whether the key would go over plain http to another machine: <paramref name="config"/> sends a key (it has one,
    /// or one is being typed) and its base URL is <c>http://</c> to a host that is not this PC.
    /// </summary>
    public static bool SendsKeyInClear(ProviderConfig config, bool hasKey) =>
        config.AuthStyle != AuthStyle.None && hasKey
        && Uri.TryCreate(config.BaseURL.Trim(), UriKind.Absolute, out var url)
        && url.Scheme == Uri.UriSchemeHttp && !IsLoopback(config.BaseURL);

    /// <summary>
    /// Whether <paramref name="baseURL"/>'s host is this PC: localhost, 127.0.0.0/8 or ::1 (macOS <c>isLoopback</c>).
    /// Anything else, a trailing dot included, counts as another machine (fail-safe: it warns). Short and decimal IPv4
    /// forms (127.1) are normalized by Uri to the address HttpClient connects to, so they count as this PC.
    /// </summary>
    public static bool IsLoopback(string baseURL)
    {
        if (!Uri.TryCreate(baseURL.Trim(), UriKind.Absolute, out var url)) return false;
        var host = url.Host.ToLowerInvariant().Trim('[', ']');
        if (host is "localhost" or "::1") return true;
        var octets = host.Split('.');
        return octets.Length == 4 && octets[0] == "127" && octets.All(o => byte.TryParse(o, out _));
    }

    /// <summary><c>host:port</c> of an http(s) URL, null when the text is not one (macOS <c>hostAndPort</c>).</summary>
    public static string? HostAndPort(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https") || url.Host.Length == 0) return null;
        return HasExplicitPort(text) ? $"{url.Host}:{url.Port}" : url.Host;
    }

    /// <summary>Whether the URL's authority names a port (Swift <c>url.port</c> is nil without one, even for :80).</summary>
    private static bool HasExplicitPort(string text)
    {
        var authority = text.Trim();
        var start = authority.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return false;
        authority = authority[(start + 3)..];
        var end = authority.IndexOfAny(['/', '?', '#']);
        if (end >= 0) authority = authority[..end];
        var colon = authority.LastIndexOf(':');
        return colon >= 0 && colon > authority.LastIndexOf(']');
    }

    /// <summary>
    /// Whether a failure of <paramref name="config"/> is worth the "ends with /v1" hint: an HTTP 404 from a local host
    /// or a custom endpoint whose base URL path does not end with <c>/v1</c>.
    /// </summary>
    public static bool SuggestsV1(ProviderConfig config, Exception error) =>
        error is LLMException { Kind: LLMErrorKind.HttpStatus, StatusCode: 404 }
        && (config.Kind == ProviderKind.Local || config.Preset is null)
        && Uri.TryCreate(config.BaseURL.Trim(), UriKind.Absolute, out var url)
        && !url.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The extra headers typed one per line as <c>Name: value</c>: lines without a valid header name are skipped, and
    /// lines naming a <see cref="KeyHeaders"/> header are left out and their names put in <paramref name="refused"/>.
    /// </summary>
    public static Dictionary<string, string> ParseHeaders(string text, out IReadOnlyList<string> refused)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<string> keys = [];
        foreach (var line in text.Split('\n', '\r'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            if (name.Length == 0 || name.Any(c => c <= ' ' || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c))) continue;
            if (KeyHeaders.Contains(name))
            {
                if (!keys.Contains(name, StringComparer.OrdinalIgnoreCase)) keys.Add(name);
                continue;
            }
            headers[name] = line[(colon + 1)..].Trim();
        }
        refused = keys;
        return new Dictionary<string, string>(headers);
    }
}
