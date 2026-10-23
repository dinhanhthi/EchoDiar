using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using EchoDiar.Core.Settings;

namespace EchoDiar.AI;

/// <summary>Swift <c>LLMMessage.Role</c>; sent as <c>"system"</c>, <c>"user"</c>, <c>"assistant"</c>.</summary>
public enum LLMRole { System, User, Assistant }

/// <summary>One chat message. Callers put transcript text only in user messages; system messages carry instructions.</summary>
public sealed record LLMMessage(LLMRole Role, string Content);

/// <summary>A chat model backend (Swift <c>LLMProvider</c>).</summary>
public interface ILLMProvider
{
    /// <summary>Text deltas of the answer. Cancelling the token (or disposing the enumerator) cancels the request.</summary>
    IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LLMMessage> messages, string model, int maxTokens, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Input + output tokens <paramref name="model"/> can take; a conservative fallback when the host cannot tell.</summary>
    ValueTask<int> ContextWindowAsync(string model, CancellationToken cancellationToken = default);
}

/// <summary>
/// An in-app model. <c>AIService</c> loads it once per job inside <c>ModelLoadGate</c> (owner LLM) and calls the
/// generator directly, so the model is unloaded before the gate is released.
/// </summary>
public interface ILocalLLMProvider : ILLMProvider
{
    /// <summary>
    /// Loads the model, runs <paramref name="body"/> with it, then unloads it and restores any process-wide state.
    /// Returns (or throws) only once the model is unloaded, also when <paramref name="body"/> throws or is cancelled.
    /// </summary>
    Task<T> WithLoadedModelAsync<T>(
        string model, Func<ILocalLLMGenerator, Task<T>> body, CancellationToken cancellationToken = default);

    /// <summary>One answer with the model loaded for it alone (no <c>ModelLoadGate</c>); <c>AIService</c> uses <see cref="WithLoadedModelAsync{T}"/>.</summary>
    IAsyncEnumerable<string> ILLMProvider.StreamAsync(
        IReadOnlyList<LLMMessage> messages, string model, int maxTokens, CancellationToken cancellationToken) =>
        StreamLoaded(this, messages, model, maxTokens, cancellationToken);

    private static async IAsyncEnumerable<string> StreamLoaded(
        ILocalLLMProvider provider, IReadOnlyList<LLMMessage> messages, string model, int maxTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var job = Task.Run(async () =>
        {
            try
            {
                await provider.WithLoadedModelAsync(model, generator =>
                    generator.GenerateAsync(messages, maxTokens, text => channel.Writer.TryWrite(text), stop.Token), stop.Token);
                channel.Writer.TryComplete();
            }
            catch (Exception error)
            {
                channel.Writer.TryComplete(error);
            }
        }, CancellationToken.None);
        try
        {
            await foreach (var text in channel.Reader.ReadAllAsync(cancellationToken)) yield return text;
        }
        finally
        {
            // The job never throws; waiting for it means the model is unloaded once the enumerator is disposed.
            stop.Cancel();
            await job;
        }
    }
}

/// <summary>A loaded in-app model, valid inside <see cref="ILocalLLMProvider.WithLoadedModelAsync{T}"/>.</summary>
public interface ILocalLLMGenerator
{
    /// <summary>
    /// Passes the pieces of the answer to <paramref name="onText"/> and returns the whole answer once generation has
    /// stopped (early, without throwing, when cancelled).
    /// </summary>
    Task<string> GenerateAsync(
        IReadOnlyList<LLMMessage> messages, int maxTokens, Action<string> onText, CancellationToken cancellationToken = default);
}

public enum LLMErrorKind { InvalidBaseUrl, HttpStatus, Provider }

