using System.Globalization;
using System.Text;
using EchoDiar.Core.Calendar;
using Windows.ApplicationModel.Appointments;
using Windows.Foundation;

namespace EchoDiar.Platform.Calendar;

/// <summary>Why a calendar call failed (macOS <c>CalendarStoreError</c>), for the app's localized message.</summary>
public enum CalendarStoreError
{
    /// <summary>Calendar access is off (never asked, denied, or revoked in Windows Settings).</summary>
    NoAccess,
    /// <summary>The occurrence is no longer in EchoDiar's calendar.</summary>
    EventNotFound,
    /// <summary>Only events in EchoDiar's calendar can be changed.</summary>
    NotOwnEvent,
    /// <summary>The Appointments API failed or did not answer.</summary>
    Failed,
}

/// <summary>A failed calendar call. The message is short English for logs and never holds event content.</summary>
public sealed class CalendarStoreException(CalendarStoreError kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public CalendarStoreError Kind { get; } = kind;
}

/// <summary>
/// <see cref="ICalendarStore"/> on the Appointments API (Windows.ApplicationModel.Appointments): every calendar through
/// the <c>AllCalendarsReadOnly</c> store, EchoDiar's own "EchoDiar" calendar through the <c>AppCalendarsReadWrite</c>
/// store (which lists only this app's calendars, so no one else can add events to it). Behaviour checked in the VM
/// probe (docs/qa/2026-10-25-windows-calendar-probe-qa-vm.md): no consent prompt, start times read back rounded to the
/// minute, <c>OriginalStartTime</c> only on recurring instances, the same LocalIds in both stores, StoreChanged after
/// <c>ChangeTracker.Enable</c>, and the own calendar reporting <c>CanCreateOrUpdateAppointments=False</c> while saves
/// work (so that flag is never used).
/// <para>
/// Event titles, locations and notes come from invites: titles are cleaned for display, notes are only scanned for a
/// meeting link, and none of them is logged. Every WinRT call is awaited with a timeout and its failure becomes a
/// <see cref="CalendarStoreException"/>; nothing blocks the calling thread.
/// </para>
/// </summary>
public sealed class AppointmentCalendarStore(Action<string>? log = null) : ICalendarStore
{
    private const string OwnCalendarTitle = "EchoDiar";

    /// <summary>A call that has not answered by then (a consent prompt with no window) fails.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Every property the event list reads: one not fetched throws when read.</summary>
    private static readonly string[] FetchProperties =
    [
        AppointmentProperties.Subject, AppointmentProperties.StartTime, AppointmentProperties.Duration,
        AppointmentProperties.AllDay, AppointmentProperties.Location, AppointmentProperties.Details,
        AppointmentProperties.Uri, AppointmentProperties.OnlineMeetingLink, AppointmentProperties.Organizer,
        AppointmentProperties.Invitees, AppointmentProperties.IsOrganizedByUser, AppointmentProperties.UserResponse,
        AppointmentProperties.IsCanceledMeeting, AppointmentProperties.OriginalStartTime, AppointmentProperties.Recurrence,
    ];

    private readonly object _sync = new();
    /// <summary>One own-calendar resolve at a time, so two first saves do not create two calendars.</summary>
    private readonly SemaphoreSlim _ownGate = new(1, 1);
    private AppointmentStore? _readStore;
    private AppointmentStore? _writeStore;
    private CalendarAccess _access = CalendarAccess.NotDetermined;

    /// <inheritdoc/>
    public event Action? Changed;

    /// <summary>The state of the all-calendars store: <see cref="CalendarAccess.NotDetermined"/> until
    /// <see cref="RequestAccessAsync"/>, <see cref="CalendarAccess.Denied"/> once a request or a query is refused.</summary>
    public CalendarAccess Access
    {
        get
        {
            lock (_sync) return _access;
        }
    }

    /// <summary>Opens the all-calendars store (Windows asks no consent; the privacy switch in Windows Settings can
    /// refuse it). A store already open is kept; a new one replaces any earlier store.</summary>
    public async Task<bool> RequestAccessAsync()
    {
        lock (_sync)
        {
            if (_access == CalendarAccess.Granted && _readStore is not null) return true;
        }

        AppointmentStore? store = null;
        try
        {
            store = await Call("RequestStoreAsync(AllCalendarsReadOnly)",
                () => AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly)).ConfigureAwait(false);
        }
        catch (CalendarStoreException ex)
        {
            Log(ex.Message);
        }

