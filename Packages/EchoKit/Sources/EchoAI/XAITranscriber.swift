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
