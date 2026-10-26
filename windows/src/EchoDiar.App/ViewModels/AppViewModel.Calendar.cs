using System.Diagnostics;
using EchoDiar.Core;
using EchoDiar.Core.Calendar;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using EchoDiar.Platform.Calendar;
using Microsoft.UI.Dispatching;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// Calendar triggers (macOS AppModel <c>syncCalendar</c>, <c>handleCalendar</c>, the calendar prompt's actions): reads
/// the next 7 days of events, publishes them, acts on a due event per the mode, and names a recording started for an
/// event after it and stops it at its end. Syncs at launch, on calendar changes (1 s debounce), on wake, on calendar
/// settings changes and at each event start or end (one one-shot timer, 30 min at most); nothing polls. Event titles and
/// links come from invites: only occurrence ids are logged. UI thread only.
/// </summary>
public sealed partial class AppViewModel
{
    /// <summary>The query starts this long before now, so an event already in progress is found (the Appointments API
    /// returns the occurrences starting in the range).</summary>
    private static readonly TimeSpan CalendarLookback = TimeSpan.FromDays(1);

    private static readonly TimeSpan CalendarHorizon = TimeSpan.FromDays(7);

    /// <summary>The longest wait of the sync timer, so a change the store did not report is seen.</summary>
    private static readonly TimeSpan CalendarMaxWait = TimeSpan.FromMinutes(30);

    /// <summary>Past the boundary, so an event starting then is due when the timer fires.</summary>
    private static readonly TimeSpan CalendarTimerSlack = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan CalendarChangeDebounce = TimeSpan.FromSeconds(1);

    private const int UpcomingLimit = 50;

    private readonly CalendarScheduler _calendarScheduler = new();

    /// <summary>One-shot: the next event start or end (30 min at most). Null until <see cref="StartCalendar"/>.</summary>
    private DispatcherQueueTimer? _calendarTimer;

    /// <summary>One-shot: 1 s after the store's last change.</summary>
    private DispatcherQueueTimer? _calendarChangeTimer;

    private bool _calendarSyncing;
    private bool _calendarSyncAgain;

    /// <summary>The event the current recording was started for: its end prompts a stop, its title names it.</summary>
    private CalendarEvent? _recordingEvent;

    private CalendarAccess _calendarAccess = CalendarAccess.NotDetermined;
    private IReadOnlyList<CalendarInfo> _calendars = [];
    private IReadOnlyList<CalendarEvent> _upcomingCalendarEvents = [];
    private CalendarEvent? _activeCalendarEvent;
    private CalendarPrompt? _pendingCalendarPrompt;
    private CalendarEvent? _calendarWaitEvent;

    /// <summary>Calendar access as last read.</summary>
    public CalendarAccess CalendarAccess
    {
        get => _calendarAccess;
        private set => SetProperty(ref _calendarAccess, value);
    }

    /// <summary>The calendars by account then title, for the watched calendars list; empty without access.</summary>
    public IReadOnlyList<CalendarInfo> Calendars
    {
        get => _calendars;
        private set
        {
            if (!_calendars.SequenceEqual(value)) SetProperty(ref _calendars, value);
        }
    }

    /// <summary>Eligible events not over yet (next 7 days), by start; empty without access.</summary>
    public IReadOnlyList<CalendarEvent> UpcomingCalendarEvents
    {
        get => _upcomingCalendarEvents;
        private set
        {
            if (!_upcomingCalendarEvents.SequenceEqual(value)) SetProperty(ref _upcomingCalendarEvents, value);
        }
    }

    /// <summary>The eligible event in progress (the latest started) at the last sync, whatever the mode.</summary>
    public CalendarEvent? ActiveCalendarEvent
    {
        get => _activeCalendarEvent;
        private set => SetProperty(ref _activeCalendarEvent, value);
    }

    /// <summary>
    /// The calendar event the pill should ask about ("&lt;title&gt; is starting": Record / Join / Not now); null = none.
    /// Set with <see cref="CalendarPromptRequested"/>; cleared by its answers, by any recording start, and when the
    /// event ended, moved, was deleted or skipped.
    /// </summary>
    public CalendarPrompt? PendingCalendarPrompt
    {
        get => _pendingCalendarPrompt;
        private set
        {
            if (SetProperty(ref _pendingCalendarPrompt, value)) SyncCalendarPillPrompt(value);
        }
    }

    /// <summary>A due event asks (Ask mode, an unconfirmed or late event, Wait for the call without detection). UI thread.</summary>
    public event Action<CalendarPrompt>? CalendarPromptRequested;

