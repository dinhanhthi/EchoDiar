import EchoCore
import Foundation
import Testing

/// Guards the library sidebar: sections by calendar-day distance from today in the given calendar (not elapsed
/// hours), fixed bucket order, newest first with ties by id; and the page math of the paged list.
struct LibrarySectionsTests {
    @Test func sectionsBucketByCalendarDaysInTheGivenCalendar() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try #require(TimeZone(identifier: "Pacific/Auckland"))
        func date(_ month: Int, _ day: Int, _ hour: Int, _ minute: Int = 0) throws -> Date {
            let components = DateComponents(year: 2026, month: month, day: day, hour: hour, minute: minute)
            return try #require(calendar.date(from: components))
        }
        func conversation(_ createdAt: Date, id: UUID = UUID()) -> Conversation {
            Conversation(id: id, createdAt: createdAt, folder: URL(fileURLWithPath: "/tmp/\(id.uuidString)"))
        }
        let now = try date(11, 20, 22)
        let future = conversation(try date(11, 21, 9))
        let d0 = conversation(try date(11, 20, 0, 30))
        let d1 = conversation(try date(11, 19, 23, 50))
        let d3 = conversation(try date(11, 17, 0, 10))  // more than 72 h ago, still 3 days
        let d4 = conversation(try date(11, 16, 23, 50))  // less than 96 h ago, already 4 days
        let d7 = conversation(try date(11, 13, 12))
        let d8 = conversation(try date(11, 12, 23, 50))
        let d30 = conversation(try date(10, 21, 0, 10))
        let d31 = conversation(try date(10, 20, 23, 50))  // less than 31 × 24 h ago
        let idA = try #require(UUID(uuidString: "00000000-0000-0000-0000-00000000000A"))
        let idB = try #require(UUID(uuidString: "00000000-0000-0000-0000-00000000000B"))
        let tieA = conversation(d31.createdAt, id: idA)
        let tieB = conversation(d31.createdAt, id: idB)

        let sections = LibrarySections.sections(
            from: [d31, tieB, d4, d0, d30, d1, future, d8, d3, tieA, d7], now: now, calendar: calendar)

        #expect(sections.map(\.bucket) == [.today, .previous3Days, .last7Days, .last30Days, .older])
        // tieA, tieB and d31 share one date, so they are ordered by id string, ascending.
        let older = [tieA, tieB, d31].map(\.id).sorted { $0.uuidString < $1.uuidString }
        #expect(sections.map { $0.conversations.map(\.id) } == [
            [future.id, d0.id], [d1.id, d3.id], [d4.id, d7.id], [d8.id, d30.id], older,
        ])
    }

    @Test func pagingCountsClampsAndSlicesPages() {
        #expect(LibraryPaging.pageCount(total: 742, size: 100) == 8)
        #expect(LibraryPaging.range(page: 7, total: 742, size: 100) == 700..<742)
        #expect(LibraryPaging.clamp(page: 7, total: 700, size: 100) == 6)
        #expect(LibraryPaging.page(containing: 250, size: 100) == 2)
        #expect(LibraryPaging.pageCount(total: 0, size: 100) == 1)
    }
}
