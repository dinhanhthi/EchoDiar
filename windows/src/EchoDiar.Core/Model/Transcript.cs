using System.Globalization;
using System.Text.Json.Serialization;
using EchoDiar.Core.Json;

namespace EchoDiar.Core.Model;

/// <summary>Swift <c>Transcript.Channel</c>. An unrecognized raw value reads as <see cref="Unknown"/>.</summary>
[JsonConverter(typeof(TranscriptChannelConverter))]
public enum TranscriptChannel
{
    Mic, System,
    /// <summary>An unrecognized raw value (written by another build). Written back as "unknown".</summary>
    Unknown,
}

public sealed class TranscriptChannelConverter() : TolerantEnumConverter<TranscriptChannel>(TranscriptChannel.Unknown);

/// <summary>One kept transcript: its number (1, 2, …) and its content.</summary>
public sealed record TranscriptVersion(int Number, Transcript Transcript);

/// <summary>
/// The <c>transcript.json</c> file format, version 1, written into the conversation folder after transcription.
/// <c>createdAt</c> is required; every other key is optional when read.
/// </summary>
public sealed record Transcript
{
    /// <summary>Speaker ids: <c>"me"</c> for the mic channel, <c>"s1"…"sN"</c> for system-channel speakers.</summary>
    public static class Speaker
    {
        public const string Me = "me";

        /// <summary><c>"s&lt;number&gt;"</c>, numbered from 1.</summary>
        public static string System(int number) => $"s{number}";
    }

    public sealed record Segment
    {
        private double? _end;
        private string? _speaker;

        [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
        /// <summary>Seconds from the start of the audio file.</summary>
        [JsonPropertyName("start")] public double Start { get; set; }

        /// <summary>Defaults to <see cref="Start"/> when missing on read.</summary>
        [JsonPropertyName("end")]
        public double End
        {
            get => _end ?? Start;
            set => _end = value;
        }

        [JsonPropertyName("channel")] public TranscriptChannel Channel { get; set; } = TranscriptChannel.Unknown;

        /// <summary>Defaults by channel when missing on read: <c>"me"</c> for mic, else <c>"s1"</c>.</summary>
        [JsonPropertyName("speaker")]
        public string Speaker
        {
            get => _speaker ?? (Channel == TranscriptChannel.Mic ? Transcript.Speaker.Me : Transcript.Speaker.System(1));
            set => _speaker = value;
        }

        [JsonPropertyName("text")] public string Text { get; set; } = "";
        /// <summary>BCP-47 code, or null when unknown.</summary>
        [JsonPropertyName("language")] public string? Language { get; set; }
    }

    public const string FileName = "transcript.json";
    /// <summary>Transcripts replaced by a re-transcription, <c>v&lt;number&gt;.json</c>; <c>transcript.json</c> stays the newest.</summary>
    public const string VersionsFolderName = "transcripts";
    public const string DiarizationMissingNote = "diarization model not installed";
    /// <summary>
    /// Added when the cloud diarizer failed or returned no speaker turns; the local diarizer ran instead when one was
    /// installed, else <see cref="DiarizationMissingNote"/> is added too.
    /// </summary>
    public const string CloudDiarizationFailedNote = "cloud diarization failed";

    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("segments")] public List<Segment> Segments { get; set; } = [];
    /// <summary>Display names chosen by the user, by speaker id. Only overrides; see <see cref="DisplayName"/>.</summary>
    [JsonPropertyName("speakerNames")] public Dictionary<string, string> SpeakerNames { get; set; } = [];
    /// <summary>Catalog id of the engine that produced the segments.</summary>
    [JsonPropertyName("engineID")] public string? EngineID { get; set; }
    [JsonPropertyName("modelID")] public string? ModelID { get; set; }
    /// <summary>Catalog id of the engine that was selected but could not handle the language; <see cref="EngineID"/> replaced it.</summary>
    [JsonPropertyName("engineFallback")] public string? EngineFallback { get; set; }
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = [];

    [JsonPropertyName("createdAt"), JsonConverter(typeof(AppleDateConverter)), JsonRequired]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The user's name for <paramref name="speaker"/>, else <see cref="DefaultName"/>.</summary>
    public string DisplayName(string speaker) =>
        SpeakerNames.TryGetValue(speaker, out var name) && name.Trim() is { Length: > 0 } trimmed
            ? trimmed
            : DefaultName(speaker);

    /// <summary><c>"Me"</c> for <c>"me"</c>, <c>"Speaker N"</c> for <c>"sN"</c>, else the raw id.</summary>
    public static string DefaultName(string speaker)
    {
        if (speaker == Speaker.Me) return RecordingSession.Channel.MicSpeaker;
        if (speaker.StartsWith('s') && int.TryParse(speaker.AsSpan(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) return $"Speaker {number}";
        return speaker;
    }

    /// <summary>
    /// Writes <c>transcript.json</c> atomically into <paramref name="folder"/>, creating it. <paramref name="keepingPrevious"/>
    /// first copies the one it replaces to the next <c>transcripts/v&lt;number&gt;.json</c>; the copy is removed again
    /// when the write fails.
    /// </summary>
    public void Write(string folder, bool keepingPrevious = false)
    {
        var data = SwiftJson.Serialize(this);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, FileName);
        string? kept = null;
        if (keepingPrevious && File.Exists(file))
        {
            Directory.CreateDirectory(Path.Combine(folder, VersionsFolderName));
            var files = KeptFiles(folder);
            var copy = Path.Combine(folder, VersionsFolderName, $"v{(files.Count > 0 ? files[^1].Number : 0) + 1}.json");
            File.Copy(file, copy, overwrite: false);
            kept = copy;
        }
        try
        {
            AtomicFile.Write(file, data);
        }
        catch
        {
            if (kept is not null)
            {
                try { File.Delete(kept); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    public static Transcript Read(string folder) => SwiftJson.Read<Transcript>(Path.Combine(folder, FileName));

    /// <summary>
    /// Every kept transcript, oldest first, then <c>transcript.json</c> as the newest. A <c>transcript.json</c> saved
    /// before versions existed is version 1. Empty when there is no transcript.
    /// </summary>
    public static List<TranscriptVersion> ReadVersions(string folder)
    {
        Transcript newest;
        try
        {
            newest = Read(folder);
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return [];
        }
        var files = KeptFiles(folder);
        var versions = new List<TranscriptVersion>();
        foreach (var (number, path) in files)
        {
            try
            {
                versions.Add(new TranscriptVersion(number, SwiftJson.Read<Transcript>(path)));
            }
            catch (Exception e) when (IsReadFailure(e))
            {
                // An unreadable one still holds its number (KeptFiles), it is just not listed.
            }
        }
        versions.Add(new TranscriptVersion((files.Count > 0 ? files[^1].Number : 0) + 1, newest));
        return versions;
    }

    /// <summary>The <c>transcripts/v&lt;number&gt;.json</c> files, by number. An unreadable one still holds its number.</summary>
    private static List<(int Number, string Path)> KeptFiles(string folder)
    {
        var versions = Path.Combine(folder, VersionsFolderName);
        string[] paths;
        try
        {
            paths = Directory.GetFiles(versions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var kept = new List<(int Number, string Path)>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('v') && name.EndsWith(".json", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(1, name.Length - 6), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 && name == $"v{number}.json")
                kept.Add((number, path));
        }
        kept.Sort((a, b) => a.Number.CompareTo(b.Number));
        return kept;
    }

    private static bool IsReadFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException;
}
