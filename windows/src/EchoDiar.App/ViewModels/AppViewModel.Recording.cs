using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.Views;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Transcribe;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// The recording state the card, the recording page and the toolbar share (macOS AppModel: selectedTarget,
/// sourceApp, recordingError, micRoutesLost, recordingLanguage): what Record records, the recorded app, the
/// "Recording problem" answer, the lost-mic route change and the recording language.
/// </summary>
public sealed partial class AppViewModel
{
    private AudioProcessGroup? _selectedTarget;
    private string? _recordingSourceName;

    /// <summary>The language picked since the last recording ended; null = the default language.</summary>
    private string? _languageOverride;

    private IReadOnlyList<string>? _supportedLanguages;

    /// <summary>The last language marker and settings writes; each next one runs after it.</summary>
    private Task _languageWrite = Task.CompletedTask;
    private Task _settingsWrite = Task.CompletedTask;

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

    /// <summary>The language the next recording starts with, or the current one while recording.</summary>
    public string RecordingLanguage => _languageOverride ?? Settings.DefaultLanguage;

    /// <summary>Before recording: the next session's language. While recording: a language marker at the current time.</summary>
    public void SetRecordingLanguage(string language)
    {
        if (language == RecordingLanguage) return;
        _languageOverride = language;
        OnPropertyChanged(nameof(RecordingLanguage));
        if (IsRecording)
        {
            // In pick order, off the UI thread: the engine writes session.json under its lock.
            _languageWrite = _languageWrite.ContinueWith(_ =>
            {
                try
                {
                    _services.Engine.SetLanguage(language);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"EchoDiar: could not add the language marker: {e}");
                }
            }, TaskScheduler.Default);
        }
        RememberLanguage(language);
    }

    /// <summary>Codes the catalog's default engine accepts, sorted by name.</summary>
    public IReadOnlyList<string> SupportedLanguages() =>
        _supportedLanguages ??= [.. TranscriptionPipeline.SupportedLanguages(ModelCatalog.DefaultASR)
            .OrderBy(LanguageOptions.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Most recent first; Auto and the pinned languages are always listed, so they are not kept.</summary>
    private void RememberLanguage(string language)
    {
        if (language == LanguageOptions.Auto || LanguageOptions.Pinned.Contains(language)) return;
        var settings = Settings with
        {
            RecentLanguages = [.. Settings.RecentLanguages.Where(l => l != language).Prepend(language).Take(LanguageOptions.MaxRecent)],
        };
        SaveSettings(settings);
    }

    /// <summary>How transcripts play (macOS Settings › Storage "Playback"): both voices in both ears, or as recorded.</summary>
    public void SetPlaybackChannels(PlaybackChannels channels)
    {
        if (channels == Settings.PlaybackChannels) return;
        SaveSettings(Settings with { PlaybackChannels = channels });
    }

    /// <summary>Applies <paramref name="settings"/> now and saves them off the UI thread, in call order.</summary>
    private void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        _settingsWrite = _settingsWrite.ContinueWith(_ =>
        {
            try
            {
                _services.Settings.Save(settings);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not save the settings: {e}");
            }
        }, TaskScheduler.Default);
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
        StartupTrace.Write($"MicRouteLost on the UI thread: {behavior}, recording {IsRecording}");
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
