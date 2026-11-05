namespace EchoDiar.AI.Cloud;

/// <summary>
/// Deepgram pre-recorded: <c>POST https://api.deepgram.com/v1/listen?...</c> with the audio bytes as the body and
/// <c>Authorization: Token &lt;key&gt;</c>. Utterances become segments.
/// </summary>
public sealed class DeepgramTranscriber : ICloudTranscriber
{
    public static readonly Uri Endpoint = new("https://api.deepgram.com/v1/listen");
    public const string DefaultModel = "nova-3";

    private readonly string _apiKey;
    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/>: null for the shared transport; a test seam otherwise (still behind the redirect guard).</summary>
    public DeepgramTranscriber(string apiKey, string model = DefaultModel, HttpMessageHandler? handler = null)
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

    /// <summary>Settings' Test: <c>GET /v1/projects</c> lists the key's projects (free, no audio). Pure, for tests.</summary>
    public static HttpRequestMessage MakeKeyCheckRequest(string apiKey) =>
        CloudTranscription.Request(
            HttpMethod.Get, new Uri("https://api.deepgram.com/v1/projects"), ProviderHttp.ListTimeout, "Authorization", "Token " + apiKey);

    /// <summary>
    /// <c>utterances=true</c> is added to the documented query: without it the answer has no utterances. The body is
    /// <paramref name="file"/>, streamed. Pure, for tests.
    /// </summary>
    public static HttpRequestMessage MakeRequest(string apiKey, string model, string file, string? language, bool diarize)
    {
        var query = new List<(string Name, string Value)>
        {
            ("model", model), ("smart_format", "true"), ("utterances", "true"), ("diarize", diarize ? "true" : "false"),
        };
        query.Add(CloudTranscription.LanguageCode(language) is { } code ? ("language", code) : ("detect_language", "true"));
        var url = new Uri(Endpoint + "?" + string.Join("&",
            query.Select(item => Uri.EscapeDataString(item.Name) + "=" + Uri.EscapeDataString(item.Value))));
        var request = CloudTranscription.Request(HttpMethod.Post, url, CloudTranscription.RequestTimeout, "Authorization", "Token " + apiKey);
        request.Content = CloudTranscription.FileContent(file, CloudTranscription.MimeType(file));
        return request;
    }

    /// <summary><c>results.utterances</c>, else the channel transcript as one segment. Pure, for tests.</summary>
    public static List<CloudSegment> Parse(ReadOnlySpan<byte> data)
    {
        var response = ProviderHttp.Decode<ListenResponse>(data);
        var channel = response.Results.Channels.FirstOrDefault();
        var language = channel?.DetectedLanguage;
        if (response.Results.Utterances is { Count: > 0 } utterances)
        {
            return utterances
                .Select(utterance => (utterance, text: utterance.Transcript.Trim()))
                .Where(item => item.text.Length > 0)
                .Select(item => new CloudSegment(
                    item.utterance.Start, item.utterance.End, item.text,
                    item.utterance.Speaker?.ToString(System.Globalization.CultureInfo.InvariantCulture), language))
                .ToList();
        }
        var text = channel?.Alternatives.FirstOrDefault()?.Transcript.Trim() ?? "";
        return text.Length == 0 ? [] : [new CloudSegment(0, response.Metadata?.Duration ?? 0, text, Language: language)];
    }

    /// <summary>The realtime adapter's descriptor (<see cref="Realtime"/>).</summary>
    public static readonly RealtimeAdapterType RealtimeType = new(
        ["nova-3", "nova-2"], new Uri("wss://api.deepgram.com/v1/listen"), null,
        (apiKey, model, language, diarize) => new Realtime(apiKey, model, language, diarize));

    /// <summary>
    /// Live <c>wss://api.deepgram.com/v1/listen?...</c> (settings in the query, no start message), <c>Authorization:
    /// Token</c> handshake header, binary 16 kHz linear16 frames. <c>Results</c> with <c>is_final:false</c> are partials
    /// (they grow over the same <c>start</c>), <c>is_final:true</c> are finals: the transcript over its words' span (else
    /// <c>start...start+duration</c>), or, when the words carry speakers (<c>diarize_model=latest</c>, integers), the words
    /// grouped by speaker and pause. <c>Finalize</c> flushes, <c>CloseStream</c> ends (answered by <c>Metadata</c>, then
    /// the close); <c>KeepAlive</c> every 4 s while no audio flows (Deepgram closes after 10 s). No ready message: the
    /// session accepts audio once connected. Auto language is <c>language=multi</c> (code-switching; no Vietnamese there,
    /// so pick it explicitly).
    /// </summary>
    public sealed class Realtime : IRealtimeAdapter
    {
        private readonly string _apiKey;
        private readonly string? _language;
        private readonly bool _diarize;

