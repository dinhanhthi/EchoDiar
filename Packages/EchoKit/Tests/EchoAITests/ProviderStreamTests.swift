@testable import EchoAI
import EchoCore
import Foundation
import Synchronization
import Testing

/// The HTTP streaming path end to end (request, status check, LF splitting, per-provider parsing) against canned
/// bodies served by `StubURLProtocol`, plus the redirect decision. No network.
struct ProviderStreamTests {
    enum Expected {
        case text(String)
        case providerError(String)
        case status(Int)
    }

    struct Case: CustomTestStringConvertible, Sendable {
        var name: String
        var config: ProviderConfig
        var status: Int
        var body: String
        var expected: Expected
        var testDescription: String { name }
    }

    private static func config(_ preset: String, host: String) -> ProviderConfig {
        var config = ProviderConfig.presets.first { $0.id == preset }!
        config.baseURL = "https://\(host)"
        return config
    }

    static let cases: [Case] = [
        Case(
            name: "OpenAI SSE: CRLF, comments, U+2028 kept inside a chunk, stops at [DONE]",
            config: config("openai", host: "openai.stub"), status: 200,
            body: ": keep-alive\r\n\r\n"
                + #"data: {"choices":[{"delta":{"role":"assistant"}}]}"# + "\r\n\r\n"
                + #"data: {"choices":[{"delta":{"content":"Hel"}}]}"# + "\r\n\r\n"
                + "data: {\"choices\":[{\"delta\":{\"content\":\"lo\u{2028}there\"}}]}\n\n"
                + "data: [DONE]\r\n\r\n"
                + #"data: {"choices":[{"delta":{"content":"after done"}}]}"# + "\n\n",
            expected: .text("Hello\u{2028}there")),
        Case(
            name: "Anthropic named events, stops at message_stop",
            config: config("anthropic", host: "anthropic.stub"), status: 200,
            body: "event: message_start\r\n" + #"data: {"type":"message_start","message":{}}"# + "\r\n\r\n"
                + "event: content_block_delta\n"
                + #"data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Sum"}}"# + "\n\n"
                + "event: ping\n" + #"data: {"type":"ping"}"# + "\n\n"
                + "event: content_block_delta\n"
                + #"data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"mary."}}"# + "\n\n"
                + "event: message_stop\n" + #"data: {"type":"message_stop"}"# + "\n\n"
                + "event: content_block_delta\n"
                + #"data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"late"}}"# + "\n\n",
            expected: .text("Summary.")),
        Case(
            name: "Anthropic mid-stream error event",
            config: config("anthropic", host: "anthropic-error.stub"), status: 200,
            body: "event: content_block_delta\n"
                + #"data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Su"}}"# + "\n\n"
                + "event: error\n" + #"data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"# + "\n\n",
            expected: .providerError("Overloaded")),
        Case(
            name: "Ollama JSONL until done",
            config: config("ollama", host: "ollama.stub"), status: 200,
            body: #"{"message":{"role":"assistant","content":"Hi"},"done":false}"# + "\n"
                + #"{"message":{"role":"assistant","content":" all"},"done":false}"# + "\n"
                + #"{"message":{"role":"assistant","content":""},"done":true}"# + "\n"
                + #"{"message":{"role":"assistant","content":"after done"},"done":false}"# + "\n",
            expected: .text("Hi all")),
        Case(
            name: "Non-2xx reports the status",
            config: config("openai", host: "openai-401.stub"), status: 401,
            body: #"{"error":{"message":"Incorrect API key"}}"#,
            expected: .status(401)),
    ]

    @Test(arguments: cases)
    func streamYieldsTextOrThrows(_ testCase: Case) async throws {
        StubURLProtocol.register(host: try #require(URL(string: testCase.config.baseURL)?.host()), status: testCase.status, body: testCase.body)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        let session = URLSession(configuration: configuration)
        defer { session.invalidateAndCancel() }
        let provider: any LLMProvider = testCase.config.kind == .anthropic
            ? AnthropicClient(config: testCase.config, apiKey: "sk-test", session: session)
            : OpenAICompatibleClient(config: testCase.config, apiKey: "sk-test", session: session)

        var text = ""
        var thrown: Error?
        do {
            for try await delta in provider.stream(messages: [LLMMessage(role: .user, content: "x")], model: "m", maxTokens: 10) {
                text += delta
            }
        } catch {
            thrown = error
        }

        switch testCase.expected {
        case .text(let expected):
            #expect(thrown == nil)
            #expect(text == expected)
        case .providerError(let message):
            guard case .provider(let got)? = thrown as? LLMError else {
                Issue.record("expected LLMError.provider, got \(String(describing: thrown))")
                return
            }
            #expect(got == message)
        case .status(let code):
            guard case .httpStatus(let got, let body)? = thrown as? LLMError else {
                Issue.record("expected LLMError.httpStatus, got \(String(describing: thrown))")
                return
            }
            #expect(got == code)
            #expect(body == testCase.body)
        }
    }

    @Test(arguments: [
        ("https://api.x.ai/v1/models", "https://api.x.ai/v2/models?page=2", true),
        ("https://api.x.ai/v1", "https://API.X.AI/v1", true),
        ("https://api.x.ai/v1", "https://api.x.ai:443/v1", true),
        ("http://127.0.0.1:11434/api/tags", "http://127.0.0.1:11434/api/tags/", true),
        ("https://api.x.ai/v1", "http://api.x.ai/v1", false),
        ("https://api.x.ai/v1", "https://evil.example/v1", false),
        ("https://api.x.ai/v1", "https://x.ai/v1", false),
        ("https://api.x.ai/v1", "https://api.x.ai:8443/v1", false),
        ("http://127.0.0.1:1234/v1", "http://127.0.0.1:1235/v1", false),
        ("http://127.0.0.1:1234/v1", "http://localhost:1234/v1", false),
    ])
    func redirectOnlyWithinTheSameOrigin(from: String, to: String, allowed: Bool) throws {
        #expect(ProviderHTTP.allowsRedirect(from: try #require(URL(string: from)), to: try #require(URL(string: to))) == allowed)
    }
}

/// Serves the response registered for the request's host, all at once (the client does the line splitting).
final class StubURLProtocol: URLProtocol, @unchecked Sendable {
    private static let responses = Mutex<[String: (status: Int, body: Data)]>([:])

    static func register(host: String, status: Int, body: String) {
        responses.withLock { $0[host] = (status, Data(body.utf8)) }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let url = request.url, let stub = Self.responses.withLock({ $0[url.host() ?? ""] }),
              let response = HTTPURLResponse(url: url, statusCode: stub.status, httpVersion: "HTTP/1.1", headerFields: nil) else {
            client?.urlProtocol(self, didFailWithError: URLError(.cannotFindHost))
            return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: stub.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
