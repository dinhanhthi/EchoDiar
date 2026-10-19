import Accelerate
import AVFoundation
import EchoCore
import Foundation

/// One transcribed stretch from a cloud STT API. Times are seconds from the start of the uploaded file.
public struct CloudSegment: Codable, Equatable, Sendable {
    public var start: Double
    public var end: Double
    public var text: String
    /// The provider's own label ("A", "speaker_1", ...); nil when it did not diarize.
    public var speaker: String?
    /// The language code the provider reported, when it did.
    public var language: String?

    public init(start: Double, end: Double, text: String, speaker: String? = nil, language: String? = nil) {
        self.start = start
        self.end = end
        self.text = text
        self.speaker = speaker
        self.language = language
    }

    /// The same segment moved `seconds` later (a split part's start).
    public func offset(by seconds: Double) -> CloudSegment {
        var segment = self
        segment.start += seconds
        segment.end += seconds
        return segment
    }
}

/// A cloud speech-to-text API. Keys come from the caller (KeychainStore via the provider's id) and only ever go in
/// headers. Cancelling the calling task cancels the request (and any polling).
public protocol CloudTranscriber: Sendable {
    /// Largest file one request may carry; nil when a meeting recording never reaches the provider's cap.
    var maxUploadBytes: Int? { get }
    /// Timestamps come from an LLM and can drift (Gemini); the UI says so.
    var approximateTimestamps: Bool { get }
    /// `file` is a mono audio file. `language`: a BCP-47 code, or nil / "auto" to detect. `diarize`: ask for
    /// speaker labels (only the system channel does).
    func transcribe(file: URL, language: String?, diarize: Bool) async throws -> [CloudSegment]
}

extension CloudTranscriber {
    public var maxUploadBytes: Int? { nil }
    public var approximateTimestamps: Bool { false }
}

/// Upload preparation shared by every cloud transcriber: mono AAC export, splitting under a size cap, offsets.
public enum CloudTranscription {
    /// Part budget for providers with a 25 MB cap (OpenAI, Groq free tier): decimal bytes, so it stays under the
    /// cap whether the provider counts MB or MiB.
    public static let cappedUploadBytes = 24_000_000
    /// Mono AAC at 32 kbps is about 14 MB per hour, so most meetings fit in one request.
    static let bitRate = 32_000
    /// Seconds per loudness window when looking for the quietest place to cut.
    static let levelWindow = 0.1
    /// Long transcription requests send nothing back until the provider is done, so the idle timeout is long.
    static let requestTimeout: TimeInterval = 900
    private static let chunkFrames: AVAudioFrameCount = 16_384

