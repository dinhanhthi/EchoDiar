import EchoCore
import Foundation

/// A model loaded once to transcribe many chunks of 16 kHz mono audio, then unloaded. Not `Sendable` (WhisperKit is
/// not): a session is loaded, used and unloaded inside one nonisolated task, kept as a local there, and never
/// stored in an actor's state or passed across isolation.
protocol TranscriptionSession: AnyObject {
    /// Speech in `samples`, with times in seconds from the chunk's first sample. `language` is a BCP-47 code; nil
    /// detects it.
    func transcribe(_ samples: [Float], language: String?) async throws -> [RawSegment]
    func unload() async
}

enum TranscriptionSessions {
    /// Loads a session for a WhisperKit or Parakeet entry; nil for other runtimes, which only transcribe files.
    static func load(entry: ModelEntry, folder: URL) async throws -> (any TranscriptionSession)? {
        switch entry.runtime {
        case .whisperKit: return try await WhisperKitEngine(entry: entry, folder: folder).session()
        case .fluidAudio: return try await ParakeetEngine(entry: entry, folder: folder).session()
        default: return nil
        }
    }
}