    /// <summary>
    /// The "Wait for the call" event in progress: the first meeting app detected (not set to Ignore) starts recording it
    /// with this event (<see cref="TakeCalendarWaitEvent"/>). Cleared by any recording start, and when the event is no
    /// longer active or the mode changed.
    /// </summary>
    public CalendarEvent? CalendarWaitEvent
    {
        get => _calendarWaitEvent;
        private set => SetProperty(ref _calendarWaitEvent, value);
    }

    /// <summary>
    /// "Record" on the calendar prompt: the detected meeting app if one is detected, else all system audio, with the
    /// event's title and auto-stop (macOS <c>recordCalendarEvent</c>).
    /// </summary>
    public void RecordCalendarEvent(CalendarEvent @event)
    {
        if (_closing) return;
        DismissCalendarPrompt(@event);
        if (IsRecording || IsStarting) return;
        _ = StartRecordingAsync(RecordableDetectedMeeting()?.Group, @event);
    }

    /// <summary>
    /// "Join" on the calendar prompt, a user's click only: opens the meeting link, then records all system audio (no
    /// call app yet) with the event's title and auto-stop (macOS <c>joinCalendarEvent</c>).
    /// </summary>
    public void JoinCalendarEvent(CalendarEvent @event)
    {
        if (_closing) return;
        DismissCalendarPrompt(@event);
        OpenCalendarEventLink(@event);
        if (IsRecording || IsStarting) return;
        _ = StartRecordingAsync(null, @event);
    }

    /// <summary>"Not now" on the calendar prompt: the event stays triggered (in memory), so it does not ask again until a relaunch.</summary>
    public void NotNowCalendarEvent(CalendarEvent @event)
    {
        if (!_closing) DismissCalendarPrompt(@event);
    }

    /// <summary>
    /// The per-event Skip switch, saved until the event ends (then pruned). The settings change syncs the calendar,
    /// which drops the event's prompt or wait.
    /// </summary>
    public void SkipCalendarEvent(CalendarEvent @event, bool skipped)
    {
        if (_closing) return;
        var current = Settings.Calendar.SkippedEvents;
        if (skipped ? current.TryGetValue(@event.Id, out var end) && end == @event.End : !current.ContainsKey(@event.Id)) return;
        var skippedEvents = new Dictionary<string, DateTimeOffset>(current, StringComparer.Ordinal);
        if (skipped) skippedEvents[@event.Id] = @event.End;
        else skippedEvents.Remove(@event.Id);
        SaveSettings(Settings with { Calendar = Settings.Calendar with { SkippedEvents = skippedEvents } });
    }

    /// <summary>
    /// Opens the event's meeting link on a user's click: only its allowlisted https <see cref="MeetingLink"/>, never
    /// another URL from the invite.
    /// </summary>
    public void OpenCalendarEventLink(CalendarEvent @event)
    {
        if (_closing || @event.Link?.Url is not { Scheme: "https" } url) return;
        _ = LaunchMeetingLinkAsync(url);
    }

    /// <summary>"Grant access", and back from Windows Settings: asks for access again when it is not granted, then syncs.</summary>
    public void RefreshCalendar() => RequestCalendarSync();

    /// <summary>The editor's values for an event of EchoDiar's calendar, and whether it repeats; null when it is not found.</summary>
    public Task<(CalendarEventDraft Draft, bool IsRecurring)?> CalendarDraftAsync(CalendarEvent @event) =>
        _services.Calendar.DraftAsync(@event, Settings.Calendar.OwnCalendarID);

    /// <summary>
    /// Saves a new event, or edits an occurrence, in EchoDiar's calendar (macOS <c>saveCalendarEvent</c>). Returns an
    /// error message to show, or null. The calendar's id is stored when it changed, or own events count as unconfirmed.
    /// </summary>
    public async Task<string?> SaveCalendarEventAsync(CalendarEventDraft draft, CalendarEvent? editing, CalendarSpan span)
    {
        try
        {
            var id = await _services.Calendar.SaveEventAsync(draft, editing, span, Settings.Calendar.OwnCalendarID);
            if (_closing) return null;
            // A new id syncs through the settings change (it changes eligibility); the same id syncs here.
            if (id != Settings.Calendar.OwnCalendarID) SaveSettings(Settings with { Calendar = Settings.Calendar with { OwnCalendarID = id } });
            else RefreshCalendar();
            return null;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not save the calendar event: {e.Message}");
            return CalendarErrorMessage(e);
        }
    }

