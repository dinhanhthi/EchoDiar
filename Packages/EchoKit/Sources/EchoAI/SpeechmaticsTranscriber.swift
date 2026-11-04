import EchoCore
import Foundation

/// Speechmatics batch: `POST /v2/jobs` (multipart `config` + `data_file`) -> `GET /v2/jobs/{id}` every 3 s while the
/// job runs -> `GET /v2/jobs/{id}/transcript?format=json-v2`. `Authorization: Bearer <key>`. One fixed region
/// (`eu1`): a job can only be read from the region that created it. Speakers are `S1`, `S2`, ...
public struct SpeechmaticsTranscriber: CloudTranscriber {
    public static let baseURL = URL(string: "https://eu1.asr.api.speechmatics.com")!
    public static let defaultModel = "enhanced"
    static let pollInterval: Duration = .seconds(3)

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
        // Billed: a retry after a lost answer could create a second job.
        let (created, _) = try await ProviderHTTP.send(
            try Self.makeJobRequest(
                apiKey: apiKey, model: model, audio: try Data(contentsOf: file), fileName: file.lastPathComponent,
                language: language, diarize: diarize),
            session: session, retry: false)
        let id = try JSONDecoder().decode(CreatedJob.self, from: created).id
        // Polls only while this job runs; cancelling the task ends the sleep and the loop.
        var done = false
        while !done {
            try await Task.sleep(for: Self.pollInterval)
            let (data, _) = try await ProviderHTTP.send(
                Self.makePollRequest(apiKey: apiKey, id: id), session: session, retry: true)
            done = try Self.parseJobStatus(data)
        }
        let (transcript, _) = try await ProviderHTTP.send(
            Self.makeTranscriptRequest(apiKey: apiKey, id: id), session: session, retry: true)
        return try Self.parse(transcript)
    }

    /// The `config` field comes before the audio: `transcription_config` with `model`, the language (`auto` to
    /// detect; `multi` plus `language_hints` for the multilingual `melia` models, which refuse `auto`) and
    /// `diarization: speaker` when asked. Pure, for tests.
    public static func makeJobRequest(
        apiKey: String, model: String = defaultModel, audio: Data, fileName: String, language: String?, diarize: Bool
    ) throws -> URLRequest {
        var request = request("v2/jobs", apiKey: apiKey)
        let code = CloudTranscription.languageCode(language)
        let multilingual = model.lowercased().hasPrefix("melia")
        let config = JobConfig(transcriptionConfig: .init(
            language: multilingual ? "multi" : code ?? "auto", model: model, diarization: diarize ? "speaker" : nil,
            languageHints: multilingual ? code.map { [$0] } : nil))
        let encoder = JSONEncoder()
        encoder.keyEncodingStrategy = .convertToSnakeCase
        var form = MultipartForm()
        form.add("config", String(decoding: try encoder.encode(config), as: UTF8.self))
        form.addFile("data_file", fileName: fileName, mimeType: CloudTranscription.mimeType(for: URL(filePath: fileName)), data: audio)
        ProviderHTTP.setMultipartBody(&request, form)
        return request
    }

    /// Settings' Test: `GET /v2/jobs?limit=1` lists at most one past job (free, no audio). Pure, for tests.
    public static func makeKeyCheckRequest(apiKey: String) -> URLRequest {
        var request = request("v2/jobs", apiKey: apiKey)
        request.url = request.url?.appending(queryItems: [URLQueryItem(name: "limit", value: "1")])
        request.timeoutInterval = ProviderHTTP.listTimeout
        return request
    }

    public static func makePollRequest(apiKey: String, id: String) -> URLRequest {
        request("v2/jobs/\(id)", apiKey: apiKey)
    }

    public static func makeTranscriptRequest(apiKey: String, id: String) -> URLRequest {
        var request = request("v2/jobs/\(id)/transcript", apiKey: apiKey)
        request.url = request.url?.appending(queryItems: [URLQueryItem(name: "format", value: "json-v2")])
        return request
    }

    /// True once the job is `done`, false while it runs; a `rejected`, `deleted` or `expired` job throws. Pure, for
    /// tests.
    public static func parseJobStatus(_ data: Data) throws -> Bool {
        let job = try JSONDecoder().decode(JobDetails.self, from: data).job
        switch job.status {
        case "done": return true
        case "rejected", "deleted", "expired":
            throw LLMError.provider(job.errors?.first?.message ?? "Speechmatics could not transcribe the audio (\(job.status)).")
        default: return false
        }
    }

    /// json-v2 `results` to segments. Each word carries its leading space; a `punctuation` item attaches per
    /// `attaches_to`: `previous` (the default) and `both` to the word before (`both` also glues the next word to
    /// it), `next` to the word after, `none` stands alone. Speaker `UU` (unknown) becomes no label. Pure, for tests.
    public static func parse(_ data: Data) throws -> [CloudSegment] {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        let response = try decoder.decode(TranscriptResponse.self, from: data)
        var words: [CloudTranscription.Word] = []
        var language: String?
        var glued = false
        var prefix = ""
        for item in response.results ?? [] {
            guard let alternative = item.alternatives?.first, !alternative.content.isEmpty else { continue }
            language = language ?? alternative.language
            let attachment = item.type == "punctuation" ? item.attachesTo ?? "previous" : nil
            if attachment == "previous" || attachment == "both", !words.isEmpty {
                words[words.count - 1].text += prefix + alternative.content
                words[words.count - 1].end = max(words[words.count - 1].end, item.endTime)
                prefix = ""
                glued = attachment == "both"
                continue
            }
            if attachment == "next" {
                prefix += (prefix.isEmpty && !glued ? " " : "") + alternative.content
                glued = true
                continue
            }
            let speaker = alternative.speaker == "UU" ? nil : alternative.speaker
            let space = glued || !prefix.isEmpty ? "" : " "
            words.append(.init(
                start: item.startTime, end: item.endTime, text: space + prefix + alternative.content, speaker: speaker))
            prefix = ""
            glued = false
        }
        return CloudTranscription.segments(from: words, separator: "", language: language)
    }

    private static func request(_ path: String, apiKey: String) -> URLRequest {
        var request = URLRequest(url: baseURL.appending(path: path), timeoutInterval: CloudTranscription.requestTimeout)
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        return request
    }
}