/// <summary>Swift <c>LLMError</c>; <see cref="Exception.Message"/> is its <c>errorDescription</c>.</summary>
public sealed class LLMException : Exception
{
    private LLMException(LLMErrorKind kind, string message, int statusCode = 0, string body = "") : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
        Body = body;
    }

    public LLMErrorKind Kind { get; }
    /// <summary>The HTTP status for <see cref="LLMErrorKind.HttpStatus"/>; 0 otherwise.</summary>
    public int StatusCode { get; }
    /// <summary>The start of the error body (at most 2 KB, keys redacted) for <see cref="LLMErrorKind.HttpStatus"/>.</summary>
    public string Body { get; }

    public static LLMException InvalidBaseUrl(string url) =>
        new(LLMErrorKind.InvalidBaseUrl, $"\"{url}\" is not a valid http(s) base URL.");

    public static LLMException HttpStatus(int statusCode, string body) =>
        new(LLMErrorKind.HttpStatus, body.Length == 0 ? $"HTTP {statusCode}." : $"HTTP {statusCode}: {body}", statusCode, body);

    public static LLMException Provider(string message) => new(LLMErrorKind.Provider, message);
}

public static class EmbeddingMath
{
    /// <summary><paramref name="v"/> scaled to unit length; a zero vector is returned unchanged.</summary>
    public static float[] Normalized(float[] v)
    {
        var sum = 0f;
        foreach (var x in v) sum += x * x;
        var norm = MathF.Sqrt(sum);
        if (!(norm > 0)) return v;
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++) result[i] = v[i] / norm;
        return result;
    }
}

internal enum StreamLineKind { Text, Skip, Done }

/// <summary>What one line of a streamed body (SSE or JSONL) means.</summary>
internal readonly record struct StreamLine(StreamLineKind Kind, string Text = "")
{
    public static StreamLine Skip => new(StreamLineKind.Skip);
    public static StreamLine Done => new(StreamLineKind.Done);
    public static StreamLine Of(string text) => new(StreamLineKind.Text, text);
}

