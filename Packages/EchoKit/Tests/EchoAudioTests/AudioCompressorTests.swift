import AVFoundation
import Darwin
import EchoCore
import Foundation
import Synchronization
import Testing
@testable import EchoAudio

/// Guards against data loss: the CAF is deleted only after a verified encode, and a failed encode keeps it.
/// Every test uses its own temporary root, never Application Support.
struct AudioCompressorTests {
    @Test func verifiedEncodeReplacesCAF() async throws {
        try await withTemporaryRoot { root in
            let folder = try writeStoppedSession(in: root, frames: 32_000)
            let caf = folder.appendingPathComponent("audio.caf")
            let cafFile = try AVAudioFile(forReading: caf)
            let cafDuration = Double(cafFile.length) / cafFile.fileFormat.sampleRate

            let result = await AudioCompressor().compress(folder: folder, bitrateKbps: 64)

            #expect(result.failure == nil)
            #expect(result.outputFileNames == [AudioCompressor.stereoFileName])
            let m4a = folder.appendingPathComponent(AudioCompressor.stereoFileName)
            let m4aFile = try AVAudioFile(forReading: m4a)
            let m4aDuration = Double(m4aFile.length) / m4aFile.fileFormat.sampleRate
            #expect(abs(m4aDuration - cafDuration) <= 0.1)
            #expect(!FileManager.default.fileExists(atPath: caf.path))
            #expect(try RecordingSession.read(from: folder).audioFileName == AudioCompressor.stereoFileName)
            #expect(try Conversation.read(from: folder).status == .ready)
        }
    }

    /// Compression runs after transcription, so it must not turn a transcribed (or failed) recording back into
    /// `ready`; `.compressing` is the launch retry of a compression interrupted after transcription.
    @Test("compressor keeps the transcription status", arguments: [
        (Conversation.Status.transcribed, true, Conversation.Status.transcribed),
        (.compressing, true, .transcribed),
        (.transcriptionFailed, false, .transcriptionFailed),
    ])
    func compressorKeepsTranscriptionStatus(before: Conversation.Status, hasTranscript: Bool, after: Conversation.Status) async throws {
        try await withTemporaryRoot { root in
            let folder = try writeStoppedSession(in: root, frames: 32_000)
            var conversation = Conversation(session: try RecordingSession.read(from: folder), folder: folder, duration: 2)
            conversation.status = before
            conversation.hasTranscript = hasTranscript
            try conversation.write()
            let compressor = AudioCompressor { source, destination, channel, bitRate in
                try AudioCompressor.encodeAAC(source: source, destination: destination, channel: channel, bitRate: bitRate)
            }

            let result = await compressor.compress(folder: folder, bitrateKbps: 64)

            #expect(result.failure == nil)
            let written = try Conversation.read(from: folder)
            #expect(written.status == after)
            #expect(written.hasTranscript == hasTranscript)
            #expect(try RecordingSession.read(from: folder).audioFileName.hasSuffix(".m4a"))
        }
    }

    /// The injected encoder leaves a partial file behind before failing, so cleanup is checked too.
    @Test func failedEncodeKeepsCAF() async throws {
        try await withTemporaryRoot { root in
            let folder = try writeStoppedSession(in: root, frames: 32_000)
            let caf = folder.appendingPathComponent("audio.caf")
            let compressor = AudioCompressor { _, destination, _, _ in
                try Data([0, 1, 2, 3]).write(to: destination)
                throw AudioCompressorError.encode(-1)
            }

            let result = await compressor.compress(folder: folder, bitrateKbps: 64)

            #expect(result.failure == .encode(-1))
            #expect(result.outputFileNames.isEmpty)
            #expect(FileManager.default.fileExists(atPath: caf.path))
            for name in [AudioCompressor.stereoFileName, AudioCompressor.micFileName, AudioCompressor.systemFileName] {
                #expect(!FileManager.default.fileExists(atPath: folder.appendingPathComponent(name).path))
            }
            #expect(try RecordingSession.read(from: folder).audioFileName == "audio.caf")
            #expect(try Conversation.read(from: folder).status == .compressionFailed)
        }
    }

    /// The post-Stop compression runs at `.utility` when no recording is active (`.background` ran it on E-cores
    /// only, several times slower), and keeps `.background` otherwise.
    @Test("compressor encodes at the job's QoS", arguments: [
        (DispatchQoS.background, QOS_CLASS_BACKGROUND),
        (.utility, QOS_CLASS_UTILITY),
    ])
    func encodesAtJobQoS(qos: DispatchQoS, expected: qos_class_t) async throws {
        try await withTemporaryRoot { root in
            let folder = try writeStoppedSession(in: root, frames: 32_000)
            let seen = Mutex<qos_class_t?>(nil)
            let compressor = AudioCompressor { _, _, _, _ in
                seen.withLock { $0 = qos_class_self() }
                throw AudioCompressorError.encode(-1)
            }

            _ = await compressor.compress(folder: folder, bitrateKbps: 64, qos: qos)

            #expect(seen.withLock { $0 } == expected)
        }
    }