    /// <summary>Deletes an occurrence (or the series from it, per <paramref name="span"/>) of EchoDiar's calendar. Returns an
    /// error message to show, or null.</summary>
    public async Task<string?> DeleteCalendarEventAsync(CalendarEvent @event, CalendarSpan span)
    {
        try
        {
            await _services.Calendar.DeleteEventAsync(@event, span, Settings.Calendar.OwnCalendarID);
            RefreshCalendar();
            return null;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not delete the calendar event: {e.Message}");
            return CalendarErrorMessage(e);
        }
    }

    /// <summary>A store failure as the macOS <c>CalendarStoreError</c> texts; the exception's own message is for logs.</summary>
    private static string CalendarErrorMessage(Exception e) => (e as CalendarStoreException)?.Kind switch
    {
        CalendarStoreError.NoAccess => Loc.T("EchoDiar has no access to your calendars."),
        CalendarStoreError.EventNotFound => Loc.T("The event no longer exists."),
        CalendarStoreError.NotOwnEvent => Loc.T("Only events in the EchoDiar calendar can be changed."),
        _ => Loc.T("The calendar could not be changed. Try again later."),
    };

    /// <summary>Starts the calendar sync once the settings are loaded and the system events exist (after <see cref="StartMeetingServices"/>).</summary>
    private void StartCalendar()
    {
        if (_closing || _calendarTimer is not null) return;
        _calendarTimer = _dispatcher.CreateTimer();
        _calendarTimer.IsRepeating = false;
        _calendarTimer.Tick += (_, _) => RequestCalendarSync();
        _calendarChangeTimer = _dispatcher.CreateTimer();
        _calendarChangeTimer.Interval = CalendarChangeDebounce;
        _calendarChangeTimer.IsRepeating = false;
        _calendarChangeTimer.Tick += (_, _) => RequestCalendarSync();
        _services.Calendar.Changed += OnCalendarStoreChanged;
        if (_systemEvents is { } events) events.SystemResumed += RequestCalendarSync;
        RequestCalendarSync();
    }

    /// <summary>At quit, before the system events go: no timer fires and no store change is posted any more.</summary>
    private void StopCalendar()
    {
        _services.Calendar.Changed -= OnCalendarStoreChanged;
        if (_systemEvents is { } events) events.SystemResumed -= RequestCalendarSync;
        _calendarTimer?.Stop();
        _calendarChangeTimer?.Stop();
    }

    /// <summary>The store changed (any thread; EchoDiar's own saves too): syncs 1 s after the last change.</summary>
    private void OnCalendarStoreChanged() => Post(() =>
    {
        if (_calendarChangeTimer is not { } timer) return;
        timer.Stop();
        timer.Start();
    });

    /// <summary>A sync now, or right after the one running; nothing before <see cref="StartCalendar"/>.</summary>
    private void RequestCalendarSync()
    {
        if (_closing || _calendarTimer is null) return;
        _ = SyncCalendarAsync();
    }

    /// <summary>Calendar settings changed (the Settings setter): re-feeds the event end of the recording, and syncs
    /// when what triggers changed (macOS <c>updateSettings</c>' <c>calendarChanged</c>).</summary>
    private void OnCalendarSettingsChanged(CalendarScheduler.Configuration old, CalendarScheduler.Configuration current)
    {
        if (ReferenceEquals(old, current)) return;
        if (old.StopAtEventEnd != current.StopAtEventEnd || old.EffectiveStopAfterEndMinutes != current.EffectiveStopAfterEndMinutes)
        {
            FeedEventEnd();
        }
        if (old.Mode != current.Mode || old.AllTimedEvents != current.AllTimedEvents || old.OwnCalendarID != current.OwnCalendarID
            || !old.IgnoredCalendarIDs.SequenceEqual(current.IgnoredCalendarIDs)
            || old.SkippedEvents.Count != current.SkippedEvents.Count
            || old.SkippedEvents.Any(p => !current.SkippedEvents.TryGetValue(p.Key, out var end) || end != p.Value))
        {
            RequestCalendarSync();
        }
    }

    /// <summary>Runs syncs one at a time: a request while one runs runs again after it, with the latest settings.</summary>
    private async Task SyncCalendarAsync()
    {
        if (_calendarSyncing)
        {
            _calendarSyncAgain = true;
            return;
        }
        _calendarSyncing = true;
        try
        {
            do
            {
                _calendarSyncAgain = false;
                try
                {
                    await SyncCalendarOnceAsync();
                }
                catch (Exception e)
                {
                    // A failure in a dispatched continuation would end the app.
                    Debug.WriteLine($"EchoDiar: calendar sync failed: {e.GetType().Name} {e.Message}");
                    if (!_closing) ArmCalendarTimer(DateTimeOffset.Now);
                }
            }
            while (_calendarSyncAgain && !_closing);
        }
        finally
        {
            _calendarSyncing = false;
        }
    }

