using EchoDiar.Setup;

namespace EchoDiar.Tests;

// Guards the command line of EchoDiar-Setup: it installs only from an allowed feed, and the elevated instance
// (--trust-certificate) never runs with any other argument.
public sealed class SetupArgumentsTests
{
    private static readonly Uri DefaultFeed = new("https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller");

    [Theory]
    // No argument: the feed built into Setup.
    [InlineData("https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller")]
    // An https feed, the extension in any case.
    [InlineData("https://example.com/test/EchoDiar.AppInstaller", "--feed", "https://example.com/test/EchoDiar.AppInstaller")]
    // http to a private IPv4 (QA with serve-update.sh).
    [InlineData("http://192.168.64.1:8765/EchoDiar.appinstaller", "--feed", "http://192.168.64.1:8765/EchoDiar.appinstaller")]
    public void InstallsFromTheDefaultOrAnAllowedFeed(string expectedFeed, params string[] args)
    {
        var parsed = SetupArguments.Parse(args, DefaultFeed);

        Assert.Equal(SetupMode.Install, parsed.Mode);
        Assert.Equal(expectedFeed, parsed.Feed?.AbsoluteUri);
        Assert.Null(parsed.Error);
    }

    [Theory]
    // http to a public or named host, or just outside 172.16.0.0/12.
    [InlineData("--feed", "http://203.0.113.5/EchoDiar.appinstaller")]
    [InlineData("--feed", "http://ed.dinhanhthi.com/windows/EchoDiar.appinstaller")]
    [InlineData("--feed", "http://172.32.0.1/EchoDiar.appinstaller")]
    // Not an .appinstaller file, another scheme, credentials in the URI.
    [InlineData("--feed", "https://ed.dinhanhthi.com/windows/EchoDiar.msixbundle")]
    [InlineData("--feed", "file:///C:/EchoDiar.appinstaller")]
    [InlineData("--feed", "https://user@ed.dinhanhthi.com/windows/EchoDiar.appinstaller")]
    // A missing value, or two feeds.
    [InlineData("--feed")]
    [InlineData("--feed", "https://a.example/EchoDiar.appinstaller", "--feed", "https://b.example/EchoDiar.appinstaller")]
    public void RefusesAnyOtherFeed(params string[] args)
    {
        var parsed = SetupArguments.Parse(args, DefaultFeed);

        Assert.Equal(SetupMode.Error, parsed.Mode);
        Assert.Null(parsed.Feed);
        Assert.False(string.IsNullOrEmpty(parsed.Error));
    }

    [Theory]
    // Alone, it only trusts the certificate.
    [InlineData(true, "--trust-certificate")]
    // With anything else, in any order, it is refused.
    [InlineData(false, "--trust-certificate", "--feed", "https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller")]
    [InlineData(false, "--feed", "https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller", "--trust-certificate")]
    [InlineData(false, "--trust-certificate", "")]
    [InlineData(false, "--trust-certificate", "--trust-certificate")]
    public void TrustsTheCertificateOnlyWhenAlone(bool trusts, params string[] args)
    {
        var parsed = SetupArguments.Parse(args, DefaultFeed);

        Assert.Equal(trusts ? SetupMode.TrustCertificate : SetupMode.Error, parsed.Mode);
        Assert.Null(parsed.Feed);
        Assert.Equal(trusts, parsed.Error is null);
    }
}
