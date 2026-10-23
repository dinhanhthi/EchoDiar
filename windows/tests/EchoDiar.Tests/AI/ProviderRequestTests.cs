using System.Text.Json;
using System.Text.Json.Nodes;
using EchoDiar.AI;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.AI;

// The API request contract and where secrets and transcript text may go (ProviderRequestTests.swift). No network: only
// the pure Make*Request builders (and the embeddings decoder) are exercised, with a fake key.
public sealed class ProviderRequestTests
{
    private const string Key = "sk-test-fake-0000";
    private const string Transcript = "Alice [00:05]: the launch moves to Friday";

    private static readonly LLMMessage[] Messages =
        [new(LLMRole.System, "Summarize the meeting."), new(LLMRole.User, Transcript)];

    [Fact]
    public async Task OpenAICompatibleRequestUsesBearerAndKeepsTranscriptInUserMessage()
    {
        using var request = OpenAICompatibleClient.MakeRequest(Preset("openai"), Key, Messages, "m", 100);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
        Assert.Equal($"Bearer {Key}", Header(request, "Authorization"));
        Assert.Null(Header(request, "api-key"));
        Assert.Equal(TimeSpan.FromSeconds(600), Timeout(request));
        var body = await Json(request);
        Assert.True(body["stream"]!.GetValue<bool>());
        var sent = body["messages"]!.AsArray();
        Assert.Equal(["user"], sent.Where(m => m!["content"]!.GetValue<string>().Contains(Transcript)).Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal(["system", "user"], sent.Select(m => m!["role"]!.GetValue<string>()));
        // OpenAI's newer models reject max_tokens.
        Assert.Equal(100, body["max_completion_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_tokens"));
        ExpectSecretsOnlyWhereAllowed(request);
    }

    [Fact]
    public async Task AzureSendsKeyInApiKeyHeader()
    {
        var config = Preset("azure");
        config.BaseURL = "https://res.openai.azure.com/openai/v1";
        using var request = OpenAICompatibleClient.MakeRequest(config, Key, Messages, "deployment", 100);

        Assert.Equal("/openai/v1/chat/completions", request.RequestUri!.AbsolutePath);
        Assert.Equal(Key, Header(request, "api-key"));
        Assert.Null(Header(request, "Authorization"));
        Assert.Equal(100, (await Json(request))["max_completion_tokens"]!.GetValue<int>());
        ExpectSecretsOnlyWhereAllowed(request);
    }

    [Fact]
    public async Task OtherHostsSendMaxTokensAndExtraHeadersCannotReplaceTheAuth()
    {
        var config = Preset("groq");
        config.ExtraHeaders = new() { ["authorization"] = "Bearer someone-else", ["HTTP-Referer"] = "https://echodiar.app" };
        using var request = OpenAICompatibleClient.MakeRequest(config, Key, Messages, "m", 100);

        Assert.Equal($"Bearer {Key}", Header(request, "Authorization"));
        Assert.Equal("https://echodiar.app", Header(request, "HTTP-Referer"));
        var body = await Json(request);
        Assert.Equal(100, body["max_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_completion_tokens"));
    }

    [Fact]
    public async Task AnthropicRequestCarriesKeyAndVersionHeaders()
    {
        var config = Preset("anthropic");
        config.ExtraHeaders = new() { ["anthropic-version"] = "2020-01-01", ["x-api-key"] = "someone-else" };
        using var request = AnthropicClient.MakeRequest(config, Key, Messages, "m", 100);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.AbsoluteUri);
        Assert.Equal(Key, Header(request, "x-api-key"));
        Assert.Equal("2023-06-01", Header(request, "anthropic-version"));
        var body = await Json(request);
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(100, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("Summarize the meeting.", body["system"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""[{"role":"user","content":"{{Transcript}}"}]"""), body["messages"]));
        ExpectSecretsOnlyWhereAllowed(request);
    }

    // Ollama's native chat API, so num_ctx can be raised: (UTF-8 bytes / 4 + maxTokens) in 4096 steps, 4096..32768.
    [Theory]
    [InlineData(0, 100, 4096)]
    [InlineData(15_984, 100, 4096)]
    [InlineData(16_000, 100, 8192)]
    [InlineData(1_000_000, 100, 32768)]
    public async Task OllamaChatUsesNativeApiWithContextInSteps(int promptBytes, int maxTokens, int numCtx)
    {
        LLMMessage[] messages = [new(LLMRole.User, new string('a', promptBytes))];
        using var request = OpenAICompatibleClient.MakeRequest(Preset("ollama"), null, messages, "qwen3", maxTokens);

        Assert.Equal("http://127.0.0.1:11434/api/chat", request.RequestUri!.AbsoluteUri);
        Assert.Null(Header(request, "Authorization"));
        var body = await Json(request);
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(numCtx, body["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal(maxTokens, body["options"]!["num_predict"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("openai", "https://api.openai.com/v1/models")]
    [InlineData("ollama", "http://127.0.0.1:11434/api/tags")]
    [InlineData("anthropic", "https://api.anthropic.com/v1/models?limit=1000")]
    public void ModelsRequestIsAShortGet(string preset, string url)
    {
        var config = Preset(preset);
        using var request = config.Kind == ProviderKind.Anthropic
            ? AnthropicClient.MakeModelsRequest(config, Key)
            : OpenAICompatibleClient.MakeModelsRequest(config, Key);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(url, request.RequestUri!.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(15), Timeout(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ftp://api.openai.com/v1")]
    [InlineData("file:///etc/passwd")]
    [InlineData("api.openai.com/v1")]
    public void InvalidBaseUrlThrows(string baseURL)
    {
        var config = Preset("openai");
        config.BaseURL = baseURL;

        var error = Assert.Throws<LLMException>(() => OpenAICompatibleClient.MakeRequest(config, Key, Messages, "m", 100));
        Assert.Equal(LLMErrorKind.InvalidBaseUrl, error.Kind);
    }

    // The message shows scheme, host and port only: user info, path and query may hold a key.
    [Theory]
    [InlineData("ftp://user:secret-pw@host.example:2121/v1/secret-path?token=abc#frag", "\"ftp://host.example:2121\" is not a valid http(s) base URL.")]
    [InlineData("secret-pw not a url", "The base URL is not a valid http(s) URL.")]
    public void InvalidBaseUrlMessageHidesUserInfoAndQuery(string baseURL, string message)
    {
        var config = Preset("openai");
        config.BaseURL = baseURL;

        var error = Assert.Throws<LLMException>(() => OpenAICompatibleClient.MakeRequest(config, Key, Messages, "m", 100));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task OpenAICompatibleEmbeddingsRequestPostsModelAndInputs()
    {
        using var request = OpenAICompatibleClient.MakeEmbeddingsRequest(Preset("openai"), Key, "e", [Transcript, "b"]);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com/v1/embeddings", request.RequestUri!.AbsoluteUri);
        Assert.Equal($"Bearer {Key}", Header(request, "Authorization"));
        var body = await Json(request);
        Assert.Equal("e", body["model"]!.GetValue<string>());
        Assert.Equal([Transcript, "b"], body["input"]!.AsArray().Select(n => n!.GetValue<string>()));
        ExpectSecretsOnlyWhereAllowed(request);
    }

    [Fact]
    public async Task OllamaEmbeddingsRequestUsesNativeEmbedAPI()
    {
        using var request = OpenAICompatibleClient.MakeEmbeddingsRequest(Preset("ollama"), null, "nomic-embed-text", [Transcript]);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:11434/api/embed", request.RequestUri!.AbsoluteUri);
        Assert.Null(Header(request, "Authorization"));
        var body = await Json(request);
        Assert.Equal("nomic-embed-text", body["model"]!.GetValue<string>());
        Assert.Equal([Transcript], body["input"]!.AsArray().Select(n => n!.GetValue<string>()));
        ExpectSecretsOnlyWhereAllowed(request);
    }

    [Fact]
    public void EmbeddingsDecodeInInputOrderAndNormalised()
    {
        const string payload = """{"data": [{"index": 1, "embedding": [0, 2]}, {"index": 0, "embedding": [3, 4]}]}""";
        var response = JsonSerializer.Deserialize<EmbeddingsResponse>(payload, ProviderHttp.Json)!;

        var vectors = response.Vectors(2);
        Assert.Equal([0.6f, 0.8f], vectors[0]);
        Assert.Equal([0f, 1f], vectors[1]);
        Assert.Throws<LLMException>(() => response.Vectors(3));
    }

    private static ProviderConfig Preset(string id) => ProviderConfig.Presets().Single(p => p.Id == id);

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static TimeSpan? Timeout(HttpRequestMessage request) =>
        request.Options.TryGetValue(ProviderHttp.TimeoutOption, out var timeout) ? timeout : null;

    private static async Task<JsonObject> Json(HttpRequestMessage request) =>
        JsonNode.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();

    /// <summary>The key never in the URL; the transcript never in the URL or any header.</summary>
    private static void ExpectSecretsOnlyWhereAllowed(HttpRequestMessage request)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Assert.DoesNotContain(Key, url);
        Assert.DoesNotContain("launch", url);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>());
        Assert.DoesNotContain(headers.SelectMany(h => h.Value), value => value.Contains("launch"));
    }
}
