import Testing
@testable import EchoAudio

/// Guards when the OS mutes the mic: every channel muted, or every channel's input volume at 0. A property the device
/// lacks (empty list) mutes nothing.
struct InputMuteTests {
    @Test(arguments: [
        ([true, true], [Float](), true),
        ([true, false], [Float](), false),
        ([false], [Float(0)], true),
        ([false], [Float(0), Float(0.4)], false),
        ([Bool](), [Float](), false),
    ])
    func decisionRule(mutes: [Bool], volumes: [Float], muted: Bool) {
        #expect(InputMute.isMuted(mutes: mutes, volumes: volumes) == muted)
    }
}
