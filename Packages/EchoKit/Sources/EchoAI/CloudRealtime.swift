import EchoCore
import Foundation

// MARK: - Messages and events

/// One WebSocket message, as the adapters build and parse it (Foundation's enum is not `Equatable`).
public enum RealtimeMessage: Equatable, Sendable {
    case text(String)
    case data(Data)
}

/// What a provider's message means for the live transcript. Times are seconds from the start of the stream (the
/// socket's first audio); the runner adds the stream's offset in the recording.
public enum RealtimeEvent: Equatable, Sendable {
    /// The session accepts audio; `expiresAt` is the provider's session end when it sends one (rotate before it).
    case started(expiresAt: Date?)
    /// The text so far of the current stretch; it replaces the previous partial. Times are nil when the provider
    /// sends none (ElevenLabs, OpenAI): display only.
    case partial(text: String, start: Double?, end: Double?, speaker: String?)
    /// Text that will not change, with its times and the provider's speaker label (nil without streaming speakers).
    case final(CloudSegment)
    /// The provider reported an error. `kind` is its short error type or code (`CloudRealtime.errorKind`), never
    /// the message text, which is external.
    case failed(kind: String)
    /// The provider finished the stream (after the close messages); the socket closes next.
    case ended
}

/// A message the session needs every `interval` while no audio flows, or the provider closes it.
public struct RealtimeKeepAlive: Equatable, Sendable {
    public var message: RealtimeMessage
    public var interval: Duration

    public init(message: RealtimeMessage, interval: Duration) {
        self.message = message
        self.interval = interval
    }
}

// MARK: - Adapter

/// One provider's realtime protocol for one socket (one channel), built for a model, a language and the diarize
/// flag. Everything here is pure: building requests and messages, framing audio, parsing answers; the I/O is
/// `RealtimeConnection`. Adapters keep per-socket state (frames sent, commit offsets, words already final), so the
/// runner owns one adapter value per socket and calls its mutating methods from one isolation domain.
///
/// Keys: the long-lived API key goes in `headers` (or the init request's header), never in a URL, a message that
/// is logged, or an error. A short-lived session token returned by the init call may be in the socket URL.
public protocol RealtimeAdapter: Sendable {
    /// Known realtime models, the default first.
    static var models: [String] { get }
    /// The socket's base URL (`wss://host/path`): its origin is the consent destination, and the URL the session
    /// connects to must have its host.
    static var endpoint: URL { get }
    /// The session-init endpoint when there is one (Gladia `POST /v2/live`); it must be on `endpoint`'s host.
    static var initURL: URL? { get }

    /// `model`: never empty (the catalog passes the default). `language`: a BCP-47 code, or nil / "auto".
    /// `diarize`: ask for streaming speaker labels where the provider has them.
    init(apiKey: String, model: String, language: String?, diarize: Bool)

    /// Sample rate of the Int16 mono PCM `frame` takes: 16 kHz, or 24 kHz (OpenAI).
    var sampleRate: Int { get }
    /// Whether finals carry the provider's speaker labels (else the existing diarizer runs).
    var streamsSpeakers: Bool { get }
    /// The init call, sent once per session and never retried; nil when the socket URL is fixed.
    func sessionRequest() throws -> URLRequest?
    /// The socket URL; `sessionAnswer` is the init call's 2xx body (nil without one). No long-lived key in it.
    func socketURL(sessionAnswer: Data?) throws -> URL
    /// Handshake headers (the auth header when the provider takes the key there).
    var headers: [String: String] { get }
    /// Messages sent right after the socket opens, before any audio (config / start).
    func startMessages() throws -> [RealtimeMessage]
    /// One chunk of little-endian Int16 mono PCM at `sampleRate` as a message: binary, or base64 in JSON.
    mutating func frame(_ pcm: Data) -> RealtimeMessage
    /// Messages that end the current stretch at `streamSeconds` (stream time of the last audio sent), so its
    /// text becomes final.
    mutating func finalize(at streamSeconds: Double) -> [RealtimeMessage]
    /// Messages that end the stream; the provider sends its last finals, then `ended` or a close, or (a provider
    /// without an end message) `isDrained` becomes true. An empty result does not mean the stream is over.
    mutating func closeMessages() -> [RealtimeMessage]
    /// True once nothing more will come from this stream after `closeMessages()` (every commit it waits for is
    /// answered). The runner reads it right after `closeMessages()` and after every `parse`; true means close the
    /// socket. Default false: wait for `ended` or the provider's close.
    var isDrained: Bool { get }
    /// Sent while no audio flows (paused stream, waiting); nil when not needed.
    var keepAlive: RealtimeKeepAlive? { get }
    /// The events of one server message; unknown message types give `[]`. Throws on undecodable JSON.
    mutating func parse(_ message: RealtimeMessage) throws -> [RealtimeEvent]
}

