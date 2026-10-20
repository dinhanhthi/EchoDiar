import EchoCore
import FluidAudio
import Foundation

/// FluidAudio's offline pyannote diarizer or, for `nemotronPresets` entries, Nemotron 3, run on the system channel
/// only. Models come from `Models/<id>/` through `OfflineDiarizerModels.load(from:)` (never `prepareModels`, which
/// purges the folder after a failed load) or `Nemotron3Models.load(config:directory:)`; `ModelHub.offlineMode`
/// (set by `ModelStore`) blocks any download.
public struct SpeakerDiarizer: Sendable {
    /// Catalog entry id → FluidAudio `Nemotron3Config` preset name.
    public static let nemotronPresets: [String: String] = [
        "nemotron-3-diarization-c128": "c128-split-w8a8", "nemotron-3-diarization-fast128": "fast128",
    ]
    /// Nemotron 3 has fixed speaker slots and no speaker-count setting.
    public static let nemotronMaxSpeakers = 8

    public let entryID: String
    /// `Models/<id>/`; FluidAudio appends `speaker-diarization/` itself for pyannote, Nemotron reads it as is.
    public let folder: URL
    /// Exact speaker count, nil = automatic. pyannote only; Nemotron ignores it.
    public let speakerCountHint: Int?

    public init(entryID: String, folder: URL, speakerCountHint: Int? = nil) {
        self.entryID = entryID
        self.folder = folder
        self.speakerCountHint = speakerCountHint
    }

    /// The Nemotron preset of a catalog entry, with `modelFileName` in the HF layout under `Models/<id>/`
    /// (`split/…` or `monolithic/v2/…`); nil for other entries.
    static func nemotronConfig(for entryID: String) -> Nemotron3Config? {
        guard let name = nemotronPresets[entryID], var config = Nemotron3Config.preset(named: name) else { return nil }
        config.modelFileName = config.hubSubdirectory + "/" + config.modelFileName
        return config
    }

    /// `channelURL` is a mono 16 kHz Float32 file. Speaker indexes follow FluidAudio's ids in order of first use.
    public func diarize(channelURL: URL, progress: @escaping @Sendable (Double) -> Void) async throws -> [DiarizationTurn] {
        if let config = Self.nemotronConfig(for: entryID) {
            let folder = folder
            // FluidAudio's Nemotron types are not Sendable: they live and die in this one task. Whole file at once,
            // so progress jumps to 1 at the end.
            let turns = try await Task.detached(priority: .utility) { () throws -> [DiarizationTurn] in
                let samples = try MonoAudio.read(channelURL, from: 0, to: nil)
                guard !samples.isEmpty else { return [] }
                // Never `loadFromHuggingFace`: its `discardStaleCache` deletes the folder, and `ModelHub.offlineMode`
                // blocks its download anyway.
                let models = try await Nemotron3Models.load(config: config, directory: folder)
                let output = try Nemotron3Diarizer(config: config, models: models).processComplete(samples)
                let segments = Nemotron3Diarizer.segments(
                    probabilities: output.probabilities, frameCount: output.frameCount)
                var indexes: [Int: Int] = [:]
                return segments.sorted { $0.startSeconds < $1.startSeconds }.map { segment in
                    let index = indexes[segment.speakerIndex] ?? indexes.count
                    indexes[segment.speakerIndex] = index
                    return DiarizationTurn(
                        start: Double(segment.startSeconds), end: Double(segment.endSeconds), speakerIndex: index)
                }
            }.value
            progress(1)
            return turns
        }
        let samples = try MonoAudio.read(channelURL, from: 0, to: nil)
        guard !samples.isEmpty else { return [] }
        let models = try await OfflineDiarizerModels.load(from: folder)
        var config = OfflineDiarizerConfig()
        if let speakerCountHint, speakerCountHint > 0 { config.clustering.numSpeakers = speakerCountHint }
        let manager = OfflineDiarizerManager(config: config)
        manager.initialize(models: models)
        let result = try await manager.process(audio: samples) { done, total in
            if total > 0 { progress(Double(done) / Double(total)) }
        }
        var indexes: [String: Int] = [:]
        return result.segments.sorted { $0.startTimeSeconds < $1.startTimeSeconds }.map { segment in
            let index = indexes[segment.speakerId] ?? indexes.count
            indexes[segment.speakerId] = index
            return DiarizationTurn(
                start: Double(segment.startTimeSeconds), end: Double(segment.endTimeSeconds), speakerIndex: index)
        }
    }
}
