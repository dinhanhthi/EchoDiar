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
