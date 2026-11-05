using System.Text.Json.Nodes;

namespace EchoDiar.AI.Cloud;

/// <summary>
/// Soniox async: <c>POST /v1/files</c> (multipart) -> <c>POST /v1/transcriptions</c> -> <c>GET /v1/transcriptions/{id}</c>
/// every 3 s while the job is queued or processing (at most <see cref="MaxPolls"/> times) -> <c>GET
/// /v1/transcriptions/{id}/transcript</c>, then the transcription and the file are deleted. <c>Authorization: Bearer
/// &lt;key&gt;</c>. Times are milliseconds on sub-word tokens; speakers are <c>1</c>, <c>2</c>, ...
/// </summary>
public sealed class SonioxTranscriber : ICloudTranscriber
{
    public static readonly Uri BaseUrl = new("https://api.soniox.com");
    /// <summary>Required: Soniox has no server default.</summary>
    public const string DefaultModel = "stt-async-v6";

    /// <summary>
    /// Soniox refuses files over 300 min. The budget is macOS's: 283 min at its 32 kbps export (4,000 B/s), about 6 % under
    /// the cap, so longer recordings are split instead of refused. At Windows' 96 kbps export it is about 94 min a part,
    /// further under the cap.
    /// </summary>
    internal const int UploadBytes = 67_920_000;

    private readonly string _apiKey;
    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/>: null for the shared transport; a test seam otherwise (still behind the redirect guard).</summary>
    public SonioxTranscriber(string apiKey, string model = DefaultModel, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        Model = model;
        _http = ProviderHttp.Client(handler);
    }

    public string Model { get; }

    public int? MaxUploadBytes => UploadBytes;

    /// <summary>The pause between two polls.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Polls before giving up (4 h at 3 s), so a job a vendor never finishes cannot poll forever.</summary>
    internal int MaxPolls { get; init; } = 4_800;

    /// <summary>The last best-effort delete (a test seam: it runs in the background, also after a cancel).</summary>
    internal Task Cleanup { get; private set; } = Task.CompletedTask;

    public async Task<IReadOnlyList<CloudSegment>> TranscribeAsync(
        string file, double duration, string? language, bool diarize, CancellationToken cancellationToken = default)
    {
        // Sent once: a retry after a lost answer would leave a second stored file.
        var fileId = await ProviderHttp.SendAsync(
            _http, () => MakeUploadRequest(_apiKey, file), retry: false, ProviderHttp.SmallJsonLimit,
            (body, _) => ProviderHttp.Decode<Created>(body).Id, cancellationToken);
        string? transcriptionId = null;
        try
        {
            // Billed: a retry after a lost answer could create a second job.
            var job = await ProviderHttp.SendAsync(
                _http, () => MakeTranscriptionRequest(_apiKey, Model, fileId, language, diarize), retry: false,
                ProviderHttp.SmallJsonLimit, (body, _) => ParseStatus(body), cancellationToken);
            transcriptionId = job.Id;
            // Polls only while this job runs; cancelling ends the wait and the loop.
            for (var polls = 0; !job.Completed; polls++)
            {
                if (polls == MaxPolls) throw new TimeoutException("Soniox did not finish the transcription in time.");
                await Task.Delay(PollInterval, cancellationToken);
                var id = job.Id;
                job = await ProviderHttp.SendAsync(
                    _http, () => MakePollRequest(_apiKey, id), retry: true, ProviderHttp.SmallJsonLimit,
                    (body, _) => ParseStatus(body), cancellationToken);
            }
            var transcriptId = job.Id;
            return await ProviderHttp.SendAsync<IReadOnlyList<CloudSegment>>(
                _http, () => MakeTranscriptRequest(_apiKey, transcriptId), retry: true, ProviderHttp.JsonLimit,
                (body, _) => Parse(body), cancellationToken);
        }
        finally
        {
            // Deleted whatever the answer, also on a cancel (Soniox would keep them for 30 days).
            Cleanup = DeleteAsync(transcriptionId, fileId);
        }
    }

