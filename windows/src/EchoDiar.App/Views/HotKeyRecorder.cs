using System.ComponentModel;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.SystemServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EchoDiar.App.Views;

/// <summary>
/// Click, then press the new global record or pause shortcut; Esc cancels (macOS ShortcutsSettingsView.swift
/// <c>HotKeyRecorder</c>). The shortcut needs Ctrl or Alt, may not be the other global shortcut, a standard edit or
/// window shortcut or an in-app one, and is saved at once. "Reset" shows while it is not the default. The registered
/// shortcuts are off while it records (<see cref="AppViewModel.SetHotKeyCapture"/>), so pressing one lands here instead
/// of toggling a recording; one recorder records at a time. Recording stops when the button loses the focus, the page
/// goes away, or the window is deactivated (the model ends the capture). Built in code; the page passes its note style,
/// so the colours follow the theme.
/// </summary>
public sealed partial class HotKeyRecorder : UserControl
{
    /// <summary>
    /// Standard shortcuts and EchoDiar's own (with Ctrl or Alt) that a global one would take from every app (macOS refuses
    /// the menu shortcuts).
    /// </summary>
    private static readonly (WindowsHotKey Key, string Title)[] Standard =
    [
        (Ctrl('C'), "Copy"),
        (Ctrl('X'), "Cut"),
        (Ctrl('V'), "Paste"),
        (Ctrl('Z'), "Undo"),
        (Ctrl('Y'), "Redo"),
        (Ctrl('A'), "Select All"),
        (new() { VirtualKey = (uint)VirtualKey.F4, Modifiers = WindowsHotKey.ModAlt }, "Close"),
        .. ShortcutsSettingsPage.InAppShortcuts.SelectMany(s => s.Keys.Where(k => k.HasCtrlOrAlt).Select(k => (k, s.Title))),
    ];

    private readonly AppViewModel _model;

    /// <summary>The pause shortcut; else the record shortcut.</summary>
    private readonly bool _pause;

    private readonly Button _keys = new() { MinWidth = 120 };
    private readonly TextBlock _keysText = new();
    private readonly Button _reset = new();
    private readonly TextBlock _note;

    /// <summary>This recorder is waiting for a shortcut.</summary>
    private bool _capturing;

