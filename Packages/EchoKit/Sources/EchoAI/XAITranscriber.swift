import EchoCore
import Foundation

/// xAI Grok speech to text: `POST https://api.x.ai/v1/stt` (multipart, `Authorization: Bearer <key>`), answered at
/// once with timed words (seconds), grouped into segments by speaker and pause. Speakers are integers from 0.
public struct XAITranscriber: CloudTranscriber {
    public static let endpoint = URL(string: "https://api.x.ai/v1/stt")!
    public static let defaultModel = "grok-voice-transcribe-2.0"

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
            apiKey: apiKey, model: model, audio: try Data(contentsOf: file), fileName: file.lastPathComponent,
            language: language, diarize: diarize)
        let (data, _) = try await ProviderHTTP.send(request, session: session, retry: false)
        return try Self.parse(data)
    }

    /// Settings' Test: `GET /v1/api-key` describes the key (free, no audio). Pure, for tests.
    public static func makeKeyCheckRequest(apiKey: String) -> URLRequest {
        var request = URLRequest(url: URL(string: "https://api.x.ai/v1/api-key")!, timeoutInterval: ProviderHTTP.listTimeout)
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        return request
    }

    /// `file` is the last field: xAI may ignore the fields sent after it. Pure, for tests.
    public static func makeRequest(
        apiKey: String, model: String = defaultModel, audio: Data, fileName: String, language: String?, diarize: Bool
    ) -> URLRequest {
        var request = URLRequest(url: endpoint, timeoutInterval: CloudTranscription.requestTimeout)
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        var form = MultipartForm()
        form.add("model", model)
        if let code = CloudTranscription.languageCode(language) { form.add("language", code) }
        if diarize { form.add("diarize", "true") }
        form.addFile("file", fileName: fileName, mimeType: CloudTranscription.mimeType(for: URL(filePath: fileName)), data: audio)
        ProviderHTTP.setMultipartBody(&request, form)
        return request
    }

    /// `words` (`text`, `start`, `end`, integer `speaker`) joined with spaces into segments; without words, the
    /// `text` as one segment over `duration`. Pure, for tests.
    public static func parse(_ data: Data) throws -> [CloudSegment] {
        let response = try JSONDecoder().decode(STTResponse.self, from: data)
        let words = (response.words ?? []).map {
            CloudTranscription.Word(start: $0.start, end: $0.end, text: $0.text, speaker: $0.speaker.map(String.init))
        }
        if !words.isEmpty {
            return CloudTranscription.segments(from: words, separator: " ", language: response.language)
        }
        let text = response.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return text.isEmpty ? [] : [CloudSegment(start: 0, end: response.duration ?? 0, text: text, language: response.language)]
    }
}

private struct STTResponse: Decodable {
    struct Word: Decodable {
        var text: String
        var start: Double
        var end: Double
        var speaker: Int?
    }

    var text: String?
    var language: String?
    var duration: Double?
    var words: [Word]?
}

extension XAITranscriber {
    /// Streaming `wss://api.x.ai/v1/stt?...` (settings in the query, no start message), `Authorization: Bearer`
    /// handshake header, binary 16 kHz PCM frames. `transcript.created` starts: audio waits for it. A
    /// `transcript.partial` with `is_final:false` is a partial; with `is_final:true` its words become finals
    /// (integer speakers with `diarize`), except the words a `speech_final` repeats from the chunk finals before it:
    /// only words that start after the last final word are new. `finalize` forces an utterance final; `audio.done`
    /// ends (answered by `transcript.done`, then the close). No keep-alive documented.
    public struct Realtime: RealtimeAdapter {
        public static let models = ["grok-voice-transcribe-2.0"]
        public static let endpoint = URL(string: "wss://api.x.ai/v1/stt")!

        public let model: String
        private let apiKey: String
        private let language: String?
        private let diarize: Bool
        /// Start of the last word emitted as final; repeated words start at or before it.
        private var lastFinalStart = -Double.infinity

        public init(apiKey: String, model: String, language: String?, diarize: Bool) {
            self.apiKey = apiKey
            self.model = model
            self.language = CloudTranscription.languageCode(language)
            self.diarize = diarize
        }

        public var streamsSpeakers: Bool { diarize }
        public var headers: [String: String] { ["Authorization": "Bearer \(apiKey)"] }

        public func socketURL(sessionAnswer: Data?) throws -> URL {
            var query = [
                URLQueryItem(name: "model", value: model),
                URLQueryItem(name: "sample_rate", value: String(sampleRate)),
                URLQueryItem(name: "encoding", value: "pcm"),
                URLQueryItem(name: "interim_results", value: "true"),
            ]
            if let language { query.append(URLQueryItem(name: "language", value: language)) }
            if diarize { query.append(URLQueryItem(name: "diarize", value: "true")) }
            return Self.endpoint.appending(queryItems: query)
        }

        public mutating func finalize(at streamSeconds: Double) -> [RealtimeMessage] {
            [.text(#"{"type":"finalize"}"#)]
        }

        public mutating func closeMessages() -> [RealtimeMessage] {
            [.text(#"{"type":"audio.done"}"#)]
        }

        public mutating func parse(_ message: RealtimeMessage) throws -> [RealtimeEvent] {
            guard let live = try CloudRealtime.decode(LiveTranscript.self, from: message) else { return [] }
            switch live.type {
            case "transcript.created":
                return [.started(expiresAt: nil)]
            case "transcript.partial" where live.isFinal != true:
                let text = live.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                guard !text.isEmpty else { return [] }
                let start = live.start ?? live.words?.first?.start
                let end = start.flatMap { start in live.duration.map { start + $0 } } ?? live.words?.last?.end
                return [.partial(text: text, start: start, end: end, speaker: live.words?.first?.speaker.map(String.init))]
            case "transcript.partial":
                return finals(live)
            case "transcript.done":
                return finals(live) + [.ended]
            case "error":
                return [.failed(kind: CloudRealtime.errorKind(live.type))]
            default:
                return []
            }
        }

        /// The new words of a final (by start time) grouped into segments; a chunk final without words is its text
        /// over `start...start+duration`.
        private mutating func finals(_ live: LiveTranscript) -> [RealtimeEvent] {
            let fresh = (live.words ?? []).filter { $0.start > lastFinalStart }
            if let last = fresh.map(\.start).max() { lastFinalStart = last }
            let words = fresh.map {
                CloudTranscription.Word(start: $0.start, end: $0.end, text: $0.text, speaker: $0.speaker.map(String.init))
            }
            if !words.isEmpty {
                return CloudTranscription.segments(from: words, separator: " ", language: nil).map { .final($0) }
            }
            let text = live.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard live.type == "transcript.partial", live.speechFinal != true, live.words?.isEmpty ?? true, !text.isEmpty,
                  let start = live.start, start > lastFinalStart else { return [] }
            lastFinalStart = start
            return [.final(CloudSegment(start: start, end: start + (live.duration ?? 0), text: text))]
        }
    }
}

private struct LiveTranscript: Decodable {
    struct Word: Decodable {
        var text: String
        var start: Double
        var end: Double
        var speaker: Int?
    }

    var type: String?
    var text: String?
    var words: [Word]?
    var isFinal: Bool?
    var speechFinal: Bool?
    var start: Double?
    var duration: Double?
}
