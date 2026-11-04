import Foundation

/// One catch-up summary made while recording: the meeting so far, up to the live line `throughLineID`.
public struct CatchUp: Codable, Equatable, Identifiable, Sendable {
    public var id: UUID
    public var createdAt: Date
    /// Seconds of recording when it was asked for.
    public var elapsed: Double
    /// The newest live line it covers; the next incremental catch-up sends the lines after it. -1 = none.
    public var throughLineID: Int
    /// Seconds from the recording start of that line.
    public var throughTime: Double
    /// Made from all the lines, without a previous catch-up (the first one, or the "from the start" setting).
    public var fullPass: Bool
    /// Markdown.
    public var text: String
    /// The provider id (`ProviderConfig` id) and model that wrote it.
    public var provider: String
    public var model: String
    /// Stopped before the end: `text` is partial, and the next catch-up does not build on it.
    public var stopped: Bool

    public init(
        id: UUID = UUID(), createdAt: Date = Date(), elapsed: Double, throughLineID: Int, throughTime: Double,
        fullPass: Bool, text: String, provider: String, model: String, stopped: Bool = false
    ) {
        self.id = id
        self.createdAt = createdAt
        self.elapsed = elapsed
        self.throughLineID = throughLineID
        self.throughTime = throughTime
        self.fullPass = fullPass
        self.text = text
        self.provider = provider
        self.model = model
        self.stopped = stopped
    }

    /// The longest `elapsed` and `throughTime` read from a file: 7 days.
    public static let maxSeconds: Double = 7 * 24 * 3600

    private enum CodingKeys: String, CodingKey {
        case id, createdAt, elapsed, throughLineID, throughTime, fullPass, text, provider, model, stopped
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        createdAt = try c.decodeIfPresent(Date.self, forKey: .createdAt) ?? Date(timeIntervalSince1970: 0)
        // The file is untrusted: times finite within [0, maxSeconds] (formatting a huge one traps), a line id from -1.
        elapsed = Self.seconds(try c.decodeIfPresent(Double.self, forKey: .elapsed) ?? 0)
        throughLineID = max(try c.decodeIfPresent(Int.self, forKey: .throughLineID) ?? -1, -1)
        throughTime = Self.seconds(try c.decodeIfPresent(Double.self, forKey: .throughTime) ?? 0)
        fullPass = try c.decodeIfPresent(Bool.self, forKey: .fullPass) ?? false
        text = try c.decodeIfPresent(String.self, forKey: .text) ?? ""
        provider = try c.decodeIfPresent(String.self, forKey: .provider) ?? ""
        model = try c.decodeIfPresent(String.self, forKey: .model) ?? ""
        stopped = try c.decodeIfPresent(Bool.self, forKey: .stopped) ?? false
    }

    /// `value` within [0, `maxSeconds`]; 0 when it is not finite.
    private static func seconds(_ value: Double) -> Double {
        value.isFinite ? min(max(value, 0), maxSeconds) : 0
    }
}

/// The `catchups.json` file format, version 1: a recording's catch-ups, oldest first. Dates are ISO 8601 (whole
/// seconds). Every key is optional when read (`decodeIfPresent` + default), an entry that does not decode is skipped,
/// and one whose id repeats an earlier entry's is dropped (the first is kept), so ids are unique.
public struct CatchUpLog: Codable, Equatable, Sendable {
    public static let fileName = "catchups.json"

    public var version = 1
    public var catchUps: [CatchUp]

    public init(catchUps: [CatchUp] = []) {
        self.catchUps = catchUps
    }

    private enum CodingKeys: String, CodingKey {
        case version, catchUps
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        version = try c.decodeIfPresent(Int.self, forKey: .version) ?? 1
        let decoded = try c.decodeIfPresent([Lossy].self, forKey: .catchUps)?.compactMap(\.catchUp) ?? []
        var seen = Set<UUID>()
        catchUps = decoded.filter { seen.insert($0.id).inserted }
    }

    /// One entry of `catchUps`: nil when it does not decode, so one bad entry does not lose the others.
    private struct Lossy: Decodable {
        let catchUp: CatchUp?

        init(from decoder: Decoder) throws {
            catchUp = try? CatchUp(from: decoder)
        }
    }

    /// The newest catch-up that was not stopped: the base of an incremental catch-up.
    public var base: CatchUp? { catchUps.last { !$0.stopped } }

    /// Empty when the file is missing; throws when it is unreadable, so an append never drops the catch-ups it holds.
    public static func load(from folder: URL) throws -> CatchUpLog {
        let url = folder.appendingPathComponent(fileName)
        guard FileManager.default.fileExists(atPath: url.path) else { return CatchUpLog() }
        return try decode(Data(contentsOf: url))
    }

    public mutating func append(_ catchUp: CatchUp) {
        catchUps.append(catchUp)
    }

    public func save(to folder: URL) throws {
        try encoded().write(to: folder.appendingPathComponent(Self.fileName), options: .atomic)
    }

    public func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(self)
    }

    public static func decode(_ data: Data) throws -> CatchUpLog {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(CatchUpLog.self, from: data)
    }
}

/// One finalized live line, the input of a catch-up (not stored).
public struct CatchUpLine: Equatable, Identifiable, Sendable {
    /// Same raw values as `RecordingTranscriber.Speaker`.
    public enum Speaker: String, Sendable {
        case me = "Me", others = "Others"
    }

    /// The live line id: increases by one, never reused within a recording.
    public var id: Int
    /// Seconds from the recording start.
    public var start: TimeInterval
    public var speaker: Speaker
    public var text: String

    public init(id: Int, start: TimeInterval, speaker: Speaker, text: String) {
        self.id = id
        self.start = start
        self.speaker = speaker
        self.text = text
    }
}
