import Darwin
import Foundation
import Testing
@testable import EchoAudio

/// Guards the "No system audio" banner: a tap that never delivered sound is suspect, one that went quiet after
/// delivering sound (the call ended, the app still holds its output open) is not.
struct SilentTapTests {
    @Test func zerosFromTheStartWhileTheTargetPlaysAreSuspected() throws {
        #expect(try silentTapSuspected(soundFrames: 0) == true)
    }

    @Test func zerosAfterSoundAreNotSuspected() throws {
        #expect(try silentTapSuspected(soundFrames: 16_000) == false)
    }

    /// Writes `soundFrames` of a 440 Hz tone then 6 s of zeros to the system channel while the target reports output.
    private func silentTapSuspected(soundFrames: Int) throws -> Bool {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID().uuidString).caf")
        defer { try? FileManager.default.removeItem(at: url) }
        let frames = soundFrames + 6 * 16_000
        let writer = try ChannelWriter(fileURL: url)
        writer.isTargetPlayingOutput = { true }
        let mic = RingBuffer(capacityFrames: frames, sampleRate: 16_000)
        let system = RingBuffer(capacityFrames: frames, sampleRate: 16_000)
        let format = ChannelWriter.SourceFormat(sampleRate: 16_000, channels: 1)
        try writer.start(micRing: mic, systemRing: system, micFormat: format, systemFormat: format)
        let sound = (0..<frames).map { $0 < soundFrames ? Float(sin(Double($0) * 2 * .pi * 440 / 16_000) * 0.5) : 0 }
        let hostTime = mach_absolute_time()
        mic.write(sound, count: frames, hostTime: hostTime)
        system.write(sound, count: frames, hostTime: hostTime)
        try writer.stop()
        #expect(writer.stats.failure == nil)
        return writer.stats.silentTapSuspected
    }
}
