import EchoCore
import Foundation

/// Deepgram pre-recorded: `POST https://api.deepgram.com/v1/listen?...` with the audio bytes as the body and
/// `Authorization: Token <key>`. Utterances become segments.
public struct DeepgramTranscriber: CloudTranscriber {
    public static let endpoint = URL(string: "https://api.deepgram.com/v1/listen")!
    public static let defaultModel = "nova-3"

    public let model: String
    private let apiKey: String
    private let session: URLSession

    /// `session`: nil for the shared session that refuses cross-origin redirects (a test seam otherwise).
    public init(apiKey: String, model: String = defaultModel, session: URLSession? = nil) {
        self.apiKey = apiKey
        self.model = model
        self.session = session ?? ProviderHTTP.session
    }

    public func transcribe(file: URL, language: String?, diarize: Bool) async throws -> [CloudSegment] {
        let request = Self.makeRequest(
            apiKey: apiKey, model: model, audio: try Data(contentsOf: file),
            mimeType: CloudTranscription.mimeType(for: file), language: language, diarize: diarize)
        let (data, _) = try await ProviderHTTP.send(request, session: session, retry: false)
        return try Self.parse(data)
    }

    /// Settings' Test: `GET /v1/projects` lists the key's projects (free, no audio). Pure, for tests.
    public static func makeKeyCheckRequest(apiKey: String) -> URLRequest {
        var request = URLRequest(url: URL(string: "https://api.deepgram.com/v1/projects")!, timeoutInterval: ProviderHTTP.listTimeout)
        request.setValue("Token \(apiKey)", forHTTPHeaderField: "Authorization")
        return request
    }

    /// `utterances=true` is added to the documented query: without it the answer has no utterances. Pure, for tests.
    public static func makeRequest(
        apiKey: String, model: String = defaultModel, audio: Data, mimeType: String, language: String?, diarize: Bool
    ) -> URLRequest {
        var query = [
            URLQueryItem(name: "model", value: model),
            URLQueryItem(name: "smart_format", value: "true"),
            URLQueryItem(name: "utterances", value: "true"),
            URLQueryItem(name: "diarize", value: diarize ? "true" : "false"),
        ]
        if let code = CloudTranscription.languageCode(language) {
            query.append(URLQueryItem(name: "language", value: code))
        } else {
            query.append(URLQueryItem(name: "detect_language", value: "true"))
        }
        var request = URLRequest(url: endpoint.appending(queryItems: query), timeoutInterval: CloudTranscription.requestTimeout)
        request.httpMethod = "POST"
        request.setValue("Token \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue(mimeType, forHTTPHeaderField: "Content-Type")
        request.httpBody = audio
        return request
    }

    /// `results.utterances`, else the channel transcript as one segment. Pure, for tests.
    public static func parse(_ data: Data) throws -> [CloudSegment] {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        let response = try decoder.decode(ListenResponse.self, from: data)
        let language = response.results.channels.first?.detectedLanguage
        if let utterances = response.results.utterances, !utterances.isEmpty {
            return utterances.compactMap { utterance in
                let text = utterance.transcript.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !text.isEmpty else { return nil }
                return CloudSegment(
                    start: utterance.start, end: utterance.end, text: text, speaker: utterance.speaker.map(String.init),
                    language: language)
            }
        }
        let text = response.results.channels.first?.alternatives.first?.transcript
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return text.isEmpty ? [] : [CloudSegment(start: 0, end: response.metadata?.duration ?? 0, text: text, language: language)]
    }
}

private struct ListenResponse: Decodable {
    struct Metadata: Decodable { var duration: Double? }
    struct Results: Decodable {
        struct Channel: Decodable {
            struct Alternative: Decodable { var transcript: String }
            var alternatives: [Alternative]
            var detectedLanguage: String?
        }

        struct Utterance: Decodable {
            var start: Double
            var end: Double
            var transcript: String
            var speaker: Int?
        }

        var channels: [Channel]
        var utterances: [Utterance]?
    }

