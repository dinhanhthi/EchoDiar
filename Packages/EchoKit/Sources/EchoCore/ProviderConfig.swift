import Foundation

/// One configured AI provider, stored in `AppSettings.providers`. Its API key is never part of this value:
/// it lives in the Keychain under `KeychainStore.service(forProvider: id)`.
public struct ProviderConfig: Codable, Equatable, Sendable, Identifiable {
    public enum Kind: String, Codable, Sendable {
        case openAICompatible, anthropic, cli, local
    }

    public enum AuthStyle: String, Codable, Sendable {
        /// `Authorization: Bearer <key>`.
        case bearer
        /// Azure OpenAI `api-key: <key>`.
        case apiKeyHeader
        /// Anthropic `x-api-key: <key>`.
        case xApiKey
        case none
    }

    /// The preset slug (`openai`, `ollama`, `claude-cli`, ...) for presets; a UUID string for custom endpoints.
    public var id: String
    public var kind: Kind
    /// The preset slug this config came from; nil for custom endpoints.
    public var preset: String?
    public var displayName: String
    /// For CLIs: the executable path override (empty = search PATH). Anthropic and Ollama bases are the host root (the clients add `/v1/...` or `/api/...`);
    /// every other base already ends where `/chat/completions` and `/models` are appended.
    public var baseURL: String
    public var authStyle: AuthStyle
    public var extraHeaders: [String: String]
    /// Fallback until the model picker is filled from `listModels()`; empty = pick one (or the CLI's default).
    public var defaultModel: String
    public var enabled: Bool

    public init(
        id: String,
        kind: Kind,
        preset: String?,
        displayName: String,
        baseURL: String,
        authStyle: AuthStyle,
        extraHeaders: [String: String] = [:],
        defaultModel: String = "",
        enabled: Bool = false
    ) {
        self.id = id
        self.kind = kind
        self.preset = preset
        self.displayName = displayName
        self.baseURL = baseURL
        self.authStyle = authStyle
        self.extraHeaders = extraHeaders
        self.defaultModel = defaultModel
        self.enabled = enabled
    }

    /// Tolerant: a missing, malformed or unknown (newer) value falls back to a default instead of failing, so one bad
    /// entry cannot fail the whole `AppSettings` decode. Only `id` is required. Encoding stays synthesized.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        func value<T: Decodable>(_ key: CodingKeys) -> T? { (try? container.decodeIfPresent(T.self, forKey: key)) ?? nil }
        id = try container.decode(String.self, forKey: .id)
        kind = value(.kind).flatMap(Kind.init(rawValue:)) ?? .openAICompatible
        preset = value(.preset)
        displayName = value(.displayName) ?? id
        baseURL = value(.baseURL) ?? ""
        // Unknown = no auth header: never put the key in a guessed header.
        authStyle = value(.authStyle).flatMap(AuthStyle.init(rawValue:)) ?? AuthStyle.none
        extraHeaders = value(.extraHeaders) ?? [:]
        defaultModel = value(.defaultModel) ?? ""
        enabled = value(.enabled) ?? false
    }

    /// A new custom OpenAI-compatible endpoint with its own UUID id.
    public static func custom(displayName: String = "Custom", baseURL: String = "") -> ProviderConfig {
        ProviderConfig(
            id: UUID().uuidString, kind: .openAICompatible, preset: nil, displayName: displayName,
            baseURL: baseURL, authStyle: .bearer
        )
    }

    /// Built-in presets, all disabled. Model ids are research fallbacks (docs/research/llm-providers.md D).
    public static let presets: [ProviderConfig] = [
        cloud("openai", "OpenAI", "https://api.openai.com/v1", model: "gpt-6.1-sol"),
        ProviderConfig(
            id: "anthropic", kind: .anthropic, preset: "anthropic", displayName: "Anthropic",
            baseURL: "https://api.anthropic.com", authStyle: .xApiKey, defaultModel: "claude-sonnet-5-5"
        ),
        cloud("xai", "xAI", "https://api.x.ai/v1", model: "grok-4.3"),
        cloud("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", model: "gemini-3.8-flash"),
        cloud("mistral", "Mistral", "https://api.mistral.ai/v1", model: "mistral-medium-latest"),
        // Base is https://<resource>.openai.azure.com/openai/v1 (set by the user); model = deployment name.
        cloud("azure", "Azure OpenAI", "", model: "", auth: .apiKeyHeader),
        cloud("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", model: ""),
        cloud("groq", "Groq", "https://api.groq.com/openai/v1", model: "openai/gpt-oss-120b"),
        cloud("deepseek", "DeepSeek", "https://api.deepseek.com", model: "deepseek-v4-pro"),
        local("lmstudio", "LM Studio", "http://127.0.0.1:1234/v1"),
        // Native API (`/api/chat`, `/api/tags`) so the context size can be set; see OpenAICompatibleClient.
        local("ollama", "Ollama", "http://127.0.0.1:11434"),
        local("jan", "Jan", "http://127.0.0.1:1337/v1"),
        // Older builds and Docker listen on :8080 instead.
        local("llamacpp", "llama.cpp server", "http://127.0.0.1:9931/v1"),
        local("vllm", "vLLM", "http://127.0.0.1:8000/v1"),
        cli("claude-cli", "Claude Code"),
        cli("codex-cli", "Codex"),
        cli("opencode-cli", "OpenCode"),
        cli("gemini-cli", "Gemini CLI"),
    ]

    private static func cloud(_ id: String, _ name: String, _ base: String, model: String, auth: AuthStyle = .bearer) -> ProviderConfig {
        ProviderConfig(id: id, kind: .openAICompatible, preset: id, displayName: name, baseURL: base, authStyle: auth, defaultModel: model)
    }

    private static func local(_ id: String, _ name: String, _ base: String) -> ProviderConfig {
        ProviderConfig(id: id, kind: .local, preset: id, displayName: name, baseURL: base, authStyle: .none)
    }

    private static func cli(_ id: String, _ name: String) -> ProviderConfig {
        ProviderConfig(id: id, kind: .cli, preset: id, displayName: name, baseURL: "", authStyle: .none)
    }
}
