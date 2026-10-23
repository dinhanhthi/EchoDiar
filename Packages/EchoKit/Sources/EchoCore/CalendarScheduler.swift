import Foundation

/// One occurrence of a calendar event, copied out of the calendar store.
public struct CalendarEvent: Sendable, Hashable, Identifiable {
    /// Occurrence id: the event id plus its start, so each occurrence of a recurring event is distinct.
    public let id: String
    public let title: String
    public let start: Date
    public let end: Date
    public let isAllDay: Bool
    public let calendarID: String
    public let link: MeetingLink?
    /// The current user declined the invitation.
    public let declined: Bool
    public let canceled: Bool
    /// The current user organizes the event (or it has no attendees) or accepted it, tentatively or not; only then
    /// may it start a recording without asking.
    public let confirmed: Bool

    public init(
        id: String, title: String, start: Date, end: Date, isAllDay: Bool, calendarID: String, link: MeetingLink?,
        declined: Bool, canceled: Bool, confirmed: Bool
    ) {
        self.id = id
        self.title = title
        self.start = start
        self.end = end
        self.isAllDay = isAllDay
        self.calendarID = calendarID
        self.link = link
        self.declined = declined
        self.canceled = canceled
        self.confirmed = confirmed
    }
}

/// Decides when a calendar event should start a recording. Pure: the caller feeds events with `update` and asks
/// `evaluate(now:)` what to do; nothing here reads the clock.
///
/// Each occurrence triggers at most once, anywhere in `[start, end)`, so a launch or wake inside an event still
/// catches it. Triggered ids live in memory and survive `update`.
public struct CalendarScheduler: Sendable {
    /// When a calendar event triggers.
    public enum Mode: String, Codable, Sendable, CaseIterable {
        case off
        /// Show a prompt at the start.
        case ask
        /// Record the first meeting app detected during the event.
        case waitForCall
        case recordAtStart

        /// An unrecognized raw value (written by another build) falls back to `ask`.
        public init(from decoder: Decoder) throws {
            self = Mode(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .ask
        }
    }

    /// Calendar settings, stored in the settings file. Every key is optional when read.
    public struct Configuration: Codable, Sendable, Equatable {
        public var mode: Mode
        /// Calendars not watched; empty watches every calendar, new ones included.
        public var ignoredCalendarIDs: [String]
        /// Count timed events without a meeting link too.
        public var allTimedEvents: Bool
        /// Prompt to stop the recording once the event has ended.
        public var stopAtEventEnd: Bool
        /// Minutes after the event end before that prompt; clamped to `stopAfterEndRange` when read.
        public var stopAfterEndMinutes: Int
        /// Occurrences the user chose to skip, mapped to their end so past ones can be pruned.
        public var skippedEvents: [String: Date]

        public static let stopAfterEndRange = 0...120

        /// The value in force, clamped again at use: a configuration built in code is not clamped.
        public var effectiveStopAfterEndMinutes: Int {
            min(max(stopAfterEndMinutes, Self.stopAfterEndRange.lowerBound), Self.stopAfterEndRange.upperBound)
        }

        public init(
            mode: Mode = .ask, ignoredCalendarIDs: [String] = [], allTimedEvents: Bool = false,
            stopAtEventEnd: Bool = true, stopAfterEndMinutes: Int = 10, skippedEvents: [String: Date] = [:]
        ) {
            self.mode = mode
            self.ignoredCalendarIDs = ignoredCalendarIDs
            self.allTimedEvents = allTimedEvents
            self.stopAtEventEnd = stopAtEventEnd
            self.stopAfterEndMinutes = stopAfterEndMinutes
            self.skippedEvents = skippedEvents
        }

        private enum CodingKeys: String, CodingKey {
            case mode, ignoredCalendarIDs, allTimedEvents, stopAtEventEnd, stopAfterEndMinutes, skippedEvents
        }

        public init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            mode = try container.decodeIfPresent(Mode.self, forKey: .mode) ?? .ask
            ignoredCalendarIDs = try container.decodeIfPresent([String].self, forKey: .ignoredCalendarIDs) ?? []
            allTimedEvents = try container.decodeIfPresent(Bool.self, forKey: .allTimedEvents) ?? false
            stopAtEventEnd = try container.decodeIfPresent(Bool.self, forKey: .stopAtEventEnd) ?? true
            let minutes = try container.decodeIfPresent(Int.self, forKey: .stopAfterEndMinutes) ?? 10
            stopAfterEndMinutes = min(max(minutes, Self.stopAfterEndRange.lowerBound), Self.stopAfterEndRange.upperBound)
            skippedEvents = try container.decodeIfPresent([String: Date].self, forKey: .skippedEvents) ?? [:]
        }

        /// A copy without skipped occurrences that have ended.
        public func pruned(now: Date) -> Configuration {
            var copy = self
            copy.skippedEvents = skippedEvents.filter { $0.value > now }
            return copy
        }
    }

    public enum Action: Equatable, Sendable {
        case none
        /// Start recording for the event; `late` when more than 5 minutes past its start.
        case start(CalendarEvent, late: Bool)
    }

    /// Seconds after the start beyond which a trigger counts as late.
    private static let lateThreshold: TimeInterval = 300

    private var events: [CalendarEvent] = []
    private var configuration = Configuration()
    private var triggered: Set<String> = []

    public init() {}

    /// Replaces the events and settings, keeping only eligible events, sorted by start (then id).
    public mutating func update(_ events: [CalendarEvent], configuration: Configuration) {
        self.configuration = configuration
        let ignored = Set(configuration.ignoredCalendarIDs)
        self.events = events
            .filter {
                !$0.isAllDay && !$0.declined && !$0.canceled && !ignored.contains($0.calendarID)
                    && ($0.link != nil || configuration.allTimedEvents)
            }
            .sorted { ($0.start, $0.id) < ($1.start, $1.id) }
    }

    /// The event to start now, if any. When several are due the latest start wins; the others are marked triggered too.
    public mutating func evaluate(now: Date) -> Action {
        guard configuration.mode != .off else { return .none }
        let due = events.filter {
            $0.start <= now && now < $0.end && !triggered.contains($0.id) && configuration.skippedEvents[$0.id] == nil
        }
        guard let event = due.last else { return .none }
        triggered.formUnion(due.map(\.id))
        return .start(event, late: now.timeIntervalSince(event.start) > Self.lateThreshold)
    }

    /// The eligible event in progress at `now` (the latest started), whatever the mode.
    public func activeEvent(at now: Date) -> CalendarEvent? {
        events.last { $0.start <= now && now < $0.end }
    }

    /// Eligible events not yet over, by start, whatever the mode or skip state.
    public func upcoming(after now: Date, limit: Int) -> [CalendarEvent] {
        Array(events.filter { $0.end > now }.prefix(limit))
    }

    /// The next event start or end strictly after `now`, when `evaluate` or the lists may change.
    public func nextChange(after now: Date) -> Date? {
        events.flatMap { [$0.start, $0.end] }.filter { $0 > now }.min()
    }
}