    /// <summary>
    /// Best-effort <c>DELETE</c> of the transcription, then of the file, in its own task and without the caller's token
    /// so it also runs when the transcription was cancelled; a failure is ignored (Soniox deletes leftovers after 30 days).
    /// </summary>
    private Task DeleteAsync(string? transcriptionId, string fileId)
    {
        var requests = new List<Func<HttpRequestMessage>>();
        if (transcriptionId is { } id) requests.Add(() => MakeDeleteTranscriptionRequest(_apiKey, id));
        requests.Add(() => MakeDeleteFileRequest(_apiKey, fileId));
        return Task.Run(async () =>
        {
            foreach (var request in requests)
            {
                try
                {
                    await ProviderHttp.SendAsync(
                        _http, request, retry: false, ProviderHttp.SmallJsonLimit, (_, _) => true, CancellationToken.None);
                }
                catch (Exception error) when (error is LLMException or HttpRequestException or HttpIOException
                                                  or TimeoutException or OperationCanceledException or IOException)
                {
                    // Best effort, as Swift's try?.
                }
            }
        });
    }

    /// <summary>The audio is streamed from <paramref name="file"/> as the lone <c>file</c> field. Pure, for tests.</summary>
    public static HttpRequestMessage MakeUploadRequest(string apiKey, string file)
    {
        var request = Request(HttpMethod.Post, "v1/files", apiKey);
        request.Content = CloudTranscription.MultipartForm(file, []);
        return request;
    }

    /// <summary>
    /// <c>enable_speaker_diarization</c> follows <paramref name="diarize"/>; <c>language_hints</c> when a language is set
    /// (else Soniox detects it). Pure, for tests.
    /// </summary>
    public static HttpRequestMessage MakeTranscriptionRequest(
        string apiKey, string model, string fileId, string? language, bool diarize)
    {
        var request = Request(HttpMethod.Post, "v1/transcriptions", apiKey);
        var code = CloudTranscription.LanguageCode(language);
        ProviderHttp.SetJsonBody(request, new TranscriptionBody(model, fileId, code is null ? null : [code], diarize));
        return request;
    }

    /// <summary>Settings' Test: <c>GET /v1/files?limit=1</c> lists at most one stored file (free, no audio). Pure, for tests.</summary>
    public static HttpRequestMessage MakeKeyCheckRequest(string apiKey)
    {
        var request = Request(HttpMethod.Get, "v1/files?limit=1", apiKey);
        request.Options.Set(ProviderHttp.TimeoutOption, ProviderHttp.ListTimeout);
        return request;
    }

    /// <summary><c>GET /v1/transcriptions/{id}</c>; <paramref name="id"/> must be a plain id (see <see cref="CloudTranscription.CheckedId"/>).</summary>
    public static HttpRequestMessage MakePollRequest(string apiKey, string id) =>
        Request(HttpMethod.Get, "v1/transcriptions/" + CloudTranscription.CheckedId(id, "Soniox"), apiKey);

    /// <summary><c>GET /v1/transcriptions/{id}/transcript</c>; <paramref name="id"/> must be a plain id.</summary>
    public static HttpRequestMessage MakeTranscriptRequest(string apiKey, string id) =>
        Request(HttpMethod.Get, "v1/transcriptions/" + CloudTranscription.CheckedId(id, "Soniox") + "/transcript", apiKey);

    /// <summary><c>DELETE /v1/transcriptions/{id}</c>; <paramref name="id"/> must be a plain id.</summary>
    public static HttpRequestMessage MakeDeleteTranscriptionRequest(string apiKey, string id) =>
        DeleteRequest("v1/transcriptions/" + CloudTranscription.CheckedId(id, "Soniox"), apiKey);

    /// <summary><c>DELETE /v1/files/{id}</c>; <paramref name="id"/> must be a plain id.</summary>
    public static HttpRequestMessage MakeDeleteFileRequest(string apiKey, string id) =>
        DeleteRequest("v1/files/" + CloudTranscription.CheckedId(id, "Soniox"), apiKey);