extension RealtimeAdapter {
    public static var initURL: URL? { nil }
    public var sampleRate: Int { 16_000 }
    public var streamsSpeakers: Bool { false }
    public func sessionRequest() throws -> URLRequest? { nil }
    public var headers: [String: String] { [:] }
    public func startMessages() throws -> [RealtimeMessage] { [] }
    public mutating func frame(_ pcm: Data) -> RealtimeMessage { .data(pcm) }
    public mutating func finalize(at streamSeconds: Double) -> [RealtimeMessage] { [] }
    public mutating func closeMessages() -> [RealtimeMessage] { [] }
    public var isDrained: Bool { false }
    public var keepAlive: RealtimeKeepAlive? { nil }
}

// MARK: - Catalog

/// The cloud providers with a realtime (WebSocket) transcription API, per `stt-protocols.md`: ElevenLabs, Deepgram,
/// AssemblyAI, OpenAI, xAI, Speechmatics, Soniox and Gladia. Realtime always uses the default cloud engine's
/// provider, key and consent; the model is the config's `realtimeModel`, else the adapter's default.
public enum CloudRealtime {
    /// The adapter type of a preset; nil when it has no realtime API (Gemini, Groq, Mistral, custom endpoints).
    public static func adapterType(for preset: String?) -> (any RealtimeAdapter.Type)? {
        switch preset {
        case "elevenlabs": ElevenLabsTranscriber.Realtime.self
        case "deepgram": DeepgramTranscriber.Realtime.self
        case "assemblyai": AssemblyAITranscriber.Realtime.self
        case "openai": OpenAICompatibleTranscriber.Realtime.self
        case "xai": XAITranscriber.Realtime.self
        case "speechmatics": SpeechmaticsTranscriber.Realtime.self
        case "soniox": SonioxTranscriber.Realtime.self
        case "gladia": GladiaTranscriber.Realtime.self
        default: nil
        }
    }

    public static func supports(_ config: ProviderConfig) -> Bool {
        CloudTranscription.supports(config) && adapterType(for: config.preset) != nil
    }

    /// Known realtime models of `config`, the default first; empty when unsupported.
    public static func models(for config: ProviderConfig) -> [String] {
        supports(config) ? adapterType(for: config.preset)?.models ?? [] : []
    }

    public static func defaultModel(for config: ProviderConfig) -> String {
        models(for: config).first ?? ""
    }

    /// `realtimeModel`, else the default.
    public static func model(for config: ProviderConfig) -> String {
        let model = config.realtimeModel.trimmingCharacters(in: .whitespacesAndNewlines)
        return model.isEmpty ? defaultModel(for: config) : model
    }

    /// The adapter for one socket of `config`; nil when unsupported or without a model.
    public static func make(
        config: ProviderConfig, apiKey: String, language: String?, diarize: Bool
    ) -> (any RealtimeAdapter)? {
        guard supports(config), let type = adapterType(for: config.preset) else { return nil }
        let model = model(for: config)
        guard !model.isEmpty else { return nil }
        return type.init(apiKey: apiKey, model: model, language: language, diarize: diarize)
    }

    /// Where the realtime audio and key go, in `CloudTranscription.destination`'s consent format
    /// `<provider id>@<scheme://host[:port]>`. `wss` counts as `https` (`ws` as `http`), so a provider whose socket is
    /// on its batch host (Deepgram, ElevenLabs, xAI, OpenAI, Gladia) shares the batch consent, and a separate
    /// realtime host (Speechmatics `eu.rt`, Soniox `stt-rt`, AssemblyAI `streaming`) has its own key. The init call
    /// must be on the socket's origin, so one key covers both; nil when unsupported or when it is not.
    public static func destination(for config: ProviderConfig) -> (consentKey: String, host: String)? {
        guard supports(config), let type = adapterType(for: config.preset),
              let socket = origin(of: type.endpoint) else { return nil }
        if let initURL = type.initURL {
            guard origin(of: initURL)?.origin == socket.origin else { return nil }
        }
        return ("\(config.id)@\(socket.origin)", socket.host)
    }

