import Foundation

/// The `transcript.json` file format, version 1, written into the conversation folder after transcription.
/// `createdAt` is required; every other key is optional when read (`decodeIfPresent` + default).
public struct Transcript: Codable, Equatable, Sendable {
    public enum Channel: String, Codable, Sendable {
        case mic, system
        /// An unrecognized raw value (written by another build). Written back as "unknown".
        case unknown

        public init(from decoder: Decoder) throws {
            self = Channel(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .unknown
        }
    }

    /// Speaker ids: `"me"` for the mic channel, `"s1"…"sN"` for system-channel speakers.
    public enum Speaker {
        public static let me = "me"

        /// `"s<number>"`, numbered from 1.
        public static func system(_ number: Int) -> String { "s\(number)" }
    }

    public struct Segment: Codable, Equatable, Identifiable, Sendable {
        public var id: UUID
        /// Seconds from the start of the audio file.
        public var start: Double
        public var end: Double
        public var channel: Channel
        public var speaker: String
        public var text: String
        /// BCP-47 code, or nil when unknown.
        public var language: String?

        public init(
            id: UUID = UUID(),
            start: Double,
            end: Double,
            channel: Channel,
            speaker: String,
            text: String,
            language: String? = nil
        ) {
            self.id = id
            self.start = start
            self.end = end
            self.channel = channel
            self.speaker = speaker
            self.text = text
            self.language = language
        }

        private enum CodingKeys: String, CodingKey {
            case id, start, end, channel, speaker, text, language
        }

        public init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
            start = try c.decodeIfPresent(Double.self, forKey: .start) ?? 0
            end = try c.decodeIfPresent(Double.self, forKey: .end) ?? start
            channel = try c.decodeIfPresent(Channel.self, forKey: .channel) ?? .unknown
            speaker = try c.decodeIfPresent(String.self, forKey: .speaker)
                ?? (channel == .mic ? Speaker.me : Speaker.system(1))
            text = try c.decodeIfPresent(String.self, forKey: .text) ?? ""
            language = try c.decodeIfPresent(String.self, forKey: .language)
        }
    }

    public static let fileName = "transcript.json"
    public static let diarizationMissingNote = "diarization model not installed"

    public var version = 1
    public var segments: [Segment]
    /// Display names chosen by the user, by speaker id. Only overrides; see `displayName(for:)`.
    public var speakerNames: [String: String]
    /// Catalog id of the engine that produced the segments.
    public var engineID: String?
    public var modelID: String?
    /// Catalog id of the engine that was selected but could not handle the language; `engineID` replaced it.
    public var engineFallback: String?
    public var notes: [String]
    public var createdAt: Date

    public init(
        segments: [Segment] = [],
        speakerNames: [String: String] = [:],
        engineID: String? = nil,
        modelID: String? = nil,
        engineFallback: String? = nil,
        notes: [String] = [],
        createdAt: Date = Date()
    ) {
        self.segments = segments
        self.speakerNames = speakerNames
        self.engineID = engineID
        self.modelID = modelID
        self.engineFallback = engineFallback
        self.notes = notes
        self.createdAt = createdAt
    }

    /// The user's name for `speaker`, else `"Me"` for `"me"`, `"Speaker N"` for `"sN"`, or the raw id.
    public func displayName(for speaker: String) -> String {
        if let name = speakerNames[speaker]?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty {
            return name
        }
        return Self.defaultName(for: speaker)
    }

    public static func defaultName(for speaker: String) -> String {
        if speaker == Speaker.me { return RecordingSession.Channel.micSpeaker }
        if speaker.hasPrefix("s"), let number = Int(speaker.dropFirst()) { return "Speaker \(number)" }
        return speaker
    }

    private enum CodingKeys: String, CodingKey {
        case version, segments, speakerNames, engineID, modelID, engineFallback, notes, createdAt
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        version = try c.decodeIfPresent(Int.self, forKey: .version) ?? 1
        segments = try c.decodeIfPresent([Segment].self, forKey: .segments) ?? []
        speakerNames = try c.decodeIfPresent([String: String].self, forKey: .speakerNames) ?? [:]
        engineID = try c.decodeIfPresent(String.self, forKey: .engineID)
        modelID = try c.decodeIfPresent(String.self, forKey: .modelID)
        engineFallback = try c.decodeIfPresent(String.self, forKey: .engineFallback)
        notes = try c.decodeIfPresent([String].self, forKey: .notes) ?? []
        createdAt = try c.decode(Date.self, forKey: .createdAt)
    }

    /// Writes `transcript.json` atomically into `folder`, creating it.
    public func write(to folder: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(self)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try data.write(to: folder.appendingPathComponent(Self.fileName), options: .atomic)
    }

    public static func read(from folder: URL) throws -> Transcript {
        try JSONDecoder().decode(Transcript.self, from: Data(contentsOf: folder.appendingPathComponent(fileName)))
    }
}
