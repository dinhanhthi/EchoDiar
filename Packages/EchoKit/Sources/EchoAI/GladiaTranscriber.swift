import EchoCore
import Foundation

/// Gladia pre-recorded v2: `POST /v2/upload` (multipart `audio`) -> `POST /v2/pre-recorded` -> `GET
/// /v2/pre-recorded/{id}` every 3 s while the job is queued or processing. `x-gladia-key: <key>`. Utterances become
/// segments; speakers are integers from 0.
public struct GladiaTranscriber: CloudTranscriber {
    public static let baseURL = URL(string: "https://api.gladia.io")!
    public static let defaultModel = "solaria-1"
    /// Gladia refuses requests over 135 min. At the export's 32 kbps (4,000 B/s) the budget is 125 min (30,000,000 B),
    /// about 7 % under the cap for container overhead and a lower real bit rate, so longer recordings are split.
    static let uploadBytes = 125 * 60 * CloudTranscription.bitRate / 8
    static let pollInterval: Duration = .seconds(3)

    public let model: String
    private let apiKey: String
    private let session: URLSession

    public var maxUploadBytes: Int? { Self.uploadBytes }

    /// `session`: nil for the shared session that refuses cross-origin redirects (a test seam otherwise).
    public init(apiKey: String, model: String = defaultModel, session: URLSession? = nil) {
        self.apiKey = apiKey
        self.model = model
        self.session = session ?? ProviderHTTP.session
    }

    public func transcribe(file: URL, language: String?, diarize: Bool) async throws -> [CloudSegment] {
        let (uploaded, _) = try await ProviderHTTP.send(
            Self.makeUploadRequest(apiKey: apiKey, audio: try Data(contentsOf: file), fileName: file.lastPathComponent),
            session: session, retry: true)
        let audioURL = try JSONDecoder().decode(UploadResponse.self, from: uploaded).audioUrl
        // Billed: every accepted job POST is a new job.
        let (created, _) = try await ProviderHTTP.send(
            try Self.makeJobRequest(apiKey: apiKey, model: model, audioURL: audioURL, language: language, diarize: diarize),
            session: session, retry: false)
        // The poll URL is built from the id, never from the answer's `result_url`.
        let id = try JSONDecoder().decode(CreatedJob.self, from: created).id
        var segments: [CloudSegment]?
        // Polls only while this job runs; cancelling the task ends the sleep and the loop.
        while segments == nil {
            try await Task.sleep(for: Self.pollInterval)
            let (data, _) = try await ProviderHTTP.send(
                Self.makePollRequest(apiKey: apiKey, id: id), session: session, retry: true)
            segments = try Self.parseJob(data)
        }
        return segments ?? []
    }

    /// Pure, for tests.
    public static func makeUploadRequest(apiKey: String, audio: Data, fileName: String) -> URLRequest {
        var request = request("v2/upload", apiKey: apiKey)
        var form = MultipartForm()
        form.addFile("audio", fileName: fileName, mimeType: CloudTranscription.mimeType(for: URL(filePath: fileName)), data: audio)
        ProviderHTTP.setMultipartBody(&request, form)
        return request
    }

    /// `diarization` follows `diarize`; `language_config.languages` is the set language, or empty to detect it.
    /// Pure, for tests.
    public static func makeJobRequest(
        apiKey: String, model: String = defaultModel, audioURL: String, language: String?, diarize: Bool
    ) throws -> URLRequest {
        var request = request("v2/pre-recorded", apiKey: apiKey)
        let languages = CloudTranscription.languageCode(language).map { [$0] } ?? []
        try ProviderHTTP.setJSONBody(&request, JobBody(
            audioUrl: audioURL, model: model, diarization: diarize,
            languageConfig: .init(languages: languages, codeSwitching: false)))
        return request
    }

    /// Settings' Test: `GET /v2/pre-recorded?limit=1` lists at most one past job (no audio). Pure, for tests.
    public static func makeKeyCheckRequest(apiKey: String) -> URLRequest {
        var request = request("v2/pre-recorded", apiKey: apiKey)
        request.url = request.url?.appending(queryItems: [URLQueryItem(name: "limit", value: "1")])
        request.timeoutInterval = ProviderHTTP.listTimeout
        return request
    }

    public static func makePollRequest(apiKey: String, id: String) -> URLRequest {
        request("v2/pre-recorded/\(id)", apiKey: apiKey)
    }

    /// The job's segments once `done` (nil while queued or processing); an `error` job throws. Utterances map 1:1
    /// (their `text`, `start`, `end`, integer `speaker`, `language`, else the job's first language); without
    /// utterances, `full_transcript` is one segment over the audio. Pure, for tests.
    public static func parseJob(_ data: Data) throws -> [CloudSegment]? {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        let response = try decoder.decode(JobResponse.self, from: data)
        switch response.status {
        case "done":
            let transcription = response.result?.transcription
            let fallback = transcription?.languages?.first
            if let utterances = transcription?.utterances, !utterances.isEmpty {
                return utterances.compactMap { utterance in
                    let text = utterance.text.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !text.isEmpty else { return nil }
                    return CloudSegment(
                        start: utterance.start, end: utterance.end, text: text, speaker: utterance.speaker.map(String.init),
                        language: utterance.language ?? fallback)
                }
            }
            let text = transcription?.fullTranscript?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            return text.isEmpty ? [] : [CloudSegment(
                start: 0, end: response.result?.metadata?.audioDuration ?? 0, text: text, language: fallback)]
        case "error":
            throw LLMError.provider("Gladia could not transcribe the audio.")
        default:
            return nil
        }
    }

