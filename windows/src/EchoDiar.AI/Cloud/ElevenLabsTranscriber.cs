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

    private sealed record SpeechToTextResponse(string? LanguageCode = null, List<SpeechToTextResponse.Word>? Words = null)
    {
        internal sealed record Word(string Text, double? Start = null, double? End = null, string? Type = null, string? SpeakerId = null);
    }
}
