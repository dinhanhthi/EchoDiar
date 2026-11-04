import EchoCore
import Foundation

/// Soniox async: `POST /v1/files` (multipart) -> `POST /v1/transcriptions` -> `GET /v1/transcriptions/{id}` every 3 s
/// while the job is queued or processing -> `GET /v1/transcriptions/{id}/transcript`, then the transcription and the
/// file are deleted. `Authorization: Bearer <key>`. Times are milliseconds on sub-word tokens; speakers are `1`, `2`, ...
public struct SonioxTranscriber: CloudTranscriber {
    public static let baseURL = URL(string: "https://api.soniox.com")!
    /// Required: Soniox has no server default.
    public static let defaultModel = "stt-async-v6"
    /// Soniox refuses files over 300 min. At the export's 32 kbps (4,000 B/s) that is 72 MB; the budget is 283 min
    /// (67,920,000 B), about 6 % under the cap for container overhead and a lower real bit rate, so longer
    /// recordings are split instead of refused.
    static let uploadBytes = 283 * 60 * CloudTranscription.bitRate / 8
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
        // Sent once: a retry after a lost answer would leave a second stored file.
        let (uploaded, _) = try await ProviderHTTP.send(
            Self.makeUploadRequest(apiKey: apiKey, audio: try Data(contentsOf: file), fileName: file.lastPathComponent),
            session: session, retry: false)
        let fileID = try JSONDecoder().decode(Created.self, from: uploaded).id
        var transcriptionID: String?
        // Deleted whatever the answer, also on a cancel (Soniox would keep them for 30 days).
        defer { delete(transcriptionID: transcriptionID, fileID: fileID) }
        // Billed: a retry after a lost answer could create a second job.
        let (created, _) = try await ProviderHTTP.send(
            try Self.makeTranscriptionRequest(apiKey: apiKey, model: model, fileID: fileID, language: language, diarize: diarize),
            session: session, retry: false)
        var job = try Self.parseStatus(created)
        transcriptionID = job.id
        // Polls only while this job runs; cancelling the task ends the sleep and the loop.
        while !job.completed {
            try await Task.sleep(for: Self.pollInterval)
            let (data, _) = try await ProviderHTTP.send(
                Self.makePollRequest(apiKey: apiKey, id: job.id), session: session, retry: true)
            job = try Self.parseStatus(data)
        }
        let (transcript, _) = try await ProviderHTTP.send(
            Self.makeTranscriptRequest(apiKey: apiKey, id: job.id), session: session, retry: true)
        return try Self.parse(transcript)
    }

    /// Best-effort `DELETE` of the transcription, then of the file, in its own task so it also runs when the
    /// transcription was cancelled; a failure is ignored (Soniox deletes leftovers after 30 days).
    private func delete(transcriptionID: String?, fileID: String) {
        var requests = transcriptionID.map { [Self.makeDeleteTranscriptionRequest(apiKey: apiKey, id: $0)] } ?? []
        requests.append(Self.makeDeleteFileRequest(apiKey: apiKey, id: fileID))
        Task { [session, requests] in
            for request in requests { _ = try? await ProviderHTTP.send(request, session: session, retry: false) }
        }
    }

    /// Pure, for tests.
    public static func makeUploadRequest(apiKey: String, audio: Data, fileName: String) -> URLRequest {
        var request = request("v1/files", apiKey: apiKey)
        var form = MultipartForm()
        form.addFile("file", fileName: fileName, mimeType: CloudTranscription.mimeType(for: URL(filePath: fileName)), data: audio)
        ProviderHTTP.setMultipartBody(&request, form)
        return request
    }

    /// `enable_speaker_diarization` follows `diarize`; `language_hints` when a language is set (else Soniox
    /// detects it). Pure, for tests.
    public static func makeTranscriptionRequest(
        apiKey: String, model: String = defaultModel, fileID: String, language: String?, diarize: Bool
    ) throws -> URLRequest {
        var request = request("v1/transcriptions", apiKey: apiKey)
        try ProviderHTTP.setJSONBody(&request, TranscriptionBody(
            model: model, fileId: fileID, languageHints: CloudTranscription.languageCode(language).map { [$0] },
            enableSpeakerDiarization: diarize))
        return request
    }

    /// Settings' Test: `GET /v1/files?limit=1` lists at most one stored file (free, no audio). Pure, for tests.
    public static func makeKeyCheckRequest(apiKey: String) -> URLRequest {
        var request = request("v1/files", apiKey: apiKey)
        request.url = request.url?.appending(queryItems: [URLQueryItem(name: "limit", value: "1")])
        request.timeoutInterval = ProviderHTTP.listTimeout
        return request
    }

    public static func makePollRequest(apiKey: String, id: String) -> URLRequest {
        request("v1/transcriptions/\(id)", apiKey: apiKey)
    }

    public static func makeTranscriptRequest(apiKey: String, id: String) -> URLRequest {
        request("v1/transcriptions/\(id)/transcript", apiKey: apiKey)
    }

    public static func makeDeleteTranscriptionRequest(apiKey: String, id: String) -> URLRequest {
        deleteRequest("v1/transcriptions/\(id)", apiKey: apiKey)
    }

    public static func makeDeleteFileRequest(apiKey: String, id: String) -> URLRequest {
        deleteRequest("v1/files/\(id)", apiKey: apiKey)
    }

    /// The job id, and whether it is `completed` (false while queued or processing); an `error` job throws. Pure,
    /// for tests.
    public static func parseStatus(_ data: Data) throws -> (id: String, completed: Bool) {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        let response = try decoder.decode(TranscriptionStatus.self, from: data)
        switch response.status {
        case "completed": return (response.id, true)
        case "error": throw LLMError.provider(response.errorMessage ?? "Soniox could not transcribe the audio.")
        default: return (response.id, false)
        }
    }

    /// `tokens` to segments. Tokens are sub-words that carry their own leading space, so they are joined with "" and
    /// grouped like other timed words: a new segment on a speaker change, a pause over 1 s, or a sentence end after
    /// 15 s. A word's tokens have no pause and one speaker, so a word is not cut in practice, and languages written
    /// without spaces still split. Times are divided by 1000, audio events dropped; the language is the first
    /// token's that has one.
    /// Pure, for tests.
    public static func parse(_ data: Data) throws -> [CloudSegment] {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        let response = try decoder.decode(TranscriptResponse.self, from: data)
        let tokens = (response.tokens ?? []).filter { $0.isAudioEvent != true && !$0.text.isEmpty }
        let words = tokens.map {
            CloudTranscription.Word(start: $0.startMs / 1000, end: $0.endMs / 1000, text: $0.text, speaker: $0.speaker)
        }
        return CloudTranscription.segments(from: words, separator: "", language: tokens.lazy.compactMap(\.language).first)
    }

    private static func request(_ path: String, apiKey: String) -> URLRequest {
        var request = URLRequest(url: baseURL.appending(path: path), timeoutInterval: CloudTranscription.requestTimeout)
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        return request
    }

    private static func deleteRequest(_ path: String, apiKey: String) -> URLRequest {
        var request = request(path, apiKey: apiKey)
        request.httpMethod = "DELETE"
        request.timeoutInterval = ProviderHTTP.listTimeout
        return request
    }
}

private struct TranscriptionBody: Encodable {
    var model: String
    var fileId: String
    var languageHints: [String]?
    var enableSpeakerDiarization: Bool
}

private struct Created: Decodable {
    var id: String
}

private struct TranscriptionStatus: Decodable {
    var id: String
    var status: String
    var errorMessage: String?
}

private struct TranscriptResponse: Decodable {
    struct Token: Decodable {
        var text: String
        var startMs: Double
        var endMs: Double
        var speaker: String?
        var language: String?
        var isAudioEvent: Bool?
    }

    var tokens: [Token]?
}
