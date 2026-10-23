using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
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
    /// <summary>
    /// The start of the error body (keys redacted, then cut to at most 2048 characters) for <see cref="LLMErrorKind.HttpStatus"/>.
    /// </summary>
    public string Body { get; }

    /// <summary>
    /// Shows only the scheme, host and port of <paramref name="url"/>: its user info, path or query may hold a key. The
    /// query and fragment are cut before parsing, as some schemes (ftp) would keep a <c>?</c> in the path.
    /// </summary>
    public static LLMException InvalidBaseUrl(string url) =>
        new(LLMErrorKind.InvalidBaseUrl,
            Uri.TryCreate(url.Split('?', '#')[0], UriKind.Absolute, out var parsed) && parsed.Host.Length > 0
                ? $"\"{parsed.Scheme}://{parsed.Host}{(parsed.IsDefaultPort ? "" : ":" + parsed.Port)}\" is not a valid http(s) base URL."
                : "The base URL is not a valid http(s) URL.");

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
    /// <summary>The longest error text kept (an error body or an in-stream error message), in characters.</summary>
    public const int ErrorBodyLimit = 2048;
    /// <summary>
    /// The longest streamed line, in bytes. A legit line is one small JSON chunk (a text delta is well under 1 KB; even
    /// a token array of a 32K context is ~200 KB), so this never cuts one, while a host that never sends LF cannot grow
    /// memory without limit.
    /// </summary>
    public const int LineLimit = 4 * 1024 * 1024;
    /// <summary>
    /// The largest JSON response, in bytes. The biggest legit one is an embeddings batch: 64 texts x 4096 dims x ~20
    /// characters per number is ~5 MB (~10 MB at 8192 dims), so 32 MB leaves a 3x margin; model lists are far smaller.
    /// </summary>
    public const int JsonLimit = 32 * 1024 * 1024;
    /// <summary>The largest small JSON response (a model list, a key check, an upload answer), in bytes.</summary>
    public const int SmallJsonLimit = 4 * 1024 * 1024;
    /// <summary>Total time one request may take however steadily it moves, so a host that trickles bytes cannot hold it.</summary>
    public static readonly TimeSpan RequestCeiling = TimeSpan.FromHours(2);
    /// <summary>The pause before the one retry of <see cref="SendAsync{T}"/>.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Where <see cref="Request"/> stores the request's timeout.</summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> TimeoutOption = new("EchoDiar.ProviderHttp.Timeout");
    /// <summary>Where <see cref="Request"/> stores the extra-header values <see cref="Redacted"/> also hides.</summary>
    public static readonly HttpRequestOptionsKey<string[]> ExtraSecretsOption = new("EchoDiar.ProviderHttp.ExtraSecrets");

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
        Wrap(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }));

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
        // Custom headers and the base URL's query may carry a key too (a gateway token); short values (a version, a
        // flag) are not secrets.
        request.Options.Set(ExtraSecretsOption,
            config.ExtraHeaders.Values.Concat(QueryValues(config.BaseURL)).Where(value => value.Length >= 8).ToArray());
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

    /// <summary>The percent-decoded values of <paramref name="url"/>'s query (<c>?key=value&amp;...</c>), however malformed the URL.</summary>
    internal static IEnumerable<string> QueryValues(string url)
    {
        var query = url.Split('#')[0];
        var mark = query.IndexOf('?');
        if (mark < 0) return [];
        return query[(mark + 1)..].Split('&').Select(pair => Uri.UnescapeDataString(pair[(pair.IndexOf('=') + 1)..]));
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

    public static T Decode<T>(ReadOnlySpan<byte> json, JsonSerializerOptions? options = null) where T : class =>
        JsonSerializer.Deserialize<T>(json, options ?? Json) ?? throw new JsonException("Expected a JSON object.");

    /// <summary>The payload of an SSE <c>data:</c> line; null for <c>event:</c>, <c>:</c> comment and blank lines.</summary>
    public static string? SseData(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var payload = line[5..];
        return payload.StartsWith(' ') ? payload[1..] : payload;
    }

    /// <summary>
    /// <paramref name="body"/> with every key the request carries (auth header values, also without their
    /// <c>Bearer</c>/<c>Token</c> scheme, extra-header values and base-URL query values) replaced, so an echoed key never
    /// reaches an error, the UI or a log. A key is also found escaped, even partly: any of its characters may be
    /// JSON-escaped (<c>\"</c>, <c>\\</c>, <c>\/</c>, <c>\u00XX</c>) or percent-encoded (<c>%XX</c>), in either hex case.
    /// The rest of the body is kept as is.
    /// </summary>
    public static string Redacted(string body, HttpRequestMessage request)
    {
        var secrets = Secrets(request);
        StringBuilder? result = null;
        var copied = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var end = -1;
            foreach (var secret in secrets)
                if ((end = Match(body, i, secret, 0).End) > i) break;
            if (end <= i) continue;
            (result ??= new()).Append(body, copied, i - copied).Append("[redacted]");
            copied = end;
            i = end - 1;
        }
        return result is null ? body : result.Append(body, copied, body.Length - copied).ToString();
    }

    /// <summary>
    /// <paramref name="text"/> redacted, then cut to <see cref="ErrorBodyLimit"/> characters. <paramref name="truncated"/>:
    /// the text is the start of a longer body, so a key cut off at its end is dropped too.
    /// </summary>
    public static string RedactedPrefix(string text, HttpRequestMessage request, bool truncated = false)
    {
        text = Redacted(text, request);
        if (truncated)
        {
            // A key cut off by the read (in any form, after a body of any script) is the longest tail that starts one.
            var secrets = Secrets(request);
            for (var start = Math.Max(0, text.Length - MaxEscapedLength(secrets)); start < text.Length; start++)
            {
                if (!secrets.Any(secret => Match(text, start, secret, 0).Partial)) continue;
                text = text[..start];
                break;
            }
        }
        if (text.Length <= ErrorBodyLimit) return text;
        var cut = char.IsHighSurrogate(text[ErrorBodyLimit - 1]) ? ErrorBodyLimit - 1 : ErrorBodyLimit;
        return text[..cut];
    }

    /// <summary>Every key in <paramref name="request"/>, longest first.</summary>
    private static List<string> Secrets(HttpRequestMessage request)
    {
        string[] headers = ["Authorization", "x-goog-api-key", "xi-api-key", "api-key", "x-api-key"];
        var auth = headers
            .SelectMany(name => request.Headers.TryGetValues(name, out var values) ? values : [])
            .SelectMany(value =>
            {
                var space = value.IndexOf(' ');
                return new[] { value, space < 0 ? value : value[(space + 1)..] };
            })
            .Where(secret => secret.Length >= 4);
        var extra = request.Options.TryGetValue(ExtraSecretsOption, out var values) ? values : [];
        return auth.Concat(extra).Distinct().OrderByDescending(secret => secret.Length).ToList();
    }

    /// <summary>The most characters a key can take in a body: <c>%XX%XX%XX</c> for a 3-byte UTF-8 character.</summary>
    private static int MaxEscapedLength(List<string> secrets) => 9 * secrets.Select(secret => secret.Length).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Where a match of <paramref name="secret"/> from its character <paramref name="k"/> on, starting at
    /// <paramref name="text"/>[<paramref name="i"/>], ends (-1 when there is none). <c>Partial</c>: the text ends
    /// inside a match.
    /// </summary>
    private static (int End, bool Partial) Match(string text, int i, string secret, int k)
    {
        var partial = false;
        for (; k < secret.Length; k++)
        {
            if (i == text.Length) return (-1, true);
            var c = secret[k];
            var (length, cut) = EscapedLength(text, i, c);
            partial |= cut;
            if (length > 0)
            {
                if (text[i] != c)
                {
                    i += length;
                    continue;
                }
                // A literal \ or % that also starts an escape of itself: try the escape, then the literal.
                var escaped = Match(text, i + length, secret, k + 1);
                if (escaped.End >= 0) return escaped;
                partial |= escaped.Partial;
            }
            if (text[i] != c) return (-1, partial);
            i++;
        }
        return (i, false);
    }

    /// <summary>
    /// The length of an escape of <paramref name="c"/> at <paramref name="text"/>[<paramref name="i"/>] (0 when there is
    /// none); <c>Cut</c>: the text ends inside one.
    /// </summary>
    private static (int Length, bool Cut) EscapedLength(string text, int i, char c)
    {
        string[] forms = text[i] switch
        {
            '\\' when c is '"' or '\\' or '/' => ["\\" + c, $"\\u{(int)c:X4}"],
            '\\' => [$"\\u{(int)c:X4}"],
            '%' when !char.IsSurrogate(c) => [string.Concat(Encoding.UTF8.GetBytes(c.ToString()).Select(b => $"%{b:X2}"))],
            _ => [],
        };
        var cut = false;
        foreach (var form in forms)
        {
            var available = Math.Min(form.Length, text.Length - i);
            if (string.Compare(text, i, form, 0, available, StringComparison.OrdinalIgnoreCase) != 0) continue;
            if (available == form.Length) return (form.Length, false);
            cut = true;
        }
        return (0, cut);
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
            var (prefix, truncated) = await Timed(() => ReadErrorAsync(body, request, timeout.Token), cancellationToken);
            throw LLMException.HttpStatus((int)response.StatusCode, RedactedPrefix(prefix, request, truncated));
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
                Append(line, buffer, start, i - start);
                start = i + 1;
                var parsed = Parse(parse, TakeLine(line), request);
                if (parsed.Kind == StreamLineKind.Done) yield break;
                if (parsed.Kind == StreamLineKind.Text) yield return parsed.Text;
            }
            Append(line, buffer, start, count - start);
        }
        if (line.Length > 0 && Parse(parse, TakeLine(line), request) is { Kind: StreamLineKind.Text } last) yield return last.Text;
    }

    /// <summary>Adds to the current line; past <see cref="LineLimit"/> (overshoot at most one read) the stream is refused.</summary>
    private static void Append(MemoryStream line, byte[] buffer, int start, int count)
    {
        line.Write(buffer, start, count);
        if (line.Length > LineLimit) throw LLMException.Provider("The provider sent a line longer than 4 MB.");
    }

    /// <summary>
    /// <paramref name="parse"/>, with an error the provider reports in the stream (HTTP 200, then an error event)
    /// redacted and cut like an error body.
    /// </summary>
    private static StreamLine Parse(Func<string, StreamLine> parse, string line, HttpRequestMessage request)
    {
        try
        {
            return parse(line);
        }
        catch (LLMException error) when (error.Kind == LLMErrorKind.Provider)
        {
            throw LLMException.Provider(RedactedPrefix(error.Message, request));
        }
    }

    /// <summary>
    /// Sends the request and decodes a 2xx JSON body of at most <paramref name="limit"/> bytes; any other status throws
    /// as in <see cref="StreamAsync"/>. Unlike a stream's idle timeout, the request's timeout here is total (headers
    /// and body): an accepted difference, as these bodies are small and come at once.
    /// </summary>
    public static async Task<T> GetJsonAsync<T>(
        HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken, int limit = JsonLimit)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutOf(request));
        var data = await Timed(async () =>
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            if (response.IsSuccessStatusCode) return await ReadJsonAsync(body, limit, null, timeout.Token);
            var (prefix, truncated) = await ReadErrorAsync(body, request, timeout.Token);
            throw LLMException.HttpStatus((int)response.StatusCode, RedactedPrefix(prefix, request, truncated));
        }, cancellationToken);
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new JsonException("Expected a JSON object.");
    }

    /// <summary>
    /// Sends the request <paramref name="makeRequest"/> builds and passes a 2xx body of at most <paramref name="limit"/>
    /// bytes and the headers to <paramref name="read"/>; any other status throws as in <see cref="StreamAsync"/>, and a
    /// provider error <paramref name="read"/> throws (a message from the vendor) is redacted and cut like an error body.
    /// The request's timeout is an idle timeout, as in Swift: it restarts with every chunk of the upload and of the
    /// answer, within <see cref="RequestCeiling"/> in all. With <paramref name="retry"/>, one retry after
    /// <see cref="RetryDelay"/> following a 5xx or a timeout when <see cref="MayRetry"/> (never a billed request); never
    /// after a 4xx or a cancellation. <paramref name="makeRequest"/> runs again for the retry (a sent request and its
    /// file stream cannot be sent twice).
    /// </summary>
    public static async Task<T> SendAsync<T>(
        HttpClient client, Func<HttpRequestMessage> makeRequest, bool retry, int limit,
        Func<byte[], HttpResponseHeaders, T> read, CancellationToken cancellationToken)
    {
        using var request = makeRequest();
        try
        {
            return await SendOnceAsync(client, request, limit, read, cancellationToken);
        }
        catch (Exception error) when (retry && MayRetry(request) && IsRetryable(error) && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(RetryDelay, cancellationToken);
            using var again = makeRequest();
            return await SendOnceAsync(client, again, limit, read, cancellationToken);
        }
    }

    /// <summary>
    /// Whether sending <paramref name="request"/> twice costs nothing: GETs (polls, key checks) and audio uploads
    /// (AssemblyAI <c>/v2/upload</c>, Gemini Files API). Billed one-shot POSTs (transcription requests,
    /// <c>generateContent</c>, AssemblyAI <c>/v2/transcript</c>) are sent once, since a retry after a lost answer could
    /// start a second paid job. Pure, for tests.
    /// </summary>
    public static bool MayRetry(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head) return true;
        if (request.Method != HttpMethod.Post) return false;
        var path = request.RequestUri?.AbsolutePath ?? "";
        return path is "/v2/upload" or "/upload/v1beta/files";
    }

    /// <summary>A 5xx or a timeout (not a connection failure, as in Swift).</summary>
    private static bool IsRetryable(Exception error) =>
        error is TimeoutException || error is LLMException { Kind: LLMErrorKind.HttpStatus, StatusCode: >= 500 and < 600 };

    private static async Task<T> SendOnceAsync<T>(
        HttpClient client, HttpRequestMessage request, int limit, Func<byte[], HttpResponseHeaders, T> read,
        CancellationToken cancellationToken)
    {
        var idle = TimeoutOf(request);
        var elapsed = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void Restart()
        {
            var left = RequestCeiling - elapsed.Elapsed;
            try
            {
                timeout.CancelAfter(left < idle ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : idle);
            }
            catch (ObjectDisposedException)
            {
                // The handler may still write the body after the answer came (an early error status).
            }
        }
        Restart();
        if (request.Content is { } content) request.Content = new ProgressContent(content, Restart);
        return await Timed(async () =>
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var (prefix, truncated) = await ReadErrorAsync(body, request, timeout.Token);
                throw LLMException.HttpStatus((int)response.StatusCode, RedactedPrefix(prefix, request, truncated));
            }
            var data = await ReadJsonAsync(body, limit, Restart, timeout.Token);
            try
            {
                return read(data, response.Headers);
            }
            catch (LLMException error) when (error.Kind == LLMErrorKind.Provider)
            {
                throw LLMException.Provider(RedactedPrefix(error.Message, request));
            }
        }, cancellationToken);
    }

    /// <summary>
    /// The whole body; past <paramref name="limit"/> the response is refused. <paramref name="progress"/> runs after each
    /// chunk.
    /// </summary>
    private static async Task<byte[]> ReadJsonAsync(Stream body, int limit, Action? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var data = new MemoryStream();
        while (await body.ReadAsync(buffer, cancellationToken) is var count and > 0)
        {
            data.Write(buffer, 0, count);
            if (data.Length > limit) throw LLMException.Provider($"The provider's response is larger than {limit / (1024 * 1024)} MB.");
            progress?.Invoke();
        }
        return data.ToArray();
    }

    /// <summary>
    /// The start of an error body: <see cref="ErrorBodyLimit"/> plus the longest form a key can take, so a key that
    /// starts within the kept part is read whole and redacted; and whether the body went on (then a key cut off at the
    /// end of the read, in any form and after a body of any script, is dropped by <see cref="RedactedPrefix"/>).
    /// </summary>
    private static async Task<(string Text, bool Truncated)> ReadErrorAsync(
        Stream body, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var limit = ErrorBodyLimit + MaxEscapedLength(Secrets(request));
        var text = await ReadPrefixAsync(body, limit, cancellationToken);
        return (text.Text, text.Length == limit);
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

    private static async Task<(string Text, int Length)> ReadPrefixAsync(Stream body, int limit, CancellationToken cancellationToken)
    {
        var bytes = new byte[limit];
        var length = 0;
        while (length < limit)
        {
            var count = await body.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (count == 0) break;
            length += count;
        }
        return (Utf8(bytes, length), length);
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

    /// <summary><paramref name="inner"/>, calling <paramref name="progress"/> after each chunk the handler sends.</summary>
    private sealed class ProgressContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action _progress;

        public ProgressContent(HttpContent inner, Action progress)
        {
            _inner = inner;
            _progress = progress;
            foreach (var (name, values) in inner.Headers)
                if (name != "Content-Length") Headers.TryAddWithoutValidation(name, values);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            _inner.CopyToAsync(new ProgressStream(stream, _progress), context, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? -1;
            return length >= 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A write-only view of <paramref name="inner"/> that calls <paramref name="progress"/> after each write.</summary>
    private sealed class ProgressStream(Stream inner, Action progress) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            progress();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            progress();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
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
