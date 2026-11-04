using System.Text.Json;

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
        var response = ProviderHttp.Decode<TranscriptResponse>(data);
        var words = new List<CloudTranscription.Word>();
        string? language = null;
        var glued = false;
        var prefix = "";
        foreach (var item in response.Results ?? [])
        {
            if (item.Alternatives?.FirstOrDefault() is not { Content.Length: > 0 } alternative) continue;
            language ??= alternative.Language;
            var attachment = item.Type == "punctuation" ? item.AttachesTo ?? "previous" : null;
            if (attachment is ("previous" or "both") && words.Count > 0)
            {
                var last = words[^1];
                words[^1] = last with { Text = last.Text + prefix + alternative.Content, End = Math.Max(last.End, item.EndTime) };
                prefix = "";
                glued = attachment == "both";
                continue;
            }
            if (attachment == "next")
            {
                prefix += (prefix.Length == 0 && !glued ? " " : "") + alternative.Content;
                glued = true;
                continue;
            }
            var speaker = alternative.Speaker == "UU" ? null : alternative.Speaker;
            var space = glued || prefix.Length > 0 ? "" : " ";
            words.Add(new(item.StartTime, item.EndTime, space + prefix + alternative.Content, speaker));
            prefix = "";
            glued = false;
        }
        return CloudTranscription.Segments(words, "", language);
    }

    private static HttpRequestMessage Request(HttpMethod method, string pathAndQuery, string apiKey) =>
        CloudTranscription.Request(
            method, new Uri(BaseUrl, pathAndQuery), CloudTranscription.RequestTimeout, "Authorization", "Bearer " + apiKey);

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
