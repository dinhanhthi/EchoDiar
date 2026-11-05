import EchoCore
import Foundation

/// `POST {base}/audio/transcriptions` (multipart) for OpenAI, Groq, Mistral and custom OpenAI-compatible endpoints.
/// The form depends on the model: `*-diarize` gets `diarized_json` with `chunking_strategy=auto`, other `gpt-*`
/// models only return text (`json`, one segment for the file), Mistral gets `timestamp_granularities=segment` (and
/// `diarize=true` when asked), everything else `verbose_json` with segment timestamps.
public struct OpenAICompatibleTranscriber: CloudTranscriber {
    /// Transcription models per preset id, the first one being the default (docs/research/asr-diarization-models.md F).
    public static let presetModels: [String: [String]] = [
        "openai": ["gpt-4o-transcribe-diarize", "gpt-4o-transcribe", "whisper-1"],
        "groq": ["whisper-large-v3-turbo"],
        "mistral": ["voxtral-mini-latest"],
    ]

    public let config: ProviderConfig
    public let model: String
    private let apiKey: String?
    private let session: URLSession

    /// `session`: nil for the shared session that refuses cross-origin redirects (a test seam otherwise).
    public init(config: ProviderConfig, model: String, apiKey: String?, session: URLSession? = nil) {
        self.config = config
        self.model = model
        self.apiKey = apiKey
        self.session = session ?? ProviderHTTP.session
    }

    /// Mistral takes up to 3 h per request; OpenAI, Groq (free tier) and unknown endpoints get the 25 MB cap.
    public var maxUploadBytes: Int? { Self.isMistral(config) ? nil : CloudTranscription.cappedUploadBytes }

    /// False for the `json` (text-only) models, whose answer is one segment over the file.
    public var returnsSegmentTimestamps: Bool { Self.isMistral(config) || !Self.returnsTextOnly(model) }

    public func transcribe(file: URL, language: String?, diarize: Bool) async throws -> [CloudSegment] {
        let request = try Self.makeRequest(
            config: config, apiKey: apiKey, model: model, audio: try Data(contentsOf: file), fileName: file.lastPathComponent,
            language: language, diarize: diarize)
        let (data, _) = try await ProviderHTTP.send(request, session: session, retry: false)
        return try Self.parse(data, duration: CloudTranscription.duration(of: file))
    }

    /// Pure, for tests.
    public static func makeRequest(
        config: ProviderConfig, apiKey: String?, model: String, audio: Data, fileName: String, language: String?,
        diarize: Bool
    ) throws -> URLRequest {
        let url = try ProviderHTTP.baseURL(config).appending(path: "audio/transcriptions")
        var request = ProviderHTTP.request(url, config: config, apiKey: apiKey, timeout: CloudTranscription.requestTimeout)
        var form = MultipartForm()
        form.addFile("file", fileName: fileName, mimeType: CloudTranscription.mimeType(for: URL(filePath: fileName)), data: audio)
        form.add("model", model)
        let language = CloudTranscription.languageCode(language)
        if isMistral(config) {
            // Mistral: `timestamp_granularities` cannot be combined with `language`, and timestamps are required.
            form.add("timestamp_granularities", "segment")
            if diarize { form.add("diarize", "true") }
        } else {
            if let language { form.add("language", language) }
            if model.hasSuffix("-diarize") {
                form.add("response_format", "diarized_json")
                form.add("chunking_strategy", "auto")
            } else if returnsTextOnly(model) {
                form.add("response_format", "json")
            } else {
                form.add("response_format", "verbose_json")
                form.add("timestamp_granularities[]", "segment")
            }
        }
        ProviderHTTP.setMultipartBody(&request, form)
        return request
    }

    /// Segments with timestamps; a text-only answer becomes one segment over `duration` seconds. Pure, for tests.
    public static func parse(_ data: Data, duration: Double) throws -> [CloudSegment] {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        let response = try decoder.decode(TranscriptionResponse.self, from: data)
        // Whisper's `verbose_json` names the language (`english`); only codes are kept.
        let language = response.language.flatMap { $0.count <= 3 ? $0 : nil }
        if let segments = response.segments, !segments.isEmpty {
            return segments.compactMap { segment in
                let text = segment.text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !text.isEmpty else { return nil }
                return CloudSegment(
                    start: segment.start, end: segment.end, text: text, speaker: segment.speaker ?? segment.speakerId,
                    language: language)
            }
        }
        let text = response.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return text.isEmpty ? [] : [CloudSegment(start: 0, end: duration, text: text, language: language)]
    }

    private static func isMistral(_ config: ProviderConfig) -> Bool { config.preset == "mistral" }

    /// Off Mistral: a `gpt-*` model without `-diarize` is sent `response_format=json`, text with no timestamps.
    private static func returnsTextOnly(_ model: String) -> Bool { !model.hasSuffix("-diarize") && model.hasPrefix("gpt-") }
}

private struct TranscriptionResponse: Decodable {
    struct Segment: Decodable {
        var start: Double
        var end: Double
        var text: String
        /// OpenAI `diarized_json`.
        var speaker: String?
        /// Mistral.
        var speakerId: String?
    }

    var text: String?
    var language: String?
    var segments: [Segment]?
}

