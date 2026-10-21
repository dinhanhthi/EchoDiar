import CoreML
import EchoCore
import FluidAudio
import Foundation

/// Parakeet TDT 0.6B v3 / Ultra CoreML models from `Models/<id>/<Repo.folderName>/`, loaded with
/// `AsrModels.loadLocal` (no ModelHub, so nothing is ever downloaded) on the Neural Engine.
public struct ParakeetEngine: TranscriptionEngine {
    /// Audio read into memory at once: 10 min of 16 kHz Float32 is about 38 MB.
    static let slabSeconds = 600.0
    /// A pause this long between words starts a new segment.
    static let segmentGapSeconds = 1.0

    public let entry: ModelEntry
    public let folder: URL

    public init(entry: ModelEntry, folder: URL) {
        self.entry = entry
        self.folder = folder
    }

    public func transcribe(
        channelURL: URL, languageRanges: [LanguageRange], progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment] {
        let models = try await load()
        let manager = AsrManager(config: .default, models: models)
        do {
            let segments = try await decode(manager, channelURL: channelURL, ranges: languageRanges, progress: progress)
            await manager.cleanup()
            return segments
        } catch {
            await manager.cleanup()
            throw error
        }
    }

    /// `loadLocal` is synchronous and compiles CoreML: it runs off the caller's thread at `.utility`.
    private func load() async throws -> AsrModels {
        let version: AsrModelVersion
        switch entry.repo.flatMap(Repo.init(rawValue:)) {
        case .parakeetV3: version = .v3
        case .parakeetUltra: version = .ultra
        default: throw TranscriptionError.unknownEngine(entry.id)
        }
        let folder = folder
        let entryID = entry.id
        return try await Task.detached(priority: .utility) {
            let configuration = AsrModels.defaultConfiguration()
            configuration.computeUnits = .cpuAndNeuralEngine
            do {
                return try AsrModels.loadLocal(from: folder, version: version, configuration: configuration)
            } catch AsrModelsError.modelNotFound {
                throw TranscriptionError.needsModel(entryID)
            }
        }.value
    }

    /// One decode per language range, each read in slabs of at most `slabSeconds`. Before the last slab of a range,
    /// its last word may be cut by the slab end: it is decoded again with the next slab, which starts at it.
    private func decode(
        _ manager: AsrManager, channelURL: URL, ranges: [LanguageRange],
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment] {
        let duration = try MonoAudio.duration(of: channelURL)
        let total = max(1, duration)
        let minimumSamples = ASRConstants.minimumRequiredSamples(forSampleRate: Int(MonoAudio.sampleRate))
        let decoderLayers = await manager.decoderLayerCount
        var segments: [RawSegment] = []
        for range in ranges {
            let language = range.language.flatMap { Language(rawValue: WhisperKitEngine.whisperLanguage($0)) }
            let rangeEnd = min(range.end ?? duration, duration)
            var slabStart = range.start
            while slabStart < rangeEnd {
                try Task.checkCancellation()
                let slabEnd = min(slabStart + Self.slabSeconds, rangeEnd)
                let samples = try MonoAudio.read(channelURL, from: slabStart, to: slabEnd)
                // Shorter than FluidAudio accepts (300 ms): nothing to decode.
                guard samples.count >= minimumSamples else { break }
                var state = TdtDecoderState.make(decoderLayers: decoderLayers)
                let result = try await manager.transcribe(samples, decoderState: &state, language: language)
                var words = buildWordTimings(from: result.tokenTimings ?? [])
                var nextStart = slabEnd
                if slabEnd < rangeEnd, words.count > 1, let last = words.popLast() {
                    nextStart = slabStart + last.startTime
                }
                if words.isEmpty {
                    let text = result.text.trimmingCharacters(in: .whitespacesAndNewlines)
                    if !text.isEmpty { segments.append(RawSegment(start: slabStart, end: slabEnd, text: text)) }
                } else {
                    segments += Self.segments(words, offset: slabStart)
                }
                progress(min(1, nextStart / total))
                slabStart = nextStart
            }
        }
        progress(1)
        return segments
    }

    /// Words grouped into segments, split after sentence-final punctuation or at a pause of `segmentGapSeconds`.
    static func segments(_ words: [WordTiming], offset: Double) -> [RawSegment] {
        var segments: [RawSegment] = []
        var current: [WordTiming] = []
        func flush() {
            guard let first = current.first, let last = current.last else { return }
            segments.append(RawSegment(
                start: offset + first.startTime, end: offset + last.endTime,
                text: current.map(\.word).joined(separator: " ")))
            current = []
        }
        for word in words {
            if let previous = current.last, word.startTime - previous.endTime >= segmentGapSeconds { flush() }
            current.append(word)
            if let mark = word.word.last, ".?!".contains(mark) { flush() }
        }
        flush()
        return segments
    }
}

extension ParakeetEngine {
    /// Loads the model for a `Session`, which the caller unloads.
    func session() async throws -> Session {
        let manager = AsrManager(config: .default, models: try await load())
        return Session(manager: manager, decoderLayers: await manager.decoderLayerCount)
    }

    /// One `AsrManager` for many chunks, each decoded from a fresh decoder state like a batch slab.
    final class Session: TranscriptionSession {
        private let manager: AsrManager
        private let decoderLayers: Int

        init(manager: AsrManager, decoderLayers: Int) {
            self.manager = manager
            self.decoderLayers = decoderLayers
        }

        /// `language` is a BCP-47 code; nil, or one Parakeet does not know, detects it. A chunk shorter than
        /// FluidAudio accepts (300 ms) is padded with silence.
        func transcribe(_ samples: [Float], language: String?) async throws -> [RawSegment] {
            let minimumSamples = ASRConstants.minimumRequiredSamples(forSampleRate: Int(MonoAudio.sampleRate))
            let padded = samples + [Float](repeating: 0, count: max(0, minimumSamples - samples.count))
            var state = TdtDecoderState.make(decoderLayers: decoderLayers)
            let result = try await manager.transcribe(
                padded, decoderState: &state,
                language: language.flatMap { Language(rawValue: WhisperKitEngine.whisperLanguage($0)) })
            let words = buildWordTimings(from: result.tokenTimings ?? [])
            guard words.isEmpty else { return ParakeetEngine.segments(words, offset: 0) }
            let text = result.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { return [] }
            return [RawSegment(start: 0, end: Double(samples.count) / MonoAudio.sampleRate, text: text)]
        }

        func unload() async {
            await manager.cleanup()
        }
    }
}
