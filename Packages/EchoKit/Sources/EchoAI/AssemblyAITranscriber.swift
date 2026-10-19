import EchoCore
import Foundation

/// AssemblyAI: `POST /v2/upload` (audio bytes) -> `POST /v2/transcript` -> `GET /v2/transcript/{id}` every 3 s while
/// the job is queued or processing. `Authorization: <key>` (no scheme). The model is AssemblyAI's default
/// (Universal-3.5 Pro, falling back to Universal-2 for languages it lacks, e.g. Vietnamese).
public struct AssemblyAITranscriber: CloudTranscriber {
    public static let baseURL = URL(string: "https://api.assemblyai.com")!
    static let pollInterval: Duration = .seconds(3)

    private let apiKey: String
    private let session: URLSession

    /// `session`: nil for the shared session that refuses cross-origin redirects (a test seam otherwise).
    public init(apiKey: String, session: URLSession? = nil) {
        self.apiKey = apiKey
        self.session = session ?? ProviderHTTP.session
    }

    public func transcribe(file: URL, language: String?, diarize: Bool) async throws -> [CloudSegment] {
        let (uploaded, _) = try await ProviderHTTP.send(
            Self.makeUploadRequest(apiKey: apiKey, audio: try Data(contentsOf: file)), session: session, retry: true)
        let uploadURL = try JSONDecoder().decode(UploadResponse.self, from: uploaded).uploadUrl
        // Billed: a retry after a lost answer could create a second job.
        let (created, _) = try await ProviderHTTP.send(
            try Self.makeTranscriptRequest(apiKey: apiKey, audioURL: uploadURL, language: language, diarize: diarize),
            session: session, retry: false)
        var job = try Self.parseTranscript(created)
        // Polls only while this job runs; cancelling the task ends the sleep and the loop.
        while job.segments == nil {
            try await Task.sleep(for: Self.pollInterval)
            let (data, _) = try await ProviderHTTP.send(
                Self.makePollRequest(apiKey: apiKey, id: job.id), session: session, retry: true)
            job = try Self.parseTranscript(data)
        }
        return job.segments ?? []
    }

    /// Pure, for tests.
    public static func makeUploadRequest(apiKey: String, audio: Data) -> URLRequest {
        var request = request("v2/upload", apiKey: apiKey)
        request.httpMethod = "POST"
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.httpBody = audio
        return request
    }

    /// `speaker_labels` follows `diarize`; `language_code` when a language is set, else `language_detection`.
    /// Pure, for tests.
    public static func makeTranscriptRequest(apiKey: String, audioURL: String, language: String?, diarize: Bool) throws -> URLRequest {
        var request = request("v2/transcript", apiKey: apiKey)
        let code = CloudTranscription.languageCode(language)
        try ProviderHTTP.setJSONBody(&request, TranscriptBody(
            audioUrl: audioURL, speakerLabels: diarize, languageCode: code, languageDetection: code == nil ? true : nil))
        return request
    }

    /// Settings' Test: `GET /v2/transcript?limit=1` lists at most one past transcript (free, no audio).
    public static func makeKeyCheckRequest(apiKey: String) -> URLRequest {
        var request = request("v2/transcript", apiKey: apiKey)
        request.url = request.url?.appending(queryItems: [URLQueryItem(name: "limit", value: "1")])
        request.timeoutInterval = ProviderHTTP.listTimeout
        return request
    }

    public static func makePollRequest(apiKey: String, id: String) -> URLRequest {
        request("v2/transcript/\(id)", apiKey: apiKey)
    }

    /// The job id, and its segments once completed (nil while queued or processing); a failed job throws.
    /// Utterances when speakers were labeled, else words grouped by pause. Pure, for tests.
    public static func parseTranscript(_ data: Data) throws -> (id: String, segments: [CloudSegment]?) {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        let response = try decoder.decode(TranscriptResponse.self, from: data)
        switch response.status {
        case "completed":
            let language = response.languageCode
            if let utterances = response.utterances, !utterances.isEmpty {
                return (response.id, utterances.compactMap { utterance in
                    let text = utterance.text.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !text.isEmpty else { return nil }
                    return CloudSegment(
                        start: Double(utterance.start) / 1000, end: Double(utterance.end) / 1000, text: text,
                        speaker: utterance.speaker, language: language)
                })
            }
            let words = (response.words ?? []).map {
                CloudTranscription.Word(start: Double($0.start) / 1000, end: Double($0.end) / 1000, text: $0.text, speaker: $0.speaker)
            }
            return (response.id, CloudTranscription.segments(from: words, separator: " ", language: language))
        case "error":
            throw LLMError.provider(response.error ?? "AssemblyAI could not transcribe the audio.")
        default:
            return (response.id, nil)
        }
    }

    private static func request(_ path: String, apiKey: String) -> URLRequest {
        var request = URLRequest(url: baseURL.appending(path: path), timeoutInterval: CloudTranscription.requestTimeout)
        request.setValue(apiKey, forHTTPHeaderField: "Authorization")
        return request
    }
}

private struct TranscriptBody: Encodable {
    var audioUrl: String
    var speakerLabels: Bool
    var languageCode: String?
    var languageDetection: Bool?
}

private struct UploadResponse: Decodable {
    var uploadUrl: String

    enum CodingKeys: String, CodingKey { case uploadUrl = "upload_url" }
}

private struct TranscriptResponse: Decodable {
    struct Utterance: Decodable {
        var start: Int
        var end: Int
        var text: String
        var speaker: String?
    }

    var id: String
    var status: String
    var error: String?
    var languageCode: String?
    var utterances: [Utterance]?
    var words: [Utterance]?
}
