using System.Text.Json;
using System.Text.Json.Nodes;

namespace EchoDiar.AI.Cloud;

/// <summary>
/// Speechmatics batch: <c>POST /v2/jobs</c> (multipart <c>config</c> + <c>data_file</c>) -> <c>GET /v2/jobs/{id}</c>
/// every 3 s while the job runs (at most <see cref="MaxPolls"/> times) -> <c>GET /v2/jobs/{id}/transcript?format=json-v2</c>.
/// <c>Authorization: Bearer &lt;key&gt;</c>. One fixed region (<c>eu1</c>): a job can only be read from the region that
/// created it. Speakers are <c>S1</c>, <c>S2</c>, ...
/// </summary>
public sealed class SpeechmaticsTranscriber : ICloudTranscriber
{
    public static readonly Uri BaseUrl = new("https://eu1.asr.api.speechmatics.com");
    public const string DefaultModel = "enhanced";

    private readonly string _apiKey;
    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/>: null for the shared transport; a test seam otherwise (still behind the redirect guard).</summary>
    public SpeechmaticsTranscriber(string apiKey, string model = DefaultModel, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        Model = model;
        _http = ProviderHttp.Client(handler);
    }

    public string Model { get; }

    /// <summary>The pause between two polls.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Polls before giving up (4 h at 3 s), so a job a vendor never finishes cannot poll forever.</summary>
    internal int MaxPolls { get; init; } = 4_800;

    public async Task<IReadOnlyList<CloudSegment>> TranscribeAsync(
        string file, double duration, string? language, bool diarize, CancellationToken cancellationToken = default)
    {
        // Billed: a retry after a lost answer could create a second job.
        var id = await ProviderHttp.SendAsync(
            _http, () => MakeJobRequest(_apiKey, Model, file, language, diarize), retry: false, ProviderHttp.SmallJsonLimit,
            (body, _) => ProviderHttp.Decode<CreatedJob>(body).Id, cancellationToken);
        // Polls only while this job runs; cancelling ends the wait and the loop.
        var done = false;
        for (var polls = 0; !done; polls++)
        {
            if (polls == MaxPolls) throw new TimeoutException("Speechmatics did not finish the transcription in time.");
            await Task.Delay(PollInterval, cancellationToken);
            done = await ProviderHttp.SendAsync(
                _http, () => MakePollRequest(_apiKey, id), retry: true, ProviderHttp.SmallJsonLimit,
                (body, _) => ParseJobStatus(body), cancellationToken);
        }
        return await ProviderHttp.SendAsync<IReadOnlyList<CloudSegment>>(
            _http, () => MakeTranscriptRequest(_apiKey, id), retry: true, ProviderHttp.JsonLimit,
            (body, _) => Parse(body), cancellationToken);
    }

    /// <summary>
    /// The <c>config</c> field comes before the audio: <c>transcription_config</c> with <c>model</c>, the language
    /// (<c>auto</c> to detect; <c>multi</c> plus <c>language_hints</c> for the multilingual <c>melia</c> models, which
    /// refuse <c>auto</c>) and <c>diarization: speaker</c> when asked. Pure, for tests.
    /// </summary>
    public static HttpRequestMessage MakeJobRequest(string apiKey, string model, string file, string? language, bool diarize)
    {
        var request = Request(HttpMethod.Post, "v2/jobs", apiKey);
        var code = CloudTranscription.LanguageCode(language);
        var multilingual = model.StartsWith("melia", StringComparison.OrdinalIgnoreCase);
        var config = new JobConfig("transcription", new(
            multilingual ? "multi" : code ?? "auto", model, diarize ? "speaker" : null,
            multilingual && code is not null ? [code] : null));
        request.Content = CloudTranscription.MultipartForm(
            [("config", JsonSerializer.Serialize(config, ProviderHttp.Json))], "data_file", file, []);
        return request;
    }

    /// <summary>Settings' Test: <c>GET /v2/jobs?limit=1</c> lists at most one past job (free, no audio). Pure, for tests.</summary>
    public static HttpRequestMessage MakeKeyCheckRequest(string apiKey)
    {
        var request = Request(HttpMethod.Get, "v2/jobs?limit=1", apiKey);
        request.Options.Set(ProviderHttp.TimeoutOption, ProviderHttp.ListTimeout);
        return request;
    }

