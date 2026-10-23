using EchoDiar.Core;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// What the floating pill reads besides the recording state (macOS AppModel <c>sourceApp</c>, <c>autoStartedAppName</c>,
/// <c>pillPrompt</c>, <c>pillAppBundleID</c>) and the positions it saves.
/// </summary>
public sealed partial class AppViewModel
{
    private int? _recordingSourceRootPid;
    private string? _autoStartedAppName;
    private PillPrompt? _pillPrompt;

    /// <summary>The recorded app's root process from the start until the recording ends; null for all system audio.</summary>
    public int? RecordingSourceRootPid
    {
        get => _recordingSourceRootPid;
        private set
        {
            if (SetProperty(ref _recordingSourceRootPid, value)) OnPropertyChanged(nameof(PillAppRootPid));
        }
    }

    /// <summary>
    /// The app whose detection ("Always") started the recording: the pill shows "Recording &lt;App&gt;" for its first
    /// 10 s, even with the indicator off.
    /// </summary>
    public string? AutoStartedAppName
    {
        get => _autoStartedAppName;
        private set => SetProperty(ref _autoStartedAppName, value);
    }

    /// <summary>
    /// What the pill asks (meeting detected, meeting ended); null = no question. A prompt shows the pill whatever the
    /// settings.
    /// </summary>
    public PillPrompt? PillPrompt
    {
        get => _pillPrompt;
        private set
        {
            if (SetProperty(ref _pillPrompt, value)) OnPropertyChanged(nameof(PillAppRootPid));
        }
    }

    /// <summary>
    /// The process tree the pill follows with "Attach to meeting window": the detected app while asking, else the
    /// recorded one (macOS <c>pillAppBundleID</c>).
    /// </summary>
    public int? PillAppRootPid => PillPrompt is PillPrompt.MeetingDetected asking ? asking.Meeting.RootPid : RecordingSourceRootPid;

    /// <summary>Where the pill was dragged on the monitor <paramref name="monitorKey"/>, saved at once.</summary>
    /// <param name="quietly">See <see cref="SavePillSettings"/>.</param>
    internal void SavePillPosition(string monitorKey, PillPosition position, bool quietly = false)
    {
        if (_closing || Settings.PillPositions.GetValueOrDefault(monitorKey) == position) return;
        SavePillSettings(Settings with { PillPositions = new(Settings.PillPositions) { [monitorKey] = position } }, quietly);
    }

    /// <summary>Where the pill was dragged from the meeting window's top-right corner, saved at once.</summary>
    /// <param name="quietly">See <see cref="SavePillSettings"/>.</param>
    internal void SavePillWindowOffset(PillPosition? offset, bool quietly = false)
    {
        if (_closing || Settings.PillWindowOffset == offset) return;
        SavePillSettings(Settings with { PillWindowOffset = offset }, quietly);
    }

    /// <summary>
    /// Saves a drag. <paramref name="quietly"/> (the main window is closing): written without raising
    /// <see cref="Settings"/>'s change, which the closed window's views would answer by touching their controls; the
    /// quit still waits for the write.
    /// </summary>
    private void SavePillSettings(AppSettings settings, bool quietly)
    {
        if (!quietly)
        {
            SaveSettings(settings);
            return;
        }
        _settings = settings;
        QueueSettingsWrite();
    }
}

/// <summary>A question the floating pill asks (macOS <c>AppModel.PillPrompt</c>).</summary>
public abstract record PillPrompt
{
    private PillPrompt() { }

    /// <summary>"&lt;App&gt; call detected — Record?".</summary>
    public sealed record MeetingDetected(DetectedMeeting Meeting) : PillPrompt;

    /// <summary>"… — stop recording?": the recording stops at <paramref name="Deadline"/> unless "Keep recording".</summary>
    public sealed record MeetingEnded(MeetingEndMonitor.Reason Reason, DateTimeOffset Deadline) : PillPrompt;
}
