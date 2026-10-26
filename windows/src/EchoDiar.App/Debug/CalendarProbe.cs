#if DEBUG
using System.Globalization;
using EchoDiar.Core.Calendar;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Calendar;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.Appointments;
using Windows.Foundation;

namespace EchoDiar.App;

/// <summary>
/// DEBUG headless harness <c>--calendar-probe</c> (task 1.4 VM gate): checks the Appointments API in this packaged
/// full-trust app before any calendar code is written. It reads every calendar, creates its own "EchoDiar Probe"
/// calendar (never the app's "EchoDiar" one), saves a one-off and a weekly appointment, reads them back, waits for
/// StoreChanged, deletes one weekly instance, then removes everything it made. Each line goes to
/// <c>Probes\calendar-probe-&lt;stamp&gt;.log</c>, stdout and the debugger; the last is the <c>CALENDAR-PROBE</c>
/// summary. Exit code 0 when own-create, save, read-back and store-changed pass, else 1.
/// </summary>
internal static class CalendarProbe
{
    private const string Flag = "--calendar-probe";
    private const string ProbeCalendarName = "EchoDiar Probe";
    private const string OneOffSubject = "Probe one-off";
    private const string WeeklySubject = "Probe weekly";
    private const uint WeeklyOccurrences = 3;

    /// <summary>A WinRT call that has not answered by then (a consent prompt with no window) is logged as a failure.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StoreChangedTimeout = TimeSpan.FromSeconds(5);

    private static readonly object LogLock = new();
    private static string? _logPath;

    /// <summary>Starts the probe when <paramref name="commandLine"/> has <c>--calendar-probe</c>, and calls <paramref name="exit"/> on this thread when it ends.</summary>
    public static bool TryRun(string[] commandLine, Action exit)
    {
        if (Array.IndexOf(commandLine, Flag) < 0) return false;

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(async () =>
        {
            var code = 1;
            try
            {
                code = await RunAsync();
            }
            catch (Exception ex)
            {
                Log($"probe failed: {ex}");
            }
            finally
            {
                dispatcher.TryEnqueue(() =>
                {
                    Environment.ExitCode = code;
                    exit();
                });
            }
        });
        return true;
    }