    /// <summary><c>GET /v2/jobs/{id}</c>; <paramref name="id"/> must be a plain id (see <see cref="CloudTranscription.CheckedId"/>).</summary>
    public static HttpRequestMessage MakePollRequest(string apiKey, string id) =>
        Request(HttpMethod.Get, "v2/jobs/" + CloudTranscription.CheckedId(id, "Speechmatics"), apiKey);

    /// <summary><c>GET /v2/jobs/{id}/transcript?format=json-v2</c>; <paramref name="id"/> must be a plain id.</summary>
    public static HttpRequestMessage MakeTranscriptRequest(string apiKey, string id) =>
        Request(HttpMethod.Get, "v2/jobs/" + CloudTranscription.CheckedId(id, "Speechmatics") + "/transcript?format=json-v2", apiKey);

    /// <summary>
    /// True once the job is <c>done</c>, false while it runs; a <c>rejected</c>, <c>deleted</c> or <c>expired</c> job
    /// throws. Pure, for tests.
    /// </summary>
    public static bool ParseJobStatus(ReadOnlySpan<byte> data)
    {
        var job = ProviderHttp.Decode<JobDetails>(data).Job;
        return job.Status switch
        {
            "done" => true,
            "rejected" or "deleted" or "expired" => throw LLMException.Provider(
                job.Errors?.FirstOrDefault()?.Message ?? $"Speechmatics could not transcribe the audio ({job.Status})."),
            _ => false,
        };
    }

    /// <summary>
    /// json-v2 <c>results</c> to segments. Each word carries its leading space; a <c>punctuation</c> item attaches per
    /// <c>attaches_to</c>: <c>previous</c> (the default) and <c>both</c> to the word before (<c>both</c> also glues the
    /// next word to it), <c>next</c> to the word after, <c>none</c> stands alone. Speaker <c>UU</c> (unknown) becomes no
    /// label. Pure, for tests.
    /// </summary>
    public static List<CloudSegment> Parse(ReadOnlySpan<byte> data)
    {
        var reader = new WordReader();
        reader.Add(ProviderHttp.Decode<TranscriptResponse>(data).Results ?? []);
        return CloudTranscription.Segments(reader.Words, "", reader.Language);
    }

    /// <summary>
    /// json-v2 <c>results</c> read into timed words, item by item; the realtime adapter keeps one across
    /// <c>AddTranscript</c> messages, so a punctuation item attaches to a word of an earlier message.
    /// </summary>
    private sealed class WordReader
    {
        private bool _glued;
        private string _prefix = "";

        public List<CloudTranscription.Word> Words { get; set; } = [];

        public string? Language { get; private set; }

        public void Add(List<TranscriptItem> items)
        {
            foreach (var item in items)
            {
                if (item.Alternatives?.FirstOrDefault() is not { Content.Length: > 0 } alternative) continue;
                Language ??= alternative.Language;
                var attachment = item.Type == "punctuation" ? item.AttachesTo ?? "previous" : null;
                if (attachment is ("previous" or "both") && Words.Count > 0)
                {
                    var last = Words[^1];
                    Words[^1] = last with { Text = last.Text + _prefix + alternative.Content, End = Math.Max(last.End, item.EndTime) };
                    _prefix = "";
                    _glued = attachment == "both";
                    continue;
                }
                if (attachment == "next")
                {
                    _prefix += (_prefix.Length == 0 && !_glued ? " " : "") + alternative.Content;
                    _glued = true;
                    continue;
                }
                var speaker = alternative.Speaker == "UU" ? null : alternative.Speaker;
                var space = _glued || _prefix.Length > 0 ? "" : " ";
                Words.Add(new(item.StartTime, item.EndTime, space + _prefix + alternative.Content, speaker));
                _prefix = "";
                _glued = false;
            }
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string pathAndQuery, string apiKey) =>
        CloudTranscription.Request(
            method, new Uri(BaseUrl, pathAndQuery), CloudTranscription.RequestTimeout, "Authorization", "Bearer " + apiKey);