        if (store is null)
        {
            Drop();
            return false;
        }

        WatchChanges(store);
        lock (_sync)
        {
            Unwatch(_readStore);
            _readStore = store;
            _writeStore = null;
            _access = CalendarAccess.Granted;
        }
        return true;
    }

    public async Task<IReadOnlyList<CalendarInfo>> CalendarsAsync()
    {
        if (ReadStore() is not { } store) return [];
        var calendars = await Read(store, "FindAppointmentCalendarsAsync",
            () => store.FindAppointmentCalendarsAsync(FindAppointmentCalendarsOptions.IncludeHidden)).ConfigureAwait(false);
        if (calendars is null) return [];

        // Only this app's calendars are in the read-write store, whatever their CanCreateOrUpdateAppointments says.
        var writable = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var own = await (await WriteStoreAsync().ConfigureAwait(false)).FindAppointmentCalendarsAsync(
                FindAppointmentCalendarsOptions.IncludeHidden).AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);
            writable.UnionWith(own.Select(c => c.LocalId));
        }
        catch (Exception ex)
        {
            Log($"own calendars unreadable: 0x{ex.HResult:X8}");
        }

        var list = new List<CalendarInfo>();
        foreach (var calendar in calendars)
        {
            try
            {
                list.Add(new CalendarInfo(calendar.LocalId, AppointmentMapping.DisplayTitle(calendar.DisplayName),
                    AppointmentMapping.DisplayTitle(calendar.SourceDisplayName), writable.Contains(calendar.LocalId)));
            }
            catch (Exception ex)
            {
                Log($"skipped a calendar: 0x{ex.HResult:X8}");
            }
        }
        return list
            .OrderBy(c => c.SourceTitle, StringComparer.Ordinal)
            .ThenBy(c => c.Title, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Instances in every calendar (hidden ones too, so each <see cref="CalendarEvent.CalendarID"/> has a row in
    /// <see cref="CalendarsAsync"/>; the scheduler filters by the ignore list). The Appointments API returns the
    /// instances of [<paramref name="from"/>, <paramref name="to"/>); the caller starts its window early enough to catch
    /// an event already in progress.
    /// </summary>
    public async Task<IReadOnlyList<CalendarEvent>> EventsAsync(DateTimeOffset from, DateTimeOffset to, string? ownCalendarId)
    {
        if (to <= from || ReadStore() is not { } store) return [];
        var options = new FindAppointmentsOptions { IncludeHidden = true };
        foreach (var property in FetchProperties) options.FetchProperties.Add(property);
        var found = await Read(store, "FindAppointmentsAsync",
            () => store.FindAppointmentsAsync(from, to - from, options)).ConfigureAwait(false);
        if (found is null) return [];

        var events = new List<CalendarEvent>(found.Count);
        foreach (var appointment in found)
        {
            try
            {
                events.Add(Event(appointment, ownCalendarId));
            }
            catch (Exception ex)
            {
                Log($"skipped an appointment: 0x{ex.HResult:X8}");
            }
        }
        return events;
    }

    private static CalendarEvent Event(Appointment appointment, string? ownCalendarId)
    {
        var start = appointment.StartTime;
        var calendarId = appointment.CalendarId;
        return new CalendarEvent(
            Id: AppointmentMapping.OccurrenceId(appointment.LocalId, appointment.OriginalStartTime ?? start),
            Title: AppointmentMapping.DisplayTitle(appointment.Subject),
            Start: start,
            End: start + appointment.Duration,
            IsAllDay: appointment.AllDay,
            CalendarID: calendarId,
            // Details (the notes) are only scanned for a link, never kept.
            Link: MeetingLink.Find(appointment.Uri, appointment.OnlineMeetingLink, appointment.Location, appointment.Details),
            Declined: appointment.UserResponse == AppointmentParticipantResponse.Declined,
            Canceled: appointment.IsCanceledMeeting,
            Confirmed: CalendarScheduler.IsConfirmed(
                appointment.IsOrganizedByUser, AppointmentMapping.Response(appointment.UserResponse),
                hasInvitees: appointment.Invitees.Count > 0,
                isOwnCalendar: ownCalendarId is not null && calendarId == ownCalendarId));
    }

    // EchoDiar's own calendar

    public Task<string> SaveEventAsync(
        CalendarEventDraft draft, CalendarEvent? editing, CalendarSpan span, string? storedCalendarId) =>
        Guard("save", async () =>
        {
            var calendar = await OwnCalendarAsync(storedCalendarId, create: editing is null).ConfigureAwait(false)
                ?? throw NotOwn();
            if (editing is null)
            {
                var appointment = new Appointment();
                Apply(draft, appointment, NewRecurrence(draft));
                await Call("SaveAppointmentAsync", () => calendar.SaveAppointmentAsync(appointment)).ConfigureAwait(false);
                return calendar.LocalId;
            }

            var (localId, originalStart, master) = await OwnMasterAsync(calendar, editing).ConfigureAwait(false);
            // A rule the form can't show is kept as read (macOS keepsRules).
            var keepRule = draft.RepeatRule == CalendarEventDraft.Repeat.Custom;
            if (master.Recurrence is not { } recurrence
                || (span == CalendarSpan.ThisAndFuture && originalStart <= master.StartTime))
            {
                // A one-off, or the whole series from its first occurrence.
                Apply(draft, master, keepRule ? null : NewRecurrence(draft), setRecurrence: !keepRule);
                await Call("SaveAppointmentAsync", () => calendar.SaveAppointmentAsync(master)).ConfigureAwait(false);
            }
            else if (span == CalendarSpan.ThisEvent)
            {
                // One occurrence can't change the rule: an exception of the series.
                var instance = await Call("GetAppointmentInstanceAsync",
                        () => calendar.GetAppointmentInstanceAsync(localId, originalStart)).ConfigureAwait(false)
                    ?? throw NotFound();
                Apply(draft, instance, null, setRecurrence: false);
                await Call("SaveAppointmentAsync", () => calendar.SaveAppointmentAsync(instance)).ConfigureAwait(false);
            }
            else
            {
                await SplitAsync(calendar, master, recurrence, originalStart, draft, keepRule).ConfigureAwait(false);
            }
            return calendar.LocalId;
        });

    public Task DeleteEventAsync(CalendarEvent @event, CalendarSpan span, string? storedCalendarId) =>
        Guard("delete", async () =>
        {
            var calendar = await OwnCalendarAsync(storedCalendarId, create: false).ConfigureAwait(false) ?? throw NotOwn();
            var (localId, originalStart, master) = await OwnMasterAsync(calendar, @event).ConfigureAwait(false);
            if (master.Recurrence is not { } recurrence
                || (span == CalendarSpan.ThisAndFuture && originalStart <= master.StartTime))
            {
                await Call("DeleteAppointmentAsync", () => calendar.DeleteAppointmentAsync(localId)).ConfigureAwait(false);
            }
            else if (span == CalendarSpan.ThisEvent)
            {
                await Call("DeleteAppointmentInstanceAsync",
                    () => calendar.DeleteAppointmentInstanceAsync(localId, originalStart)).ConfigureAwait(false);
            }
            else
            {
                var exceptions = await ExceptionsBeforeAsync(calendar, localId, originalStart).ConfigureAwait(false);
                EndSeriesBefore(recurrence, originalStart);
                master.Recurrence = recurrence;
                await Call("SaveAppointmentAsync", () => calendar.SaveAppointmentAsync(master)).ConfigureAwait(false);
                await RestoreExceptionsAsync(calendar, localId, exceptions).ConfigureAwait(false);
            }
        });

    public async Task<(CalendarEventDraft Draft, bool IsRecurring)?> DraftAsync(CalendarEvent @event, string? storedCalendarId)
    {
        try
        {
            return await Guard("draft", async () =>
            {
                var calendar = await OwnCalendarAsync(storedCalendarId, create: false).ConfigureAwait(false);
                if (calendar is null) return null;
                var (localId, originalStart, master) = await OwnMasterAsync(calendar, @event).ConfigureAwait(false);
                var occurrence = master;
                if (master.Recurrence is not null)
                {
                    occurrence = await Call("GetAppointmentInstanceAsync",
                            () => calendar.GetAppointmentInstanceAsync(localId, originalStart)).ConfigureAwait(false)
                        ?? throw NotFound();
                }

                var rule = master.Recurrence is { } recurrence ? AppointmentMapping.Rule(recurrence) : null;
                var (repeat, repeatEnd) = AppointmentMapping.RepeatOf(rule, master.StartTime, TimeZoneInfo.Local);
                var draft = new CalendarEventDraft(
                    occurrence.Subject ?? "", occurrence.StartTime, occurrence.StartTime + occurrence.Duration,
                    occurrence.Uri?.AbsoluteUri ?? "", repeat, repeatEnd);
                return ((CalendarEventDraft, bool)?)(draft, master.Recurrence is not null);
            }).ConfigureAwait(false);
        }
        catch (CalendarStoreException ex)
        {
            Log(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// "This and Future Events" from an occurrence after the first (plan decision: no direct API): a new series from
    /// that occurrence with the draft applied, then the old series ended on the day before. The new series is saved
    /// first and removed again when the old one can't be ended, so a failure duplicates or loses nothing.
    /// </summary>
    private async Task SplitAsync(AppointmentCalendar calendar, Appointment master, AppointmentRecurrence recurrence,
        DateTimeOffset originalStart, CalendarEventDraft draft, bool keepRule)
    {
        var exceptions = await ExceptionsBeforeAsync(calendar, master.LocalId, originalStart).ConfigureAwait(false);
        var next = new Appointment
        {
            Location = master.Location,
            DetailsKind = master.DetailsKind,
            Details = master.Details,
            AllDay = master.AllDay,
            Reminder = master.Reminder,
        };
        Apply(draft, next, keepRule ? AppointmentMapping.Copy(recurrence) : NewRecurrence(draft));
        await Call("SaveAppointmentAsync(new series)", () => calendar.SaveAppointmentAsync(next)).ConfigureAwait(false);

        try
        {
            EndSeriesBefore(recurrence, originalStart);
            master.Recurrence = recurrence;
            await Call("SaveAppointmentAsync(old series)", () => calendar.SaveAppointmentAsync(master)).ConfigureAwait(false);
        }
        catch (CalendarStoreException)
        {
            var nextId = next.LocalId;
            if (!string.IsNullOrEmpty(nextId))
            {
                try
                {
                    await Call("DeleteAppointmentAsync(new series)", () => calendar.DeleteAppointmentAsync(nextId)).ConfigureAwait(false);
                }
                catch (CalendarStoreException ex)
                {
                    Log(ex.Message);
                }
            }
            throw;
        }
        await RestoreExceptionsAsync(calendar, master.LocalId, exceptions).ConfigureAwait(false);
    }

    /// <summary>
    /// The changed or deleted occurrences of a series before <paramref name="before"/>: saving a series' recurrence
    /// drops its exceptions on Windows, so ending it puts them back after (EventKit keeps them).
    /// </summary>
    private static async Task<IReadOnlyList<AppointmentException>> ExceptionsBeforeAsync(
        AppointmentCalendar calendar, string localId, DateTimeOffset before)
    {
        var exceptions = await Call("FindExceptionsFromMasterAsync",
            () => calendar.FindExceptionsFromMasterAsync(localId)).ConfigureAwait(false);
        return exceptions.Where(e => e.Appointment.OriginalStartTime is { } start && start < before).ToList();
    }

    /// <summary>Puts <paramref name="exceptions"/> back on the series: the form's fields of a changed occurrence, or
    /// its deletion. One that fails is logged; the series itself is already saved.</summary>
    private async Task RestoreExceptionsAsync(
        AppointmentCalendar calendar, string localId, IReadOnlyList<AppointmentException> exceptions)
    {
        foreach (var exception in exceptions)
        {
            var original = exception.Appointment.OriginalStartTime!.Value;
            try
            {
                if (exception.IsDeleted)
                {
                    await Call("DeleteAppointmentInstanceAsync(exception)",
                        () => calendar.DeleteAppointmentInstanceAsync(localId, original)).ConfigureAwait(false);
                    continue;
                }
                var instance = await Call("GetAppointmentInstanceAsync(exception)",
                    () => calendar.GetAppointmentInstanceAsync(localId, original)).ConfigureAwait(false);
                if (instance is null) continue;
                var changed = exception.Appointment;
                instance.Subject = changed.Subject;
                instance.StartTime = changed.StartTime;
                instance.Duration = changed.Duration;
                instance.Uri = changed.Uri;
                await Call("SaveAppointmentAsync(exception)", () => calendar.SaveAppointmentAsync(instance)).ConfigureAwait(false);
            }
            catch (CalendarStoreException ex)
            {
                Log(ex.Message);
            }
        }
    }

    /// <summary>Ends <paramref name="recurrence"/> at the last second of the day before <paramref name="occurrenceStart"/>.</summary>
    private static void EndSeriesBefore(AppointmentRecurrence recurrence, DateTimeOffset occurrenceStart)
    {
        recurrence.Occurrences = null;
        recurrence.Until = AppointmentMapping.EndOfDayBefore(occurrenceStart, TimeZoneInfo.Local);
    }

    /// <summary>The recurrence for the form's Repeat; null for None and Custom.</summary>
    private static AppointmentRecurrence? NewRecurrence(CalendarEventDraft draft) =>
        AppointmentMapping.RuleFor(draft, TimeZoneInfo.Local) is { } rule ? AppointmentMapping.Recurrence(rule) : null;

    /// <summary>The form's values onto <paramref name="appointment"/>; location and notes are left as they are.</summary>
    private static void Apply(CalendarEventDraft draft, Appointment appointment, AppointmentRecurrence? recurrence,
        bool setRecurrence = true)
    {
        appointment.Subject = draft.Title.Trim();
        appointment.StartTime = draft.Start;
        appointment.Duration = draft.End - draft.Start;
        appointment.Uri = draft.Url;
        if (setRecurrence) appointment.Recurrence = recurrence;
    }

    /// <summary>The series (or one-off) behind <paramref name="event"/>, refetched; throws unless it is in
    /// <paramref name="calendar"/>.</summary>
    private static async Task<(string LocalId, DateTimeOffset OriginalStart, Appointment Master)> OwnMasterAsync(
        AppointmentCalendar calendar, CalendarEvent @event)
    {
        if (@event.CalendarID != calendar.LocalId
            || !AppointmentMapping.TryParseOccurrenceId(@event.Id, out var localId, out var originalStart))
        {
            throw NotOwn();
        }

        var master = await Call("GetAppointmentAsync", () => calendar.GetAppointmentAsync(localId)).ConfigureAwait(false)
            ?? throw NotFound();
        if (master.CalendarId != calendar.LocalId) throw NotOwn();
        return (localId, originalStart, master);
    }

    /// <summary>
    /// EchoDiar's calendar in the read-write store (only this app's calendars): the stored id, else one titled
    /// "EchoDiar" (adopted), else a new one when <paramref name="create"/>; null when missing and not created.
    /// </summary>
    private async Task<AppointmentCalendar?> OwnCalendarAsync(string? storedId, bool create)
    {
        if (Access != CalendarAccess.Granted)
            throw new CalendarStoreException(CalendarStoreError.NoAccess, "No calendar access.");
        var store = await WriteStoreAsync().ConfigureAwait(false);
        await _ownGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var calendars = await Call("FindAppointmentCalendarsAsync(own)",
                () => store.FindAppointmentCalendarsAsync(FindAppointmentCalendarsOptions.IncludeHidden)).ConfigureAwait(false);
            var own = calendars.FirstOrDefault(c => storedId is not null && c.LocalId == storedId)
                ?? calendars.FirstOrDefault(c => c.DisplayName == OwnCalendarTitle);
            if (own is not null || !create) return own;
            return await Call("CreateAppointmentCalendarAsync",
                () => store.CreateAppointmentCalendarAsync(OwnCalendarTitle)).ConfigureAwait(false)
                ?? throw new CalendarStoreException(CalendarStoreError.Failed, "EchoDiar's calendar could not be created.");
        }
        finally
        {
            _ownGate.Release();
        }
    }

    // Stores

    private AppointmentStore? ReadStore()
    {
        lock (_sync) return _access == CalendarAccess.Granted ? _readStore : null;
    }

    private async Task<AppointmentStore> WriteStoreAsync()
    {
        lock (_sync)
        {
            if (_writeStore is not null) return _writeStore;
        }

        var store = await Call("RequestStoreAsync(AppCalendarsReadWrite)",
                () => AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AppCalendarsReadWrite)).ConfigureAwait(false)
            ?? throw new CalendarStoreException(CalendarStoreError.NoAccess, "No calendar access.");
        lock (_sync)
        {
            // A concurrent request may have won; either store works.
            return _writeStore ??= store;
        }
    }

    /// <summary>A query on the read store; null when it was refused, which turns <see cref="Access"/> to denied and
    /// drops the store (the next <see cref="RequestAccessAsync"/> makes a new one).</summary>
    private async Task<T?> Read<T>(AppointmentStore store, string name, Func<IAsyncOperation<T>> start) where T : class
    {
        try
        {
            return await Call(name, start).ConfigureAwait(false);
        }
        catch (CalendarStoreException ex) when (ex.Kind == CalendarStoreError.NoAccess)
        {
            Log(ex.Message);
            lock (_sync)
            {
                if (!ReferenceEquals(_readStore, store)) return null;
            }
            Drop();
            return null;
        }
    }

    private void Drop()
    {
        lock (_sync)
        {
            Unwatch(_readStore);
            _readStore = null;
            _writeStore = null;
            _access = CalendarAccess.Denied;
        }
    }

    /// <summary>Raises <see cref="Changed"/> on StoreChanged. The read store also sees this app's own writes (VM probe),
    /// so the write store is not watched.</summary>
    private void WatchChanges(AppointmentStore store)
    {
        try
        {
            store.ChangeTracker.Enable();
            store.StoreChanged += OnStoreChanged;
        }
        catch (Exception ex)
        {
            Log($"change tracking unavailable: 0x{ex.HResult:X8}");
        }
    }

    private void Unwatch(AppointmentStore? store)
    {
        if (store is null) return;
        try
        {
            store.StoreChanged -= OnStoreChanged;
        }
        catch (Exception ex)
        {
            Log($"StoreChanged unsubscribe failed: 0x{ex.HResult:X8}");
        }
    }

    private void OnStoreChanged(AppointmentStore sender, AppointmentStoreChangedEventArgs args)
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log($"Changed handler failed: {ex.GetType().Name}");
        }
    }

    // Calls

    /// <summary>Runs <paramref name="body"/>; any failure other than a <see cref="CalendarStoreException"/> becomes one.</summary>
    private static async Task<T> Guard<T>(string name, Func<Task<T>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CalendarStoreException)
        {
            throw Failure(name, ex);
        }
    }

    private static Task Guard(string name, Func<Task> body) => Guard(name, async () =>
    {
        await body().ConfigureAwait(false);
        return true;
    });

    /// <summary>Awaits one WinRT operation with <see cref="CallTimeout"/>.</summary>
    private static async Task<T> Call<T>(string name, Func<IAsyncOperation<T>> start)
    {
        try
        {
            return await start().AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Failure(name, ex);
        }
    }

    private static async Task Call(string name, Func<IAsyncAction> start)
    {
        try
        {
            await start().AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Failure(name, ex);
        }
    }

    private const int EAccessDenied = unchecked((int)0x80070005);

    private static CalendarStoreException Failure(string name, Exception ex) =>
        ex is UnauthorizedAccessException || ex.HResult == EAccessDenied
            ? new CalendarStoreException(CalendarStoreError.NoAccess, $"{name}: access denied.", ex)
            : new CalendarStoreException(CalendarStoreError.Failed,
                ex is TimeoutException ? $"{name}: no answer." : $"{name} failed: 0x{ex.HResult:X8}.", ex);

    private static CalendarStoreException NotFound() =>
        new(CalendarStoreError.EventNotFound, "The event no longer exists.");

    private static CalendarStoreException NotOwn() =>
        new(CalendarStoreError.NotOwnEvent, "Only events in EchoDiar's calendar can be changed.");

    private void Log(string message)
    {
        try
        {
            log?.Invoke($"EchoDiar calendar: {message}");
        }
        catch (Exception)
        {
            // A logger failure must not break a calendar call.
        }
    }
}

