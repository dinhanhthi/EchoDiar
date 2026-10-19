import EchoCore
import Foundation

public struct LLMMessage: Codable, Equatable, Sendable {
    public enum Role: String, Codable, Sendable {
        case system, user, assistant
    }

    public var role: Role
    public var content: String

    public init(role: Role, content: String) {
        self.role = role
        self.content = content
    }
}

/// A chat model backend. Callers put transcript text only in user messages; system messages carry instructions.
public protocol LLMProvider: Sendable {
    /// Text deltas of the answer. Cancelling the consuming task cancels the underlying request.
    func stream(messages: [LLMMessage], model: String, maxTokens: Int) -> AsyncThrowingStream<String, Error>
    func listModels() async throws -> [String]
    /// Input + output tokens `model` can take; a conservative fallback when the host cannot tell.
    func contextWindow(model: String) async -> Int
}

/// An in-app model (Foundation Models, MLX). `AIService` loads it once per job inside `ModelLoadGate` (owner `.llm`)
/// and calls the generator directly in the job's task, so the model is unloaded before the gate is released.
public protocol LocalLLMProvider: LLMProvider {
    /// Loads the model, runs `body` with it, then unloads it and restores any process-wide state. Returns (or
    /// throws) only once the model is unloaded, also when `body` throws or the task is cancelled.
    func withLoadedModel<T: Sendable>(
        model: String, _ body: @Sendable (any LocalLLMGenerator) async throws -> T
    ) async throws -> T
}

/// A loaded in-app model, valid inside `LocalLLMProvider.withLoadedModel`.
public protocol LocalLLMGenerator: Sendable {
    /// Passes the pieces of the answer to `onText` and returns the whole answer once generation has stopped
    /// (early, without throwing, when the task is cancelled).
    func generate(
        messages: [LLMMessage], maxTokens: Int, onText: @escaping @Sendable (String) -> Void
    ) async throws -> String
}

