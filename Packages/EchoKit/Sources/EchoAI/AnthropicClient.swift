import EchoCore
import Foundation

/// Native Anthropic Messages API client (its OpenAI compatibility layer is for testing only).
public struct AnthropicClient: LLMProvider {
    public static let apiVersion = "2023-06-01"
    /// Every current Claude model has at least this much.
    static let fallbackContext = 200_000

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
        return ProviderHTTP.stream(request, session: session, parse: Self.eventLine)
    }

    public func listModels() async throws -> [String] {
        let request = try Self.makeModelsRequest(config: config, apiKey: apiKey)
        return try await ProviderHTTP.getJSON(ModelList.self, request, session: session).data.map(\.id)
    }

    public func contextWindow(model: String) async -> Int { Self.fallbackContext }

    /// `POST {base}/v1/messages`, streaming; system messages go to the top-level `system` field. No `temperature`
    /// or `top_p` (recent models reject non-default values). Pure, for tests.
    public static func makeRequest(
        config: ProviderConfig, apiKey: String?, messages: [LLMMessage], model: String, maxTokens: Int
    ) throws -> URLRequest {
        var request = try baseRequest("v1/messages", config: config, apiKey: apiKey, timeout: ProviderHTTP.streamTimeout)
        let system = messages.filter { $0.role == .system }.map(\.content).joined(separator: "\n\n")
        let body = MessagesBody(
            model: model, maxTokens: maxTokens, system: system.isEmpty ? nil : system,
            messages: bodyMessages(messages.filter { $0.role != .system }), stream: true)
        try ProviderHTTP.setJSONBody(&request, body)
        return request
    }

    /// The API takes at most 4 cache breakpoints per request; 3 leaves one spare.
    static let maxCacheMarks = 3

    /// Cache marks become `cache_control` (5-minute TTL) on text blocks. Empty-text parts are dropped first: they
    /// never count as marks and are never sent. Of the rest, only the last `maxCacheMarks` marked parts are kept
    /// (message order, then part order); earlier marks count as unmarked. A message with a kept mark is sent as one
    /// text block per non-empty part; any other message, including one whose marks were all dropped, stays a plain
    /// string, as before. Same rule as the Windows client.
    private static func bodyMessages(_ messages: [LLMMessage]) -> [BodyMessage] {
        let marks = messages.indices.flatMap { m in
            messages[m].parts.indices.filter { messages[m].parts[$0].cache && !messages[m].parts[$0].text.isEmpty }
                .map { (m, $0) }
        }
        let kept = marks.suffix(maxCacheMarks)
        return messages.enumerated().map { m, message in
            let keptParts = Set(kept.filter { $0.0 == m }.map(\.1))
            guard !keptParts.isEmpty else { return BodyMessage(role: message.role, content: .text(message.content)) }
            let blocks = message.parts.enumerated().filter { !$1.text.isEmpty }.map { p, part in
                TextBlock(text: part.text, cacheControl: keptParts.contains(p) ? .ephemeral : nil)
            }
            return BodyMessage(role: message.role, content: .blocks(blocks))
        }
    }

    /// `GET {base}/v1/models?limit=1000` (one page holds every model).
    public static func makeModelsRequest(config: ProviderConfig, apiKey: String?) throws -> URLRequest {
        var request = try baseRequest("v1/models", config: config, apiKey: apiKey, timeout: ProviderHTTP.listTimeout)
        request.url?.append(queryItems: [URLQueryItem(name: "limit", value: "1000")])
        return request
    }

    private static func baseRequest(_ path: String, config: ProviderConfig, apiKey: String?, timeout: TimeInterval) throws -> URLRequest {
        let url = try ProviderHTTP.baseURL(config).appending(path: path)
        var request = ProviderHTTP.request(url, config: config, apiKey: apiKey, timeout: timeout)
        request.setValue(apiVersion, forHTTPHeaderField: "anthropic-version")
        return request
    }

    /// Named SSE events; each `data:` JSON repeats the event name in `type`. Text comes from
    /// `content_block_delta` events with a `text_delta`; `message_stop` ends the answer.
    @Sendable private static func eventLine(_ line: String) throws -> StreamLine {
        guard let data = ProviderHTTP.sseData(line) else { return .skip }
        let event = try JSONDecoder().decode(Event.self, from: Data(data.utf8))
        switch event.type {
        case "content_block_delta" where event.delta?.type == "text_delta":
            guard let text = event.delta?.text, !text.isEmpty else { return .skip }
            return .text(text)
        case "message_stop":
            return .done
        case "error":
            throw LLMError.provider(event.error?.message ?? "The provider returned an error.")
        default:
            return .skip
        }
    }
}

private struct MessagesBody: Encodable {
    var model: String
    var maxTokens: Int
    var system: String?
    var messages: [BodyMessage]
    var stream: Bool
}

private struct BodyMessage: Encodable {
    enum Content: Encodable {
        case text(String)
        case blocks([TextBlock])

        func encode(to encoder: any Encoder) throws {
            var container = encoder.singleValueContainer()
            switch self {
            case .text(let text): try container.encode(text)
            case .blocks(let blocks): try container.encode(blocks)
            }
        }
    }

    var role: LLMMessage.Role
    var content: Content
}

private struct TextBlock: Encodable {
    struct CacheControl: Encodable {
        static let ephemeral = CacheControl(type: "ephemeral")
        var type: String
    }

    var type = "text"
    var text: String
    var cacheControl: CacheControl?
}

private struct Event: Decodable {
    struct Delta: Decodable {
        var type: String?
        var text: String?
    }

    var type: String
    var delta: Delta?
    var error: APIErrorBody?
}
