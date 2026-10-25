using System.ComponentModel;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using EchoDiar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Globalization;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › General (macOS GeneralSettingsView.swift). Every control shows <see cref="SettingsViewModel"/> and
/// changes it; the change is saved after its delay. Launch at login follows the startup task's real state each time
/// the page shows (macOS <c>onAppear</c>). The app language is Windows' language override for the app, read by the
/// next launch: "Relaunch now" shows while the choice differs from this run's.
/// </summary>
public sealed partial class GeneralSettingsPage : UserControl
{
    /// <summary>
    /// The consent message people are told (macOS <c>consentMessage</c>, "on my Mac" made "on my PC"); the onboarding's
    /// consent step shows the same text.
    /// </summary>
    public const string ConsentMessage = "Heads up: I'm recording this call to take notes. The recording stays on my PC.";

    /// <summary>The searchable rows (macOS SettingsSearch.swift, General), each the Tag of its card.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Launch at login", ["startup", "startup apps", "login item", "open at login", "sign in"]),
        new("App language", ["language", "locale", "interface", "relaunch"]),
        new("Show floating indicator while recording", ["pill", "overlay", "floating"]),
        new("Hide it while an EchoDiar window is in front", ["floating indicator", "pill", "hide"]),
        new("Show it in screenshots and screen recordings", ["floating indicator", "pill", "screenshot", "screen sharing", "capture"]),
        new("Indicator size", ["floating indicator", "pill", "small", "medium", "large"]),
        new("Delete conversations without confirmation", ["confirm", "Recycle Bin", "trash", "library"]),
        new("Copy consent message", ["consent", "privacy", "tell people"]),
        new("Show onboarding again", ["permissions", "welcome", "setup"]),
    ];

    private readonly AppViewModel _model;
    private readonly SettingsViewModel _settings;

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    public GeneralSettingsPage(AppViewModel model)
    {
        _model = model;
        _settings = model.SettingsModel;
        InitializeComponent();
        ConsentText.Text = $"\"{Loc.T(ConsentMessage)}\"";
        var system = Loc.T("System");
        LanguageCombo.ItemsSource = AppLanguages.All.Select(language => language.DisplayName(system)).ToList();
        _syncing = true;
        LanguageCombo.SelectedIndex = IndexOf(AppLanguages.Matching(ApplicationLanguages.PrimaryLanguageOverride));
        _syncing = false;

        LaunchToggle.Toggled += (_, _) => _ = OnLaunchToggledAsync();
        ShowPillToggle.Toggled += (_, _) => Set(() => _settings.ShowPill = ShowPillToggle.IsOn);
        HidePillToggle.Toggled += (_, _) => Set(() => _settings.HidePillWhenAppActive = HidePillToggle.IsOn);
        CapturesToggle.Toggled += (_, _) => Set(() => _settings.ShowPillInScreenCaptures = CapturesToggle.IsOn);
        PillSizeCombo.SelectionChanged += (_, _) =>
        {
            if (!_syncing && PillSizeCombo.SelectedIndex >= 0) _settings.PillSize = (PillSize)PillSizeCombo.SelectedIndex;
        };
        LanguageCombo.SelectionChanged += (_, _) => OnLanguageChosen();
        RelaunchButton.Click += (_, _) => OnRelaunchClicked();
        DeleteToggle.Toggled += (_, _) => Set(() => _settings.DeleteWithoutConfirmation = DeleteToggle.IsOn);
        CopyConsentButton.Click += (_, _) => CopyConsent();
        ShowOnboardingButton.Click += (_, _) => _model.ShowOnboarding();

        Loaded += (_, _) =>
        {
            _settings.PropertyChanged += OnSettingsChanged;
            _model.PropertyChanged += OnModelChanged;
            _model.Updater.PropertyChanged += OnUpdaterChanged;
            Refresh();
            RefreshRelaunch();
            _ = _settings.SyncLaunchAtLoginAsync();
        };
        Unloaded += (_, _) =>
        {
            _settings.PropertyChanged -= OnSettingsChanged;
            _model.PropertyChanged -= OnModelChanged;
            _model.Updater.PropertyChanged -= OnUpdaterChanged;
        };
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    /// <summary>The work that blocks "Relaunch now" started or ended (the same changes as the toolbar's Update button).</summary>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.RecordingSnapshot) or nameof(AppViewModel.IsStarting)
            or nameof(AppViewModel.TranscriptionProgress) or nameof(AppViewModel.Conversations) or nameof(AppViewModel.AIJob))
        {
            RefreshRelaunch();
        }
    }

    private void OnUpdaterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Updater.IsInstalling)) RefreshRelaunch();
    }

    private void Set(Action change)
    {
        if (!_syncing) change();
    }

    /// <summary>Every control shows the current settings.</summary>
    private void Refresh()
    {
        _syncing = true;
        try
        {
            LaunchToggle.IsOn = _settings.LaunchAtLogin;
            LaunchToggle.IsEnabled = _settings.CanChangeLaunchAtLogin;
            LaunchCard.Description = _settings.LaunchAtLoginNote!; // null hides it
            ShowPillToggle.IsOn = _settings.ShowPill;
            HidePillToggle.IsOn = _settings.HidePillWhenAppActive;
            // macOS disables "Hide it…" while the indicator is off.
            HidePillCard.IsEnabled = _settings.ShowPill;
            CapturesToggle.IsOn = _settings.ShowPillInScreenCaptures;
            PillSizeCombo.SelectedIndex = (int)_settings.PillSize;
            DeleteToggle.IsOn = _settings.DeleteWithoutConfirmation;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static int IndexOf(AppLanguage language) => AppLanguages.All.ToList().IndexOf(language);

    private AppLanguage? ChosenLanguage =>
        LanguageCombo.SelectedIndex >= 0 ? AppLanguages.All[LanguageCombo.SelectedIndex] : null;

    /// <summary>The picked language is the override the next launch reads ("" for System); the relaunch row follows.</summary>
    private void OnLanguageChosen()
    {
        if (_syncing || ChosenLanguage is not { } language) return;
        ApplicationLanguages.PrimaryLanguageOverride = language.OverrideTag();
        RefreshRelaunch();
    }

    /// <summary>
    /// Why "Relaunch now" is off (macOS <c>relaunchBlocker</c>): the relaunch would cut off a recording, a transcription,
    /// a compression or an AI answer (<see cref="AppViewModel.UpdateBlocked"/>), and an installing update closes the app
    /// itself.
    /// </summary>
    private string? RelaunchBlocker =>
        _model.IsRecording || _model.IsStarting ? Loc.T("Stop the recording first")
        : _model.Updater.IsInstalling ? Loc.T("Installing the update…")
        : _model.UpdateBlocked ? Loc.T("Wait for the transcriptions and AI answers to finish")
        : null;

    /// <summary>
    /// The relaunch row shows while the choice differs from this run's language; its button is off with the reason as the
    /// row's description (a disabled button shows no tooltip). Called at 15 Hz while recording: it only sets values.
    /// </summary>
    private void RefreshRelaunch()
    {
        var shown = ChosenLanguage is { } language && language != App.LanguageAtLaunch;
        RelaunchCard.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        var blocker = RelaunchBlocker;
        RelaunchButton.IsEnabled = blocker is null;
        if (RelaunchCard.Description as string != blocker) RelaunchCard.Description = blocker!; // null hides it
    }

    /// <summary>Checked again on click (macOS): work may have started since the row was shown.</summary>
    private void OnRelaunchClicked()
    {
        if (RelaunchBlocker is not null)
        {
            RefreshRelaunch();
            return;
        }
        RelaunchButton.IsEnabled = false;
        ((App)Application.Current).Relaunch();
    }

    /// <summary>A flip by the user asks the startup task; the toggle then shows its real state.</summary>
    private async Task OnLaunchToggledAsync()
    {
        if (_syncing || LaunchToggle.IsOn == _settings.LaunchAtLogin) return;
        LaunchToggle.IsEnabled = false;
        try
        {
            await _settings.SetLaunchAtLoginAsync(LaunchToggle.IsOn);
        }
        finally
        {
            Refresh();
        }
    }

    /// <summary>The message on the clipboard; the button says "Copied" from then on (macOS).</summary>
    private void CopyConsent()
    {
        if (!CopyConsentMessage()) return;
        CopyConsentText.Text = Loc.T("Copied");
        CopyConsentIcon.Glyph = "";
    }

    /// <summary>Puts <see cref="ConsentMessage"/> on the clipboard (here and in the onboarding); false when it failed.</summary>
    internal static bool CopyConsentMessage()
    {
        var package = new DataPackage();
        package.SetText(Loc.T(ConsentMessage));
        try
        {
            Clipboard.SetContent(package);
            return true;
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"EchoDiar: could not copy the consent message: {e}");
            return false;
        }
    }
}