    private static async Task<int> RunAsync()
    {
        try
        {
            Directory.CreateDirectory(App.Paths.Probes);
            _logPath = Path.Combine(App.Paths.Probes, $"calendar-probe-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"no log file: {ex.Message}");
        }

        var now = DateTimeOffset.Now;
        Log($"start {now:O} (OS {Environment.OSVersion.VersionString})");

        // 1. Every calendar, read-only (the store that asks for the Calendar privacy consent).
        var readAll = "FAIL";
        var readStore = await Step("RequestStoreAsync(AllCalendarsReadOnly)",
            () => AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly));
        if (readStore is null) Log("FAIL RequestStoreAsync(AllCalendarsReadOnly): null store");
        else
        {
            Log("OK RequestStoreAsync(AllCalendarsReadOnly)");
            var calendars = await Step("FindAppointmentCalendarsAsync(IncludeHidden)",
                () => readStore.FindAppointmentCalendarsAsync(FindAppointmentCalendarsOptions.IncludeHidden));
            if (calendars is not null)
            {
                foreach (var calendar in calendars) LogCalendar(calendar);
                Log($"calendars: {calendars.Count}");
                readAll = calendars.Count > 0 ? "OK" : "EMPTY 0";
            }
        }

        // 2. The probe's own calendar, read-write.
        var ownCreate = "FAIL";
        var save = "FAIL";
        var readBack = "FAIL";
        var storeChanged = "FAIL";
        var deleteInstance = "FAIL";
        var cleanup = "FAIL";
        AppointmentCalendar? probeCalendar = null;
        bool? appointmentsDeleted = null;
        var writeStore = await Step("RequestStoreAsync(AppCalendarsReadWrite)",
            () => AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AppCalendarsReadWrite));
        if (writeStore is null) Log("FAIL RequestStoreAsync(AppCalendarsReadWrite): null store");
        else
        {
            Log("OK RequestStoreAsync(AppCalendarsReadWrite)");
            try
            {
                probeCalendar = await CreateProbeCalendar(writeStore);
                if (probeCalendar is not null)
                {
                    ownCreate = "OK";
                    LogCalendar(probeCalendar);

                    // 3. Change tracking on both stores (only the read-write one counts for the summary).
                    var writeChanged = WatchStore(writeStore, "read-write");
                    var readChanged = readStore is null ? null : WatchStore(readStore, "read-only");

                    // 4. A one-off and a weekly appointment.
                    var oneOffStart = now.AddMinutes(10);
                    var weeklyStart = now.AddHours(1);
                    var oneOff = await SaveOneOff(probeCalendar, oneOffStart);
                    var weekly = await SaveWeekly(probeCalendar, weeklyStart);
                    if (oneOff is not null && weekly is not null) save = "OK";

                    // 5. StoreChanged after the saves.
                    storeChanged = await WaitChanged(writeChanged, "read-write") ? "OK" : "FAIL";
                    if (readChanged is not null) await WaitChanged(readChanged, "read-only");

                    // 6. Read back over 7 days on both stores, then the weekly series over its 3 weeks.
                    var window = (Start: now.AddHours(-1), Length: TimeSpan.FromDays(7));
                    var written = await Find(writeStore, "read-write", window.Start, window.Length);
                    if (readStore is not null) await Find(readStore, "read-only", window.Start, window.Length);
                    var seriesLength = TimeSpan.FromDays(7 * WeeklyOccurrences + 1);
                    var series = await Find(writeStore, "read-write weekly", window.Start, seriesLength);
                    var weeklyInstances = Instances(series, probeCalendar, WeeklySubject);
                    Log($"weekly instances over {seriesLength.TotalDays:0} days: {weeklyInstances.Count} (expected {WeeklyOccurrences})");
                    if (Instances(written, probeCalendar, OneOffSubject).Count == 1
                        && Instances(written, probeCalendar, WeeklySubject).Count >= 1
                        && weeklyInstances.Count == WeeklyOccurrences)
                    {
                        readBack = "OK";
                    }

                    // 7. Delete the weekly series' second instance.
                    if (weekly is not null && weeklyInstances.Count >= 2)
                    {
                        deleteInstance = await DeleteInstance(writeStore, probeCalendar, weekly, weeklyInstances[1], window.Start, seriesLength)
                            ? "OK"
                            : "FAIL";
                    }
                    else Log("FAIL DeleteAppointmentInstanceAsync: no second weekly instance to delete");

                    // 8a. The test appointments.
                    appointmentsDeleted = true;
                    foreach (var localId in new[] { oneOff, weekly })
                    {
                        if (localId is null) continue;
                        appointmentsDeleted &= await Step($"DeleteAppointmentAsync({localId})", () => probeCalendar.DeleteAppointmentAsync(localId));
                    }
                }
            }
            finally
            {
                // 8b. The probe calendar, whatever failed above.
                if (probeCalendar is not null)
                {
                    var calendar = probeCalendar;
                    var calendarDeleted = await Step("AppointmentCalendar.DeleteAsync", () => calendar.DeleteAsync());
                    if (calendarDeleted) Log($"OK deleted calendar {ProbeCalendarName}");
                    // A step that threw before 8a left its appointments to the calendar's delete.
                    cleanup = calendarDeleted && appointmentsDeleted != false ? "OK" : "FAIL";
                }
            }
        }

