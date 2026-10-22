using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoDiar.Core.Settings;

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

    internal SettingsViewModel(AppViewModel app)
    {
        _app = app;
        app.PropertyChanged += OnAppChanged;
    }

    /// <summary>The current settings, for what has no property of its own yet.</summary>
    public AppSettings Settings => _app.Settings;

    /// <summary>Applies <paramref name="change"/> to the current settings; saved after the delay. No change, no save.</summary>
    public void Update(Func<AppSettings, AppSettings> change) => _app.ApplySettings(change(_app.Settings));

    // General

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
