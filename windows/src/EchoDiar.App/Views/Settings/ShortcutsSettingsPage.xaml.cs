using System.ComponentModel;
using CommunityToolkit.WinUI.Controls;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.SystemServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// What an in-app shortcut does (macOS EchoDiarApp.swift: the main menu's actions, and Esc and Space).
/// <see cref="DeleteSelection"/> is Windows' Delete key, not one of the page's shortcuts (<see cref="MainWindow"/>).
/// </summary>
public enum InAppAction { RecordStop, PauseResume, ShowRecording, ShowList, OpenChat, Search, Settings, Back, PlayPause, DeleteSelection }

/// <summary>
/// A shortcut used inside EchoDiar: its action, its title on the Shortcuts page, and its keys (Ctrl for macOS ⌘; Esc and
/// Space without modifiers). <see cref="MainWindow"/> makes them its keyboard accelerators, and a global shortcut may not
/// take one with Ctrl or Alt (<see cref="HotKeyRecorder"/>).
/// </summary>
public sealed record InAppShortcut(InAppAction Action, string Title, IReadOnlyList<WindowsHotKey> Keys)
{
    /// <summary>The keys as the page shows them, e.g. "Ctrl+[ / Esc".</summary>
    public string KeysText => string.Join(" / ", Keys.Select(GlobalHotKeys.Describe));
}

/// <summary>
/// Settings › Shortcuts (macOS ShortcutsSettingsView.swift). The toggles show <see cref="SettingsViewModel"/> and change
/// it; each recorder is off while its toggle is; a shortcut Windows refused shows why under it
/// (<see cref="AppViewModel.HotKeyError"/>).
/// </summary>
public sealed partial class ShortcutsSettingsPage : UserControl
{
    /// <summary>The shortcuts used inside EchoDiar (macOS <c>inAppShortcuts</c>, in its order and with its titles).</summary>
    public static IReadOnlyList<InAppShortcut> InAppShortcuts { get; } =
    [
        new(InAppAction.RecordStop, "Record / Stop", [Key('R', WindowsHotKey.ModControl)]),
        new(InAppAction.PauseResume, "Pause / Resume", [Key('P', WindowsHotKey.ModControl | WindowsHotKey.ModShift)]),
        new(InAppAction.ShowRecording, "Show the recording", [Key('1', WindowsHotKey.ModControl)]),
        new(InAppAction.ShowList, "Show the recordings list", [Key('2', WindowsHotKey.ModControl)]),
        new(InAppAction.OpenChat, "Open the chat of the open conversation", [Key('I', WindowsHotKey.ModControl)]),
        new(InAppAction.Search, "Search conversations or settings", [Key('F', WindowsHotKey.ModControl)]),
        new(InAppAction.Settings, "Settings", [Key(0xBC, WindowsHotKey.ModControl)]), // VK_OEM_COMMA
        new(InAppAction.Back, "Back", [Key(0xDB, WindowsHotKey.ModControl), Key(0x1B, 0)]), // VK_OEM_4 ([), VK_ESCAPE
        new(InAppAction.PlayPause, "Play / Pause the transcript audio", [Key(0x20, 0)]), // VK_SPACE
    ];

    /// <summary>The searchable rows (macOS SettingsSearch.swift, Shortcuts), each the Tag of its element.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Global record shortcut", ["hotkey", "keyboard", "record from any app"]),
        new("Global pause shortcut", ["hotkey", "keyboard", "pause", "resume"]),
        new("In EchoDiar", ["keyboard shortcuts", "keys", .. InAppShortcuts.Select(s => s.Title)]),
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
        var keysStyle = (Style)Resources["KeysTextStyle"];
        foreach (var shortcut in InAppShortcuts)
        {
            InAppList.Children.Add(new SettingsCard
            {
                Header = Loc.T(shortcut.Title),
                Content = new TextBlock { Style = keysStyle, Text = shortcut.KeysText },
            });
        }

        RecordToggle.Toggled += (_, _) => Set(() => _settings.GlobalHotKeyEnabled = RecordToggle.IsOn);
        PauseToggle.Toggled += (_, _) => Set(() => _settings.GlobalPauseHotKeyEnabled = PauseToggle.IsOn);

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            Refresh();
        };
        Unloaded += (_, _) => _model.PropertyChanged -= OnModelChanged;
    }

    private static WindowsHotKey Key(uint virtualKey, uint modifiers) => new() { VirtualKey = virtualKey, Modifiers = modifiers };

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