    /// <summary>
    /// The job id, and whether it is <c>completed</c> (false while queued or processing); an <c>error</c> job throws.
    /// Pure, for tests.
    /// </summary>
    public static (string Id, bool Completed) ParseStatus(ReadOnlySpan<byte> data)
    {
        var response = ProviderHttp.Decode<TranscriptionStatus>(data);
        return response.Status switch
        {
            "completed" => (response.Id, true),
            "error" => throw LLMException.Provider(response.ErrorMessage ?? "Soniox could not transcribe the audio."),
            _ => (response.Id, false),
        };
    }

    /// <summary>
    /// <c>tokens</c> to segments. Tokens are sub-words that carry their own leading space, so they are joined with "" and
    /// grouped like other timed words: a new segment on a speaker change, a pause over 1 s, or a sentence end after 15 s.
    /// A word's tokens have no pause and one speaker, so a word is not cut in practice, and languages written without
    /// spaces still split. Times are divided by 1000, audio events dropped; the language is the first token's that has
    /// one. Pure, for tests.
    /// </summary>
    public static List<CloudSegment> Parse(ReadOnlySpan<byte> data)
    {
        var response = ProviderHttp.Decode<TranscriptResponse>(data);
        var tokens = (response.Tokens ?? []).Where(token => token.IsAudioEvent != true && token.Text.Length > 0).ToList();
        var words = tokens.Select(token =>
            new CloudTranscription.Word(token.StartMs / 1000, token.EndMs / 1000, token.Text, token.Speaker));
        return CloudTranscription.Segments(words, "", tokens.Select(token => token.Language).FirstOrDefault(code => code is not null));
    }

    private static HttpRequestMessage Request(HttpMethod method, string pathAndQuery, string apiKey) =>
        CloudTranscription.Request(
            method, new Uri(BaseUrl, pathAndQuery), CloudTranscription.RequestTimeout, "Authorization", "Bearer " + apiKey);

    private static HttpRequestMessage DeleteRequest(string path, string apiKey)
    {
        var request = Request(HttpMethod.Delete, path, apiKey);
        request.Options.Set(ProviderHttp.TimeoutOption, ProviderHttp.ListTimeout);
        return request;
    }

    /// <summary>The realtime adapter's descriptor (<see cref="Realtime"/>).</summary>
    public static readonly RealtimeAdapterType RealtimeType = new(
        ["stt-rt-v6"], new Uri("wss://stt-rt.soniox.com/transcribe-websocket"), null,
        (apiKey, model, language, diarize) => new Realtime(apiKey, model, language, diarize));

    /// <summary>
    /// Realtime <c>wss://stt-rt.soniox.com/transcribe-websocket</c>, <c>Authorization: Bearer</c> handshake header (the
    /// start message's <c>api_key</c> is deprecated), a JSON start message (model, raw pcm_s16le 16 kHz mono, language
    /// hints, speaker diarization, language identification, endpoint detection), binary audio frames. Each answer
    /// re-sends the non-final tokens and adds new final ones once; tokens are sub-words with their own spacing, times in
    /// ms. Final tokens are held until a segment is complete (a later segment began, or an <c>&lt;end&gt;</c> /
    /// <c>&lt;fin&gt;</c> marker or the end of the stream), so a word is never split across finals; the partial is the
    /// held text plus the non-final tokens. Speakers <c>1</c>, <c>2</c>, ... <c>finalize</c> flushes, an empty text frame
    /// ends (answered by <c>finished</c>, then the close), <c>keepalive</c> every 10 s while no audio flows (Soniox closes
    /// after 20 s). No ready message: the session accepts audio once connected.
    /// </summary>
    public sealed class Realtime : IRealtimeAdapter
    {
        /// <summary>End-of-utterance (<c>&lt;end&gt;</c>, endpoint detection) and manual-finalize (<c>&lt;fin&gt;</c>) markers.</summary>
        private static readonly HashSet<string> Markers = ["<end>", "<fin>"];

        private readonly string _apiKey;
        private readonly string? _language;
        private readonly bool _diarize;

        /// <summary>Final tokens not emitted yet, and the first language among them.</summary>
        private List<CloudTranscription.Word> _held = [];
        private string? _heldLanguage;

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

        public RealtimeKeepAlive? KeepAlive => new(new RealtimeMessage.Text("{\"type\":\"keepalive\"}"), TimeSpan.FromSeconds(10));