    /// Transcribes `channel` of `source` with `transcriber`: exports it to mono m4a in a temporary folder, splits it
    /// at the quietest moments when it is over `transcriber.maxUploadBytes`, sends the parts one by one and moves
    /// each part's segments by the part's start. The temporary files are removed before returning.
    public static func transcribe(
        source: URL, channel: Int, with transcriber: any CloudTranscriber, language: String?, diarize: Bool
    ) async throws -> [CloudSegment] {
        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("EchoDiar-cloud-\(UUID().uuidString)")
        try fm.createDirectory(at: work, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        defer { try? fm.removeItem(at: work) }
        let maxBytes = transcriber.maxUploadBytes
        let parts = try await Task.detached(priority: .utility) {
            try uploadParts(source: source, channel: channel, maxBytes: maxBytes, in: work)
        }.value
        var segments: [CloudSegment] = []
        for part in parts {
            try Task.checkCancellation()
            let result = try await transcriber.transcribe(file: part.url, language: language, diarize: diarize)
            segments += sanitized(result, duration: part.duration).map { $0.offset(by: part.start) }
        }
        return segments
    }

    /// The provider's segments made safe to store: times clamped to `0...duration` (non-finite ones to 0), `end`
    /// not before `start`, empty texts dropped. Pure, for tests.
    public static func sanitized(_ segments: [CloudSegment], duration: Double) -> [CloudSegment] {
        let duration = duration.isFinite ? max(0, duration) : 0
        func clamp(_ time: Double) -> Double { time.isFinite ? min(max(time, 0), duration) : 0 }
        return segments.compactMap { segment in
            var segment = segment
            segment.text = segment.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !segment.text.isEmpty else { return nil }
            segment.start = clamp(segment.start)
            segment.end = max(segment.start, clamp(segment.end))
            return segment
        }
    }

    /// Where to cut a file of `duration` seconds at `bytesPerSecond` so no part is over `maxBytes`: each cut is at
    /// the quietest `window`-second slot of `levels` (RMS per slot from the start) in the second half of the
    /// allowed length, else at the limit. Pure, for tests.
    public static func planParts(
        duration: Double, bytesPerSecond: Double, maxBytes: Int, levels: [Float], window: Double
    ) -> [ClosedRange<Double>] {
        let maxLength = Double(maxBytes) / bytesPerSecond
        guard maxLength.isFinite, maxLength > 0, duration > maxLength else { return [0...max(0, duration)] }
        var parts: [ClosedRange<Double>] = []
        var start = 0.0
        while duration - start > maxLength {
            let limit = start + maxLength
            let first = Int(((start + maxLength / 2) / window).rounded(.up))
            let last = min(levels.count, Int((limit / window).rounded(.down))) - 1
            var cut = limit
            if first <= last, let quietest = (first...last).min(by: { levels[$0] < levels[$1] }) {
                cut = (Double(quietest) + 0.5) * window
            }
            parts.append(start...cut)
            start = cut
        }
        parts.append(start...duration)
        return parts
    }

    /// The primary subtag of a BCP-47 code (`fr-FR` -> `fr`), which every provider here takes; nil for Auto.
    public static func languageCode(_ language: String?) -> String? {
        guard let trimmed = language?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty,
              trimmed.lowercased() != "auto" else { return nil }
        return trimmed.split(whereSeparator: { $0 == "-" || $0 == "_" }).first.map { $0.lowercased() }
    }

    static func mimeType(for url: URL) -> String {
        ["m4a": "audio/mp4", "mp4": "audio/mp4", "wav": "audio/wav", "mp3": "audio/mpeg"][url.pathExtension.lowercased()]
            ?? "application/octet-stream"
    }

    // MARK: - Words to segments

    /// A timed word; `text` carries the spacing that follows it when the provider sends spacing tokens.
    struct Word {
        var start: Double
        var end: Double
        var text: String
        var speaker: String?
    }

    /// A new segment starts when the speaker changes, after a pause over `maxPause`, or after a sentence end once
    /// the segment is `longSegment` seconds long (keeps a monologue from becoming one line).
    static func segments(
        from words: [Word], separator: String, language: String?, maxPause: Double = 1.0, longSegment: Double = 15
    ) -> [CloudSegment] {
        var segments: [CloudSegment] = []
        for word in words {
            if var current = segments.last, current.speaker == word.speaker, word.start - current.end <= maxPause,
               !(current.end - current.start >= longSegment && endsSentence(current.text)) {
                current.text += separator + word.text
                current.end = max(current.end, word.end)
                segments[segments.count - 1] = current
            } else {
                segments.append(CloudSegment(
                    start: word.start, end: word.end, text: word.text, speaker: word.speaker, language: language))
            }
        }
        return segments.compactMap { segment in
            var segment = segment
            segment.text = segment.text.trimmingCharacters(in: .whitespacesAndNewlines)
            return segment.text.isEmpty ? nil : segment
        }
    }

    private static func endsSentence(_ text: String) -> Bool {
        guard let last = text.trimmingCharacters(in: .whitespaces).last else { return false }
        return ".?!。？！".contains(last)
    }

    // MARK: - Audio

    /// The whole channel as one m4a, or parts when it is over `maxBytes`.
    private static func uploadParts(
        source: URL, channel: Int, maxBytes: Int?, in work: URL
    ) throws -> [(url: URL, start: Double, duration: Double)] {
        let whole = work.appendingPathComponent("channel.m4a")
        let duration = try exportM4A(source, channel: channel, range: nil, to: whole)
        let bytes = try FileManager.default.attributesOfItem(atPath: whole.path)[.size] as? Int ?? 0
        guard let maxBytes, bytes > maxBytes, duration > 0 else { return [(whole, 0, duration)] }
        try FileManager.default.removeItem(at: whole)
        let ranges = planParts(
            duration: duration, bytesPerSecond: Double(bytes) / duration, maxBytes: maxBytes,
            levels: try levels(of: source, channel: channel), window: levelWindow)
        return try ranges.enumerated().map { index, range in
            let url = work.appendingPathComponent("part-\(index).m4a")
            let length = try exportM4A(source, channel: channel, range: range, to: url)
            return (url, range.lowerBound, length)
        }
    }

    /// Writes `channel` of `source` from `range` (seconds; nil = all) as mono AAC m4a at the source's sample
    /// rate; returns the written length in seconds.
    @discardableResult
    static func exportM4A(_ source: URL, channel: Int, range: ClosedRange<Double>?, to destination: URL) throws -> Double {
        let input = try AVAudioFile(forReading: source, commonFormat: .pcmFormatFloat32, interleaved: false)
        let format = input.processingFormat
        guard channel < Int(format.channelCount),
              let mono = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: format.sampleRate, channels: 1, interleaved: false),
              let read = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: chunkFrames),
              let write = AVAudioPCMBuffer(pcmFormat: mono, frameCapacity: chunkFrames) else {
            throw LLMError.provider("Cannot read channel \(channel) of \(source.lastPathComponent).")
        }
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: format.sampleRate,
            AVNumberOfChannelsKey: 1, AVEncoderBitRateKey: bitRate,
        ]
        let output = try AVAudioFile(forWriting: destination, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        let first = min(input.length, AVAudioFramePosition(max(0, range?.lowerBound ?? 0) * format.sampleRate))
        let last = min(input.length, range.map { AVAudioFramePosition($0.upperBound * format.sampleRate) } ?? input.length)
        input.framePosition = first
        var remaining = max(0, last - first)
        while remaining > 0 {
            try input.read(into: read, frameCount: AVAudioFrameCount(min(AVAudioFramePosition(chunkFrames), remaining)))
            guard read.frameLength > 0 else { break }
            write.frameLength = read.frameLength
            write.floatChannelData![0].update(from: read.floatChannelData![channel], count: Int(read.frameLength))
            try output.write(from: write)
            remaining -= AVAudioFramePosition(read.frameLength)
        }
        return Double(last - first) / format.sampleRate
    }

    /// RMS of `channel` per `levelWindow` slot.
    private static func levels(of source: URL, channel: Int) throws -> [Float] {
        let input = try AVAudioFile(forReading: source, commonFormat: .pcmFormatFloat32, interleaved: false)
        let frames = AVAudioFrameCount(max(1, (input.processingFormat.sampleRate * levelWindow).rounded()))
        guard channel < Int(input.processingFormat.channelCount),
              let buffer = AVAudioPCMBuffer(pcmFormat: input.processingFormat, frameCapacity: frames) else { return [] }
        var levels: [Float] = []
        while input.framePosition < input.length {
            try input.read(into: buffer, frameCount: frames)
            guard buffer.frameLength > 0 else { break }
            var rms: Float = 0
            vDSP_rmsqv(buffer.floatChannelData![channel], 1, &rms, vDSP_Length(buffer.frameLength))
            levels.append(rms)
        }
        return levels
    }

    static func duration(of url: URL) -> Double {
        guard let file = try? AVAudioFile(forReading: url) else { return 0 }
        return Double(file.length) / file.processingFormat.sampleRate
    }
}

