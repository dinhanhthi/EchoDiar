using System.Globalization;

namespace EchoDiar.AI.Cloud;

/// <summary>
/// xAI Grok speech to text: <c>POST https://api.x.ai/v1/stt</c> (multipart, <c>Authorization: Bearer &lt;key&gt;</c>),
/// answered at once with timed words (seconds), grouped into segments by speaker and pause. Speakers are integers from 0.
/// </summary>
public sealed class XAITranscriber : ICloudTranscriber
{
    public static readonly Uri Endpoint = new("https://api.x.ai/v1/stt");
    public const string DefaultModel = "grok-voice-transcribe-2.0";

    private readonly string _apiKey;
    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/>: null for the shared transport; a test seam otherwise (still behind the redirect guard).</summary>
    public XAITranscriber(string apiKey, string model = DefaultModel, HttpMessageHandler? handler = null)
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

    /// <summary>Settings' Test: <c>GET /v1/api-key</c> describes the key (free, no audio). Pure, for tests.</summary>
    public static HttpRequestMessage MakeKeyCheckRequest(string apiKey) =>
        CloudTranscription.Request(
            HttpMethod.Get, new Uri("https://api.x.ai/v1/api-key"), ProviderHttp.ListTimeout, "Authorization", "Bearer " + apiKey);

    /// <summary><c>file</c> is the last field: xAI may ignore the fields sent after it. Pure, for tests.</summary>
    public static HttpRequestMessage MakeRequest(string apiKey, string model, string file, string? language, bool diarize)
    {
        var request = CloudTranscription.Request(
            HttpMethod.Post, Endpoint, CloudTranscription.RequestTimeout, "Authorization", "Bearer " + apiKey);
        var fields = new List<(string, string)> { ("model", model) };
        if (CloudTranscription.LanguageCode(language) is { } code) fields.Add(("language", code));
        if (diarize) fields.Add(("diarize", "true"));
        request.Content = CloudTranscription.MultipartForm(fields, "file", file, []);
        return request;
    }

    /// <summary>
    /// <c>words</c> (<c>text</c>, <c>start</c>, <c>end</c>, integer <c>speaker</c>) joined with spaces into segments;
    /// without words, the <c>text</c> as one segment over <c>duration</c>. Pure, for tests.
    /// </summary>
    public static List<CloudSegment> Parse(ReadOnlySpan<byte> data)
    {
        var response = ProviderHttp.Decode<SttResponse>(data);
        var words = (response.Words ?? []).Select(word => new CloudTranscription.Word(
            word.Start, word.End, word.Text, word.Speaker?.ToString(CultureInfo.InvariantCulture))).ToList();
        if (words.Count > 0) return CloudTranscription.Segments(words, " ", response.Language);
        var text = response.Text?.Trim() ?? "";
        return text.Length == 0 ? [] : [new CloudSegment(0, response.Duration ?? 0, text, Language: response.Language)];
    }

    /// <summary>The realtime adapter's descriptor (<see cref="Realtime"/>).</summary>
    public static readonly RealtimeAdapterType RealtimeType = new(
        ["grok-voice-transcribe-2.0"], new Uri("wss://api.x.ai/v1/stt"), null,
        (apiKey, model, language, diarize) => new Realtime(apiKey, model, language, diarize));

    /// <summary>
    /// Streaming <c>wss://api.x.ai/v1/stt?...</c> (settings in the query, no start message), <c>Authorization: Bearer</c>
    /// handshake header, binary 16 kHz PCM frames. <c>transcript.created</c> starts: audio waits for it. A
    /// <c>transcript.partial</c> with <c>is_final:false</c> is a partial; with <c>is_final:true</c> its words become finals
    /// (integer speakers with <c>diarize</c>), except the words a <c>speech_final</c> repeats from the chunk finals before
    /// it: only words that start after the last final word are new. <c>finalize</c> forces an utterance final;
    /// <c>audio.done</c> ends (answered by <c>transcript.done</c>, then the close). No keep-alive documented.
    /// </summary>
    public sealed class Realtime : IRealtimeAdapter
    {
        private readonly string _apiKey;
        private readonly string? _language;
        private readonly bool _diarize;