    /// <summary>
    /// Reads access, the calendars and the events from 1 day ago to 7 days ahead, prunes ended skips, publishes the
    /// upcoming events, drops a prompt or wait whose event is no longer active, acts on a due event and re-arms the timer
    /// (macOS <c>syncCalendar</c>). Store calls run off the UI thread.
    /// </summary>
    private async Task SyncCalendarOnceAsync()
    {
        var store = _services.Calendar;
        // Windows asks no consent: reopening the store is cheap, and finds access turned on in Windows Settings since.
        if (store.Access != CalendarAccess.Granted) await store.RequestAccessAsync();
        if (_closing) return;
        CalendarAccess = store.Access;
        if (await ReadCalendarAsync(store.CalendarsAsync, "calendars") is { } calendars) Calendars = calendars;
        if (_closing) return;

        var now = DateTimeOffset.Now;
        var config = Settings.Calendar;
        var pruned = config.Pruned(now);
        // Pruning only removes: compared by count, as the copy's collections are new instances.
        if (pruned.SkippedEvents.Count != config.SkippedEvents.Count)
        {
            // The settings change asks for a sync, which runs right after this one with nothing left to prune.
            SaveSettings(Settings with { Calendar = pruned });
            return;
        }
        var ownCalendarId = config.OwnCalendarID;
        var events = await ReadCalendarAsync(
            () => store.EventsAsync(now - CalendarLookback, now + CalendarHorizon, ownCalendarId), "events");
        if (_closing) return;
        CalendarAccess = store.Access;

        now = DateTimeOffset.Now;
        config = Settings.Calendar;
        // A failed read keeps the events of the last sync.
        if (events is not null) _calendarScheduler.Update(events, config);
        UpcomingCalendarEvents = _calendarScheduler.Upcoming(now, UpcomingLimit);
        ActiveCalendarEvent = _calendarScheduler.ActiveEvent(now);
        // Ended, deleted, moved or skipped since: drop its prompt and wait (and the wait in another mode).
        bool IsActive(CalendarEvent @event) =>
            UpcomingCalendarEvents.Any(e => e.Id == @event.Id && e.Start <= now) && !config.SkippedEvents.ContainsKey(@event.Id);
        if (PendingCalendarPrompt is { } shown && !IsActive(shown.Event)) PendingCalendarPrompt = null;
        if (CalendarWaitEvent is { } waiting && (!IsActive(waiting) || config.Mode != CalendarScheduler.Mode.WaitForCall))
        {
            CalendarWaitEvent = null;
        }
        HandleCalendar(_calendarScheduler.Evaluate(now));
        ArmCalendarTimer(now);
    }

    /// <summary>A store read; null (logged) when it failed.</summary>
    private static async Task<IReadOnlyList<T>?> ReadCalendarAsync<T>(Func<Task<IReadOnlyList<T>>> read, string what)
    {
        try
        {
            return await read();
        }
        catch (Exception e)
        {
            // The store's messages never hold event content.
            Debug.WriteLine($"EchoDiar: could not read the {what}: {e.Message}");
            return null;
        }
    }

    /// <summary>The next event start or end plus 1 s, 30 min at most; nothing while the mode is Off and no event is listed.</summary>
    private void ArmCalendarTimer(DateTimeOffset now)
    {
        if (_calendarTimer is not { } timer) return;
        timer.Stop();
        var next = _calendarScheduler.NextChange(now);
        if (next is null && Settings.Calendar.Mode == CalendarScheduler.Mode.Off) return;
        var wait = next is { } at && at - now < CalendarMaxWait ? at - now : CalendarMaxWait;
        timer.Interval = wait + CalendarTimerSlack;
        timer.Start();
    }