        Log($"CALENDAR-PROBE read-all={readAll} own-create={ownCreate} save={save} read-back={readBack} "
            + $"store-changed={storeChanged} delete-instance={deleteInstance} cleanup={cleanup}");
        return ownCreate == "OK" && save == "OK" && readBack == "OK" && storeChanged == "OK" ? 0 : 1;
    }

    /// <summary>A fresh "EchoDiar Probe" calendar: one left by an earlier run is deleted first, so its appointments do not count.</summary>
    private static async Task<AppointmentCalendar?> CreateProbeCalendar(AppointmentStore store)
    {
        var own = await Step("FindAppointmentCalendarsAsync(read-write)",
            () => store.FindAppointmentCalendarsAsync(FindAppointmentCalendarsOptions.IncludeHidden));
        foreach (var leftover in own?.Where(c => c.DisplayName == ProbeCalendarName) ?? [])
        {
            Log($"leftover {ProbeCalendarName} calendar {leftover.LocalId}: deleting");
            await Step("AppointmentCalendar.DeleteAsync(leftover)", () => leftover.DeleteAsync());
        }

        var created = await Step($"CreateAppointmentCalendarAsync({ProbeCalendarName})",
            () => store.CreateAppointmentCalendarAsync(ProbeCalendarName));
        if (created is not null) Log($"OK created calendar {ProbeCalendarName}: {created.LocalId}");
        else Log($"FAIL CreateAppointmentCalendarAsync({ProbeCalendarName}): no calendar");
        return created;
    }

    /// <summary>Enables change tracking on <paramref name="store"/> and completes the returned task on its first StoreChanged.</summary>
    private static TaskCompletionSource<bool>? WatchStore(AppointmentStore store, string label)
    {
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            store.ChangeTracker.Enable();
            Log($"OK ChangeTracker.Enable ({label})");
        }
        catch (Exception ex)
        {
            LogFail($"ChangeTracker.Enable ({label})", ex);
        }

        try
        {
            store.StoreChanged += (_, _) =>
            {
                Log($"StoreChanged ({label})");
                fired.TrySetResult(true);
            };
            return fired;
        }
        catch (Exception ex)
        {
            LogFail($"StoreChanged subscribe ({label})", ex);
            return null;
        }
    }

    private static async Task<bool> WaitChanged(TaskCompletionSource<bool>? fired, string label)
    {
        if (fired is null) return false;
        try
        {
            await fired.Task.WaitAsync(StoreChangedTimeout);
            Log($"OK StoreChanged fired ({label})");
            return true;
        }
        catch (TimeoutException)
        {
            Log($"FAIL StoreChanged ({label}): not fired within {StoreChangedTimeout.TotalSeconds:0} s");
            return false;
        }
    }

    private static async Task<string?> SaveOneOff(AppointmentCalendar calendar, DateTimeOffset start)
    {
        Appointment appointment;
        try
        {
            appointment = new Appointment
            {
                Subject = OneOffSubject,
                StartTime = start,
                Duration = TimeSpan.FromMinutes(30),
                Uri = new Uri("https://meet.google.com/abc-defg-hij"),
                Location = "Room 1",
            };
        }
        catch (Exception ex)
        {
            LogFail("Appointment (one-off)", ex);
            return null;
        }

        return await Save(calendar, appointment, "one-off");
    }

    private static async Task<string?> SaveWeekly(AppointmentCalendar calendar, DateTimeOffset start)
    {
        Appointment appointment;
        try
        {
            appointment = new Appointment
            {
                Subject = WeeklySubject,
                StartTime = start,
                Duration = TimeSpan.FromMinutes(30),
                Recurrence = new AppointmentRecurrence
                {
                    Unit = AppointmentRecurrenceUnit.Weekly,
                    Interval = 1,
                    // Sunday = 1, Monday = 2 … Saturday = 64, as DayOfWeek counts from Sunday = 0.
                    DaysOfWeek = (AppointmentDaysOfWeek)(1 << (int)start.DayOfWeek),
                    Occurrences = WeeklyOccurrences,
                },
            };
        }
        catch (Exception ex)
        {
            LogFail("Appointment (weekly)", ex);
            return null;
        }

        return await Save(calendar, appointment, "weekly");
    }

    private static async Task<string?> Save(AppointmentCalendar calendar, Appointment appointment, string label)
    {
        if (!await Step($"SaveAppointmentAsync({label})", () => calendar.SaveAppointmentAsync(appointment))) return null;
        Log($"OK saved {label}: LocalId={appointment.LocalId} start={appointment.StartTime:O}");
        return string.IsNullOrEmpty(appointment.LocalId) ? null : appointment.LocalId;
    }

    /// <summary>The instances in <paramref name="store"/> over the window, each logged; null when the query failed.</summary>
    private static async Task<IReadOnlyList<Appointment>?> Find(AppointmentStore store, string label, DateTimeOffset start, TimeSpan length)
    {
        var options = new FindAppointmentsOptions { IncludeHidden = true };
        foreach (var property in new[]
                 {
                     AppointmentProperties.Subject, AppointmentProperties.StartTime, AppointmentProperties.Duration,
                     AppointmentProperties.AllDay, AppointmentProperties.Location, AppointmentProperties.Details,
                     AppointmentProperties.Uri, AppointmentProperties.OnlineMeetingLink, AppointmentProperties.Organizer,
                     AppointmentProperties.Invitees, AppointmentProperties.IsOrganizedByUser, AppointmentProperties.UserResponse,
                     AppointmentProperties.IsCanceledMeeting, AppointmentProperties.OriginalStartTime, AppointmentProperties.Recurrence,
                 })
        {
            options.FetchProperties.Add(property);
        }

        var found = await Step($"FindAppointmentsAsync({label}, {length.TotalDays:0} days)",
            () => store.FindAppointmentsAsync(start, length, options));
        if (found is null) return null;
        Log($"FindAppointmentsAsync({label}): {found.Count} instances");
        foreach (var appointment in found) LogAppointment(appointment);
        return found;
    }

    /// <summary>The probe calendar's instances with <paramref name="subject"/>, by start.</summary>
    private static List<Appointment> Instances(IReadOnlyList<Appointment>? found, AppointmentCalendar calendar, string subject) =>
        found?
            .Where(a => Read(() => a.CalendarId) == calendar.LocalId && Read(() => a.Subject) == subject)
            .OrderBy(InstanceStart)
            .ToList()
        ?? [];

    private static DateTimeOffset InstanceStart(Appointment appointment)
    {
        try
        {
            return appointment.OriginalStartTime ?? appointment.StartTime;
        }
        catch (Exception)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    /// <summary>Deletes <paramref name="instance"/> of the weekly series, then checks a re-query no longer has its start.</summary>
    private static async Task<bool> DeleteInstance(AppointmentStore store, AppointmentCalendar calendar, string seriesId,
        Appointment instance, DateTimeOffset start, TimeSpan length)
    {
        var instanceStart = InstanceStart(instance);
        if (!await Step($"DeleteAppointmentInstanceAsync({seriesId}, {instanceStart:O})",
                () => calendar.DeleteAppointmentInstanceAsync(seriesId, instanceStart)))
        {
            return false;
        }

        var after = Instances(await Find(store, "read-write after instance delete", start, length), calendar, WeeklySubject);
        var gone = after.All(a => InstanceStart(a) != instanceStart);
        Log($"{(gone ? "OK" : "FAIL")} instance {instanceStart:O} {(gone ? "gone" : "still there")}; {after.Count} weekly instances left");
        return gone;
    }

    private static void LogCalendar(AppointmentCalendar calendar) => Log(
        $"calendar DisplayName={Read(() => calendar.DisplayName)} SourceDisplayName={Read(() => calendar.SourceDisplayName)} "
        + $"LocalId={Read(() => calendar.LocalId)} UserDataAccountId={Read(() => calendar.UserDataAccountId)} "
        + $"CanCreateOrUpdateAppointments={Read(() => calendar.CanCreateOrUpdateAppointments)} IsHidden={Read(() => calendar.IsHidden)}");

    private static void LogAppointment(Appointment a) => Log(
        $"  instance Subject={Read(() => a.Subject)} StartTime={Read(() => a.StartTime.ToString("O", CultureInfo.InvariantCulture))} "
        + $"OriginalStartTime={Read(() => a.OriginalStartTime?.ToString("O", CultureInfo.InvariantCulture))} "
        + $"CalendarId={Read(() => a.CalendarId)} LocalId={Read(() => a.LocalId)} Uri={Read(() => a.Uri)} "
        + $"OnlineMeetingLink={Read(() => a.OnlineMeetingLink)} IsOrganizedByUser={Read(() => a.IsOrganizedByUser)} "
        + $"UserResponse={Read(() => a.UserResponse)} Invitees={Read(() => a.Invitees.Count)} "
        + $"Recurrence={Read(() => a.Recurrence is not null)}");

    /// <summary>One property, or its error: a property the query did not fetch may throw.</summary>
    private static string Read(Func<object?> get)
    {
        try
        {
            return Convert.ToString(get(), CultureInfo.InvariantCulture) ?? "null";
        }
        catch (Exception ex)
        {
            return $"<0x{ex.HResult:X8}>";
        }
    }

    /// <summary>Awaits one WinRT operation with a timeout; logs and returns default on failure.</summary>
    private static async Task<T?> Step<T>(string name, Func<IAsyncOperation<T>> start)
    {
        try
        {
            return await start().AsTask().WaitAsync(CallTimeout);
        }
        catch (Exception ex)
        {
            LogFail(name, ex);
            return default;
        }
    }

    /// <summary>Awaits one WinRT action with a timeout; logs and returns false on failure.</summary>
    private static async Task<bool> Step(string name, Func<IAsyncAction> start)
    {
        try
        {
            await start().AsTask().WaitAsync(CallTimeout);
            return true;
        }
        catch (Exception ex)
        {
            LogFail(name, ex);
            return false;
        }
    }

    private static void LogFail(string step, Exception ex) =>
        Log($"FAIL {step}: 0x{ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");

    private static void Log(string message)
    {
        var line = $"EchoDiar calendar-probe: {message}";
        Console.WriteLine(line);
        System.Diagnostics.Debug.WriteLine(line);
        if (_logPath is null) return;
        try
        {
            lock (LogLock) File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"EchoDiar calendar-probe: log write failed: {ex.Message}");
        }
    }
}