        /// <summary>Start of the last word emitted as final; repeated words start at or before it.</summary>
        private double _lastFinalStart = double.NegativeInfinity;

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

        public IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string> { ["Authorization"] = "Bearer " + _apiKey };

        public Uri SocketUrl(byte[]? sessionAnswer)
        {
            var query = new List<(string, string)>
            {
                ("model", Model), ("sample_rate", CloudRealtime.Invariant(SampleRate)), ("encoding", "pcm"), ("interim_results", "true"),
            };
            if (_language is { } language) query.Add(("language", language));
            if (_diarize) query.Add(("diarize", "true"));
            return CloudRealtime.WithQuery(RealtimeType.Endpoint, query);
        }

        public IReadOnlyList<RealtimeMessage> Finalize(double streamSeconds) => [new RealtimeMessage.Text("{\"type\":\"finalize\"}")];

        public IReadOnlyList<RealtimeMessage> CloseMessages() => [new RealtimeMessage.Text("{\"type\":\"audio.done\"}")];

        public IReadOnlyList<RealtimeEvent> Parse(RealtimeMessage message)
        {
            if (CloudRealtime.Decode<LiveTranscript>(message) is not { } live) return [];
            switch (live.Type)
            {
                case "transcript.created":
                    return [new RealtimeEvent.Started()];
                case "transcript.partial" when live.IsFinal != true:
                    var text = live.Text?.Trim() ?? "";
                    if (text.Length == 0) return [];
                    var start = live.Start ?? live.Words?.FirstOrDefault()?.Start;
                    var end = (start is { } from && live.Duration is { } duration ? from + duration : (double?)null)
                              ?? live.Words?.LastOrDefault()?.End;
                    return [new RealtimeEvent.Partial(text, start, end, Speaker(live.Words?.FirstOrDefault()?.Speaker))];
                case "transcript.partial":
                    return Finals(live);
                case "transcript.done":
                    return [.. Finals(live), new RealtimeEvent.Ended()];
                case "error":
                    return [new RealtimeEvent.Failed(CloudRealtime.ErrorKind(live.Type))];
                default:
                    return [];
            }
        }

        /// <summary>
        /// The new words of a final (by start time), a segment per sentence (the diarizer after Stop labels each one when
        /// the words have no speaker); a chunk final without words is its text over <c>start...start+duration</c>.
        /// </summary>
        private List<RealtimeEvent> Finals(LiveTranscript live)
        {
            var fresh = (live.Words ?? []).Where(word => word.Start > _lastFinalStart).ToList();
            if (fresh.Count > 0) _lastFinalStart = fresh.Max(word => word.Start);
            var words = fresh.Select(word => new CloudTranscription.Word(word.Start, word.End, word.Text, Speaker(word.Speaker))).ToList();
            if (words.Count > 0)
                return CloudTranscription.Segments(words, " ", null, longSegment: 0)
                    .Select(segment => (RealtimeEvent)new RealtimeEvent.Final(segment)).ToList();
            var text = live.Text?.Trim() ?? "";
            if (live.Type != "transcript.partial" || live.SpeechFinal == true || live.Words is { Count: > 0 } || text.Length == 0
                || live.Start is not { } start || !(start > _lastFinalStart)) return [];
            _lastFinalStart = start;
            return [new RealtimeEvent.Final(new CloudSegment(start, start + (live.Duration ?? 0), text))];
        }

        private static string? Speaker(int? speaker) => speaker?.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record LiveTranscript(
        string? Type = null, string? Text = null, List<LiveWord>? Words = null, bool? IsFinal = null, bool? SpeechFinal = null,
        double? Start = null, double? Duration = null);

    private sealed record LiveWord(string Text, double Start, double End, int? Speaker = null);

    private sealed record SttResponse(
        string? Text = null, string? Language = null, double? Duration = null, List<SttWord>? Words = null);

    private sealed record SttWord(string Text, double Start, double End, int? Speaker = null);
}
