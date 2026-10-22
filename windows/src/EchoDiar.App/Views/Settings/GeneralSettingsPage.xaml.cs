using System.ComponentModel;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › General (macOS GeneralSettingsView.swift). Every control shows <see cref="SettingsViewModel"/> and
/// changes it; the change is saved after its delay. Launch at login follows the startup task's real state each time
/// the page shows (macOS <c>onAppear</c>).
/// </summary>
public sealed partial class GeneralSettingsPage : UserControl
{
    /// <summary>
    /// The consent message people are told (macOS <c>consentMessage</c>, "on my Mac" made "on my PC"); the onboarding's
    /// consent step (task 19.4) shows the same text.
    /// </summary>
    public const string ConsentMessage = "Heads up: I'm recording this call to take notes. The recording stays on my PC.";

    /// <summary>The searchable rows (macOS SettingsSearch.swift, General), each the Tag of its card.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Launch at login", ["startup", "startup apps", "login item", "open at login", "sign in"]),
        new("Default language", ["language", "English", "French", "Vietnamese"]),
        new("Show floating indicator while recording", ["pill", "overlay", "floating"]),
        new("Hide it while an EchoDiar window is in front", ["floating indicator", "pill", "hide"]),
        new("Show it in screenshots and screen recordings", ["floating indicator", "pill", "screenshot", "screen sharing", "capture"]),
        new("Indicator size", ["floating indicator", "pill", "small", "medium", "large"]),
        new("Delete conversations without confirmation", ["confirm", "Recycle Bin", "trash", "library"]),
        new("Copy consent message", ["consent", "privacy", "tell people"]),
    ];

    private readonly AppViewModel _model;
    private readonly SettingsViewModel _settings;

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    /// <summary>The language menu's codes, in item order; rebuilt when the choices change.</summary>
    private List<string> _languageCodes = [];

    public GeneralSettingsPage(AppViewModel model)
    {
        _model = model;
        _settings = model.SettingsModel;
        InitializeComponent();
        ConsentText.Text = $"\"{Loc.T(ConsentMessage)}\"";

        LaunchToggle.Toggled += (_, _) => _ = OnLaunchToggledAsync();
        LanguageCombo.SelectionChanged += (_, _) =>
        {
            var index = LanguageCombo.SelectedIndex;
            if (!_syncing && index >= 0 && index < _languageCodes.Count) _settings.DefaultLanguage = _languageCodes[index];
        };
        ShowPillToggle.Toggled += (_, _) => Set(() => _settings.ShowPill = ShowPillToggle.IsOn);
        HidePillToggle.Toggled += (_, _) => Set(() => _settings.HidePillWhenAppActive = HidePillToggle.IsOn);
        CapturesToggle.Toggled += (_, _) => Set(() => _settings.ShowPillInScreenCaptures = CapturesToggle.IsOn);
        PillSizeCombo.SelectionChanged += (_, _) =>
        {
            if (!_syncing && PillSizeCombo.SelectedIndex >= 0) _settings.PillSize = (PillSize)PillSizeCombo.SelectedIndex;
        };
        DeleteToggle.Toggled += (_, _) => Set(() => _settings.DeleteWithoutConfirmation = DeleteToggle.IsOn);
        CopyConsentButton.Click += (_, _) => CopyConsent();

        Loaded += (_, _) =>
        {
            _settings.PropertyChanged += OnSettingsChanged;
            Refresh();
            _ = _settings.SyncLaunchAtLoginAsync();
        };
        Unloaded += (_, _) => _settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

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
            RefreshLanguages();
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

    /// <summary>
    /// The default language menu in LanguageOptions order (Auto, the pinned languages, the recent ones, every other
    /// supported one), flattened: a ComboBox has no separators.
    /// </summary>
    private void RefreshLanguages()
    {
        var selection = _settings.DefaultLanguage;
        List<string> codes = [.. LanguageOptions.Groups(selection, _settings.Settings.RecentLanguages, _model.SupportedLanguages()).SelectMany(g => g)];
        if (!codes.SequenceEqual(_languageCodes))
        {
            _languageCodes = codes;
            LanguageCombo.Items.Clear();
            foreach (var code in codes) LanguageCombo.Items.Add(LanguageOptions.Name(code));
        }
        LanguageCombo.SelectedIndex = _languageCodes.IndexOf(selection);
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
        var package = new DataPackage();
        package.SetText(Loc.T(ConsentMessage));
        try
        {
            Clipboard.SetContent(package);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"EchoDiar: could not copy the consent message: {e}");
            return;
        }
        CopyConsentText.Text = Loc.T("Copied");
        CopyConsentIcon.Glyph = "";
    }
}