// MARK: - Engines

/// Cloud engines are picked by engine id `cloud:<provider id>` (Transcription tab default, Re-transcribe and Retry
/// menus); the model is the config's `transcriptionModel`, else the vendor default. The provider id is also the
/// Keychain id, so OpenAI, Groq, Mistral and Gemini share the key of their LLM config.
extension CloudTranscription {
    public static let enginePrefix = "cloud:"

    public static func engineID(providerID: String) -> String { enginePrefix + providerID }

    /// The provider id of a cloud engine id; nil for catalog ids and nil.
    public static func providerID(engineID: String?) -> String? {
        guard let engineID, engineID.hasPrefix(enginePrefix) else { return nil }
        let id = String(engineID.dropFirst(enginePrefix.count))
        return id.isEmpty ? nil : id
    }

    /// OpenAI, Groq, Mistral, Gemini (native audio API, not the LLM preset's OpenAI shim), the speech-to-text
    /// presets and custom OpenAI-compatible endpoints.
    public static func supports(_ config: ProviderConfig) -> Bool {
        switch config.kind {
        case .speechToText: true
        case .openAICompatible: config.preset == nil || ["openai", "groq", "mistral", "gemini"].contains(config.preset)
        case .anthropic, .cli, .local: false
        }
    }

    /// The model used when `transcriptionModel` is empty; "" when AssemblyAI picks it or for custom endpoints.
    public static func defaultModel(for config: ProviderConfig) -> String {
        switch config.preset {
        case "gemini": GeminiAudioTranscriber.defaultModel
        case "elevenlabs": ElevenLabsTranscriber.defaultModel
        case "deepgram": DeepgramTranscriber.defaultModel
        case let preset?: OpenAICompatibleTranscriber.presetModels[preset]?.first ?? ""
        case nil: ""
        }
    }

