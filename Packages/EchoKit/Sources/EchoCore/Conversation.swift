import Foundation

/// The `conversation.json` file format, version 1, the library metadata of one recording folder.
/// `id` and `createdAt` are required; every other key is optional when read (`decodeIfPresent` + default).
public struct Conversation: Codable, Equatable, Identifiable, Sendable {
    public enum Status: String, Codable, Sendable {
        case recording, recovered, compressing, compressionFailed, ready, transcribing, transcribed, failed
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