/// <summary>
/// DEBUG headless harness <c>--seed-event</c> (task 8.5): saves an event in EchoDiar's own calendar through the app's
/// <see cref="AppointmentCalendarStore"/>, so calendar triggers can be tested without accounts.
/// <c>--seed-event &lt;minutesFromNow&gt; &lt;durationMinutes&gt; [--link &lt;https url&gt;] [--invitee] [--title &lt;text&gt;] [--weekly]</c>
/// saves one (negative minutes start it in the past), then tags it (<c>Details</c>) and, with <c>--invitee</c>, adds
/// an invitee and marks it as not organized by the user, so the trust rule reads it as unconfirmed. The calendar id is
/// written to <c>calendar.ownCalendarID</c> when the setting differs. <c>--seed-event --clear</c> deletes every tagged
/// event of the own calendar. Each line goes to <c>Probes\seed-event-&lt;stamp&gt;.log</c>, stdout and the debugger.
/// Exit code 0, 1 when a step failed, 2 for invalid arguments (with the usage line).
/// </summary>
internal static class CalendarSeed
{
    private const string Flag = "--seed-event";
    private const string ClearFlag = "--clear";
    private const string OwnCalendarName = "EchoDiar";
    /// <summary>The tag in <c>Details</c> that <c>--clear</c> looks for; no link in it, so the trigger reads none.</summary>
    private const string SeedTag = "EchoDiar QA seed";
    private const string DefaultTitle = "QA seed";
    private const int MaxMinutes = 7 * 24 * 60;
    private const string Usage =
        "usage: echodiar.exe --seed-event <minutesFromNow> <durationMinutes> [--link <https url>] [--invitee] "
        + "[--title <text>] [--weekly] | echodiar.exe --seed-event --clear";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ClearRange = TimeSpan.FromDays(365);

