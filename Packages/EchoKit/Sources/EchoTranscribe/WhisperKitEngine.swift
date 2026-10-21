import Accelerate
import EchoCore
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
    /// `isSilent`. WhisperKit 1.1.1 never computes `noSpeechProb`, so near silence still decodes to "Thank you.".
    static let speechRMS: Float = 0.003

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
    private func decode(
        _ whisper: WhisperKit, channelURL: URL, ranges: [LanguageRange], progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment] {
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
                    let results = try await whisper.transcribe(audioArray: chunk.audioSamples, decodeOptions: options)
                    segments += Self.speechSegments(
                        results, in: chunk.audioSamples, offset: offset, detectLanguage: language == nil)
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
    /// empty or whose audio `isSilent` are dropped. `detectLanguage` keeps each result's detected language.
    static func speechSegments(
        _ results: [TranscriptionResult], in samples: [Float], offset: Double, detectLanguage: Bool
    ) -> [RawSegment] {
        var segments: [RawSegment] = []
        for result in results {
            let detected = detectLanguage ? result.language : nil
            for segment in result.segments {
                let text = speechText(segment.text)
                guard !text.isEmpty, !isSilent(samples, from: segment.start, to: segment.end) else { continue }
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
        return text
    }

    /// True when no 100 ms frame of `samples` from `start` to `end` (seconds) reaches `speechRMS`: Whisper made the
    /// segment up from silence. The range is clamped to the buffer, as Whisper's timestamps can run past the audio.
    static func isSilent(
        _ samples: [Float], from start: Float, to end: Float, sampleRate: Int = WhisperKit.sampleRate
    ) -> Bool {
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

    /// Whisper takes the primary subtag: "en-US" → "en".
    static func whisperLanguage(_ code: String) -> String {
        String(code.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).first ?? "")
    }
}

extension WhisperKitEngine {
    /// Loads the model for a `Session`, which the caller unloads.
    func session() async throws -> Session {
        Session(whisper: try await Self.load(entry: entry, folder: folder),
                windowSamples: (entry.windowSeconds ?? Self.defaultWindowSeconds) * WhisperKit.sampleRate)
    }

    /// One loaded model for many chunks, decoded like the batch windows: a chunk longer than `windowSamples` is
    /// split at silences, and each window gets the batch's options and `speechSegments` filtering.
    final class Session: TranscriptionSession {
        private let whisper: WhisperKit
        private let windowSamples: Int

        init(whisper: WhisperKit, windowSamples: Int) {
            self.whisper = whisper
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
                let results = try await whisper.transcribe(audioArray: chunk.audioSamples, decodeOptions: options)
                segments += WhisperKitEngine.speechSegments(
                    results, in: chunk.audioSamples, offset: Double(chunk.seekOffsetIndex) / sampleRate,
                    detectLanguage: code == nil)
            }
            return segments
        }

        func unload() async {
            await whisper.unloadModels()
        }
    }
}
