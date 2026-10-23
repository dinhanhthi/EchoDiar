namespace EchoDiar.AI.Cloud;

/// <summary>
/// Deepgram pre-recorded: <c>POST https://api.deepgram.com/v1/listen?...</c> with the audio bytes as the body and
/// <c>Authorization: Token &lt;key&gt;</c>. Utterances become segments.
/// </summary>
public sealed class DeepgramTranscriber : ICloudTranscriber
{
    public static readonly Uri Endpoint = new("https://api.deepgram.com/v1/listen");
    public const string DefaultModel = "nova-3";

    private readonly string _apiKey;
    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/>: null for the shared transport; a test seam otherwise (still behind the redirect guard).</summary>
    public DeepgramTranscriber(string apiKey, string model = DefaultModel, HttpMessageHandler? handler = null)
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

    /// <summary>Settings' Test: <c>GET /v1/projects</c> lists the key's projects (free, no audio). Pure, for tests.</summary>
    public static HttpRequestMessage MakeKeyCheckRequest(string apiKey) =>
        CloudTranscription.Request(
            HttpMethod.Get, new Uri("https://api.deepgram.com/v1/projects"), ProviderHttp.ListTimeout, "Authorization", "Token " + apiKey);

    /// <summary>
    /// <c>utterances=true</c> is added to the documented query: without it the answer has no utterances. The body is
    /// <paramref name="file"/>, streamed. Pure, for tests.
    /// </summary>
    public static HttpRequestMessage MakeRequest(string apiKey, string model, string file, string? language, bool diarize)
    {
        var query = new List<(string Name, string Value)>
        {
            ("model", model), ("smart_format", "true"), ("utterances", "true"), ("diarize", diarize ? "true" : "false"),
        };
        query.Add(CloudTranscription.LanguageCode(language) is { } code ? ("language", code) : ("detect_language", "true"));
        var url = new Uri(Endpoint + "?" + string.Join("&",
            query.Select(item => Uri.EscapeDataString(item.Name) + "=" + Uri.EscapeDataString(item.Value))));
        var request = CloudTranscription.Request(HttpMethod.Post, url, CloudTranscription.RequestTimeout, "Authorization", "Token " + apiKey);
        request.Content = CloudTranscription.FileContent(file, CloudTranscription.MimeType(file));
        return request;
    }

    /// <summary><c>results.utterances</c>, else the channel transcript as one segment. Pure, for tests.</summary>
    public static List<CloudSegment> Parse(ReadOnlySpan<byte> data)
    {
        var response = ProviderHttp.Decode<ListenResponse>(data);
        var channel = response.Results.Channels.FirstOrDefault();
        var language = channel?.DetectedLanguage;
        if (response.Results.Utterances is { Count: > 0 } utterances)
        {
            return utterances
                .Select(utterance => (utterance, text: utterance.Transcript.Trim()))
                .Where(item => item.text.Length > 0)
                .Select(item => new CloudSegment(
                    item.utterance.Start, item.utterance.End, item.text,
                    item.utterance.Speaker?.ToString(System.Globalization.CultureInfo.InvariantCulture), language))
                .ToList();
        }
        var text = channel?.Alternatives.FirstOrDefault()?.Transcript.Trim() ?? "";
        return text.Length == 0 ? [] : [new CloudSegment(0, response.Metadata?.Duration ?? 0, text, Language: language)];
    }

    private sealed record ListenResponse(ListenResults Results, ListenMetadata? Metadata = null);

    private sealed record ListenMetadata(double? Duration = null);

    private sealed record ListenResults(List<ListenChannel> Channels, List<ListenUtterance>? Utterances = null);

    private sealed record ListenChannel(List<ListenAlternative> Alternatives, string? DetectedLanguage = null);

    private sealed record ListenAlternative(string Transcript);

    private sealed record ListenUtterance(double Start, double End, string Transcript, int? Speaker = null);
}
