using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EchoDiar.Core.Settings;

namespace EchoDiar.AI.Cloud;

// Messages and events.

/// <summary>
/// One WebSocket message, as the adapters build and parse it (Swift <c>RealtimeMessage</c>). Value equality, also for
/// the bytes of a binary message.
/// </summary>
public abstract record RealtimeMessage
{
    private RealtimeMessage()
    {
    }

    public sealed record Text(string Value) : RealtimeMessage;

    public sealed record Binary(byte[] Data) : RealtimeMessage
    {
        public bool Equals(Binary? other) => other is not null && Data.AsSpan().SequenceEqual(other.Data);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(Data);
            return hash.ToHashCode();
        }
    }
}

/// <summary>
/// What a provider's message means for the live transcript (Swift <c>RealtimeEvent</c>). Times are seconds from the start
/// of the stream (the socket's first audio); the runner adds the stream's offset in the recording.
/// </summary>
public abstract record RealtimeEvent
{
    private RealtimeEvent()
    {
    }

    /// <summary>The session accepts audio; <c>ExpiresAt</c> is the provider's session end when it sends one (rotate before it).</summary>
    public sealed record Started(DateTimeOffset? ExpiresAt = null) : RealtimeEvent;

    /// <summary>
    /// The text so far of the current stretch; it replaces the previous partial. Times are null when the provider sends
    /// none (ElevenLabs, OpenAI): display only.
    /// </summary>
    public sealed record Partial(string Text, double? Start = null, double? End = null, string? Speaker = null) : RealtimeEvent;

    /// <summary>Text that will not change, with its times and the provider's speaker label (null without streaming speakers).</summary>
    public sealed record Final(CloudSegment Segment) : RealtimeEvent;

    /// <summary>
    /// The provider reported an error. <c>Kind</c> is its short error type or code (<see cref="CloudRealtime.ErrorKind"/>),
    /// never the message text, which is external.
    /// </summary>
    public sealed record Failed(string Kind) : RealtimeEvent;

    /// <summary>The provider finished the stream (after the close messages); the socket closes next.</summary>
    public sealed record Ended : RealtimeEvent;
}

/// <summary>A message the session needs every <paramref name="Interval"/> while no audio flows, or the provider closes it.</summary>
public sealed record RealtimeKeepAlive(RealtimeMessage Message, TimeSpan Interval);

// Adapter.

/// <summary>
/// What Swift keeps as an adapter's static members and its <c>init</c>: the known models (the default first), the
/// socket's base URL (<c>wss://host/path</c>: its origin is the consent destination, and the URL the session connects to
/// must have its host and port), the session-init endpoint when there is one (Gladia <c>POST /v2/live</c>; it must be on
/// <c>Endpoint</c>'s origin), and the factory <c>(apiKey, model, language, diarize)</c>. A descriptor rather than static
/// abstract interface members: the catalog picks it by preset at run time, and an interface with static abstract members
/// could not be used as a type argument. Each adapter exposes one <c>public static readonly RealtimeAdapterType</c>.
/// </summary>
/// <param name="Create">
/// <c>model</c>: never empty (the catalog passes the default). <c>language</c>: a BCP-47 code, or null / <c>auto</c>.
/// <c>diarize</c>: ask for streaming speaker labels where the provider has them.
/// </param>
public sealed record RealtimeAdapterType(
    IReadOnlyList<string> Models, Uri Endpoint, Uri? InitUrl, Func<string, string, string?, bool, IRealtimeAdapter> Create);

