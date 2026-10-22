using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using EchoDiar.Core.Json;

namespace EchoDiar.Core.Model;

/// <summary>Swift <c>Conversation.Status</c>. An unrecognized raw value reads as <see cref="Unknown"/>.</summary>
[JsonConverter(typeof(ConversationStatusConverter))]
public enum ConversationStatus
{
    Recording, Recovered, Compressing, CompressionFailed, Ready, Transcribing, Transcribed,
    /// <summary>The recording failed.</summary>
    Failed,
    /// <summary>The last transcription job failed.</summary>
    TranscriptionFailed,
    /// <summary>A missing or unrecognized raw value (written by another build). Written back as "unknown".</summary>
    Unknown,
}

public sealed class ConversationStatusConverter() : TolerantEnumConverter<ConversationStatus>(ConversationStatus.Unknown);

/// <summary>
/// The <c>conversation.json</c> file format, version 1, the library metadata of one recording folder.
/// <c>id</c> and <c>createdAt</c> are required; every other key is optional when read. Property defaults are the read
/// defaults of Swift's <c>init(from:)</c> (<c>status</c> is unknown), so set <see cref="Status"/> when creating one.
/// </summary>
public sealed record Conversation
{
    public const string FileName = "conversation.json";
    /// <summary>The AI summary (Markdown) next to <c>conversation.json</c>: the newest of the kept summaries.</summary>
    public const string SummaryFileName = "summary.md";
    /// <summary>Every summary made, <c>v&lt;number&gt;.md</c> with <c>summaries.json</c>.</summary>
    public const string SummariesFolderName = "summaries";
    public const string SummaryIndexFileName = "summaries.json";

