import EchoCore
import FluidAudio
import Foundation

/// FluidAudio's offline pyannote diarizer, run on the system channel only. Models come from
/// `Models/<id>/` through `OfflineDiarizerModels.load(from:)` (never `prepareModels`, which purges the folder
/// after a failed load); `ModelHub.offlineMode` (set by `ModelStore`) blocks any download.
public struct SpeakerDiarizer: Sendable {
    public let entryID: String
    /// `Models/<id>/`; FluidAudio appends `speaker-diarization/` itself.
    public let folder: URL
    /// Exact speaker count, nil = automatic.
    public let speakerCountHint: Int?

    public init(entryID: String, folder: URL, speakerCountHint: Int? = nil) {
        self.entryID = entryID
        self.folder = folder
        self.speakerCountHint = speakerCountHint
    }

    /// `channelURL` is a mono 16 kHz Float32 file. Speaker indexes follow FluidAudio's ids in order of first use.
    public func diarize(channelURL: URL, progress: @escaping @Sendable (Double) -> Void) async throws -> [DiarizationTurn] {
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
