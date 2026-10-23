using System.Text;
using EchoDiar.Core.Settings;

namespace EchoDiar.AI;

/// <summary>
/// OpenAI Chat Completions client for every OpenAI-compatible cloud API and local host. The <c>ollama</c> preset uses
/// Ollama's native <c>/api/chat</c> instead, because only that API can raise its context size (4K by default on
/// smaller machines, which silently truncates a transcript).
/// </summary>
public sealed class OpenAICompatibleClient : ILLMProvider
{
    /// <summary>Ollama's <c>num_ctx</c> never goes above this; also what <see cref="ContextWindowAsync"/> reports for it.</summary>
    internal const int OllamaMaxContext = 32_768;
    /// <summary>Context fallback for other local hosts, whose loaded context size is unknown.</summary>
    internal const int LocalContext = 8_192;
    /// <summary>Every cloud model in docs/research/llm-providers.md D has at least this much.</summary>
    internal const int CloudContext = 128_000;
    /// <summary>Texts per embeddings request.</summary>
    internal const int EmbeddingsBatch = 64;

    private readonly string? _apiKey;
    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/>: null for the shared transport; a test seam otherwise (still behind the redirect guard).</summary>
    public OpenAICompatibleClient(ProviderConfig config, string? apiKey, HttpMessageHandler? handler = null)
    {
        Config = config;
        _apiKey = apiKey;
        _http = ProviderHttp.Client(handler);
    }

    public ProviderConfig Config { get; }

    public IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LLMMessage> messages, string model, int maxTokens, CancellationToken cancellationToken = default) =>
        ProviderHttp.StreamAsync(
            _http, () => MakeRequest(Config, _apiKey, messages, model, maxTokens),
            IsOllama(Config) ? OllamaLine : ChatCompletionsLine, cancellationToken);

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = MakeModelsRequest(Config, _apiKey);
        if (IsOllama(Config))
            return (await ProviderHttp.GetJsonAsync<OllamaTags>(_http, request, cancellationToken)).Models.Select(m => m.Name).ToList();
        return (await ProviderHttp.GetJsonAsync<ModelList>(_http, request, cancellationToken)).Data.Select(m => m.Id).ToList();
    }

    public ValueTask<int> ContextWindowAsync(string model, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(IsOllama(Config) ? OllamaMaxContext : Config.Kind == ProviderKind.Local ? LocalContext : CloudContext);

    /// <summary>One L2-normalised vector per text, in the order of <paramref name="texts"/>, <see cref="EmbeddingsBatch"/> texts per request.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, string model, CancellationToken cancellationToken = default)
    {
        var vectors = new List<float[]>(texts.Count);
        for (var start = 0; start < texts.Count; start += EmbeddingsBatch)
        {
            var batch = texts.Skip(start).Take(EmbeddingsBatch).ToList();
            using var request = MakeEmbeddingsRequest(Config, _apiKey, model, batch);
            var response = await ProviderHttp.GetJsonAsync<EmbeddingsResponse>(_http, request, cancellationToken);
            vectors.AddRange(response.Vectors(batch.Count));
        }
        return vectors;
    }

    /// <summary><c>POST {base}/chat/completions</c> (or Ollama <c>POST {base}/api/chat</c>), streaming. Pure, for tests.</summary>
    public static HttpRequestMessage MakeRequest(
        ProviderConfig config, string? apiKey, IReadOnlyList<LLMMessage> messages, string model, int maxTokens)
    {
        var baseUrl = ProviderHttp.BaseUrl(config);
        if (IsOllama(config))
        {
            var ollama = ProviderHttp.Request(ProviderHttp.Append(baseUrl, "api/chat"), config, apiKey, ProviderHttp.StreamTimeout);
            var options = new OllamaOptions(OllamaContextSize(messages, maxTokens), maxTokens);
            ProviderHttp.SetJsonBody(ollama, new OllamaChatBody(model, messages, true, options));
            return ollama;
        }
        var request = ProviderHttp.Request(
            ProviderHttp.Append(baseUrl, "chat/completions"), config, apiKey, ProviderHttp.StreamTimeout);
        // OpenAI's newer models reject max_tokens; most other hosts only know max_tokens.
        var completionLimit = config.Preset is "openai" or "azure";
        ProviderHttp.SetJsonBody(request, new ChatBody(
            model, messages, true, completionLimit ? null : maxTokens, completionLimit ? maxTokens : null));
        return request;
    }

    /// <summary><c>GET {base}/models</c> (Ollama: <c>GET {base}/api/tags</c>).</summary>
    public static HttpRequestMessage MakeModelsRequest(ProviderConfig config, string? apiKey)
    {
        var path = IsOllama(config) ? "api/tags" : "models";
        return ProviderHttp.Request(ProviderHttp.Append(ProviderHttp.BaseUrl(config), path), config, apiKey, ProviderHttp.ListTimeout);
    }

    /// <summary><c>POST {base}/embeddings</c> (Ollama: <c>POST {base}/api/embed</c>), both with <c>{"model", "input": [...]}</c>. Pure, for tests.</summary>
    public static HttpRequestMessage MakeEmbeddingsRequest(
        ProviderConfig config, string? apiKey, string model, IReadOnlyList<string> inputs)
    {
        var path = IsOllama(config) ? "api/embed" : "embeddings";
        // A local host may load the model before it answers, so the long timeout.
        var request = ProviderHttp.Request(
            ProviderHttp.Append(ProviderHttp.BaseUrl(config), path), config, apiKey, ProviderHttp.StreamTimeout);
        ProviderHttp.SetJsonBody(request, new EmbeddingsBody(model, inputs));
        return request;
    }

    /// <summary>
    /// Prompt estimate (UTF-8 bytes / 4, errs high for accented text) + the answer, rounded up to 4K steps (Ollama
    /// reloads the model when <c>num_ctx</c> changes), between 4K and <see cref="OllamaMaxContext"/>.
    /// </summary>
    internal static int OllamaContextSize(IReadOnlyList<LLMMessage> messages, int maxTokens)
    {
        var needed = messages.Sum(m => Encoding.UTF8.GetByteCount(m.Content)) / 4 + maxTokens;
        const int step = 4_096;
        return Math.Min(OllamaMaxContext, Math.Max(step, (needed + step - 1) / step * step));
    }

    private static bool IsOllama(ProviderConfig config) => config.Preset == "ollama";

    /// <summary>SSE <c>data:</c> chunks, <c>choices[0].delta.content</c>, until <c>[DONE]</c>.</summary>
    private static StreamLine ChatCompletionsLine(string line)
    {
        if (ProviderHttp.SseData(line) is not { } data) return StreamLine.Skip;
        if (data == "[DONE]") return StreamLine.Done;
        var chunk = ProviderHttp.Decode<ChatChunk>(data);
        if (chunk.Error is { } error) throw LLMException.Provider(error.Message ?? "The provider returned an error.");
        var text = chunk.Choices?.FirstOrDefault()?.Delta?.Content;
        return string.IsNullOrEmpty(text) ? StreamLine.Skip : StreamLine.Of(text);
    }

    /// <summary>JSONL objects, <c>message.content</c>, until <c>done: true</c>.</summary>
    private static StreamLine OllamaLine(string line)
    {
        if (line.Length == 0) return StreamLine.Skip;
        var chunk = ProviderHttp.Decode<OllamaChunk>(line);
        if (chunk.Error is { } error) throw LLMException.Provider(error);
        if (chunk.Message?.Content is { Length: > 0 } text) return StreamLine.Of(text);
        return chunk.Done == true ? StreamLine.Done : StreamLine.Skip;
    }

    private sealed record ChatBody(
        string Model, IReadOnlyList<LLMMessage> Messages, bool Stream, int? MaxTokens, int? MaxCompletionTokens);

    private sealed record ChatChunk(List<ChatChunk.Choice>? Choices = null, APIErrorBody? Error = null)
    {
        internal sealed record Choice(ChoiceDelta? Delta = null);
        internal sealed record ChoiceDelta(string? Content = null);
    }

    private sealed record OllamaOptions(int NumCtx, int NumPredict);

    private sealed record OllamaChatBody(string Model, IReadOnlyList<LLMMessage> Messages, bool Stream, OllamaOptions Options);

    private sealed record OllamaChunk(OllamaChunk.ChunkMessage? Message = null, bool? Done = null, string? Error = null)
    {
        internal sealed record ChunkMessage(string? Content = null);
    }

    /// <summary><c>{"models": [{"name": ...}]}</c> from Ollama's <c>/api/tags</c>.</summary>
    private sealed record OllamaTags(List<OllamaTags.Model> Models)
    {
        internal sealed record Model(string Name);
    }

    private sealed record EmbeddingsBody(string Model, IReadOnlyList<string> Input);
}

