import Foundation

/// A section of the library sidebar, by how many local days ago a conversation started.
public enum LibraryBucket: CaseIterable, Sendable {
    /// Today, and dates after today.
    case today
    /// 1 to 3 days ago.
    case previous3Days
    /// 4 to 7 days ago.
    case last7Days
    /// 8 to 30 days ago.
    case last30Days
    /// More than 30 days ago.
    case older

    /// The bucket for `days`, the number of calendar days between the start of the conversation's day and the start
    /// of today; 0 or less is today.
    init(days: Int) {
        switch days {
        case ...0: self = .today
        case 1...3: self = .previous3Days
        case 4...7: self = .last7Days
        case 8...30: self = .last30Days
        default: self = .older
        }
    }
}

/// The conversations of one non-empty bucket, sorted as `LibrarySections.sorted`.
public struct LibrarySection: Equatable, Identifiable, Sendable {
    public let bucket: LibraryBucket
    public let conversations: [Conversation]

    public var id: LibraryBucket { bucket }

    public init(bucket: LibraryBucket, conversations: [Conversation]) {
        self.bucket = bucket
        self.conversations = conversations
    }
}

/// Splits the library into the sidebar's sections.
public enum LibrarySections {
    /// Newest `createdAt` first; equal dates by `id.uuidString`, ascending.
    public static func sorted(_ conversations: [Conversation]) -> [Conversation] {
        conversations.sorted {
            $0.createdAt != $1.createdAt ? $0.createdAt > $1.createdAt : $0.id.uuidString < $1.id.uuidString
        }
    }

    /// The non-empty sections in bucket order (today first), each sorted as `sorted`. Days are counted between the
    /// starts of the local days of `createdAt` and `now` in `calendar`.
    public static func sections(
        from conversations: [Conversation], now: Date, calendar: Calendar = .current
    ) -> [LibrarySection] {
        let today = calendar.startOfDay(for: now)
        let grouped = Dictionary(grouping: sorted(conversations)) { conversation in
            let day = calendar.startOfDay(for: conversation.createdAt)
            return LibraryBucket(days: calendar.dateComponents([.day], from: day, to: today).day ?? 0)
        }
        return LibraryBucket.allCases.compactMap { bucket in
            grouped[bucket].map { LibrarySection(bucket: bucket, conversations: $0) }
        }
    }
}

/// Page math for the paged library list; pages are 0-based.
public enum LibraryPaging {
    /// The page sizes offered in Settings.
    public static let sizeOptions = [50, 100, 200, 500]

    /// The number of pages, at least 1 (an empty list has one empty page).
    public static func pageCount(total: Int, size: Int) -> Int {
        max(1, (max(0, total) + validSize(size) - 1) / validSize(size))
    }

    /// `page` limited to the pages that exist.
    public static func clamp(page: Int, total: Int, size: Int) -> Int {
        min(max(0, page), pageCount(total: total, size: size) - 1)
    }

    /// The page that holds the item at `index`.
    public static func page(containing index: Int, size: Int) -> Int {
        max(0, index) / validSize(size)
    }

    /// The item indices of `page`, clamped first; empty when `total` is 0.
    public static func range(page: Int, total: Int, size: Int) -> Range<Int> {
        let total = max(0, total)
        let lower = min(clamp(page: page, total: total, size: size) * validSize(size), total)
        return lower..<min(lower + validSize(size), total)
    }

    /// A size read from a settings file may be 0 or negative.
    private static func validSize(_ size: Int) -> Int { max(1, size) }
}
