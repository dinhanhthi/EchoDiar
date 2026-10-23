import EchoCore
import Foundation
import Testing

/// Guards which calendar events count and that each occurrence starts a recording at most once.
struct CalendarSchedulerTests {
    private let start = Date(timeIntervalSinceReferenceDate: 0)
    private let zoom = MeetingLink.find(url: URL(string: "https://zoom.us/j/1"), location: nil, notes: nil)

    private func at(_ seconds: TimeInterval) -> Date { start.addingTimeInterval(seconds) }

    private func event(
        _ id: String, link: Bool = true, isAllDay: Bool = false, declined: Bool = false, calendarID: String = "work"
    ) -> CalendarEvent {
        CalendarEvent(
            id: id, title: id, start: start, end: at(3_600), isAllDay: isAllDay, calendarID: calendarID,
            link: link ? zoom : nil, declined: declined, canceled: false)
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

    @Test func unknownModeAndOutOfRangeMinutesDecodeSafely() throws {
        let json = Data(#"{"mode":"bogus","stopAfterEndMinutes":999}"#.utf8)
        let configuration = try JSONDecoder().decode(CalendarScheduler.Configuration.self, from: json)
        #expect(configuration.mode == .ask)
        #expect(configuration.stopAfterEndMinutes == 120)
        #expect(configuration.stopAtEventEnd)
    }
}
