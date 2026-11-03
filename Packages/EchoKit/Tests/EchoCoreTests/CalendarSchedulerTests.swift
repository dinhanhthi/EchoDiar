import EchoCore
import Foundation
import Testing

/// Guards which calendar events count and that each occurrence starts a recording at most once.
struct CalendarSchedulerTests {
    private let start = Date(timeIntervalSinceReferenceDate: 0)
    private let zoom = MeetingLink.find(url: URL(string: "https://zoom.us/j/1"), location: nil, notes: nil)

    private func at(_ seconds: TimeInterval) -> Date { start.addingTimeInterval(seconds) }

    private func event(
        _ id: String, link: Bool = true, isAllDay: Bool = false, declined: Bool = false, calendarID: String = "work",
        from: TimeInterval = 0, to: TimeInterval = 3_600
    ) -> CalendarEvent {
        CalendarEvent(
            id: id, title: id, start: at(from), end: at(to), isAllDay: isAllDay, calendarID: calendarID,
            link: link ? zoom : nil, declined: declined, canceled: false, confirmed: true)
    }

    @Test func onlyEligibleEventsAreKept() {
        let events = [
            event("call"), event("allDay", isAllDay: true), event("declined", declined: true),
            event("noLink", link: false), event("ignored", calendarID: "home"),
        ]
        var scheduler = CalendarScheduler()
        scheduler.update(events, configuration: .init(ignoredCalendarIDs: ["home"]))
        #expect(scheduler.upcoming(after: start, limit: 10).map(\.id) == ["call"])

        scheduler.update(events, configuration: .init(ignoredCalendarIDs: ["home"], allTimedEvents: true))
        #expect(scheduler.upcoming(after: start, limit: 10).map(\.id) == ["call", "noLink"])
    }

    @Test func ownCalendarEventsCountWithoutALinkUnlessIgnored() {
        let events = [event("own", link: false, calendarID: "echo")]
        var scheduler = CalendarScheduler()
        scheduler.update(events, configuration: .init(ownCalendarID: "echo"))
        #expect(scheduler.upcoming(after: start, limit: 10).map(\.id) == ["own"])
        #expect(scheduler.evaluate(now: start) == .start(events[0], late: false))

        scheduler.update(events, configuration: .init(ignoredCalendarIDs: ["echo"], ownCalendarID: "echo"))
        #expect(scheduler.upcoming(after: start, limit: 10).isEmpty)
    }

    @Test func anEventTriggersOnceAtItsStart() {
        let events = [event("call")]
        var scheduler = CalendarScheduler()
        scheduler.update(events, configuration: .init())
        #expect(scheduler.evaluate(now: at(-1)) == CalendarScheduler.Action.none)
        #expect(scheduler.evaluate(now: start) == .start(events[0], late: false))
        scheduler.update(events, configuration: .init())
        #expect(scheduler.evaluate(now: at(1)) == CalendarScheduler.Action.none)
    }

    @Test func skippedEventsAndModeOffNeverTrigger() {
        let events = [event("call")]
        var scheduler = CalendarScheduler()
        scheduler.update(events, configuration: .init(skippedEvents: ["call": at(3_600)]))
        #expect(scheduler.evaluate(now: start) == CalendarScheduler.Action.none)
        scheduler.update(events, configuration: .init(mode: .off))
        #expect(scheduler.evaluate(now: start) == CalendarScheduler.Action.none)
    }

    @Test func aLateStartIsFlagged() {
        let events = [event("call")]
        var scheduler = CalendarScheduler()
        scheduler.update(events, configuration: .init())
        #expect(scheduler.evaluate(now: at(301)) == .start(events[0], late: true))
    }

    /// "a" [0, 3600) and "b" [1800, 5400) overlap; "c" starts at 7200. Arguments: now, recording start, a dismissed
    /// id, the expected match.
    @Test(arguments: [
        (2_000.0, 1_900.0, nil, "b"),  // Overlap: the start closest to the recording start wins.
        (2_000.0, 100.0, nil, "a"),
        (2_000.0, 900.0, nil, "b"),  // Same distance: the later start wins.
        (2_000.0, 1_900.0, "b", "a"),  // A dismissed event is skipped.
        (6_960.0, 6_960.0, nil, "c"),  // Starts within 5 minutes.
        (6_000.0, 6_000.0, nil, nil),  // Nothing in progress, the next start is 20 minutes away.
        (6_960.0, 6_960.0, "c", nil),
    ] as [(TimeInterval, TimeInterval, String?, String?)])
    func recordingMatchPicksTheEventClosestToTheRecordingStart(
        now: TimeInterval, startedAt: TimeInterval, dismissed: String?, expected: String?
    ) {
        var scheduler = CalendarScheduler()
        scheduler.update(
            [event("a", from: 0, to: 3_600), event("b", from: 1_800, to: 5_400), event("c", from: 7_200, to: 9_000)],
            configuration: .init())
        let match = scheduler.recordingMatch(
            startedAt: at(startedAt), now: at(now), excluding: dismissed.map { [$0] } ?? [])
        #expect(match?.id == expected)
    }

    @Test func unknownModeAndOutOfRangeMinutesDecodeSafely() throws {
        let json = Data(#"{"mode":"bogus","stopAfterEndMinutes":999}"#.utf8)
        let configuration = try JSONDecoder().decode(CalendarScheduler.Configuration.self, from: json)
        #expect(configuration.mode == .ask)
        #expect(configuration.stopAfterEndMinutes == 120)
        #expect(configuration.stopAtEventEnd)
        #expect(CalendarScheduler.Configuration(stopAfterEndMinutes: 999).effectiveStopAfterEndMinutes == 120)
    }
}