    public static func model(for config: ProviderConfig) -> String {
        let model = config.transcriptionModel.trimmingCharacters(in: .whitespacesAndNewlines)
        return model.isEmpty ? defaultModel(for: config) : model
    }

    /// Whether `config` (enabled or not) has what a request needs besides its key: a model (AssemblyAI excepted)
    /// and a valid destination (the base URL, unless the preset has a native API).
    public static func isComplete(_ config: ProviderConfig) -> Bool {
        guard supports(config) else { return false }
        if config.preset == "assemblyai" { return true }
        guard !model(for: config).isEmpty else { return false }
        return destination(for: config) != nil
    }

    /// Whether requests carry a key: always, except custom endpoints set to no authentication.
    public static func needsKey(_ config: ProviderConfig) -> Bool {
        config.kind == .speechToText || config.authStyle != .none
    }

    public static func makeTranscriber(config: ProviderConfig, apiKey: String?) -> any CloudTranscriber {
        let model = model(for: config)
        let key = apiKey ?? ""
        switch config.preset {
        case "gemini": return GeminiAudioTranscriber(apiKey: key, model: model)
        case "elevenlabs": return ElevenLabsTranscriber(apiKey: key, model: model)
        case "deepgram": return DeepgramTranscriber(apiKey: key, model: model)
        case "assemblyai": return AssemblyAITranscriber(apiKey: key)
        default: return OpenAICompatibleTranscriber(config: config, model: model, apiKey: apiKey)
        }
    }

    /// Where `config`'s audio and key go, for the one-time confirmation: `consentKey` is
    /// `<provider id>@<scheme://host[:port]>` (lowercased, default port dropped), so a changed base URL asks again;
    /// `host` is shown in the alert. The presets with a native API use its fixed origin. nil for an invalid base URL.
    public static func destination(for config: ProviderConfig) -> (consentKey: String, host: String)? {
        let url: URL
        switch config.preset {
        case "gemini": url = GeminiAudioTranscriber.baseURL
        case "elevenlabs": url = ElevenLabsTranscriber.endpoint
        case "deepgram": url = DeepgramTranscriber.endpoint
        case "assemblyai": url = AssemblyAITranscriber.baseURL
        default:
            guard let base = try? ProviderHTTP.baseURL(config) else { return nil }
            url = base
        }
        guard let scheme = url.scheme?.lowercased(), let host = url.host()?.lowercased(), !host.isEmpty else { return nil }
        let port = url.port.flatMap { $0 == ["http": 80, "https": 443][scheme] ? nil : $0 }
        let origin = "\(scheme)://\(host)" + (port.map { ":\($0)" } ?? "")
        return ("\(config.id)@\(origin)", host)
    }

    /// Settings' Test for the speech-to-text presets: one free GET that needs a valid key and uploads no audio.
    /// Throws like a transcription request (`LLMError.httpStatus` for a refused key).
    public static func checkKey(config: ProviderConfig, apiKey: String) async throws {
        let request: URLRequest
        switch config.preset {
        case "elevenlabs": request = ElevenLabsTranscriber.makeKeyCheckRequest(apiKey: apiKey)
        case "deepgram": request = DeepgramTranscriber.makeKeyCheckRequest(apiKey: apiKey)
        case "assemblyai": request = AssemblyAITranscriber.makeKeyCheckRequest(apiKey: apiKey)
        default: throw LLMError.provider("No key check for \(config.displayName).")
        }
        _ = try await ProviderHTTP.send(request, session: ProviderHTTP.session, retry: true)
    }

    /// A short reason for the card ("invalid API key"); the caller prefixes the provider name. Pure, for tests.
    public static func failureReason(_ error: Error) -> String {
        switch error {
        case LLMError.httpStatus(401, _), LLMError.httpStatus(403, _): return "invalid API key"
        case LLMError.httpStatus(413, _): return "audio too large"
        case LLMError.httpStatus(429, _): return "rate limit or quota reached"
        case LLMError.httpStatus(let code, let body) where (400..<500).contains(code):
            return vendorMessage(body).map { "HTTP \(code): \($0)" } ?? "HTTP \(code)"
        case LLMError.httpStatus(let code, _): return "HTTP \(code)"
        case LLMError.invalidBaseURL: return "invalid base URL"
        case let error as URLError:
            switch error.code {
            case .timedOut: return "timed out"
            case .notConnectedToInternet, .networkConnectionLost: return "no internet connection"
            case .cancelled: return "cancelled"
            default: return "network error"
            }
        default:
            return shortLine(error.localizedDescription)
        }
    }

