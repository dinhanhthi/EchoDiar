using EchoDiar.AI;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.AI;

// Guards what Settings › AI checks in a provider's row (security): the plain-http warning shows whenever a key would
// leave this PC unencrypted (fail-safe: an unusual spelling of this PC still warns), the extra headers saved in
// settings.json never hold a key header, and a 404 from a local or custom endpoint hints at the missing /v1.
public sealed class ProviderInputTests
{
    private static ProviderConfig Config(string baseURL, AuthStyle auth = AuthStyle.Bearer) => new()
    {
        Id = "custom", Kind = ProviderKind.OpenAICompatible, Preset = null, DisplayName = "Custom", BaseURL = baseURL,
        AuthStyle = auth, Enabled = true,
    };

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://127.4.5.6/v1", true)]
    [InlineData("http://[::1]:8080/v1", true)]
    [InlineData("http://localhost:1234/v1", true)]
    [InlineData("HTTP://LocalHost/v1", true)]
    [InlineData("  http://localhost/v1  ", true)]
    [InlineData("http://localhost./v1", false)]
    [InlineData("http://localhost.evil.com/v1", false)]
    [InlineData("http://0.0.0.0:8080/v1", false)]
    [InlineData("http://192.168.1.20:11434/v1", false)]
    [InlineData("http://128.0.0.1/v1", false)]
    [InlineData("http://::1/v1", false)] // not a URL without brackets
    [InlineData("not a url", false)]
    public void LoopbackIsOnlyThisPC(string baseURL, bool loopback) =>
        Assert.Equal(loopback, ProviderInput.IsLoopback(baseURL));

    // .NET reads the short IPv4 form 127.1 as 127.0.0.1, the address HttpClient connects to, so it is this PC.
    [Fact]
    public void ShortIPv4LoopbackIsReadAsDotNetConnectsToIt()
    {
        Assert.Equal("127.0.0.1", new Uri("http://127.1/").Host);
        Assert.True(ProviderInput.IsLoopback("http://127.1/v1"));
    }

    [Theory]
    [InlineData("http://192.168.1.20:11434/v1", true)]
    [InlineData("http://0.0.0.0:8080/v1", true)]
    [InlineData("http://localhost.evil.com/v1", true)]
    [InlineData("http://localhost./v1", true)]
    [InlineData("http://example.com/v1", true)]
    [InlineData("https://192.168.1.20/v1", false)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("http://127.0.0.1:11434/v1", false)]
    [InlineData("http://[::1]:8080/v1", false)]
    [InlineData("http://localhost:1234/v1", false)]
    [InlineData("http://::1/v1", false)] // not a URL: the client refuses it before sending anything
    public void PlainHttpToAnotherMachineWarns(string baseURL, bool warns) =>
        Assert.Equal(warns, ProviderInput.SendsKeyInClear(Config(baseURL), hasKey: true));

    [Fact]
    public void NoKeyNoWarning()
    {
        Assert.False(ProviderInput.SendsKeyInClear(Config("http://192.168.1.20/v1"), hasKey: false));
        Assert.False(ProviderInput.SendsKeyInClear(Config("http://192.168.1.20/v1", AuthStyle.None), hasKey: true));
    }

    [Fact]
    public void HeadersAreParsedOnePerLine()
    {
        var headers = ProviderInput.ParseHeaders(
            "HTTP-Referer: https://example.com\r\n  X-Title :  EchoDiar  \nx-time: 10:30\n\nno colon\n: no name\n"
            + "Bad Name: x\nBad\tTab: x\nBad/Slash: x\nBad(Paren): x\nCafé: x\n",
            out var refused);

        Assert.Equal(
            [("HTTP-Referer", "https://example.com"), ("X-Title", "EchoDiar"), ("x-time", "10:30")],
            headers.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => (h.Key, h.Value)));
        Assert.Empty(refused);
    }

    [Fact]
    public void KeyHeadersAreRefused()
    {
        var headers = ProviderInput.ParseHeaders(
            "Authorization: Bearer sk-secret\nproxy-authorization: Basic x\nX-API-KEY: k1\napi-key: k2\n"
            + "x-goog-api-key: k3\nXI-API-KEY: k4\nauthorization: again\nX-Title: kept",
            out var refused);

        Assert.Equal(["X-Title"], headers.Keys);
        Assert.Equal(
            ["Authorization", "proxy-authorization", "X-API-KEY", "api-key", "x-goog-api-key", "XI-API-KEY"], refused);
    }

    [Theory]
    [InlineData("http://192.168.1.20:8080", ProviderKind.Local, true)]
    [InlineData("http://192.168.1.20:8080/api", ProviderKind.Local, true)]
    [InlineData("http://192.168.1.20:8080/v1", ProviderKind.Local, false)]
    [InlineData("http://192.168.1.20:8080/V1/", ProviderKind.Local, false)]
    [InlineData("https://gateway.example.com/openai", ProviderKind.OpenAICompatible, true)]
    public void A404WithoutV1SuggestsIt(string baseURL, ProviderKind kind, bool hint)
    {
        var config = Config(baseURL) with { Kind = kind };

        Assert.Equal(hint, ProviderInput.SuggestsV1(config, LLMException.HttpStatus(404, "404 page not found")));
    }

    [Fact]
    public void OnlyA404FromALocalOrCustomEndpointSuggestsV1()
    {
        var custom = Config("http://192.168.1.20:8080");

        Assert.False(ProviderInput.SuggestsV1(custom, LLMException.HttpStatus(401, "")));
        Assert.False(ProviderInput.SuggestsV1(custom, LLMException.Provider("404")));
        Assert.False(ProviderInput.SuggestsV1(custom with { Preset = "openai" }, LLMException.HttpStatus(404, "")));
    }
}
