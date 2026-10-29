import Accelerate
import CoreML
import EchoCore
import FluidAudio
import Foundation
import WhisperKit

/// WhisperKit CoreML models from `Models/<id>/` (flattened, with `tokenizer.json`). Never downloads: the
/// tokenizer is checked before WhisperKit is created, since its fallback would fetch it from the Hub.
public struct WhisperKitEngine: TranscriptionEngine {
    public static let defaultWindowSeconds = 30
    /// Whisper's language codes.
    public static let supportedLanguages = Array(Constants.languageCodes)
    /// Audio read into memory at once: 10 min of 16 kHz Float32 is about 38 MB.
    static let slabSeconds = 600.0
    /// RMS (about −50 dBFS) that some 100 ms frame of a segment's audio must reach for the segment to be kept, see
    /// `isSilent`. WhisperKit 1.1.1 never computes `noSpeechProb`, so near silence still decodes to "Thank you.";
    /// louder noise (keys, breath) passes this and is left to the Silero gate, see `hasSpeech`.
    static let speechRMS: Float = 0.003
    /// Silero's probability that a 256 ms frame is speech, from which it counts as voiced. Above silero-vad's 0.5
    /// default because each window starts from a fresh state: on a few seconds of audio, breath and keys reached
    /// 0.88 (measured on 4 s windows of a noisy mic) while speech, even one "Merci.", stays at 0.99–1.0.
    static let speechProbability: Float = 0.9
    /// Subtitle credits and video outros Whisper learned from subtitled videos and prints over silence or noise, even
    /// next to speech. A segment that starts with one is always dropped: the credits Whisper prints start their
    /// segment, while speech may mention subtitles mid-sentence ("on doit prévoir le sous-titrage de la vidéo").
    /// Each entry is the parts that must all appear, matched without case or diacritics: the first starts the
    /// segment, after leading whitespace and punctuation (quotes, dashes), the others are anywhere.
    static let subtitleCredits: [[String]] = [
        // French credits ("Sous-titrage Société Radio-Canada", "Sous-titrage ST' 501") and outro.
        ["sous-titres réalisés par"], ["sous-titrage"], ["merci d'avoir regardé"],
        // English outros and credits.
        ["thanks for watching"], ["thank you for watching"], ["subtitles by"], ["subtitled by"],
        // Vietnamese YouTube subscribe calls only: a real talk can end with "cảm ơn các bạn đã theo dõi".
        ["hãy subscribe cho kênh"], ["hãy đăng ký kênh"],
        // Spanish credit and outro.
        ["subtítulos realizados por"], ["gracias por ver el video"],
        // Chinese community credit ("字幕由Amara.org社区提供", not "字幕由谁提供？") and a channel's like-and-subscribe
        // outro ("请不吝点赞 订阅 转发 打赏支持明镜与点点栏目").
        ["字幕由", "社区提供"], ["请不吝点赞 订阅 转发 打赏"], ["明镜与点点栏目"],
    ]
    /// Subtitle credit parts nobody says in a meeting, even mid-sentence: a segment containing one anywhere is
    /// dropped, matched without case or diacritics.
    static let subtitleCreditsAnywhere: [String] = [
        // The community subtitles credit, in any language ("Subtítulos por la comunidad de Amara.org").
        "amara.org",
    ]

    public let entry: ModelEntry
    public let folder: URL

    public init(entry: ModelEntry, folder: URL) {
        self.entry = entry
        self.folder = folder
    }

    public func transcribe(
        channelURL: URL, languageRanges: [LanguageRange], progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment] {
        let whisper = try await Self.load(entry: entry, folder: folder)
        do {
            let segments = try await decode(whisper, channelURL: channelURL, ranges: languageRanges, progress: progress)
            await whisper.unloadModels()
            return segments
        } catch {
            await whisper.unloadModels()
            throw error
        }
    }