/// <summary>
/// One provider's realtime protocol for one socket (one channel), built for a model, a language and the diarize flag
/// (Swift <c>RealtimeAdapter</c>). Everything here is pure: building requests and messages, framing audio, parsing
/// answers; the I/O is <see cref="RealtimeConnection"/>. Adapters keep per-socket state (frames sent, commit offsets,
/// words already final), so the runner makes one instance per socket and calls it from one task at a time (not
/// thread-safe). The defaults are default interface members: call them through <see cref="IRealtimeAdapter"/>.
/// <para>
/// Keys: the long-lived API key goes in <see cref="Headers"/> (or the init request's header), never in a URL, a message
/// that is logged, or an error. A short-lived session token returned by the init call may be in the socket URL.
/// </para>
/// </summary>
public interface IRealtimeAdapter
{
    /// <summary>The adapter's descriptor (models, endpoint, init URL), the same object the catalog returns for its preset.</summary>
    RealtimeAdapterType Type { get; }

    /// <summary>Sample rate of the Int16 mono PCM <see cref="Frame"/> takes: 16 kHz, or 24 kHz (OpenAI).</summary>
    int SampleRate => 16_000;

    /// <summary>Whether finals carry the provider's speaker labels (else the existing diarizer runs).</summary>
    bool StreamsSpeakers => false;

    /// <summary>
    /// The init call, sent once per session and never retried (with the redacted errors of <c>ProviderHttp</c>); null when
    /// the socket URL is fixed. A new message each call.
    /// </summary>
    HttpRequestMessage? SessionRequest() => null;

    /// <summary>The socket URL; <paramref name="sessionAnswer"/> is the init call's 2xx body (null without one). No long-lived key in it.</summary>
    Uri SocketUrl(byte[]? sessionAnswer);

    /// <summary>Handshake headers (the auth header when the provider takes the key there).</summary>
    IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string>();

    /// <summary>
    /// Messages sent right after the socket opens, before any audio (config / start). Built before the socket opens, so a
    /// setting the provider cannot take (Speechmatics Auto) throws without opening (and billing) a socket.
    /// </summary>
    IReadOnlyList<RealtimeMessage> StartMessages() => [];

    /// <summary>One chunk of little-endian Int16 mono PCM at <see cref="SampleRate"/> as a message: binary, or base64 in JSON.</summary>
    RealtimeMessage Frame(byte[] pcm) => new RealtimeMessage.Binary(pcm);

    /// <summary>
    /// Messages that end the current stretch at <paramref name="streamSeconds"/> (stream time of the last audio sent), so
    /// its text becomes final.
    /// </summary>
    IReadOnlyList<RealtimeMessage> Finalize(double streamSeconds) => [];

    /// <summary>
    /// Messages that end the stream; the provider sends its last finals, then <see cref="RealtimeEvent.Ended"/> or a
    /// close, or (a provider without an end message) <see cref="IsDrained"/> becomes true. An empty result does not mean
    /// the stream is over.
    /// </summary>
    IReadOnlyList<RealtimeMessage> CloseMessages() => [];

    /// <summary>
    /// True once nothing more will come from this stream after <see cref="CloseMessages"/> (every commit it waits for is
    /// answered). The runner reads it right after <see cref="CloseMessages"/> and after every <see cref="Parse"/>; true
    /// means close the socket. Default false: wait for <see cref="RealtimeEvent.Ended"/> or the provider's close.
    /// </summary>
    bool IsDrained => false;

    /// <summary>Sent while no audio flows (paused stream, waiting); null when not needed.</summary>
    RealtimeKeepAlive? KeepAlive => null;

    /// <summary>
    /// The events of one server message; unknown message types give an empty list, a provider error
    /// <see cref="RealtimeEvent.Failed"/> with <see cref="CloudRealtime.ErrorKind"/> of its code or type. Throws
    /// <see cref="JsonException"/> on undecodable JSON.
    /// </summary>
    IReadOnlyList<RealtimeEvent> Parse(RealtimeMessage message);
}

// Catalog.

