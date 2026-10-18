import EchoCore
import Foundation

/// A batch ASR engine for one mono channel. Implementations load their model inside `transcribe`, never
/// download anything, and unload before returning.
public protocol TranscriptionEngine: Sendable {
    /// `channelURL` is a mono 16 kHz Float32 file. Segment times are seconds from the start of that file.
    /// `progress` gets 0...1.
    func transcribe(
        channelURL: URL, languageRanges: [LanguageRange], progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment]
}

/// A stretch of audio with one language setting, built from the session's language markers.
public struct LanguageRange: Codable, Equatable, Sendable {
    /// Seconds from the start of the audio file.
    public var start: Double
    /// nil = to the end of the file.
    public var end: Double?
    /// BCP-47 code; nil = detect per window.
    public var language: String?

    public init(start: Double, end: Double?, language: String?) {
        self.start = start
        self.end = end
        self.language = language
    }

    /// Consecutive markers with the same language are merged; `auto`, empty or no marker mean detection.
    public static func ranges(from markers: [RecordingSession.LanguageMarker]) -> [LanguageRange] {
        var points: [(start: Double, language: String?)] = [(0, nil)]
        for marker in markers.sorted(by: { $0.fileTime < $1.fileTime }) {
            let trimmed = marker.language.trimmingCharacters(in: .whitespacesAndNewlines)
            let language = trimmed.isEmpty || trimmed.lowercased() == "auto" ? nil : trimmed
            let start = max(0, marker.fileTime)
            if start <= points[points.count - 1].start {
                points[points.count - 1].language = language
            } else {
                points.append((start, language))
            }
        }
        var ranges: [LanguageRange] = []
        for point in points where ranges.last.map({ $0.language != point.language }) ?? true {
            ranges.append(LanguageRange(start: point.start, end: nil, language: point.language))
        }
        for index in ranges.indices.dropLast() { ranges[index].end = ranges[index + 1].start }
        return ranges
    }
}

/// One conversation folder to transcribe. Codable so a future XPC worker can take it as is.
public struct TranscriptionJob: Codable, Equatable, Sendable {
    /// The conversation (and session) id.
    public var conversationID: UUID
    /// Holds `session.json`; receives `transcript.json`.
    public var folder: URL
    /// Set when the audio was compressed into two mono files: `session.audioFileName` is then the mic file
    /// and this one the system file. nil: a stereo file (ch0 mic, ch1 system) or a mono file used as system.
    public var systemFileName: String?
    /// Catalog id of the ASR engine; nil = the default resolution.
    public var engineID: String?
    /// "Process now": never deferred for thermal state or Low Power Mode.
    public var processNow: Bool

    public init(
        conversationID: UUID, folder: URL, systemFileName: String? = nil, engineID: String? = nil,
        processNow: Bool = false
    ) {
        self.conversationID = conversationID
        self.folder = folder
        self.systemFileName = systemFileName
        self.engineID = engineID
        self.processNow = processNow
    }
}

public struct JobProgress: Codable, Equatable, Sendable {
    public enum Step: String, Codable, Sendable {
        case queued, deferred, extracting, transcribingMic, transcribingSystem, diarizing, merging
        case finished, needsModel, failed
    }

    public var conversationID: UUID
    public var step: Step
    /// Whole job, 0...1.
    public var fraction: Double
    /// Error text for `failed`, catalog id for `needsModel`.
    public var detail: String?

    public init(conversationID: UUID, step: Step, fraction: Double, detail: String? = nil) {
        self.conversationID = conversationID
        self.step = step
        self.fraction = fraction
        self.detail = detail
    }
}

public enum TranscriptionError: Error, LocalizedError {
    /// The catalog entry is not installed (or its tokenizer is missing).
    case needsModel(String)
    case unknownEngine(String)
    case sessionUnreadable
    case audioUnreadable(String)
    case engineFailed(String)

    public var errorDescription: String? {
        switch self {
        case .needsModel(let id): "Needs model: \(id) is not installed."
        case .unknownEngine(let id): "\(id) is not a registered transcription engine."
        case .sessionUnreadable: "The recording's session.json is missing or unreadable."
        case .audioUnreadable(let detail): "Could not read the audio (\(detail))."
        case .engineFailed(let detail): "Transcription failed (\(detail))."
        }
    }
}
