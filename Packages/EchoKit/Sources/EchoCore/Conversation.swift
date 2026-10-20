import Foundation

/// The `conversation.json` file format, version 1, the library metadata of one recording folder.
/// `id` and `createdAt` are required; every other key is optional when read (`decodeIfPresent` + default).
public struct Conversation: Codable, Equatable, Identifiable, Sendable {
    public enum Status: String, Codable, Sendable {
        /// `failed`: the recording failed. `transcriptionFailed`: the last transcription job failed.
        case recording, recovered, compressing, compressionFailed, ready, transcribing, transcribed, failed
        case transcriptionFailed
        /// A missing or unrecognized raw value (written by another build). Written back as "unknown".
        case unknown

        public init(from decoder: Decoder) throws {
            self = Status(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .unknown
        }
    }

    public static let fileName = "conversation.json"

    public var version = 1
    public var id: UUID
    public var title: String
    /// The user renamed it; otherwise `title` is `defaultTitle`.
    public var customTitle: Bool
    public var createdAt: Date
    /// Seconds of audio.
    public var duration: Double
    public var sourceAppName: String?
    public var sourceBundleID: String?
    public var status: Status
    public var hasTranscript: Bool
    public var hasSummary: Bool
    /// Size on disk of the whole folder when last measured.
    public var sizeBytes: Int64
    /// The recording folder; not stored in the file.
    public var folder: URL
    /// The folder still has audio files; not stored in the file, set by `LibraryStore`.
    public var hasAudio = true

    public init(
        id: UUID,
        title: String? = nil,
        customTitle: Bool = false,
        createdAt: Date,
        duration: Double = 0,
        sourceAppName: String? = nil,
        sourceBundleID: String? = nil,
        status: Status = .ready,
        hasTranscript: Bool = false,
        hasSummary: Bool = false,
        sizeBytes: Int64 = 0,
        folder: URL
    ) {
        self.id = id
        self.title = title ?? Self.defaultTitle(sourceAppName: sourceAppName, createdAt: createdAt)
        self.customTitle = customTitle
        self.createdAt = createdAt
        self.duration = duration
        self.sourceAppName = sourceAppName
        self.sourceBundleID = sourceBundleID
        self.status = status
        self.hasTranscript = hasTranscript
        self.hasSummary = hasSummary
        self.sizeBytes = sizeBytes
        self.folder = folder
    }

    /// Metadata for a folder that has no `conversation.json` yet. `duration` is the audio length.
    public init(session: RecordingSession, folder: URL, duration: Double) {
        let app = session.sourceApps.first
        let status: Status = switch session.state {
        case .recording, .paused: .recording
        case .recovered: .recovered
        case .failed: .failed
        case .stopped: .ready
        case .unknown: .unknown
        }
        self.init(
            id: session.id,
            createdAt: session.startedAt,
            duration: duration,
            sourceAppName: app?.name,
            sourceBundleID: app?.bundleIDs.first,
            status: status,
            folder: folder
        )
    }

    /// `"<Source app> call · 18 Oct 2026, 14:05"`, or `"Recording · …"` without a source app (global tap).
    public static func defaultTitle(sourceAppName: String?, createdAt: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "d MMM yyyy, HH:mm"
        let date = formatter.string(from: createdAt)
        if let name = sourceAppName, !name.isEmpty { return "\(name) call · \(date)" }
        return "Recording · \(date)"
    }

    private enum CodingKeys: String, CodingKey {
        case version, id, title, customTitle, createdAt, duration, sourceAppName, sourceBundleID, status
        case hasTranscript, hasSummary, sizeBytes
    }

    /// Decoding needs the folder: use `read(from:)`. A bare decode leaves `folder` empty.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        version = try c.decodeIfPresent(Int.self, forKey: .version) ?? 1
        id = try c.decode(UUID.self, forKey: .id)
        createdAt = try c.decode(Date.self, forKey: .createdAt)
        sourceAppName = try c.decodeIfPresent(String.self, forKey: .sourceAppName)
        sourceBundleID = try c.decodeIfPresent(String.self, forKey: .sourceBundleID)
        customTitle = try c.decodeIfPresent(Bool.self, forKey: .customTitle) ?? false
        title = try c.decodeIfPresent(String.self, forKey: .title)
            ?? Self.defaultTitle(sourceAppName: sourceAppName, createdAt: createdAt)
        duration = try c.decodeIfPresent(Double.self, forKey: .duration) ?? 0
        status = try c.decodeIfPresent(Status.self, forKey: .status) ?? .unknown
        hasTranscript = try c.decodeIfPresent(Bool.self, forKey: .hasTranscript) ?? false
        hasSummary = try c.decodeIfPresent(Bool.self, forKey: .hasSummary) ?? false
        sizeBytes = try c.decodeIfPresent(Int64.self, forKey: .sizeBytes) ?? 0
        folder = URL(fileURLWithPath: "/")
    }

