using EchoDiar.Core.Calendar;
using EchoDiar.Platform.Calendar;
using Windows.ApplicationModel.Appointments;
using Repeat = EchoDiar.Core.Calendar.CalendarEventDraft.Repeat;

namespace EchoDiar.Platform.Tests;

// The pure parts of AppointmentCalendarStore: invite titles as display text (security: untrusted), occurrence ids and
// the form's Repeat against an appointment recurrence. No Appointments API call; that is checked in the VM.
public sealed class AppointmentMappingTests
{
    // UTC+10 without DST: 2026-10-25 20:00 UTC is Monday 26 October 06:00 there (Sunday in UTC).
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test+10", "Test+10");

    private static readonly DateTimeOffset Start = new(2026, 10, 25, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "")]
    [InlineData("  \t\r\n ", "")]
    [InlineData("  Weekly\tsync \r\n with\u2028team  ", "Weekly sync with team")]
    [InlineData("Pay\u202Egnp.exe", "Paygnp.exe")] // right-to-left override
    [InlineData("a\u202Ab\u202Bc\u202Cd\u202De", "abcde")] // embeddings, pop, left-to-right override
    [InlineData("a\u2066b\u2067c\u2068d\u2069e", "abcde")] // isolates
    [InlineData("a\u200Eb\u200Fc\u061Cd\uFEFFe\u00ADf", "abcdef")] // marks, BOM, soft hyphen
    [InlineData("a\u0000b\u0007c\u001Bd\u007Fe\u0085f", "a b c d e f")] // C0, DEL and C1 controls
    [InlineData("Team \U0001F468\u200D\U0001F469\u200D\U0001F467 call", "Team \U0001F468\u200D\U0001F469\u200D\U0001F467 call")] // joiners kept
    [InlineData("\u0646\u200C\u0645", "\u0646\u200C\u0645")] // ZWNJ kept
    public void TitlesBecomeOneLineWithoutControlOrFormatCharacters(string? title, string expected) =>
        Assert.Equal(expected, AppointmentMapping.DisplayTitle(title));

    [Fact]
    public void TitlesAreCutAt200CharactersWithoutSplittingOne()
    {
        Assert.Equal(new string('a', 200), AppointmentMapping.DisplayTitle(new string('a', 500)));

        // 199 letters then emoji (two UTF-16 units each): the 200th character is a whole emoji.
        var cut = AppointmentMapping.DisplayTitle(new string('a', 199) + string.Concat(Enumerable.Repeat("\U0001F600", 5)));
        Assert.Equal(new string('a', 199) + "\U0001F600", cut);

        // A family emoji is one character, kept whole or not at all.
        var family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        Assert.Equal(new string('a', 199) + family, AppointmentMapping.DisplayTitle(new string('a', 199) + family + family));
    }

    [Fact]
    public void AnOccurrenceIdRoundTripsAndIgnoresTheOffset()
    {
        var local = new DateTimeOffset(2026, 11, 1, 21, 52, 0, TimeSpan.FromHours(1));
        var id = AppointmentMapping.OccurrenceId("c,7,1", local);

        Assert.Equal("c,7,1|2026-11-01T20:52:00.0000000+00:00", id);
        Assert.Equal(id, AppointmentMapping.OccurrenceId("c,7,1", local.ToUniversalTime()));
        Assert.True(AppointmentMapping.TryParseOccurrenceId(id, out var localId, out var start));
        Assert.Equal(("c,7,1", local), (localId, start));

        // The last '|' splits, so an id holding one still round-trips.
        Assert.True(AppointmentMapping.TryParseOccurrenceId(AppointmentMapping.OccurrenceId("a|b", local), out localId, out _));
        Assert.Equal("a|b", localId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("c,7,1")]
    [InlineData("|2026-11-01T20:52:00.0000000+00:00")]
    [InlineData("c,7,1|yesterday")]
    public void AMalformedOccurrenceIdIsRefused(string id) =>
        Assert.False(AppointmentMapping.TryParseOccurrenceId(id, out _, out _));

    [Theory]
    [InlineData(Repeat.Daily, AppointmentRecurrenceUnit.Daily, AppointmentDaysOfWeek.None, 0u)]
    [InlineData(Repeat.Weekdays, AppointmentRecurrenceUnit.Weekly,
        AppointmentDaysOfWeek.Monday | AppointmentDaysOfWeek.Tuesday | AppointmentDaysOfWeek.Wednesday
        | AppointmentDaysOfWeek.Thursday | AppointmentDaysOfWeek.Friday, 0u)]
    [InlineData(Repeat.Weekly, AppointmentRecurrenceUnit.Weekly, AppointmentDaysOfWeek.Monday, 0u)] // the zone's weekday
    [InlineData(Repeat.Monthly, AppointmentRecurrenceUnit.Monthly, AppointmentDaysOfWeek.None, 26u)] // the zone's day
    public void EachRepeatMapsToARecurrenceAndBack(Repeat repeat, AppointmentRecurrenceUnit unit, AppointmentDaysOfWeek days, uint day)
    {
        var repeatEnd = new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.FromHours(10));
        var draft = new CalendarEventDraft("Sync", Start, Start.AddMinutes(30), repeatRule: repeat, repeatEnd: repeatEnd);

        var rule = AppointmentMapping.RuleFor(draft, Zone);

        // The series ends at the last second of the End day in the zone.
        var until = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(10));
        Assert.Equal(new AppointmentMapping.RecurrenceRule(unit, 1, days, day, null, until), rule);
        Assert.Equal((repeat, (DateTimeOffset?)until), AppointmentMapping.RepeatOf(rule, Start, Zone));
    }

    [Theory]
    [InlineData(Repeat.None)]
    [InlineData(Repeat.Custom)]
    public void NoneAndCustomMakeNoRecurrence(Repeat repeat) =>
        Assert.Null(AppointmentMapping.RuleFor(new CalendarEventDraft("Sync", Start, Start.AddMinutes(30), repeatRule: repeat), Zone));

    [Fact]
    public void NoRecurrenceReadsAsNoneAndARuleWithoutEndNeverEnds()
    {
        Assert.Equal((Repeat.None, (DateTimeOffset?)null), AppointmentMapping.RepeatOf(null, Start, Zone));
        var daily = new AppointmentMapping.RecurrenceRule(AppointmentRecurrenceUnit.Daily, 1, AppointmentDaysOfWeek.None, 0, null, null);
        Assert.Equal((Repeat.Daily, (DateTimeOffset?)null), AppointmentMapping.RepeatOf(daily, Start, Zone));
    }

    [Theory]
    [InlineData(AppointmentRecurrenceUnit.Daily, 2u, AppointmentDaysOfWeek.None, 0u, null)] // every other day
    [InlineData(AppointmentRecurrenceUnit.Weekly, 1u, AppointmentDaysOfWeek.Monday, 0u, 3u)] // a count end
    [InlineData(AppointmentRecurrenceUnit.Weekly, 1u, AppointmentDaysOfWeek.Monday | AppointmentDaysOfWeek.Wednesday, 0u, null)]
    [InlineData(AppointmentRecurrenceUnit.Weekly, 1u, AppointmentDaysOfWeek.Sunday, 0u, null)] // not the zone's weekday
    [InlineData(AppointmentRecurrenceUnit.Monthly, 1u, AppointmentDaysOfWeek.None, 25u, null)] // not the zone's day
    [InlineData(AppointmentRecurrenceUnit.MonthlyOnDay, 1u, AppointmentDaysOfWeek.Monday, 0u, null)]
    [InlineData(AppointmentRecurrenceUnit.Yearly, 1u, AppointmentDaysOfWeek.None, 26u, null)]
    public void ARuleTheFormCantShowReadsAsCustomWithoutEnd(
        AppointmentRecurrenceUnit unit, uint interval, AppointmentDaysOfWeek days, uint day, uint? occurrences)
    {
        var rule = new AppointmentMapping.RecurrenceRule(unit, interval, days, day, occurrences, Start.AddDays(30));
        Assert.Equal((Repeat.Custom, (DateTimeOffset?)null), AppointmentMapping.RepeatOf(rule, Start, Zone));
    }

    [Fact]
    public void ASplitEndsTheOldSeriesAtTheLastSecondOfTheDayBefore() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 23, 59, 59, TimeSpan.FromHours(10)), AppointmentMapping.EndOfDayBefore(Start, Zone));
}
