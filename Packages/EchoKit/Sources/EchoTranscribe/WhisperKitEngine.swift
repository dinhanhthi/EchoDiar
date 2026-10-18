import EchoCore
import Foundation
import WhisperKit

/// WhisperKit CoreML models from `Models/<id>/` (flattened, with `tokenizer.json`). Never downloads: the
/// tokenizer is checked before WhisperKit is created, since its fallback would fetch it from the Hub.
public struct WhisperKitEngine: TranscriptionEngine {
    public static let defaultWindowSeconds = 30
    /// Audio read into memory at once: 10 min of 16 kHz Float32 is about 38 MB.
    static let slabSeconds = 600.0

    public let entry: ModelEntry
    public let folder: URL

    public init(entry: ModelEntry, folder: URL) {
        self.entry = entry
        self.folder = folder
    }

    public func transcribe(
        channelURL: URL, languageRanges: [LanguageRange], progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment] {
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
        let whisper = try await WhisperKit(config)
        do {
            let segments = try await decode(whisper, channelURL: channelURL, ranges: languageRanges, progress: progress)
            await whisper.unloadModels()
            return segments
        } catch {
            await whisper.unloadModels()
            throw error
        }
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
            let options = DecodingOptions(
                language: language,
                detectLanguage: language == nil,
                skipSpecialTokens: true,
                wordTimestamps: false
            )
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
                    for result in results {
                        let detected = language == nil ? result.language : nil
                        for segment in result.segments {
                            let text = Self.speechText(segment.text)
                            guard !text.isEmpty else { continue }
                            segments.append(RawSegment(
                                start: offset + Double(segment.start), end: offset + Double(segment.end),
                                text: text, detectedLanguage: detected))
                        }
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

    /// Whisper takes the primary subtag: "en-US" → "en".
    static func whisperLanguage(_ code: String) -> String {
        String(code.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).first ?? "")
    }
}
