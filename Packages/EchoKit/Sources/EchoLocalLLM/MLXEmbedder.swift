import EchoAI
import Foundation
import MLX
import MLXEmbedders
import MLXLMCommon

/// A downloaded MLX embedding model run in-process. `withLoaded` loads the model from `folder` (a `ModelStore`
/// folder passed in by the app), then unloads it and empties MLX's buffer cache before it returns. Callers run it
/// inside `ModelLoadGate` (owner `.embedder`): MLX memory settings are process-wide.
public struct MLXEmbedder: TextEmbedder {
    /// Catalog entry id, also the index key.
    public let entryID: String
    public let folder: URL

    public var key: String { entryID }
    public var isLocal: Bool { true }

    public init(entryID: String, folder: URL) {
        self.entryID = entryID
        self.folder = folder
    }

    public func withLoaded<T: Sendable>(_ body: @Sendable (any LoadedTextEmbedder) async throws -> T) async throws -> T {
        let priorCacheLimit = Memory.cacheLimit
        Memory.cacheLimit = MLXProvider.jobCacheLimit
        // Runs once `run` has returned, so after the container (weights) is released.
        defer { MLXProvider.unload(restoring: priorCacheLimit) }
        return try await Self.run(folder: folder, flavor: Flavor(entryID: entryID), body)
    }

    /// Loads the model and runs `body` with it; the container is released when this returns.
    private static func run<T: Sendable>(
        folder: URL, flavor: Flavor, _ body: @Sendable (any LoadedTextEmbedder) async throws -> T
    ) async throws -> T {
        // Local folder only: never the Hugging Face hub loader.
        let container = try await EmbedderModelFactory.shared.loadContainer(
            from: folder, using: TransformersTokenizerLoader())
        try Task.checkCancellation()
        return try await body(Loaded(container: container, flavor: flavor))
    }

    /// Per-model prefixes, pooling and position ids. Set here rather than read from `1_Pooling/config.json`, which
    /// MLXEmbedders 3.31.4 cannot parse for e5 (it then falls back to no pooling for BERT models).
    struct Flavor: Sendable {
        var queryPrefix = ""
        var passagePrefix = ""
        var pooling: Pooling.Strategy = .mean
        /// First position id: XLM-RoBERTa counts positions from `pad_token_id + 1`, BERT from 0.
        var firstPosition = 0
        /// Token the model pools on, appended when the tokenizer leaves it out (Qwen3's `<|endoftext|>`).
        var endToken: String?

        init(entryID: String) {
            switch entryID {
            case "mlx-multilingual-e5-small":
                queryPrefix = "query: "
                passagePrefix = "passage: "
            case "mlx-bge-m3":
                // Raw CLS hidden state: `.cls` would take BertModel's tanh pooler head instead.
                pooling = .first
                firstPosition = 2
            case "mlx-qwen3-embedding-0.6b":
                queryPrefix = "Instruct: Given a question, retrieve conversation passages that answer it\nQuery: "
                pooling = .last
                endToken = "<|endoftext|>"
            default:
                break
            }
        }
    }

    private struct Loaded: LoadedTextEmbedder {
        let container: EmbedderModelContainer
        let flavor: Flavor

        static let maxTokens = 512
        static let batchSize = 16

        func embed(_ texts: [String], query: Bool) async throws -> [[Float]] {
            let prefix = query ? flavor.queryPrefix : flavor.passagePrefix
            var vectors: [[Float]] = []
            vectors.reserveCapacity(texts.count)
            for start in stride(from: 0, to: texts.count, by: Self.batchSize) {
                try Task.checkCancellation()
                let batch = texts[start ..< min(start + Self.batchSize, texts.count)].map { prefix + $0 }
                vectors += await embedBatch(batch)
            }
            return vectors
        }

        /// One model call for up to `batchSize` texts, right-padded to the longest. Padding never reaches a
        /// vector: BERT models take the attention mask and mean pooling weighs by it; Qwen3 attends causally, so
        /// its last real token (picked from the mask) never sees the padding after it.
        private func embedBatch(_ texts: [String]) async -> [[Float]] {
            let flavor = flavor
            return await container.perform { context in
                let tokenizer = context.tokenizer
                let endID = flavor.endToken.flatMap { tokenizer.convertTokenToId($0) }
                let encoded = texts.map { text in
                    var ids = tokenizer.encode(text: text, addSpecialTokens: true)
                    if let endID, ids.last != endID { ids.append(endID) }
                    // Keeps the closing special token (`</s>`, `<|endoftext|>`) the model was trained to end on.
                    if ids.count > Self.maxTokens, let last = ids.last { ids = ids.prefix(Self.maxTokens - 1) + [last] }
                    return ids
                }
                let length = encoded.map(\.count).max() ?? 0
                let padID = tokenizer.eosTokenId ?? 0
                let tokens = MLXArray(encoded.flatMap { $0 + Array(repeating: padID, count: length - $0.count) }
                    .map(Int32.init)).reshaped(encoded.count, length)
                let mask = MLXArray(encoded.flatMap {
                    Array(repeating: Int32(1), count: $0.count) + Array(repeating: Int32(0), count: length - $0.count)
                }).reshaped(encoded.count, length)
                let positions = broadcast(
                    MLXArray(Int32(flavor.firstPosition) ..< Int32(flavor.firstPosition + length)),
                    to: [encoded.count, length])
                let output = context.model(
                    tokens, positionIds: positions, tokenTypeIds: MLXArray.zeros(like: tokens), attentionMask: mask)
                let pooled = Pooling(strategy: flavor.pooling)(output, mask: mask)
                pooled.eval()
                // Normalised in Float32: fp16/bf16 models would leave the norm off by ~0.2%.
                return pooled.map { EmbeddingMath.normalized($0.asArray(Float.self)) }
            }
        }
    }
}