    /// <summary>The realtime adapter's descriptor (<see cref="Realtime"/>).</summary>
    public static readonly RealtimeAdapterType RealtimeType = new(
        ["enhanced", "standard", "melia-1", "oak-1"], new Uri("wss://eu.rt.speechmatics.com/v2"), null,
        (apiKey, model, language, diarize) => new Realtime(apiKey, model, language, diarize));

    /// <summary>
    /// Realtime <c>wss://eu.rt.speechmatics.com/v2</c> (one fixed region), <c>Authorization: Bearer</c> handshake header, a
    /// <c>StartRecognition</c> start message (raw pcm_s16le 16 kHz, the model, partials, <c>max_delay</c> 2 s, speaker
    /// diarization with <c>standard</c>/<c>enhanced</c>), binary audio frames (<c>AddAudio</c>), each counted for
    /// <c>EndOfStream</c>'s <c>last_seq_no</c>. <c>RecognitionStarted</c> starts; <c>AddPartialTranscript</c> is a partial
    /// (after the held words), <c>AddTranscript</c> final words (json-v2 results, read like the batch transcript:
    /// punctuation attached, speakers <c>S1</c>, <c>S2</c>, <c>UU</c> = none), held until their sentence ends, times in
    /// seconds from the stream start. <c>ForceEndOfUtterance</c> flushes (answered by <c>EndOfUtterance</c>);
    /// <c>EndOfStream</c> ends (answered by <c>EndOfTranscript</c>). Realtime has no automatic language: Auto is refused
    /// (except the multilingual <c>melia</c> models, sent <c>multi</c>). Keep-alive is WebSocket ping/pong (3 min idle
    /// close), not a message.
    /// </summary>
    public sealed class Realtime : IRealtimeAdapter
    {
        private readonly string _apiKey;
        private readonly string? _language;
        private readonly bool _diarize;

        /// <summary><c>AddAudio</c> frames sent: Speechmatics numbers them from 1.</summary>
        private int _framesSent;

        /// <summary>
        /// Final words not emitted yet: an <c>AddTranscript</c> carries a few words, so they are held until their sentence
        /// ends (or the speaker changes), and flushed by <c>EndOfUtterance</c> and <c>EndOfTranscript</c>.
        /// </summary>
        private readonly WordReader _held = new();

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

        /// <summary>Only <c>standard</c> and <c>enhanced</c> label speakers in realtime.</summary>
        public bool StreamsSpeakers => _diarize && Model is "standard" or "enhanced";

        public IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string> { ["Authorization"] = "Bearer " + _apiKey };

        public Uri SocketUrl(byte[]? sessionAnswer) => RealtimeType.Endpoint;

        public IReadOnlyList<RealtimeMessage> StartMessages()
        {
            var config = new JsonObject { ["model"] = Model, ["enable_partials"] = true, ["max_delay"] = 2 };
            if (Model.StartsWith("melia", StringComparison.OrdinalIgnoreCase))
            {
                config["language"] = "multi";
                if (_language is { } hint) config["language_hints"] = new JsonArray(hint);
            }
            else if (_language is { } language)
            {
                config["language"] = language;
            }
            else
            {
                throw LLMException.Provider("Speechmatics live transcription needs a language: choose one instead of Auto.");
            }
            if (StreamsSpeakers) config["diarization"] = "speaker";
            return [CloudRealtime.Json(new JsonObject
            {
                ["message"] = "StartRecognition",
                ["audio_format"] = new JsonObject { ["type"] = "raw", ["encoding"] = "pcm_s16le", ["sample_rate"] = SampleRate },
                ["transcription_config"] = config,
            })];
        }

        public RealtimeMessage Frame(byte[] pcm)
        {
            _framesSent++;
            return new RealtimeMessage.Binary(pcm);
        }

        public IReadOnlyList<RealtimeMessage> Finalize(double streamSeconds) =>
            [new RealtimeMessage.Text("{\"message\":\"ForceEndOfUtterance\"}")];

