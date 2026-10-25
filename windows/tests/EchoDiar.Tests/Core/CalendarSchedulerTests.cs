using System.Text;
using System.Text.Json.Nodes;
using EchoDiar.Core.Calendar;
using EchoDiar.Core.Json;
using Mode = EchoDiar.Core.Calendar.CalendarScheduler.Mode;
using Configuration = EchoDiar.Core.Calendar.CalendarScheduler.Configuration;

namespace EchoDiar.Tests.Core;

// Port of macOS CalendarSchedulerTests: guards which calendar events count and that each occurrence starts a recording
// at most once; plus the trust rule (Windows plan Q3) and the macOS settings shape.
public sealed class CalendarSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly MeetingLink? Zoom = MeetingLink.Find(new Uri("https://zoom.us/j/1"), null, null, null);

    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    private static CalendarEvent Event(
        string id, bool link = true, bool isAllDay = false, bool declined = false, string calendarID = "work",
        bool confirmed = true) =>
        new(id, id, Start, At(3_600), isAllDay, calendarID, link ? Zoom : null, declined, Canceled: false, confirmed);

    private static List<string> Ids(IEnumerable<CalendarEvent> events) => events.Select(e => e.Id).ToList();

    [Fact]
    public void OnlyEligibleEventsAreKept()
    {
        CalendarEvent[] events =
        [
            Event("call"), Event("allDay", isAllDay: true), Event("declined", declined: true),
            Event("noLink", link: false), Event("ignored", calendarID: "home"),
        ];
        var scheduler = new CalendarScheduler();
        scheduler.Update(events, new Configuration { IgnoredCalendarIDs = ["home"] });
        Assert.Equal(["call"], Ids(scheduler.Upcoming(Start, 10)));

        scheduler.Update(events, new Configuration { IgnoredCalendarIDs = ["home"], AllTimedEvents = true });
        Assert.Equal(["call", "noLink"], Ids(scheduler.Upcoming(Start, 10)));
    }

    [Fact]
    public void OwnCalendarEventsCountWithoutALinkUnlessIgnored()
    {
        CalendarEvent[] events = [Event("own", link: false, calendarID: "echo")];
        var scheduler = new CalendarScheduler();
        scheduler.Update(events, new Configuration { OwnCalendarID = "echo" });
        Assert.Equal(["own"], Ids(scheduler.Upcoming(Start, 10)));
        Assert.Equal(CalendarScheduler.Action.Start(events[0], Late: false), scheduler.Evaluate(Start));

        scheduler.Update(events, new Configuration { IgnoredCalendarIDs = ["echo"], OwnCalendarID = "echo" });
        Assert.Empty(scheduler.Upcoming(Start, 10));
    }

    [Fact]
    public void AnEventTriggersOnceAtItsStart()
    {
        CalendarEvent[] events = [Event("call")];
        var scheduler = new CalendarScheduler();
        scheduler.Update(events, new Configuration());
        Assert.Equal(CalendarScheduler.Action.None, scheduler.Evaluate(At(-1)));
        Assert.Equal(CalendarScheduler.Action.Start(events[0], Late: false), scheduler.Evaluate(Start));
        scheduler.Update(events, new Configuration());
        Assert.Equal(CalendarScheduler.Action.None, scheduler.Evaluate(At(1)));
    }

    [Fact]
    public void SkippedEventsAndModeOffNeverTrigger()
    {
        CalendarEvent[] events = [Event("call")];
        var scheduler = new CalendarScheduler();
        scheduler.Update(events, new Configuration { SkippedEvents = new() { ["call"] = At(3_600) } });
        Assert.Equal(CalendarScheduler.Action.None, scheduler.Evaluate(Start));
        scheduler.Update(events, new Configuration { Mode = Mode.Off });
        Assert.Equal(CalendarScheduler.Action.None, scheduler.Evaluate(Start));
    }

    [Fact]
    public void ALateStartIsFlagged()
    {
        CalendarEvent[] events = [Event("call")];
        var scheduler = new CalendarScheduler();
        scheduler.Update(events, new Configuration());
        Assert.Equal(CalendarScheduler.Action.Start(events[0], Late: true), scheduler.Evaluate(At(301)));
    }

    [Fact]
    public void ActiveEventUpcomingAndNextChangeFollowTheClock()
    {
        var later = Event("later") with { Start = At(7_200), End = At(9_000) };
        var scheduler = new CalendarScheduler();
        scheduler.Update([later, Event("call")], new Configuration());
        Assert.Equal(At(0), scheduler.NextChange(At(-1)));
        Assert.Equal(At(3_600), scheduler.NextChange(Start));
        Assert.Equal("call", scheduler.ActiveEvent(At(10))?.Id);
        Assert.Null(scheduler.ActiveEvent(At(3_600)));
        Assert.Equal(["later"], Ids(scheduler.Upcoming(At(3_600), 10)));
        Assert.Null(scheduler.NextChange(At(9_000)));
    }

    // Trust rule (plan Q3, macOS c28c628): an invite the user has not answered never starts silently, and "no
    // invitees" confirms only in EchoDiar's own calendar (a shared calendar's owner could add events).
    [Theory]
    [InlineData(false, CalendarResponse.None, true, false, false)] // A stranger's invite.
    [InlineData(true, CalendarResponse.None, true, false, true)] // Organized by the user.
    [InlineData(false, CalendarResponse.Accepted, true, false, true)]
    [InlineData(false, CalendarResponse.None, false, true, true)] // Invitee-less, in EchoDiar's calendar.
    [InlineData(false, CalendarResponse.None, false, false, false)] // Invitee-less, on a shared calendar.
    public void IsConfirmed(bool organizer, CalendarResponse response, bool hasInvitees, bool ownCalendar, bool expected) =>
        Assert.Equal(expected, CalendarScheduler.IsConfirmed(organizer, response, hasInvitees, ownCalendar));

    // Ask always prompts; the automatic modes prompt for an unconfirmed or > 5 min late event (Windows plan: late asks in
    // Wait for the call too, where macOS only checks it at start).
    [Theory]
    [InlineData(Mode.Ask, true, false, true)]
    [InlineData(Mode.RecordAtStart, true, false, false)]
    [InlineData(Mode.RecordAtStart, false, false, true)]
    [InlineData(Mode.WaitForCall, true, true, true)]
    public void ShouldAsk(Mode mode, bool confirmed, bool late, bool expected) =>
        Assert.Equal(expected, CalendarScheduler.ShouldAsk(mode, Event("call", confirmed: confirmed), late));

    [Fact]
    public void UnknownModeAndOutOfRangeMinutesDecodeSafely()
    {
        var configuration = SwiftJson.Deserialize<Configuration>("""{"mode":"bogus","stopAfterEndMinutes":999}"""u8);
        Assert.Equal(Mode.Ask, configuration.Mode);
        Assert.Equal(120, configuration.StopAfterEndMinutes);
        Assert.True(configuration.StopAtEventEnd);
        Assert.Equal(120, new Configuration { StopAfterEndMinutes = 999 }.EffectiveStopAfterEndMinutes);
    }

    [Fact]
    public void MacOSShapedJsonRoundTrips()
    {
        const string json = """
            {"allTimedEvents":true,"ignoredCalendarIDs":["home"],"mode":"recordAtStart","ownCalendarID":"echo",
             "skippedEvents":{"a|1":780000000.5},"stopAfterEndMinutes":15,"stopAtEventEnd":false}
            """;
        var configuration = SwiftJson.Deserialize<Configuration>(Encoding.UTF8.GetBytes(json));
        Assert.Equal(Mode.RecordAtStart, configuration.Mode);
        Assert.Equal(["home"], configuration.IgnoredCalendarIDs);
        Assert.True(configuration.AllTimedEvents);
        Assert.False(configuration.StopAtEventEnd);
        Assert.Equal(15, configuration.StopAfterEndMinutes);
        Assert.Equal("echo", configuration.OwnCalendarID);
        Assert.Equal(Start.AddSeconds(780_000_000.5), configuration.SkippedEvents["a|1"]);

        var written = JsonNode.Parse(SwiftJson.Serialize(configuration))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), written), written.ToJsonString());
    }
}
