using System.Text.Json.Serialization;
using EchoDiar.Core.Json;

namespace EchoDiar.Core.Calendar;

/// <summary>
/// One occurrence of a calendar event, copied out of the calendar store (port of Swift <c>CalendarEvent</c>). Title and
/// link come from invites: untrusted data.
/// </summary>
/// <param name="Id">Occurrence id: the event id plus its start, so each occurrence of a recurring event is distinct.</param>
/// <param name="Declined">The current user declined the invitation.</param>
/// <param name="Confirmed">The rule of <see cref="CalendarScheduler.IsConfirmed"/>: only then may the event start a
/// recording without asking.</param>
public sealed record CalendarEvent(
    string Id, string Title, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, string CalendarID,
    MeetingLink? Link, bool Declined, bool Canceled, bool Confirmed);

/// <summary>The current user's answer to an invitation (Windows <c>AppointmentParticipantResponse</c>; Unknown maps to
/// <see cref="None"/>).</summary>
public enum CalendarResponse { None, Tentative, Accepted, Declined }

/// <summary>
/// Decides when a calendar event should start a recording (port of Swift <c>CalendarScheduler</c>). Pure: the caller
/// feeds events with <see cref="Update"/> and asks <see cref="Evaluate"/> what to do; nothing here reads the clock.
/// Each occurrence triggers at most once, anywhere in [start, end), so a launch or wake inside an event still catches
/// it. Triggered ids live in memory and survive <see cref="Update"/>.
/// </summary>
public sealed class CalendarScheduler
{
    /// <summary>When a calendar event triggers. An unrecognized value (written by another build) reads as <see cref="Ask"/>.</summary>
    [JsonConverter(typeof(ModeConverter))]
    public enum Mode
    {
        Off,
        /// <summary>Show a prompt at the start.</summary>
        Ask,
        /// <summary>Record the first meeting app detected during the event.</summary>
        WaitForCall,
        RecordAtStart,
    }

    public sealed class ModeConverter() : TolerantEnumConverter<Mode>(Mode.Ask);

    /// <summary>Calendar settings, stored in the settings file. Every key is optional when read.</summary>
    public sealed record Configuration : IJsonOnDeserialized
    {
        public static readonly (int Min, int Max) StopAfterEndRange = (0, 120);

        [JsonPropertyName("mode")] public Mode Mode { get; set; } = Mode.Ask;
        /// <summary>Calendars not watched; empty watches every calendar, new ones included.</summary>
        [JsonPropertyName("ignoredCalendarIDs")] public List<string> IgnoredCalendarIDs { get; set; } = [];
        /// <summary>Count timed events without a meeting link too.</summary>
        [JsonPropertyName("allTimedEvents")] public bool AllTimedEvents { get; set; }
        /// <summary>Prompt to stop the recording once the event has ended.</summary>
        [JsonPropertyName("stopAtEventEnd")] public bool StopAtEventEnd { get; set; } = true;
        /// <summary>Minutes after the event end before that prompt; clamped to <see cref="StopAfterEndRange"/> when read.</summary>
        [JsonPropertyName("stopAfterEndMinutes")] public int StopAfterEndMinutes { get; set; } = 10;
        /// <summary>Occurrences the user chose to skip, mapped to their end so past ones can be pruned.</summary>
        [JsonPropertyName("skippedEvents"), JsonConverter(typeof(AppleDateDictionaryConverter))]
        public Dictionary<string, DateTimeOffset> SkippedEvents { get; set; } = [];
        /// <summary>Id of EchoDiar's own calendar, set when the first event is saved; null = not created yet.</summary>
        [JsonPropertyName("ownCalendarID")] public string? OwnCalendarID { get; set; }

        /// <summary>The value in force, clamped again at use: a configuration built in code is not clamped.</summary>
        [JsonIgnore]
        public int EffectiveStopAfterEndMinutes => Math.Clamp(StopAfterEndMinutes, StopAfterEndRange.Min, StopAfterEndRange.Max);

        /// <summary>A copy without skipped occurrences that have ended.</summary>
        public Configuration Pruned(DateTimeOffset now) => this with
        {
            IgnoredCalendarIDs = [.. IgnoredCalendarIDs],
            SkippedEvents = SkippedEvents.Where(p => p.Value > now).ToDictionary(StringComparer.Ordinal),
        };

        void IJsonOnDeserialized.OnDeserialized() => StopAfterEndMinutes = EffectiveStopAfterEndMinutes;
    }

