namespace EchoDiar.Core.Calendar;

/// <summary>Calendar access as last read.</summary>
public enum CalendarAccess { NotDetermined, Denied, Granted }

/// <summary>Which occurrences an edit or delete of a repeating event applies to.</summary>
public enum CalendarSpan { ThisEvent, ThisAndFuture }

/// <summary>One calendar, for the settings list.</summary>
/// <param name="SourceTitle">The account it belongs to.</param>
/// <param name="IsWritable">EchoDiar may add events to it.</param>
public sealed record CalendarInfo(string Id, string Title, string SourceTitle, bool IsWritable);

/// <summary>
/// The user's calendars (port of the surface macOS <c>CalendarStore</c> offers the app; the Platform implements it on
/// the Appointments API). Events are copied out as <see cref="CalendarEvent"/>: titles, locations and notes come from
/// invites, so they are data only and never logged. Writes go only to EchoDiar's own calendar.
/// </summary>
public interface ICalendarStore
{
    /// <summary>Raised when the calendar database changes, on any thread.</summary>
    event Action? Changed;

    CalendarAccess Access { get; }

    /// <summary>Asks for access to the calendars; true when granted.</summary>
    Task<bool> RequestAccessAsync();

    /// <summary>Calendars sorted by source then title; empty without access.</summary>
    Task<IReadOnlyList<CalendarInfo>> CalendarsAsync();

    /// <summary>
    /// Occurrences overlapping [<paramref name="from"/>, <paramref name="to"/>) in every calendar; empty without access.
    /// <paramref name="ownCalendarId"/> is EchoDiar's calendar, whose events without invitees count as the user's in
    /// <see cref="CalendarScheduler.IsConfirmed"/>.
    /// </summary>
    Task<IReadOnlyList<CalendarEvent>> EventsAsync(DateTimeOffset from, DateTimeOffset to, string? ownCalendarId);

    /// <summary>Saves <paramref name="draft"/> as a new event, or over the <paramref name="editing"/> occurrence (per
    /// <paramref name="span"/>), in EchoDiar's calendar, creating it when missing. Returns that calendar's id to store.</summary>
    Task<string> SaveEventAsync(CalendarEventDraft draft, CalendarEvent? editing, CalendarSpan span, string? storedCalendarId);

    /// <summary>Deletes an occurrence of EchoDiar's calendar, or the series from it per <paramref name="span"/>.</summary>
    Task DeleteEventAsync(CalendarEvent @event, CalendarSpan span, string? storedCalendarId);

    /// <summary>The form's values for an occurrence of EchoDiar's calendar and whether it repeats; null when it is not
    /// found there.</summary>
    Task<(CalendarEventDraft Draft, bool IsRecurring)?> DraftAsync(CalendarEvent @event, string? storedCalendarId);
}
