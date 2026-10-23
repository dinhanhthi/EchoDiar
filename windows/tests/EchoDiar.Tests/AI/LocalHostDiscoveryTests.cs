using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using EchoDiar.AI;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.AI;

// Local host discovery against a fake HttpMessageHandler and a fake `foundry service status` (no network, no real
// Foundry): probe URLs and order, failures read as not found, no key sent, redirects never leave the origin, and only
// a loopback Foundry endpoint is ever probed. Swift has no LocalHostDiscovery tests; these are new.
public sealed class LocalHostDiscoveryTests : IDisposable
{
    private const string FoundryRunning = "🟢 Model management service is running on http://localhost:5273/openai/status\r\n";
    private const string Models = """{"object":"list","data":[{"id":"model-a"},{"id":"model-b"}]}""";

    private static readonly string[] ProbeUrls =
    [
        "http://127.0.0.1:1234/v1/models",
        "http://127.0.0.1:11434/api/tags",
        "http://127.0.0.1:1337/v1/models",
        "http://127.0.0.1:9931/v1/models",
        "http://127.0.0.1:8080/v1/models",
        "http://127.0.0.1:8000/v1/models",
    ];

    private readonly string root = Directory.CreateTempSubdirectory("EchoDiar-discovery-tests-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task FoundHostsComeInProbeOrderWithFoundryLast()
    {
        var handler = new FakeHandler(request => request.RequestUri!.ToString() switch
        {
            "http://127.0.0.1:1234/v1/models" => Respond(200, Models),
            "http://127.0.0.1:11434/api/tags" => Respond(200, """{"models":[{"name":"qwen3:4b"}]}"""),
            "http://127.0.0.1:8080/v1/models" => Respond(200, """{"data":[]}"""),
            "http://127.0.0.1:5273/v1/models" => Respond(200, """{"data":[{"id":"phi-4-mini"}]}"""),
            _ => throw new HttpRequestException("Connection refused"),
        });

        var hosts = await Scan(handler, FoundryRunning);

        Assert.Equal(
            [
                ("lmstudio", "http://127.0.0.1:1234/v1", "model-a,model-b"),
                ("ollama", "http://127.0.0.1:11434", "qwen3:4b"),
                ("llamacpp", "http://127.0.0.1:8080/v1", ""),
                ("foundry-local", "http://127.0.0.1:5273/v1", "phi-4-mini"),
            ],
            hosts.Select(host => (host.Preset, host.BaseURL, string.Join(',', host.Models))));
        Assert.Equal(
            ProbeUrls.Append("http://127.0.0.1:5273/v1/models").Order(StringComparer.Ordinal),
            handler.Urls.Order(StringComparer.Ordinal));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Empty(request.Headers);
        });
    }

    [Fact]
    public async Task ErrorsBadBodiesAndNonSuccessAreNotFound()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Port switch
        {
            1234 => Respond(500, Models),
            11434 => Respond(200, Models), // An OpenAI-style body where Ollama's is expected.
            1337 => Respond(404, ""),
            9931 => Respond(200, "not json"),
            8080 => Respond(200, """{"data":null}"""),
            _ => throw new HttpRequestException("Connection refused"),
        });

        Assert.Empty(await Scan(handler, null));
        Assert.Equal(ProbeUrls.Order(StringComparer.Ordinal), handler.Urls.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ASlowHostTimesOut()
    {
        var handler = new FakeHandler(async (request, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return Respond(200, Models);
        });
        var clock = Stopwatch.StartNew();

        Assert.Empty(await LocalHostDiscovery.ScanAsync(TimeSpan.FromSeconds(0.2), handler, _ => Task.FromResult<string?>(FoundryRunning), Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
        Assert.Equal(7, handler.Requests.Count);
    }

    [Fact]
    public async Task ARedirectToAnotherHostIsNotFollowed()
    {
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.Port != 1234) throw new HttpRequestException("Connection refused");
            var response = Respond(302, "");
            response.Headers.Location = new Uri("http://192.168.1.20:1234/v1/models");
            return response;
        });

        Assert.Empty(await Scan(handler, null));
        Assert.DoesNotContain(handler.Urls, url => url.Contains("192.168", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingTheScanThrows()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var handler = new FakeHandler(request => Respond(200, Models));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalHostDiscovery.ScanAsync(TimeSpan.FromSeconds(1), handler, _ => Task.FromResult<string?>(null), cancel.Token));
    }

    // MARK: - Foundry Local

    [Theory]
    [InlineData(FoundryRunning, "http://127.0.0.1:5273/v1")]
    [InlineData("Service is Started on http://127.0.0.1:61234, PID 4242!", "http://127.0.0.1:61234/v1")]
    [InlineData("\u001b[32mrunning on HTTP://LOCALHOST:5273/openai/status\u001b[0m", "http://127.0.0.1:5273/v1")]
    [InlineData("proxy http://10.0.0.5:5273/x, then http://localhost:6000/openai/status", "http://127.0.0.1:6000/v1")]
    [InlineData("🔴 Model management service is not running!", null)]
    [InlineData("\0\u0001garbage {\"x\": http:/ 127.0.0.1 :5273", null)]
    [InlineData("running on http://10.0.0.5:5273/openai/status", null)]
    [InlineData("running on http://127.0.0.1.evil.example:5273/", null)]
    [InlineData("running on http://localhost:5273@evil.example/", null)]
    [InlineData("running on http://localhost.evil.example:5273/", null)]
    [InlineData("running on http://127.0.0.1:5273.evil.example/", null)]
    [InlineData("running on https://127.0.0.1:5273/", null)]
    [InlineData("running on xhttp://127.0.0.1:5273/", null)]
    [InlineData("running on http://127.0.0.1:0/", null)]
    [InlineData("running on http://127.0.0.1:99999/", null)]
    [InlineData("running on http://127.0.0.1:123456/", null)]
    public void FoundryEndpointIsOnlyALoopbackPort(string output, string? expected) =>
        Assert.Equal(expected, LocalHostDiscovery.FoundryBaseURL(output));

    [Theory]
    [InlineData("🔴 Model management service is not running!")]
    [InlineData("garbage")]
    [InlineData("running on http://10.0.0.5:5273/openai/status")]
    [InlineData(null)]
    public async Task NoLoopbackEndpointProbesNothingForFoundry(string? output)
    {
        var handler = new FakeHandler(request => Respond(200, Models));

        var hosts = await Scan(handler, output);

        Assert.DoesNotContain(hosts, host => host.Preset == "foundry-local");
        Assert.Equal(ProbeUrls.Order(StringComparer.Ordinal), handler.Urls.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AFailingFoundryRunnerIsNotFound()
    {
        var handler = new FakeHandler(request => throw new HttpRequestException("Connection refused"));

        var hosts = await LocalHostDiscovery.ScanAsync(
            TimeSpan.FromSeconds(1), handler, _ => throw new InvalidOperationException("boom"), Token);

        Assert.Empty(hosts);
    }

    [Fact]
    public async Task FoundryThatIsMissingOrANonNpmCmdIsNotFound()
    {
        Assert.Null(await LocalHostDiscovery.FoundryStatusAsync(Shell(root), TimeSpan.FromSeconds(3), null, Token));

        File.WriteAllText(Path.Combine(root, "foundry.cmd"), "@echo off\r\n\"%~dp0\\real\\foundry.exe\" %*\r\n");
        Assert.Null(await LocalHostDiscovery.FoundryStatusAsync(Shell(root), TimeSpan.FromSeconds(3), null, Token));
    }

    [Fact]
    public async Task FoundryStatusRunsWithItsArgumentsAndTheAllowlistOnly()
    {
        SkipOnWindows();
        var shell = FakeFoundry("""printf '%s %s|%s|%s\n' "$1" "$2" "${USER-unset}" "$HOME" """);

        var output = await LocalHostDiscovery.FoundryStatusAsync(shell, TimeSpan.FromSeconds(3), null, Token);

        Assert.Equal($"service status|unset|{Path.Combine(root, "home")}\n", output);
    }

    [Fact]
    public async Task FoundryStatusThatHangsIsKilledAfterTheTimeout()
    {
        SkipOnWindows();
        var shell = FakeFoundry("exec /bin/sleep 600");
        var clock = Stopwatch.StartNew();

        Assert.Null(await LocalHostDiscovery.FoundryStatusAsync(shell, TimeSpan.FromSeconds(0.5), null, Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
    }

    [Fact]
    public void FoundryLocalPresetFollowsVllm()
    {
        var presets = ProviderConfig.Presets();
        var index = presets.FindIndex(preset => preset.Id == "foundry-local");

        Assert.Equal("vllm", presets[index - 1].Id);
        var foundry = presets[index];
        Assert.Equal(
            (ProviderKind.Local, "foundry-local", "Foundry Local", "http://127.0.0.1:5273/v1", AuthStyle.None, false),
            (foundry.Kind, foundry.Preset, foundry.DisplayName, foundry.BaseURL, foundry.AuthStyle, foundry.Enabled));
    }

    // MARK: - Helpers

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<IReadOnlyList<DiscoveredHost>> Scan(FakeHandler handler, string? foundryOutput) =>
        LocalHostDiscovery.ScanAsync(TimeSpan.FromSeconds(5), handler, _ => Task.FromResult(foundryOutput), Token);

    private ShellEnvironment Shell(string bin)
    {
        var home = Path.Combine(root, "home");
        return new(() => bin, name => name == "USERPROFILE" ? home : null);
    }

    private ShellEnvironment FakeFoundry(string body)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        var path = Path.Combine(bin, "foundry.exe");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        return Shell(bin);
    }

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("The fake foundry is a shell script; Windows starts only real .exe files.");
    }

    private static HttpResponseMessage Respond(int status, string body) =>
        new((HttpStatusCode)status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    /// <summary>Answers every request with <c>respond</c> and records it; the probes run in parallel.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((r, _) => Task.FromResult(respond(r))) { }

        private readonly ConcurrentQueue<HttpRequestMessage> requests = new();

        public IReadOnlyList<HttpRequestMessage> Requests => [.. requests];
        public IEnumerable<string> Urls => requests.Select(request => request.RequestUri!.ToString());

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue(request);
            var response = await respond(request, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
    }
}
