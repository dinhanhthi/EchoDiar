using System.Globalization;

namespace EchoDiar.AI.Cloud;

/// <summary>
/// Gladia pre-recorded v2: <c>POST /v2/upload</c> (multipart <c>audio</c>) -> <c>POST /v2/pre-recorded</c> -> <c>GET
/// /v2/pre-recorded/{id}</c> every 3 s while the job is queued or processing (at most <see cref="MaxPolls"/> times).
/// <c>x-gladia-key: &lt;key&gt;</c>. Utterances become segments; speakers are integers from 0.
/// </summary>
public sealed class GladiaTranscriber : ICloudTranscriber
{
    public static readonly Uri BaseUrl = new("https://api.gladia.io");
    public const string DefaultModel = "solaria-1";

    /// <summary>
    /// Gladia refuses requests over 135 min. The budget is macOS's: 125 min at its 32 kbps export (4,000 B/s), about 7 %
    /// under the cap, so longer recordings are split. At Windows' 96 kbps export it is about 42 min a part, further under
    /// the cap.
    /// </summary>
    internal const int UploadBytes = 30_000_000;

    private readonly string _apiKey;
    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/>: null for the shared transport; a test seam otherwise (still behind the redirect guard).</summary>
    public GladiaTranscriber(string apiKey, string model = DefaultModel, HttpMessageHandler? handler = null)
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

    public async Task<IReadOnlyList<CloudSegment>> TranscribeAsync(
        string file, double duration, string? language, bool diarize, CancellationToken cancellationToken = default)
    {
        var audioUrl = await ProviderHttp.SendAsync(
            _http, () => MakeUploadRequest(_apiKey, file), retry: true, ProviderHttp.SmallJsonLimit,
            (body, _) => ProviderHttp.Decode<UploadResponse>(body).AudioUrl, cancellationToken);
        // Billed: every accepted job POST is a new job. The poll URL is built from the id, never from the answer's
        // `result_url`.
        var id = await ProviderHttp.SendAsync(
            _http, () => MakeJobRequest(_apiKey, Model, audioUrl, language, diarize), retry: false, ProviderHttp.SmallJsonLimit,
            (body, _) => ProviderHttp.Decode<CreatedJob>(body).Id, cancellationToken);
        // Polls only while this job runs; cancelling ends the wait and the loop.
        List<CloudSegment>? segments = null;
        for (var polls = 0; segments is null; polls++)
        {
            if (polls == MaxPolls) throw new TimeoutException("Gladia did not finish the transcription in time.");
            await Task.Delay(PollInterval, cancellationToken);
            segments = await ProviderHttp.SendAsync(
                _http, () => MakePollRequest(_apiKey, id), retry: true, ProviderHttp.JsonLimit,
                (body, _) => ParseJob(body), cancellationToken);
        }
        return segments;
    }

    /// <summary>The audio is streamed from <paramref name="file"/> as the lone <c>audio</c> field. Pure, for tests.</summary>
    public static HttpRequestMessage MakeUploadRequest(string apiKey, string file)
    {
        var request = Request(HttpMethod.Post, "v2/upload", apiKey);
        request.Content = CloudTranscription.MultipartForm([], "audio", file, []);
        return request;
    }

    /// <summary>
    /// <c>diarization</c> follows <paramref name="diarize"/>; <c>language_config.languages</c> is the set language, or
    /// empty to detect it. Pure, for tests.
    /// </summary>
    public static HttpRequestMessage MakeJobRequest(string apiKey, string model, string audioUrl, string? language, bool diarize)
    {
        var request = Request(HttpMethod.Post, "v2/pre-recorded", apiKey);
        var code = CloudTranscription.LanguageCode(language);
        ProviderHttp.SetJsonBody(request, new JobBody(audioUrl, model, diarize, new(code is null ? [] : [code], false)));
        return request;
    }

    /// <summary>Settings' Test: <c>GET /v2/pre-recorded?limit=1</c> lists at most one past job (no audio). Pure, for tests.</summary>
    public static HttpRequestMessage MakeKeyCheckRequest(string apiKey)
    {
        var request = Request(HttpMethod.Get, "v2/pre-recorded?limit=1", apiKey);
        request.Options.Set(ProviderHttp.TimeoutOption, ProviderHttp.ListTimeout);
        return request;
    }

    /// <summary><c>GET /v2/pre-recorded/{id}</c>; <paramref name="id"/> must be a plain id (see <see cref="CloudTranscription.CheckedId"/>).</summary>
    public static HttpRequestMessage MakePollRequest(string apiKey, string id) =>
        Request(HttpMethod.Get, "v2/pre-recorded/" + CloudTranscription.CheckedId(id, "Gladia"), apiKey);

    /// <summary>
    /// The job's segments once <c>done</c> (null while queued or processing); an <c>error</c> job throws. Utterances map
    /// 1:1 (their <c>text</c>, <c>start</c>, <c>end</c>, integer <c>speaker</c>, <c>language</c>, else the job's first
    /// language); without utterances, <c>full_transcript</c> is one segment over the audio. Pure, for tests.
    /// </summary>
    public static List<CloudSegment>? ParseJob(ReadOnlySpan<byte> data)
    {
        var response = ProviderHttp.Decode<JobResponse>(data);
        switch (response.Status)
        {
            case "done":
                var transcription = response.Result?.Transcription;
                var fallback = transcription?.Languages?.FirstOrDefault();
                if (transcription?.Utterances is { Count: > 0 } utterances)
                {
                    return utterances
                        .Select(utterance => (utterance, text: utterance.Text.Trim()))
                        .Where(item => item.text.Length > 0)
                        .Select(item => new CloudSegment(
                            item.utterance.Start, item.utterance.End, item.text,
                            item.utterance.Speaker?.ToString(CultureInfo.InvariantCulture), item.utterance.Language ?? fallback))
                        .ToList();
                }
                var text = transcription?.FullTranscript?.Trim() ?? "";
                return text.Length == 0 ? [] : [new CloudSegment(
                    0, response.Result?.Metadata?.AudioDuration ?? 0, text, Language: fallback)];
            case "error":
                throw LLMException.Provider("Gladia could not transcribe the audio.");
            default:
                return null;
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string pathAndQuery, string apiKey) =>
        CloudTranscription.Request(method, new Uri(BaseUrl, pathAndQuery), CloudTranscription.RequestTimeout, "x-gladia-key", apiKey);

    private sealed record JobBody(string AudioUrl, string Model, bool Diarization, LanguageConfig LanguageConfig);

    private sealed record LanguageConfig(List<string> Languages, bool CodeSwitching);

    private sealed record UploadResponse(string AudioUrl);

    private sealed record CreatedJob(string Id);

    private sealed record JobResponse(string Status, JobResult? Result = null);

    private sealed record JobResult(JobMetadata? Metadata = null, JobTranscription? Transcription = null);

    private sealed record JobMetadata(double? AudioDuration = null);

    private sealed record JobTranscription(
        List<string>? Languages = null, string? FullTranscript = null, List<Utterance>? Utterances = null);

    private sealed record Utterance(double Start, double End, string Text, int? Speaker = null, string? Language = null);
}
