import AppKit
import EchoCore
import EventKit

/// The user's calendars through EventKit: access, the calendar list and event occurrences copied out as
/// `CalendarEvent`. Event titles and notes come from invites, so they are data only and never logged.
@MainActor
final class CalendarStore {
    /// One event calendar, for the settings list.
    struct CalendarInfo: Identifiable, Hashable {
        let id: String
        let title: String
        let sourceTitle: String
        let color: NSColor
    }

    /// Called on the main actor when the calendar database changes.
    var onChange: (() -> Void)?

    private var store = EKEventStore()
    private nonisolated(unsafe) var observer: (any NSObjectProtocol)?

    init() {
        // Any store's change, so a recreated store needs no new observer.
        observer = NotificationCenter.default.addObserver(forName: .EKEventStoreChanged, object: nil, queue: .main) {
            [weak self] _ in
            MainActor.assumeIsolated { self?.onChange?() }
        }
    }

    deinit {
        if let observer { NotificationCenter.default.removeObserver(observer) }
    }

    /// The status last read. A store made without access never sees the calendars, so a change to full access (the
    /// in-app prompt or System Settings) replaces it before any query.
    private var lastStatus = EKEventStore.authorizationStatus(for: .event)

    var status: EKAuthorizationStatus {
        let status = EKEventStore.authorizationStatus(for: .event)
        if status == .fullAccess, lastStatus != .fullAccess { store = EKEventStore() }
        lastStatus = status
        return status
    }

    var isAuthorized: Bool { status == .fullAccess }

    /// Asks for full access to events. On grant, `status` recreates the store before the next query.
    func requestAccess() async -> Bool {
        do {
            return try await store.requestFullAccessToEvents()
        } catch {
            NSLog("EchoDiar: calendar access request failed: \(error)")
            return false
        }
    }

    /// Event calendars sorted by source then title; empty when not authorized.
    func calendars() -> [CalendarInfo] {
        guard isAuthorized else { return [] }
        return store.calendars(for: .event)
            .map {
                CalendarInfo(
                    id: $0.calendarIdentifier, title: $0.title, sourceTitle: $0.source?.title ?? "",
                    color: $0.color ?? .secondaryLabelColor)
            }
            .sorted { ($0.sourceTitle, $0.title) < ($1.sourceTitle, $1.title) }
    }

    /// Occurrences overlapping `from..<to` in every calendar; empty when not authorized.
    func events(from: Date, to: Date) -> [CalendarEvent] {
        guard isAuthorized else { return [] }
        let predicate = store.predicateForEvents(withStart: from, end: to, calendars: nil)
        return store.events(matching: predicate).map { event in
            let baseID = event.eventIdentifier ?? event.calendarItemIdentifier
            let status = event.attendees?.first(where: \.isCurrentUser)?.participantStatus
            // Subscribed, birthday and read-only calendars hold events the user never answered: there "no attendees"
            // does not confirm.
            let ownCalendar = event.calendar.type != .subscription && event.calendar.type != .birthday
                && event.calendar.allowsContentModifications
            return CalendarEvent(
                id: baseID + "|" + String(Int(event.startDate.timeIntervalSince1970)),
                title: Self.displayTitle(event.title),
                start: event.startDate,
                end: event.endDate,
                isAllDay: event.isAllDay,
                calendarID: event.calendar.calendarIdentifier,
                link: MeetingLink.find(url: event.url, location: event.location, notes: event.notes),
                declined: status == .declined,
                canceled: event.status == .canceled,
                // An invite added without the user's answer (one from a stranger too) never starts silently.
                confirmed: ((event.attendees ?? []).isEmpty && ownCalendar) || event.organizer?.isCurrentUser == true
                    || status == .accepted || status == .tentative
            )
        }
    }

    /// An invite's title as safe display text: control characters become spaces, format characters (bidi
    /// overrides, invisible marks) go, except the joiners that emoji and some scripts need; one line, 200 characters.
    private static func displayTitle(_ title: String?) -> String {
        var scalars = String.UnicodeScalarView()
        for scalar in (title ?? "").unicodeScalars {
            switch scalar.properties.generalCategory {
            case .control, .lineSeparator, .paragraphSeparator: scalars.append(" ")
            case .format where scalar != "\u{200C}" && scalar != "\u{200D}": continue
            default: scalars.append(scalar)
            }
        }
        return String(String(scalars).split(whereSeparator: \.isWhitespace).joined(separator: " ").prefix(200))
    }
}
