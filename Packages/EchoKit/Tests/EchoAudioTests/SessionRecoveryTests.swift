import AVFoundation
import Darwin
import EchoCore
import Foundation
import Testing
@testable import EchoAudio

/// Guards crash recovery. Every test uses its own temporary root, never Application Support.
struct SessionRecoveryTests {
    /// A real crash cannot be simulated in-process, so the CAF is written by `ChannelWriter` (the production
    /// write path) fed by synthetic 16 kHz rings, flushed with `pause()` and fsynced through a separate fd,
    /// and recovery runs while the writer still holds the `ExtAudioFileRef` open (CAF data size still -1);
    /// `stop()` runs only afterwards, for cleanup.
    @Test func unclosedFsyncedCAFIsRecovered() throws {
        try withTemporaryRoot { root in
            let session = RecordingSession(pid: 999_999, state: .recording)
            let folder = session.folder(in: root)
            try session.write(to: folder)
            let audioURL = folder.appendingPathComponent(session.audioFileName)

            let frames = 8_000
            let writer = try ChannelWriter(fileURL: audioURL)
            defer { try? writer.stop() }
            let mic = RingBuffer(capacityFrames: 16_384, sampleRate: 16_000)
            let system = RingBuffer(capacityFrames: 16_384, sampleRate: 16_000)
            let format = ChannelWriter.SourceFormat(sampleRate: 16_000, channels: 1)
            try writer.start(micRing: mic, systemRing: system, micFormat: format, systemFormat: format)
            let samples = (0..<frames).map { Float(sin(Double($0) * 2 * .pi * 440 / 16_000) * 0.5) }
            let hostTime = mach_absolute_time()
            mic.write(samples, count: frames, hostTime: hostTime)
            system.write(samples, count: frames, hostTime: hostTime)
            writer.pause()

            let fd = open(audioURL.path, O_RDONLY)
            #expect(fd >= 0)
            #expect(fsync(fd) == 0)
            close(fd)

            let written = writer.stats.framesWritten
            #expect(writer.stats.failure == nil)
            #expect(written == Int64(frames))

            let results = SessionRecovery.recover(root: root, isOwnerAlive: { _ in false })

            let file = try AVAudioFile(forReading: audioURL)
            #expect(file.length == written)
            #expect(file.fileFormat.channelCount == 2)
            let result = try #require(results.first)
            #expect(results.count == 1)
            #expect(result.state == .recovered)
            #expect(result.duration > 0)
            #expect(try RecordingSession.read(from: folder).state == .recovered)
        }
    }

    @Test func missingCAFFails() throws {
        try withTemporaryRoot { root in
            let session = RecordingSession(pid: 999_999, state: .recording)
            let folder = session.folder(in: root)
            try session.write(to: folder)

            let results = SessionRecovery.recover(root: root, isOwnerAlive: { _ in false })

            #expect(results.map(\.state) == [.failed])
            #expect(results.first?.duration == 0)
            #expect(try RecordingSession.read(from: folder).state == .failed)
        }
    }

    @Test func liveOwnerIsUntouched() throws {
        try withTemporaryRoot { root in
            let session = RecordingSession(pid: 999_999, state: .recording)
            let folder = session.folder(in: root)
            try session.write(to: folder)
            let before = try Data(contentsOf: folder.appendingPathComponent(RecordingSession.fileName))

            let results = SessionRecovery.recover(root: root, isOwnerAlive: { _ in true })

            #expect(results.isEmpty)
            #expect(try Data(contentsOf: folder.appendingPathComponent(RecordingSession.fileName)) == before)
        }
    }

    /// Recovery runs in a task nothing awaits, so it can meet a session this process has just started.
    @Test func sessionOfThisProcessIsUntouched() throws {
        try withTemporaryRoot { root in
            let session = RecordingSession(pid: getpid(), state: .recording)
            let folder = session.folder(in: root)
            try session.write(to: folder)
            let before = try Data(contentsOf: folder.appendingPathComponent(RecordingSession.fileName))

            let results = SessionRecovery.recover(root: root, isOwnerAlive: { _ in false })

            #expect(results.isEmpty)
            #expect(try Data(contentsOf: folder.appendingPathComponent(RecordingSession.fileName)) == before)
        }
    }

    /// Runs `body` with a fresh `tmp/<UUID>` conversations root, removed afterwards.
    private func withTemporaryRoot(_ body: (URL) throws -> Void) throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try body(root)
    }
}
