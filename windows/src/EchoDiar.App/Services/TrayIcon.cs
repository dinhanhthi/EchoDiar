using System.ComponentModel;
using EchoDiar.App.ViewModels;
using EchoDiar.App.Views;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Platform.SystemServices;
using EchoDiar.Transcribe;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EchoDiar.App.Services;

/// <summary>What the tray icon shows (macOS <c>IndicatorState</c>).</summary>
internal enum IndicatorState { Idle, Recording, Paused, Processing }

/// <summary>
/// The notification area icon and its menu (macOS StatusItemController.swift), on the Platform <see cref="NotifyIcon"/>.
/// The menu is rebuilt each time it opens; a left click opens the main window (the Windows convention; macOS opens the
/// menu). The icon follows the recording: the logo, with a red dot while recording or an orange pause badge while paused
/// at its bottom-right (macOS), and the tooltip names the state. The window's session end (sign-out, shutdown) goes to
/// <c>sessionEnding</c>, synchronously. UI thread only; menu picks run after the menu closed, posted to the dispatcher.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppViewModel _model;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _openMain;
    private readonly Action _quit;
    private readonly NotifyIcon _icon;
    private IndicatorState? _shown;

#if DEBUG
    /// <summary>"Simulate state": shown until the real state (<see cref="_real"/>) changes.</summary>
    private IndicatorState? _simulated;
    private IndicatorState? _real;