private struct JobConfig: Encodable {
    struct TranscriptionConfig: Encodable {
        var language: String
        var model: String
        var diarization: String?
        var languageHints: [String]?
    }

    var type = "transcription"
    var transcriptionConfig: TranscriptionConfig
}

private struct CreatedJob: Decodable {
    var id: String
}

private struct JobDetails: Decodable {
    struct Job: Decodable {
        struct Failure: Decodable { var message: String? }
        var status: String
        var errors: [Failure]?
    }

    var job: Job
}

private struct TranscriptResponse: Decodable {
    struct Item: Decodable {
        struct Alternative: Decodable {
            var content: String
            var language: String?
            var speaker: String?
        }

        var type: String?
        var startTime: Double
        var endTime: Double
        var attachesTo: String?
        var alternatives: [Alternative]?
    }

    var results: [Item]?
}

extension SpeechmaticsTranscriber {
    /// Realtime `wss://eu.rt.speechmatics.com/v2` (one fixed region), `Authorization: Bearer` handshake header, a
    /// `StartRecognition` start message (raw pcm_s16le 16 kHz, the model, partials, `max_delay` 2 s, speaker
    /// diarization with `standard`/`enhanced`), binary audio frames (`AddAudio`), each counted for `EndOfStream`'s
    /// `last_seq_no`. `RecognitionStarted` starts; `AddPartialTranscript` is a partial, `AddTranscript` finals
    /// (json-v2 results, read like the batch transcript: punctuation attached, speakers `S1`, `S2`, `UU` = none),
    /// times in seconds from the stream start. `ForceEndOfUtterance` flushes; `EndOfStream` ends (answered by
    /// `EndOfTranscript`). Realtime has no automatic language: Auto is refused (except the multilingual `melia`
    /// models, sent `multi`). Keep-alive is WebSocket ping/pong (3 min idle close), not a message.
    public struct Realtime: RealtimeAdapter {
        public static let models = ["enhanced", "standard", "melia-1", "oak-1"]
        public static let endpoint = URL(string: "wss://eu.rt.speechmatics.com/v2")!

