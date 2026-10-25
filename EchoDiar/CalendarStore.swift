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

    /// Occurrences overlapping `from..<to` in every calendar; empty when not authorized. `ownCalendarID` is
    /// EchoDiar's calendar, whose events without invitees count as the user's.
    func events(from: Date, to: Date, ownCalendarID: String?) -> [CalendarEvent] {
        guard isAuthorized else { return [] }
        let predicate = store.predicateForEvents(withStart: from, end: to, calendars: nil)
        return store.events(matching: predicate).map { event in
            let status = event.attendees?.first(where: \.isCurrentUser)?.participantStatus
            // "No attendees" confirms only where no one else can add events: "On My Mac" (never shared) and EchoDiar's
            // calendar. EventKit has no shared flag, and a writable calendar may be someone's share, so those ask.
            // Subscribed, birthday and read-only calendars never confirm this way.
            let trustedCalendar = event.calendar.type != .subscription && event.calendar.type != .birthday
                && event.calendar.allowsContentModifications
                && (event.calendar.source?.sourceType == .local
                    || (ownCalendarID != nil && event.calendar.calendarIdentifier == ownCalendarID))
            return CalendarEvent(
                id: occurrenceID(event),
                title: Self.displayTitle(event.title),
                start: event.startDate,
                end: event.endDate,
                isAllDay: event.isAllDay,
                calendarID: event.calendar.calendarIdentifier,
                link: MeetingLink.find(url: event.url, location: event.location, notes: event.notes),
                declined: status == .declined,
                canceled: event.status == .canceled,
                // An invite added without the user's answer (one from a stranger too) never starts silently.
                confirmed: ((event.attendees ?? []).isEmpty && trustedCalendar) || event.organizer?.isCurrentUser == true
                    || status == .accepted || status == .tentative
            )
        }
    }

    /// One occurrence's id: the event id plus its start in whole seconds.
    private func occurrenceID(_ event: EKEvent) -> String {
        let baseID = event.eventIdentifier ?? event.calendarItemIdentifier
        return baseID + "|" + String(Int(event.startDate.timeIntervalSince1970))
    }

    // MARK: - EchoDiar's own calendar

    /// EchoDiar's calendar, created when missing: the stored id, else a writable "On My Mac" calendar titled "EchoDiar",
    /// else a new one on the local source, else on the default calendar's source. A calendar on another source is never
    /// adopted by title: one shared with the user could hold events its owner adds.
    func ownCalendar(storedID: String?) throws -> EKCalendar {
        guard isAuthorized else { throw CalendarStoreError.noAccess }
        if let calendar = existingOwnCalendar(storedID: storedID) { return calendar }
        func make(on source: EKSource) throws -> EKCalendar {
            let calendar = EKCalendar(for: .event, eventStore: store)
            calendar.title = Self.ownCalendarTitle
            calendar.source = source
            try store.saveCalendar(calendar, commit: true)
            return calendar
        }
        if let local = store.sources.first(where: { $0.sourceType == .local }) {
            do {
                return try make(on: local)
            } catch {
                NSLog("EchoDiar: creating the calendar on the local source failed: \(error)")
            }
        }
        guard let source = store.defaultCalendarForNewEvents?.source else { throw CalendarStoreError.noWritableSource }
        return try make(on: source)
    }

    /// Saves `draft` as a new event, or over the `editing` occurrence, in EchoDiar's calendar. Returns that calendar's
    /// id to store.
    func saveEvent(_ draft: CalendarEventDraft, editing: CalendarEvent?, span: EKSpan, storedCalendarID: String?) throws
        -> String
    {
        let calendar: EKCalendar
        let event: EKEvent
        if let editing {
            (calendar, event) = try ownOccurrence(editing, storedCalendarID: storedCalendarID)
        } else {
            calendar = try ownCalendar(storedID: storedCalendarID)
            event = EKEvent(eventStore: store)
            event.calendar = calendar
        }
        event.title = draft.title.trimmingCharacters(in: .whitespacesAndNewlines)
        event.startDate = draft.start
        event.endDate = draft.end
        event.url = draft.url
        // One occurrence of a series can't change the rule (the editor only offers this span when Repeat is unchanged);
        // a rule the form can't show is kept.
        let keepsRules = draft.repeatRule == .custom
            || (editing != nil && span == .thisEvent && event.hasRecurrenceRules)
        if !keepsRules { event.recurrenceRules = Self.recurrenceRules(for: draft) }
        try store.save(event, span: span, commit: true)
        return calendar.calendarIdentifier
    }

    /// Deletes an occurrence (or the series from it, per `span`) of EchoDiar's calendar.
    func deleteEvent(_ event: CalendarEvent, span: EKSpan, storedCalendarID: String?) throws {
        let (_, occurrence) = try ownOccurrence(event, storedCalendarID: storedCalendarID)
        try store.remove(occurrence, span: span, commit: true)
    }

    /// The form's values for an occurrence of EchoDiar's calendar, and whether it repeats; nil when it is not found.
    func draft(for event: CalendarEvent, storedCalendarID: String?) -> (draft: CalendarEventDraft, isRecurring: Bool)? {
        guard let (_, occurrence) = try? ownOccurrence(event, storedCalendarID: storedCalendarID) else { return nil }
        let (repeatRule, repeatEnd) = Self.repeatRule(of: occurrence)
        let draft = CalendarEventDraft(
            title: occurrence.title ?? "", start: occurrence.startDate, end: occurrence.endDate,
            urlText: occurrence.url?.absoluteString ?? "", repeatRule: repeatRule, repeatEnd: repeatEnd)
        return (draft, occurrence.hasRecurrenceRules)
    }

    private static let ownCalendarTitle = "EchoDiar"

    /// EchoDiar's calendar if it exists and is writable; never creates one. By title only on the local source, which
    /// can't be shared: a shared calendar titled "EchoDiar" would let its owner add events that count as the user's.
    private func existingOwnCalendar(storedID: String?) -> EKCalendar? {
        if let storedID, let calendar = store.calendar(withIdentifier: storedID), calendar.allowsContentModifications {
            return calendar
        }
        return store.calendars(for: .event)
            .first {
                $0.title == Self.ownCalendarTitle && $0.source?.sourceType == .local && $0.allowsContentModifications
            }
    }

    /// The `event` occurrence refetched from the current store; throws unless it is in EchoDiar's calendar.
    private func ownOccurrence(_ event: CalendarEvent, storedCalendarID: String?) throws -> (EKCalendar, EKEvent) {
        guard isAuthorized else { throw CalendarStoreError.noAccess }
        guard let calendar = existingOwnCalendar(storedID: storedCalendarID),
              event.calendarID == calendar.calendarIdentifier
        else { throw CalendarStoreError.notOwnEvent }
        guard let occurrence = occurrence(for: event, in: calendar) else { throw CalendarStoreError.eventNotFound }
        return (calendar, occurrence)
    }

    private func occurrence(for event: CalendarEvent, in calendar: EKCalendar) -> EKEvent? {
        let predicate = store.predicateForEvents(
            withStart: event.start - 1, end: event.start + 1, calendars: [calendar])
        return store.events(matching: predicate).first { occurrenceID($0) == event.id }
    }

    /// The rules for the form's Repeat and End; nil for `.none`. The series ends at the end of `repeatEnd`'s day.
    private static func recurrenceRules(for draft: CalendarEventDraft) -> [EKRecurrenceRule]? {
        let calendar = Calendar.current
        let end = draft.repeatEnd.flatMap { day in
            calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: day))
                .map { EKRecurrenceEnd(end: $0 - 1) }
        }
        switch draft.repeatRule {
        case .none, .custom:
            return nil
        case .daily:
            return [EKRecurrenceRule(recurrenceWith: .daily, interval: 1, end: end)]
        case .weekly:
            return [EKRecurrenceRule(recurrenceWith: .weekly, interval: 1, end: end)]
        case .weekdays:
            let days = Self.weekdays.map { EKRecurrenceDayOfWeek(dayOfTheWeek: $0, weekNumber: 0) }
            return [
                EKRecurrenceRule(
                    recurrenceWith: .weekly, interval: 1, daysOfTheWeek: days, daysOfTheMonth: nil,
                    monthsOfTheYear: nil, weeksOfTheYear: nil, daysOfTheYear: nil, setPositions: nil, end: end)
            ]
        case .monthly:
            let day = calendar.component(.day, from: draft.start)
            return [
                EKRecurrenceRule(
                    recurrenceWith: .monthly, interval: 1, daysOfTheWeek: nil, daysOfTheMonth: [NSNumber(value: day)],
                    monthsOfTheYear: nil, weeksOfTheYear: nil, daysOfTheYear: nil, setPositions: nil, end: end)
            ]
        }
    }

    private static let weekdays: [EKWeekday] = [.monday, .tuesday, .wednesday, .thursday, .friday]

    /// The form's Repeat and End for an event's rules; `.custom` for anything the form can't show.
    private static func repeatRule(of event: EKEvent) -> (CalendarEventDraft.Repeat, Date?) {
        guard let rules = event.recurrenceRules, !rules.isEmpty else { return (.none, nil) }
        guard rules.count == 1, let rule = rules.first, rule.interval == 1,
              rule.recurrenceEnd.map({ $0.endDate != nil }) ?? true,
              rule.monthsOfTheYear?.isEmpty ?? true, rule.weeksOfTheYear?.isEmpty ?? true,
              rule.daysOfTheYear?.isEmpty ?? true, rule.setPositions?.isEmpty ?? true
        else { return (.custom, nil) }
        let end = rule.recurrenceEnd?.endDate
        let days = rule.daysOfTheWeek ?? []
        let monthDays = rule.daysOfTheMonth ?? []
        let calendar = Calendar.current
        switch rule.frequency {
        case .daily where days.isEmpty && monthDays.isEmpty:
            return (.daily, end)
        case .weekly where monthDays.isEmpty && days.allSatisfy({ $0.weekNumber == 0 }):
            let weekdays = Set(days.map(\.dayOfTheWeek.rawValue))
            if days.count == Self.weekdays.count, weekdays == Set(Self.weekdays.map(\.rawValue)) {
                return (.weekdays, end)
            }
            if days.isEmpty || weekdays == [calendar.component(.weekday, from: event.startDate)] {
                return (.weekly, end)
            }
            return (.custom, nil)
        case .monthly where days.isEmpty
            && (monthDays.isEmpty || monthDays.map(\.intValue) == [calendar.component(.day, from: event.startDate)]):
            return (.monthly, end)
        default:
            return (.custom, nil)
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

/// Why a write to EchoDiar's calendar failed.
enum CalendarStoreError: LocalizedError {
    case noAccess, noWritableSource, eventNotFound, notOwnEvent

    var errorDescription: String? {
        switch self {
        case .noAccess: String(localized: "EchoDiar has no access to your calendars.")
        case .noWritableSource: String(localized: "No calendar account can hold EchoDiar's calendar.")
        case .eventNotFound: String(localized: "The event no longer exists.")
        case .notOwnEvent: String(localized: "Only events in the EchoDiar calendar can be changed.")
        }
    }
}
