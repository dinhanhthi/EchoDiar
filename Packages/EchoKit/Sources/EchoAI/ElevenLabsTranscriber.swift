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
    /// Pure, for tests.
    public static func parse(_ data: Data) throws -> [CloudSegment] {
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
        return CloudTranscription.segments(from: words, separator: "", language: response.languageCode)
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
