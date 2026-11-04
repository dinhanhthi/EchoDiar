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

    private sealed record SttResponse(
        string? Text = null, string? Language = null, double? Duration = null, List<SttWord>? Words = null);

    private sealed record SttWord(string Text, double Start, double End, int? Speaker = null);
}
