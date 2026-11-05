namespace EchoDiar.AI.Cloud;

/// <summary>
/// ElevenLabs Scribe: <c>POST https://api.elevenlabs.io/v1/speech-to-text</c> (multipart, <c>xi-api-key</c>). The answer
/// is timed words, grouped into segments by speaker and pause.
/// </summary>
public sealed class ElevenLabsTranscriber : ICloudTranscriber
{
    public static readonly Uri Endpoint = new("https://api.elevenlabs.io/v1/speech-to-text");
    public const string DefaultModel = "scribe_v2";

    private readonly string _apiKey;
    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/>: null for the shared transport; a test seam otherwise (still behind the redirect guard).</summary>
    public ElevenLabsTranscriber(string apiKey, string model = DefaultModel, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        Model = model;
        _http = ProviderHttp.Client(handler);
    }

    public string Model { get; }

    public Task<IReadOnlyList<CloudSegment>> TranscribeAsync(
        string file, double duration, string? language, bool diarize, CancellationToken cancellationToken = default) =>
        ProviderHttp.SendAsync<IReadOnlyList<CloudSegment>>(
            _http, () => MakeRequest(_apiKey, Model, file, language, diarize), retry: false, ProviderHttp.JsonLimit,
            (body, _) => Parse(body), cancellationToken);

    /// <summary>Settings' Test: <c>GET /v1/models</c> (free, no audio). Pure, for tests.</summary>
    public static HttpRequestMessage MakeKeyCheckRequest(string apiKey) =>
        CloudTranscription.Request(
            HttpMethod.Get, new Uri("https://api.elevenlabs.io/v1/models"), ProviderHttp.ListTimeout, "xi-api-key", apiKey);

    /// <summary>Pure, for tests.</summary>
    public static HttpRequestMessage MakeRequest(string apiKey, string model, string file, string? language, bool diarize)
    {
        var request = CloudTranscription.Request(HttpMethod.Post, Endpoint, CloudTranscription.RequestTimeout, "xi-api-key", apiKey);
        var fields = new List<(string, string)> { ("model_id", model) };
        if (diarize) fields.Add(("diarize", "true"));
        if (CloudTranscription.LanguageCode(language) is { } code) fields.Add(("language_code", code));
        request.Content = CloudTranscription.MultipartForm(file, fields);
        return request;
    }

    /// <summary>
    /// <c>words</c> (<c>word</c>, <c>spacing</c>, <c>audio_event</c>) to segments; spacing stays in the text, audio events
    /// are dropped. Pure, for tests.
    /// </summary>
    public static List<CloudSegment> Parse(ReadOnlySpan<byte> data)
    {
        var response = ProviderHttp.Decode<SpeechToTextResponse>(data);
        var words = new List<CloudTranscription.Word>();
        foreach (var word in response.Words ?? [])
        {
            switch (word.Type)
            {
                case "spacing":
                    if (words.Count > 0) words[^1] = words[^1] with { Text = words[^1].Text + word.Text };
                    break;
                case "audio_event":
                    break;
                default:
                    if (word.Start is { } start && word.End is { } end) words.Add(new(start, end, word.Text, word.SpeakerId));
                    break;
            }
        }
        return CloudTranscription.Segments(words, "", response.LanguageCode);
    }

    /// <summary>The realtime adapter's descriptor (<see cref="Realtime"/>).</summary>
    public static readonly RealtimeAdapterType RealtimeType = new(
        ["scribe_v2_realtime"], new Uri("wss://api.elevenlabs.io/v1/speech-to-text/realtime"), null,
        (apiKey, model, language, diarize) => new Realtime(apiKey, model, language));

    /// <summary>
    /// Scribe v2 Realtime: <c>wss://api.elevenlabs.io/v1/speech-to-text/realtime?...</c> (settings in the query, no start
    /// message), <c>xi-api-key</c> handshake header, 16 kHz PCM sent as base64 in JSON. The server commits on silence
    /// (<c>commit_strategy=vad</c>); <c>Finalize</c> and <c>CloseMessages</c> also send a manual commit (when audio came
    /// since the last one) so the last stretch becomes final. Partials have no times (display only); finals are
    /// <c>committed_transcript_with_timestamps</c> words in seconds (the plain <c>committed_transcript</c> repeats them
    /// without times and is ignored). No streaming speakers and no end message: once closing, the timed answer to the
    /// last manual commit gives <see cref="RealtimeEvent.Ended"/> (even without words) and <c>IsDrained</c>, and the
    /// runner closes the socket. A manual commit counts as answered by the next timed commit message (VAD commits answer
    /// too, so the count stops at 0); the plain one is not waited for, since its order against the timed one is not
    /// documented.
    /// </summary>
    public sealed class Realtime : IRealtimeAdapter
    {
        private readonly string _apiKey;
        private readonly string? _language;

