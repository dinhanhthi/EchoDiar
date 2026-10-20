import EchoCore
import Foundation

/// A text embedding backend (MLX, Apple NaturalLanguage, an API). Local embedders are loaded inside
/// `ModelLoadGate` (owner `.embedder`) by the caller, which holds the gate for one batch of `withLoaded`.
public protocol TextEmbedder: Sendable {
    /// Folder-safe id that includes the model (`mlx-qwen3-embedding-0.6b`, `apple-nl`, `api-<provider id>-<model>`),
    /// so vectors of different models never share an index folder.
    var key: String { get }
    /// Whether no text leaves the Mac (in-app models, loopback hosts).
    var isLocal: Bool { get }
    /// Loads the model, runs `body` with it, then unloads it. Returns (or throws) only once the model is unloaded,
    /// also when `body` throws or the task is cancelled.
    func withLoaded<T: Sendable>(_ body: @Sendable (any LoadedTextEmbedder) async throws -> T) async throws -> T
}

/// A loaded embedder, valid inside `TextEmbedder.withLoaded`.
public protocol LoadedTextEmbedder: Sendable {
    /// One L2-normalised vector per text, in the order of `texts`. `query` is true for a search question, false for
    /// indexed passages (models with query/passage prefixes use it).
    func embed(_ texts: [String], query: Bool) async throws -> [[Float]]
}

public enum EmbeddingMath {
    /// `v` scaled to unit length; a zero vector is returned unchanged.
    public static func normalized(_ v: [Float]) -> [Float] {
        let norm = v.reduce(0) { $0 + $1 * $1 }.squareRoot()
        return norm > 0 ? v.map { $0 / norm } : v
    }
}

/// An OpenAI-compatible or Ollama embeddings endpoint. Nothing to load: `withLoaded` runs `body` with the client.
public struct APITextEmbedder: TextEmbedder {
    public let key: String
    public let isLocal: Bool
    private let client: OpenAICompatibleClient
    private let model: String

    public init(config: ProviderConfig, apiKey: String?, model: String) {
        client = OpenAICompatibleClient(config: config, apiKey: apiKey)
        self.model = model
        key = "api-" + Self.folderSafe(config.id + "-" + model)
        let host = URL(string: config.baseURL.trimmingCharacters(in: .whitespacesAndNewlines))?.host()?.lowercased()
        isLocal = ["localhost", "127.0.0.1", "::1"].contains(host ?? "")
    }

    public func withLoaded<T: Sendable>(_ body: @Sendable (any LoadedTextEmbedder) async throws -> T) async throws -> T {
        try await body(Loaded(client: client, model: model))
    }

    /// Lowercased, every character outside `[a-z0-9._-]` replaced by `-`.
    private static func folderSafe(_ text: String) -> String {
        let allowed = Set("abcdefghijklmnopqrstuvwxyz0123456789._-")
        return String(text.lowercased().map { allowed.contains($0) ? $0 : "-" })
    }

    private struct Loaded: LoadedTextEmbedder {
        let client: OpenAICompatibleClient
        let model: String

        /// `query` is unused: API models take no query/passage prefix here.
        func embed(_ texts: [String], query: Bool) async throws -> [[Float]] {
            try await client.embed(texts, model: model)
        }
    }
}
