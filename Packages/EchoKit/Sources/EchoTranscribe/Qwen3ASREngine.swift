import EchoCore
import Foundation
import MLXCommon
import Qwen3ASR
import WhisperKit

/// Qwen3-ASR 0.6B / 1.7B MLX 4-bit (speech-swift) from `Models/<id>/`, loaded with `offlineMode: true` so it never
/// downloads or touches speech-swift's own cache. Runs on the GPU, batch only.
public struct Qwen3ASREngine: TranscriptionEngine {
    /// Audio read into memory at once: 10 min of 16 kHz Float32 is about 38 MB.
    static let slabSeconds = 600.0
    /// Longest window per decode, cut at a silence. Qwen3-ASR gives no timestamps, so each window is one segment;
    /// speech-swift switches to a slower per-token decode path above 15 s.
    static let windowSeconds = 15
    /// Shorter windows are skipped.
    static let minimumSamples = 1_600

    public let entry: ModelEntry
    public let folder: URL

    public init(entry: ModelEntry, folder: URL) {
        self.entry = entry
        self.folder = folder
    }

    /// Load, decode and unload run in one detached task at `.utility`: decoding is synchronous MLX work.
    public func transcribe(
        channelURL: URL, languageRanges: [LanguageRange], progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment] {
        guard let repo = entry.repo else { throw TranscriptionError.unknownEngine(entry.id) }
        let entry = entry
        let folder = folder
        let task = Task.detached(priority: .utility) {
            // The repo id tells speech-swift the model size and bits.
            let model = try await Qwen3ASRModel.fromPretrained(modelId: repo, cacheDir: folder, offlineMode: true)
            // `unload` frees the weights and clears the MLX cache; loading pinned 90% of the GPU working set as wired
            // memory for the whole process, which `unload` leaves in place.
            defer {
                model.unload()
                MetalBudget.unpinMemory()
            }
            return try await Self.decode(
                model, entry: entry, channelURL: channelURL, ranges: languageRanges, progress: progress)
        }
        return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }

    /// One decode per language range, each read in slabs of at most `slabSeconds` and split at silences into
    /// windows of at most `windowSeconds`. Before the last slab of a range, its last window may end mid-word at the
    /// slab cut: it is decoded with the next slab, which starts where it starts.
    private static func decode(
        _ model: Qwen3ASRModel, entry: ModelEntry, channelURL: URL, ranges: [LanguageRange],
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment] {
        let sampleRate = MonoAudio.sampleRate
        let duration = try MonoAudio.duration(of: channelURL)
        let total = max(1, duration)
        var segments: [RawSegment] = []
        for range in ranges {
            let options = Qwen3DecodingOptions(language: range.language.flatMap { languageName($0, entry: entry) })
            let rangeEnd = min(range.end ?? duration, duration)
            var slabStart = range.start
            while slabStart < rangeEnd {
                try Task.checkCancellation()
                let slabEnd = min(slabStart + slabSeconds, rangeEnd)
                let samples = try MonoAudio.read(channelURL, from: slabStart, to: slabEnd)
                guard !samples.isEmpty else { break }
                var chunks = try await VADAudioChunker().chunkAll(
                    audioArray: samples, maxChunkLength: windowSeconds * Int(sampleRate), decodeOptions: nil)
                var nextStart = slabEnd
                if slabEnd < rangeEnd, chunks.count > 1, let last = chunks.popLast() {
                    nextStart = slabStart + Double(last.seekOffsetIndex) / sampleRate
                }
                for chunk in chunks where chunk.audioSamples.count >= minimumSamples {
                    try Task.checkCancellation()
                    let start = slabStart + Double(chunk.seekOffsetIndex) / sampleRate
                    let end = start + Double(chunk.audioSamples.count) / sampleRate
                    let text = try model.transcribeCheckingCancellation(
                        audio: chunk.audioSamples, sampleRate: Int(sampleRate), options: options
                    ).trimmingCharacters(in: .whitespacesAndNewlines)
                    if !text.isEmpty { segments.append(RawSegment(start: start, end: end, text: text)) }
                    progress(min(1, end / total))
                }
                slabStart = nextStart
            }
        }
        progress(1)
        return segments
    }

    /// Qwen3-ASR takes English language names ("Vietnamese"); nil (auto-detect) for codes the entry does not list.
    static func languageName(_ code: String, entry: ModelEntry) -> String? {
        let primary = WhisperKitEngine.whisperLanguage(code)
        guard case .codes(let codes) = entry.languages, codes.contains(primary) else { return nil }
        return Locale(identifier: "en").localizedString(forLanguageCode: primary)
    }
}
