import EchoCore
import Foundation
import FoundationModels

/// Apple's on-device model (macOS 26, Apple Intelligence on). Create it only when `PlatformFeatures.foundationModels`.
/// Every `generate` call gets a new `LanguageModelSession`, so each map call starts with a fresh context window.
@available(macOS 26, *)
public struct AppleFoundationModelProvider: LocalLLMProvider {
    /// The only model; matches the catalog entry id.
    public static let modelID = "apple-foundation-models"
    static let fallbackContext = 4096

    public init() {}

    /// Why the model cannot run on this Mac right now; nil when it is available.
    public static var unavailableReason: String? {
        guard PlatformFeatures.foundationModels else { return "Apple Foundation Models need macOS 26." }
        return switch SystemLanguageModel.default.availability {
        case .available:
            nil
        case .unavailable(.deviceNotEligible):
            "This Mac does not support Apple Intelligence."
        case .unavailable(.appleIntelligenceNotEnabled):
            "Apple Intelligence is turned off (System Settings › Apple Intelligence & Siri)."
        case .unavailable(.modelNotReady):
            "The Apple Intelligence model is still downloading. Try again later."
        case .unavailable:
            "Apple Foundation Models are not available on this Mac."
        }
    }

    /// Whether the model can write in `language` (BCP-47, e.g. "vi"); callers fall back to English otherwise.
    public func supports(language: String) -> Bool {
        SystemLanguageModel.default.supportsLocale(Locale(identifier: language))
    }

    /// The system loads and unloads the model; there is nothing to release here.
    public func withLoadedModel<T: Sendable>(
        model: String, _ body: @Sendable (any LocalLLMGenerator) async throws -> T
    ) async throws -> T {
        if let reason = Self.unavailableReason { throw LLMError.provider(reason) }
        return try await body(Generator())
    }

    private struct Generator: LocalLLMGenerator {
        func generate(
            messages: [LLMMessage], maxTokens: Int, onText: @escaping @Sendable (String) -> Void
        ) async throws -> String {
            let instructions = messages.filter { $0.role == .system }.map(\.content).joined(separator: "\n\n")
            let session = LanguageModelSession(instructions: instructions.isEmpty ? nil : instructions)
            let response = session.streamResponse(
                to: AppleFoundationModelProvider.prompt(messages),
                options: GenerationOptions(maximumResponseTokens: maxTokens))
            // Snapshots carry the whole answer so far; pass on only what is new. The last snapshot is the answer.
            var sent = ""
            for try await snapshot in response {
                let text = snapshot.content
                if let delta = SnapshotDelta.delta(from: sent, to: text) { onText(delta) }
                sent = text
            }
            return sent
        }
    }

    public func listModels() async throws -> [String] {
        [Self.modelID]
    }

    /// `contextSize` is back-deployed: 4096 before macOS 27, the model's real window after.
    public func contextWindow(model: String) async -> Int {
        let size = SystemLanguageModel.default.contextSize
        return size > 0 ? size : Self.fallbackContext
    }

    /// The non-system messages: a single one as is, a conversation with role labels.
    static func prompt(_ messages: [LLMMessage]) -> String {
        let turns = messages.filter { $0.role != .system }
        guard turns.count > 1 else { return turns.first?.content ?? "" }
        return turns.map { "\($0.role == .user ? "User" : "Assistant"): \($0.content)" }.joined(separator: "\n\n")
    }
}

/// The new text of a cumulative snapshot stream.
enum SnapshotDelta {
    /// The unicode scalars `snapshot` adds after `previous`; nil when it adds none or does not extend `previous`.
    /// Scalars, not Characters: a token boundary can split a grapheme cluster (a combining mark, a ZWJ emoji), and
    /// the extended cluster would no longer match `previous` as a Character prefix.
    static func delta(from previous: String, to snapshot: String) -> String? {
        let old = previous.unicodeScalars
        let new = snapshot.unicodeScalars
        guard new.count > old.count, new.starts(with: old) else { return nil }
        var added = String.UnicodeScalarView()
        added.append(contentsOf: new.dropFirst(old.count))
        return String(added)
    }
}