    /// <summary>
    /// Acts on a due event per the mode; skipped while recording (the scheduler marked it triggered). Asks for Ask, an
    /// unconfirmed or late event, and Wait for the call with meeting detection off; Record at start starts all system
    /// audio; Wait for the call records a meeting app already detected, else waits for one (macOS <c>handleCalendar</c>).
    /// Logs the occurrence id only: titles come from invites.
    /// </summary>
    private void HandleCalendar(CalendarScheduler.Action action)
    {
        if (action.Event is not { } @event) return;
        if (IsRecording || IsStarting)
        {
            Debug.WriteLine($"EchoDiar: calendar event {@event.Id} skipped: already recording");
            return;
        }
        var mode = Settings.Calendar.Mode;
        if (mode == CalendarScheduler.Mode.Off) return;
        if (CalendarScheduler.ShouldAsk(mode, @event, action.Late)
            || (mode == CalendarScheduler.Mode.WaitForCall && !Settings.MeetingDetection))
        {
            var prompt = new CalendarPrompt(@event, action.Late);
            PendingCalendarPrompt = prompt;
            CalendarPromptRequested?.Invoke(prompt);
            return;
        }
        if (mode == CalendarScheduler.Mode.RecordAtStart)
        {
            AutoStartedAppName = @event.Title;
            _ = StartRecordingAsync(null, @event);
        }
        else if (RecordableDetectedMeeting() is { } meeting)
        {
            // Its mic was already on: the detector will not report it again.
            AutoStartedAppName = @event.Title;
            _ = StartRecordingAsync(meeting.Group, @event);
        }
        else
        {
            CalendarWaitEvent = @event;
        }
    }

    /// <summary>
    /// For the detector (<see cref="OnMeetingDetected"/>): the "Wait for the call" event still in progress, taken (cleared) so only the first
    /// detection starts a recording; null when none or it has ended.
    /// </summary>
    private CalendarEvent? TakeCalendarWaitEvent()
    {
        if (CalendarWaitEvent is not { } waiting) return null;
        CalendarWaitEvent = null;
        return DateTimeOffset.Now < waiting.End ? waiting : null;
    }

    /// <summary>The detected meeting app whose mic is still on, unless set to Ignore since.</summary>
    private DetectedMeeting? RecordableDetectedMeeting()
    {
        if (_detectedMeeting is not { } meeting
            || Settings.PerAppBehavior.GetValueOrDefault(meeting.App.AppExe, MeetingBehavior.Ask) == MeetingBehavior.Ignore)
        {
            return null;
        }
        lock (_raisedMeetings) return _raisedMeetings.Contains(meeting.Exe) ? meeting : null;
    }

    private void DismissCalendarPrompt(CalendarEvent @event)
    {
        if (PendingCalendarPrompt?.Event.Id == @event.Id) PendingCalendarPrompt = null;
    }

    /// <summary>
    /// A recording started and its end watch is armed (after <see cref="BeginEndWatch"/>): any recording handles the
    /// calendar event (no prompt, no later start from its wait); <paramref name="event"/> (the event it records, or
    /// null) names it at the end and feeds its end to the end monitor.
    /// </summary>
    private void CalendarRecordingStarted(CalendarEvent? @event)
    {
        if (_endWatch is null) return;
        PendingCalendarPrompt = null;
        CalendarWaitEvent = null;
        _recordingEvent = @event;
        FeedEventEnd();
    }

    /// <summary>The recording ended: the event it recorded, whose title names it; cleared.</summary>
    private CalendarEvent? TakeRecordingEvent()
    {
        var @event = _recordingEvent;
        _recordingEvent = null;
        return @event;
    }

    /// <summary>The recorded event's end plus "Stop after", with "Stop when the event ends"; none without (macOS beginEndWatch).</summary>
    private void FeedEventEnd()
    {
        if (_recordingEvent is not { } @event) return;
        var config = Settings.Calendar;
        DateTimeOffset? end = config.StopAtEventEnd ? @event.End.AddMinutes(config.EffectiveStopAfterEndMinutes) : null;
        RecordEnd(new MeetingEndMonitor.Input.ScheduledEnd(end));
    }

    /// <summary>
    /// Names the finished recording after its event (macOS <c>finishRecording</c>'s <c>title</c>), before its jobs, which
    /// keep the title. The store cleaned the title (control and bidi characters removed, at most 200 characters).
    /// </summary>
    private async Task ApplyEventTitleAsync(Guid id, string title)
    {
        try
        {
            await _services.Library.RenameAsync(id, title);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not rename conversation {id}: {e.GetType().Name}");
        }
    }

    private static async Task LaunchMeetingLinkAsync(Uri url)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(url)) Debug.WriteLine("EchoDiar: the meeting link did not open.");
        }
        catch (Exception e)
        {
            // Not the link: it comes from an invite.
            Debug.WriteLine($"EchoDiar: could not open the meeting link: 0x{e.HResult:X8}");
        }
    }
}

/// <summary>A calendar event the pill asks about; <paramref name="Late"/>: more than 5 min past its start.</summary>
public sealed record CalendarPrompt(CalendarEvent Event, bool Late);
