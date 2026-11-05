using System.Text.Json.Nodes;
using EchoDiar.Core.Settings;

namespace EchoDiar.AI.Cloud;

/// <summary>
/// <c>POST {base}/audio/transcriptions</c> (multipart) for OpenAI, Groq, Mistral and custom OpenAI-compatible endpoints.
/// The form depends on the model: <c>*-diarize</c> gets <c>diarized_json</c> with <c>chunking_strategy=auto</c>, other
/// <c>gpt-*</c> models only return text (<c>json</c>, one segment for the file), Mistral gets
/// <c>timestamp_granularities=segment</c> (and <c>diarize=true</c> when asked), everything else <c>verbose_json</c> with
/// segment timestamps.
/// </summary>
public sealed class OpenAICompatibleTranscriber : ICloudTranscriber
{
    /// <summary>Transcription models per preset id, the first one being the default (docs/research/asr-diarization-models.md F).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> PresetModels = new Dictionary<string, string[]>
    {
        ["openai"] = ["gpt-4o-transcribe-diarize", "gpt-4o-transcribe", "whisper-1"],
        ["groq"] = ["whisper-large-v3-turbo"],
        ["mistral"] = ["voxtral-mini-latest"],
    };

    private readonly string? _apiKey;
    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/>: null for the shared transport; a test seam otherwise (still behind the redirect guard).</summary>
    public OpenAICompatibleTranscriber(ProviderConfig config, string model, string? apiKey, HttpMessageHandler? handler = null)
    {
        Config = config;
        Model = model;
        _apiKey = apiKey;
        _http = ProviderHttp.Client(handler);
    }

    public ProviderConfig Config { get; }
    public string Model { get; }

    /// <summary>Mistral takes up to 3 h per request; OpenAI, Groq (free tier) and unknown endpoints get the 25 MB cap.</summary>
    public int? MaxUploadBytes => IsMistral(Config) ? null : CloudTranscription.CappedUploadBytes;

    /// <summary>False for the <c>json</c> (text-only) models, whose answer is one segment over the file.</summary>
    public bool ReturnsSegmentTimestamps => IsMistral(Config) || !ReturnsTextOnly(Model);

    public Task<IReadOnlyList<CloudSegment>> TranscribeAsync(
        string file, double duration, string? language, bool diarize, CancellationToken cancellationToken = default) =>
        ProviderHttp.SendAsync<IReadOnlyList<CloudSegment>>(
            _http, () => MakeRequest(Config, _apiKey, Model, file, language, diarize), retry: false, ProviderHttp.JsonLimit,
            (body, _) => Parse(body, duration), cancellationToken);

    /// <summary>The audio is streamed from <paramref name="file"/>, whose name is the part's file name. Pure, for tests.</summary>
    public static HttpRequestMessage MakeRequest(
        ProviderConfig config, string? apiKey, string model, string file, string? language, bool diarize)
    {
        var url = ProviderHttp.Append(ProviderHttp.BaseUrl(config), "audio/transcriptions");
        var request = ProviderHttp.Request(url, config, apiKey, CloudTranscription.RequestTimeout);
        var fields = new List<(string, string)> { ("model", model) };
        var code = CloudTranscription.LanguageCode(language);
        if (IsMistral(config))
        {
            // Mistral: timestamp_granularities cannot be combined with language, and timestamps are required.
            fields.Add(("timestamp_granularities", "segment"));
            if (diarize) fields.Add(("diarize", "true"));
        }
        else
        {
            if (code is not null) fields.Add(("language", code));
            if (model.EndsWith("-diarize", StringComparison.Ordinal))
            {
                fields.Add(("response_format", "diarized_json"));
                fields.Add(("chunking_strategy", "auto"));
            }
            else if (ReturnsTextOnly(model))
            {
                fields.Add(("response_format", "json"));
            }
            else
            {
                fields.Add(("response_format", "verbose_json"));
                fields.Add(("timestamp_granularities[]", "segment"));
            }
        }
        request.Method = HttpMethod.Post;
        request.Content = CloudTranscription.MultipartForm(file, fields);
        return request;
    }

