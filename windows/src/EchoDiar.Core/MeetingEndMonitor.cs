using EchoDiar.Core.Settings;

namespace EchoDiar.Core;

/// <summary>
/// Decides when a recording should end (port of Swift <c>MeetingEndMonitor</c>). Pure: the caller feeds timestamped
/// inputs with <see cref="Record"/> and asks <see cref="Evaluate"/> what to do; nothing here reads the clock.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>Max duration (wall clock from <see cref="StartedAt"/>) always stops, before any snooze is consulted, so a
/// recording cannot run forever unattended.</item>
/// <item>System sleep and disk full stop at once.</item>
/// <item>Source app quit, source input and output inactive, both channels silent, screen locked, input route lost and
/// the calendar event's scheduled end reached prompt with a countdown (default action Stop). "Keep recording" snoozes
/// that trigger for <see cref="SnoozeDuration"/>; a condition that still holds afterwards prompts again.</item>
/// <item>Source inactivity is ignored once the source app quit (its own prompt covers it).</item>
/// <item>Call ended: the system channel went silent after it had been audible while the meeting app's mic is off,
/// both for <c>callEndedSeconds</c>. Either alone is normal mid-call (others quiet, a muted web client); also ignored
/// once the source app quit. Paused time does not count: a pause measures nothing.</item>
/// <item>The recording starts as silent: the silence prompt fires unless <c>Audible(true)</c> arrives within the
/// threshold.</item>
/// <item><see cref="Evaluate"/> returns the same prompt on every call while its condition holds; the caller dedupes,
/// and dismisses the prompt when the result drops back to <see cref="Decision.None"/>.</item>
/// </list>
/// </remarks>
public sealed class MeetingEndMonitor
{
    public const int PromptCountdown = 15;
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromMinutes(10);

    public abstract record Input
    {
        private Input() { }

        /// <summary>The source (meeting) app's processes are gone.</summary>
        public sealed record SourceExited : Input;
        /// <summary>The source app's input or output is running (<c>false</c>: both stopped).</summary>
        public sealed record SourceActive(bool Active) : Input;
        /// <summary>The mic or system channel is above the silence floor (<c>false</c>: both silent).</summary>
        public sealed record Audible(bool IsAudible) : Input;
        /// <summary>The system channel alone is above the silence floor.</summary>
        public sealed record SystemAudible(bool IsAudible) : Input;
        /// <summary>The meeting app's mic input is running.</summary>
        public sealed record SourceInputActive(bool Active) : Input;
        /// <summary>The recording is paused (<c>false</c>: resumed).</summary>
        public sealed record Paused(bool IsPaused) : Input;
        public sealed record ScreenLocked(bool Locked) : Input;
        /// <summary>The input device went away; fed only when the route-change setting asks to stop.</summary>
        public sealed record InputRouteLost : Input;
        public sealed record SystemSleep : Input;
        public sealed record DiskFull : Input;
        /// <summary>The calendar event being recorded ends at <paramref name="At"/>; null clears it.</summary>
        public sealed record ScheduledEnd(DateTimeOffset? At) : Input;
    }

    public enum Reason
    {
        SourceExited, SourceInactive, CallEnded, Silence, ScreenLocked, InputRouteLost, SystemSleep, DiskFull, MaxDuration,
        EventEnded,
    }

    /// <summary>What <see cref="Evaluate"/> says to do (Swift <c>Action</c>).</summary>
    public abstract record Decision
    {
        private Decision() { }

        public static Decision None { get; } = new NoneDecision();

        public sealed record NoneDecision : Decision;
        /// <summary>Ask, then stop when <paramref name="Countdown"/> seconds pass without "Keep recording".</summary>
        public sealed record PromptStop(Reason Reason, int Countdown) : Decision;
        public sealed record StopNow(Reason Reason) : Decision;
    }

    private bool _sourceExited;
    private DateTimeOffset? _sourceInactiveSince;
    private DateTimeOffset? _silentSince;
    private bool _systemHeard;
    private DateTimeOffset? _systemSilentSince;
    private DateTimeOffset? _sourceInputInactiveSince;
    private bool _paused;
    private bool _screenLocked;
    private bool _inputRouteLost;
    private DateTimeOffset? _scheduledEnd;
    private Reason? _stopReason;
    private readonly Dictionary<Reason, DateTimeOffset> _snoozedUntil = [];

    public MeetingEndMonitor(MeetingEndMonitorConfiguration configuration, DateTimeOffset startedAt)
    {
        Configuration = configuration;
        StartedAt = startedAt;
        _silentSince = startedAt;
    }

