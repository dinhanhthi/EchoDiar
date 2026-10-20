@testable import EchoTranscribe
import Testing

/// Guards the live boundary fragments: a segment cut by the window end is held and decoded again in full.
struct LiveTranscriberCutTests {
    @Test func holdsASegmentRunningIntoTheWindowEndFromItsStart() {
        #expect(LiveTranscriber.cut([(start: 0.5, end: 4), (start: 7.5, end: 10)], window: 10) == 7.5)
    }

    @Test func fallsBackToTheOverlapForNonStopSpeechLongerThanMaxKeep() {
        #expect(LiveTranscriber.cut([(start: 3, end: 10)], window: 10) == 9)
    }
}
