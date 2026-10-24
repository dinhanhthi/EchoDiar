import Foundation

/// The event form's values, before they are saved to EchoDiar's own calendar.
public struct CalendarEventDraft: Sendable, Equatable {
    /// How the event repeats.
    public enum Repeat: String, Sendable, CaseIterable {
        case none, daily, weekdays, weekly, monthly
        /// A rule read back from another app that the form can't represent; kept as-is unless changed.
        case custom
    }

    public var title: String
    public var start: Date
    public var end: Date
    /// The meeting link as typed; may be empty.
    public var urlText: String
    public var repeatRule: Repeat
    /// Last day of the series (inclusive); nil = never ends.
    public var repeatEnd: Date?

    public init(
        title: String, start: Date, end: Date, urlText: String = "",
        repeatRule: Repeat = .none, repeatEnd: Date? = nil
    ) {
        self.title = title
        self.start = start
        self.end = end
        self.urlText = urlText
        self.repeatRule = repeatRule
        self.repeatEnd = repeatEnd
    }

    /// The trimmed link when it is `https` with a host, else nil. The link is user input: no other scheme is kept.
    public var url: URL? {
        let text = urlText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: text), url.scheme?.lowercased() == "https",
              let host = url.host(), !host.isEmpty else { return nil }
        return url
    }

    /// Whether the form can be saved: a title, end after start, an empty or valid link, and a series end not before the start day.
    public var isValid: Bool {
        guard !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, end > start else { return false }
        guard urlText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || url != nil else { return false }
        if let repeatEnd, repeatEnd < Calendar.current.startOfDay(for: start) { return false }
        return true
    }
}
