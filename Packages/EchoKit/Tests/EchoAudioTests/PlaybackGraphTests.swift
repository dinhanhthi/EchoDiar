import AVFoundation
import EchoCore
import Testing
@testable import EchoAudio

/// Renders the playback graph offline with sound on one recorded channel only: the mic (0, left) as when only the
/// user speaks, or the system (1, right) as when only the others do.
struct PlaybackGraphTests {
    @Test(arguments: [0, 1])
    func mixedPlaysEachChannelInBothEars(channel: Int) throws {
        let (left, right) = try render(.mixed, soundOn: channel)
        #expect(left > 0.1)
        #expect(abs(left - right) < 0.001)
    }

    @Test func splitKeepsTheMicInTheLeftEar() throws {
        let (left, right) = try render(.split, soundOn: 0)
        #expect(left > 0.1)
        #expect(right == 0)
    }

    /// RMS of the left and right output channels.
    private func render(_ channels: PlaybackChannels, soundOn channel: Int) throws -> (Float, Float) {
        let frames: AVAudioFrameCount = 4_096
        let stereo = try #require(AVAudioFormat(standardFormatWithSampleRate: 16_000, channels: 2))
        let engine = AVAudioEngine()
        let player = AVAudioPlayerNode()
        PlaybackGraph.connect(player, to: engine, format: stereo, channels: channels)
        try engine.enableManualRenderingMode(.offline, format: stereo, maximumFrameCount: frames)
        try engine.start()
        defer { engine.stop() }

        let input = try #require(AVAudioPCMBuffer(pcmFormat: stereo, frameCapacity: frames))
        input.frameLength = frames
        for i in 0..<Int(frames) {
            input.floatChannelData![channel][i] = 0.5 * sin(Float(i) * 0.1)
            input.floatChannelData![1 - channel][i] = 0
        }
        player.scheduleBuffer(input)
        player.play()

        let output = try #require(AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: frames))
        #expect(try engine.renderOffline(frames, to: output) == .success)
        func rms(_ channel: Int) -> Float {
            let samples = UnsafeBufferPointer(start: output.floatChannelData![channel], count: Int(output.frameLength))
            return (samples.reduce(0) { $0 + $1 * $1 } / Float(samples.count)).squareRoot()
        }
        return (rms(0), rms(1))
    }
}