    /// The encoder writes a valid m4a of a shorter recording, so only the duration check can reject it.
    @Test func shortOutputFailsDurationCheckAndKeepsCAF() async throws {
        try await withTemporaryRoot { root in
            let folder = try writeStoppedSession(in: root, frames: 32_000)
            let caf = folder.appendingPathComponent("audio.caf")
            let shortCAF = try writeStoppedSession(in: root, frames: 8_000).appendingPathComponent("audio.caf")
            let compressor = AudioCompressor { _, destination, channel, bitRate in
                try AudioCompressor.encodeAAC(source: shortCAF, destination: destination, channel: channel, bitRate: bitRate)
            }

            let result = await compressor.compress(folder: folder, bitrateKbps: 64)

            guard case .durationMismatch(let source, let output) = result.failure else {
                Issue.record("expected durationMismatch, got \(String(describing: result.failure))")
                return
            }
            #expect(abs(source - 2) < 0.01)
            #expect(output < source - AudioCompressor.durationTolerance)
            #expect(result.outputFileNames.isEmpty)
            #expect(FileManager.default.fileExists(atPath: caf.path))
            for name in [AudioCompressor.stereoFileName, AudioCompressor.micFileName, AudioCompressor.systemFileName] {
                #expect(!FileManager.default.fileExists(atPath: folder.appendingPathComponent(name).path))
            }
            #expect(try RecordingSession.read(from: folder).audioFileName == "audio.caf")
            #expect(try Conversation.read(from: folder).status == .compressionFailed)
        }
    }

    /// An untrusted `session.json` points `audioFileName` at a real CAF outside the folder: it must be neither
    /// encoded nor deleted.
    @Test func audioFileNameOutsideFolderIsRejected() async throws {
        try await withTemporaryRoot { root in
            let folder = try writeStoppedSession(in: root, frames: 32_000)
            let outside = root.appendingPathComponent("outside.caf")
            try FileManager.default.moveItem(at: folder.appendingPathComponent("audio.caf"), to: outside)
            let outsideSize = try FileManager.default.attributesOfItem(atPath: outside.path)[.size] as? Int
            var session = try RecordingSession.read(from: folder)
            session.audioFileName = "../outside.caf"
            try session.write(to: folder)

            let result = await AudioCompressor().compress(folder: folder, bitrateKbps: 64)

            #expect(result.failure == .unsafeAudioFileName)
            #expect(result.outputFileNames.isEmpty)
            #expect(FileManager.default.fileExists(atPath: outside.path))
            #expect(try FileManager.default.attributesOfItem(atPath: outside.path)[.size] as? Int == outsideSize)
            let m4as = try FileManager.default.contentsOfDirectory(atPath: folder.path)
                .filter { $0.hasPrefix("audio") && $0.hasSuffix(".m4a") }
            #expect(m4as.isEmpty)
            #expect(try Conversation.read(from: folder).status == .compressionFailed)
        }
    }

    /// Writes a stopped session whose CAF holds `frames` of a 440 Hz tone on both channels, written by
    /// `ChannelWriter` (the production write path) and closed with `stop()`. Returns the session folder.
    private func writeStoppedSession(in root: URL, frames: Int) throws -> URL {
        let session = RecordingSession(pid: 999_999, state: .stopped)
        let folder = session.folder(in: root)
        try session.write(to: folder)

        let writer = try ChannelWriter(fileURL: folder.appendingPathComponent(session.audioFileName))
        let mic = RingBuffer(capacityFrames: 32_768, sampleRate: 16_000)
        let system = RingBuffer(capacityFrames: 32_768, sampleRate: 16_000)
        let format = ChannelWriter.SourceFormat(sampleRate: 16_000, channels: 1)
        try writer.start(micRing: mic, systemRing: system, micFormat: format, systemFormat: format)
        let samples = (0..<frames).map { Float(sin(Double($0) * 2 * .pi * 440 / 16_000) * 0.5) }
        let hostTime = mach_absolute_time()
        mic.write(samples, count: frames, hostTime: hostTime)
        system.write(samples, count: frames, hostTime: hostTime)
        try writer.stop()

        #expect(writer.stats.failure == nil)
        #expect(writer.stats.framesWritten == Int64(frames))
        return folder
    }

    /// Runs `body` with a fresh `tmp/<UUID>` conversations root, removed afterwards.
    private func withTemporaryRoot(_ body: (URL) async throws -> Void) async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try await body(root)
    }
}
