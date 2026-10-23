using System.Globalization;
using System.Text.Json;
using EchoDiar.Core;
using EchoDiar.Core.Settings;

namespace EchoDiar.AI.Cloud;

/// <summary>
/// One transcribed stretch from a cloud STT API (Swift <c>CloudSegment</c>). Times are seconds from the start of the
/// uploaded file.
/// </summary>
/// <param name="Speaker">The provider's own label ("A", "speaker_1", ...); null when it did not diarize.</param>
/// <param name="Language">The language code the provider reported, when it did.</param>
public sealed record CloudSegment(double Start, double End, string Text, string? Speaker = null, string? Language = null)
{
    /// <summary>The same segment moved <paramref name="seconds"/> later (a split part's start).</summary>
    public CloudSegment Offset(double seconds) => this with { Start = Start + seconds, End = End + seconds };
}

/// <summary>
/// A cloud speech-to-text API (Swift <c>CloudTranscriber</c>). Keys come from the caller (the
/// <see cref="ISecretStore"/> key of the provider's id) and only ever go in headers. Cancelling the token cancels the
/// request (and any polling).
/// </summary>
public interface ICloudTranscriber
{
    /// <summary>Largest file one request may carry; null when a meeting recording never reaches the provider's cap.</summary>
    int? MaxUploadBytes => null;

    /// <summary>Timestamps come from an LLM and can drift (Gemini); the UI says so.</summary>
    bool ApproximateTimestamps => false;

    /// <summary>
    /// <paramref name="file"/> is a mono audio file of <paramref name="duration"/> seconds (Swift reads the duration
    /// from the file; here the exporter reports it). <paramref name="language"/>: a BCP-47 code, or null / <c>auto</c>
    /// to detect. <paramref name="diarize"/>: ask for speaker labels (only the system channel does).
    /// </summary>
    Task<IReadOnlyList<CloudSegment>> TranscribeAsync(
        string file, double duration, string? language, bool diarize, CancellationToken cancellationToken = default);
}

/// <summary>Seconds from the start of a recording.</summary>
public readonly record struct TimeRange(double Start, double End);

/// <summary>
/// Writes one channel of a recording as a mono AAC <c>.m4a</c> (Windows: Media Foundation at
/// <see cref="CloudTranscription.ExportBitRate"/>, decision 6), and measures its loudness for the split.
/// Implemented by Platform; tests use a fake.
/// </summary>
public interface IAudioExporter
{
    /// <summary>
    /// Writes <paramref name="channel"/> of <paramref name="source"/> from <paramref name="range"/> (null = all) to
    /// <paramref name="destination"/> as mono AAC; returns the written length in seconds.
    /// </summary>
    double ExportMonoAac(string source, int channel, TimeRange? range, string destination, CancellationToken cancellationToken);

    /// <summary>The RMS of <paramref name="channel"/> per <paramref name="window"/>-second slot from the start.</summary>
    float[] Levels(string source, int channel, double window, CancellationToken cancellationToken);
}

/// <summary>Where a provider's audio and key go (Swift <c>destination(for:)</c>).</summary>
/// <param name="ConsentKey">
/// <c>&lt;provider id&gt;@&lt;scheme://host[:port]&gt;</c> (lowercased, default port dropped), the key of the one-time
/// confirmation, so a changed base URL asks again.
/// </param>
/// <param name="Host">The host shown in the confirmation.</param>
public readonly record struct CloudDestination(string ConsentKey, string Host);

/// <summary>Upload preparation shared by every cloud transcriber: mono AAC export, splitting under a size cap, offsets.</summary>
public static partial class CloudTranscription
{
    /// <summary>
    /// Part budget for providers with a 25 MB cap (OpenAI, Groq free tier): decimal bytes, so it stays under the cap
    /// whether the provider counts MB or MiB.
    /// </summary>
    public const int CappedUploadBytes = 24_000_000;

    /// <summary>
    /// The export bit rate on Windows: Media Foundation's AAC encoder offers no less than 96 kbps at 48 kHz (decision 6;
    /// macOS uses 32 kbps). That is about 43 MB per hour, so a capped provider gets parts of about 33 minutes.
    /// </summary>
    public const int ExportBitRate = 96_000;

