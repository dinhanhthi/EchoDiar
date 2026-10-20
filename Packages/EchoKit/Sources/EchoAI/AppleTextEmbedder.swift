import EchoCore
import Foundation
import NaturalLanguage

/// The "Basic" built-in embedder: Apple's on-device `NLContextualEmbedding` for Latin-script languages, with no model
/// to download from the app (the system fetches its assets once). It is a token model, not trained for retrieval:
/// passages are the mean of their token vectors, so ranking is rougher than with a real embedding model.
public struct AppleTextEmbedder: TextEmbedder {
    public let key = "apple-nl"
    public let isLocal = true

    public init() {}

    public func withLoaded<T: Sendable>(_ body: @Sendable (any LoadedTextEmbedder) async throws -> T) async throws -> T {
        guard let embedding = NLContextualEmbedding(script: .latin) else {
            throw LLMError.provider("Apple's text embedding model is not available on this Mac.")
        }
        if !embedding.hasAvailableAssets {
            let result = try await embedding.requestAssets()
            guard result == .available else {
                throw LLMError.provider(
                    "Apple's text embedding model could not be downloaded. Check the internet connection and try again.")
            }
        }
        try embedding.load()
        defer { embedding.unload() }
        return try await body(Loaded(model: Model(embedding: embedding)))
    }

    /// `@unchecked Sendable`: `NLContextualEmbedding` is not `Sendable`. It lives only inside one `withLoaded` call,
    /// which the caller runs under `ModelLoadGate`, and `embed` calls are awaited one after another.
    private final class Model: @unchecked Sendable {
        let embedding: NLContextualEmbedding

        init(embedding: NLContextualEmbedding) {
            self.embedding = embedding
        }

        /// The mean of the token vectors of `text`, L2-normalised; a zero vector for empty text.
        func vector(_ text: String) throws -> [Float] {
            let dimension = embedding.dimension
            guard !text.isEmpty else { return [Float](repeating: 0, count: dimension) }
            let result = try embedding.embeddingResult(for: text, language: nil)
            var sum = [Double](repeating: 0, count: dimension)
            var count = 0
            result.enumerateTokenVectors(in: text.startIndex..<text.endIndex) { vector, _ in
                for (i, value) in vector.prefix(dimension).enumerated() { sum[i] += value }
                count += 1
                return true
            }
            guard count > 0 else { return [Float](repeating: 0, count: dimension) }
            return EmbeddingMath.normalized(sum.map { Float($0 / Double(count)) })
        }
    }

    private struct Loaded: LoadedTextEmbedder {
        let model: Model

        /// `query` is unused: the NL model takes no query/passage prefix.
        func embed(_ texts: [String], query: Bool) async throws -> [[Float]] {
            try texts.map { try model.vector($0) }
        }
    }
}
