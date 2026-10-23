import EchoCore
import Foundation
import Testing

/// Guards which calendar links the app treats as meetings: event content is untrusted, so only https allowlisted hosts.
struct MeetingLinkTests {
    @Test(arguments: [
        // A link inside the notes text is found.
        (nil, nil, "Join: https://us02web.zoom.us/j/123?pwd=x thanks", "Zoom https://us02web.zoom.us/j/123?pwd=x"),
        // Plain http and a look-alike host are refused.
        (URL(string: "http://zoom.us/j/1"), nil, nil, nil),
        (nil, nil, "https://zoom.us.evil.com/j/1", nil),
        // An unrelated https link is not a meeting.
        (nil, "https://example.com/room", nil, nil),
        // The event URL wins over the notes.
        (URL(string: "https://meet.google.com/abc-defg-hij"), nil, "https://zoom.us/j/1",
         "Google Meet https://meet.google.com/abc-defg-hij"),
    ] as [(URL?, String?, String?, String?)])
    func find(url: URL?, location: String?, notes: String?, expected: String?) {
        let link = MeetingLink.find(url: url, location: location, notes: notes)
        #expect(link.map { "\($0.serviceName) \($0.url.absoluteString)" } == expected)
    }
}
