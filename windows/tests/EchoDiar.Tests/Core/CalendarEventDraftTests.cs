using EchoDiar.Core.Calendar;

namespace EchoDiar.Tests.Core;

// Port of macOS CalendarEventDraftTests: guards event form validation. The meeting link is user input, so only empty
// or https with a host may be saved. Also covers the new-event defaults and the inclusive series end, which macOS
// keeps in EventEditorSheet and CalendarStore. A fixed +02:00 zone keeps the day math independent of the machine.
public sealed class CalendarEventDraftTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test+02", "Test+02");

    [Theory]
    // Empty link and an https link are fine.
    [InlineData("", 1800, true)]
    [InlineData("https://zoom.us/j/1", 1800, true)]
    // Plain http and a script URL are refused.
    [InlineData("http://zoom.us/j/1", 1800, false)]
    [InlineData("javascript:alert(1)", 1800, false)]
    // End must come after start.
    [InlineData("", 0, false)]
    public void IsValid(string urlText, int durationSeconds, bool expected)
    {
        var draft = new CalendarEventDraft("Standup", Start, Start.AddSeconds(durationSeconds), urlText);

        Assert.Equal(expected, draft.IsValidIn(Zone));
    }

    // A new event starts at the next :00 or :30 (seconds dropped) and lasts 30 minutes, kept on the start's day.
    [Theory]
    [InlineData("2027-01-15T10:07:42+02:00", "2027-01-15T10:30:00+02:00", "2027-01-15T11:00:00+02:00")]
    [InlineData("2027-01-15T10:30:15+02:00", "2027-01-15T11:00:00+02:00", "2027-01-15T11:30:00+02:00")]
    [InlineData("2027-01-15T23:20:00+02:00", "2027-01-15T23:30:00+02:00", "2027-01-15T23:59:00+02:00")]
    public void NewDraftStartsAtTheNextHalfHour(string now, string start, string end)
    {
        var draft = CalendarEventDraft.New(DateTimeOffset.Parse(now), Zone);

        Assert.Equal("", draft.Title);
        Assert.Equal(DateTimeOffset.Parse(start), draft.Start);
        Assert.Equal(DateTimeOffset.Parse(end), draft.End);
        Assert.Equal(CalendarEventDraft.Repeat.None, draft.RepeatRule);
        Assert.Null(draft.RepeatEnd);
    }

    // The series end is a day, inclusive: a date at midnight of the start's day is valid and the rule ends at the end
    // of that day; the day before is refused.
    [Fact]
    public void RepeatEndIsInclusiveOfItsDay()
    {
        var start = DateTimeOffset.Parse("2027-01-15T10:00:00+02:00");
        var draft = new CalendarEventDraft(
            "Standup", start, start.AddMinutes(30), repeatRule: CalendarEventDraft.Repeat.Daily,
            repeatEnd: DateTimeOffset.Parse("2027-01-15T00:00:00+02:00"));

        Assert.True(draft.IsValidIn(Zone));
        Assert.Equal(DateTimeOffset.Parse("2027-01-15T23:59:59+02:00"), draft.RepeatUntilIn(Zone));

        draft.RepeatEnd = DateTimeOffset.Parse("2027-01-14T23:59:00+02:00");
        Assert.False(draft.IsValidIn(Zone));
    }
}
