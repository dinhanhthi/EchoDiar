@testable import EchoTranscribe
import AVFoundation
import EchoAI
import EchoCore
import Foundation
import Synchronization
import Testing

/// "Tell speakers apart" off: the job runs no diarizer, asks the cloud ASR for no speaker labels, adds no note,
/// labels the whole meeting audio "Speaker 1", and creates the missing `conversation.json` with the audio length.
struct SpeakersOffPipelineTests {
    @Test func speakersOffRunsNoDiarizerAndCreatesTheConversation() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let session = RecordingSession(state: .stopped, audioFileName: "audio.caf")
        let folder = session.folder(in: root)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try session.write(to: folder)
        try Self.writeStereo(seconds: 2, to: folder.appendingPathComponent(session.audioFileName))

        let asr = FakeTranscriber()
        let diarizer = FakeTranscriber()
        let pipeline = TranscriptionPipeline(
            modelStore: ModelStore(root: root.appendingPathComponent("Models")), gate: ModelLoadGate(),
            deferWhenConstrained: false)
        let job = TranscriptionJob(conversationID: session.id, folder: folder, processNow: true, diarize: false)

        _ = try await pipeline.transcribe(
            job, cloud: CloudEngine(engineID: "cloud:fake", name: "Fake", model: "", transcriber: asr),
            cloudDiarizer: CloudEngine(engineID: "cloud:fake-diarizer", name: "Fake", model: "", transcriber: diarizer))

        #expect(asr.diarizeFlags == [false])
        #expect(diarizer.diarizeFlags.isEmpty)
        let transcript = try Transcript.read(from: folder)
        #expect(transcript.notes.isEmpty)
        #expect(transcript.segments.count == 2)
        #expect(transcript.segments.allSatisfy { $0.speaker == Transcript.Speaker.system(1) })
        let conversation = try Conversation.read(from: folder)
        #expect(conversation.status == .transcribed)
        #expect(conversation.hasTranscript)
        #expect(abs(conversation.duration - 2) <= 0.01)
    }

    /// Returns two segments with distinct provider speaker labels, whatever `diarize` asks; records `diarize`.
    private final class FakeTranscriber: CloudTranscriber {
        private let flags = Mutex<[Bool]>([])
        var diarizeFlags: [Bool] { flags.withLock { $0 } }

        func transcribe(file: URL, language: String?, diarize: Bool) async throws -> [CloudSegment] {
            flags.withLock { $0.append(diarize) }
            return [
                CloudSegment(start: 0, end: 0.8, text: "hello there", speaker: "A"),
                CloudSegment(start: 1, end: 1.8, text: "general kenobi", speaker: "B"),
            ]
        }
    }

    /// A 16 kHz stereo Float32 CAF: silent mic (ch0, skipped), a 440 Hz tone on the system channel (ch1).
    private static func writeStereo(seconds: Int, to url: URL) throws {
        let frames = seconds * 16_000
        let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 2, interleaved: false)!
        let file = try AVAudioFile(forWriting: url, settings: format.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames))!
        buffer.frameLength = buffer.frameCapacity
        for index in 0..<frames {
            buffer.floatChannelData![0][index] = 0
            buffer.floatChannelData![1][index] = Float(sin(Double(index) * 2 * .pi * 440 / 16_000) * 0.5)
        }
        try file.write(from: buffer)
    }
}
