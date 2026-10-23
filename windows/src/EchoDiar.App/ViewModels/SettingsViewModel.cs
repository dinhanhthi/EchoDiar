using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.SystemServices;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// The settings the Settings pages show and change (macOS AppModel <c>settings</c> and <c>updateSettings</c>): each
/// change applies at once and is saved 300 ms after the last one (<see cref="AppViewModel.ApplySettings"/>), so a burst
/// of changes writes the file once. Every property is raised again whenever the settings change, from here or elsewhere
/// (the Record menu's language, the player's channels). Lives as long as the app (<see cref="AppViewModel.SettingsModel"/>).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppViewModel _app;
    private readonly ILaunchAtLogin _launchAtLogin;
    private LaunchAtLoginState? _launchAtLoginState;
    private string? _launchAtLoginNote;

    internal SettingsViewModel(AppViewModel app, ILaunchAtLogin launchAtLogin)
    {
        _app = app;
        _launchAtLogin = launchAtLogin;
        app.PropertyChanged += OnAppChanged;
    }

    /// <summary>The current settings, for what has no property of its own yet.</summary>
    public AppSettings Settings => _app.Settings;

    /// <summary>Applies <paramref name="change"/> to the current settings; saved after the delay. No change, no save.</summary>
    public void Update(Func<AppSettings, AppSettings> change) => _app.ApplySettings(change(_app.Settings));

    // General

    /// <summary>"Launch at login": the startup task's real state, read by <see cref="SyncLaunchAtLoginAsync"/>.</summary>
    public bool LaunchAtLogin => Settings.LaunchAtLogin;

    /// <summary>False while a policy decides launch at login.</summary>
    public bool CanChangeLaunchAtLogin =>
        _launchAtLoginState is not (LaunchAtLoginState.DisabledByPolicy or LaunchAtLoginState.EnabledByPolicy);

    /// <summary>Why launch at login is not as asked (turned off in Windows, a policy, an error); null when it is.</summary>
    public string? LaunchAtLoginNote
    {
        get => _launchAtLoginNote;
        private set => SetProperty(ref _launchAtLoginNote, value);
    }

    /// <summary>
    /// Turns launch at login on or off, then shows its real state (macOS <c>setLaunchAtLogin</c>). Turned off by the
    /// user, it opens Windows' Startup apps, where only they can turn it on (macOS opens Login Items for an approval).
    /// </summary>
    public async Task SetLaunchAtLoginAsync(bool enabled)
    {
        string? error = null;
        try
        {
            if (enabled)
            {
                if (await _launchAtLogin.RequestEnableAsync() == LaunchAtLoginState.DisabledByUser)
                {
                    _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps"));
                }
            }
            else
            {
                await _launchAtLogin.DisableAsync();
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not change launch at login: {e}");
            error = e.Message;
        }
        await SyncLaunchAtLoginAsync(error);
    }

    /// <summary>
    /// The toggle follows the startup task's real state, which Windows' Startup apps can change too (macOS
    /// <c>syncLaunchAtLogin</c>, on appear); <paramref name="error"/> is shown instead of the state's note.
    /// </summary>
    public async Task SyncLaunchAtLoginAsync(string? error = null)
    {
        try
        {
            var state = await _launchAtLogin.GetStateAsync();
            _launchAtLoginState = state;
            Update(s => s with { LaunchAtLogin = state is LaunchAtLoginState.Enabled or LaunchAtLoginState.EnabledByPolicy });
            LaunchAtLoginNote = error ?? NoteFor(state);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read launch at login: {e}");
            LaunchAtLoginNote = error ?? e.Message;
        }
        OnPropertyChanged(nameof(CanChangeLaunchAtLogin));
        OnPropertyChanged(nameof(LaunchAtLogin));
    }

    private static string? NoteFor(LaunchAtLoginState state) => state switch
    {
        LaunchAtLoginState.DisabledByUser => Loc.T("Turned off in Windows Settings > Apps > Startup. Turn EchoDiar on there."),
        LaunchAtLoginState.DisabledByPolicy => Loc.T("Turned off by your organization."),
        LaunchAtLoginState.EnabledByPolicy => Loc.T("Turned on by your organization."),
        _ => null,
    };

    /// <summary>The language new recordings are transcribed in ("auto" or a Whisper code).</summary>
    public string DefaultLanguage
    {
        get => Settings.DefaultLanguage;
        set => Update(s => s with { DefaultLanguage = value });
    }

    /// <summary>How transcripts play: both voices in both ears, or as recorded.</summary>
    public PlaybackChannels PlaybackChannels
    {
        get => Settings.PlaybackChannels;
        set => Update(s => s with { PlaybackChannels = value });
    }

    // Transcription

    /// <summary>"Transcribe automatically": a recording is transcribed once it stops.</summary>
    public bool AutoTranscribe
    {
        get => Settings.AutoTranscribe;
        set => Update(s => s with { AutoTranscribe = value });
    }

    /// <summary>"Transcribe while recording": the live transcript, kept for the job at Stop.</summary>
    public bool TranscribeWhileRecording
    {
        get => Settings.TranscribeWhileRecording;
        set => Update(s => s with { TranscribeWhileRecording = value });
    }

    /// <summary>"Free unused models after", in minutes.</summary>
    public int ModelIdleMinutes
    {
        get => Settings.ModelIdleMinutes;
        set => Update(s => s with { ModelIdleMinutes = value });
    }

    /// <summary>Transcription waits while energy saver is on (the setting only; task 18.2 makes the pipeline wait).</summary>
    public bool DeferTranscriptionWhenConstrained
    {
        get => Settings.DeferTranscriptionWhenConstrained;
        set => Update(s => s with { DeferTranscriptionWhenConstrained = value });
    }

    /// <summary>"Tell speakers apart".</summary>
    public bool Diarization
    {
        get => Settings.Diarization;
        set => Update(s => s with { Diarization = value });
    }

    /// <summary>"Number of other speakers": null is Auto.</summary>
    public int? SpeakerCountHint
    {
        get => Settings.SpeakerCountHint;
        set => Update(s => s with { SpeakerCountHint = value });
    }

    // Storage

    /// <summary>"Compression bitrate": the total AAC bitrate of the m4a, in kbps.</summary>
    public int CompressionBitrateKbps
    {
        get => Settings.CompressionBitrateKbps;
        set => Update(s => s with { CompressionBitrateKbps = value });
    }

    /// <summary>"Keep uncompressed audio": the CAF stays next to the m4a.</summary>
    public bool KeepUncompressedAudio
    {
        get => Settings.KeepUncompressedAudio;
        set => Update(s => s with { KeepUncompressedAudio = value });
    }

    /// <summary>"Delete audio after transcription": the audio goes to the Recycle Bin once a transcription succeeds.</summary>
    public bool DeleteAudioAfterTranscription
    {
        get => Settings.DeleteAudioAfterTranscription;
        set => Update(s => s with { DeleteAudioAfterTranscription = value });
    }

    /// <summary>"Auto-delete old conversations": its schedule starts or stops at once.</summary>
    public bool AutoDeleteEnabled
    {
        get => Settings.AutoDeleteEnabled;
        set => Update(s => s with { AutoDeleteEnabled = value });
    }

    /// <summary>Auto-delete's "Older than" in days.</summary>
    public int AutoDeleteAfterDays
    {
        get => Settings.AutoDeleteAfterDays;
        set => Update(s => s with { AutoDeleteAfterDays = value });
    }

    /// <summary>Auto-delete's "Delete": the audio only or the whole conversation.</summary>
    public RetentionMode AutoDeleteMode
    {
        get => Settings.AutoDeleteMode;
        set => Update(s => s with { AutoDeleteMode = value });
    }

    /// <summary>"Show floating indicator while recording".</summary>
    public bool ShowPill
    {
        get => Settings.ShowPill;
        set => Update(s => s with { ShowPill = value });
    }

    /// <summary>"Hide it while an EchoDiar window is in front".</summary>
    public bool HidePillWhenAppActive
    {
        get => Settings.HidePillWhenAppActive;
        set => Update(s => s with { HidePillWhenAppActive = value });
    }

    /// <summary>"Show it in screenshots and screen recordings".</summary>
    public bool ShowPillInScreenCaptures
    {
        get => Settings.ShowPillInScreenCaptures;
        set => Update(s => s with { ShowPillInScreenCaptures = value });
    }

    /// <summary>"Indicator size".</summary>
    public PillSize PillSize
    {
        get => Settings.PillSize;
        set => Update(s => s with { PillSize = value });
    }

    /// <summary>"Delete conversations without confirmation".</summary>
    public bool DeleteWithoutConfirmation
    {
        get => Settings.DeleteWithoutConfirmation;
        set => Update(s => s with { DeleteWithoutConfirmation = value });
    }

    /// <summary>Every property follows the settings (an empty name refreshes every binding).</summary>
    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.Settings)) OnPropertyChanged(string.Empty);
    }
}