/// <summary>HTTP plumbing shared by the API clients (Swift <c>ProviderHTTP</c>).</summary>
internal static class ProviderHttp
{
    /// <summary>Streaming answers can start late (a local host evaluating a long prompt), so the idle timeout is long.</summary>
    public static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(600);
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);
    /// <summary>The longest error body kept, in bytes.</summary>
    public const int ErrorBodyLimit = 2048;

    /// <summary>Where <see cref="Request"/> stores the request's timeout.</summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> TimeoutOption = new("EchoDiar.ProviderHttp.Timeout");

    /// <summary>Request bodies: snake_case keys (<c>max_tokens</c>, <c>num_ctx</c>), nulls omitted. Also the response decoder.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>The client every API client uses by default: no cookies, no automatic redirects (see <see cref="RedirectGuard"/>).</summary>
    private static readonly Lazy<HttpClient> Shared = new(() =>
        Wrap(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }));

    /// <summary>The shared client, or one over <paramref name="transport"/> (a test seam), both behind <see cref="RedirectGuard"/>.</summary>
    public static HttpClient Client(HttpMessageHandler? transport) => transport is null ? Shared.Value : Wrap(transport);

    private static HttpClient Wrap(HttpMessageHandler transport) =>
        new(new RedirectGuard(transport)) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Whether a redirect from <paramref name="original"/> may be followed: only to the same scheme, host and port.
    /// Extra headers and <c>x-api-key</c>/<c>api-key</c> would otherwise reach another host, and an https -> http
    /// redirect would send them in cleartext.
    /// </summary>
    public static bool AllowsRedirect(Uri original, Uri target)
    {
        if (!original.IsAbsoluteUri || !target.IsAbsoluteUri || original.Host.Length == 0) return false;
        return string.Equals(original.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(original.Host, target.Host, StringComparison.OrdinalIgnoreCase)
               && original.Port == target.Port;
    }

    public static Uri BaseUrl(ProviderConfig config)
    {
        var text = config.BaseURL.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.Host.Length == 0)
            throw LLMException.InvalidBaseUrl(text);
        return url;
    }

    /// <summary>Swift <c>URL.appending(path:)</c>: <paramref name="path"/> after the base path, with one <c>/</c> between; the query kept.</summary>
    public static Uri Append(Uri baseUrl, string path) =>
        new(baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + path + baseUrl.Query);

    /// <summary>A GET with the config's extra headers, then its auth header (so extras cannot replace the auth).</summary>
    public static HttpRequestMessage Request(Uri url, ProviderConfig config, string? apiKey, TimeSpan timeout)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(TimeoutOption, timeout);
        foreach (var (name, value) in config.ExtraHeaders) SetHeader(request, name, value);
        if (!string.IsNullOrEmpty(apiKey))
        {
            switch (config.AuthStyle)
            {
                case AuthStyle.Bearer: SetHeader(request, "Authorization", "Bearer " + apiKey); break;
                case AuthStyle.ApiKeyHeader: SetHeader(request, "api-key", apiKey); break;
                case AuthStyle.XApiKey: SetHeader(request, "x-api-key", apiKey); break;
                case AuthStyle.None: break;
            }
        }
        return request;
    }

    /// <summary>
    /// Swift <c>setValue(_:forHTTPHeaderField:)</c>: replaces any value of <paramref name="name"/>. A name that is not
    /// a valid request header (a content header such as Content-Type, which the body sets) is dropped.
    /// </summary>
    public static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        if (request.Headers.TryGetValues(name, out _)) request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }

    public static void SetJsonBody<T>(HttpRequestMessage request, T body)
    {
        request.Method = HttpMethod.Post;
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json));
        request.Content.Headers.ContentType = new("application/json");
    }

    public static T Decode<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Json) ?? throw new JsonException("Expected a JSON object.");

    /// <summary>The payload of an SSE <c>data:</c> line; null for <c>event:</c>, <c>:</c> comment and blank lines.</summary>
    public static string? SseData(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var payload = line[5..];
        return payload.StartsWith(' ') ? payload[1..] : payload;
    }

    /// <summary>
    /// <paramref name="body"/> with every key the request carries (auth header values, also without their
    /// <c>Bearer</c>/<c>Token</c> scheme) replaced, so an echoed key never reaches an error, the UI or a log.
    /// </summary>
    public static string Redacted(string body, HttpRequestMessage request)
    {
        string[] headers = ["Authorization", "x-goog-api-key", "xi-api-key", "api-key", "x-api-key"];
        var secrets = headers
            .SelectMany(name => request.Headers.TryGetValues(name, out var values) ? values : [])
            .SelectMany(value =>
            {
                var space = value.IndexOf(' ');
                return new[] { value, space < 0 ? value : value[(space + 1)..] };
            })
            .Where(secret => secret.Length >= 4)
            .OrderByDescending(secret => secret.Length);
        foreach (var secret in secrets) body = body.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return body;
    }

    /// <summary>
    /// Streams the body of the request <paramref name="makeRequest"/> builds (on the first move, so a bad config throws
    /// there) line by line through <paramref name="parse"/>, split on LF only. Disposing the enumerator or cancelling
    /// <paramref name="cancellationToken"/> aborts the request; no data for the request's timeout throws
    /// <see cref="TimeoutException"/>. A non-2xx status throws <see cref="LLMException.HttpStatus"/> with the start of
    /// the body, keys redacted.
    /// </summary>
    public static async IAsyncEnumerable<string> StreamAsync(
        HttpClient client, Func<HttpRequestMessage> makeRequest, Func<string, StreamLine> parse,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = makeRequest();
        var idle = TimeoutOf(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(idle);
        using var response = await Timed(
            () => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token), cancellationToken);
        await using var body = await Timed(() => response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var prefix = await Timed(() => ReadPrefixAsync(body, ErrorBodyLimit, timeout.Token), cancellationToken);
            throw LLMException.HttpStatus((int)response.StatusCode, Redacted(prefix, request));
        }

        // Split on LF only: some servers leave U+2028/U+2029 (and lone CRs) unescaped inside JSON strings.
        var buffer = new byte[16 * 1024];
        using var line = new MemoryStream();
        while (true)
        {
            timeout.CancelAfter(idle);
            var count = await Timed(() => body.ReadAsync(buffer, timeout.Token).AsTask(), cancellationToken);
            if (count == 0) break;
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                line.Write(buffer, start, i - start);
                start = i + 1;
                var parsed = parse(TakeLine(line));
                if (parsed.Kind == StreamLineKind.Done) yield break;
                if (parsed.Kind == StreamLineKind.Text) yield return parsed.Text;
            }
            line.Write(buffer, start, count - start);
        }
        if (line.Length > 0 && parse(TakeLine(line)) is { Kind: StreamLineKind.Text } last) yield return last.Text;
    }

    /// <summary>Sends the request and decodes a 2xx JSON body; any other status throws as in <see cref="StreamAsync"/>.</summary>
    public static async Task<T> GetJsonAsync<T>(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutOf(request));
        var (status, data) = await Timed(async () =>
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(timeout.Token));
        }, cancellationToken);
        if ((int)status is < 200 or > 299)
            throw LLMException.HttpStatus((int)status, Redacted(Utf8(data, Math.Min(data.Length, ErrorBodyLimit)), request));
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new JsonException("Expected a JSON object.");
    }

    private static TimeSpan TimeoutOf(HttpRequestMessage request) =>
        request.Options.TryGetValue(TimeoutOption, out var timeout) ? timeout : StreamTimeout;

    /// <summary>Runs <paramref name="operation"/>; a cancellation that the caller did not ask for is the timeout.</summary>
    private static async Task<T> Timed<T>(Func<Task<T>> operation, CancellationToken caller)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw new TimeoutException("The request timed out.");
        }
    }

    private static async Task<string> ReadPrefixAsync(Stream body, int limit, CancellationToken cancellationToken)
    {
        var bytes = new byte[limit];
        var length = 0;
        while (length < limit)
        {
            var count = await body.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (count == 0) break;
            length += count;
        }
        return Utf8(bytes, length);
    }

    /// <summary>The line in <paramref name="line"/> without a trailing CR, as UTF-8 (invalid bytes become U+FFFD); empties it.</summary>
    private static string TakeLine(MemoryStream line)
    {
        var bytes = line.GetBuffer();
        var length = (int)line.Length;
        if (length > 0 && bytes[length - 1] == (byte)'\r') length--;
        var text = Utf8(bytes, length);
        line.SetLength(0);
        return text;
    }

    private static string Utf8(byte[] bytes, int length) => Encoding.UTF8.GetString(bytes, 0, length);
}

