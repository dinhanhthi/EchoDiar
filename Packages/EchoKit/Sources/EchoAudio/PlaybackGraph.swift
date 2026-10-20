import AVFoundation
import EchoCore

/// Wires a player of a recording into an engine. The recording keeps the mic left and the system right for
/// transcription; `.mixed` downmixes them to mono, which the main mixer plays in both ears (about -3 dB each).
public enum PlaybackGraph {
    /// Attaches `player`, which plays audio in `format`, and connects it to `engine`'s main mixer.
    public static func connect(
        _ player: AVAudioPlayerNode, to engine: AVAudioEngine, format: AVAudioFormat, channels: PlaybackChannels
    ) {
        engine.attach(player)
        guard channels == .mixed, let mono = AVAudioFormat(standardFormatWithSampleRate: format.sampleRate, channels: 1)
        else {
            engine.connect(player, to: engine.mainMixerNode, format: format)
            return
        }
        let downmix = AVAudioMixerNode()
        engine.attach(downmix)
        engine.connect(player, to: downmix, format: format)
        engine.connect(downmix, to: engine.mainMixerNode, format: mono)
    }
}