    /// Loads the model from `folder`; the caller unloads it. Also used by `Session`.
    static func load(entry: ModelEntry, folder: URL) async throws -> WhisperKit {
        guard FileManager.default.fileExists(atPath: folder.appendingPathComponent("tokenizer.json").path) else {
            throw TranscriptionError.needsModel(entry.id)
        }
        let config = WhisperKitConfig(
            modelFolder: folder.path,
            tokenizerFolder: folder,
            computeOptions: ModelComputeOptions(
                audioEncoderCompute: .cpuAndNeuralEngine, textDecoderCompute: .cpuAndNeuralEngine),
            verbose: false,
            logLevel: .error,
            prewarm: false,
            load: true,
            download: false
        )
        return try await WhisperKit(config)
    }

    /// One decode per language range; each range is read in slabs of at most `slabSeconds`, each slab is split at
    /// silences into windows of `windowSeconds`, and every window detects its own language when the range has none.
    /// A window where Silero hears no speech is not decoded.
    private func decode(
        _ whisper: WhisperKit, channelURL: URL, ranges: [LanguageRange], progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment] {
        let detector = await Self.loadVoiceDetector()
        let sampleRate = Double(WhisperKit.sampleRate)
        let duration = try MonoAudio.duration(of: channelURL)
        let total = max(1, duration)
        let windowSamples = (entry.windowSeconds ?? Self.defaultWindowSeconds) * WhisperKit.sampleRate
        var segments: [RawSegment] = []
        for range in ranges {
            let language = range.language.map(Self.whisperLanguage)
            let options = Self.decodingOptions(language: language)
            let rangeEnd = min(range.end ?? duration, duration)
            var slabStart = range.start
            while slabStart < rangeEnd {
                try Task.checkCancellation()
                let slabEnd = min(slabStart + Self.slabSeconds, rangeEnd)
                let samples = try MonoAudio.read(channelURL, from: slabStart, to: slabEnd)
                guard !samples.isEmpty else { break }
                var chunks = try await VADAudioChunker().chunkAll(
                    audioArray: samples, maxChunkLength: windowSamples, decodeOptions: options)
                // Before the last slab, the last window ends at the slab cut, maybe mid-word: it is decoded with
                // the next slab, which starts where it starts (a VAD silence).
                var nextStart = slabEnd
                if slabEnd < rangeEnd, chunks.count > 1, let last = chunks.popLast() {
                    nextStart = slabStart + Double(last.seekOffsetIndex) / sampleRate
                }
                for chunk in chunks {
                    try Task.checkCancellation()
                    let offset = slabStart + Double(chunk.seekOffsetIndex) / sampleRate
                    let voicing = await Self.voicing(detector, chunk.audioSamples)
                    if Self.hasSpeech(voicing) {
                        let results = try await whisper.transcribe(
                            audioArray: chunk.audioSamples, decodeOptions: options)
                        segments += Self.speechSegments(
                            results, in: chunk.audioSamples, offset: offset, detectLanguage: language == nil,
                            voicing: voicing)
                    }
                    let done = offset + Double(chunk.audioSamples.count) / sampleRate
                    progress(min(1, done / total))
                }
                slabStart = nextStart
            }
        }
        progress(1)
        return segments
    }

    /// Whisper's options for a window: `language` is a Whisper code, nil detects it per window.
    static func decodingOptions(language: String?) -> DecodingOptions {
        DecodingOptions(
            language: language,
            detectLanguage: language == nil,
            skipSpecialTokens: true,
            wordTimestamps: false
        )
    }

    /// The speech segments of one window's results, `offset` seconds into the audio: segments whose `speechText` is
    /// empty or a subtitle credit, whose audio `isSilent`, or where the window's Silero `voicing` (nil without a
    /// detector) has no speech, are dropped. `detectLanguage` keeps each result's detected language.
    static func speechSegments(
        _ results: [TranscriptionResult], in samples: [Float], offset: Double, detectLanguage: Bool,
        voicing: [Float]?
    ) -> [RawSegment] {
        var segments: [RawSegment] = []
        for result in results {
            let detected = detectLanguage ? result.language : nil
            for segment in result.segments {
                let text = speechText(segment.text)
                guard !text.isEmpty, !isSubtitleCredit(text), !isSilent(samples, from: segment.start, to: segment.end),
                      voicing.map({ hasSpeech($0, from: segment.start, to: segment.end) }) ?? true
                else { continue }
                segments.append(RawSegment(
                    start: offset + Double(segment.start), end: offset + Double(segment.end),
                    text: text, detectedLanguage: detected))
            }
        }
        return segments
    }

