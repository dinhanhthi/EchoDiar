import Foundation

/// The archived chats of one conversation: `chats/<id>.json` (a `ChatLog`) each, listed in `chats/chats.json`.
/// `chat.json` stays the open chat. Writes put the new copy first and delete last: a crash may duplicate a chat, never
/// lose it.
public enum ChatArchive {
    /// One archived chat as listed in `chats.json`.
    public struct Entry: Codable, Identifiable, Equatable, Sendable {
        public var id: UUID
        public var title: String
        public var archivedAt: Date
        public var messageCount: Int
    }

    /// The result of archiving the open chat; `needsTitle` when it had no title, so `entry.title` is the fallback.
    public typealias Archived = (entry: Entry, needsTitle: Bool)

    public static let folderName = "chats"
    static let indexFileName = "chats.json"

    /// Newest first, as stored; empty when there is none or the index is unreadable.
    public static func entries(in folder: URL) -> [Entry] {
        guard let data = try? Data(contentsOf: chats(in: folder).appendingPathComponent(indexFileName)),
              let index = try? Index.decode(data) else { return [] }
        return index.chats
    }

    /// The archived chat `id`; nil when it is gone or unreadable.
    public static func log(_ id: UUID, in folder: URL) -> ChatLog? {
        guard let data = try? Data(contentsOf: fileURL(id, in: folder)) else { return nil }
        return try? ChatLog.decode(data)
    }

    /// Every archived message, for search.
    public static func messages(in folder: URL) -> [ChatLog.Message] {
        entries(in: folder).flatMap { log($0.id, in: folder)?.messages ?? [] }
    }

    /// Moves `chat.json` into the archive; nil when it has no messages. Its title, else its first question on one
    /// line, at most 60 characters.
    public static func archive(in folder: URL) throws -> Archived? {
        let chat = ChatLog.read(from: folder)
        guard !chat.messages.isEmpty else { return nil }
        let entry = Entry(
            id: UUID(),
            title: chat.title ?? fallbackTitle(chat),
            // Whole seconds, as stored in ISO 8601, so the entry returned equals the one read back.
            archivedAt: Date(timeIntervalSince1970: Date().timeIntervalSince1970.rounded(.down)),
            messageCount: chat.messages.count)
        try FileManager.default.createDirectory(at: chats(in: folder), withIntermediateDirectories: true)
        try chat.encoded().write(to: fileURL(entry.id, in: folder), options: .atomic)
        try writeIndex([entry] + storedEntries(in: folder), in: folder)
        try FileManager.default.removeItem(at: folder.appendingPathComponent(ChatLog.fileName))
        return (entry, chat.title == nil)
    }

    /// Makes the archived chat `id` the open `chat.json`, with its entry's title unless that is still the fallback (so
    /// it gets named when archived again), after archiving a non-empty open chat (returned; nil when it was empty). An
    /// unknown `id` changes nothing.
    public static func resume(_ id: UUID, in folder: URL) throws -> Archived? {
        guard let entry = entries(in: folder).first(where: { $0.id == id }), var chat = log(id, in: folder)
        else { return nil }
        let archived = try archive(in: folder)
        chat.title = entry.title == fallbackTitle(chat) ? nil : entry.title
        try chat.write(to: folder)
        try writeIndex(storedEntries(in: folder).filter { $0.id != id }, in: folder)
        try FileManager.default.removeItem(at: fileURL(id, in: folder))
        return archived
    }

    /// Renames the archived chat `id`; does nothing when it or the folder is gone.
    public static func setTitle(_ title: String, for id: UUID, in folder: URL) throws {
        var all = try storedEntries(in: folder)
        guard let index = all.firstIndex(where: { $0.id == id }) else { return }
        all[index].title = title
        try writeIndex(all, in: folder)
    }

    /// The first question on one line, at most 60 characters.
    private static func fallbackTitle(_ chat: ChatLog) -> String {
        let question = chat.messages.first { $0.role == .user }?.text ?? ""
        return String(question.split(whereSeparator: \.isWhitespace).joined(separator: " ").prefix(60))
    }

    private static func chats(in folder: URL) -> URL {
        folder.appendingPathComponent(folderName, isDirectory: true)
    }

    private static func fileURL(_ id: UUID, in folder: URL) -> URL {
        chats(in: folder).appendingPathComponent("\(id.uuidString).json")
    }

    /// The index to rewrite: empty when missing; throws when unreadable, so a write never drops the chats it lists.
    private static func storedEntries(in folder: URL) throws -> [Entry] {
        let url = chats(in: folder).appendingPathComponent(indexFileName)
        guard FileManager.default.fileExists(atPath: url.path) else { return [] }
        return try Index.decode(Data(contentsOf: url)).chats
    }

    private static func writeIndex(_ entries: [Entry], in folder: URL) throws {
        try Index(chats: entries).encoded()
            .write(to: chats(in: folder).appendingPathComponent(indexFileName), options: .atomic)
    }

    /// The `chats.json` file format, version 1. Dates are ISO 8601.
    struct Index: Codable {
        var version = 1
        var chats: [Entry]

        func encoded() throws -> Data {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            encoder.dateEncodingStrategy = .iso8601
            return try encoder.encode(self)
        }

        static func decode(_ data: Data) throws -> Index {
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            return try decoder.decode(Index.self, from: data)
        }
    }
}
