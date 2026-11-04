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

    private sealed record TranscriptionBody(string Model, string FileId, List<string>? LanguageHints, bool EnableSpeakerDiarization);

    private sealed record Created(string Id);

    private sealed record TranscriptionStatus(string Id, string Status, string? ErrorMessage = null);

    private sealed record TranscriptResponse(List<Token>? Tokens = null);

    private sealed record Token(
        string Text, double StartMs, double EndMs, string? Speaker = null, string? Language = null, bool? IsAudioEvent = null);
}