    var metadata: Metadata?
    var results: Results
}

extension DeepgramTranscriber {
    /// Live `wss://api.deepgram.com/v1/listen?...` (settings in the query, no start message), `Authorization: Token`
    /// handshake header, binary 16 kHz linear16 frames. `Results` with `is_final:false` are partials (they grow over
    /// the same `start`), `is_final:true` are finals: the transcript over its words' span (else `start...start+duration`),
    /// or, when the words carry speakers (`diarize_model=latest`, integers), the words grouped by speaker and pause. `Finalize` flushes,
    /// `CloseStream` ends (answered by `Metadata`, then the close); `KeepAlive` every 4 s while no audio flows
    /// (Deepgram closes after 10 s). No ready message: the session accepts audio once connected. Auto language is
    /// `language=multi` (code-switching; no Vietnamese there, so pick it explicitly).
    public struct Realtime: RealtimeAdapter {
        public static let models = ["nova-3", "nova-2"]
        public static let endpoint = URL(string: "wss://api.deepgram.com/v1/listen")!

        public let model: String
        private let apiKey: String
        private let language: String?
        private let diarize: Bool

        public init(apiKey: String, model: String, language: String?, diarize: Bool) {
            self.apiKey = apiKey
            self.model = model
            self.language = CloudTranscription.languageCode(language)
            self.diarize = diarize
        }

        public var streamsSpeakers: Bool { diarize }
        public var headers: [String: String] { ["Authorization": "Token \(apiKey)"] }
        public var keepAlive: RealtimeKeepAlive? { RealtimeKeepAlive(message: .text(#"{"type":"KeepAlive"}"#), interval: .seconds(4)) }

        public func socketURL(sessionAnswer: Data?) throws -> URL {
            var query = [
                URLQueryItem(name: "model", value: model),
                URLQueryItem(name: "encoding", value: "linear16"),
                URLQueryItem(name: "sample_rate", value: String(sampleRate)),
                URLQueryItem(name: "channels", value: "1"),
                URLQueryItem(name: "interim_results", value: "true"),
                URLQueryItem(name: "smart_format", value: "true"),
                URLQueryItem(name: "endpointing", value: "300"),
                URLQueryItem(name: "utterance_end_ms", value: "1000"),
                URLQueryItem(name: "language", value: language ?? "multi"),
            ]
            if diarize { query.append(URLQueryItem(name: "diarize_model", value: "latest")) }
            return Self.endpoint.appending(queryItems: query)
        }

        public mutating func finalize(at streamSeconds: Double) -> [RealtimeMessage] {
            [.text(#"{"type":"Finalize"}"#)]
        }

        public mutating func closeMessages() -> [RealtimeMessage] {
            [.text(#"{"type":"CloseStream"}"#)]
        }

        /// A message with a `channel` is a `Results` (the documented interim example has no `type`); `Metadata`
        /// ends; `Error` fails; `SpeechStarted`, `UtteranceEnd` and others give nothing.
        public mutating func parse(_ message: RealtimeMessage) throws -> [RealtimeEvent] {
            // The type first: `UtteranceEnd` and `SpeechStarted` have an array `channel`, which `LiveResults` refuses.
            guard let envelope = try CloudRealtime.decode(LiveType.self, from: message) else { return [] }
            switch envelope.type {
            case "Metadata": return [.ended]
            case "Error": return [.failed(kind: CloudRealtime.errorKind(envelope.type))]
            case nil, "Results": break
            default: return []
            }
            guard let live = try CloudRealtime.decode(LiveResults.self, from: message),
                  let alternative = live.channel?.alternatives.first else { return [] }
            let text = alternative.transcript.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { return [] }
            let start = live.start ?? alternative.words?.first?.start ?? 0
            let end = live.duration.map { start + $0 } ?? alternative.words?.last?.end ?? start
            let words = (alternative.words ?? []).map {
                CloudTranscription.Word(
                    start: $0.start, end: $0.end, text: $0.punctuatedWord ?? $0.word, speaker: $0.speaker.map(String.init))
            }
            guard live.isFinal == true else {
                return [.partial(text: text, start: start, end: end, speaker: words.first?.speaker)]
            }
            guard words.contains(where: { $0.speaker != nil }) else {
                return [.final(CloudSegment(start: words.first?.start ?? start, end: words.last?.end ?? end, text: text))]
            }
            return CloudTranscription.segments(from: words, separator: " ", language: nil).map { .final($0) }
        }
    }
}

private struct LiveType: Decodable {
    var type: String?
}

private struct LiveResults: Decodable {
    struct Channel: Decodable {
        struct Alternative: Decodable {
            struct Word: Decodable {
                var word: String
                var start: Double
                var end: Double
                var punctuatedWord: String?
                var speaker: Int?
            }

            var transcript: String
            var words: [Word]?
        }

        var alternatives: [Alternative]
    }

    var type: String?
    var start: Double?
    var duration: Double?
    var isFinal: Bool?
    var channel: Channel?
}