    /// <param name="pause">Records the global pause shortcut; else the global record shortcut.</param>
    /// <param name="noteStyle">The style of the note under the button (why a shortcut was refused).</param>
    public HotKeyRecorder(AppViewModel model, bool pause, Style noteStyle)
    {
        _model = model;
        _pause = pause;
        _keys.Content = _keysText;
        AutomationProperties.SetName(_keys, Loc.T(pause ? "Global pause shortcut" : "Global record shortcut"));
        _reset.Content = Loc.T("Reset");
        _note = new TextBlock
        {
            Style = noteStyle,
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            Visibility = Visibility.Collapsed,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(_keys);
        row.Children.Add(_reset);
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(row);
        panel.Children.Add(_note);
        Content = panel;

        _keys.Click += (_, _) => Toggle();
        _reset.Click += (_, _) => Reset();
        // Before the button: Space and Enter would click it, Tab would move the focus.
        _keys.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: true);
        _keys.LostFocus += (_, _) => Stop();
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Stop();
            _model.PropertyChanged -= OnModelChanged;
        };
    }

    private WindowsHotKey DefaultKey => _pause ? WindowsHotKey.DefaultPause : WindowsHotKey.DefaultRecord;

    private WindowsHotKey Key => _pause ? _model.Settings.GlobalPauseHotKeyWindows : _model.Settings.GlobalHotKeyWindows;

    /// <summary>The other global shortcut, which this one may not equal.</summary>
    private WindowsHotKey Other => _pause ? _model.Settings.GlobalHotKeyWindows : _model.Settings.GlobalPauseHotKeyWindows;

    private string OtherName => _pause ? "global record shortcut" : "global pause shortcut";

    private static WindowsHotKey Ctrl(char key) => new() { VirtualKey = key, Modifiers = WindowsHotKey.ModControl };

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.IsCapturingHotKey):
                // Ended by the model (the window was deactivated).
                if (!_model.IsCapturingHotKey) _capturing = false;
                Refresh();
                break;
            case nameof(AppViewModel.Settings):
                Refresh();
                break;
        }
    }

    /// <summary>The shortcut or "Press a shortcut…", the hint, Reset; the button is off while the other recorder records.</summary>
    private void Refresh()
    {
        var key = Key;
        _keysText.Text = _capturing ? Loc.T("Press a shortcut…") : GlobalHotKeys.Describe(key);
        ToolTipService.SetToolTip(_keys, Loc.T(_capturing ? "Press Esc to cancel" : "Click to record a new shortcut"));
        _keys.IsEnabled = _capturing || !_model.IsCapturingHotKey;
        _reset.Visibility = key != DefaultKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Toggle()
    {
        if (_capturing)
        {
            Stop();
            return;
        }
        ShowNote(null);
        _capturing = true;
        _model.SetHotKeyCapture(true);
        Refresh();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        var key = e.Key;
        if (key == VirtualKey.Escape)
        {
            Stop();
            return;
        }
        if (IsModifierOrNone(key)) return;
        var pressed = new WindowsHotKey { VirtualKey = (uint)key, Modifiers = Modifiers(e) };
        if (!pressed.HasCtrlOrAlt)
        {
            ShowNote(Loc.T("Use Ctrl or Alt with the key."));
            return;
        }
        // A global shortcut takes the keys from every app.
        if (Standard.FirstOrDefault(s => s.Key == pressed).Title is { } taken)
        {
            ShowNote(Loc.Format("%@ is the \"%@\" shortcut.", GlobalHotKeys.Describe(pressed), Loc.T(taken)));
            return;
        }
        if (pressed == Other)
        {
            ShowNote(Loc.Format("%@ is the %@.", GlobalHotKeys.Describe(pressed), OtherName));
            return;
        }
        // Saved while still recording, so only the new shortcut is registered when the recording ends.
        Save(pressed);
        Stop();
    }

    /// <summary>Back to the default, unless the other shortcut has taken it.</summary>
    private void Reset()
    {
        if (DefaultKey == Other)
        {
            ShowNote(Loc.Format("%@ is the %@.", GlobalHotKeys.Describe(DefaultKey), OtherName));
            return;
        }
        ShowNote(null);
        Save(DefaultKey);
    }

    private void Save(WindowsHotKey key) => _model.SettingsModel.Update(s =>
        _pause ? s with { GlobalPauseHotKeyWindows = key } : s with { GlobalHotKeyWindows = key });

    private void Stop()
    {
        if (!_capturing) return;
        _capturing = false;
        _model.SetHotKeyCapture(false);
        Refresh();
    }

    private void ShowNote(string? note)
    {
        _note.Text = note ?? "";
        _note.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The MOD_* mask of the modifiers held now (Alt from the key's own status: Alt+key comes as a system key).</summary>
    private static uint Modifiers(KeyRoutedEventArgs e)
    {
        uint modifiers = 0;
        if (ConversationListView.IsKeyDown(VirtualKey.Control)) modifiers |= WindowsHotKey.ModControl;
        if (e.KeyStatus.IsMenuKeyDown || ConversationListView.IsKeyDown(VirtualKey.Menu)) modifiers |= WindowsHotKey.ModAlt;
        if (ConversationListView.IsKeyDown(VirtualKey.Shift)) modifiers |= WindowsHotKey.ModShift;
        if (ConversationListView.IsKeyDown(VirtualKey.LeftWindows) || ConversationListView.IsKeyDown(VirtualKey.RightWindows))
        {
            modifiers |= WindowsHotKey.ModWin;
        }
        return modifiers;
    }

    /// <summary>A modifier on its own (the shortcut's key comes next), or no key (an IME's).</summary>
    private static bool IsModifierOrNone(VirtualKey key) => key is VirtualKey.None or VirtualKey.Control or VirtualKey.Shift
        or VirtualKey.Menu or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftWindows or VirtualKey.RightWindows
        or (VirtualKey)229; // VK_PROCESSKEY
}
