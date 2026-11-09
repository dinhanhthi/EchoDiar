@testable import EchoTranscribe
import AVFoundation
import EchoAI
import EchoCore
import Foundation
import Testing

/// Guards Stop: cancelling the caller's task cancels the running job, which ends as `.cancelled`, not `.failed`, and
/// writes no transcript. The fake cloud transcriber ends on its own after 10 s, so a job the cancel never reaches
/// finishes instead of hanging.
struct CancelPipelineTests {
    @Test(.timeLimit(.minutes(1)))
    func aCancelledJobEndsAsCancelledAndWritesNothing() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let session = RecordingSession(state: .stopped, audioFileName: "audio.caf")
        let folder = session.folder(in: root)
        try Self.writeRecording(session, in: folder, seconds: 3)
        let pipeline = TranscriptionPipeline(
            modelStore: ModelStore(root: root.appendingPathComponent("Models")), gate: ModelLoadGate(),
            deferWhenConstrained: false)
        let job = TranscriptionJob(conversationID: session.id, folder: folder, processNow: true, diarize: false)
        let cloud = CloudEngine(
            engineID: "cloud:fake", name: "Fake", model: "batch-model", transcriber: SlowTranscriber())

        let caller = Task { try await pipeline.transcribe(job, cloud: cloud) }
        var steps: [JobProgress.Step] = []
        for await progress in pipeline.progress {
            steps.append(progress.step)
            if progress.step != .queued && progress.step != .deferred { caller.cancel() }
            if [.finished, .failed, .cancelled, .needsModel].contains(progress.step) { break }
        }
        _ = await caller.result

        #expect(steps.last == .cancelled)
        #expect(!steps.contains(.failed) && !steps.contains(.finished))
        #expect((try? Conversation.read(from: folder))?.status == .ready)
        #expect(!FileManager.default.fileExists(atPath: folder.appendingPathComponent(Transcript.fileName).path))
    }

    /// Answers one segment after 10 s.
    private struct SlowTranscriber: CloudTranscriber {
        func transcribe(file: URL, language: String?, diarize: Bool) async throws -> [CloudSegment] {
            try await Task.sleep(for: .seconds(10))
            return [CloudSegment(start: 0, end: 1, text: "too late")]
        }
    }

    /// A stopped recording folder with a stereo CAF of `seconds`, a 440 Hz tone on both channels.
    private static func writeRecording(_ session: RecordingSession, in folder: URL, seconds: Int) throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try session.write(to: folder)
        let frames = seconds * 16_000
        let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 2, interleaved: false)!
        let file = try AVAudioFile(
            forWriting: folder.appendingPathComponent(session.audioFileName), settings: format.settings,
            commonFormat: .pcmFormatFloat32, interleaved: false)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames))!
        buffer.frameLength = buffer.frameCapacity
        for index in 0..<frames {
            let sample = Float(sin(Double(index) * 2 * .pi * 440 / 16_000) * 0.5)
            buffer.floatChannelData![0][index] = sample
            buffer.floatChannelData![1][index] = sample
        }
        try file.write(from: buffer)
    }
}
