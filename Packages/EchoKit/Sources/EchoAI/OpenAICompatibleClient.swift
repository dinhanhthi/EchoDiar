import EchoCore
import Foundation

/// OpenAI Chat Completions client for every OpenAI-compatible cloud API and local host. The `ollama` preset uses
/// Ollama's native `/api/chat` instead, because only that API can raise its context size (4K by default on
/// smaller Macs, which silently truncates a transcript).
public struct OpenAICompatibleClient: LLMProvider {
    /// Ollama's `num_ctx` never goes above this; also what `contextWindow` reports for it.
    static let ollamaMaxContext = 32_768
    /// Context fallback for other local hosts, whose loaded context size is unknown.
    static let localContext = 8_192
    /// Every cloud model in docs/research/llm-providers.md D has at least this much.
    static let cloudContext = 128_000

    public let config: ProviderConfig
    private let apiKey: String?
    private let session: URLSession

    /// `session`: nil for the shared session that refuses cross-origin redirects (a test seam otherwise).
    public init(config: ProviderConfig, apiKey: String?, session: URLSession? = nil) {
        self.config = config
        self.apiKey = apiKey
        self.session = session ?? ProviderHTTP.session
    }

    public func stream(messages: [LLMMessage], model: String, maxTokens: Int) -> AsyncThrowingStream<String, Error> {
        let request: URLRequest
        do {
            request = try Self.makeRequest(config: config, apiKey: apiKey, messages: messages, model: model, maxTokens: maxTokens)
        } catch {
            return AsyncThrowingStream { $0.finish(throwing: error) }
        }
        let parse = Self.isOllama(config) ? Self.ollamaLine : Self.chatCompletionsLine
        return ProviderHTTP.stream(request, session: session, parse: parse)
    }

    public func listModels() async throws -> [String] {
        let request = try Self.makeModelsRequest(config: config, apiKey: apiKey)
        if Self.isOllama(config) {
            return try await ProviderHTTP.getJSON(OllamaTags.self, request, session: session).models.map(\.name)
        }
        return try await ProviderHTTP.getJSON(ModelList.self, request, session: session).data.map(\.id)
    }

    public func contextWindow(model: String) async -> Int {
        if Self.isOllama(config) { return Self.ollamaMaxContext }
        return config.kind == .local ? Self.localContext : Self.cloudContext
    }

    /// `POST {base}/chat/completions` (or Ollama `POST {base}/api/chat`), streaming. Pure, for tests.
    public static func makeRequest(
        config: ProviderConfig, apiKey: String?, messages: [LLMMessage], model: String, maxTokens: Int
    ) throws -> URLRequest {
        let base = try ProviderHTTP.baseURL(config)
        if isOllama(config) {
            var request = ProviderHTTP.request(
                base.appending(path: "api/chat"), config: config, apiKey: apiKey, timeout: ProviderHTTP.streamTimeout)
            let options = OllamaOptions(
                numCtx: ollamaContextSize(messages: messages, maxTokens: maxTokens), numPredict: maxTokens)
            try ProviderHTTP.setJSONBody(&request, OllamaChatBody(model: model, messages: messages, stream: true, options: options))
            return request
        }
        var request = ProviderHTTP.request(
            base.appending(path: "chat/completions"), config: config, apiKey: apiKey, timeout: ProviderHTTP.streamTimeout)
        // OpenAI's newer models reject `max_tokens`; most other hosts only know `max_tokens`.
        let completionLimit = ["openai", "azure"].contains(config.preset ?? "")
        let body = ChatBody(
            model: model, messages: messages, stream: true,
            maxTokens: completionLimit ? nil : maxTokens, maxCompletionTokens: completionLimit ? maxTokens : nil)
        try ProviderHTTP.setJSONBody(&request, body)
        return request
    }

    /// `GET {base}/models` (Ollama: `GET {base}/api/tags`).
    public static func makeModelsRequest(config: ProviderConfig, apiKey: String?) throws -> URLRequest {
        let base = try ProviderHTTP.baseURL(config)
        let path = isOllama(config) ? "api/tags" : "models"
        return ProviderHTTP.request(base.appending(path: path), config: config, apiKey: apiKey, timeout: ProviderHTTP.listTimeout)
    }

    /// Prompt estimate (UTF-8 bytes / 4, errs high for accented text) + the answer, rounded up to 4K steps
    /// (Ollama reloads the model when `num_ctx` changes), between 4K and `ollamaMaxContext`.
    static func ollamaContextSize(messages: [LLMMessage], maxTokens: Int) -> Int {
        let needed = messages.reduce(0) { $0 + $1.content.utf8.count } / 4 + maxTokens
        let step = 4_096
        return min(ollamaMaxContext, max(step, (needed + step - 1) / step * step))
    }

    private static func isOllama(_ config: ProviderConfig) -> Bool { config.preset == "ollama" }

    /// SSE `data:` chunks, `choices[0].delta.content`, until `[DONE]`.
    @Sendable private static func chatCompletionsLine(_ line: String) throws -> StreamLine {
        guard let data = ProviderHTTP.sseData(line) else { return .skip }
        if data == "[DONE]" { return .done }
        let chunk = try JSONDecoder().decode(ChatChunk.self, from: Data(data.utf8))
        if let error = chunk.error { throw LLMError.provider(error.message ?? "The provider returned an error.") }
        guard let text = chunk.choices?.first?.delta?.content, !text.isEmpty else { return .skip }
        return .text(text)
    }

    /// JSONL objects, `message.content`, until `done: true`.
    @Sendable private static func ollamaLine(_ line: String) throws -> StreamLine {
        guard !line.isEmpty else { return .skip }
        let chunk = try JSONDecoder().decode(OllamaChunk.self, from: Data(line.utf8))
        if let error = chunk.error { throw LLMError.provider(error) }
        if let text = chunk.message?.content, !text.isEmpty { return .text(text) }
        return chunk.done == true ? .done : .skip
    }
}

private struct ChatBody: Encodable {
    var model: String
    var messages: [LLMMessage]
    var stream: Bool
    var maxTokens: Int?
    var maxCompletionTokens: Int?
}

private struct ChatChunk: Decodable {
    struct Choice: Decodable {
        struct Delta: Decodable { var content: String? }
        var delta: Delta?
    }

    var choices: [Choice]?
    var error: APIErrorBody?
}

private struct OllamaOptions: Encodable {
    var numCtx: Int
    var numPredict: Int
}

private struct OllamaChatBody: Encodable {
    var model: String
    var messages: [LLMMessage]
    var stream: Bool
    var options: OllamaOptions
}

private struct OllamaChunk: Decodable {
    struct Message: Decodable { var content: String? }
    var message: Message?
    var done: Bool?
    var error: String?
}

/// `{"models": [{"name": ...}]}` from Ollama's `/api/tags`.
struct OllamaTags: Decodable {
    struct Model: Decodable { var name: String }
    var models: [Model]
}