/// <summary>
/// <c>{"data": [{"index", "embedding"}]}</c> (OpenAI-style, any order) or <c>{"embeddings": [[...]]}</c> (Ollama <c>/api/embed</c>).
/// </summary>
internal sealed record EmbeddingsResponse(List<EmbeddingsResponse.Item>? Data = null, List<float[]>? Embeddings = null)
{
    internal sealed record Item(int Index, float[] Embedding);

    /// <summary>The vectors in input order, L2-normalised; throws unless there is exactly one non-empty vector per input.</summary>
    public List<float[]> Vectors(int count)
    {
        List<float[]?> vectors;
        if (Data is { } data)
        {
            if (data.Contains(null!)) throw LLMException.Provider("The provider returned embeddings with missing or repeated indices.");
            var items = data.OrderBy(item => item.Index).ToList();
            if (!items.Select(item => item.Index).SequenceEqual(Enumerable.Range(0, items.Count)))
                throw LLMException.Provider("The provider returned embeddings with missing or repeated indices.");
            vectors = items.Select(item => (float[]?)item.Embedding).ToList();
        }
        else
        {
            vectors = Embeddings?.Select(v => (float[]?)v).ToList() ?? [];
        }
        if (vectors.Count != count)
            throw LLMException.Provider($"The provider returned {vectors.Count} embeddings for {count} texts.");
        if (vectors.Any(v => v is not { Length: > 0 })) throw LLMException.Provider("The provider returned an empty embedding.");
        return vectors.Select(v => EmbeddingMath.Normalized(v!)).ToList();
    }
}
