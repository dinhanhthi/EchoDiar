using System.ComponentModel;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › Shortcuts (macOS ShortcutsSettingsView.swift). The toggles show <see cref="SettingsViewModel"/> and change
/// it; each recorder is off while its toggle is; a shortcut Windows refused shows why under it
/// (<see cref="AppViewModel.HotKeyError"/>).
/// </summary>
public sealed partial class ShortcutsSettingsPage : UserControl
{
    /// <summary>The searchable rows (macOS SettingsSearch.swift, Shortcuts), each the Tag of its element.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Global record shortcut", ["hotkey", "keyboard", "record from any app"]),
        new("Global pause shortcut", ["hotkey", "keyboard", "pause", "resume"]),
        new("In EchoDiar", ["keyboard shortcuts", "keys"]),
    ];

    private readonly AppViewModel _model;
    private readonly SettingsViewModel _settings;

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    public ShortcutsSettingsPage(AppViewModel model)
    {
        _model = model;
        _settings = model.SettingsModel;
        InitializeComponent();
        var noteStyle = (Style)Resources["NoteStyle"];
        RecordShortcutCard.Content = new HotKeyRecorder(model, pause: false, noteStyle);
        PauseShortcutCard.Content = new HotKeyRecorder(model, pause: true, noteStyle);

        RecordToggle.Toggled += (_, _) => Set(() => _settings.GlobalHotKeyEnabled = RecordToggle.IsOn);
        PauseToggle.Toggled += (_, _) => Set(() => _settings.GlobalPauseHotKeyEnabled = PauseToggle.IsOn);

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            Refresh();
        };
        Unloaded += (_, _) => _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.Settings) or nameof(AppViewModel.HotKeyError) or nameof(AppViewModel.PauseHotKeyError))
        {
            Refresh();
        }
    }

    private void Set(Action change)
    {
        if (!_syncing) change();
    }

    /// <summary>Every control shows the current settings and registration errors.</summary>
    private void Refresh()
    {
        _syncing = true;
        try
        {
            RecordToggle.IsOn = _settings.GlobalHotKeyEnabled;
            RecordShortcutCard.IsEnabled = _settings.GlobalHotKeyEnabled;
            ShowError(RecordErrorRow, RecordErrorText, _model.HotKeyError);
            PauseToggle.IsOn = _settings.GlobalPauseHotKeyEnabled;
            PauseShortcutCard.IsEnabled = _settings.GlobalPauseHotKeyEnabled;
            ShowError(PauseErrorRow, PauseErrorText, _model.PauseHotKeyError);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void ShowError(FrameworkElement row, TextBlock text, string? error)
    {
        text.Text = error ?? "";
        row.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