    /// Segment text without Whisper's non-speech output; empty means the segment is dropped.
    /// - Special tokens such as `<|endoftext|>` are removed wherever they are.
    /// - Annotations (`[...]`, `(...)`, `*...*`) are removed when they make up the whole text, e.g. `[BLANK_AUDIO]`,
    ///   `[ Silence ]`, `(music)`, `*music*`.
    /// - At the start or end of speech, only annotations naming a known non-speech sound are removed, so speech
    ///   that merely contains brackets is kept.
    /// - Text left with no letter or digit, e.g. `...` on a silent mic, is removed.
    static func speechText(_ raw: String) -> String {
        let annotation = #/\[[^\[\]]*\]|\([^()]*\)|\*[^*]+\*/#
        let trailingAnnotation = #/(?:\[[^\[\]]*\]|\([^()]*\)|\*[^*]+\*)\s*$/#
        let nonSpeech = #/(?i)blank[ _]?audio|silence|music|noise|applause|laugh|inaudible|no[ _]speech|static/#
        var text = raw.replacing(#/<\|[^|]*\|>/#, with: "").trimmingCharacters(in: .whitespacesAndNewlines)
        if text.replacing(annotation, with: "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return "" }
        while let match = text.prefixMatch(of: annotation), match.output.contains(nonSpeech) {
            text = text[match.range.upperBound...].trimmingCharacters(in: .whitespacesAndNewlines)
        }
        while let match = text.firstMatch(of: trailingAnnotation), match.output.contains(nonSpeech) {
            text = text[..<match.range.lowerBound].trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return text.contains { $0.isLetter || $0.isNumber } ? text : ""
    }

    /// True when no 100 ms frame of `samples` from `start` to `end` (seconds) reaches `speechRMS`: Whisper made the
    /// segment up from silence. The range is clamped to the buffer, as Whisper's timestamps can run past the audio;
    /// a NaN or infinite timestamp has no audio, so the segment is silent.
    static func isSilent(
        _ samples: [Float], from start: Float, to end: Float, sampleRate: Int = WhisperKit.sampleRate
    ) -> Bool {
        guard start.isFinite, end.isFinite else { return true }
        let rate = Float(sampleRate)
        let lower = min(samples.count, max(0, Int(start * rate)))
        let upper = min(samples.count, max(lower, Int((end * rate).rounded(.up))))
        let frame = sampleRate / 10
        return samples.withUnsafeBufferPointer { buffer in
            stride(from: lower, to: upper, by: frame).allSatisfy { first in
                var rms: Float = 0
                vDSP_rmsqv(buffer.baseAddress! + first, 1, &rms, vDSP_Length(min(frame, upper - first)))
                return rms < speechRMS
            }
        }
    }

    /// True when `text` starts with one of the `subtitleCredits`, after leading whitespace and punctuation, or
    /// contains one of the `subtitleCreditsAnywhere`.
    static func isSubtitleCredit(_ text: String) -> Bool {
        let options: String.CompareOptions = [.caseInsensitive, .diacriticInsensitive]
        let start = text.drop { $0.isWhitespace || $0.isPunctuation }
        return subtitleCreditsAnywhere.contains { text.range(of: $0, options: options) != nil }
            || subtitleCredits.contains { parts in
                start.range(of: parts[0], options: options.union(.anchored)) != nil
                    && parts.dropFirst().allSatisfy { start.range(of: $0, options: options) != nil }
            }
    }

    /// FluidAudio's Silero VAD from `Bundle.module`, on the CPU: the Neural Engine is left to Whisper. Nil (logged)
    /// when it cannot load: windows are then decoded without the gate.
    static func loadVoiceDetector() async -> VadManager? {
        let configuration = MLModelConfiguration()
        configuration.computeUnits = .cpuOnly
        do {
            guard let url = Bundle.module.url(
                forResource: "silero-vad-unified-256ms-v6.2.1", withExtension: "mlmodelc")
            else { throw CocoaError(.fileNoSuchFile) }
            return VadManager(config: .default, vadModel: try MLModel(contentsOf: url, configuration: configuration))
        } catch {
            NSLog("EchoDiar: Silero VAD failed to load, transcribing without it: %@", String(describing: error))
            return nil
        }
    }

    /// Silero's speech probability for each 256 ms frame of one window's `samples`; nil without a detector or when it
    /// fails. Silero restarts from its initial state on every call: run it once per window, never per segment.
    static func voicing(_ detector: VadManager?, _ samples: [Float]) async -> [Float]? {
        try? await detector?.process(samples).map(\.probability)
    }

    /// False when a window's `voicing` has no frame of speech: Whisper would only make text up there, so the window
    /// is not decoded. True without a detector (nil).
    static func hasSpeech(_ voicing: [Float]?) -> Bool {
        voicing?.contains { $0 >= speechProbability } ?? true
    }

    /// True when a 256 ms frame of `voicing` from `start` to `end` (seconds), or the frame on either side of them,
    /// reaches `speechProbability`. The range is clamped to the frames as in `isSilent`, so a segment stamped past
    /// the audio has none and is dropped, as is one with a NaN or infinite timestamp.
    static func hasSpeech(_ voicing: [Float], from start: Float, to end: Float) -> Bool {
        guard start.isFinite, end.isFinite else { return false }
        let frameSeconds = Float(VadManager.chunkSize) / Float(VadManager.sampleRate)
        let lower = min(voicing.count, max(0, Int(start / frameSeconds)))
        let upper = min(voicing.count, max(lower, Int((end / frameSeconds).rounded(.up))))
        guard lower < upper else { return false }
        return voicing[max(0, lower - 1)..<min(voicing.count, upper + 1)].contains { $0 >= speechProbability }
    }

    /// Whisper takes the primary subtag: "en-US" → "en".
    static func whisperLanguage(_ code: String) -> String {
        String(code.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).first ?? "")
    }
}

extension WhisperKitEngine {
    /// Loads the model and the Silero detector for a `Session`, which the caller unloads.
    func session() async throws -> Session {
        Session(whisper: try await Self.load(entry: entry, folder: folder),
                detector: await Self.loadVoiceDetector(),
                windowSamples: (entry.windowSeconds ?? Self.defaultWindowSeconds) * WhisperKit.sampleRate)
    }

    /// One loaded model for many chunks, decoded like the batch windows: a chunk longer than `windowSamples` is
    /// split at silences, and each window gets the batch's Silero gate, options and `speechSegments` filtering.
    final class Session: TranscriptionSession {
        private let whisper: WhisperKit
        private let detector: VadManager?
        private let windowSamples: Int

        init(whisper: WhisperKit, detector: VadManager?, windowSamples: Int) {
            self.whisper = whisper
            self.detector = detector
            self.windowSamples = windowSamples
        }

        /// `language` is a BCP-47 code; nil, empty or `auto` detects it per window, as in `LanguageRange`.
        func transcribe(_ samples: [Float], language: String?) async throws -> [RawSegment] {
            let trimmed = language?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            let code = trimmed.isEmpty || trimmed.lowercased() == "auto"
                ? nil : WhisperKitEngine.whisperLanguage(trimmed)
            let options = WhisperKitEngine.decodingOptions(language: code)
            let sampleRate = Double(WhisperKit.sampleRate)
            var segments: [RawSegment] = []
            for chunk in try await VADAudioChunker().chunkAll(
                audioArray: samples, maxChunkLength: windowSamples, decodeOptions: options) {
                try Task.checkCancellation()
                let voicing = await WhisperKitEngine.voicing(detector, chunk.audioSamples)
                guard WhisperKitEngine.hasSpeech(voicing) else { continue }
                let results = try await whisper.transcribe(audioArray: chunk.audioSamples, decodeOptions: options)
                segments += WhisperKitEngine.speechSegments(
                    results, in: chunk.audioSamples, offset: Double(chunk.seekOffsetIndex) / sampleRate,
                    detectLanguage: code == nil, voicing: voicing)
            }
            return segments
        }

        func unload() async {
            await whisper.unloadModels()
        }
    }
}
