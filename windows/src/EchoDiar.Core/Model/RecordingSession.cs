using System.Globalization;
using System.Text.Json.Serialization;
using EchoDiar.Core.Json;

namespace EchoDiar.Core.Model;

/// <summary>Swift <c>RecordingSession.State</c>. A missing or unrecognized raw value reads as <see cref="Unknown"/>.</summary>
[JsonConverter(typeof(SessionStateConverter))]
public enum SessionState
{
    Recording, Paused, Stopped, Recovered, Failed,
    /// <summary>A missing or unrecognized raw value (written by another build). Written back as "unknown".</summary>
    Unknown,
}

public sealed class SessionStateConverter() : TolerantEnumConverter<SessionState>(SessionState.Unknown);

/// <summary>Swift <c>RecordingSession.TapMode</c>. An unrecognized raw value reads as <see cref="Unknown"/>.</summary>
[JsonConverter(typeof(TapModeConverter))]
public enum TapMode
{
    PerApp, Global,
    /// <summary>An unrecognized raw value. Written back as "unknown".</summary>
    Unknown,
}

public sealed class TapModeConverter() : TolerantEnumConverter<TapMode>(TapMode.Unknown);

/// <summary>
/// The <c>session.json</c> file format, version 1, written next to the audio file of every recording.
/// <c>id</c>, <c>pid</c> and <c>startedAt</c> are required; every other key is optional when read. Property defaults
/// are the read defaults of Swift's <c>init(from:)</c> (<c>state</c> and <c>tapMode</c> are unknown), so set
/// <see cref="State"/> and <see cref="TapMode"/> when creating one.
/// </summary>
public sealed record RecordingSession
{
    /// <summary>Channel indexes in the audio file: ch0 = microphone (speaker "Me"), ch1 = system audio.</summary>
    public static class Channel
    {
        public const int Mic = 0;
        public const int System = 1;
        public const string MicSpeaker = "Me";
    }

    /// <summary>Every key is required on read (synthesized Codable).</summary>
    public sealed record SourceApp
    {
        [JsonPropertyName("name")] public required string Name { get; set; }
        [JsonPropertyName("bundleIDs"), JsonRequired] public List<string> BundleIDs { get; set; } = [];
        [JsonPropertyName("pids"), JsonRequired] public List<int> Pids { get; set; } = [];
    }

    /// <summary>A pause in wall-clock time; <see cref="FileTime"/> is the audio file position (seconds) where it happened.</summary>
    public sealed record PauseInterval
    {
        [JsonPropertyName("fileTime")] public required double FileTime { get; set; }
        [JsonPropertyName("start"), JsonConverter(typeof(AppleDateConverter))]
        public required DateTimeOffset Start { get; set; }
        /// <summary>Null while still paused.</summary>
        [JsonPropertyName("end"), JsonConverter(typeof(AppleDateConverter))]
        public DateTimeOffset? End { get; set; }
    }

    public sealed record LanguageMarker
    {
        /// <summary>Seconds from the start of the audio file.</summary>
        [JsonPropertyName("fileTime")] public required double FileTime { get; set; }
        /// <summary>"auto" or a BCP-47 code.</summary>
        [JsonPropertyName("language")] public required string Language { get; set; }
    }

    public const string FileName = "session.json";

    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("id"), JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The process that is recording; recovery treats sessions of a dead owner as interrupted.</summary>
    [JsonPropertyName("pid"), JsonRequired] public int Pid { get; set; } = Environment.ProcessId;
    [JsonPropertyName("state")] public SessionState State { get; set; } = SessionState.Unknown;

    [JsonPropertyName("startedAt"), JsonConverter(typeof(AppleDateConverter)), JsonRequired]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("sourceApps")] public List<SourceApp> SourceApps { get; set; } = [];
    [JsonPropertyName("tapMode")] public TapMode TapMode { get; set; } = TapMode.Unknown;
    /// <summary>
    /// An app was picked but its audio could not be isolated, so every process except EchoDiar was recorded
    /// (<see cref="TapMode"/> is global while <see cref="SourceApps"/> lists only that app).
    /// </summary>
    [JsonPropertyName("globalTapFallback")] public bool GlobalTapFallback { get; set; }
    [JsonPropertyName("micDeviceName")] public string? MicDeviceName { get; set; }
    [JsonPropertyName("outputDeviceName")] public string? OutputDeviceName { get; set; }
    /// <summary>Channel names by index, see <see cref="Channel"/>.</summary>
    [JsonPropertyName("channels")] public List<string> Channels { get; set; } = ["mic", "system"];
    [JsonPropertyName("sampleRate")] public double SampleRate { get; set; } = 16_000;
    [JsonPropertyName("sampleFormat")] public string SampleFormat { get; set; } = "int16";
    [JsonPropertyName("pauses")] public List<PauseInterval> Pauses { get; set; } = [];
    [JsonPropertyName("languageMarkers")] public List<LanguageMarker> LanguageMarkers { get; set; } = [];
    /// <summary><c>user</c>, <c>diskFull</c>, <c>writeFailed</c> or <c>captureFailed</c>.</summary>
    [JsonPropertyName("endReason")] public string? EndReason { get; set; }
    [JsonPropertyName("audioFileName")] public string AudioFileName { get; set; } = "audio.caf";
    /// <summary>Recorded by the DEBUG <c>--capture-probe</c> harness.</summary>
    [JsonPropertyName("probe")] public bool Probe { get; set; }

    /// <summary><c>&lt;yyyy-MM-dd_HHmmss&gt;-&lt;id8&gt;</c>: <see cref="StartedAt"/> in local time, then the first 8 characters of <see cref="Id"/>, lowercased.</summary>
    [JsonIgnore]
    public string FolderName =>
        $"{StartedAt.ToLocalTime().ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)}-{Id.ToString("D")[..8].ToLowerInvariant()}";

    /// <summary>This session's folder under the conversations root (<see cref="AppPaths.Conversations"/>).</summary>
    public string Folder(string root) => Path.Combine(root, FolderName);

    /// <summary>Writes <c>session.json</c> atomically into <paramref name="folder"/>, creating it.</summary>
    public void Write(string folder)
    {
        var data = SwiftJson.Serialize(this);
        Directory.CreateDirectory(folder);
        AtomicFile.Write(Path.Combine(folder, FileName), data);
    }

    public static RecordingSession Read(string folder) => SwiftJson.Read<RecordingSession>(Path.Combine(folder, FileName));
}