    /// `scheme://host[:port]` (lowercased, `wss` -> `https`, `ws` -> `http`, default port dropped) and the host.
    static func origin(of url: URL) -> (origin: String, host: String)? {
        guard let raw = url.scheme?.lowercased(), let host = url.host()?.lowercased(), !host.isEmpty else { return nil }
        let scheme = ["wss": "https", "ws": "http"][raw] ?? raw
        let port = url.port.flatMap { $0 == ["http": 80, "https": 443][scheme] ? nil : $0 }
        return ("\(scheme)://\(host)" + (port.map { ":\($0)" } ?? ""), host)
    }

    /// Whether `adapter` may connect to `url`: `wss` on the adapter's endpoint host and port. The init call's
    /// answer is external, so its URL is checked before any connection. Pure, for tests.
    public static func allowsSocket(_ url: URL, for adapter: some RealtimeAdapter) -> Bool {
        let endpoint = type(of: adapter).endpoint
        guard url.scheme?.lowercased() == "wss", let host = url.host()?.lowercased(), !host.isEmpty else { return false }
        return host == endpoint.host()?.lowercased() && (url.port ?? 443) == (endpoint.port ?? 443)
    }

    /// A provider error type or code reduced to a short identifier (letters, digits, `_`, `-`, `.`; at most 40
    /// characters), so external text never reaches the UI or a log; "error" when nothing is left. Pure, for tests.
    public static func errorKind(_ raw: String?) -> String {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "_-."))
        let scalars = (raw ?? "").unicodeScalars.filter { $0.isASCII && allowed.contains($0) }
        let kind = String(String(scalars).prefix(40))
        return kind.isEmpty ? "error" : kind
    }

    // MARK: Adapter helpers

    /// A server message decoded as `T` (snake_case keys); nil for an empty message. Throws on undecodable JSON.
    static func decode<T: Decodable>(_ type: T.Type, from message: RealtimeMessage) throws -> T? {
        let data = payload(message)
        guard !data.isEmpty else { return nil }
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return try decoder.decode(T.self, from: data)
    }

    /// The bytes of a message (a text message's UTF-8).
    static func payload(_ message: RealtimeMessage) -> Data {
        switch message {
        case .text(let text): Data(text.utf8)
        case .data(let data): data
        }
    }

    /// `object` as a JSON text message, keys sorted so the bytes are stable.
    static func json(_ object: [String: Any]) throws -> RealtimeMessage {
        let data = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes])
        return .text(String(decoding: data, as: UTF8.self))
    }

    // MARK: PCM

    /// Float samples (-1...1, clamped) as little-endian Int16 PCM. Pure, for tests.
    public static func pcm16(_ samples: [Float]) -> Data {
        var data = Data(capacity: samples.count * 2)
        for sample in samples {
            let value = Int16((min(max(sample.isFinite ? sample : 0, -1), 1) * 32_767).rounded())
            withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) }
        }
        return data
    }
}

/// A streaming linear resampler (16 -> 24 kHz for OpenAI): it keeps the last sample and the phase between calls, so
/// chunk edges do not click and the output length follows the total input length. Pure, for tests.
public struct RealtimeResampler: Sendable {
    public let inputRate: Int
    public let outputRate: Int
    private var last: Float?
    /// Position of the next output sample in units of 1 / `outputRate` input samples, from `last` (or from the
    /// first sample before any input).
    private var position = 0

    public init(from inputRate: Int, to outputRate: Int) {
        precondition(inputRate > 0 && outputRate > 0)
        self.inputRate = inputRate
        self.outputRate = outputRate
    }