        public Realtime(string apiKey, string model, string? language, bool diarize)
        {
            _apiKey = apiKey;
            Model = model;
            _language = CloudTranscription.LanguageCode(language);
            _diarize = diarize;
        }

        public string Model { get; }

        public RealtimeAdapterType Type => RealtimeType;

        public int SampleRate => 16_000;

        public bool StreamsSpeakers => _diarize;

        public IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string> { ["Authorization"] = "Token " + _apiKey };

        public RealtimeKeepAlive? KeepAlive => new(new RealtimeMessage.Text("{\"type\":\"KeepAlive\"}"), TimeSpan.FromSeconds(4));

        public Uri SocketUrl(byte[]? sessionAnswer)
        {
            var query = new List<(string, string)>
            {
                ("model", Model), ("encoding", "linear16"), ("sample_rate", CloudRealtime.Invariant(SampleRate)), ("channels", "1"),
                ("interim_results", "true"), ("smart_format", "true"), ("endpointing", "300"), ("utterance_end_ms", "1000"),
                ("language", _language ?? "multi"),
            };
            if (_diarize) query.Add(("diarize_model", "latest"));
            return CloudRealtime.WithQuery(RealtimeType.Endpoint, query);
        }

        public IReadOnlyList<RealtimeMessage> Finalize(double streamSeconds) => [new RealtimeMessage.Text("{\"type\":\"Finalize\"}")];

        public IReadOnlyList<RealtimeMessage> CloseMessages() => [new RealtimeMessage.Text("{\"type\":\"CloseStream\"}")];

        /// <summary>
        /// A message with a <c>channel</c> is a <c>Results</c> (the documented interim example has no <c>type</c>);
        /// <c>Metadata</c> ends; <c>Error</c> fails; <c>SpeechStarted</c>, <c>UtteranceEnd</c> and others give nothing.
        /// </summary>
        public IReadOnlyList<RealtimeEvent> Parse(RealtimeMessage message)
        {
            // The type first: `UtteranceEnd` and `SpeechStarted` have an array `channel`, which `LiveResults` refuses.
            if (CloudRealtime.Decode<LiveType>(message) is not { } envelope) return [];
            switch (envelope.Type)
            {
                case "Metadata": return [new RealtimeEvent.Ended()];
                case "Error": return [new RealtimeEvent.Failed(CloudRealtime.ErrorKind(envelope.Type))];
                case null or "Results": break;
                default: return [];
            }
            if (CloudRealtime.Decode<LiveResults>(message) is not { } live
                || live.Channel?.Alternatives.FirstOrDefault() is not { } alternative) return [];
            var text = alternative.Transcript.Trim();
            if (text.Length == 0) return [];
            var start = live.Start ?? alternative.Words?.FirstOrDefault()?.Start ?? 0;
            var end = live.Duration is { } duration ? start + duration : alternative.Words?.LastOrDefault()?.End ?? start;
            var words = (alternative.Words ?? []).Select(word => new CloudTranscription.Word(
                word.Start, word.End, word.PunctuatedWord ?? word.Word,
                word.Speaker?.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
            if (live.IsFinal != true)
                return [new RealtimeEvent.Partial(text, start, end, words.Count > 0 ? words[0].Speaker : null)];
            if (!words.Any(word => word.Speaker is not null))
            {
                return [new RealtimeEvent.Final(new CloudSegment(
                    words.Count > 0 ? words[0].Start : start, words.Count > 0 ? words[^1].End : end, text))];
            }
            return CloudTranscription.Segments(words, " ", null).Select(segment => (RealtimeEvent)new RealtimeEvent.Final(segment)).ToList();
        }
    }

    private sealed record LiveType(string? Type = null);

    private sealed record LiveResults(
        string? Type = null, double? Start = null, double? Duration = null, bool? IsFinal = null, LiveChannel? Channel = null);

    private sealed record LiveChannel(List<LiveAlternative> Alternatives);

    private sealed record LiveAlternative(string Transcript, List<LiveWord>? Words = null);

    private sealed record LiveWord(string Word, double Start, double End, string? PunctuatedWord = null, int? Speaker = null);

    private sealed record ListenResponse(ListenResults Results, ListenMetadata? Metadata = null);

    private sealed record ListenMetadata(double? Duration = null);

    private sealed record ListenResults(List<ListenChannel> Channels, List<ListenUtterance>? Utterances = null);

    private sealed record ListenChannel(List<ListenAlternative> Alternatives, string? DetectedLanguage = null);

    private sealed record ListenAlternative(string Transcript);

    private sealed record ListenUtterance(double Start, double End, string Transcript, int? Speaker = null);
}