    /// <summary>Segments with timestamps; a text-only answer becomes one segment over <paramref name="duration"/> seconds. Pure, for tests.</summary>
    public static List<CloudSegment> Parse(ReadOnlySpan<byte> data, double duration)
    {
        var response = ProviderHttp.Decode<TranscriptionResponse>(data);
        // Whisper's verbose_json names the language ("english"); only codes are kept.
        var language = response.Language is { Length: <= 3 } code ? code : null;
        if (response.Segments is { Count: > 0 } segments)
        {
            return segments
                .Select(segment => (segment, text: segment.Text.Trim()))
                .Where(item => item.text.Length > 0)
                .Select(item => new CloudSegment(
                    item.segment.Start, item.segment.End, item.text, item.segment.Speaker ?? item.segment.SpeakerId, language))
                .ToList();
        }
        var text = response.Text?.Trim() ?? "";
        return text.Length == 0 ? [] : [new CloudSegment(0, duration, text, Language: language)];
    }

    /// <summary>
    /// The realtime adapter's descriptor (<see cref="Realtime"/>). The default's <c>session.update</c> body is documented
    /// (<c>languages</c>, <c>delay</c>); the others get the generic <c>language</c> field (not verified for them).
    /// </summary>
    public static readonly RealtimeAdapterType RealtimeType = new(
        ["gpt-live-transcribe", "gpt-transcribe", "gpt-realtime-whisper"], new Uri("wss://api.openai.com/v1/realtime"), null,
        (apiKey, model, language, diarize) => new Realtime(apiKey, model, language));

    /// <summary>
    /// OpenAI realtime transcription (the <c>openai</c> preset only): <c>wss://api.openai.com/v1/realtime?intent=transcription</c>,
    /// <c>Authorization: Bearer</c> handshake header, a <c>session.update</c> start message (24 kHz <c>audio/pcm</c>, the
    /// model, manual commits: <c>turn_detection</c> null), audio as base64 in <c>input_audio_buffer.append</c> JSON frames
    /// at <b>24 kHz</b>. There are no word or segment times: <c>Finalize</c> commits the audio since the last commit and
    /// remembers that stretch of stream time; <c>committed</c> ties it to an <c>item_id</c>, and that item's
    /// <c>completed</c> transcript is the final over the stretch (turns may complete out of order). Deltas accumulate per
    /// item into partials without times. No speakers. <c>session.created</c> starts (its <c>expires_at</c> caps the
    /// session). No close message: <c>CloseMessages</c> commits the rest (by the audio framed) and
    /// <see cref="RealtimeEvent.Ended"/> follows the last pending transcript; <c>IsDrained</c> says so at once when nothing
    /// is left to commit or wait for.
    /// </summary>
    public sealed class Realtime : IRealtimeAdapter
    {
        /// <summary>OpenAI refuses a commit of less than 100 ms of audio.</summary>
        internal const double MinimumCommit = 0.1;

        private readonly string _apiKey;
        private readonly string? _language;

        /// <summary>Stream seconds framed so far, and where the last commit ended.</summary>
        private double _framedSeconds;
        private double _committedThrough;

        /// <summary>Commits not yet acknowledged, oldest first, then the acknowledged ones by item.</summary>
        private readonly Queue<(double Start, double End)> _pendingSpans = new();
        private readonly Dictionary<string, (double Start, double End)> _spans = [];
        private readonly Dictionary<string, string> _deltas = [];
        private bool _closing;

        public Realtime(string apiKey, string model, string? language)
        {
            _apiKey = apiKey;
            Model = model;
            _language = CloudTranscription.LanguageCode(language);
        }

        public string Model { get; }

        public RealtimeAdapterType Type => RealtimeType;

        public int SampleRate => 24_000;

        public IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string> { ["Authorization"] = "Bearer " + _apiKey };

        public Uri SocketUrl(byte[]? sessionAnswer) => CloudRealtime.WithQuery(RealtimeType.Endpoint, [("intent", "transcription")]);