/// <summary>
/// Follows a redirect only within the original request's origin (<see cref="ProviderHttp.AllowsRedirect"/>); any other
/// redirect is returned as is, which the callers report as an HTTP error.
/// </summary>
internal sealed class RedirectGuard(HttpMessageHandler transport) : DelegatingHandler(transport)
{
    private const int MaxRedirects = 10;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var original = request.RequestUri!;
        var current = request;
        for (var hop = 0; ; hop++)
        {
            var response = await base.SendAsync(current, cancellationToken);
            if (hop == MaxRedirects || !IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
                return response;
            var target = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (!ProviderHttp.AllowsRedirect(original, target)) return response;
            current = Follow(current, target, response.StatusCode);
            response.Dispose();
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>The same request to <paramref name="target"/>; a 303, or a 301/302 after a POST, becomes a GET without a body.</summary>
    private static HttpRequestMessage Follow(HttpRequestMessage request, Uri target, HttpStatusCode status)
    {
        var toGet = status == HttpStatusCode.SeeOther && request.Method != HttpMethod.Head
                    || status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && request.Method == HttpMethod.Post;
        var next = new HttpRequestMessage(toGet ? HttpMethod.Get : request.Method, target)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = toGet ? null : request.Content,
        };
        foreach (var header in request.Headers) next.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return next;
    }
}

/// <summary><c>{"error": {"message": ...}}</c>, used by both OpenAI-style and Anthropic error payloads.</summary>
internal sealed record APIErrorBody(string? Message = null);

/// <summary><c>{"data": [{"id": ...}]}</c> from a models endpoint (OpenAI-style and Anthropic).</summary>
internal sealed record ModelList(List<ModelList.Model> Data)
{
    internal sealed record Model(string Id);
}