extension OpenAICompatibleTranscriber {
    /// OpenAI realtime transcription (the `openai` preset only): `wss://api.openai.com/v1/realtime?intent=transcription`,
    /// `Authorization: Bearer` handshake header, a `session.update` start message (24 kHz `audio/pcm`, the model,
    /// manual commits: `turn_detection` null), audio as base64 in `input_audio_buffer.append` JSON frames at
    /// **24 kHz**. There are no word or segment times: `finalize(at:)` commits the audio since the last commit and
    /// remembers that stretch of stream time; `committed` ties it to an `item_id`, and that item's `completed`
    /// transcript is the final over the stretch (turns may complete out of order). Deltas accumulate per item into
    /// partials without times. No speakers. `session.created` starts (its `expires_at` caps the session). No close
    /// message: `closeMessages` commits the rest (by the audio framed) and `ended` follows the last pending
    /// transcript; `isDrained` says so at once when nothing is left to commit or wait for.
    public struct Realtime: RealtimeAdapter {
        /// The default's `session.update` body is documented (`languages`, `delay`); the others get the generic
        /// `language` field (not verified for them).
        public static let models = ["gpt-live-transcribe", "gpt-transcribe", "gpt-realtime-whisper"]
        public static let endpoint = URL(string: "wss://api.openai.com/v1/realtime")!
        /// OpenAI refuses a commit of less than 100 ms of audio.
        static let minimumCommit = 0.1

        public let model: String
        private let apiKey: String
        private let language: String?
        /// Stream seconds framed so far, and where the last commit ended.
        private var framedSeconds = 0.0
        private var committedThrough = 0.0
        /// Commits not yet acknowledged, oldest first, then the acknowledged ones by item.
        private var pendingSpans: [ClosedRange<Double>] = []
        private var spans: [String: ClosedRange<Double>] = [:]
        private var deltas: [String: String] = [:]
        private var closing = false

        public init(apiKey: String, model: String, language: String?, diarize: Bool) {
            self.apiKey = apiKey
            self.model = model
            self.language = CloudTranscription.languageCode(language)
        }

        public var sampleRate: Int { 24_000 }
        public var headers: [String: String] { ["Authorization": "Bearer \(apiKey)"] }

        public func socketURL(sessionAnswer: Data?) throws -> URL {
            Self.endpoint.appending(queryItems: [URLQueryItem(name: "intent", value: "transcription")])
        }

        /// `gpt-live-transcribe` takes `languages` and `delay`; another model gets the generic `language`.
        public func startMessages() throws -> [RealtimeMessage] {
            var transcription: [String: Any] = ["model": model]
            if model == Self.models[0] {
                transcription["delay"] = "low"
                if let language { transcription["languages"] = [language] }
            } else if let language {
                transcription["language"] = language
            }
            let input: [String: Any] = [
                "format": ["type": "audio/pcm", "rate": sampleRate],
                "transcription": transcription,
                "turn_detection": NSNull(),
            ]
            return [try CloudRealtime.json([
                "type": "session.update",
                "session": ["type": "transcription", "audio": ["input": input]],
            ])]
        }

        public mutating func frame(_ pcm: Data) -> RealtimeMessage {
            framedSeconds += Double(pcm.count / 2) / Double(sampleRate)
            return .text(#"{"type":"input_audio_buffer.append","audio":"\#(pcm.base64EncodedString())"}"#)
        }

        /// Nothing when under 100 ms of audio came since the last commit (an empty commit is an error).
        public mutating func finalize(at streamSeconds: Double) -> [RealtimeMessage] {
            guard streamSeconds - committedThrough >= Self.minimumCommit else { return [] }
            pendingSpans.append(committedThrough...streamSeconds)
            committedThrough = streamSeconds
            return [.text(#"{"type":"input_audio_buffer.commit"}"#)]
        }

        public mutating func closeMessages() -> [RealtimeMessage] {
            closing = true
            return finalize(at: framedSeconds)
        }

        /// Closing, and every commit has its transcript (or failed).
        public var isDrained: Bool { closing && pendingSpans.isEmpty && spans.isEmpty }

        public mutating func parse(_ message: RealtimeMessage) throws -> [RealtimeEvent] {
            guard let live = try CloudRealtime.decode(LiveEvent.self, from: message) else { return [] }
            switch live.type {
            case "session.created":
                return [.started(expiresAt: live.session?.expiresAt.map { Date(timeIntervalSince1970: $0) })]
            case "input_audio_buffer.committed":
                guard let item = live.itemId, !pendingSpans.isEmpty else { return [] }
                spans[item] = pendingSpans.removeFirst()
                return []
            case "conversation.item.input_audio_transcription.delta":
                guard let item = live.itemId, let delta = live.delta else { return [] }
                let text = (deltas[item] ?? "") + delta
                deltas[item] = text
                let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                return trimmed.isEmpty ? [] : [.partial(text: trimmed, start: nil, end: nil, speaker: nil)]
            case "conversation.item.input_audio_transcription.completed":
                guard let item = live.itemId else { return [] }
                deltas[item] = nil
                var events: [RealtimeEvent] = []
                let text = live.transcript?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                if let span = spans.removeValue(forKey: item), !text.isEmpty {
                    events.append(.final(CloudSegment(start: span.lowerBound, end: span.upperBound, text: text)))
                }
                return events + endedIfDone()
            case "conversation.item.input_audio_transcription.failed":
                if let item = live.itemId {
                    deltas[item] = nil
                    spans[item] = nil
                }
                return [.failed(kind: CloudRealtime.errorKind(live.error?.code ?? live.type))]
            case "error":
                return [.failed(kind: CloudRealtime.errorKind(live.error?.code ?? live.error?.type ?? live.type))]
            default:
                return []
            }
        }

        /// `ended` once closing and every commit has its transcript.
        private func endedIfDone() -> [RealtimeEvent] {
            isDrained ? [.ended] : []
        }
    }
}

private struct LiveEvent: Decodable {
    struct Session: Decodable { var expiresAt: Double? }
    struct Failure: Decodable {
        var type: String?
        var code: String?
    }

    var type: String?
    var session: Session?
    var itemId: String?
    var delta: String?
    var transcript: String?
    var error: Failure?
}
