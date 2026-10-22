using EchoDiar.Core.Audio;
using EchoDiar.Core.Settings;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// The recording state the card, the recording page and the toolbar share (macOS AppModel: selectedTarget,
/// sourceApp, recordingError, micRoutesLost): what Record records, the recorded app, the "Recording problem" answer
/// and the lost-mic route change.
/// </summary>
public sealed partial class AppViewModel
{
    private AudioProcessGroup? _selectedTarget;
    private string? _recordingSourceName;

    /// <summary>What Record records: null for all system audio, else the app picked in the Record menu.</summary>
    public AudioProcessGroup? SelectedTarget
    {
        get => _selectedTarget;
        set => SetProperty(ref _selectedTarget, value);
    }

    /// <summary>The recorded app's name from the start until the recording ends; null for all system audio.</summary>
    public string? RecordingSourceName
    {
        get => _recordingSourceName;
        private set => SetProperty(ref _recordingSourceName, value);
    }

    /// <summary>The window closed: views stop their frame callbacks, since its controls fail every call.</summary>
    internal bool IsClosing => _closing;

    /// <summary>
    /// The mic in use was lost during the recording (it moved to the default input) and "When the microphone changes"
    /// is Ask: the window asks whether to stop. Raised on the UI thread.
    /// </summary>
    public event Action? MicRouteLostAsk;

    /// <summary>The "Recording problem" dialog was closed on <paramref name="shown"/>; a newer error stays.</summary>
    public void DismissRecordingError(string shown)
    {
        if (RecordingError == shown) RecordingError = null;
    }

    /// <summary>The engine reads the setting when the mic is lost, and reports it here through the UI thread.</summary>
    private void FollowRouteChanges()
    {
        _services.Engine.RouteBehavior = () => _settings.RouteChangeBehavior;
        _services.Engine.MicRouteLost += behavior => Post(() => OnMicRouteLost(behavior));
    }

    /// <summary>Ask: the window's prompt; Stop: the normal stop; Continue: nothing (macOS micRoutesLost).</summary>
    private void OnMicRouteLost(RouteChangeBehavior behavior)
    {
        if (!IsRecording) return;
        switch (behavior)
        {
            case RouteChangeBehavior.Ask:
                MicRouteLostAsk?.Invoke();
                break;
            case RouteChangeBehavior.Stop:
                _ = StopRecordingAsync();
                break;
        }
    }
}