extension LocalLLMProvider {
    /// One answer with the model loaded for it alone (no `ModelLoadGate`); `AIService` uses `withLoadedModel`.
    public func stream(messages: [LLMMessage], model: String, maxTokens: Int) -> AsyncThrowingStream<String, Error> {
        AsyncThrowingStream { continuation in
            let task = Task(priority: .utility) {
                do {
                    _ = try await withLoadedModel(model: model) { generator in
                        try await generator.generate(messages: messages, maxTokens: maxTokens) { continuation.yield($0) }
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}

public enum LLMError: Error, LocalizedError {
    case invalidBaseURL(String)
    case httpStatus(Int, String)
    case provider(String)

    public var errorDescription: String? {
        switch self {
        case .invalidBaseURL(let url): "\"\(url)\" is not a valid http(s) base URL."
        case .httpStatus(let code, let body): body.isEmpty ? "HTTP \(code)." : "HTTP \(code): \(body)"
        case .provider(let message): message
        }
    }
}

/// What one line of a streamed body (SSE or JSONL) means.
enum StreamLine {
    case text(String)
    case skip
    case done
}

/// HTTP plumbing shared by the API clients.
enum ProviderHTTP {
    /// Streaming answers can start late (a local host evaluating a long prompt), so the idle timeout is long.
    static let streamTimeout: TimeInterval = 600
    static let listTimeout: TimeInterval = 15

    /// The session every API client uses by default; it refuses cross-origin redirects (see `RedirectGuard`).
    static let session = URLSession(configuration: .ephemeral, delegate: RedirectGuard(), delegateQueue: nil)

    /// Whether a redirect from `original` may be followed: only to the same scheme, host and port. URLSession drops
    /// `Authorization` on a cross-host redirect but keeps `x-api-key`, `api-key` and the config's extra headers, and
    /// an https -> http redirect would send them in cleartext. Pure, for tests.
    static func allowsRedirect(from original: URL, to target: URL) -> Bool {
        func origin(_ url: URL) -> (scheme: String, host: String, port: Int?) {
            let scheme = url.scheme?.lowercased() ?? ""
            let defaultPort = ["http": 80, "https": 443][scheme]
            return (scheme, url.host()?.lowercased() ?? "", url.port ?? defaultPort)
        }
        let (from, to) = (origin(original), origin(target))
        return !from.host.isEmpty && from.scheme == to.scheme && from.host == to.host && from.port == to.port
    }

    static func baseURL(_ config: ProviderConfig) throws -> URL {
        let text = config.baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: text), ["http", "https"].contains(url.scheme?.lowercased()), url.host() != nil else {
            throw LLMError.invalidBaseURL(text)
        }
        return url
    }

    /// A request with the config's extra headers, then its auth header (so extras cannot replace the auth).
    static func request(_ url: URL, config: ProviderConfig, apiKey: String?, timeout: TimeInterval) -> URLRequest {
        var request = URLRequest(url: url, timeoutInterval: timeout)
        for (name, value) in config.extraHeaders { request.setValue(value, forHTTPHeaderField: name) }
        if let apiKey, !apiKey.isEmpty {
            switch config.authStyle {
            case .bearer: request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
            case .apiKeyHeader: request.setValue(apiKey, forHTTPHeaderField: "api-key")
            case .xApiKey: request.setValue(apiKey, forHTTPHeaderField: "x-api-key")
            case .none: break
            }
        }
        return request
    }

    static func setJSONBody(_ request: inout URLRequest, _ body: some Encodable) throws {
        let encoder = JSONEncoder()
        encoder.keyEncodingStrategy = .convertToSnakeCase
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try encoder.encode(body)
    }

    /// Streams the body of `request` line by line through `parse`. The request runs in a task that is
    /// cancelled when the stream terminates, which cancels the URLSession task.
    static func stream(
        _ request: URLRequest, session: URLSession, parse: @escaping @Sendable (String) throws -> StreamLine
    ) -> AsyncThrowingStream<String, Error> {
        AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    let (bytes, response) = try await session.bytes(for: request)
                    if let status = (response as? HTTPURLResponse)?.statusCode, !(200..<300).contains(status) {
                        var body = Data()
                        for try await byte in bytes {
                            body.append(byte)
                            if body.count >= 2048 { break }
                        }
                        throw LLMError.httpStatus(status, String(decoding: body, as: UTF8.self))
                    }
                    // Split on LF only: `bytes.lines` also splits on U+2028/U+2029, which some servers leave
                    // unescaped inside JSON strings.
                    var line = Data()
                    for try await byte in bytes {
                        guard byte == UInt8(ascii: "\n") else {
                            line.append(byte)
                            continue
                        }
                        if line.last == UInt8(ascii: "\r") { line.removeLast() }
                        let text = String(decoding: line, as: UTF8.self)
                        line.removeAll(keepingCapacity: true)
                        switch try parse(text) {
                        case .text(let text): continuation.yield(text)
                        case .skip: continue
                        case .done: return continuation.finish()
                        }
                    }
                    if !line.isEmpty, case .text(let text) = try parse(String(decoding: line, as: UTF8.self)) {
                        continuation.yield(text)
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// The payload of an SSE `data:` line; nil for `event:`, `:` comment and blank lines.
    static func sseData(_ line: String) -> String? {
        guard line.hasPrefix("data:") else { return nil }
        let payload = line.dropFirst(5)
        return String(payload.first == " " ? payload.dropFirst() : payload)
    }

    static func getJSON<T: Decodable>(_ type: T.Type, _ request: URLRequest, session: URLSession) async throws -> T {
        let (data, response) = try await session.data(for: request)
        if let status = (response as? HTTPURLResponse)?.statusCode, !(200..<300).contains(status) {
            throw LLMError.httpStatus(status, String(decoding: data.prefix(2048), as: UTF8.self))
        }
        return try JSONDecoder().decode(T.self, from: data)
    }
}

/// Refuses redirects that leave the original request's origin; URLSession then returns the 3xx response itself,
/// which the callers report as an HTTP error.
final class RedirectGuard: NSObject, URLSessionTaskDelegate, Sendable {
    func urlSession(
        _ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void
    ) {
        guard let original = task.originalRequest?.url, let target = request.url,
              ProviderHTTP.allowsRedirect(from: original, to: target) else {
            return completionHandler(nil)
        }
        completionHandler(request)
    }
}

/// `{"error": {"message": ...}}`, used by both OpenAI-style and Anthropic error payloads.
struct APIErrorBody: Decodable {
    var message: String?
}

/// `{"data": [{"id": ...}]}` from a models endpoint (OpenAI-style and Anthropic).
struct ModelList: Decodable {
    struct Model: Decodable { var id: String }
    var data: [Model]
}