/// <summary>The pure parts of <see cref="AppointmentCalendarStore"/>: display text, occurrence ids and the form's
/// Repeat against an appointment recurrence. No WinRT object is made here except in <see cref="Recurrence"/> and
/// <see cref="Copy"/>.</summary>
internal static class AppointmentMapping
{
    public const int MaxTitleLength = 200;

    /// <summary>An appointment recurrence's fields that the form's Repeat reads and writes.</summary>
    /// <param name="Day">Day of the month (Monthly only); 0 when unused.</param>
    public sealed record RecurrenceRule(
        AppointmentRecurrenceUnit Unit, uint Interval, AppointmentDaysOfWeek DaysOfWeek, uint Day, uint? Occurrences,
        DateTimeOffset? Until);

    private const AppointmentDaysOfWeek Weekdays = AppointmentDaysOfWeek.Monday | AppointmentDaysOfWeek.Tuesday
        | AppointmentDaysOfWeek.Wednesday | AppointmentDaysOfWeek.Thursday | AppointmentDaysOfWeek.Friday;

    /// <summary>
    /// An invite's text as safe display text (macOS <c>displayTitle</c>): control and line/paragraph separators become
    /// spaces; format characters (bidi overrides and isolates, invisible marks) go, except the joiners U+200C/U+200D that
    /// emoji and some scripts need; whitespace runs become one space; trimmed; at most 200 characters, never cutting a
    /// character in two. Empty when nothing is left.
    /// </summary>
    public static string DisplayTitle(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(Math.Min(text.Length, MaxTitleLength * 2));
        var pendingSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format && rune.Value is not (0x200C or 0x200D)) continue;
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                || Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(rune.ToString());
        }

        var clean = builder.ToString();
        if (clean.Length <= MaxTitleLength) return clean;
        var info = new StringInfo(clean);
        return info.LengthInTextElements <= MaxTitleLength
            ? clean
            : info.SubstringByTextElements(0, MaxTitleLength).TrimEnd();
    }

    /// <summary>An occurrence's id: the appointment's LocalId and the occurrence's original start (its slot in the
    /// series, unchanged when the occurrence is moved) in UTC round-trip form.</summary>
    public static string OccurrenceId(string localId, DateTimeOffset originalStart) =>
        localId + "|" + originalStart.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The parts of an <see cref="OccurrenceId"/>; split at the last '|'.</summary>
    public static bool TryParseOccurrenceId(string id, out string localId, out DateTimeOffset originalStart)
    {
        localId = "";
        originalStart = default;
        var bar = id.LastIndexOf('|');
        if (bar <= 0) return false;
        if (!DateTimeOffset.TryParseExact(id[(bar + 1)..], "O", CultureInfo.InvariantCulture, DateTimeStyles.None,
                out originalStart))
        {
            return false;
        }
        localId = id[..bar];
        return true;
    }

    /// <summary>The user's answer; Unknown reads as <see cref="CalendarResponse.None"/>.</summary>
    public static CalendarResponse Response(AppointmentParticipantResponse response) => response switch
    {
        AppointmentParticipantResponse.Tentative => CalendarResponse.Tentative,
        AppointmentParticipantResponse.Accepted => CalendarResponse.Accepted,
        AppointmentParticipantResponse.Declined => CalendarResponse.Declined,
        _ => CalendarResponse.None,
    };

    /// <summary>
    /// The recurrence for the form's Repeat and End; null for None and Custom (the caller keeps a custom rule). Weekly
    /// repeats on the start's weekday, Weekdays Monday to Friday, Monthly on the start's day of the month, all in
    /// <paramref name="zone"/>; the series ends at the last second of the End day.
    /// </summary>
    public static RecurrenceRule? RuleFor(CalendarEventDraft draft, TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTime(draft.Start, zone);
        var until = draft.RepeatUntilIn(zone);
        return draft.RepeatRule switch
        {
            CalendarEventDraft.Repeat.Daily => new(AppointmentRecurrenceUnit.Daily, 1, AppointmentDaysOfWeek.None, 0, null, until),
            CalendarEventDraft.Repeat.Weekdays => new(AppointmentRecurrenceUnit.Weekly, 1, Weekdays, 0, null, until),
            CalendarEventDraft.Repeat.Weekly => new(AppointmentRecurrenceUnit.Weekly, 1, DayFlag(start.DayOfWeek), 0, null, until),
            CalendarEventDraft.Repeat.Monthly => new(AppointmentRecurrenceUnit.Monthly, 1, AppointmentDaysOfWeek.None,
                (uint)start.Day, null, until),
            _ => null,
        };
    }

    /// <summary>The form's Repeat and End for a series starting at <paramref name="seriesStart"/>: Custom (and no End)
    /// for anything the form can't show, such as an interval, a count end or other days.</summary>
    public static (CalendarEventDraft.Repeat Repeat, DateTimeOffset? RepeatEnd) RepeatOf(
        RecurrenceRule? rule, DateTimeOffset seriesStart, TimeZoneInfo zone)
    {
        if (rule is null) return (CalendarEventDraft.Repeat.None, null);
        if (rule.Interval != 1 || rule.Occurrences is not null) return (CalendarEventDraft.Repeat.Custom, null);
        var start = TimeZoneInfo.ConvertTime(seriesStart, zone);
        var repeat = rule.Unit switch
        {
            AppointmentRecurrenceUnit.Daily when rule.DaysOfWeek == AppointmentDaysOfWeek.None => CalendarEventDraft.Repeat.Daily,
            AppointmentRecurrenceUnit.Weekly when rule.DaysOfWeek == Weekdays => CalendarEventDraft.Repeat.Weekdays,
            AppointmentRecurrenceUnit.Weekly when rule.DaysOfWeek is AppointmentDaysOfWeek.None
                || rule.DaysOfWeek == DayFlag(start.DayOfWeek) => CalendarEventDraft.Repeat.Weekly,
            AppointmentRecurrenceUnit.Monthly when rule.Day == 0 || rule.Day == start.Day => CalendarEventDraft.Repeat.Monthly,
            _ => CalendarEventDraft.Repeat.Custom,
        };
        return (repeat, repeat == CalendarEventDraft.Repeat.Custom ? null : rule.Until);
    }

    /// <summary>The last second of the day before <paramref name="occurrenceStart"/>'s day in <paramref name="zone"/>
    /// (a "This and Future Events" split ends the old series there).</summary>
    public static DateTimeOffset EndOfDayBefore(DateTimeOffset occurrenceStart, TimeZoneInfo zone)
    {
        var day = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(occurrenceStart, zone).Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(day, zone.GetUtcOffset(day)).AddSeconds(-1);
    }

    /// <summary>Sunday = 1 … Saturday = 64, as <see cref="DayOfWeek"/> counts from Sunday = 0.</summary>
    private static AppointmentDaysOfWeek DayFlag(DayOfWeek day) => (AppointmentDaysOfWeek)(1 << (int)day);

    public static RecurrenceRule Rule(AppointmentRecurrence recurrence) => new(
        recurrence.Unit, recurrence.Interval, recurrence.DaysOfWeek, recurrence.Day, recurrence.Occurrences,
        recurrence.Until);

    public static AppointmentRecurrence Recurrence(RecurrenceRule rule)
    {
        var recurrence = new AppointmentRecurrence { Unit = rule.Unit, Interval = rule.Interval };
        if (rule.DaysOfWeek != AppointmentDaysOfWeek.None) recurrence.DaysOfWeek = rule.DaysOfWeek;
        if (rule.Day != 0) recurrence.Day = rule.Day;
        if (rule.Occurrences is { } count) recurrence.Occurrences = count;
        if (rule.Until is { } until) recurrence.Until = until;
        return recurrence;
    }

    /// <summary>A copy of every settable field, for a new series that keeps a rule the form can't show. Unused fields
    /// (0, null, empty) are left at their defaults, as the setters refuse some of them.</summary>
    public static AppointmentRecurrence Copy(AppointmentRecurrence source)
    {
        var copy = new AppointmentRecurrence
        {
            Unit = source.Unit,
            Interval = source.Interval,
            DaysOfWeek = source.DaysOfWeek,
            WeekOfMonth = source.WeekOfMonth,
        };
        if (source.Month != 0) copy.Month = source.Month;
        if (source.Day != 0) copy.Day = source.Day;
        if (source.Occurrences is { } count) copy.Occurrences = count;
        if (source.Until is { } until) copy.Until = until;
        if (!string.IsNullOrEmpty(source.TimeZone)) copy.TimeZone = source.TimeZone;
        return copy;
    }
}
