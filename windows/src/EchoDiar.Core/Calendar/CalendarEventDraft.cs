namespace EchoDiar.Core.Calendar;

/// <summary>
/// The event form's values, before they are saved to EchoDiar's own calendar (port of Swift <c>CalendarEventDraft</c>,
/// plus the new-event defaults and the series end of macOS <c>EventEditorSheet</c> / <c>CalendarStore</c>). Day math
/// takes the time zone as a parameter; the parameterless members use <see cref="TimeZoneInfo.Local"/>.
/// </summary>
public sealed record CalendarEventDraft
{
    /// <summary>How the event repeats.</summary>
    public enum Repeat
    {
        None, Daily, Weekdays, Weekly, Monthly,
        /// <summary>A rule read back from another app that the form can't represent; kept as-is unless changed.</summary>
        Custom,
    }

    public CalendarEventDraft(
        string title, DateTimeOffset start, DateTimeOffset end, string urlText = "", Repeat repeatRule = Repeat.None,
        DateTimeOffset? repeatEnd = null)
    {
        Title = title;
        Start = start;
        End = end;
        UrlText = urlText;
        RepeatRule = repeatRule;
        RepeatEnd = repeatEnd;
    }

    public string Title { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    /// <summary>The meeting link as typed; may be empty.</summary>
    public string UrlText { get; set; }
    public Repeat RepeatRule { get; set; }
    /// <summary>Last day of the series (inclusive); null = never ends.</summary>
    public DateTimeOffset? RepeatEnd { get; set; }

    /// <summary>A new event: untitled, at the next :00 or :30 after <paramref name="now"/>, 30 minutes long.</summary>
    public static CalendarEventDraft New(DateTimeOffset now, TimeZoneInfo zone)
    {
        var start = NextHalfHour(now, zone);
        return new CalendarEventDraft("", start, DefaultEnd(start, zone));
    }

    /// <summary>The next :00 or :30 after <paramref name="date"/> (seconds dropped).</summary>
    public static DateTimeOffset NextHalfHour(DateTimeOffset date, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(date, zone);
        var trimmed = local.AddTicks(-(local.Ticks % TimeSpan.TicksPerMinute));
        var next = trimmed.AddMinutes(local.Minute < 30 ? 30 - local.Minute : 60 - local.Minute);
        return TimeZoneInfo.ConvertTime(next, zone);
    }

    /// <summary>Start + 30 min, kept on the start's day (23:59 at the latest).</summary>
    public static DateTimeOffset DefaultEnd(DateTimeOffset start, TimeZoneInfo zone)
    {
        var end = TimeZoneInfo.ConvertTime(start.AddMinutes(30), zone);
        var startDay = TimeZoneInfo.ConvertTime(start, zone).Date;
        return end.Date == startDay ? end : AtLocal(startDay.AddHours(23).AddMinutes(59), zone);
    }

    /// <summary>The trimmed link when it is https with a host, else null. The link is user input: no other scheme is kept.</summary>
    public Uri? Url =>
        Uri.TryCreate(UrlText.Trim(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(url.Host)
            ? url
            : null;

    /// <summary><see cref="IsValidIn"/> in the local time zone.</summary>
    public bool IsValid => IsValidIn(TimeZoneInfo.Local);

    /// <summary>Whether the form can be saved: a title, end after start, an empty or valid link, and a series end not
    /// before the start day.</summary>
    public bool IsValidIn(TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(Title) || End <= Start) return false;
        if (!string.IsNullOrWhiteSpace(UrlText) && Url is null) return false;
        return RepeatEnd is not { } repeatEnd || repeatEnd >= StartOfDay(Start, zone);
    }

    /// <summary><see cref="RepeatUntilIn"/> in the local time zone.</summary>
    public DateTimeOffset? RepeatUntil => RepeatUntilIn(TimeZoneInfo.Local);

    /// <summary>When the series ends: the last second of <see cref="RepeatEnd"/>'s day; null = never.</summary>
    public DateTimeOffset? RepeatUntilIn(TimeZoneInfo zone) =>
        RepeatEnd is { } day ? AtLocal(TimeZoneInfo.ConvertTime(day, zone).Date.AddDays(1), zone).AddSeconds(-1) : null;

    private static DateTimeOffset StartOfDay(DateTimeOffset date, TimeZoneInfo zone) =>
        AtLocal(TimeZoneInfo.ConvertTime(date, zone).Date, zone);

    /// <summary>The instant of wall-clock time <paramref name="local"/> in <paramref name="zone"/>. A time skipped by a
    /// DST gap gets the standard offset, which lands on the first time after the gap (as Swift's startOfDay).</summary>
    private static DateTimeOffset AtLocal(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)), zone);
    }
}
