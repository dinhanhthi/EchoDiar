import Foundation

/// The `session.json` file format, version 1, written next to the audio file of every recording.
/// `id`, `pid` and `startedAt` are required; every other key is optional when read, so new fields must
/// follow the same `decodeIfPresent` + default rule.
public struct RecordingSession: Codable, Equatable, Sendable {
    public enum State: String, Codable, Sendable {
        case recording, paused, stopped, recovered, failed
        /// A missing or unrecognized raw value (written by another build). Written back as "unknown".
        case unknown

        public init(from decoder: Decoder) throws {
            self = State(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .unknown
        }
    }

    public enum TapMode: String, Codable, Sendable {
        case perApp, global
        /// An unrecognized raw value. Written back as "unknown".
        case unknown

        public init(from decoder: Decoder) throws {
            self = TapMode(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .unknown
        }
    }

    /// Channel indexes in the audio file: ch0 = microphone (speaker "Me"), ch1 = system audio.
    public enum Channel {
        public static let mic = 0
        public static let system = 1
        public static let micSpeaker = "Me"
    }

    public struct SourceApp: Codable, Equatable, Sendable {
        public var name: String
        public var bundleIDs: [String]
        public var pids: [Int32]

        public init(name: String, bundleIDs: [String] = [], pids: [Int32] = []) {
            self.name = name
            self.bundleIDs = bundleIDs
            self.pids = pids
        }
    }

    /// A pause in wall-clock time; `fileTime` is the audio file position (seconds) where it happened.
    public struct PauseInterval: Codable, Equatable, Sendable {
        public var fileTime: Double
        public var start: Date
        /// `nil` while still paused.
        public var end: Date?

        public init(fileTime: Double, start: Date, end: Date? = nil) {
            self.fileTime = fileTime
            self.start = start
            self.end = end
        }
    }

    public struct LanguageMarker: Codable, Equatable, Sendable {
        /// Seconds from the start of the audio file.
        public var fileTime: Double
        /// "auto" or a BCP-47 code.
        public var language: String

        public init(fileTime: Double, language: String) {
            self.fileTime = fileTime
            self.language = language
        }
    }

    public static let fileName = "session.json"

    public var version = 1
    public var id: UUID
    /// The process that is recording; recovery treats sessions of a dead owner as interrupted.
    public var pid: Int32
    public var state: State
    public var startedAt: Date
    public var sourceApps: [SourceApp]
    public var tapMode: TapMode
    /// An app was picked but its audio could not be isolated, so every process except EchoDiar was recorded
    /// (`tapMode` is `global` while `sourceApps` lists only that app).
    public var globalTapFallback: Bool
    public var micDeviceName: String?
    public var outputDeviceName: String?
    /// Channel names by index, see `Channel`.
    public var channels: [String]
    public var sampleRate: Double
    public var sampleFormat: String
    public var pauses: [PauseInterval]
    public var languageMarkers: [LanguageMarker]
    public var endReason: String?
    public var audioFileName: String
    /// Recorded by the DEBUG `--capture-probe` harness.
    public var probe: Bool

    public init(
        id: UUID = UUID(),
        pid: Int32 = ProcessInfo.processInfo.processIdentifier,
        state: State = .recording,
        startedAt: Date = Date(),
        sourceApps: [SourceApp] = [],
        tapMode: TapMode = .global,
        globalTapFallback: Bool = false,
        micDeviceName: String? = nil,
        outputDeviceName: String? = nil,
        languageMarkers: [LanguageMarker] = [],
        audioFileName: String = "audio.caf",
        probe: Bool = false
    ) {
        self.id = id
        self.pid = pid
        self.state = state
        self.startedAt = startedAt
        self.sourceApps = sourceApps
        self.tapMode = tapMode
        self.globalTapFallback = globalTapFallback
        self.micDeviceName = micDeviceName
        self.outputDeviceName = outputDeviceName
        channels = ["mic", "system"]
        sampleRate = 16_000
        sampleFormat = "int16"
        pauses = []
        self.languageMarkers = languageMarkers
        endReason = nil
        self.audioFileName = audioFileName
        self.probe = probe
    }

    private enum CodingKeys: String, CodingKey {
        case version, id, pid, state, startedAt, sourceApps, tapMode, globalTapFallback, micDeviceName, outputDeviceName
        case channels, sampleRate, sampleFormat, pauses, languageMarkers, endReason, audioFileName, probe
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        version = try c.decodeIfPresent(Int.self, forKey: .version) ?? 1
        id = try c.decode(UUID.self, forKey: .id)
        pid = try c.decode(Int32.self, forKey: .pid)
        state = try c.decodeIfPresent(State.self, forKey: .state) ?? .unknown
        startedAt = try c.decode(Date.self, forKey: .startedAt)
        sourceApps = try c.decodeIfPresent([SourceApp].self, forKey: .sourceApps) ?? []
        tapMode = try c.decodeIfPresent(TapMode.self, forKey: .tapMode) ?? .unknown
        globalTapFallback = try c.decodeIfPresent(Bool.self, forKey: .globalTapFallback) ?? false
        micDeviceName = try c.decodeIfPresent(String.self, forKey: .micDeviceName)
        outputDeviceName = try c.decodeIfPresent(String.self, forKey: .outputDeviceName)
        channels = try c.decodeIfPresent([String].self, forKey: .channels) ?? ["mic", "system"]
        sampleRate = try c.decodeIfPresent(Double.self, forKey: .sampleRate) ?? 16_000
        sampleFormat = try c.decodeIfPresent(String.self, forKey: .sampleFormat) ?? "int16"
        pauses = try c.decodeIfPresent([PauseInterval].self, forKey: .pauses) ?? []
        languageMarkers = try c.decodeIfPresent([LanguageMarker].self, forKey: .languageMarkers) ?? []
        endReason = try c.decodeIfPresent(String.self, forKey: .endReason)
        audioFileName = try c.decodeIfPresent(String.self, forKey: .audioFileName) ?? "audio.caf"
        probe = try c.decodeIfPresent(Bool.self, forKey: .probe) ?? false
    }

    // MARK: - Storage

    /// `~/Library/Application Support/EchoDiar/Conversations/`, the single root of every recording folder.
    public static var conversationsRoot: URL {
        conversationsRoot(in: FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0])
    }

    /// The conversations root under another Application Support folder (tests).
    public static func conversationsRoot(in applicationSupport: URL) -> URL {
        applicationSupport
            .appendingPathComponent("EchoDiar", isDirectory: true)
            .appendingPathComponent("Conversations", isDirectory: true)
    }

    /// `<yyyy-MM-dd_HHmmss>-<id8>`: `startedAt` in local time, then the first 8 characters of `id`, lowercased.
    public var folderName: String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd_HHmmss"
        return "\(formatter.string(from: startedAt))-\(id.uuidString.prefix(8).lowercased())"
    }

    public func folder(in root: URL = RecordingSession.conversationsRoot) -> URL {
        root.appendingPathComponent(folderName, isDirectory: true)
    }

    /// Writes `session.json` atomically into `folder`, creating it.
    public func write(to folder: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(self)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try data.write(to: folder.appendingPathComponent(Self.fileName), options: .atomic)
    }

    public static func read(from folder: URL) throws -> RecordingSession {
        try JSONDecoder().decode(RecordingSession.self, from: Data(contentsOf: folder.appendingPathComponent(fileName)))
    }
}
