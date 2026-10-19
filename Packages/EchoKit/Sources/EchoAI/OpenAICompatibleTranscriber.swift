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
            } else if model.hasPrefix("gpt-") {
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