    private static func request(_ path: String, apiKey: String) -> URLRequest {
        var request = URLRequest(url: baseURL.appending(path: path), timeoutInterval: CloudTranscription.requestTimeout)
        request.setValue(apiKey, forHTTPHeaderField: "x-gladia-key")
        return request
    }
}

private struct JobBody: Encodable {
    struct LanguageConfig: Encodable {
        var languages: [String]
        var codeSwitching: Bool
    }

    var audioUrl: String
    var model: String
    var diarization: Bool
    var languageConfig: LanguageConfig
}

private struct UploadResponse: Decodable {
    var audioUrl: String

    enum CodingKeys: String, CodingKey { case audioUrl = "audio_url" }
}

private struct CreatedJob: Decodable {
    var id: String
}

private struct JobResponse: Decodable {
    struct Result: Decodable {
        struct Metadata: Decodable { var audioDuration: Double? }
        struct Transcription: Decodable {
            struct Utterance: Decodable {
                var text: String
                var start: Double
                var end: Double
                var speaker: Int?
                var language: String?
            }

            var languages: [String]?
            var fullTranscript: String?
            var utterances: [Utterance]?
        }

        var metadata: Metadata?
        var transcription: Transcription?
    }

    var status: String
    var result: Result?
}

extension GladiaTranscriber {
    /// Live v2: `POST https://api.gladia.io/v2/live` with `x-gladia-key` (all settings: wav/pcm 16-bit 16 kHz mono,
    /// the model, languages, partial and final transcripts; Gladia refuses 24 kHz) answers a `url`
    /// (`wss://api.gladia.io/v2/live?token=...`) whose session token authenticates the socket: the key is not sent
    /// there, and the URL must never be logged. Binary audio frames. `transcript` messages with `is_final:false` are
    /// partials, `is_final:true` finals (utterance text and times in seconds from the session start). No streaming
    /// speakers, no per-utterance finalize, no keep-alive; `stop_recording` ends (the server closes with 1000). No
    /// ready or end message is read: the session accepts audio once connected, and ends with the close (`isDrained`
    /// stays false).
    public struct Realtime: RealtimeAdapter {
        public static let models = ["solaria-1"]
        public static let endpoint = URL(string: "wss://api.gladia.io/v2/live")!
        public static let initURL: URL? = baseURL.appending(path: "v2/live")

        public let model: String
        private let apiKey: String
        private let language: String?

        public init(apiKey: String, model: String, language: String?, diarize: Bool) {
            self.apiKey = apiKey
            self.model = model
            self.language = CloudTranscription.languageCode(language)
        }

        /// Billed: a new live session each time it is sent.
        public func sessionRequest() throws -> URLRequest? {
            var request = GladiaTranscriber.request("v2/live", apiKey: apiKey)
            request.timeoutInterval = ProviderHTTP.listTimeout
            try ProviderHTTP.setJSONBody(&request, LiveInitBody(
                sampleRate: sampleRate, model: model,
                languageConfig: .init(languages: language.map { [$0] } ?? [], codeSwitching: false)))
            return request
        }

        /// The answer's `url`; `RealtimeConnection.connect` checks it is `wss` on `api.gladia.io`.
        public func socketURL(sessionAnswer: Data?) throws -> URL {
            guard let sessionAnswer, let answer = try? JSONDecoder().decode(LiveSession.self, from: sessionAnswer),
                  let url = URL(string: answer.url) else {
                throw LLMError.provider("Gladia did not start a live session.")
            }
            return url
        }

        public mutating func closeMessages() -> [RealtimeMessage] {
            [.text(#"{"type":"stop_recording"}"#)]
        }

        public mutating func parse(_ message: RealtimeMessage) throws -> [RealtimeEvent] {
            guard let live = try CloudRealtime.decode(LiveMessage.self, from: message) else { return [] }
            if let exception = live.exception { return [.failed(kind: CloudRealtime.errorKind(exception))] }
            switch live.type {
            case "transcript":
                guard let data = live.data, let utterance = data.utterance else { return [] }
                let text = utterance.text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !text.isEmpty else { return [] }
                guard data.isFinal == true else {
                    return [.partial(text: text, start: utterance.start, end: utterance.end, speaker: nil)]
                }
                return [.final(CloudSegment(start: utterance.start, end: utterance.end, text: text, language: utterance.language))]
            case "error":
                return [.failed(kind: CloudRealtime.errorKind(live.type))]
            default:
                return []
            }
        }
    }
}

private struct LiveInitBody: Encodable {
    struct LanguageConfig: Encodable {
        var languages: [String]
        var codeSwitching: Bool
    }

    struct MessagesConfig: Encodable {
        var receivePartialTranscripts = true
        var receiveFinalTranscripts = true
    }

    var encoding = "wav/pcm"
    var sampleRate: Int
    var bitDepth = 16
    var channels = 1
    var model: String
    var languageConfig: LanguageConfig
    var messagesConfig = MessagesConfig()
}

private struct LiveSession: Decodable {
    var url: String
}

private struct LiveMessage: Decodable {
    struct Payload: Decodable {
        struct Utterance: Decodable {
            var text: String
            var start: Double
            var end: Double
            var language: String?
        }

        var isFinal: Bool?
        var utterance: Utterance?
    }

    var type: String?
    var exception: String?
    var data: Payload?
}