    /// <summary>Seconds per loudness window when looking for the quietest place to cut.</summary>
    public const double LevelWindow = 0.1;

    /// <summary>Long transcription requests send nothing back until the provider is done, so the idle timeout is long.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(900);

    /// <summary>
    /// Transcribes <paramref name="channel"/> of <paramref name="source"/> with <paramref name="transcriber"/>: exports
    /// it to mono m4a in a private temporary folder, splits it at the quietest moments when it is over
    /// <see cref="ICloudTranscriber.MaxUploadBytes"/>, sends the parts one by one and moves each part's segments by the
    /// part's start. The temporary files are removed before returning.
    /// </summary>
    public static async Task<List<CloudSegment>> TranscribeAsync(
        string source, int channel, ICloudTranscriber transcriber, IAudioExporter exporter, string? language, bool diarize,
        CancellationToken cancellationToken = default)
    {
        // Created user-only (0700 on Unix; the user's own temp folder on Windows).
        var work = Directory.CreateTempSubdirectory("EchoDiar-cloud-").FullName;
        try
        {
            var maxBytes = transcriber.MaxUploadBytes;
            var parts = await Task.Run(
                () => UploadParts(exporter, source, channel, maxBytes, work, cancellationToken), cancellationToken);
            var segments = new List<CloudSegment>();
            foreach (var part in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await transcriber.TranscribeAsync(part.File, part.Duration, language, diarize, cancellationToken);
                segments.AddRange(Sanitized(result, part.Duration).Select(segment => segment.Offset(part.Start)));
            }
            return segments;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Best effort, as Swift's try?: the OS cleans its temp folder.
            }
        }
    }

    /// <summary>
    /// The provider's segments made safe to store: times clamped to <c>0...duration</c> (non-finite ones to 0),
    /// <c>End</c> not before <c>Start</c>, empty texts dropped. Pure, for tests.
    /// </summary>
    public static List<CloudSegment> Sanitized(IEnumerable<CloudSegment> segments, double duration)
    {
        duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        double Clamp(double time) => double.IsFinite(time) ? Math.Min(Math.Max(time, 0), duration) : 0;
        var result = new List<CloudSegment>();
        foreach (var segment in segments)
        {
            var text = segment.Text.Trim();
            if (text.Length == 0) continue;
            var start = Clamp(segment.Start);
            result.Add(segment with { Text = text, Start = start, End = Math.Max(start, Clamp(segment.End)) });
        }
        return result;
    }

    /// <summary>
    /// Where to cut a file of <paramref name="duration"/> seconds at <paramref name="bytesPerSecond"/> so no part is over
    /// <paramref name="maxBytes"/>: each cut is at the quietest <paramref name="window"/>-second slot of
    /// <paramref name="levels"/> (RMS per slot from the start) in the second half of the allowed length, else at the
    /// limit. Pure, for tests.
    /// </summary>
    public static List<TimeRange> PlanParts(
        double duration, double bytesPerSecond, int maxBytes, IReadOnlyList<float> levels, double window)
    {
        var maxLength = maxBytes / bytesPerSecond;
        if (!double.IsFinite(maxLength) || !(maxLength > 0) || !(duration > maxLength)) return [new(0, Math.Max(0, duration))];
        var parts = new List<TimeRange>();
        var start = 0.0;
        while (duration - start > maxLength)
        {
            var limit = start + maxLength;
            var first = (int)Math.Ceiling((start + maxLength / 2) / window);
            var last = Math.Min(levels.Count, (int)Math.Floor(limit / window)) - 1;
            var cut = limit;
            if (first <= last)
            {
                var quietest = first;
                for (var slot = first + 1; slot <= last; slot++)
                    if (levels[slot] < levels[quietest]) quietest = slot;
                cut = (quietest + 0.5) * window;
            }
            parts.Add(new(start, cut));
            start = cut;
        }
        parts.Add(new(start, duration));
        return parts;
    }

    /// <summary>The primary subtag of a BCP-47 code (<c>fr-FR</c> -> <c>fr</c>), which every provider here takes; null for Auto.</summary>
    public static string? LanguageCode(string? language)
    {
        var trimmed = language?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        return trimmed.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
    }

    internal static string MimeType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".m4a" or ".mp4" => "audio/mp4",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        _ => "application/octet-stream",
    };

    // Words to segments.

    /// <summary>A timed word; <c>Text</c> carries the spacing that follows it when the provider sends spacing tokens.</summary>
    internal record struct Word(double Start, double End, string Text, string? Speaker);

    /// <summary>
    /// A new segment starts when the speaker changes, after a pause over <paramref name="maxPause"/>, or after a sentence
    /// end once the segment is <paramref name="longSegment"/> seconds long (keeps a monologue from becoming one line).
    /// </summary>
    internal static List<CloudSegment> Segments(
        IEnumerable<Word> words, string separator, string? language, double maxPause = 1.0, double longSegment = 15)
    {
        var segments = new List<CloudSegment>();
        foreach (var word in words)
        {
            if (segments.Count > 0 && segments[^1] is var current && current.Speaker == word.Speaker
                && word.Start - current.End <= maxPause
                && !(current.End - current.Start >= longSegment && EndsSentence(current.Text)))
            {
                segments[^1] = current with { Text = current.Text + separator + word.Text, End = Math.Max(current.End, word.End) };
            }
            else
            {
                segments.Add(new(word.Start, word.End, word.Text, word.Speaker, language));
            }
        }
        return segments.Select(segment => segment with { Text = segment.Text.Trim() })
            .Where(segment => segment.Text.Length > 0).ToList();
    }

    /// <summary>Whether the last character before trailing spaces and tabs (not line breaks, as Swift's <c>.whitespaces</c>) ends a sentence.</summary>
    private static bool EndsSentence(string text)
    {
        var end = text.Length;
        while (end > 0 && (text[end - 1] == '\t' || char.GetUnicodeCategory(text[end - 1]) == UnicodeCategory.SpaceSeparator)) end--;
        return end > 0 && ".?!\u3002\uFF1F\uFF01".Contains(text[end - 1]);
    }

    // Audio.

    private sealed record UploadPart(string File, double Start, double Duration);

    // Requests.

    /// <summary>A request to a vendor's native API: <paramref name="header"/> carries the key; the timeout as Swift's.</summary>
    internal static HttpRequestMessage Request(HttpMethod method, Uri url, TimeSpan timeout, string header, string value)
    {
        var request = new HttpRequestMessage(method, url);
        request.Options.Set(ProviderHttp.TimeoutOption, timeout);
        ProviderHttp.SetHeader(request, header, value);
        return request;
    }

    /// <summary>The audio file as a body streamed from disk (memory stays flat whatever its size).</summary>
    internal static StreamContent FileContent(string file, string? mimeType)
    {
        var content = new StreamContent(new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan));
        if (mimeType is not null) content.Headers.ContentType = new(mimeType);
        return content;
    }

    /// <summary>
    /// A multipart/form-data body: the audio file part <c>file</c> (streamed from disk, with its name and MIME type),
    /// then <paramref name="fields"/> in order. Names are quoted as Swift's <c>MultipartForm</c> writes them.
    /// </summary>
    internal static MultipartFormDataContent MultipartForm(string file, IEnumerable<(string Name, string Value)> fields)
    {
        var form = new MultipartFormDataContent("EchoDiar-" + Guid.NewGuid().ToString("D").ToUpperInvariant());
        var audio = FileContent(file, MimeType(file));
        audio.Headers.ContentDisposition = new("form-data") { Name = $"\"file\"", FileName = $"\"{Path.GetFileName(file)}\"" };
        form.Add(audio);
        foreach (var (name, value) in fields)
        {
            var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(value));
            part.Headers.ContentDisposition = new("form-data") { Name = $"\"{name}\"" };
            form.Add(part);
        }
        return form;
    }

    /// <summary>The whole channel as one m4a, or parts when it is over <paramref name="maxBytes"/>.</summary>
    private static List<UploadPart> UploadParts(
        IAudioExporter exporter, string source, int channel, int? maxBytes, string work, CancellationToken cancellationToken)
    {
        var whole = Path.Combine(work, "channel.m4a");
        var duration = exporter.ExportMonoAac(source, channel, null, whole, cancellationToken);
        var bytes = new FileInfo(whole).Length;
        if (maxBytes is not { } max || bytes <= max || !(duration > 0)) return [new(whole, 0, duration)];
        File.Delete(whole);
        var ranges = PlanParts(
            duration, bytes / duration, max, exporter.Levels(source, channel, LevelWindow, cancellationToken), LevelWindow);
        return ranges.Select((range, index) =>
        {
            var file = Path.Combine(work, $"part-{index}.m4a");
            var length = exporter.ExportMonoAac(source, channel, range, file, cancellationToken);
            return new UploadPart(file, range.Start, length);
        }).ToList();
    }
}

