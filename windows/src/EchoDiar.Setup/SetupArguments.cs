using System;
using System.Net;

namespace EchoDiar.Setup;

/// <summary>What one run of Setup does.</summary>
internal enum SetupMode
{
    /// <summary>Show the window and install from <see cref="SetupArguments.Feed"/>.</summary>
    Install,

    /// <summary>The elevated instance: import the embedded certificate, nothing else.</summary>
    TrustCertificate,

    /// <summary>The command line is refused; <see cref="SetupArguments.Error"/> says why.</summary>
    Error,
}

/// <summary>The command line of EchoDiar-Setup. Pure, so the tests compile it on net10.0 too. The elevated instance
/// takes exactly <c>--trust-certificate</c> and nothing else; an install takes the built-in feed or <c>--feed</c>
/// with an https <c>.appinstaller</c> URI (or http to a private or loopback IPv4 for QA).</summary>
internal sealed class SetupArguments
{
    public const string TrustCertificateArgument = "--trust-certificate";
    private const string FeedArgument = "--feed";

    private const string Usage =
        "Usage: EchoDiar-Setup [--feed <uri>]\n" +
        "<uri> is the https address of an .appinstaller file, or http to a private or loopback IPv4 address.";

    private SetupArguments(SetupMode mode, Uri? feed, string? error)
    {
        Mode = mode;
        Feed = feed;
        Error = error;
    }

    public SetupMode Mode { get; }

    /// <summary>The App Installer feed to install from; set only in <see cref="SetupMode.Install"/>.</summary>
    public Uri? Feed { get; }

    /// <summary>The reason and the usage; set only in <see cref="SetupMode.Error"/>.</summary>
    public string? Error { get; }

    /// <param name="defaultFeed">The feed built into Setup (its <c>SetupFeedUri</c> metadata), used without
    /// <c>--feed</c>. It is a build input, so it is not checked here.</param>
    public static SetupArguments Parse(string[] args, Uri defaultFeed)
    {
        if (args.Length == 1 && args[0] == TrustCertificateArgument)
            return new SetupArguments(SetupMode.TrustCertificate, null, null);

        Uri? feed = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == TrustCertificateArgument) return Fail($"{TrustCertificateArgument} takes no other argument.");
            if (args[i] != FeedArgument) return Fail($"Unknown argument \"{args[i]}\".");
            if (feed is not null) return Fail($"{FeedArgument} is given more than once.");
            if (i + 1 == args.Length) return Fail($"{FeedArgument} needs a URI.");
            feed = AllowedFeed(args[++i]);
            if (feed is null) return Fail($"\"{args[i]}\" is not an allowed feed.");
        }
        return new SetupArguments(SetupMode.Install, feed ?? defaultFeed, null);
    }

    private static SetupArguments Fail(string reason) =>
        new(SetupMode.Error, null, reason + "\n\n" + Usage);

    private static Uri? AllowedFeed(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var feed)) return null;
        if (feed.UserInfo.Length > 0) return null;
        if (!feed.AbsolutePath.EndsWith(".appinstaller", StringComparison.OrdinalIgnoreCase)) return null;
        if (feed.Scheme == Uri.UriSchemeHttps) return feed;
        return feed.Scheme == Uri.UriSchemeHttp && IsPrivateOrLoopbackIPv4(feed) ? feed : null;
    }

    /// <summary>10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 or 127.0.0.0/8; never a named or IPv6 host.</summary>
    private static bool IsPrivateOrLoopbackIPv4(Uri feed)
    {
        if (feed.HostNameType != UriHostNameType.IPv4 || !IPAddress.TryParse(feed.Host, out var address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 127
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}