        /// <summary>
        /// Audio was framed since the last manual commit: a commit without new audio could be <c>commit_throttled</c>, and
        /// the server closes after an error.
        /// </summary>
        private bool _uncommitted;

        /// <summary>Manual commits sent and not yet answered by a <c>committed_transcript_with_timestamps</c>.</summary>
        private int _unansweredCommits;

        private bool _closing;

        public Realtime(string apiKey, string model, string? language)
        {
            _apiKey = apiKey;
            Model = model;
            _language = CloudTranscription.LanguageCode(language);
        }

        public string Model { get; }

        public RealtimeAdapterType Type => RealtimeType;

        public int SampleRate => 16_000;

        public IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string> { ["xi-api-key"] = _apiKey };

        public Uri SocketUrl(byte[]? sessionAnswer)
        {
            var query = new List<(string, string)>
            {
                ("model_id", Model), ("audio_format", "pcm_" + CloudRealtime.Invariant(SampleRate)), ("commit_strategy", "vad"),
                ("include_timestamps", "true"),
            };
            if (_language is { } language) query.Add(("language_code", language));
            return CloudRealtime.WithQuery(RealtimeType.Endpoint, query);
        }

        public RealtimeMessage Frame(byte[] pcm)
        {
            _uncommitted = true;
            return Chunk(Convert.ToBase64String(pcm), commit: false);
        }

        /// <summary>A manual commit, only when audio was framed since the last one.</summary>
        public IReadOnlyList<RealtimeMessage> Finalize(double streamSeconds)
        {
            if (!_uncommitted) return [];
            _uncommitted = false;
            _unansweredCommits++;
            return [Chunk("", commit: true)];
        }

        public IReadOnlyList<RealtimeMessage> CloseMessages()
        {
            _closing = true;
            return Finalize(0);
        }

        /// <summary>Closing, and every manual commit is answered.</summary>
        public bool IsDrained => _closing && _unansweredCommits == 0;

        /// <summary>
        /// <c>session_started</c> starts; a non-empty <c>partial_transcript</c> is a partial; the timed words of
        /// <c>committed_transcript_with_timestamps</c> become finals (grouped like the batch words, without speakers); a
        /// message with an <c>error</c> fails with its <c>message_type</c>; <see cref="RealtimeEvent.Ended"/> follows the
        /// answer to the last commit once closing.
        /// </summary>
        public IReadOnlyList<RealtimeEvent> Parse(RealtimeMessage message)
        {
            if (CloudRealtime.Decode<LiveEnvelope>(message) is not { } envelope) return [];
            if (envelope.Error is not null) return [new RealtimeEvent.Failed(CloudRealtime.ErrorKind(envelope.MessageType))];
            switch (envelope.MessageType)
            {
                case "session_started":
                    return [new RealtimeEvent.Started()];
                case "partial_transcript":
                    var text = envelope.Text?.Trim() ?? "";
                    return text.Length == 0 ? [] : [new RealtimeEvent.Partial(text)];
                case "committed_transcript_with_timestamps":
                    var events = new List<RealtimeEvent>();
                    foreach (var segment in ElevenLabsTranscriber.Parse(CloudRealtime.Payload(message)))
                        events.Add(new RealtimeEvent.Final(segment with { Speaker = null }));
                    var answered = _unansweredCommits > 0;
                    _unansweredCommits = Math.Max(_unansweredCommits - 1, 0);
                    if (answered && IsDrained) events.Add(new RealtimeEvent.Ended());
                    return events;
                default:
                    return [];
            }
        }

        private RealtimeMessage Chunk(string base64, bool commit) => new RealtimeMessage.Text(
            "{\"message_type\":\"input_audio_chunk\",\"audio_base_64\":\"" + base64 + "\",\"commit\":"
            + (commit ? "true" : "false") + ",\"sample_rate\":" + CloudRealtime.Invariant(SampleRate) + "}");
    }

    private sealed record LiveEnvelope(string? MessageType = null, string? Text = null, string? Error = null);

    private sealed record SpeechToTextResponse(string? LanguageCode = null, List<SpeechToTextResponse.Word>? Words = null)
    {
        internal sealed record Word(string Text, double? Start = null, double? End = null, string? Type = null, string? SpeakerId = null);
    }
}
