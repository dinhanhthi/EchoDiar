import EchoCore
import Foundation

/// ElevenLabs Scribe: `POST https://api.elevenlabs.io/v1/speech-to-text` (multipart, `xi-api-key`). The answer is
/// timed words, grouped into segments by speaker and pause.
public struct ElevenLabsTranscriber: CloudTranscriber {
    public static let endpoint = URL(string: "https://api.elevenlabs.io/v1/speech-to-text")!
    public static let defaultModel = "scribe_v2"

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

    /// Settings' Test: `GET /v1/models` (free, no audio). Pure, for tests.
    public static func makeKeyCheckRequest(apiKey: String) -> URLRequest {
        var request = URLRequest(url: URL(string: "https://api.elevenlabs.io/v1/models")!, timeoutInterval: ProviderHTTP.listTimeout)
        request.setValue(apiKey, forHTTPHeaderField: "xi-api-key")
        return request
    }

    /// Pure, for tests.
    public static func makeRequest(
        apiKey: String, model: String = defaultModel, audio: Data, fileName: String, language: String?, diarize: Bool
    ) -> URLRequest {
        var request = URLRequest(url: endpoint, timeoutInterval: CloudTranscription.requestTimeout)
        request.setValue(apiKey, forHTTPHeaderField: "xi-api-key")
        var form = MultipartForm()
        form.addFile("file", fileName: fileName, mimeType: CloudTranscription.mimeType(for: URL(filePath: fileName)), data: audio)
        form.add("model_id", model)
        if diarize { form.add("diarize", "true") }
        if let code = CloudTranscription.languageCode(language) { form.add("language_code", code) }
        ProviderHTTP.setMultipartBody(&request, form)
        return request
    }

    /// `words` (`word`, `spacing`, `audio_event`) to segments; spacing stays in the text, audio events are dropped.
    /// `longSegment` as in `CloudTranscription.segments` (0: a segment per sentence). Pure, for tests.
    public static func parse(_ data: Data, longSegment: Double = 15) throws -> [CloudSegment] {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        let response = try decoder.decode(SpeechToTextResponse.self, from: data)
        var words: [CloudTranscription.Word] = []
        for word in response.words ?? [] {
            switch word.type {
            case "spacing":
                if !words.isEmpty { words[words.count - 1].text += word.text }
            case "audio_event":
                continue
            default:
                guard let start = word.start, let end = word.end else { continue }
                words.append(.init(start: start, end: end, text: word.text, speaker: word.speakerId))
            }
        }
        return CloudTranscription.segments(
            from: words, separator: "", language: response.languageCode, longSegment: longSegment)
    }
}

private struct SpeechToTextResponse: Decodable {
    struct Word: Decodable {
        var text: String
        var start: Double?
        var end: Double?
        var type: String?
        var speakerId: String?
    }

    var languageCode: String?
    var words: [Word]?
}

extension ElevenLabsTranscriber {
    /// Scribe v2 Realtime: `wss://api.elevenlabs.io/v1/speech-to-text/realtime?...` (settings in the query, no start
    /// message), `xi-api-key` handshake header, 16 kHz PCM sent as base64 in JSON. The server commits on silence
    /// (`commit_strategy=vad`); `finalize` and `closeMessages` also send a manual commit (when audio came since the last
    /// one) so the last stretch becomes final. Partials have no times (display only); finals are `committed_transcript_with_timestamps` words in
    /// seconds (the plain `committed_transcript` repeats them without times and is ignored). No streaming speakers
    /// and no end message: once closing, the timed answer to the last manual commit gives `ended` (even without
    /// words) and `isDrained`, and the runner closes the socket. A manual commit counts as answered by the next
    /// timed commit message (VAD commits answer too, so the count stops at 0); the plain one is not waited for,
    /// since its order against the timed one is not documented.
    public struct Realtime: RealtimeAdapter {
        public static let models = ["scribe_v2_realtime"]
        public static let endpoint = URL(string: "wss://api.elevenlabs.io/v1/speech-to-text/realtime")!

        public let model: String
        private let apiKey: String
        private let language: String?
        /// Audio was framed since the last manual commit: a commit without new audio could be `commit_throttled`,
        /// and the server closes after an error.
        private var uncommitted = false
        /// Manual commits sent and not yet answered by a `committed_transcript_with_timestamps`.
        private var unansweredCommits = 0
        private var closing = false

        public init(apiKey: String, model: String, language: String?, diarize: Bool) {
            self.apiKey = apiKey
            self.model = model
            self.language = CloudTranscription.languageCode(language)
        }

        public var headers: [String: String] { ["xi-api-key": apiKey] }

        public func socketURL(sessionAnswer: Data?) throws -> URL {
            var query = [
                URLQueryItem(name: "model_id", value: model),
                URLQueryItem(name: "audio_format", value: "pcm_\(sampleRate)"),
                URLQueryItem(name: "commit_strategy", value: "vad"),
                URLQueryItem(name: "include_timestamps", value: "true"),
            ]
            if let language { query.append(URLQueryItem(name: "language_code", value: language)) }
            return Self.endpoint.appending(queryItems: query)
        }

        public mutating func frame(_ pcm: Data) -> RealtimeMessage {
            uncommitted = true
            return chunk(pcm.base64EncodedString(), commit: false)
        }

        /// A manual commit, only when audio was framed since the last one.
        public mutating func finalize(at streamSeconds: Double) -> [RealtimeMessage] {
            guard uncommitted else { return [] }
            uncommitted = false
            unansweredCommits += 1
            return [chunk("", commit: true)]
        }

        public mutating func closeMessages() -> [RealtimeMessage] {
            closing = true
            return finalize(at: 0)
        }

        /// Closing, and every manual commit is answered.
        public var isDrained: Bool { closing && unansweredCommits == 0 }

        /// `session_started` starts; a non-empty `partial_transcript` is a partial; the timed words of
        /// `committed_transcript_with_timestamps` become finals (a segment per sentence: without speakers, the diarizer
        /// after Stop labels each one, so a commit spanning two speakers is not one speaker's); a
        /// message with an `error` fails with its `message_type`; `ended` follows the answer to the last commit
        /// once closing.
        public mutating func parse(_ message: RealtimeMessage) throws -> [RealtimeEvent] {
            guard let envelope = try CloudRealtime.decode(LiveEnvelope.self, from: message) else { return [] }
            if envelope.error != nil { return [.failed(kind: CloudRealtime.errorKind(envelope.messageType))] }
            switch envelope.messageType {
            case "session_started":
                return [.started(expiresAt: nil)]
            case "partial_transcript":
                let text = envelope.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                return text.isEmpty ? [] : [.partial(text: text, start: nil, end: nil, speaker: nil)]
            case "committed_transcript_with_timestamps":
                let segments = try ElevenLabsTranscriber.parse(CloudRealtime.payload(message), longSegment: 0)
                let finals: [RealtimeEvent] = segments.map {
                    var segment = $0
                    segment.speaker = nil
                    return .final(segment)
                }
                let answered = unansweredCommits > 0
                unansweredCommits = max(unansweredCommits - 1, 0)
                return finals + (answered && isDrained ? [.ended] : [])
            default:
                return []
            }
        }

        private func chunk(_ base64: String, commit: Bool) -> RealtimeMessage {
            .text(#"{"message_type":"input_audio_chunk","audio_base_64":"\#(base64)","commit":\#(commit),"sample_rate":\#(sampleRate)}"#)
        }
    }
}

private struct LiveEnvelope: Decodable {
    var messageType: String?
    var text: String?
    var error: String?
}