    public MeetingEndMonitor(DateTimeOffset startedAt) : this(new MeetingEndMonitorConfiguration(), startedAt) { }

    public MeetingEndMonitorConfiguration Configuration { get; }
    public DateTimeOffset StartedAt { get; }

    public void Record(Input input, DateTimeOffset at)
    {
        switch (input)
        {
            case Input.SourceExited: _sourceExited = true; break;
            case Input.SourceActive(var active): _sourceInactiveSince = active ? null : _sourceInactiveSince ?? at; break;
            case Input.Audible(var audible): _silentSince = audible ? null : _silentSince ?? at; break;
            case Input.SystemAudible(var audible):
                _systemHeard = _systemHeard || audible;
                _systemSilentSince = audible || !_systemHeard ? null : _systemSilentSince ?? at;
                break;
            case Input.SourceInputActive(var active):
                _sourceInputInactiveSince = active ? null : _sourceInputInactiveSince ?? at;
                break;
            case Input.Paused(var isPaused):
                _paused = isPaused;
                // Silence still running at resume counts from there.
                if (!isPaused && _systemSilentSince is not null) _systemSilentSince = at;
                break;
            case Input.ScreenLocked(var locked): _screenLocked = locked; break;
            case Input.InputRouteLost: if (!IsSnoozed(Reason.InputRouteLost, at)) _inputRouteLost = true; break;
            case Input.SystemSleep: _stopReason ??= Reason.SystemSleep; break;
            case Input.DiskFull: _stopReason ??= Reason.DiskFull; break;
            case Input.ScheduledEnd(var end): _scheduledEnd = end; break;
        }
    }

    /// <summary>"Keep recording" on the prompt for <paramref name="reason"/>: that trigger stays quiet for <see cref="SnoozeDuration"/>.</summary>
    public void KeepRecording(Reason reason, DateTimeOffset now)
    {
        _snoozedUntil[reason] = now + SnoozeDuration;
        if (reason == Reason.InputRouteLost) _inputRouteLost = false;
    }

    public Decision Evaluate(DateTimeOffset now)
    {
        // Clamped again at use: a configuration built in code is not clamped (only a read one is).
        var c = Configuration;
        var maxDuration = TimeSpan.FromMinutes(Effective(c.MaxDurationMinutes, MeetingEndMonitorConfiguration.MaxDurationRange));
        if (now - StartedAt >= maxDuration) return new Decision.StopNow(Reason.MaxDuration);
        if (_stopReason is { } stopReason) return new Decision.StopNow(stopReason);

        var inactiveThreshold = TimeSpan.FromSeconds(Effective(c.SourceInactiveSeconds, MeetingEndMonitorConfiguration.SourceInactiveRange));
        var callEndedThreshold = TimeSpan.FromSeconds(Effective(c.CallEndedSeconds, MeetingEndMonitorConfiguration.CallEndedRange));
        var silenceThreshold = TimeSpan.FromMinutes(Effective(c.SilenceMinutes, MeetingEndMonitorConfiguration.SilenceRange));
        (Reason Reason, bool Fired)[] triggers =
        [
            (Reason.SourceExited, _sourceExited),
            (Reason.InputRouteLost, _inputRouteLost),
            (Reason.ScreenLocked, _screenLocked),
            (Reason.SourceInactive, !_sourceExited && Held(_sourceInactiveSince, inactiveThreshold, now)),
            (Reason.CallEnded, !_sourceExited && !_paused && Held(CallEndedSince, callEndedThreshold, now)),
            (Reason.Silence, Held(_silentSince, silenceThreshold, now)),
            (Reason.EventEnded, Held(_scheduledEnd, TimeSpan.Zero, now)),
        ];
        foreach (var (reason, fired) in triggers)
        {
            if (fired && !IsSnoozed(reason, now)) return new Decision.PromptStop(reason, PromptCountdown);
        }
        return Decision.None;
    }

    /// <summary>When both call-ended conditions started holding.</summary>
    private DateTimeOffset? CallEndedSince =>
        _systemSilentSince is { } systemSilent && _sourceInputInactiveSince is { } inputInactive
            ? (systemSilent > inputInactive ? systemSilent : inputInactive)
            : null;

    private bool IsSnoozed(Reason reason, DateTimeOffset at) =>
        _snoozedUntil.TryGetValue(reason, out var until) && at < until;

    private static bool Held(DateTimeOffset? since, TimeSpan threshold, DateTimeOffset now) =>
        since is { } start && now - start >= threshold;

    private static int Effective(int value, (int Min, int Max) range) => AppSettings.Clamp(value, range);
}