        /// <summary><c>gpt-live-transcribe</c> takes <c>languages</c> and <c>delay</c>; another model gets the generic <c>language</c>.</summary>
        public IReadOnlyList<RealtimeMessage> StartMessages()
        {
            var transcription = new JsonObject { ["model"] = Model };
            if (Model == RealtimeType.Models[0])
            {
                transcription["delay"] = "low";
                if (_language is { } language) transcription["languages"] = new JsonArray(language);
            }
            else if (_language is { } language)
            {
                transcription["language"] = language;
            }
            var input = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = SampleRate },
                ["transcription"] = transcription,
                ["turn_detection"] = null,
            };
            return [CloudRealtime.Json(new JsonObject
            {
                ["type"] = "session.update",
                ["session"] = new JsonObject { ["type"] = "transcription", ["audio"] = new JsonObject { ["input"] = input } },
            })];
        }

        public RealtimeMessage Frame(byte[] pcm)
        {
            _framedSeconds += (pcm.Length / 2) / (double)SampleRate;
            return new RealtimeMessage.Text(
                "{\"type\":\"input_audio_buffer.append\",\"audio\":\"" + Convert.ToBase64String(pcm) + "\"}");
        }

        /// <summary>Nothing when under 100 ms of audio came since the last commit (an empty commit is an error).</summary>
        public IReadOnlyList<RealtimeMessage> Finalize(double streamSeconds)
        {
            if (!(streamSeconds - _committedThrough >= MinimumCommit)) return [];
            _pendingSpans.Enqueue((_committedThrough, streamSeconds));
            _committedThrough = streamSeconds;
            return [new RealtimeMessage.Text("{\"type\":\"input_audio_buffer.commit\"}")];
        }

        public IReadOnlyList<RealtimeMessage> CloseMessages()
        {
            _closing = true;
            return Finalize(_framedSeconds);
        }

        /// <summary>Closing, and every commit has its transcript (or failed).</summary>
        public bool IsDrained => _closing && _pendingSpans.Count == 0 && _spans.Count == 0;

        public IReadOnlyList<RealtimeEvent> Parse(RealtimeMessage message)
        {
            if (CloudRealtime.Decode<LiveEvent>(message) is not { } live) return [];
            switch (live.Type)
            {
                case "session.created":
                    return [new RealtimeEvent.Started(CloudRealtime.ExpiresAt(live.Session?.ExpiresAt))];
                case "input_audio_buffer.committed":
                {
                    if (live.ItemId is not { } item || _pendingSpans.Count == 0) return [];
                    _spans[item] = _pendingSpans.Dequeue();
                    return [];
                }
                case "conversation.item.input_audio_transcription.delta":
                {
                    if (live.ItemId is not { } item || live.Delta is not { } delta) return [];
                    var text = _deltas.GetValueOrDefault(item, "") + delta;
                    _deltas[item] = text;
                    var trimmed = text.Trim();
                    return trimmed.Length == 0 ? [] : [new RealtimeEvent.Partial(trimmed)];
                }
                case "conversation.item.input_audio_transcription.completed":
                {
                    if (live.ItemId is not { } item) return [];
                    _deltas.Remove(item);
                    var events = new List<RealtimeEvent>();
                    var text = live.Transcript?.Trim() ?? "";
                    if (_spans.Remove(item, out var span) && text.Length > 0)
                        events.Add(new RealtimeEvent.Final(new CloudSegment(span.Start, span.End, text)));
                    if (IsDrained) events.Add(new RealtimeEvent.Ended());
                    return events;
                }
                case "conversation.item.input_audio_transcription.failed":
                    if (live.ItemId is { } failed)
                    {
                        _deltas.Remove(failed);
                        _spans.Remove(failed);
                    }
                    return [new RealtimeEvent.Failed(CloudRealtime.ErrorKind(live.Error?.Code ?? live.Type))];
                case "error":
                    return [new RealtimeEvent.Failed(CloudRealtime.ErrorKind(live.Error?.Code ?? live.Error?.Type ?? live.Type))];
                default:
                    return [];
            }
        }
    }

    private sealed record LiveEvent(
        string? Type = null, LiveSession? Session = null, string? ItemId = null, string? Delta = null, string? Transcript = null,
        LiveFailure? Error = null);

    private sealed record LiveSession(double? ExpiresAt = null);

    private sealed record LiveFailure(string? Type = null, string? Code = null);

    private static bool IsMistral(ProviderConfig config) => config.Preset == "mistral";

    /// <summary>Off Mistral: a <c>gpt-*</c> model without <c>-diarize</c> is sent <c>response_format=json</c>, text with no timestamps.</summary>
    private static bool ReturnsTextOnly(string model) =>
        !model.EndsWith("-diarize", StringComparison.Ordinal) && model.StartsWith("gpt-", StringComparison.Ordinal);

    private sealed record TranscriptionResponse(string? Text = null, string? Language = null, List<TranscriptionResponse.Segment>? Segments = null)
    {
        /// <param name="Speaker">OpenAI <c>diarized_json</c>.</param>
        /// <param name="SpeakerId">Mistral.</param>
        internal sealed record Segment(double Start, double End, string Text, string? Speaker = null, string? SpeakerId = null);
    }
}