    /// The message of a vendor error body (`error.message`, `error`, `message`, `detail`, `detail.message`,
    /// `detail[0].msg`, `err_msg`), shortened; nil when there is none. The body was redacted by `ProviderHTTP.send`.
    static func vendorMessage(_ body: String) -> String? {
        guard let json = try? JSONSerialization.jsonObject(with: Data(body.utf8)) as? [String: Any] else { return nil }
        func text(_ value: Any?) -> String? {
            switch value {
            case let string as String: string
            case let object as [String: Any]: text(object["message"]) ?? text(object["msg"])
            case let array as [Any]: text(array.first)
            default: nil
            }
        }
        let message = text(json["error"]) ?? text(json["message"]) ?? text(json["detail"]) ?? text(json["err_msg"])
        return message.map(shortLine).flatMap { $0.isEmpty ? nil : $0 }
    }

    /// The first line, at most 120 characters.
    private static func shortLine(_ text: String) -> String {
        let line = text.split(whereSeparator: \.isNewline).first.map(String.init)?
            .trimmingCharacters(in: .whitespaces) ?? ""
        return line.count > 120 ? String(line.prefix(120)) + "..." : line
    }
}

// MARK: - HTTP

extension ProviderHTTP {
    /// Sends `request` and returns a 2xx body; anything else throws `LLMError.httpStatus` with the request's keys
    /// redacted from the body. With `retry`, one retry after a 5xx or a timeout when `mayRetry(request)` (never a
    /// billed request); never after a 4xx or a cancellation.
    static func send(_ request: URLRequest, session: URLSession, retry: Bool) async throws -> (Data, HTTPURLResponse) {
        do {
            return try await sendOnce(request, session: session)
        } catch let error where retry && mayRetry(request) && isRetryable(error) {
            try await Task.sleep(for: .seconds(2))
            return try await sendOnce(request, session: session)
        }
    }

    /// Whether sending `request` twice costs nothing: GETs (polls, key checks) and audio uploads (AssemblyAI
    /// `/v2/upload`, Gemini Files API). Billed one-shot POSTs (transcription requests, `generateContent`, AssemblyAI
    /// `/v2/transcript`) are sent once, since a retry after a lost answer could start a second paid job. Pure, for
    /// tests.
    static func mayRetry(_ request: URLRequest) -> Bool {
        switch request.httpMethod?.uppercased() ?? "GET" {
        case "GET", "HEAD": return true
        case "POST":
            let path = request.url?.path() ?? ""
            return path == "/v2/upload" || path == "/upload/v1beta/files"
        default: return false
        }
    }

    /// `body` with every key the request carries (auth header values, without their `Bearer` / `Token` scheme)
    /// replaced, so an echoed key never reaches an error, the UI or a log. Pure, for tests.
    static func redacted(_ body: String, request: URLRequest) -> String {
        let headers = ["Authorization", "x-goog-api-key", "xi-api-key", "api-key", "x-api-key"]
        let secrets = headers.compactMap { request.value(forHTTPHeaderField: $0) }.flatMap { value in
            [value, value.split(separator: " ", maxSplits: 1).last.map(String.init) ?? value]
        }
        return secrets.filter { $0.count >= 4 }.sorted { $0.count > $1.count }
            .reduce(body) { $0.replacingOccurrences(of: $1, with: "[redacted]") }
    }

    static func isRetryable(_ error: Error) -> Bool {
        switch error {
        case LLMError.httpStatus(let code, _): (500..<600).contains(code)
        case let error as URLError: error.code == .timedOut
        default: false
        }
    }

    private static func sendOnce(_ request: URLRequest, session: URLSession) async throws -> (Data, HTTPURLResponse) {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        guard (200..<300).contains(http.statusCode) else {
            throw LLMError.httpStatus(
                http.statusCode, redacted(String(decoding: data.prefix(2048), as: UTF8.self), request: request))
        }
        return (data, http)
    }

    static func setMultipartBody(_ request: inout URLRequest, _ form: MultipartForm) {
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(form.boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = form.body + Data("--\(form.boundary)--\r\n".utf8)
    }
}

/// A multipart/form-data body, built field by field.
struct MultipartForm {
    let boundary = "EchoDiar-\(UUID().uuidString)"
    private(set) var body = Data()

    mutating func add(_ name: String, _ value: String) {
        body += Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8)
    }

    mutating func addFile(_ name: String, fileName: String, mimeType: String, data: Data) {
        body += Data(
            "--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"; filename=\"\(fileName)\"\r\nContent-Type: \(mimeType)\r\n\r\n".utf8)
        body += data
        body += Data("\r\n".utf8)
    }
}