    public mutating func process(_ input: [Float]) -> [Float] {
        guard !input.isEmpty else { return [] }
        let samples = last.map { [$0] + input } ?? input
        let end = (samples.count - 1) * outputRate
        var output: [Float] = []
        output.reserveCapacity(input.count * outputRate / inputRate + 1)
        while position < end {
            let index = position / outputRate
            let fraction = Float(position % outputRate) / Float(outputRate)
            output.append(samples[index] + (samples[index + 1] - samples[index]) * fraction)
            position += inputRate
        }
        position -= end
        last = samples.last
        return output
    }
}

// MARK: - Connection

/// One realtime socket on `ProviderHTTP.session` (the redirect guard). Call `socketURL(for:)` once per provider
/// session (it runs the init call when the adapter has one: a new, billed session each time), then `connect`.
/// No reconnects here. Cancelling the task that awaits `send` or `receive` closes the socket.
public final class RealtimeConnection: Sendable {
    /// The socket's request timeout: how long it may receive nothing before URLSession fails it.
    static let idleTimeout: TimeInterval = 300
    private let task: URLSessionWebSocketTask

    private init(task: URLSessionWebSocketTask) {
        self.task = task
    }

    /// The URL to connect to: the adapter's, after its init call when it has one (sent once, redacted errors).
    /// The URL may carry a session token: never log it.
    public static func socketURL(for adapter: some RealtimeAdapter, session: URLSession? = nil) async throws -> URL {
        guard let request = try adapter.sessionRequest() else { return try adapter.socketURL(sessionAnswer: nil) }
        let (data, _) = try await ProviderHTTP.send(request, session: session ?? ProviderHTTP.session, retry: false)
        return try adapter.socketURL(sessionAnswer: data)
    }

    /// Opens the socket to `url` with the adapter's headers and sends its start messages. Throws
    /// `LLMError.httpStatus(code, "")` when the provider refuses the handshake (401 for a bad key).
    public static func connect(
        _ adapter: some RealtimeAdapter, url: URL, session: URLSession? = nil
    ) async throws -> RealtimeConnection {
        guard CloudRealtime.allowsSocket(url, for: adapter) else {
            throw LLMError.provider("Unexpected realtime host.")
        }
        // A WebSocket task fails once nothing was received for its request timeout (60 s by default), which would cut
        // a quiet socket (no audio during a drain, a provider that only answers commits); the runner also pings.
        var request = URLRequest(url: url, timeoutInterval: idleTimeout)
        for (name, value) in adapter.headers { request.setValue(value, forHTTPHeaderField: name) }
        let connection = RealtimeConnection(task: (session ?? ProviderHTTP.session).webSocketTask(with: request))
        connection.task.resume()
        do {
            for message in try adapter.startMessages() { try await connection.send(message) }
        } catch {
            connection.close()
            throw error
        }
        return connection
    }

    public func send(_ message: RealtimeMessage) async throws {
        let outgoing: URLSessionWebSocketTask.Message = switch message {
        case .text(let text): .string(text)
        case .data(let data): .data(data)
        }
        try await cancellable { try await self.task.send(outgoing) }
    }

    /// The next server message; throws when the socket closed or failed (`closeCode` says how).
    public func receive() async throws -> RealtimeMessage {
        let incoming = try await cancellable { try await self.task.receive() }
        switch incoming {
        case .string(let text): return .text(text)
        case .data(let data): return .data(data)
        @unknown default: return .data(Data())
        }
    }

    /// A WebSocket ping, not awaited (a pong that never comes must not hold the audio back); a failed ping shows up
    /// as a failed `send` or `receive`. Keeps idle sockets open (Speechmatics closes after 3 min without audio or ping).
    public func ping() {
        task.sendPing { _ in }
    }

    /// The close code the server sent (`.invalid` while open).
    public var closeCode: URLSessionWebSocketTask.CloseCode { task.closeCode }

    /// Closes the socket at once (send the adapter's close messages and wait for `ended` first for a clean end).
    public func close() {
        task.cancel(with: .normalClosure, reason: nil)
    }

    private func cancellable<T: Sendable>(_ operation: @Sendable () async throws -> T) async throws -> T {
        do {
            return try await withTaskCancellationHandler(operation: operation) { [task] in
                task.cancel(with: .goingAway, reason: nil)
            }
        } catch {
            if let http = task.response as? HTTPURLResponse, http.statusCode != 101 {
                throw LLMError.httpStatus(http.statusCode, "")
            }
            throw error
        }
    }
}