    /// <summary>What <see cref="Evaluate"/> decided: <see cref="None"/>, or start recording for <see cref="Event"/>;
    /// <see cref="Late"/> when more than 5 minutes past its start.</summary>
    public sealed record Action(CalendarEvent? Event, bool Late)
    {
        public static readonly Action None = new(null, false);

        public static Action Start(CalendarEvent @event, bool Late) => new(@event, Late);
    }

    /// <summary>Beyond this after the start, a trigger counts as late.</summary>
    private static readonly TimeSpan LateThreshold = TimeSpan.FromSeconds(300);

    private List<CalendarEvent> _events = [];
    private Mode _mode = Mode.Ask;
    private Dictionary<string, DateTimeOffset> _skipped = [];
    private readonly HashSet<string> _triggered = new(StringComparer.Ordinal);

    /// <summary>
    /// Trust rule (plan Q3, macOS c28c628): the user organizes the event, or accepted it (tentatively or not), or it has
    /// no invitees and is in EchoDiar's own calendar. "No invitees" on any other calendar does not confirm: a shared
    /// calendar's owner could add it. An invite the user has not answered (a stranger's too) is not confirmed.
    /// </summary>
    public static bool IsConfirmed(bool isOrganizedByUser, CalendarResponse userResponse, bool hasInvitees, bool isOwnCalendar) =>
        (!hasInvitees && isOwnCalendar) || isOrganizedByUser
        || userResponse is CalendarResponse.Accepted or CalendarResponse.Tentative;

    /// <summary>
    /// Whether a due event shows the prompt instead of starting on its own: always in <see cref="Mode.Ask"/>; in the
    /// automatic modes when the event is not <see cref="CalendarEvent.Confirmed"/> or <paramref name="late"/>. Unlike
    /// macOS (late checked only in Record at start), late asks in Wait for the call too. Wait for the call with meeting
    /// detection off behaves like Ask: the caller handles that. <see cref="Mode.Off"/> never triggers: false.
    /// </summary>
    public static bool ShouldAsk(Mode mode, CalendarEvent @event, bool late) => mode switch
    {
        Mode.Off => false,
        Mode.Ask => true,
        _ => late || !@event.Confirmed,
    };

    /// <summary>
    /// Replaces the events and settings, keeping only eligible events (timed, not declined or canceled, in a watched
    /// calendar, with a meeting link unless <see cref="Configuration.AllTimedEvents"/>), sorted by start, then id.
    /// Events in EchoDiar's own calendar count without a meeting link.
    /// </summary>
    public void Update(IEnumerable<CalendarEvent> events, Configuration configuration)
    {
        _mode = configuration.Mode;
        _skipped = new Dictionary<string, DateTimeOffset>(configuration.SkippedEvents, StringComparer.Ordinal);
        var ignored = configuration.IgnoredCalendarIDs.ToHashSet(StringComparer.Ordinal);
        var own = configuration.OwnCalendarID;
        _events = events
            .Where(e => !e.IsAllDay && !e.Declined && !e.Canceled && !ignored.Contains(e.CalendarID)
                && (e.Link is not null || configuration.AllTimedEvents || (own is not null && e.CalendarID == own)))
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The event to start now, if any. When several are due the latest start wins; the others are marked
    /// triggered too.</summary>
    public Action Evaluate(DateTimeOffset now)
    {
        if (_mode == Mode.Off) return Action.None;
        var due = _events
            .Where(e => e.Start <= now && now < e.End && !_triggered.Contains(e.Id) && !_skipped.ContainsKey(e.Id))
            .ToList();
        if (due.Count == 0) return Action.None;
        _triggered.UnionWith(due.Select(e => e.Id));
        var @event = due[^1];
        return Action.Start(@event, Late: now - @event.Start > LateThreshold);
    }

    /// <summary>The eligible event in progress at <paramref name="now"/> (the latest started), whatever the mode.</summary>
    public CalendarEvent? ActiveEvent(DateTimeOffset now) => _events.LastOrDefault(e => e.Start <= now && now < e.End);

    /// <summary>Eligible events not yet over, by start, whatever the mode or skip state.</summary>
    public IReadOnlyList<CalendarEvent> Upcoming(DateTimeOffset now, int limit) =>
        _events.Where(e => e.End > now).Take(limit).ToList();

    /// <summary>The next event start or end strictly after <paramref name="now"/>, when <see cref="Evaluate"/> or the
    /// lists may change; null when none.</summary>
    public DateTimeOffset? NextChange(DateTimeOffset now) =>
        _events.SelectMany(e => new[] { e.Start, e.End }).Where(d => d > now).Select(d => (DateTimeOffset?)d).Min();
}