    /// Writes `conversation.json` atomically into `folder`.
    public func write() throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(self).write(to: folder.appendingPathComponent(Self.fileName), options: .atomic)
    }

    public static func read(from folder: URL) throws -> Conversation {
        var conversation = try JSONDecoder().decode(
            Conversation.self, from: Data(contentsOf: folder.appendingPathComponent(fileName)))
        conversation.folder = folder
        return conversation
    }

    /// The AI summary (Markdown) next to `conversation.json`: the newest of the kept summaries.
    public static let summaryFileName = "summary.md"
    /// Every summary made, `v<number>.md` with `summaries.json` (`SummaryVersion`s without their text).
    public static let summariesFolderName = "summaries"
    static let summaryIndexFileName = "summaries.json"

    /// The saved summary; nil when there is none.
    public static func readSummary(from folder: URL) -> String? {
        try? String(contentsOf: folder.appendingPathComponent(summaryFileName), encoding: .utf8)
    }

    /// Every kept summary, oldest first. A `summary.md` saved before versions existed is version 1, with no date
    /// or model. Empty when there is no summary.
    public static func readSummaryVersions(from folder: URL) -> [SummaryVersion] {
        let summaries = folder.appendingPathComponent(summariesFolderName, isDirectory: true)
        guard let data = try? Data(contentsOf: summaries.appendingPathComponent(summaryIndexFileName)),
              let index = try? SummaryIndex.decode(data) else {
            return readSummary(from: folder).map { [SummaryVersion(number: 1, text: $0)] } ?? []
        }
        return index.summaries.compactMap { version in
            var version = version
            guard let text = try? String(contentsOf: summaries.appendingPathComponent(version.fileName), encoding: .utf8)
            else { return nil }
            version.text = text
            return version
        }
    }

    /// Keeps `summary` as the next version and as `summary.md`, then sets `hasSummary` in a freshly read
    /// `conversation.json` (`self` when the folder has none yet), so a status written meanwhile survives. `provider`
    /// and `model` made it; `stopped` when it was stopped before its end. Files are written atomically.
    public func writeSummary(
        _ summary: String, provider: String? = nil, model: String? = nil, stopped: Bool = false
    ) throws {
        let summaries = folder.appendingPathComponent(Self.summariesFolderName, isDirectory: true)
        try FileManager.default.createDirectory(at: summaries, withIntermediateDirectories: true)
        var versions = Self.readSummaryVersions(from: folder)
        // A summary from before versions has no file of its own yet.
        for version in versions where !FileManager.default.fileExists(
            atPath: summaries.appendingPathComponent(version.fileName).path) {
            try Data(version.text.utf8).write(to: summaries.appendingPathComponent(version.fileName), options: .atomic)
        }
        let version = SummaryVersion(
            number: (versions.last?.number ?? 0) + 1, createdAt: Date(), provider: provider, model: model,
            stopped: stopped, text: summary)
        try Data(summary.utf8).write(to: summaries.appendingPathComponent(version.fileName), options: .atomic)
        versions.append(version)
        try SummaryIndex(summaries: versions).encoded()
            .write(to: summaries.appendingPathComponent(Self.summaryIndexFileName), options: .atomic)
        try Data(summary.utf8).write(to: folder.appendingPathComponent(Self.summaryFileName), options: .atomic)
        var stored = (try? Conversation.read(from: folder)) ?? self
        stored.hasSummary = true
        try stored.write()
    }
}

/// One kept summary: its number (1, 2, …), when and with what it was made, and its text (in `v<number>.md`, not in
/// `summaries.json`).
public struct SummaryVersion: Codable, Equatable, Sendable {
    public var number: Int
    public var createdAt: Date?
    /// The AI tab provider id, as in `ChatLog.Message`.
    public var provider: String?
    public var model: String?
    /// Stopped before its end: the part streamed until then.
    public var stopped = false
    public var text = ""

    public init(
        number: Int, createdAt: Date? = nil, provider: String? = nil, model: String? = nil, stopped: Bool = false,
        text: String
    ) {
        self.number = number
        self.createdAt = createdAt
        self.provider = provider
        self.model = model
        self.stopped = stopped
        self.text = text
    }

    var fileName: String { "v\(number).md" }

    private enum CodingKeys: String, CodingKey {
        case number, createdAt, provider, model, stopped
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        number = try c.decode(Int.self, forKey: .number)
        createdAt = try c.decodeIfPresent(Date.self, forKey: .createdAt)
        provider = try c.decodeIfPresent(String.self, forKey: .provider)
        model = try c.decodeIfPresent(String.self, forKey: .model)
        stopped = try c.decodeIfPresent(Bool.self, forKey: .stopped) ?? false
    }
}

