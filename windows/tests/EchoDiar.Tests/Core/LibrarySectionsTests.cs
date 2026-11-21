using EchoDiar.Core;
using EchoDiar.Core.Model;

namespace EchoDiar.Tests.Core;

// Guards the library sidebar: which section a conversation lands in (local calendar days, not 24-hour spans, not UTC
// days) and the page math of the paged list.
public sealed class LibrarySectionsTests
{
    // UTC+7 with no DST: 01:00 local on 20 Nov is still 19 Nov in UTC, so a UTC-day count would bucket wrongly.
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("Test+07", TimeSpan.FromHours(7), "Test+07", "Test+07");
    private static readonly DateTimeOffset Now = Local(2026, 11, 20, 1, 0);

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(7)).ToUniversalTime();

    private static Conversation Make(string id, DateTimeOffset createdAt) =>
        new() { Id = Guid.Parse(id), CreatedAt = createdAt };

    [Fact]
    public void SectionsBucketByLocalDayNewestFirst()
    {
        var future = Make("00000000-0000-0000-0000-000000000001", Local(2026, 11, 21, 9, 0));
        var midnightB = Make("b0000000-0000-0000-0000-000000000000", Local(2026, 11, 20, 0, 0));
        var midnightA = Make("a0000000-0000-0000-0000-000000000000", Local(2026, 11, 20, 0, 0));
        var day1 = Make("00000000-0000-0000-0000-000000000003", Local(2026, 11, 19, 23, 59));
        var day3 = Make("00000000-0000-0000-0000-000000000004", Local(2026, 11, 17, 0, 0));
        var day4 = Make("00000000-0000-0000-0000-000000000005", Local(2026, 11, 16, 23, 0));
        var day7 = Make("00000000-0000-0000-0000-000000000006", Local(2026, 11, 13, 12, 0));
        var day8 = Make("00000000-0000-0000-0000-000000000007", Local(2026, 11, 12, 12, 0));
        var day30 = Make("00000000-0000-0000-0000-000000000008", Local(2026, 10, 21, 12, 0));
        var day31 = Make("00000000-0000-0000-0000-000000000009", Local(2026, 10, 20, 23, 59));

        var sections = LibrarySections.Sections(
            [day31, day4, midnightB, day8, day1, future, day30, midnightA, day7, day3], Now, Zone);

        Assert.Equal(
            [LibraryBucket.Today, LibraryBucket.Previous3Days, LibraryBucket.Last7Days, LibraryBucket.Last30Days, LibraryBucket.Older],
            sections.Select(s => s.Bucket));
        Assert.Equal([future, midnightA, midnightB], sections[0].Conversations);
        Assert.Equal([day1, day3], sections[1].Conversations);
        Assert.Equal([day4, day7], sections[2].Conversations);
        Assert.Equal([day8, day30], sections[3].Conversations);
        Assert.Equal([day31], sections[4].Conversations);
        Assert.Equal([LibraryBucket.Older], LibrarySections.Sections([day31], Now, Zone).Select(s => s.Bucket));
    }

    [Fact]
    public void PagingCountsClampsAndSlices()
    {
        Assert.Equal(8, LibraryPaging.PageCount(742, 100));
        Assert.Equal((700, 42), LibraryPaging.Range(7, 742, 100));
        Assert.Equal(6, LibraryPaging.Clamp(7, 700, 100));
        Assert.Equal(2, LibraryPaging.PageContaining(250, 100));
        Assert.Equal(1, LibraryPaging.PageCount(0, 100));
        Assert.Equal((0, 0), LibraryPaging.Range(0, 0, 100));
    }
}
