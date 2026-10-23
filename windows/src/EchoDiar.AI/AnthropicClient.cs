using EchoDiar.Core.Settings;

namespace EchoDiar.AI;

/// <summary>Native Anthropic Messages API client (its OpenAI compatibility layer is for testing only).</summary>
public sealed class AnthropicClient : ILLMProvider
{
    public const string ApiVersion = "2023-06-01";
    /// <summary>Every current Claude model has at least this much.</summary>
    internal const int FallbackContext = 200_000;

    private readonly string? _apiKey;
    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/>: null for the shared transport; a test seam otherwise (still behind the redirect guard).</summary>
    public AnthropicClient(ProviderConfig config, string? apiKey, HttpMessageHandler? handler = null)
    {
        Config = config;
        _apiKey = apiKey;
        _http = ProviderHttp.Client(handler);
    }

    public ProviderConfig Config { get; }

    public IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LLMMessage> messages, string model, int maxTokens, CancellationToken cancellationToken = default) =>
        ProviderHttp.StreamAsync(
            _http, () => MakeRequest(Config, _apiKey, messages, model, maxTokens), EventLine, cancellationToken);

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = MakeModelsRequest(Config, _apiKey);
        return (await ProviderHttp.GetJsonAsync<ModelList>(_http, request, cancellationToken)).Data.Select(m => m.Id).ToList();
    }

    public ValueTask<int> ContextWindowAsync(string model, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(FallbackContext);

    /// <summary>
    /// <c>POST {base}/v1/messages</c>, streaming; system messages go to the top-level <c>system</c> field. No
    /// <c>temperature</c> or <c>top_p</c> (recent models reject non-default values). Pure, for tests.
    /// </summary>
    public static HttpRequestMessage MakeRequest(
        ProviderConfig config, string? apiKey, IReadOnlyList<LLMMessage> messages, string model, int maxTokens)
    {
        var request = BaseRequest("v1/messages", config, apiKey, ProviderHttp.StreamTimeout);
        var system = string.Join("\n\n", messages.Where(m => m.Role == LLMRole.System).Select(m => m.Content));
        ProviderHttp.SetJsonBody(request, new MessagesBody(
            model, maxTokens, system.Length == 0 ? null : system, messages.Where(m => m.Role != LLMRole.System).ToList(), true));
        return request;
    }

    /// <summary><c>GET {base}/v1/models?limit=1000</c> (one page holds every model).</summary>
    public static HttpRequestMessage MakeModelsRequest(ProviderConfig config, string? apiKey)
    {
        var request = BaseRequest("v1/models", config, apiKey, ProviderHttp.ListTimeout);
        var url = request.RequestUri!;
        request.RequestUri = new Uri(url.AbsoluteUri + (url.Query.Length == 0 ? "?" : "&") + "limit=1000");
        return request;
    }

    private static HttpRequestMessage BaseRequest(string path, ProviderConfig config, string? apiKey, TimeSpan timeout)
    {
        var request = ProviderHttp.Request(ProviderHttp.Append(ProviderHttp.BaseUrl(config), path), config, apiKey, timeout);
        ProviderHttp.SetHeader(request, "anthropic-version", ApiVersion);
        return request;
    }

    /// <summary>
    /// Named SSE events; each <c>data:</c> JSON repeats the event name in <c>type</c>. Text comes from
    /// <c>content_block_delta</c> events with a <c>text_delta</c>; <c>message_stop</c> ends the answer.
    /// </summary>
    private static StreamLine EventLine(string line)
    {
        if (ProviderHttp.SseData(line) is not { } data) return StreamLine.Skip;
        var e = ProviderHttp.Decode<Event>(data);
        switch (e.Type)
        {
            case "content_block_delta" when e.Delta?.Type == "text_delta":
                return e.Delta.Text is { Length: > 0 } text ? StreamLine.Of(text) : StreamLine.Skip;
            case "message_stop":
                return StreamLine.Done;
            case "error":
                throw LLMException.Provider(e.Error?.Message ?? "The provider returned an error.");
            default:
                return StreamLine.Skip;
        }
    }

    private sealed record MessagesBody(
        string Model, int MaxTokens, string? System, IReadOnlyList<LLMMessage> Messages, bool Stream);

    private sealed record Event(string Type, Event.EventDelta? Delta = null, APIErrorBody? Error = null)
    {
        internal sealed record EventDelta(string? Type = null, string? Text = null);
    }
}