/// The `summaries.json` file format, version 1. Dates are ISO 8601.
struct SummaryIndex: Codable {
    var version = 1
    var summaries: [SummaryVersion]

    func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(self)
    }

    static func decode(_ data: Data) throws -> SummaryIndex {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(SummaryIndex.self, from: data)
    }
}

/// The `chat.json` file format, version 1: the AI tab's questions and answers about one conversation, oldest first,
/// and an optional `title` (kept when an archived chat is resumed). Dates are ISO 8601. Only `role` and `text` are
/// required in a message.
public struct ChatLog: Codable, Equatable, Sendable {
    public struct Message: Codable, Equatable, Sendable {
        public enum Role: String, Codable, Sendable {
            case user, assistant
        }

        public var role: Role
        public var text: String
        /// The AI tab provider id (`ProviderConfig` id or LLM catalog entry id); answers only.
        public var provider: String?
        public var model: String?
        public var timestamp: Date?
        /// The numbered conversation sources of a library-chat answer; nil elsewhere.
        public var references: [Reference]?

        public init(
            role: Role, text: String, provider: String? = nil, model: String? = nil, timestamp: Date? = Date(),
            references: [Reference]? = nil
        ) {
            self.role = role
            self.text = text
            self.provider = provider
            self.model = model
            self.timestamp = timestamp
            self.references = references
        }
    }

    /// A conversation source of a library-chat answer: `number` is the `n` its citations use.
    public struct Reference: Codable, Equatable, Sendable {
        public var number: Int
        public var conversationID: UUID
        public var title: String

        public init(number: Int, conversationID: UUID, title: String) {
            self.number = number
            self.conversationID = conversationID
            self.title = title
        }
    }

    public static let fileName = "chat.json"

    public var version = 1
    public var title: String?
    public var messages: [Message]

    public init(title: String? = nil, messages: [Message] = []) {
        self.title = title
        self.messages = messages
    }

    private enum CodingKeys: String, CodingKey {
        case version, title, messages
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        version = try c.decodeIfPresent(Int.self, forKey: .version) ?? 1
        title = try c.decodeIfPresent(String.self, forKey: .title)
        messages = try c.decodeIfPresent([Message].self, forKey: .messages) ?? []
    }

    /// An empty log when the file is missing or unreadable.
    public static func read(from folder: URL) -> ChatLog {
        guard let data = try? Data(contentsOf: folder.appendingPathComponent(fileName)) else { return ChatLog() }
        return (try? decode(data)) ?? ChatLog()
    }

    public func write(to folder: URL) throws {
        try encoded().write(to: folder.appendingPathComponent(Self.fileName), options: .atomic)
    }

    public func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(self)
    }

    public static func decode(_ data: Data) throws -> ChatLog {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(ChatLog.self, from: data)
    }
}

/// Paths inside a conversation folder built from untrusted names, such as `session.audioFileName`.
public enum ConversationFiles {
    /// `folder/fileName`, or nil when `fileName` is not a plain name (empty, `.`, `..`, contains `/`), points
    /// outside `folder`, or is a symlink. A missing file is allowed; callers check existence.
    public static func audioURL(in folder: URL, fileName: String) -> URL? {
        guard !fileName.isEmpty, fileName != ".", fileName != "..", !fileName.contains("/") else { return nil }
        let url = folder.appendingPathComponent(fileName, isDirectory: false)
        guard url.lastPathComponent == fileName else { return nil }
        if (try? url.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink == true { return nil }
        // Paths, not URLs: a folder URL may or may not end with a slash. The parent is standardized again because
        // standardizing strips a leading `/private` only from paths that exist, and the file may be missing.
        let parent = url.standardizedFileURL.deletingLastPathComponent().standardizedFileURL
        guard parent.path == folder.standardizedFileURL.path else { return nil }
        return url
    }
}

/// Conversations that started on the same local day.
public struct DayGroup: Equatable, Identifiable, Sendable {
    /// Start of the day.
    public var day: Date
    /// Newest first.
    public var conversations: [Conversation]

    public var id: Date { day }

    public init(day: Date, conversations: [Conversation]) {
        self.day = day
        self.conversations = conversations
    }

    /// Groups by local day, newest day first, each group newest first.
    public static func groups(from conversations: [Conversation], calendar: Calendar = .current) -> [DayGroup] {
        Dictionary(grouping: conversations) { calendar.startOfDay(for: $0.createdAt) }
            .map { DayGroup(day: $0.key, conversations: $0.value.sorted { $0.createdAt > $1.createdAt }) }
            .sorted { $0.day > $1.day }
    }
}
