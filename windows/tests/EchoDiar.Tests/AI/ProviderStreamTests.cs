using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EchoDiar.AI;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.AI;

// The HTTP path end to end (request, status check, LF splitting, per-provider parsing, redirects, error redaction)
// against canned bodies served by a fake HttpMessageHandler (ProviderStreamTests.swift). No network.
public sealed class ProviderStreamTests
{
    private const string Key = "sk-test-secret-1234";

    private sealed record Case(ProviderConfig Config, int Status, string Body, string? Text = null, string? ProviderError = null);

    private static readonly Dictionary<string, Case> Cases = new()
    {
        ["OpenAI SSE: CRLF, comments, U+2028 kept inside a chunk, stops at [DONE]"] = new(
            Config("openai", "openai.stub"), 200,
            ": keep-alive\r\n\r\n"
            + """data: {"choices":[{"delta":{"role":"assistant"}}]}""" + "\r\n\r\n"
            + """data: {"choices":[{"delta":{"content":"Hel"}}]}""" + "\r\n\r\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"lo\u2028there\"}}]}\n\n"
            + "data: [DONE]\r\n\r\n"
            + """data: {"choices":[{"delta":{"content":"after done"}}]}""" + "\n\n",
            Text: "Hello\u2028there"),
        ["Anthropic named events, stops at message_stop"] = new(
            Config("anthropic", "anthropic.stub"), 200,
            "event: message_start\r\n" + """data: {"type":"message_start","message":{}}""" + "\r\n\r\n"
            + "event: content_block_delta\n"
            + """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Sum"}}""" + "\n\n"
            + "event: ping\n" + """data: {"type":"ping"}""" + "\n\n"
            + "event: content_block_delta\n"
            + """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"mary."}}""" + "\n\n"
            + "event: message_stop\n" + """data: {"type":"message_stop"}""" + "\n\n"
            + "event: content_block_delta\n"
            + """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"late"}}""" + "\n\n",
            Text: "Summary."),
        ["Anthropic mid-stream error event"] = new(
            Config("anthropic", "anthropic-error.stub"), 200,
            "event: content_block_delta\n"
            + """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Su"}}""" + "\n\n"
            + "event: error\n" + """data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""" + "\n\n",
            ProviderError: "Overloaded"),
        ["Ollama JSONL until done"] = new(
            Config("ollama", "ollama.stub"), 200,
            """{"message":{"role":"assistant","content":"Hi"},"done":false}""" + "\n"
            + """{"message":{"role":"assistant","content":" all"},"done":false}""" + "\n"
            + """{"message":{"role":"assistant","content":""},"done":true}""" + "\n"
            + """{"message":{"role":"assistant","content":"after done"},"done":false}""" + "\n",
            Text: "Hi all"),
        ["Non-2xx reports the status"] = new(
            Config("openai", "openai-401.stub"), 401, """{"error":{"message":"Incorrect API key"}}"""),
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task StreamYieldsTextOrThrows(string name)
    {
        var testCase = Cases[name];
        var handler = new FakeHandler(_ => Respond(testCase.Status, testCase.Body));
        var provider = Provider(testCase.Config, handler);

        var text = new StringBuilder();
        var thrown = await Record.ExceptionAsync(async () =>
        {
            await foreach (var delta in provider.StreamAsync([new(LLMRole.User, "x")], "m", 10, TestContext.Current.CancellationToken))
                text.Append(delta);
        });

        if (testCase.Text is { } expected)
        {
            Assert.Null(thrown);
            Assert.Equal(expected, text.ToString());
        }
        else if (testCase.ProviderError is { } message)
        {
            var error = Assert.IsType<LLMException>(thrown);
            Assert.Equal(LLMErrorKind.Provider, error.Kind);
            Assert.Equal(message, error.Message);
        }
        else
        {
            var error = Assert.IsType<LLMException>(thrown);
            Assert.Equal(LLMErrorKind.HttpStatus, error.Kind);
            Assert.Equal(testCase.Status, error.StatusCode);
            Assert.Equal(testCase.Body, error.Body);
        }
    }

    // An echoed key must never reach the error text (UI, logs), on the streaming and the JSON path; the body is cut at 2 KB.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ErrorBodyIsCutAndKeysRedacted(bool streaming)
    {
        var body = $$$"""{"error":{"message":"Incorrect API key {{{Key}}} (Bearer {{{Key}}})"}}""" + new string('x', 3000);
        var provider = Provider(Config("openai", "openai.stub"), new FakeHandler(_ => Respond(401, body)));

        var error = await Assert.ThrowsAsync<LLMException>(async () =>
        {
            if (!streaming)
            {
                await provider.ListModelsAsync(TestContext.Current.CancellationToken);
                return;
            }
            await foreach (var _ in provider.StreamAsync([new(LLMRole.User, "x")], "m", 10, TestContext.Current.CancellationToken)) { }
        });

        Assert.Equal(401, error.StatusCode);
        Assert.DoesNotContain(Key, error.Message);
        Assert.DoesNotContain(Key, error.Body);
        Assert.Equal(body[..2048].Replace($"Bearer {Key}", "[redacted]").Replace(Key, "[redacted]"), error.Body);
    }

    // A cross-origin redirect is refused (the 3xx is the error, nothing reaches the other host); a same-origin one is
    // followed with the auth header kept.
    [Theory]
    [InlineData("https://evil.example/chat/completions", false)]
    [InlineData("http://openai.stub/chat/completions", false)]
    [InlineData("/v2/chat/completions", true)]
    public async Task RedirectIsFollowedOnlyWithinTheOrigin(string location, bool followed)
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath == "/chat/completions"
            ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } }
            : Respond(200, """data: {"choices":[{"delta":{"content":"ok"}}]}""" + "\n\ndata: [DONE]\n\n"));
        var provider = Provider(Config("openai", "openai.stub"), handler);

        var text = new StringBuilder();
        var thrown = await Record.ExceptionAsync(async () =>
        {
            await foreach (var delta in provider.StreamAsync([new(LLMRole.User, "x")], "m", 10, TestContext.Current.CancellationToken))
                text.Append(delta);
        });

        if (followed)
        {
            Assert.Null(thrown);
            Assert.Equal("ok", text.ToString());
            Assert.Equal(["/chat/completions", "/v2/chat/completions"], handler.Requests.Select(r => r.RequestUri!.AbsolutePath));
            Assert.All(handler.Requests, r => Assert.Equal($"Bearer {Key}", r.Headers.Authorization?.ToString()));
            Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        }
        else
        {
            Assert.Equal(307, Assert.IsType<LLMException>(thrown).StatusCode);
            Assert.Single(handler.Requests);
        }
    }

    [Theory]
    [InlineData("https://api.x.ai/v1/models", "https://api.x.ai/v2/models?page=2", true)]
    [InlineData("https://api.x.ai/v1", "https://API.X.AI/v1", true)]
    [InlineData("https://api.x.ai/v1", "https://api.x.ai:443/v1", true)]
    [InlineData("http://127.0.0.1:11434/api/tags", "http://127.0.0.1:11434/api/tags/", true)]
    [InlineData("https://api.x.ai/v1", "http://api.x.ai/v1", false)]
    [InlineData("https://api.x.ai/v1", "https://evil.example/v1", false)]
    [InlineData("https://api.x.ai/v1", "https://x.ai/v1", false)]
    [InlineData("https://api.x.ai/v1", "https://api.x.ai:8443/v1", false)]
    [InlineData("http://127.0.0.1:1234/v1", "http://127.0.0.1:1235/v1", false)]
    [InlineData("http://127.0.0.1:1234/v1", "http://localhost:1234/v1", false)]
    public void RedirectOnlyWithinTheSameOrigin(string from, string to, bool allowed) =>
        Assert.Equal(allowed, ProviderHttp.AllowsRedirect(new Uri(from), new Uri(to)));

    [Theory]
    [InlineData("openai", """{"object":"list","data":[{"id":"gpt-a","object":"model"},{"id":"gpt-b"}]}""")]
    [InlineData("anthropic", """{"data":[{"id":"gpt-a","type":"model"},{"id":"gpt-b"}],"has_more":false}""")]
    [InlineData("ollama", """{"models":[{"name":"gpt-a","size":1},{"name":"gpt-b"}]}""")]
    public async Task ListModelsDecodesEachHostsList(string preset, string body)
    {
        var provider = Provider(Config(preset, preset + ".stub"), new FakeHandler(_ => Respond(200, body)));

        Assert.Equal(["gpt-a", "gpt-b"], await provider.ListModelsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbedSendsBatchesOf64AndKeepsInputOrder()
    {
        // Ollama /api/embed answers {"embeddings": [...]}; text "k" gets the vector [1, k].
        var sizes = new List<int>();
        var handler = new FakeHandler(async request =>
        {
            var inputs = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["input"]!.AsArray();
            sizes.Add(inputs.Count);
            var vectors = inputs.Select(n => $"[1,{n!.GetValue<string>()}]");
            return Respond(200, $$"""{"embeddings":[{{string.Join(",", vectors)}}]}""");
        });
        var client = new OpenAICompatibleClient(Config("ollama", "ollama.stub"), null, handler);
        var texts = Enumerable.Range(0, 130).Select(i => i.ToString()).ToList();

        var vectors = await client.EmbedAsync(texts, "e", TestContext.Current.CancellationToken);

        Assert.Equal([64, 64, 2], sizes);
        Assert.Equal(130, vectors.Count);
        for (var k = 0; k < vectors.Count; k++)
        {
            Assert.Equal(k, vectors[k][1] / vectors[k][0], 3);
            Assert.Equal(1, MathF.Sqrt(vectors[k][0] * vectors[k][0] + vectors[k][1] * vectors[k][1]), 5);
        }
    }

    [Fact]
    public async Task LocalProviderStreamsTheGeneratorsPiecesOnceLoaded()
    {
        var local = new FakeLocalProvider();
        ILLMProvider provider = local;

        var text = new StringBuilder();
        await foreach (var delta in provider.StreamAsync([new(LLMRole.User, "x")], "m", 10, TestContext.Current.CancellationToken))
            text.Append(delta);

        Assert.Equal("Hello world", text.ToString());
        Assert.Equal(["load", "unload"], local.Events);
    }

    private static ProviderConfig Config(string preset, string host)
    {
        var config = ProviderConfig.Presets().Single(p => p.Id == preset);
        config.BaseURL = $"https://{host}";
        return config;
    }

    private static ILLMProvider Provider(ProviderConfig config, HttpMessageHandler handler) =>
        config.Kind == ProviderKind.Anthropic
            ? new AnthropicClient(config, Key, handler)
            : new OpenAICompatibleClient(config, Key, handler);

    private static HttpResponseMessage Respond(int status, string body) =>
        new((HttpStatusCode)status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    /// <summary>Answers every request with <c>respond</c>, all at once (the client does the line splitting), and records it.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this(r => Task.FromResult(respond(r))) { }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = await respond(request);
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class FakeLocalProvider : ILocalLLMProvider
    {
        public List<string> Events { get; } = [];

        public async Task<T> WithLoadedModelAsync<T>(
            string model, Func<ILocalLLMGenerator, Task<T>> body, CancellationToken cancellationToken = default)
        {
            Events.Add("load");
            try { return await body(new Generator()); }
            finally { Events.Add("unload"); }
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["m"]);

        public ValueTask<int> ContextWindowAsync(string model, CancellationToken cancellationToken = default) => ValueTask.FromResult(8192);

        private sealed class Generator : ILocalLLMGenerator
        {
            public Task<string> GenerateAsync(
                IReadOnlyList<LLMMessage> messages, int maxTokens, Action<string> onText, CancellationToken cancellationToken = default)
            {
                onText("Hello");
                onText(" world");
                return Task.FromResult("Hello world");
            }
        }
    }
}
