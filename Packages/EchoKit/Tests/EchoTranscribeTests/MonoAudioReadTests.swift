@testable import EchoTranscribe
import AVFoundation
import Foundation
import Testing

/// Guards the input of every ASR engine and the diarizer: the exact samples of a mono file, whole or a range,
/// across read chunk boundaries.
struct MonoAudioReadTests {
    @Test func readsTheWholeFileAndARangeExactly() throws {
        let samples = (0..<200_000).map { Float($0 % 1_000) / 1_000 }
        let url = try Self.writeMono(samples)
        defer { try? FileManager.default.removeItem(at: url) }

        #expect(try MonoAudio.read(url, from: 0, to: nil) == samples)
        #expect(try MonoAudio.read(url, from: 1, to: 5.5) == Array(samples[16_000..<88_000]))
        #expect(try MonoAudio.read(url, from: 12, to: 60) == Array(samples[192_000...]))
    }

    /// A 16 kHz mono Float32 CAF, as `MonoAudio.extract` writes.
    private static func writeMono(_ samples: [Float]) throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("mono-read-\(UUID().uuidString).caf")
        let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false)!
        let file = try AVAudioFile(forWriting: url, settings: format.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(samples.count))!
        buffer.frameLength = buffer.frameCapacity
        samples.withUnsafeBufferPointer { buffer.floatChannelData![0].update(from: $0.baseAddress!, count: samples.count) }
        try file.write(from: buffer)
        return url
    }
}
