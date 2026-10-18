import EchoCore
import Foundation

/// A local LLM host that answered its model-list probe.
public struct DiscoveredHost: Equatable, Sendable {
    /// The matching `ProviderConfig` preset slug (`lmstudio`, `ollama`, ...).
    public var preset: String
    /// The base URL that answered (llama-server can be on :9931 or :8080).
    public var baseURL: String
    public var models: [String]
}

/// Probes the known local host ports (docs/research/llm-providers.md E). On demand only: no timers, no polling.
public enum LocalHostDiscovery {
    static let probes: [(preset: String, baseURL: String)] = [
        ("lmstudio", "http://127.0.0.1:1234/v1"),
        ("ollama", "http://127.0.0.1:11434"),
        ("jan", "http://127.0.0.1:1337/v1"),
        ("llamacpp", "http://127.0.0.1:9931/v1"),
        ("llamacpp", "http://127.0.0.1:8080/v1"),
        ("vllm", "http://127.0.0.1:8000/v1"),
    ]

    /// All probes in parallel, each bounded by `timeout`; hosts in probe order. Any error or non-2xx = not found.
    public static func scan(timeout: TimeInterval = 0.4) async -> [DiscoveredHost] {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout
        let session = URLSession(configuration: configuration, delegate: RedirectGuard(), delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        return await withTaskGroup(of: (Int, DiscoveredHost?).self) { group in
            for (index, entry) in probes.enumerated() {
                group.addTask { (index, await probe(entry.preset, entry.baseURL, session: session, timeout: timeout)) }
            }
            var found: [(Int, DiscoveredHost)] = []
            for await (index, host) in group {
                if let host { found.append((index, host)) }
            }
            return found.sorted { $0.0 < $1.0 }.map(\.1)
        }
    }

    /// `GET {base}/models` (Ollama: `GET {base}/api/tags`), the same requests the client's `listModels()` sends.
    private static func probe(_ preset: String, _ baseURL: String, session: URLSession, timeout: TimeInterval) async -> DiscoveredHost? {
        let config = ProviderConfig(id: preset, kind: .local, preset: preset, displayName: preset, baseURL: baseURL, authStyle: .none)
        guard var request = try? OpenAICompatibleClient.makeModelsRequest(config: config, apiKey: nil) else { return nil }
        request.timeoutInterval = timeout
        let models: [String]? = preset == "ollama"
            ? try? await ProviderHTTP.getJSON(OllamaTags.self, request, session: session).models.map(\.name)
            : try? await ProviderHTTP.getJSON(ModelList.self, request, session: session).data.map(\.id)
        return models.map { DiscoveredHost(preset: preset, baseURL: baseURL, models: $0) }
    }
}