/// <summary>
/// The cloud providers with a realtime (WebSocket) transcription API, per <c>stt-protocols.md</c>: ElevenLabs, Deepgram,
/// AssemblyAI, OpenAI, xAI, Speechmatics, Soniox and Gladia (Swift <c>CloudRealtime</c>). Realtime always uses the
/// default cloud engine's provider, key and consent; the model is the config's <c>RealtimeModel</c>, else the adapter's
/// default.
/// </summary>
public static class CloudRealtime
{
    /// <summary>The adapter descriptor of a preset; null when it has no realtime API (Gemini, Groq, Mistral, custom endpoints).</summary>
    public static RealtimeAdapterType? AdapterType(string? preset) => preset switch
    {
        "elevenlabs" => ElevenLabsTranscriber.RealtimeType,
        "deepgram" => DeepgramTranscriber.RealtimeType,
        "assemblyai" => AssemblyAITranscriber.RealtimeType,
        "openai" => OpenAICompatibleTranscriber.RealtimeType,
        "xai" => XAITranscriber.RealtimeType,
        "speechmatics" => SpeechmaticsTranscriber.RealtimeType,
        "soniox" => SonioxTranscriber.RealtimeType,
        "gladia" => GladiaTranscriber.RealtimeType,
        _ => null,
    };

    /// <summary>Whether <paramref name="config"/> streams: a cloud transcription config (never a custom endpoint) of a realtime preset.</summary>
    public static bool Supports(ProviderConfig config) =>
        CloudTranscription.Supports(config) && AdapterType(config.Preset) is not null;

    /// <summary>Known realtime models of <paramref name="config"/>, the default first; empty when unsupported.</summary>
    public static IReadOnlyList<string> Models(ProviderConfig config) =>
        Supports(config) ? AdapterType(config.Preset)?.Models ?? [] : [];

    public static string DefaultModel(ProviderConfig config) => Models(config).FirstOrDefault() ?? "";

    /// <summary><c>RealtimeModel</c> (trimmed), else the default.</summary>
    public static string Model(ProviderConfig config)
    {
        var model = config.RealtimeModel.Trim();
        return model.Length == 0 ? DefaultModel(config) : model;
    }

    /// <summary>The adapter for one socket of <paramref name="config"/>; null when unsupported or without a model.</summary>
    public static IRealtimeAdapter? Make(ProviderConfig config, string apiKey, string? language, bool diarize)
    {
        if (!Supports(config) || AdapterType(config.Preset) is not { } type) return null;
        var model = Model(config);
        return model.Length == 0 ? null : type.Create(apiKey, model, language, diarize);
    }

    /// <summary>
    /// Where the realtime audio and key go, in <see cref="CloudTranscription.Destination(ProviderConfig)"/>'s consent
    /// format <c>&lt;provider id&gt;@&lt;scheme://host[:port]&gt;</c>. <c>wss</c> counts as <c>https</c> (<c>ws</c> as
    /// <c>http</c>), so a provider whose socket is on its batch host (Deepgram, ElevenLabs, xAI, OpenAI, Gladia) shares
    /// the batch consent, and a separate realtime host (Speechmatics <c>eu.rt</c>, Soniox <c>stt-rt</c>, AssemblyAI
    /// <c>streaming</c>) has its own key. The init call must be on the socket's origin, so one key covers both; null when
    /// unsupported or when it is not.
    /// </summary>
    public static CloudDestination? Destination(ProviderConfig config)
    {
        if (!Supports(config) || AdapterType(config.Preset) is not { } type || Origin(type.Endpoint) is not { } socket) return null;
        if (type.InitUrl is { } initUrl && Origin(initUrl)?.Origin != socket.Origin) return null;
        return new($"{config.Id}@{socket.Origin}", socket.Host);
    }