        public let model: String
        private let apiKey: String
        private let language: String?
        private let diarize: Bool
        /// `AddAudio` frames sent: Speechmatics numbers them from 1.
        private var framesSent = 0

        public init(apiKey: String, model: String, language: String?, diarize: Bool) {
            self.apiKey = apiKey
            self.model = model
            self.language = CloudTranscription.languageCode(language)
            self.diarize = diarize
        }

        /// Only `standard` and `enhanced` label speakers in realtime.
        public var streamsSpeakers: Bool { diarize && ["standard", "enhanced"].contains(model) }
        public var headers: [String: String] { ["Authorization": "Bearer \(apiKey)"] }

        public func socketURL(sessionAnswer: Data?) throws -> URL { Self.endpoint }

        public func startMessages() throws -> [RealtimeMessage] {
            var config: [String: Any] = ["model": model, "enable_partials": true, "max_delay": 2]
            if model.lowercased().hasPrefix("melia") {
                config["language"] = "multi"
                if let language { config["language_hints"] = [language] }
            } else if let language {
                config["language"] = language
            } else {
                throw LLMError.provider("Speechmatics live transcription needs a language: choose one instead of Auto.")
            }
            if streamsSpeakers { config["diarization"] = "speaker" }
            return [try CloudRealtime.json([
                "message": "StartRecognition",
                "audio_format": ["type": "raw", "encoding": "pcm_s16le", "sample_rate": sampleRate],
                "transcription_config": config,
            ])]
        }

        public mutating func frame(_ pcm: Data) -> RealtimeMessage {
            framesSent += 1
            return .data(pcm)
        }

        public mutating func finalize(at streamSeconds: Double) -> [RealtimeMessage] {
            [.text(#"{"message":"ForceEndOfUtterance"}"#)]
        }

        public mutating func closeMessages() -> [RealtimeMessage] {
            [.text(#"{"message":"EndOfStream","last_seq_no":\#(framesSent)}"#)]
        }

        public mutating func parse(_ message: RealtimeMessage) throws -> [RealtimeEvent] {
            guard let live = try CloudRealtime.decode(LiveMessage.self, from: message) else { return [] }
            switch live.message {
            case "RecognitionStarted":
                return [.started(expiresAt: nil)]
            case "AddPartialTranscript":
                let text = live.metadata?.transcript?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                guard !text.isEmpty else { return [] }
                let speaker = live.results?.lazy.compactMap { $0.alternatives?.first?.speaker }.first { $0 != "UU" }
                return [.partial(text: text, start: live.metadata?.startTime, end: live.metadata?.endTime, speaker: speaker)]
            case "AddTranscript":
                return try SpeechmaticsTranscriber.parse(CloudRealtime.payload(message)).map { .final($0) }
            case "EndOfTranscript":
                return [.ended]
            case "Error":
                return [.failed(kind: CloudRealtime.errorKind(live.type ?? live.message))]
            default:
                return []
            }
        }
    }
}

private struct LiveMessage: Decodable {
    /// `EndOfUtterance` (the answer to `ForceEndOfUtterance`) has metadata without a transcript.
    struct Metadata: Decodable {
        var transcript: String?
        var startTime: Double?
        var endTime: Double?
    }

    struct Result: Decodable {
        struct Alternative: Decodable { var speaker: String? }
        var alternatives: [Alternative]?
    }

    var message: String?
    var type: String?
    var metadata: Metadata?
    var results: [Result]?
}
