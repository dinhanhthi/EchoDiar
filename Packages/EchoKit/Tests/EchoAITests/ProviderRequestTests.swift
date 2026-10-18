import EchoAI
import EchoCore
import Foundation
import Testing

/// Guards the API request contract and where secrets and transcript text may go. No network: only the pure
/// `makeRequest` builders are exercised, with a fake key.
struct ProviderRequestTests {
    private let key = "sk-test-fake-0000"
    private let transcript = "Alice [00:05]: the launch moves to Friday"
    private var messages: [LLMMessage] {
        [LLMMessage(role: .system, content: "Summarize the meeting."), LLMMessage(role: .user, content: transcript)]
    }

    @Test func openAICompatibleRequestUsesBearerAndKeepsTranscriptInUserMessage() throws {
        let request = try OpenAICompatibleClient.makeRequest(
            config: try preset("openai"), apiKey: key, messages: messages, model: "m", maxTokens: 100)

        #expect(request.httpMethod == "POST")
        #expect(request.url?.absoluteString == "https://api.openai.com/v1/chat/completions")
        #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer \(key)")
        #expect(request.value(forHTTPHeaderField: "api-key") == nil)
        let body = try json(request)
        #expect(body["stream"] as? Bool == true)
        let sent = try #require(body["messages"] as? [[String: String]])
        #expect(sent.filter { $0["content"]?.contains(transcript) == true }.map { $0["role"] } == ["user"])
        expectSecretsOnlyWhereAllowed(request)
    }

    @Test func azureSendsKeyInApiKeyHeader() throws {
        var config = try preset("azure")
        config.baseURL = "https://res.openai.azure.com/openai/v1"
        let request = try OpenAICompatibleClient.makeRequest(
            config: config, apiKey: key, messages: messages, model: "deployment", maxTokens: 100)

        #expect(request.url?.path() == "/openai/v1/chat/completions")
        #expect(request.value(forHTTPHeaderField: "api-key") == key)
        #expect(request.value(forHTTPHeaderField: "Authorization") == nil)
        expectSecretsOnlyWhereAllowed(request)
    }

    @Test func anthropicRequestCarriesKeyAndVersionHeaders() throws {
        let request = try AnthropicClient.makeRequest(
            config: try preset("anthropic"), apiKey: key, messages: messages, model: "m", maxTokens: 100)

        #expect(request.httpMethod == "POST")
        #expect(request.url?.absoluteString == "https://api.anthropic.com/v1/messages")
        #expect(request.value(forHTTPHeaderField: "x-api-key") == key)
        #expect(request.value(forHTTPHeaderField: "anthropic-version") == "2023-06-01")
        let body = try json(request)
        #expect(body["stream"] as? Bool == true)
        #expect(body["system"] as? String == "Summarize the meeting.")
        #expect(body["messages"] as? [[String: String]] == [["role": "user", "content": transcript]])
        expectSecretsOnlyWhereAllowed(request)
    }

    private func preset(_ id: String) throws -> ProviderConfig {
        try #require(ProviderConfig.presets.first { $0.id == id })
    }

    private func json(_ request: URLRequest) throws -> [String: Any] {
        let data = try #require(request.httpBody)
        return try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    /// The key never in the URL; the transcript never in the URL or any header.
    private func expectSecretsOnlyWhereAllowed(_ request: URLRequest) {
        let url = request.url?.absoluteString ?? ""
        #expect(!url.contains(key))
        #expect(!url.contains("launch"))
        #expect(!(request.allHTTPHeaderFields ?? [:]).values.contains { $0.contains("launch") })
    }
}
