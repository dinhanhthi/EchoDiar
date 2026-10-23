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
            else if (model.StartsWith("gpt-", StringComparison.Ordinal))
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

    private static bool IsMistral(ProviderConfig config) => config.Preset == "mistral";

    private sealed record TranscriptionResponse(string? Text = null, string? Language = null, List<TranscriptionResponse.Segment>? Segments = null)
    {
        /// <param name="Speaker">OpenAI <c>diarized_json</c>.</param>
        /// <param name="SpeakerId">Mistral.</param>
        internal sealed record Segment(double Start, double End, string Text, string? Speaker = null, string? SpeakerId = null);
    }
}