        public Uri SocketUrl(byte[]? sessionAnswer) => RealtimeType.Endpoint;

        public IReadOnlyList<RealtimeMessage> StartMessages()
        {
            var config = new JsonObject
            {
                ["model"] = Model, ["audio_format"] = "pcm_s16le", ["sample_rate"] = SampleRate, ["num_channels"] = 1,
                ["enable_speaker_diarization"] = _diarize, ["enable_language_identification"] = true,
                ["enable_endpoint_detection"] = true,
            };
            if (_language is { } language) config["language_hints"] = new JsonArray(language);
            return [CloudRealtime.Json(config)];
        }

        public IReadOnlyList<RealtimeMessage> Finalize(double streamSeconds) => [new RealtimeMessage.Text("{\"type\":\"finalize\"}")];

        public IReadOnlyList<RealtimeMessage> CloseMessages() => [new RealtimeMessage.Text("")];

        public IReadOnlyList<RealtimeEvent> Parse(RealtimeMessage message)
        {
            if (CloudRealtime.Decode<LiveResponse>(message) is not { } live) return [];
            if (live.ErrorCode is not null || live.ErrorType is not null)
            {
                return [new RealtimeEvent.Failed(CloudRealtime.ErrorKind(
                    live.ErrorType ?? live.ErrorCode?.ToString(System.Globalization.CultureInfo.InvariantCulture)))];
            }
            var flush = live.Finished == true;
            var pending = new List<LiveToken>();
            foreach (var token in live.Tokens ?? [])
            {
                if (Markers.Contains(token.Text))
                {
                    flush = flush || token.IsFinal == true;
                }
                else if (token.IsFinal == true)
                {
                    var start = token.StartMs / 1000 ?? (_held.Count > 0 ? _held[^1].End : 0);
                    _held.Add(new(start, token.EndMs / 1000 ?? start, token.Text, token.Speaker));
                    _heldLanguage ??= token.Language;
                }
                else
                {
                    pending.Add(token);
                }
            }
            var events = EmitHeld(flush).Select(segment => (RealtimeEvent)new RealtimeEvent.Final(segment)).ToList();
            var text = string.Concat(_held.Select(word => word.Text).Concat(pending.Select(token => token.Text))).Trim();
            if (text.Length > 0)
            {
                var start = _held.Count > 0 ? _held[0].Start : pending.FirstOrDefault()?.StartMs / 1000;
                var end = pending.LastOrDefault()?.EndMs / 1000 ?? (_held.Count > 0 ? _held[^1].End : null);
                var speaker = (_held.Count > 0 ? _held[0].Speaker : null) ?? pending.FirstOrDefault()?.Speaker;
                events.Add(new RealtimeEvent.Partial(text, start, end, speaker));
            }
            if (live.Finished == true) events.Add(new RealtimeEvent.Ended());
            return events;
        }

        /// <summary>The held tokens' complete segments (all of them with <paramref name="all"/>); the last one stays held otherwise.</summary>
        private List<CloudSegment> EmitHeld(bool all)
        {
            var segments = CloudTranscription.Segments(_held, "", _heldLanguage);
            if (all || segments.Count == 0)
            {
                _held = [];
                _heldLanguage = null;
                return segments;
            }
            var last = segments[^1];
            segments.RemoveAt(segments.Count - 1);
            _held = _held.Where(word => word.Start >= last.Start).ToList();
            return segments;
        }
    }

    private sealed record LiveResponse(
        List<LiveToken>? Tokens = null, bool? Finished = null, int? ErrorCode = null, string? ErrorType = null);

    private sealed record LiveToken(
        string Text, double? StartMs = null, double? EndMs = null, bool? IsFinal = null, string? Speaker = null,
        string? Language = null);

    private sealed record TranscriptionBody(string Model, string FileId, List<string>? LanguageHints, bool EnableSpeakerDiarization);

    private sealed record Created(string Id);

    private sealed record TranscriptionStatus(string Id, string Status, string? ErrorMessage = null);

    private sealed record TranscriptResponse(List<Token>? Tokens = null);

    private sealed record Token(
        string Text, double StartMs, double EndMs, string? Speaker = null, string? Language = null, bool? IsAudioEvent = null);
}