/// <summary>
/// Cloud engines are picked by engine id <c>cloud:&lt;provider id&gt;</c> (Transcription tab default, Re-transcribe and
/// Retry menus); the model is the config's <c>TranscriptionModel</c>, else the vendor default. The provider id is also
/// the <see cref="ISecretStore"/> id, so OpenAI, Groq, Mistral and Gemini share the key of their LLM config.
/// </summary>
public static partial class CloudTranscription
{
    public const string EnginePrefix = "cloud:";

    public static string EngineId(string providerId) => EnginePrefix + providerId;

    /// <summary>The provider id of a cloud engine id; null for catalog ids and null.</summary>
    public static string? ProviderId(string? engineId)
    {
        if (engineId is null || !engineId.StartsWith(EnginePrefix, StringComparison.Ordinal)) return null;
        var id = engineId[EnginePrefix.Length..];
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// OpenAI, Groq, Mistral, Gemini (native audio API, not the LLM preset's OpenAI shim), the speech-to-text presets
    /// and custom OpenAI-compatible endpoints.
    /// </summary>
    public static bool Supports(ProviderConfig config) => config.Kind switch
    {
        ProviderKind.SpeechToText => true,
        ProviderKind.OpenAICompatible => config.Preset is null or "openai" or "groq" or "mistral" or "gemini",
        _ => false,
    };

    /// <summary>Presets that can be the default cloud diarizer. Mistral is left out until its model id is verified to diarize.</summary>
    public static readonly IReadOnlySet<string> DiarizationPresets =
        new HashSet<string> { "openai", "elevenlabs", "deepgram", "assemblyai", "gemini" };

    /// <summary>Whether <paramref name="model"/> of <paramref name="config"/> returns speaker labels (a non-diarizing model would still bill a transcription).</summary>
    public static bool Diarizes(ProviderConfig config, string model)
    {
        model = model.Trim().ToLowerInvariant();
        return config.Preset switch
        {
            "openai" => model.EndsWith("-diarize", StringComparison.Ordinal),
            "elevenlabs" => model.StartsWith("scribe", StringComparison.Ordinal),
            "deepgram" => model.StartsWith("nova", StringComparison.Ordinal),
            "gemini" => model.Contains("gemini", StringComparison.Ordinal),
            "assemblyai" => true,
            _ => false,
        };
    }

    /// <summary>
    /// The speaker turns of a cloud diarizer's segments (their text is not kept): labels numbered 0, 1, ... by first
    /// appearance in start order, unlabeled segments dropped, sorted by start. Pure, for tests.
    /// </summary>
    public static List<DiarizationTurn> Turns(IEnumerable<CloudSegment> segments)
    {
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var turns = new List<DiarizationTurn>();
        foreach (var segment in segments.OrderBy(segment => segment.Start))
        {
            if (segment.Speaker is not { } label) continue;
            if (!indexes.TryGetValue(label, out var index)) indexes[label] = index = indexes.Count;
            turns.Add(new(segment.Start, segment.End, index));
        }
        return turns;
    }

    /// <summary>Whether requests carry a key: always, except custom endpoints set to no authentication.</summary>
    public static bool NeedsKey(ProviderConfig config) => config.Kind == ProviderKind.SpeechToText || config.AuthStyle != AuthStyle.None;

    /// <summary>The model used when <c>TranscriptionModel</c> is empty; "" when AssemblyAI picks it or for custom endpoints.</summary>
    public static string DefaultModel(ProviderConfig config) => config.Preset switch
    {
        "gemini" => GeminiAudioTranscriber.DefaultModel,
        "elevenlabs" => ElevenLabsTranscriber.DefaultModel,
        "deepgram" => DeepgramTranscriber.DefaultModel,
        { } preset => OpenAICompatibleTranscriber.PresetModels.TryGetValue(preset, out var models) ? models[0] : "",
        null => "",
    };

    public static string Model(ProviderConfig config)
    {
        var model = config.TranscriptionModel.Trim();
        return model.Length == 0 ? DefaultModel(config) : model;
    }

    /// <summary>The model used when <c>DiarizationModel</c> is empty; "" when AssemblyAI picks it or the preset cannot diarize.</summary>
    public static string DefaultDiarizationModel(ProviderConfig config) => config.Preset switch
    {
        "openai" => OpenAICompatibleTranscriber.PresetModels["openai"].FirstOrDefault(model => model.EndsWith("-diarize", StringComparison.Ordinal)) ?? "",
        "gemini" => GeminiAudioTranscriber.DefaultModel,
        "elevenlabs" => ElevenLabsTranscriber.DefaultModel,
        "deepgram" => DeepgramTranscriber.DefaultModel,
        _ => "",
    };

    public static string DiarizationModel(ProviderConfig config)
    {
        var model = config.DiarizationModel.Trim();
        return model.Length == 0 ? DefaultDiarizationModel(config) : model;
    }

    /// <summary>
    /// Whether <paramref name="config"/> (enabled or not) has what a request needs besides its key: a model (AssemblyAI
    /// excepted) and a valid destination (the base URL, unless the preset has a native API).
    /// </summary>
    public static bool IsComplete(ProviderConfig config)
    {
        if (!Supports(config)) return false;
        if (config.Preset == "assemblyai") return true;
        return Model(config).Length > 0 && Destination(config) is not null;
    }

    /// <summary>
    /// <paramref name="model"/>: null for the transcription model (<see cref="Model"/>); a cloud diarizer passes its
    /// diarization model. <paramref name="handler"/>: null for the shared transport (a test seam otherwise).
    /// </summary>
    public static ICloudTranscriber MakeTranscriber(
        ProviderConfig config, string? apiKey, string? model = null, HttpMessageHandler? handler = null)
    {
        model ??= Model(config);
        var key = apiKey ?? "";
        return config.Preset switch
        {
            "gemini" => new GeminiAudioTranscriber(key, model, handler),
            "elevenlabs" => new ElevenLabsTranscriber(key, model, handler),
            "deepgram" => new DeepgramTranscriber(key, model, handler),
            "assemblyai" => new AssemblyAITranscriber(key, handler),
            _ => new OpenAICompatibleTranscriber(config, model, apiKey, handler),
        };
    }

    /// <summary>
    /// Where <paramref name="config"/>'s audio and key go, for the one-time confirmation (see <see cref="CloudDestination"/>).
    /// The presets with a native API use its fixed origin, whatever their base URL says. Null for an invalid base URL.
    /// </summary>
    public static CloudDestination? Destination(ProviderConfig config)
    {
        Uri url;
        switch (config.Preset)
        {
            case "gemini": url = GeminiAudioTranscriber.BaseUrl; break;
            case "elevenlabs": url = ElevenLabsTranscriber.Endpoint; break;
            case "deepgram": url = DeepgramTranscriber.Endpoint; break;
            case "assemblyai": url = AssemblyAITranscriber.BaseUrl; break;
            default:
                try
                {
                    url = ProviderHttp.BaseUrl(config);
                }
                catch (LLMException)
                {
                    return null;
                }
                break;
        }
        return Destination(config.Id, url);
    }

    /// <summary>
    /// Settings' Test for the speech-to-text presets: one free body-less GET that needs a valid key and uploads no audio.
    /// Throws like a transcription request (<see cref="LLMErrorKind.HttpStatus"/> for a refused key).
    /// </summary>
    public static async Task CheckKeyAsync(
        ProviderConfig config, string apiKey, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        Func<HttpRequestMessage> makeRequest = config.Preset switch
        {
            "elevenlabs" => () => ElevenLabsTranscriber.MakeKeyCheckRequest(apiKey),
            "deepgram" => () => DeepgramTranscriber.MakeKeyCheckRequest(apiKey),
            "assemblyai" => () => AssemblyAITranscriber.MakeKeyCheckRequest(apiKey),
            _ => throw LLMException.Provider($"No key check for {config.DisplayName}."),
        };
        await ProviderHttp.SendAsync(
            ProviderHttp.Client(handler), makeRequest, retry: true, ProviderHttp.SmallJsonLimit, (_, _) => true, cancellationToken);
    }

    /// <summary>
    /// The destination of <paramref name="url"/> for provider <paramref name="providerId"/>: the consent key
    /// <c>&lt;id&gt;@&lt;scheme://host[:port]&gt;</c> (lowercased, default port dropped) and the host; null without a host.
    /// </summary>
    internal static CloudDestination? Destination(string providerId, Uri url)
    {
        if (!url.IsAbsoluteUri || url.Host.Length == 0) return null;
        var scheme = url.Scheme.ToLowerInvariant();
        var host = url.Host.ToLowerInvariant();
        var origin = $"{scheme}://{host}" + (url.IsDefaultPort ? "" : ":" + url.Port.ToString(CultureInfo.InvariantCulture));
        return new($"{providerId}@{origin}", host);
    }

    /// <summary>A short reason for the card ("invalid API key"); the caller prefixes the provider name. Pure, for tests.</summary>
    public static string FailureReason(Exception error) => error switch
    {
        LLMException { Kind: LLMErrorKind.HttpStatus, StatusCode: 401 or 403 } => "invalid API key",
        LLMException { Kind: LLMErrorKind.HttpStatus, StatusCode: 413 } => "audio too large",
        LLMException { Kind: LLMErrorKind.HttpStatus, StatusCode: 429 } => "rate limit or quota reached",
        LLMException { Kind: LLMErrorKind.HttpStatus, StatusCode: >= 400 and < 500 } http =>
            VendorMessage(http.Body) is { } message ? $"HTTP {http.StatusCode}: {message}" : $"HTTP {http.StatusCode}",
        LLMException { Kind: LLMErrorKind.HttpStatus } http => $"HTTP {http.StatusCode}",
        LLMException { Kind: LLMErrorKind.InvalidBaseUrl } => "invalid base URL",
        TimeoutException => "timed out",
        OperationCanceledException => "cancelled",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError } =>
            "no internet connection",
        HttpRequestException or IOException => "network error",
        _ => ShortLine(error.Message),
    };