    /// <summary>
    /// <c>scheme://host[:port]</c> (lowercased, <c>wss</c> -> <c>https</c>, <c>ws</c> -> <c>http</c>, default port
    /// dropped) and the host; null for a relative URL or one without a host.
    /// </summary>
    internal static (string Origin, string Host)? Origin(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Host.Length == 0) return null;
        var raw = url.Scheme.ToLowerInvariant();
        var scheme = raw switch { "wss" => "https", "ws" => "http", _ => raw };
        var host = url.Host.ToLowerInvariant();
        int? defaultPort = scheme switch { "https" => 443, "http" => 80, _ => null };
        var port = url.Port < 0 || url.Port == defaultPort ? "" : ":" + url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ($"{scheme}://{host}{port}", host);
    }

    /// <summary>
    /// Whether <paramref name="adapter"/> may connect to <paramref name="url"/>: <c>wss</c> on the adapter's endpoint host
    /// and port, without user info. The init call's answer is external, so its URL is checked before any connection.
    /// Pure, for tests.
    /// </summary>
    public static bool AllowsSocket(Uri url, IRealtimeAdapter adapter)
    {
        var endpoint = adapter.Type.Endpoint;
        if (!url.IsAbsoluteUri || !endpoint.IsAbsoluteUri || !url.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Host.Length == 0 || url.UserInfo.Length > 0) return false;
        return url.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) && PortOrDefault(url) == PortOrDefault(endpoint);
    }

    private static int PortOrDefault(Uri url) => url.Port >= 0 ? url.Port : 443;

    /// <summary>
    /// A provider error type or code reduced to a short identifier (ASCII letters, digits, <c>_</c>, <c>-</c>, <c>.</c>;
    /// at most 40 characters), so external text never reaches the UI or a log; "error" when nothing is left. Pure, for
    /// tests.
    /// </summary>
    public static string ErrorKind(string? raw)
    {
        var kind = new string((raw ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.').Take(40).ToArray());
        return kind.Length == 0 ? "error" : kind;
    }

    // Adapter helpers.

    /// <summary>
    /// A server message decoded as <typeparamref name="T"/> with <c>ProviderHttp.Json</c> (snake_case keys; nullable
    /// annotations respected, so DTO members a provider may omit must be nullable); null for an empty message. Throws
    /// <see cref="JsonException"/> on undecodable JSON.
    /// </summary>
    internal static T? Decode<T>(RealtimeMessage message) where T : class
    {
        var data = Payload(message);
        return data.Length == 0 ? null : ProviderHttp.Decode<T>(data);
    }

    /// <summary>The bytes of a message (a text message's UTF-8).</summary>
    internal static byte[] Payload(RealtimeMessage message) => message switch
    {
        RealtimeMessage.Text text => Encoding.UTF8.GetBytes(text.Value),
        RealtimeMessage.Binary binary => binary.Data,
        _ => [],
    };

    /// <summary>
    /// <paramref name="value"/> as a JSON text message, object keys sorted (ordinal, at every level) and slashes not
    /// escaped, so the bytes are stable and match Swift's <c>.sortedKeys, .withoutEscapingSlashes</c>.
    /// </summary>
    internal static RealtimeMessage Json(JsonNode value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteSorted(writer, value);
        return new RealtimeMessage.Text(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (name, child) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(name);
                    WriteSorted(writer, child);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var child in array) WriteSorted(writer, child);
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// <paramref name="endpoint"/> with <paramref name="items"/> as its query, names and values percent-encoded (Swift
    /// <c>appending(queryItems:)</c>).
    /// </summary>
    internal static Uri WithQuery(Uri endpoint, IEnumerable<(string Name, string Value)> items) =>
        new(endpoint.AbsoluteUri + "?" + string.Join("&",
            items.Select(item => Uri.EscapeDataString(item.Name) + "=" + Uri.EscapeDataString(item.Value))));

    /// <summary>
    /// A provider's session end in Unix seconds (Swift <c>Date(timeIntervalSince1970:)</c>); null without one, or when it
    /// is not a representable date (the value is external).
    /// </summary>
    internal static DateTimeOffset? ExpiresAt(double? unixSeconds)
    {
        if (unixSeconds is not { } seconds || !double.IsFinite(seconds)) return null;
        var max = (DateTimeOffset.MaxValue - DateTimeOffset.UnixEpoch).TotalSeconds;
        var min = (DateTimeOffset.MinValue - DateTimeOffset.UnixEpoch).TotalSeconds;
        return seconds > min && seconds < max ? DateTimeOffset.UnixEpoch.AddSeconds(seconds) : null;
    }

    /// <summary>An integer as invariant-culture digits (query values, hand-written JSON messages).</summary>
    internal static string Invariant(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // PCM.

    /// <summary>
    /// Float samples (-1...1, clamped; non-finite ones as 0) as little-endian Int16 PCM, rounded half away from zero as
    /// Swift's <c>rounded()</c>. Pure, for tests.
    /// </summary>
    public static byte[] Pcm16(ReadOnlySpan<float> samples)
    {
        var data = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = float.IsFinite(samples[i]) ? samples[i] : 0f;
            var value = (short)MathF.Round(Math.Min(Math.Max(sample, -1f), 1f) * 32_767f, MidpointRounding.AwayFromZero);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), value);
        }
        return data;
    }
}

