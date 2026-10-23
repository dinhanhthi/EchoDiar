namespace EchoDiar.AI.Cloud;

/// <summary>
/// AssemblyAI: <c>POST /v2/upload</c> (audio bytes) -> <c>POST /v2/transcript</c> -> <c>GET /v2/transcript/{id}</c>
/// every 3 s while the job is queued or processing (at most <see cref="MaxPolls"/> times). <c>Authorization: &lt;key&gt;</c>
/// (no scheme). The model is AssemblyAI's default (Universal-3.5 Pro, falling back to Universal-2 for languages it
/// lacks, e.g. Vietnamese).
/// </summary>
public sealed class AssemblyAITranscriber : ICloudTranscriber
{
    public static readonly Uri BaseUrl = new("https://api.assemblyai.com");

    private readonly string _apiKey;
    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/>: null for the shared transport; a test seam otherwise (still behind the redirect guard).</summary>
    public AssemblyAITranscriber(string apiKey, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        _http = ProviderHttp.Client(handler);
    }

    /// <summary>The pause between two polls.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Polls before giving up (4 h at 3 s; AssemblyAI takes a fraction of the audio's length), so a job a vendor never
    /// finishes cannot poll forever.
    /// </summary>
    internal int MaxPolls { get; init; } = 4_800;

    public async Task<IReadOnlyList<CloudSegment>> TranscribeAsync(
        string file, double duration, string? language, bool diarize, CancellationToken cancellationToken = default)
    {
        var uploadUrl = await ProviderHttp.SendAsync(
            _http, () => MakeUploadRequest(_apiKey, file), retry: true, ProviderHttp.SmallJsonLimit,
            (body, _) => ProviderHttp.Decode<UploadResponse>(body).UploadUrl, cancellationToken);
        // Billed: a retry after a lost answer could create a second job.
        var job = await ProviderHttp.SendAsync(
            _http, () => MakeTranscriptRequest(_apiKey, uploadUrl, language, diarize), retry: false, ProviderHttp.JsonLimit,
            (body, _) => ParseTranscript(body), cancellationToken);
        // Polls only while this job runs; cancelling ends the wait and the loop.
        for (var polls = 0; job.Segments is null; polls++)
        {
            if (polls == MaxPolls) throw new TimeoutException("AssemblyAI did not finish the transcription in time.");
            await Task.Delay(PollInterval, cancellationToken);
            var id = job.Id;
            job = await ProviderHttp.SendAsync(
                _http, () => MakePollRequest(_apiKey, id), retry: true, ProviderHttp.JsonLimit,
                (body, _) => ParseTranscript(body), cancellationToken);
        }
        return job.Segments;
    }

    /// <summary>The audio is streamed from <paramref name="file"/>. Pure, for tests.</summary>
    public static HttpRequestMessage MakeUploadRequest(string apiKey, string file)
    {
        var request = Request(HttpMethod.Post, "v2/upload", apiKey);
        request.Content = CloudTranscription.FileContent(file, "application/octet-stream");
        return request;
    }

    /// <summary><c>speaker_labels</c> follows <paramref name="diarize"/>; <c>language_code</c> when a language is set, else <c>language_detection</c>. Pure, for tests.</summary>
    public static HttpRequestMessage MakeTranscriptRequest(string apiKey, string audioUrl, string? language, bool diarize)
    {
        var request = Request(HttpMethod.Post, "v2/transcript", apiKey);
        var code = CloudTranscription.LanguageCode(language);
        ProviderHttp.SetJsonBody(request, new TranscriptBody(audioUrl, diarize, code, code is null ? true : null));
        return request;
    }

    /// <summary>Settings' Test: <c>GET /v2/transcript?limit=1</c> lists at most one past transcript (free, no audio).</summary>
    public static HttpRequestMessage MakeKeyCheckRequest(string apiKey)
    {
        var request = Request(HttpMethod.Get, "v2/transcript?limit=1", apiKey);
        request.Options.Set(ProviderHttp.TimeoutOption, ProviderHttp.ListTimeout);
        return request;
    }

    /// <summary>
    /// <c>GET /v2/transcript/{id}</c>. The id comes from the API, so it must be a plain id (letters, digits, <c>-</c>,
    /// <c>_</c>): anything else could reach another path with the key.
    /// </summary>
    public static HttpRequestMessage MakePollRequest(string apiKey, string id)
    {
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw LLMException.Provider("AssemblyAI returned an invalid job id.");
        return Request(HttpMethod.Get, "v2/transcript/" + id, apiKey);
    }

    /// <summary>
    /// The job id, and its segments once completed (null while queued or processing); a failed job throws. Utterances
    /// when speakers were labeled, else words grouped by pause. Pure, for tests.
    /// </summary>
    public static (string Id, List<CloudSegment>? Segments) ParseTranscript(ReadOnlySpan<byte> data)
    {
        var response = ProviderHttp.Decode<TranscriptResponse>(data);
        switch (response.Status)
        {
            case "completed":
                var language = response.LanguageCode;
                if (response.Utterances is { Count: > 0 } utterances)
                {
                    return (response.Id, utterances
                        .Select(utterance => (utterance, text: utterance.Text.Trim()))
                        .Where(item => item.text.Length > 0)
                        .Select(item => new CloudSegment(
                            item.utterance.Start / 1000.0, item.utterance.End / 1000.0, item.text, item.utterance.Speaker, language))
                        .ToList());
                }
                var words = (response.Words ?? []).Select(word =>
                    new CloudTranscription.Word(word.Start / 1000.0, word.End / 1000.0, word.Text, word.Speaker));
                return (response.Id, CloudTranscription.Segments(words, " ", language));
            case "error":
                throw LLMException.Provider(response.Error ?? "AssemblyAI could not transcribe the audio.");
            default:
                return (response.Id, null);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string pathAndQuery, string apiKey) =>
        CloudTranscription.Request(
            method, new Uri(BaseUrl, pathAndQuery), CloudTranscription.RequestTimeout, "Authorization", apiKey);

    private sealed record TranscriptBody(string AudioUrl, bool SpeakerLabels, string? LanguageCode, bool? LanguageDetection);

    private sealed record UploadResponse(string UploadUrl);

    private sealed record TranscriptResponse(
        string Id, string Status, string? Error = null, string? LanguageCode = null,
        List<Utterance>? Utterances = null, List<Utterance>? Words = null);

    private sealed record Utterance(int Start, int End, string Text, string? Speaker = null);
}
