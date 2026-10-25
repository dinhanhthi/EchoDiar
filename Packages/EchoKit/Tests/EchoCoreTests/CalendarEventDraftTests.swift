import EchoCore
import Foundation
import Testing

/// Guards event form validation: the meeting link is user input, so only empty or https with a host may be saved.
struct CalendarEventDraftTests {
    private static let start = Date(timeIntervalSince1970: 1_800_000_000)

    @Test(arguments: [
        // Empty link and an https link are fine.
        ("", 1800.0, true),
        ("https://zoom.us/j/1", 1800.0, true),
        // Plain http and a script URL are refused.
        ("http://zoom.us/j/1", 1800.0, false),
        ("javascript:alert(1)", 1800.0, false),
        // End must come after start.
        ("", 0.0, false),
    ] as [(String, TimeInterval, Bool)])
    func isValid(urlText: String, duration: TimeInterval, expected: Bool) {
        let draft = CalendarEventDraft(
            title: "Standup", start: Self.start, end: Self.start.addingTimeInterval(duration), urlText: urlText)
        #expect(draft.isValid == expected)
    }
}