        public IReadOnlyList<RealtimeMessage> CloseMessages() =>
            [new RealtimeMessage.Text("{\"message\":\"EndOfStream\",\"last_seq_no\":" + CloudRealtime.Invariant(_framesSent) + "}")];

        public IReadOnlyList<RealtimeEvent> Parse(RealtimeMessage message)
        {
            if (CloudRealtime.Decode<LiveMessage>(message) is not { } live) return [];
            switch (live.Message)
            {
                case "RecognitionStarted":
                    return [new RealtimeEvent.Started()];
                case "AddPartialTranscript":
                    var text = live.Metadata?.Transcript?.Trim() ?? "";
                    var speaker = (live.Results ?? [])
                        .Select(result => result.Alternatives?.FirstOrDefault()?.Speaker)
                        .FirstOrDefault(label => label is not null && label != "UU");
                    return Partial(text, live.Metadata?.StartTime, live.Metadata?.EndTime, speaker);
                case "AddTranscript":
                    _held.Add(ProviderHttp.Decode<TranscriptResponse>(CloudRealtime.Payload(message)).Results ?? []);
                    return [.. EmitHeld(all: false), .. Partial("", null, null, null)];
                case "EndOfUtterance":
                    return EmitHeld(all: true);
                case "EndOfTranscript":
                    return [.. EmitHeld(all: true), new RealtimeEvent.Ended()];
                case "Error":
                    return [new RealtimeEvent.Failed(CloudRealtime.ErrorKind(live.Type ?? live.Message))];
                default:
                    return [];
            }
        }

        /// <summary>The held words' complete segments as finals (all of them with <paramref name="all"/>); the last one stays held otherwise.</summary>
        private List<RealtimeEvent> EmitHeld(bool all)
        {
            var segments = CloudTranscription.Segments(_held.Words, "", _held.Language, longSegment: 0);
            if (all || segments.Count == 0)
            {
                _held.Words = [];
            }
            else
            {
                var last = segments[^1];
                segments.RemoveAt(segments.Count - 1);
                _held.Words = _held.Words.Where(word => word.Start >= last.Start).ToList();
            }
            return segments.Select(segment => (RealtimeEvent)new RealtimeEvent.Final(segment)).ToList();
        }

        /// <summary>The held words followed by the partial <paramref name="text"/>; nothing when both are empty.</summary>
        private List<RealtimeEvent> Partial(string text, double? start, double? end, string? speaker)
        {
            var heldText = string.Concat(_held.Words.Select(word => word.Text)).Trim();
            var full = string.Join(" ", new[] { heldText, text }.Where(part => part.Length > 0));
            if (full.Length == 0) return [];
            var words = _held.Words;
            return [new RealtimeEvent.Partial(
                full, words.Count > 0 ? words[0].Start : start, end ?? (words.Count > 0 ? words[^1].End : null),
                words.Count > 0 ? words[0].Speaker ?? speaker : speaker)];
        }
    }

    /// <summary><c>EndOfUtterance</c> (the answer to <c>ForceEndOfUtterance</c>) has metadata without a transcript.</summary>
    private sealed record LiveMessage(
        string? Message = null, string? Type = null, LiveMetadata? Metadata = null, List<LiveResult>? Results = null);

    private sealed record LiveMetadata(string? Transcript = null, double? StartTime = null, double? EndTime = null);

    private sealed record LiveResult(List<LiveAlternative>? Alternatives = null);

    private sealed record LiveAlternative(string? Speaker = null);

    private sealed record JobConfig(string Type, TranscriptionConfig TranscriptionConfig);

    private sealed record TranscriptionConfig(string Language, string Model, string? Diarization, List<string>? LanguageHints);

    private sealed record CreatedJob(string Id);

    private sealed record JobDetails(Job Job);

    private sealed record Job(string Status, List<JobFailure>? Errors = null);

    private sealed record JobFailure(string? Message = null);

    private sealed record TranscriptResponse(List<TranscriptItem>? Results = null);

    private sealed record TranscriptItem(
        double StartTime, double EndTime, string? Type = null, string? AttachesTo = null, List<Alternative>? Alternatives = null);

    private sealed record Alternative(string Content, string? Language = null, string? Speaker = null);
}
