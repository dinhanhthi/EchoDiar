using EchoDiar.Core.Settings;

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
    /// The app whose detection started the recording: the pill shows "Recording &lt;App&gt;" for its first 10 s, even
    /// with the indicator off. Set by meeting detection (Phase 20); null until then.
    /// </summary>
    public string? AutoStartedAppName
    {
        get => _autoStartedAppName;
        private set => SetProperty(ref _autoStartedAppName, value);
    }

    /// <summary>
    /// What the pill asks (meeting detected, meeting ended); null = no question. A prompt shows the pill whatever the
    /// settings. Set by meeting detection (Phase 20); null until then.
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
    /// The process tree the pill follows with "Attach to meeting window": the recorded app (Phase 20: the detected app
    /// while asking).
    /// </summary>
    public int? PillAppRootPid => RecordingSourceRootPid;

    /// <summary>Where the pill was dragged on the monitor <paramref name="monitorKey"/>, saved at once.</summary>
    internal void SavePillPosition(string monitorKey, PillPosition position)
    {
        if (_closing || Settings.PillPositions.GetValueOrDefault(monitorKey) == position) return;
        SaveSettings(Settings with { PillPositions = new(Settings.PillPositions) { [monitorKey] = position } });
    }

    /// <summary>Where the pill was dragged from the meeting window's top-right corner, saved at once.</summary>
    internal void SavePillWindowOffset(PillPosition? offset)
    {
        if (_closing || Settings.PillWindowOffset == offset) return;
        SaveSettings(Settings with { PillWindowOffset = offset });
    }
}

/// <summary>A question the floating pill asks (macOS <c>PillPrompt</c>); Phase 20 adds the cases.</summary>
public abstract record PillPrompt;
