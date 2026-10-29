@testable import EchoTranscribe
import Testing

/// Guards the "N s behind" notice of the live transcript: shown only while listening with at least
/// `behindNoticeSeconds` of speech waiting to be decoded, as rounded seconds.
struct RecordingTranscriberStatusTests {
    typealias Snapshot = RecordingTranscriber.Snapshot

    @Test func behindOnlyWhileListeningFromTheNoticeThreshold() {
        let notice = Double(RecordingTranscriber.behindNoticeSeconds)

        #expect(RecordingTranscriber.behindSeconds(Snapshot(state: .listening, pendingSeconds: notice - 0.5)) == nil)
        #expect(RecordingTranscriber.behindSeconds(Snapshot(state: .listening, pendingSeconds: notice))
            == RecordingTranscriber.behindNoticeSeconds)
        #expect(RecordingTranscriber.behindSeconds(Snapshot(state: .listening, pendingSeconds: 42.5)) == 43)
        #expect(RecordingTranscriber.behindSeconds(Snapshot(state: .paused, pendingSeconds: 60)) == nil)
        #expect(RecordingTranscriber.behindSeconds(Snapshot(state: .loading, pendingSeconds: 60)) == nil)
    }
}