#endif

    /// <param name="openMain">Shows and activates the main window.</param>
    /// <param name="quit">"Quit › Sure to quit".</param>
    /// <param name="sessionEnding">The session ends: runs inside the window procedure and must finish its work before returning.</param>
    public TrayIcon(AppViewModel model, DispatcherQueue dispatcher, Action openMain, Action quit, Action sessionEnding)
    {
        _model = model;
        _dispatcher = dispatcher;
        _openMain = openMain;
        _quit = quit;
        // windows/tools/make-icons.sh makes it from assets/logo.png.
        var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "EchoDiar.ico");
        _icon = new NotifyIcon(logo, Loc.T("EchoDiar"), message => StartupTrace.Write(message));
        _icon.Clicked += () => Post(_openMain);
        _icon.SessionEnding += sessionEnding;
        _icon.MenuItems = BuildMenu;
        _model.PropertyChanged += OnModelChanged;
        Update();
    }

    public void Dispose()
    {
        _model.PropertyChanged -= OnModelChanged;
        _icon.Dispose();
    }

    /// <summary>Recording or paused from the engine; else processing while a compression or transcription runs (macOS indicatorState).</summary>
    private IndicatorState State => _model.RecordingSnapshot.State switch
    {
        RecordingEngine.State.Recording => IndicatorState.Recording,
        RecordingEngine.State.Paused => IndicatorState.Paused,
        _ => IsProcessing ? IndicatorState.Processing : IndicatorState.Idle,
    };

    private bool IsProcessing =>
        _model.Conversations.Any(c => c.Status is ConversationStatus.Compressing or ConversationStatus.Transcribing)
        || _model.TranscriptionProgress.Values.Any(p => p.Step is not (JobStep.Finished or JobStep.NeedsModel or JobStep.Failed));

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.RecordingSnapshot) or nameof(AppViewModel.Conversations)
            or nameof(AppViewModel.TranscriptionProgress))
        {
            Update();
        }
    }

    /// <summary>The icon and tooltip for the state; the snapshot changes at 15 Hz while recording, the icon only on a new state.</summary>
    private void Update()
    {
        var state = State;
#if DEBUG
        if (state != _real) _simulated = null;
        _real = state;
        if (_simulated is { } simulated) state = simulated;
#endif
        Show(state);
    }

    private void Show(IndicatorState state)
    {
        if (state == _shown) return;
        _shown = state;
        switch (state)
        {
            case IndicatorState.Recording:
                // The Light theme's status colours: saturated enough on a light or dark taskbar and under white pause bars.
                _icon.SetGlyph(NotifyIconGlyph.Record, MainWindow.ThemeColor("SystemFillColorCritical", ElementTheme.Light));
                break;
            case IndicatorState.Paused:
                _icon.SetGlyph(NotifyIconGlyph.Pause, MainWindow.ThemeColor("SystemFillColorCaution", ElementTheme.Light));
                break;
            default:
                // macOS animates a waveform while processing; here the logo stays and the tooltip says it.
                _icon.SetGlyph(NotifyIconGlyph.Logo);
                break;
        }
        _icon.SetTooltip(state switch
        {
            IndicatorState.Recording => Loc.T("EchoDiar, recording"),
            IndicatorState.Paused => Loc.T("EchoDiar, recording paused"),
            IndicatorState.Processing => Loc.T("EchoDiar, processing"),
            _ => Loc.T("EchoDiar"),
        });
    }

    /// <summary>The menu as it opens (macOS menuNeedsUpdate); the values are read now.</summary>
    private IReadOnlyList<NotifyIconMenuItem> BuildMenu()
    {
        List<NotifyIconMenuItem> items = [Item(Loc.T("Open EchoDiar"), _openMain)];
        var snapshot = _model.RecordingSnapshot;
        if (snapshot.State != RecordingEngine.State.Idle)
        {
            AddRecordingItems(items, snapshot);
        }
        else
        {
            // Off while the update installs: it closes the app.
            items.Add(Item(Loc.T("Start recording"), () => _ = _model.StartRecordingAsync(_model.SelectedTarget),
                enabled: !_model.IsStarting && !_model.Updater.IsInstalling));
        }
        if (_model.DetectedMeetingName is { } app)
        {
            items.Add(new NotifyIconMenuItem(Loc.Format("Detected: %@", app), Enabled: false));
            items.Add(Item(Loc.T("Stop asking for this app"), _model.StopAskingForDetectedApp));
            items.Add(NotifyIconMenuItem.Separator);
        }
        // "Settings…" would resolve to the catalog's "Settings" (same resource name): the ellipsis is added.
        items.Add(Item($"{Loc.T("Settings")}…", () =>
        {
            _model.OpenSettings();
            _openMain();
        }));
#if DEBUG
        items.Add(new NotifyIconMenuItem(Loc.T("Simulate state"), Submenu:
        [
            .. Enum.GetValues<IndicatorState>().Select(state => Item(state.ToString(), () => Simulate(state), isChecked: state == _shown)),
        ]));
#endif
        items.Add(NotifyIconMenuItem.Separator);
        items.Add(UpdateItem());
        items.Add(Item(Loc.T("About EchoDiar"), () =>
        {
            _model.OpenSettings(SettingsSearch.About);
            _openMain();
        }));
        items.Add(NotifyIconMenuItem.Separator);
        items.Add(new NotifyIconMenuItem(Loc.T("Quit"), Submenu: [Item(Loc.T("Sure to quit"), _quit)]));
        return items;
    }

    /// <summary>
    /// macOS's update item: "Check for updates…" checks and shows the answer in the window; "Update available…" asks to
    /// install it there. Off while work runs that installing would cut off (<see cref="AppViewModel.UpdateBlocked"/>, as the
    /// window's Update button), while a check or the install runs, and for a copy not installed from an .appinstaller (its
    /// text says so).
    /// </summary>
    private NotifyIconMenuItem UpdateItem()
    {
        var updater = _model.Updater;
        var enabled = updater.IsActive && !updater.IsChecking && !updater.IsInstalling && !_model.UpdateBlocked;
        return Item(updater.MenuTitle, () =>
        {
            _openMain();
            if (updater.UpdateAvailable) updater.OfferUpdate();
            else _ = updater.CheckNowAsync();
        }, enabled);
    }

    /// <summary>
    /// Elapsed time, Pause/Resume, Mute/Unmute Microphone, Stop, Language and the silent-tap item (macOS
    /// addRecordingItems).
    /// </summary>
    private void AddRecordingItems(List<NotifyIconMenuItem> items, RecordingEngine.Snapshot snapshot)
    {
        var paused = snapshot.State == RecordingEngine.State.Paused;
        items.Add(new NotifyIconMenuItem($"{Loc.T(paused ? "Paused" : "Recording")} {ConversationText.Clock(snapshot.Elapsed)}", Enabled: false));
        items.Add(Item(Loc.T(paused ? "Resume" : "Pause"), () => _ = _model.TogglePauseAsync()));
        items.Add(Item(Loc.T(snapshot.MicMuted ? "Unmute Microphone" : "Mute Microphone"), _model.ToggleMicMute));
        items.Add(Item(Loc.T("Stop recording"), () => _ = _model.StopRecordingAsync()));
        items.Add(LanguageItem());
        if (snapshot.SilentTapSuspected)
        {
            // macOS opens the System Audio Recording privacy pane, which Windows has not: the recording page shows the
            // warning and the source to pick instead.
            items.Add(Item(Loc.T("System audio is silent — Fix…"), () =>
            {
                _model.ShowRecording();
                _openMain();
            }));
        }
        items.Add(NotifyIconMenuItem.Separator);
    }

    /// <summary>The pill's language menu (<see cref="LanguageOptions.Fill"/>): groups between separators, the current one checked.</summary>
    private NotifyIconMenuItem LanguageItem()
    {
        var current = _model.RecordingLanguage;
        List<NotifyIconMenuItem> languages = [];
        foreach (var group in LanguageOptions.Groups(current, _model.Settings.RecentLanguages, _model.SupportedLanguages()))
        {
            if (languages.Count > 0) languages.Add(NotifyIconMenuItem.Separator);
            foreach (var code in group)
            {
                languages.Add(Item(LanguageOptions.Name(code), () => _model.SetRecordingLanguage(code), isChecked: code == current));
            }
        }
        return new NotifyIconMenuItem(Loc.T("Language"), Submenu: languages);
    }

    /// <summary>An item whose pick runs on the dispatcher, out of the icon's window procedure.</summary>
    private NotifyIconMenuItem Item(string text, Action action, bool enabled = true, bool isChecked = false) =>
        new(text, () => Post(action), enabled, isChecked);

    private void Post(Action action) => _dispatcher.TryEnqueue(() => action());

#if DEBUG
    private void Simulate(IndicatorState state)
    {
        _simulated = state;
        Show(state);
    }
#endif
}
