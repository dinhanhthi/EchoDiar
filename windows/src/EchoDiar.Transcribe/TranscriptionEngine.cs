using System.Text.Json;
using System.Text.Json.Serialization;
using EchoDiar.Core;
using EchoDiar.Core.Model;

namespace EchoDiar.Transcribe;

/// <summary>
/// A batch ASR engine for one mono channel (Swift <c>TranscriptionEngine</c>). Implementations load their model
/// inside <see cref="TranscribeAsync"/>, never download anything, and unload before returning.
/// </summary>
public interface ITranscriptionEngine
{
    /// <summary>
    /// <paramref name="channelPath"/> is a 16 kHz mono Float32 CAF (read it with <see cref="MonoAudio.Read"/> in slabs of
    /// <see cref="MonoAudio.SlabSeconds"/>). Segment times are seconds from the start of that file.
    /// <paramref name="progress"/> gets 0…1.
    /// </summary>
    Task<IReadOnlyList<RawSegment>> TranscribeAsync(
        string channelPath, IReadOnlyList<LanguageRange> languageRanges, Action<double> progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Speaker turns on the system channel (Swift <c>SpeakerDiarizer</c>). Loads its model inside
/// <see cref="DiarizeAsync"/> and unloads before returning.
/// </summary>
public interface ISpeakerDiarizer
{
    /// <summary>
    /// <paramref name="channelPath"/> as in <see cref="ITranscriptionEngine.TranscribeAsync"/>. Turns are numbered by
    /// first use. <paramref name="progress"/> gets 0…1.
    /// </summary>
    Task<IReadOnlyList<DiarizationTurn>> DiarizeAsync(string channelPath, Action<double> progress, CancellationToken cancellationToken);
}

/// <summary>A stretch of audio with one language setting, built from the session's language markers.</summary>
/// <param name="Start">Seconds from the start of the audio file.</param>
/// <param name="End">null = to the end of the file.</param>
/// <param name="Language">BCP-47 code; null = detect per window.</param>
public sealed record LanguageRange(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double? End,
    [property: JsonPropertyName("language")] string? Language)
{
    /// <summary>Consecutive markers with the same language are merged; <c>auto</c>, empty or no marker mean detection.</summary>
    public static List<LanguageRange> Ranges(IReadOnlyList<RecordingSession.LanguageMarker> markers)
    {
        var points = new List<(double Start, string? Language)> { (0, null) };
        foreach (var marker in markers.OrderBy(m => m.FileTime))
        {
            var trimmed = marker.Language.Trim();
            var language = trimmed.Length == 0 || trimmed.ToLowerInvariant() == "auto" ? null : trimmed;
            var start = Math.Max(0, marker.FileTime);
            if (start <= points[^1].Start) points[^1] = (points[^1].Start, language);
            else points.Add((start, language));
        }
        var ranges = new List<LanguageRange>();
        foreach (var point in points)
        {
            if (ranges.Count == 0 || ranges[^1].Language != point.Language) ranges.Add(new(point.Start, null, point.Language));
        }
        for (var i = 0; i < ranges.Count - 1; i++) ranges[i] = ranges[i] with { End = ranges[i + 1].Start };
        return ranges;
    }
}

/// <summary>
/// One conversation folder to transcribe (Swift <c>TranscriptionJob</c>). Keys as the Swift Codable; Swift never
/// persists a job, so only the key names are kept (<see cref="Folder"/> is a path, not a file URL).
/// </summary>
/// <remarks>Swift's <c>prior</c> (transcribe-while-recording reuse) arrives in Phase 16.</remarks>
public sealed record TranscriptionJob
{
    /// <summary>The conversation (and session) id.</summary>
    [JsonPropertyName("conversationID")] public required Guid ConversationID { get; set; }
    /// <summary>Holds <c>session.json</c>; receives <c>transcript.json</c>.</summary>
    [JsonPropertyName("folder")] public required string Folder { get; set; }
    /// <summary>
    /// Set when the audio was compressed into two mono files: <c>session.audioFileName</c> is then the mic file and
    /// this one the system file. null: a stereo file (ch0 mic, ch1 system) or a mono file used as system.
    /// </summary>
    [JsonPropertyName("systemFileName")] public string? SystemFileName { get; set; }
    /// <summary>Catalog id of the ASR engine; null = the default resolution.</summary>
    [JsonPropertyName("engineID")] public string? EngineID { get; set; }
    /// <summary>
    /// "auto" or a BCP-47 code for the whole recording, replacing the session's language markers for this run
    /// (re-transcribe); null = the markers.
    /// </summary>
    [JsonPropertyName("language")] public string? Language { get; set; }
    /// <summary>"Process now": never deferred (Windows has no deferral yet; kept for parity).</summary>
    [JsonPropertyName("processNow")] public bool ProcessNow { get; set; }
    /// <summary>Speakers expected on the system channel for this run; null = the pipeline's hint.</summary>
    [JsonPropertyName("speakerCountHint")] public int? SpeakerCountHint { get; set; }
    /// <summary>The preferred local diarizer (catalog id); null = catalog order.</summary>
    [JsonPropertyName("diarizerID")] public string? DiarizerID { get; set; }
    /// <summary>"Tell speakers apart": false runs no diarizer, so the whole system channel is one speaker (<c>s1</c>).</summary>
    [JsonPropertyName("diarize")] public bool Diarize { get; set; } = true;
}

[JsonConverter(typeof(JobStepConverter))]
public enum JobStep
{
    Queued, Deferred, Extracting, TranscribingMic, TranscribingSystem, Diarizing, Merging,
    Finished, NeedsModel, Failed,
}

/// <summary>Swift raw values (<c>"transcribingMic"</c>).</summary>
public sealed class JobStepConverter() : JsonStringEnumConverter<JobStep>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

/// <param name="Fraction">Whole job, 0…1.</param>
/// <param name="Detail">Error text for <see cref="JobStep.Failed"/>, catalog id for <see cref="JobStep.NeedsModel"/>.</param>
public sealed record JobProgress(
    [property: JsonPropertyName("conversationID")] Guid ConversationID,
    [property: JsonPropertyName("step")] JobStep Step,
    [property: JsonPropertyName("fraction")] double Fraction,
    [property: JsonPropertyName("detail")] string? Detail = null);

public enum TranscriptionErrorKind
{
    /// <summary>The catalog entry is not installed.</summary>
    NeedsModel,
    UnknownEngine,
    SessionUnreadable,
    AudioUnreadable,
    EngineFailed,
}

/// <summary>Swift <c>TranscriptionError</c>; <see cref="Detail"/> is its associated value.</summary>
public sealed class TranscriptionException(TranscriptionErrorKind kind, string detail = "", Exception? inner = null)
    : Exception(Describe(kind, detail), inner)
{
    public TranscriptionErrorKind Kind { get; } = kind;
    public string Detail { get; } = detail;

    private static string Describe(TranscriptionErrorKind kind, string detail) => kind switch
    {
        TranscriptionErrorKind.NeedsModel => $"Needs model: {detail} is not installed.",
        TranscriptionErrorKind.UnknownEngine => $"{detail} is not a registered transcription engine.",
        TranscriptionErrorKind.SessionUnreadable => "The recording's session.json is missing or unreadable.",
        TranscriptionErrorKind.AudioUnreadable => $"Could not read the audio ({detail}).",
        _ => $"Transcription failed ({detail}).",
    };
}

/// <summary>Language codes of the multilingual Whisper models.</summary>
public static class WhisperLanguages
{
    /// <summary>
    /// whisper.cpp's language table (OpenAI Whisper's 99 codes plus <c>yue</c>), in id order, checked against the
    /// Whisper.net 1.9.1 natives (<c>whisper_lang_id</c>, ids 0…99). Whisper.net's <c>GetSupportedLanguages</c> lists
    /// only ids 0…98, so it omits <c>yue</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl", "ca", "nl", "ar", "sv", "it", "id", "hi",
        "fi", "vi", "he", "uk", "el", "ms", "cs", "ro", "da", "hu", "ta", "no", "th", "ur", "hr", "bg", "lt", "la",
        "mi", "ml", "cy", "sk", "te", "fa", "lv", "bn", "sr", "az", "sl", "kn", "et", "mk", "br", "eu", "is", "hy",
        "ne", "mn", "bs", "kk", "sq", "sw", "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka", "be",
        "tg", "sd", "gu", "am", "yi", "lo", "uz", "fo", "ht", "ps", "tk", "nn", "mt", "sa", "lb", "my", "bo", "tl",
        "mg", "as", "tt", "haw", "ln", "ha", "ba", "jw", "su", "yue",
    ];

    /// <summary>The primary subtag, lowercased (<c>"pt-BR"</c> → <c>"pt"</c>), as Swift <c>whisperLanguage</c>.</summary>
    public static string Primary(string code) =>
        code.ToLowerInvariant().Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
}
