using System.Globalization;
using System.Text.Json.Serialization;
using EchoDiar.Core.Json;

namespace EchoDiar.Core.Model;

/// <summary>
/// The archived chats of one conversation: <c>chats/&lt;id&gt;.json</c> (a <see cref="ChatLog"/>) each, listed in
/// <c>chats/chats.json</c>. <c>chat.json</c> stays the open chat. Writes put the new copy first and delete last: a crash
/// may duplicate a chat, never lose it.
/// </summary>
public static class ChatArchive
{
    /// <summary>One archived chat as listed in <c>chats.json</c>. Every key is required on read (synthesized Codable).</summary>
    public sealed record Entry
    {
        [JsonPropertyName("id")] public required Guid Id { get; set; }
        [JsonPropertyName("title")] public required string Title { get; set; }
        [JsonPropertyName("archivedAt"), JsonConverter(typeof(Iso8601SecondsConverter))]
        public required DateTimeOffset ArchivedAt { get; set; }
        [JsonPropertyName("messageCount")] public required int MessageCount { get; set; }
    }

    /// <summary>The result of archiving the open chat; <see cref="NeedsTitle"/> when it had no title, so the entry's title is the fallback.</summary>
    public sealed record Archived(Entry Entry, bool NeedsTitle);

    /// <summary>The <c>chats.json</c> file format, version 1. Dates are ISO 8601. Both keys are required (synthesized Codable).</summary>
    public sealed record Index
    {
        [JsonPropertyName("version"), JsonRequired] public int Version { get; set; } = 1;
        [JsonPropertyName("chats")] public required List<Entry> Chats { get; set; }
    }

    public const string FolderName = "chats";
    public const string IndexFileName = "chats.json";

    /// <summary>Newest first, as stored; empty when there is none or the index is unreadable.</summary>
    public static List<Entry> Entries(string folder)
    {
        try
        {
            return SwiftJson.Read<Index>(Path.Combine(Chats(folder), IndexFileName)).Chats;
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return [];
        }
    }

    /// <summary>The archived chat <paramref name="id"/>; null when it is gone or unreadable.</summary>
    public static ChatLog? Log(Guid id, string folder)
    {
        try
        {
            return ChatLog.Decode(File.ReadAllBytes(FilePath(id, folder)));
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return null;
        }
    }

    /// <summary>Every archived message, for search.</summary>
    public static List<ChatLog.Message> Messages(string folder) =>
        Entries(folder).SelectMany(entry => Log(entry.Id, folder)?.Messages ?? []).ToList();

    /// <summary>
    /// Moves <c>chat.json</c> into the archive; null when it has no messages. Its title, else its first question on one
    /// line, at most 60 characters.
    /// </summary>
    public static Archived? Archive(string folder)
    {
        var chat = ChatLog.Read(folder);
        if (chat.Messages.Count == 0) return null;
        var entry = new Entry
        {
            Id = Guid.NewGuid(),
            Title = chat.Title ?? FallbackTitle(chat),
            // Whole seconds, as stored in ISO 8601, so the entry returned equals the one read back.
            ArchivedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            MessageCount = chat.Messages.Count,
        };
        Directory.CreateDirectory(Chats(folder));
        AtomicFile.Write(FilePath(entry.Id, folder), chat.Encoded());
        WriteIndex([entry, .. StoredEntries(folder)], folder);
        File.Delete(Path.Combine(folder, ChatLog.FileName));
        return new Archived(entry, chat.Title is null);
    }

    /// <summary>
    /// Makes the archived chat <paramref name="id"/> the open <c>chat.json</c>, with its entry's title unless that is
    /// still the fallback (so it gets named when archived again), after archiving a non-empty open chat (returned; null
    /// when it was empty). An unknown <paramref name="id"/> changes nothing.
    /// </summary>
    public static Archived? Resume(Guid id, string folder)
    {
        var entry = Entries(folder).FirstOrDefault(e => e.Id == id);
        if (entry is null || Log(id, folder) is not { } chat) return null;
        var archived = Archive(folder);
        chat.Title = entry.Title == FallbackTitle(chat) ? null : entry.Title;
        chat.Write(folder);
        WriteIndex(StoredEntries(folder).Where(e => e.Id != id).ToList(), folder);
        File.Delete(FilePath(id, folder));
        return archived;
    }

    /// <summary>Renames the archived chat <paramref name="id"/>; does nothing when it or the folder is gone.</summary>
    public static void SetTitle(string title, Guid id, string folder)
    {
        var all = StoredEntries(folder);
        var index = all.FindIndex(e => e.Id == id);
        if (index < 0) return;
        all[index] = all[index] with { Title = title };
        WriteIndex(all, folder);
    }

    /// <summary>
    /// Deletes the archived chat <paramref name="id"/>: the index first, so a crash leaves an unlisted file, never a
    /// listed chat that is gone. An unknown <paramref name="id"/> changes nothing.
    /// </summary>
    public static void Delete(Guid id, string folder)
    {
        var all = StoredEntries(folder);
        if (!all.Exists(e => e.Id == id)) return;
        WriteIndex(all.Where(e => e.Id != id).ToList(), folder);
        try { File.Delete(FilePath(id, folder)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Deletes every archived chat; <c>chat.json</c> stays.</summary>
    public static void DeleteAll(string folder)
    {
        var chats = Chats(folder);
        if (!Directory.Exists(chats)) return;
        Directory.Delete(chats, recursive: true);
    }

    /// <summary>The first question on one line, at most 60 characters (grapheme clusters, as Swift counts them).</summary>
    private static string FallbackTitle(ChatLog chat)
    {
        var question = chat.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var line = string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var length = 0;
        for (var count = 0; count < 60 && length < line.Length; count++)
            length += StringInfo.GetNextTextElementLength(line, length);
        return line[..length];
    }

    private static string Chats(string folder) => Path.Combine(folder, FolderName);

    private static string FilePath(Guid id, string folder) =>
        Path.Combine(Chats(folder), $"{id.ToString("D").ToUpperInvariant()}.json");

    /// <summary>The index to rewrite: empty when missing; throws when unreadable, so a write never drops the chats it lists.</summary>
    private static List<Entry> StoredEntries(string folder)
    {
        var path = Path.Combine(Chats(folder), IndexFileName);
        return File.Exists(path) ? SwiftJson.Read<Index>(path).Chats : [];
    }

    private static void WriteIndex(List<Entry> entries, string folder) =>
        SwiftJson.Write(Path.Combine(Chats(folder), IndexFileName), new Index { Chats = entries });

    private static bool IsReadFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException;
}