    /// <summary>
    /// The message of a vendor error body (<c>error.message</c>, <c>error</c>, <c>message</c>, <c>detail</c>,
    /// <c>detail.message</c>, <c>detail[0].msg</c>, <c>err_msg</c>), shortened; null when there is none. The body was
    /// redacted by <see cref="ProviderHttp.SendAsync{T}"/>.
    /// </summary>
    internal static string? VendorMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = json.RootElement;
            var message = Text(Property(root, "error")) ?? Text(Property(root, "message")) ?? Text(Property(root, "detail"))
                          ?? Text(Property(root, "err_msg"));
            return message is null ? null : ShortLine(message) is { Length: > 0 } line ? line : null;
        }
        catch (JsonException)
        {
            return null;
        }

        static JsonElement? Property(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

        static string? Text(JsonElement? value) => value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Object => Text(Property(value.Value, "message")) ?? Text(Property(value.Value, "msg")),
            JsonValueKind.Array => value.Value.GetArrayLength() > 0 ? Text(value.Value[0]) : null,
            _ => null,
        };
    }

    /// <summary>The first non-empty line, trimmed, at most 120 characters (text elements, as Swift counts).</summary>
    private static string ShortLine(string text)
    {
        var line = text.Split(['\n', '\r', '\v', '\f', '\u0085', '\u2028', '\u2029'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "";
        var info = new StringInfo(line);
        return info.LengthInTextElements > 120 ? info.SubstringByTextElements(0, 120) + "..." : line;
    }
}