    private static readonly object LogLock = new();
    private static string? _logPath;

    private sealed record Request(
        bool Clear, int MinutesFromNow = 0, int DurationMinutes = 0, string? Link = null, bool Invitee = false,
        string? Title = null, bool Weekly = false);

    /// <summary>Starts the seed when <paramref name="commandLine"/> has <c>--seed-event</c>, and calls <paramref name="exit"/> on this thread when it ends.</summary>
    public static bool TryRun(string[] commandLine, Action exit)
    {
        var index = Array.IndexOf(commandLine, Flag);
        if (index < 0) return false;

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(async () =>
        {
            var code = 1;
            try
            {
                OpenLog();
                if (Parse(commandLine[(index + 1)..]) is not { } request)
                {
                    Log($"invalid arguments: {string.Join(' ', commandLine[(index + 1)..])}");
                    Log(Usage);
                    code = 2;
                }
                else code = request.Clear ? await ClearAsync() : await SeedAsync(request);
            }
            catch (Exception ex)
            {
                Log($"seed failed: {ex}");
            }
            finally
            {
                dispatcher.TryEnqueue(() =>
                {
                    Environment.ExitCode = code;
                    exit();
                });
            }
        });
        return true;
    }

    /// <summary>The request, or null when the arguments are invalid (each flag at most once).</summary>
    private static Request? Parse(string[] args)
    {
        if (args.Length == 1 && args[0] == ClearFlag) return new Request(Clear: true);
        if (args.Length < 2
            || !int.TryParse(args[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var duration)
            || Math.Abs(minutes) > MaxMinutes || duration is < 1 or > MaxMinutes)
        {
            return null;
        }

        var request = new Request(Clear: false, minutes, duration);
        for (var i = 2; i < args.Length; i++)
        {
            var hasValue = i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1])
                && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            switch (args[i])
            {
                case "--invitee" when !request.Invitee:
                    request = request with { Invitee = true };
                    break;
                case "--weekly" when !request.Weekly:
                    request = request with { Weekly = true };
                    break;
                case "--link" when request.Link is null && hasValue:
                    request = request with { Link = args[++i] };
                    break;
                case "--title" when request.Title is null && hasValue:
                    request = request with { Title = args[++i] };
                    break;
                default:
                    return null;
            }
        }
        return request;
    }

    private static async Task<int> SeedAsync(Request request)
    {
        var now = DateTimeOffset.Now;
        var start = now.AddMinutes(request.MinutesFromNow);
        start = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerSecond));
        var end = start.AddMinutes(request.DurationMinutes);
        var draft = new CalendarEventDraft(request.Title ?? DefaultTitle, start, end, request.Link ?? "",
            request.Weekly ? CalendarEventDraft.Repeat.Weekly : CalendarEventDraft.Repeat.None);
        if (!draft.IsValid || (request.Link is not null && draft.Url is null))
        {
            Log("invalid event: a title is needed and the link must be https with a host");
            Log(Usage);
            return 2;
        }
        Log($"start {now:O}: seeding start={start:O} end={end:O} link={draft.Url?.ToString() ?? "none"} "
            + $"invitee={request.Invitee} weekly={request.Weekly}");

        var settingsStore = new SettingsStore(App.Paths);
        var settings = settingsStore.Load();
        var storedId = settings.Calendar.OwnCalendarID;

        // The app's own calendar before the save: the new appointment is the one not there yet.
        var writeStore = await Step("RequestStoreAsync(AppCalendarsReadWrite)",
            () => AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AppCalendarsReadWrite));
        if (writeStore is null) return Fail("no read-write calendar store");
        var window = (Start: start.AddMinutes(-1), Length: TimeSpan.FromMinutes(2));
        var before = new HashSet<string>(StringComparer.Ordinal);
        foreach (var calendar in await OwnCalendars(writeStore, storedId) ?? [])
        {
            var found = await Find(calendar, window.Start, window.Length);
            if (found is null) return Fail("own calendar unreadable before the save");
            before.UnionWith(found.Select(a => a.LocalId));
        }

        // The save the app's event form makes.
        var store = new AppointmentCalendarStore(message => Log($"store: {message}"));
        if (!await store.RequestAccessAsync()) return Fail("no calendar access (Windows Settings › Privacy › Calendar)");
        string calendarId;
        try
        {
            calendarId = await store.SaveEventAsync(draft, null, CalendarSpan.ThisEvent, storedId);
        }
        catch (CalendarStoreException ex)
        {
            return Fail($"SaveEventAsync: {ex.Kind} {ex.Message}");
        }
        Log($"OK SaveEventAsync: calendar {calendarId}");
        if (storedId != calendarId)
        {
            settingsStore.Save(settings with { Calendar = settings.Calendar with { OwnCalendarID = calendarId } });
            Log($"settings: calendar.ownCalendarID {storedId ?? "null"} -> {calendarId}");
        }

        // Tag it (and add the invitee) on the series itself, never on one instance.
        var own = await Step($"GetAppointmentCalendarAsync({calendarId})", () => writeStore.GetAppointmentCalendarAsync(calendarId));
        if (own is null) return Fail($"calendar {calendarId} not in the read-write store");
        var after = await Find(own, window.Start, window.Length);
        if (after is null) return Fail("own calendar unreadable after the save");
        var added = after.Select(a => a.LocalId).Where(id => !before.Contains(id)).Distinct().ToList();
        if (added.Count != 1) return Fail($"expected 1 new appointment, found {added.Count}: {string.Join(", ", added)}");
        var localId = added[0];
        var appointment = await Step($"GetAppointmentAsync({localId})", () => own.GetAppointmentAsync(localId));
        if (appointment is null) return Fail($"appointment {localId} not found");
        try
        {
            appointment.Details = SeedTag;
            if (request.Invitee)
            {
                appointment.IsOrganizedByUser = false;
                appointment.UserResponse = AppointmentParticipantResponse.None;
                appointment.Invitees.Add(new AppointmentInvitee
                {
                    DisplayName = "QA Invitee",
                    Address = "qa-invitee@example.com",
                    Role = AppointmentParticipantRole.RequiredAttendee,
                    Response = AppointmentParticipantResponse.None,
                });
            }
        }
        catch (Exception ex)
        {
            return Fail($"tag/invitee: 0x{ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
        }
        if (!await Step("SaveAppointmentAsync(tag)", () => own.SaveAppointmentAsync(appointment))) return Fail("tag save");

        // What the trigger will read, through the app's store code.
        var saved = await Step($"GetAppointmentAsync({localId}) read-back", () => own.GetAppointmentAsync(localId));
        if (saved is not null)
        {
            Log($"read-back IsOrganizedByUser={Read(() => saved.IsOrganizedByUser)} UserResponse={Read(() => saved.UserResponse)} "
                + $"Invitees={Read(() => saved.Invitees.Count)} "
                + $"Details={Read(() => saved.Details)} Recurrence={Read(() => saved.Recurrence is not null)}");
        }
        var events = await store.EventsAsync(start.AddMinutes(-1), end, calendarId);
        var seen = events.FirstOrDefault(e => e.Id.StartsWith(localId + "|", StringComparison.Ordinal));
        Log(seen is null
            ? "WARN EventsAsync does not list it yet"
            : $"EventsAsync: Id={seen.Id} Confirmed={seen.Confirmed} Link={seen.Link?.ToString() ?? "none"}");

        Log($"SEED-EVENT OK LocalId={localId} start={start:O} end={end:O} calendar={calendarId}");
        return 0;
    }

    /// <summary>Deletes every tagged appointment (a series with its master) of the own calendar; untagged ones and the calendar stay.</summary>
    private static async Task<int> ClearAsync()
    {
        var storedId = new SettingsStore(App.Paths).Load().Calendar.OwnCalendarID;
        var writeStore = await Step("RequestStoreAsync(AppCalendarsReadWrite)",
            () => AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AppCalendarsReadWrite));
        if (writeStore is null) return Fail("no read-write calendar store");
        var calendars = await OwnCalendars(writeStore, storedId);
        if (calendars is null) return Fail("own calendars unreadable");
        if (calendars.Count == 0)
        {
            Log("SEED-EVENT clear: no EchoDiar calendar, nothing to delete");
            return 0;
        }

        var deleted = 0;
        var failed = false;
        var now = DateTimeOffset.Now;
        foreach (var calendar in calendars)
        {
            var found = await Find(calendar, now - ClearRange, ClearRange * 2);
            if (found is null)
            {
                failed = true;
                continue;
            }
            var tagged = found
                .Where(a => Read(() => a.Details) == SeedTag)
                .Select(a => a.LocalId)
                .Distinct(StringComparer.Ordinal);
            foreach (var localId in tagged)
            {
                if (await Step($"DeleteAppointmentAsync({localId})", () => calendar.DeleteAppointmentAsync(localId)))
                {
                    deleted++;
                    Log($"deleted {localId} from calendar {calendar.LocalId}");
                }
                else failed = true;
            }
        }
        Log($"SEED-EVENT clear {(failed ? "FAIL" : "OK")} deleted={deleted}");
        return failed ? 1 : 0;
    }

    /// <summary>EchoDiar's calendar as the store resolves it: the stored id, else one named "EchoDiar"; null when unreadable.</summary>
    private static async Task<List<AppointmentCalendar>?> OwnCalendars(AppointmentStore store, string? storedId)
    {
        var calendars = await Step("FindAppointmentCalendarsAsync(read-write)",
            () => store.FindAppointmentCalendarsAsync(FindAppointmentCalendarsOptions.IncludeHidden));
        if (calendars is null) return null;
        var own = calendars.FirstOrDefault(c => storedId is not null && c.LocalId == storedId)
            ?? calendars.FirstOrDefault(c => c.DisplayName == OwnCalendarName);
        return own is null ? [] : [own];
    }

    private static async Task<IReadOnlyList<Appointment>?> Find(AppointmentCalendar calendar, DateTimeOffset start, TimeSpan length)
    {
        var options = new FindAppointmentsOptions { IncludeHidden = true };
        options.FetchProperties.Add(AppointmentProperties.Subject);
        options.FetchProperties.Add(AppointmentProperties.StartTime);
        options.FetchProperties.Add(AppointmentProperties.Details);
        return await Step($"FindAppointmentsAsync({calendar.LocalId}, {length.TotalMinutes:0} min)",
            () => calendar.FindAppointmentsAsync(start, length, options));
    }

    private static int Fail(string message)
    {
        Log($"SEED-EVENT FAIL {message}");
        return 1;
    }

    private static string Read(Func<object?> get)
    {
        try
        {
            return Convert.ToString(get(), CultureInfo.InvariantCulture) ?? "null";
        }
        catch (Exception ex)
        {
            return $"<0x{ex.HResult:X8}>";
        }
    }

    private static async Task<T?> Step<T>(string name, Func<IAsyncOperation<T>> start)
    {
        try
        {
            return await start().AsTask().WaitAsync(CallTimeout);
        }
        catch (Exception ex)
        {
            Log($"FAIL {name}: 0x{ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }

    private static async Task<bool> Step(string name, Func<IAsyncAction> start)
    {
        try
        {
            await start().AsTask().WaitAsync(CallTimeout);
            return true;
        }
        catch (Exception ex)
        {
            Log($"FAIL {name}: 0x{ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(App.Paths.Probes);
            _logPath = Path.Combine(App.Paths.Probes, $"seed-event-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"no log file: {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        var line = $"EchoDiar seed-event: {message}";
        Console.WriteLine(line);
        System.Diagnostics.Debug.WriteLine(line);
        if (_logPath is null) return;
        try
        {
            lock (LogLock) File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"EchoDiar seed-event: log write failed: {ex.Message}");
        }
    }
}
#endif
