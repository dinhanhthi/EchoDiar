using EchoDiar.Core.Model;

namespace EchoDiar.Core;

/// <summary>A library sidebar section, in display order (macOS LibrarySections.swift).</summary>
public enum LibraryBucket
{
    /// <summary>Today, and dates after today.</summary>
    Today,
    /// <summary>1 to 3 days before today.</summary>
    Previous3Days,
    /// <summary>4 to 7 days before today.</summary>
    Last7Days,
    /// <summary>8 to 30 days before today.</summary>
    Last30Days,
    /// <summary>More than 30 days before today.</summary>
    Older,
}

/// <summary>The conversations of one sidebar section, newest first (macOS LibrarySections.swift).</summary>
public sealed record LibrarySection(LibraryBucket Bucket, IReadOnlyList<Conversation> Conversations);

/// <summary>The library sidebar's sort order and sections (macOS LibrarySections.swift).</summary>
public static class LibrarySections
{
    /// <summary>
    /// Newest <see cref="Conversation.CreatedAt"/> first; ties by the id string, ascending and ordinal. Swift compares
    /// <c>id.uuidString</c> (uppercase) and this the lowercase <c>"D"</c> form: the order is the same, as <c>-</c> sorts
    /// before digits and digits before letters in either case.
    /// </summary>
    public static IReadOnlyList<Conversation> Sorted(IEnumerable<Conversation> conversations) =>
        [.. conversations.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id.ToString("D"), StringComparer.Ordinal)];

    /// <summary>
    /// The non-empty sections in <see cref="LibraryBucket"/> order, each <see cref="Sorted"/>. A conversation's bucket
    /// is d, the calendar days from its local day to <paramref name="now"/>'s local day in <paramref name="zone"/>:
    /// d ≤ 0 (also a future date) Today, 1–3 Previous3Days, 4–7 Last7Days, 8–30 Last30Days, more Older.
    /// </summary>
    public static IReadOnlyList<LibrarySection> Sections(IEnumerable<Conversation> conversations, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        return [.. Sorted(conversations)
            .GroupBy(c => Bucket((today - TimeZoneInfo.ConvertTime(c.CreatedAt, zone).Date).Days))
            .OrderBy(group => group.Key)
            .Select(group => new LibrarySection(group.Key, [.. group]))];
    }

    private static LibraryBucket Bucket(int days) => days switch
    {
        <= 0 => LibraryBucket.Today,
        <= 3 => LibraryBucket.Previous3Days,
        <= 7 => LibraryBucket.Last7Days,
        <= 30 => LibraryBucket.Last30Days,
        _ => LibraryBucket.Older,
    };
}

/// <summary>Pages of the library sidebar; pages are 0-based (macOS LibrarySections.swift).</summary>
public static class LibraryPaging
{
    /// <summary>The entries-per-page choices.</summary>
    public static readonly IReadOnlyList<int> SizeOptions = [50, 100, 200, 500];

    /// <summary>Pages needed for <paramref name="total"/> entries; at least 1, so an empty library has one empty page.</summary>
    public static int PageCount(int total, int size) => Math.Max(1, (total + Size(size) - 1) / Size(size));

    /// <summary><paramref name="page"/> limited to the existing pages.</summary>
    public static int Clamp(int page, int total, int size) => Math.Clamp(page, 0, PageCount(total, size) - 1);

    /// <summary>The page that holds the entry at <paramref name="index"/>.</summary>
    public static int PageContaining(int index, int size) => Math.Max(0, index) / Size(size);

    /// <summary>
    /// The entries of <paramref name="page"/>: first index and count. A page outside the existing ones is
    /// <see cref="Clamp"/>ed first, so the count is never negative.
    /// </summary>
    public static (int Start, int Count) Range(int page, int total, int size)
    {
        var start = Clamp(page, total, size) * Size(size);
        return (start, Math.Max(0, Math.Min(Size(size), total - start)));
    }

    // A size below 1 would divide by zero.
    private static int Size(int size) => Math.Max(1, size);
}