/// <summary>
/// A streaming linear resampler (16 -> 24 kHz for OpenAI; Swift <c>RealtimeResampler</c>, same output): it keeps the
/// last sample and the phase between calls, so chunk edges do not click and the output length follows the total input
/// length. One instance per stream, not thread-safe. Pure, for tests.
/// </summary>
public sealed class RealtimeResampler
{
    private float? _last;

    /// <summary>
    /// Position of the next output sample in units of 1 / <see cref="OutputRate"/> input samples, from <c>_last</c> (or
    /// from the first sample before any input). 64-bit, as Swift's <c>Int</c>.
    /// </summary>
    private long _position;

    public RealtimeResampler(int from, int to)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(from);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(to);
        InputRate = from;
        OutputRate = to;
    }

    public int InputRate { get; }
    public int OutputRate { get; }

    public float[] Process(ReadOnlySpan<float> input)
    {
        if (input.IsEmpty) return [];
        float[] samples;
        if (_last is { } last)
        {
            samples = new float[input.Length + 1];
            samples[0] = last;
            input.CopyTo(samples.AsSpan(1));
        }
        else
        {
            samples = input.ToArray();
        }
        var end = (long)(samples.Length - 1) * OutputRate;
        var output = new List<float>((int)Math.Min(int.MaxValue, (long)input.Length * OutputRate / InputRate + 1));
        while (_position < end)
        {
            var index = (int)(_position / OutputRate);
            var fraction = (float)(_position % OutputRate) / OutputRate;
            output.Add(samples[index] + (samples[index + 1] - samples[index]) * fraction);
            _position += InputRate;
        }
        _position -= end;
        _last = samples[^1];
        return [.. output];
    }
}

// Connection.

/// <summary>
/// One realtime socket (Swift <c>RealtimeConnection</c>). Call <see cref="SocketUrlAsync"/> once per provider session
/// (it runs the init call when the adapter has one: a new, billed session each time), then <see cref="ConnectAsync"/>.
/// No reconnects here. One <see cref="SendAsync"/> at a time is queued behind the other (as URLSession does), and one
/// <see cref="ReceiveAsync"/> may run beside it. Cancelling a send or receive aborts the socket.
/// <para>
/// Errors never carry the socket URL (it may hold Gladia's session token) or the key: transport failures are rethrown
/// with a fixed text (an <see cref="HttpRequestException"/> keeping its <see cref="HttpRequestError"/>, else a
/// <see cref="WebSocketException"/> keeping its <see cref="WebSocketError"/>), and a refused handshake is
/// <see cref="LLMErrorKind.HttpStatus"/> with an empty body.
/// </para>
/// <para>
/// Redirects: the handshake goes through a handler with <c>AllowAutoRedirect = false</c>, so a 3xx answer is a refused
/// handshake (its status) and the headers never reach another host. Keep it that way.
/// </para>
/// </summary>
public sealed class RealtimeConnection : IDisposable
{
    /// <summary>The largest server message, in bytes: URLSession's default <c>maximumMessageSize</c> (1 MiB).</summary>
    public const int MessageLimit = 1024 * 1024;