    private string? _title;

    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("id")] public required Guid Id { get; set; }

    /// <summary>The user's title, else <see cref="DefaultTitle"/> (also when the key is missing on read).</summary>
    [JsonPropertyName("title")]
    public string Title
    {
        get => _title ?? DefaultTitle(SourceAppName, CreatedAt);
        set => _title = value;
    }

    /// <summary>The user renamed it; otherwise <see cref="Title"/> is <see cref="DefaultTitle"/>.</summary>
    [JsonPropertyName("customTitle")] public bool CustomTitle { get; set; }

    [JsonPropertyName("createdAt"), JsonConverter(typeof(AppleDateConverter))]
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>Seconds of audio.</summary>
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("sourceAppName")] public string? SourceAppName { get; set; }
    [JsonPropertyName("sourceBundleID")] public string? SourceBundleID { get; set; }
    [JsonPropertyName("status")] public ConversationStatus Status { get; set; } = ConversationStatus.Unknown;
    [JsonPropertyName("hasTranscript")] public bool HasTranscript { get; set; }
    [JsonPropertyName("hasSummary")] public bool HasSummary { get; set; }
    /// <summary>Size on disk of the whole folder when last measured.</summary>
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }

    /// <summary>The recording folder; not stored in the file.</summary>
    [JsonIgnore] public string Folder { get; set; } = "";
    /// <summary>The folder still has audio files; not stored in the file, set by the library.</summary>
    [JsonIgnore] public bool HasAudio { get; set; } = true;

    /// <summary>Metadata for a folder that has no <c>conversation.json</c> yet. <paramref name="duration"/> is the audio length.</summary>
    public static Conversation FromSession(RecordingSession session, string folder, double duration)
    {
        var app = session.SourceApps.FirstOrDefault();
        return new Conversation
        {
            Id = session.Id,
            CreatedAt = session.StartedAt,
            Duration = duration,
            SourceAppName = app?.Name,
            SourceBundleID = app?.BundleIDs.FirstOrDefault(),
            Status = session.State switch
            {
                SessionState.Recording or SessionState.Paused => ConversationStatus.Recording,
                SessionState.Recovered => ConversationStatus.Recovered,
                SessionState.Failed => ConversationStatus.Failed,
                SessionState.Stopped => ConversationStatus.Ready,
                _ => ConversationStatus.Unknown,
            },
            Folder = folder,
        };
    }

    /// <summary><c>"&lt;Source app&gt; call · 18 Oct 2026, 14:05"</c>, or <c>"Recording · …"</c> without a source app, in local time.</summary>
    public static string DefaultTitle(string? sourceAppName, DateTimeOffset createdAt)
    {
        var date = createdAt.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(sourceAppName) ? $"Recording · {date}" : $"{sourceAppName} call · {date}";
    }

    /// <summary>Writes <c>conversation.json</c> atomically into <see cref="Folder"/> (which must exist).</summary>
    public void Write() => SwiftJson.Write(Path.Combine(Folder, FileName), this);

    public static Conversation Read(string folder)
    {
        var conversation = SwiftJson.Read<Conversation>(Path.Combine(folder, FileName));
        conversation.Folder = folder;
        return conversation;
    }

    /// <summary>The saved summary; null when there is none.</summary>
    public static string? ReadSummary(string folder) => ReadText(Path.Combine(folder, SummaryFileName));

    /// <summary>
    /// Every kept summary, oldest first. A <c>summary.md</c> saved before versions existed is version 1, with no date
    /// or model. Empty when there is no summary.
    /// </summary>
    public static List<SummaryVersion> ReadSummaryVersions(string folder)
    {
        var summaries = Path.Combine(folder, SummariesFolderName);
        SummaryIndex index;
        try
        {
            index = SwiftJson.Read<SummaryIndex>(Path.Combine(summaries, SummaryIndexFileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return ReadSummary(folder) is { } text ? [new SummaryVersion { Number = 1, Text = text }] : [];
        }
        var versions = new List<SummaryVersion>();
        foreach (var version in index.Summaries)
        {
            if (ReadText(Path.Combine(summaries, version.FileName)) is { } text) versions.Add(version with { Text = text });
        }
        return versions;
    }

    /// <summary>
    /// Keeps <paramref name="summary"/> as the next version and as <c>summary.md</c>, then sets <c>hasSummary</c> in a
    /// freshly read <c>conversation.json</c> (this one when the folder has none yet), so a status written meanwhile
    /// survives. <paramref name="stopped"/> when it was stopped before its end. Files are written atomically.
    /// </summary>
    public void WriteSummary(string summary, string? provider = null, string? model = null, bool stopped = false)
    {
        var summaries = Path.Combine(Folder, SummariesFolderName);
        Directory.CreateDirectory(summaries);
        var versions = ReadSummaryVersions(Folder);
        // A summary from before versions has no file of its own yet.
        foreach (var kept in versions)
        {
            var path = Path.Combine(summaries, kept.FileName);
            if (!File.Exists(path)) AtomicFile.Write(path, Encoding.UTF8.GetBytes(kept.Text));
        }
        var version = new SummaryVersion
        {
            Number = (versions.Count > 0 ? versions[^1].Number : 0) + 1,
            CreatedAt = DateTimeOffset.UtcNow,
            Provider = provider,
            Model = model,
            Stopped = stopped,
            Text = summary,
        };
        var bytes = Encoding.UTF8.GetBytes(summary);
        AtomicFile.Write(Path.Combine(summaries, version.FileName), bytes);
        versions.Add(version);
        SwiftJson.Write(Path.Combine(summaries, SummaryIndexFileName), new SummaryIndex { Summaries = versions });
        AtomicFile.Write(Path.Combine(Folder, SummaryFileName), bytes);
        Conversation stored;
        try
        {
            stored = Read(Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            stored = this;
        }
        (stored with { HasSummary = true }).Write();
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// One kept summary: its number (1, 2, …), when and with what it was made, and its text (in <c>v&lt;number&gt;.md</c>,
/// not in <c>summaries.json</c>).
/// </summary>
public sealed record SummaryVersion
{
    [JsonPropertyName("number")] public required int Number { get; set; }
    [JsonPropertyName("createdAt"), JsonConverter(typeof(Iso8601SecondsConverter))]
    public DateTimeOffset? CreatedAt { get; set; }
    /// <summary>The AI tab provider id, as in <see cref="ChatLog.Message"/>.</summary>
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    /// <summary>Stopped before its end: the part streamed until then.</summary>
    [JsonPropertyName("stopped")] public bool Stopped { get; set; }
    [JsonIgnore] public string Text { get; set; } = "";

    [JsonIgnore] public string FileName => $"v{Number}.md";
}

/// <summary>The <c>summaries/summaries.json</c> file format, version 1. Dates are ISO 8601. Both keys are required (synthesized Codable).</summary>
public sealed record SummaryIndex
{
    [JsonPropertyName("version"), JsonRequired] public int Version { get; set; } = 1;
    [JsonPropertyName("summaries")] public required List<SummaryVersion> Summaries { get; set; }
}

/// <summary>
/// The <c>chat.json</c> file format, version 1: the AI tab's questions and answers about one conversation, oldest first,
/// and an optional <c>title</c> (kept when an archived chat is resumed). Dates are ISO 8601. Only <c>role</c> and
/// <c>text</c> are required in a message.
/// </summary>
public sealed record ChatLog
{
    public const string FileName = "chat.json";

    /// <summary>A message; <see cref="Timestamp"/> is null when missing on read, so set it when creating one.</summary>
    public sealed record Message
    {
        [JsonPropertyName("role")] public required ChatRole Role { get; set; }
        [JsonPropertyName("text")] public required string Text { get; set; }
        /// <summary>The AI tab provider id (provider config id or LLM catalog entry id); answers only.</summary>
        [JsonPropertyName("provider")] public string? Provider { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("timestamp"), JsonConverter(typeof(Iso8601SecondsConverter))]
        public DateTimeOffset? Timestamp { get; set; }
        /// <summary>The numbered conversation sources of a library-chat answer; null elsewhere.</summary>
        [JsonPropertyName("references")] public List<Reference>? References { get; set; }
    }

    /// <summary>A conversation source of a library-chat answer: <see cref="Number"/> is the <c>n</c> its citations use.</summary>
    public sealed record Reference
    {
        [JsonPropertyName("number")] public required int Number { get; set; }
        [JsonPropertyName("conversationID")] public required Guid ConversationID { get; set; }
        [JsonPropertyName("title")] public required string Title { get; set; }
    }

    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("messages")] public List<Message> Messages { get; set; } = [];

    /// <summary>An empty log when the file is missing or unreadable.</summary>
    public static ChatLog Read(string folder)
    {
        try
        {
            return Decode(File.ReadAllBytes(Path.Combine(folder, FileName)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new ChatLog();
        }
    }

    public void Write(string folder) => AtomicFile.Write(Path.Combine(folder, FileName), Encoded());

    public byte[] Encoded() => SwiftJson.Serialize(this);

    public static ChatLog Decode(ReadOnlySpan<byte> utf8) => SwiftJson.Deserialize<ChatLog>(utf8);
}

/// <summary>Swift <c>ChatLog.Message.Role</c>: synthesized, so an unknown role fails the decode.</summary>
[JsonConverter(typeof(ChatRoleConverter))]
public enum ChatRole
{
    [JsonStringEnumMemberName("user")] User,
    [JsonStringEnumMemberName("assistant")] Assistant,
}

public sealed class ChatRoleConverter() : JsonStringEnumConverter<ChatRole>(namingPolicy: null, allowIntegerValues: false);

/// <summary>Paths inside a conversation folder built from untrusted names, such as <c>session.audioFileName</c>.</summary>
public static class ConversationFiles
{
    /// <summary>
    /// <c>folder/fileName</c>, or null when <paramref name="fileName"/> is not a plain name (empty, <c>.</c>, <c>..</c>,
    /// contains <c>/</c>, <c>\</c>, <c>:</c> or NUL, ends with a dot or a space, or is a Windows device name such as
    /// <c>CON</c> or <c>COM1.caf</c>), points outside <paramref name="folder"/>, or is a symlink. The same on every OS,
    /// as the library is shared with macOS. A missing file is allowed; callers check existence.
    /// </summary>
    public static string? AudioPath(string folder, string fileName)
    {
        if (fileName is "" or "." or "..") return null;
        // ':' too: a drive-relative name ("C:x") or an NTFS alternate data stream ("audio.caf:x"). NUL: path APIs throw.
        if (fileName.IndexOfAny(['/', '\\', ':', '\0']) >= 0) return null;
        // Windows drops a trailing dot or space, and opens a device for a reserved name (even with an extension).
        if (fileName[^1] is '.' or ' ' || IsDeviceName(fileName)) return null;
        var path = Path.Combine(folder, fileName);
        if (Path.GetFileName(path) != fileName) return null;
        if (new FileInfo(path).LinkTarget is not null) return null;
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is null || !string.Equals(Trim(parent), Trim(Path.GetFullPath(folder)), PathComparison)) return null;
        return path;
    }

    /// <summary>
    /// Whether Windows opens a device for this path component: CON, PRN, AUX, NUL, COM0-9 and LPT0-9 (and ¹²³),
    /// any case, before the first dot and trailing spaces.
    /// </summary>
    public static bool IsDeviceName(string fileName)
    {
        var dot = fileName.IndexOf('.');
        var stem = (dot < 0 ? fileName : fileName[..dot]).TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && "0123456789¹²³".Contains(stem[3]);
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
}
