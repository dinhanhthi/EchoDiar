import EchoAI
import Foundation
import MLX
import MLXLLM
import MLXLMCommon
import Tokenizers

/// A downloaded MLX model run in-process. `withLoadedModel` loads the model from `folder` (a `ModelStore` folder
/// passed in by the app) once for a whole job, then unloads it and empties MLX's buffer cache before it returns.
/// Callers run it inside `ModelLoadGate` (owner `.llm`): MLX memory settings are process-wide.
public struct MLXProvider: LocalLLMProvider {
    /// Catalog entry id, also the only model `listModels` reports.
    public let entryID: String
    public let folder: URL

    /// Freed GPU buffers MLX keeps for reuse while a job runs (the value mlx-swift-examples' LLMEval uses).
    static let jobCacheLimit = 20 * 1024 * 1024

    public init(entryID: String, folder: URL) {
        self.entryID = entryID
        self.folder = folder
    }

    public func withLoadedModel<T: Sendable>(
        model: String, _ body: @Sendable (any LocalLLMGenerator) async throws -> T
    ) async throws -> T {
        let priorCacheLimit = Memory.cacheLimit
        Memory.cacheLimit = Self.jobCacheLimit
        // Runs once `run` has returned, so after the container (weights, KV cache) is released.
        defer { Self.unload(restoring: priorCacheLimit) }
        return try await Self.run(folder: folder, body)
    }

    public func listModels() async throws -> [String] {
        [entryID]
    }

    /// The model's `max_position_embeddings` (top level or `text_config`), capped by RAM: the KV cache of a long
    /// transcript needs room next to the weights (32K tokens on 16 GB Macs and up, 8K below).
    public func contextWindow(model: String) async -> Int {
        let ramCap = ProcessInfo.processInfo.physicalMemory >= 16 << 30 ? 32_768 : 8_192
        let url = folder.appendingPathComponent("config.json")
        guard let data = try? Data(contentsOf: url),
              let config = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return ramCap }
        let textConfig = config["text_config"] as? [String: Any]
        let modelMax = (config["max_position_embeddings"] ?? textConfig?["max_position_embeddings"]) as? Int
        return min(modelMax ?? ramCap, ramCap)
    }

    /// Loads the model and runs `body` with it; the container is released when this returns.
    private static func run<T: Sendable>(
        folder: URL, _ body: @Sendable (any LocalLLMGenerator) async throws -> T
    ) async throws -> T {
        // Local folder only: never the Hugging Face hub loader.
        let container = try await LLMModelFactory.shared.loadContainer(from: folder, using: TransformersTokenizerLoader())
        try Task.checkCancellation()
        return try await body(Generator(container: container))
    }

    private struct Generator: LocalLLMGenerator {
        let container: ModelContainer

        /// Streams the answer through `onText` and returns once generation has stopped.
        func generate(
            messages: [LLMMessage], maxTokens: Int, onText: @escaping @Sendable (String) -> Void
        ) async throws -> String {
            try await container.perform { context in
                // `enable_thinking` turns off Qwen-style reasoning; templates without it ignore the variable.
                let input = UserInput(
                    chat: messages.map(MLXProvider.chatMessage), additionalContext: ["enable_thinking": false])
                let lmInput = try await context.processor.prepare(input: input)
                // The prompt prefill inside `TokenIterator` cannot be cancelled: skip it once Cancel has come.
                try Task.checkCancellation()
                let iterator = try TokenIterator(
                    input: lmInput, model: context.model, parameters: GenerateParameters(maxTokens: maxTokens))
                let (stream, task) = generateTask(
                    promptTokenCount: lmInput.text.tokens.size, modelConfiguration: context.configuration,
                    tokenizer: context.tokenizer, iterator: iterator)
                // Ends early when this task is cancelled, which cancels `task`; awaiting it makes sure the model is
                // no longer in use before the container is released.
                var text = ""
                for await generation in stream {
                    if let chunk = generation.chunk {
                        text += chunk
                        onText(chunk)
                    }
                }
                await task.value
                return text
            }
        }
    }

    private static func chatMessage(_ message: LLMMessage) -> Chat.Message {
        switch message.role {
        case .system: .system(message.content)
        case .user: .user(message.content)
        case .assistant: .assistant(message.content)
        }
    }

    /// Returns the freed buffers to the system and puts back the cache limit another MLX user may have set.
    static func unload(restoring priorCacheLimit: Int) {
        Memory.clearCache()
        Memory.cacheLimit = priorCacheLimit
    }
}

/// Loads `tokenizer.json` + `tokenizer_config.json` (and `chat_template.jinja`) from the model folder with
/// swift-transformers; no network access.
struct TransformersTokenizerLoader: MLXLMCommon.TokenizerLoader {
    func load(from directory: URL) async throws -> any MLXLMCommon.Tokenizer {
        TokenizerBridge(try await Tokenizers.AutoTokenizer.from(modelFolder: directory))
    }
}

/// swift-transformers' tokenizer seen as an MLXLMCommon one (same mapping as mlx-swift-lm's
/// `#adaptHuggingFaceTokenizer` macro, which would pull in the MLXHuggingFace hub integration).
private struct TokenizerBridge: MLXLMCommon.Tokenizer {
    let upstream: any Tokenizers.Tokenizer

    init(_ upstream: any Tokenizers.Tokenizer) {
        self.upstream = upstream
    }

    func encode(text: String, addSpecialTokens: Bool) -> [Int] {
        upstream.encode(text: text, addSpecialTokens: addSpecialTokens)
    }

    func decode(tokenIds: [Int], skipSpecialTokens: Bool) -> String {
        upstream.decode(tokens: tokenIds, skipSpecialTokens: skipSpecialTokens)
    }

    func convertTokenToId(_ token: String) -> Int? {
        upstream.convertTokenToId(token)
    }

    func convertIdToToken(_ id: Int) -> String? {
        upstream.convertIdToToken(id)
    }

    var bosToken: String? { upstream.bosToken }
    var eosToken: String? { upstream.eosToken }
    var unknownToken: String? { upstream.unknownToken }

    func applyChatTemplate(
        messages: [[String: any Sendable]], tools: [[String: any Sendable]]?, additionalContext: [String: any Sendable]?
    ) throws -> [Int] {
        do {
            return try upstream.applyChatTemplate(messages: messages, tools: tools, additionalContext: additionalContext)
        } catch Tokenizers.TokenizerError.missingChatTemplate {
            throw MLXLMCommon.TokenizerError.missingChatTemplate
        }
    }
}
