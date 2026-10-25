import Foundation

/// A video-call link in a calendar event. Event content is untrusted: only `https` links on allowlisted hosts count.
public struct MeetingLink: Sendable, Hashable {
    /// Brand name, not localized.
    public let serviceName: String
    public let url: URL

    /// The first allowlisted link in the event URL, then its location, then its notes.
    public static func find(url: URL?, location: String?, notes: String?) -> MeetingLink? {
        let candidates = [url].compactMap { $0 } + links(in: location) + links(in: notes)
        return candidates.lazy.compactMap(meetingLink(for:)).first
    }

    /// Host (or parent domain) → service.
    private static let services = [
        ("zoom.us", "Zoom"),
        ("meet.google.com", "Google Meet"),
        ("teams.microsoft.com", "Microsoft Teams"),
        ("teams.live.com", "Microsoft Teams"),
        ("webex.com", "Webex"),
    ]

    private static func meetingLink(for url: URL) -> MeetingLink? {
        guard url.scheme?.lowercased() == "https", let host = url.host()?.lowercased() else { return nil }
        guard let service = services.first(where: { host == $0.0 || host.hasSuffix("." + $0.0) }) else { return nil }
        return MeetingLink(serviceName: service.1, url: url)
    }

    private static func links(in text: String?) -> [URL] {
        guard let text, let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue)
        else { return [] }
        return detector.matches(in: text, range: NSRange(text.startIndex..., in: text)).compactMap(\.url)
    }
}
