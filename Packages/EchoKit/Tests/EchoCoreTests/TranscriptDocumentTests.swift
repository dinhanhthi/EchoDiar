import EchoCore
import Foundation
import Testing

/// Guards the copy of a selection across several messages in the transcript text view.
struct TranscriptDocumentTests {
    @Test func selectionAcrossTwoBodiesCopiesOneLinePerMessageWithThePartsAndNames() {
        let segments = [
            Transcript.Segment(start: 5, end: 9, channel: .mic, speaker: "me", text: "Hello there everyone"),
            Transcript.Segment(start: 72, end: 80, channel: .system, speaker: "s1", text: "Good morning to you"),
        ]
        let names = ["me": "Thi", "s1": "Alice"]
        let document = TranscriptDocument(segments: segments, names: { names[$0.speaker] ?? $0.speaker })

        // From "there…" in the first body to "…morning" in the second.
        let first = document.entries[0].bodyRange, second = document.entries[1].bodyRange
        let start = first.location + 6
        let range = NSRange(location: start, length: second.location + 12 - start)

        #expect(document.copyText(for: range) == "Thi [00:05]: there everyone\nAlice [01:12]: Good morning")
    }
}