    /// <summary>The longest handshake (URLSession's default request timeout).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How often the socket sends a WebSocket ping.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a ping may go unanswered before the socket is aborted (Swift <c>idleTimeout</c>).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(300);

    /// <summary>How long <see cref="CloseAsync"/> waits to send the close frame.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The handshake transport: no redirects, no cookies; the system proxy as the batch requests.</summary>
    private static readonly Lazy<HttpMessageInvoker> SharedInvoker = new(() =>
        new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }));

    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sending = new(1, 1);

    private RealtimeConnection(ClientWebSocket socket)
    {
        _socket = socket;
    }

    /// <summary>The close status the server sent; null while open or after a failure without one.</summary>
    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    /// <summary>
    /// The URL to connect to: the adapter's, after its init call when it has one (sent once, never retried, errors
    /// redacted). The URL may carry a session token: never log it. <paramref name="handler"/>: a test seam for the init
    /// call (null for the shared transport, behind the redirect guard).
    /// </summary>
    public static async Task<Uri> SocketUrlAsync(
        IRealtimeAdapter adapter, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        if (adapter.SessionRequest() is not { } request) return adapter.SocketUrl(null);
        var answer = await ProviderHttp.SendAsync(
            ProviderHttp.Client(handler), () => request, retry: false, ProviderHttp.SmallJsonLimit, (data, _) => data,
            cancellationToken);
        return adapter.SocketUrl(answer);
    }

    /// <summary>
    /// Builds the adapter's start messages, opens the socket to <paramref name="url"/> with its headers and sends them.
    /// Throws <see cref="LLMErrorKind.HttpStatus"/> with an empty body when the provider refuses the handshake (401 for a
    /// bad key), <see cref="TimeoutException"/> after <see cref="ConnectTimeout"/>, and a provider error before any
    /// connection when <see cref="CloudRealtime.AllowsSocket"/> refuses the URL. <paramref name="invoker"/>: a test seam
    /// (null for the shared no-redirect transport).
    /// </summary>
    public static async Task<RealtimeConnection> ConnectAsync(
        IRealtimeAdapter adapter, Uri url, HttpMessageInvoker? invoker = null, CancellationToken cancellationToken = default)
    {
        if (!CloudRealtime.AllowsSocket(url, adapter)) throw LLMException.Provider("Unexpected realtime host.");
        var start = adapter.StartMessages();
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.CollectHttpResponseDetails = true;
            // WebSocket pings every 30 s (Swift: the runner's ping()), so idle sockets stay open (Speechmatics closes after
            // 3 min without audio or ping/pong); a ping unanswered for IdleTimeout aborts the socket (Swift: the request
            // timeout), which fails the pending receive.
            socket.Options.KeepAliveInterval = PingInterval;
            socket.Options.KeepAliveTimeout = IdleTimeout;
            foreach (var (name, value) in adapter.Headers) socket.Options.SetRequestHeader(name, value);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(ConnectTimeout);
                try
                {
                    await socket.ConnectAsync(url, invoker ?? SharedInvoker.Value, timeout.Token);
                }
                catch (Exception error) when (error is OperationCanceledException || IsTransport(error))
                {
                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                    if (timeout.IsCancellationRequested) throw new TimeoutException("The realtime connection timed out.");
                    if (socket.HttpStatusCode is var status && status != 0 && status != HttpStatusCode.SwitchingProtocols)
                        throw LLMException.HttpStatus((int)status, "");
                    throw Sanitized(error, "The realtime connection failed.");
                }
            }
            var connection = new RealtimeConnection(socket);
            foreach (var message in start) await connection.SendAsync(message, cancellationToken);
            return connection;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task SendAsync(RealtimeMessage message, CancellationToken cancellationToken = default)
    {
        var (bytes, type) = message switch
        {
            RealtimeMessage.Text text => (Encoding.UTF8.GetBytes(text.Value), WebSocketMessageType.Text),
            RealtimeMessage.Binary binary => (binary.Data, WebSocketMessageType.Binary),
            _ => throw new ArgumentException("Unknown realtime message.", nameof(message)),
        };
        await _sending.WaitAsync(cancellationToken);
        try
        {
            await Guarded(() => _socket.SendAsync(bytes, type, endOfMessage: true, cancellationToken), cancellationToken);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>
    /// The next server message (fragments joined, at most <see cref="MessageLimit"/> bytes). Throws a
    /// <see cref="WebSocketException"/> once the server closed (<see cref="CloseStatus"/> says how) or the socket failed.
    /// </summary>
    public async Task<RealtimeMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16 * 1024];
        using var data = new MemoryStream();
        while (true)
        {
            var result = await Guarded(() => _socket.ReceiveAsync(buffer, cancellationToken), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try
                {
                    // Completes the close handshake; the status description is external text and is not kept.
                    using var timeout = new CancellationTokenSource(CloseTimeout);
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                }
                catch (Exception error) when (IsTransport(error) || error is OperationCanceledException)
                {
                    // Best effort: the server is gone either way.
                }
                throw new WebSocketException(WebSocketError.InvalidState, "The realtime connection was closed.");
            }
            data.Write(buffer, 0, result.Count);
            if (data.Length > MessageLimit)
            {
                _socket.Abort();
                throw new WebSocketException(WebSocketError.Faulted, "The realtime message is too large.");
            }
            if (!result.EndOfMessage) continue;
            return result.MessageType == WebSocketMessageType.Text
                ? (RealtimeMessage)new RealtimeMessage.Text(Encoding.UTF8.GetString(data.GetBuffer(), 0, (int)data.Length))
                : new RealtimeMessage.Binary(data.ToArray());
        }
    }

    /// <summary>
    /// Closes the socket: sends the close frame (waiting at most 2 s, not for the server's answer), then releases it.
    /// Send the adapter's close messages and wait for <see cref="RealtimeEvent.Ended"/> first for a clean end.
    /// </summary>
    public async Task CloseAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
        }
        catch (Exception error) when (IsTransport(error) || error is OperationCanceledException)
        {
            // Best effort: the socket is released below.
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>Aborts the socket at once.</summary>
    public void Dispose()
    {
        _socket.Dispose();
    }

    /// <summary>
    /// Runs a socket operation: a cancellation aborts the socket (and stays an <see cref="OperationCanceledException"/>);
    /// a transport failure is rethrown without its text.
    /// </summary>
    private async Task<T> Guarded<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested
                                      && (error is OperationCanceledException || IsTransport(error)))
        {
            _socket.Abort();
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception error) when (IsTransport(error))
        {
            throw Sanitized(error, "The realtime connection failed.");
        }
    }

    private Task Guarded(Func<Task> operation, CancellationToken cancellationToken) =>
        Guarded(async () =>
        {
            await operation();
            return true;
        }, cancellationToken);

    private static bool IsTransport(Exception error) => error is WebSocketException or HttpRequestException or IOException;

    /// <summary>
    /// <paramref name="error"/> with <paramref name="text"/> instead of its message and no inner exception (whose text
    /// could name the URL): an <see cref="HttpRequestException"/> found in the chain keeps its
    /// <see cref="HttpRequestError"/> (so <see cref="CloudTranscription.FailureReason"/> still says "no internet
    /// connection"), else a <see cref="WebSocketException"/> with the original's <see cref="WebSocketError"/>.
    /// </summary>
    private static Exception Sanitized(Exception error, string text)
    {
        for (var inner = error; inner is not null; inner = inner.InnerException)
            if (inner is HttpRequestException http) return new HttpRequestException(http.HttpRequestError, text);
        return error is WebSocketException socket && socket.WebSocketErrorCode != WebSocketError.Success
            ? new WebSocketException(socket.WebSocketErrorCode, text)
            : new WebSocketException(WebSocketError.Faulted, text);
    }
}
