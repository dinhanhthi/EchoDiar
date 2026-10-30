using System.ComponentModel;
using EchoDiar.App.ViewModels;
using EchoDiar.Platform.SystemServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace EchoDiar.App.Views;

/// <summary>
/// The detail when no entry is shown (macOS WelcomeView.swift): the list, or the recording page while it starts
/// ("Starting…" in place of the hint) or once it has ended. With an empty library and nothing recording, it invites
/// the first recording: the shortcut that starts one and Record ("Starting…" while the recording starts). Nothing
/// loads, so it shows at once.
/// </summary>
public sealed partial class WelcomeView : UserControl
{
    /// <summary>How long the placeholder shows without "Starting…": a ring shown for a frame or two reads as a flicker.</summary>
    private static readonly TimeSpan SpinnerDelay = TimeSpan.FromMilliseconds(150);

    private static readonly UISettings UISettings = new();

    private readonly AppViewModel _model;

    /// <summary>Fades "Starting…" in after <see cref="SpinnerDelay"/>.</summary>
    private readonly DispatcherQueueTimer _spinnerTimer;

    public WelcomeView(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        _spinnerTimer = DispatcherQueue.CreateTimer();
        _spinnerTimer.Interval = SpinnerDelay;
        _spinnerTimer.IsRepeating = false;
        _spinnerTimer.Tick += (t, _) =>
        {
            t.Stop();
            StartingHint.OpacityTransition = UISettings.AnimationsEnabled ? new ScalarTransition() : null;
            StartingHintRing.IsActive = true;
            StartingHint.Opacity = 1;
        };
        // The toolbar's Record, which opens the recording page: this pane stays while the recording starts (MainWindow).
        RecordButton.Click += (_, _) => _ = _model.StartRecordingAsync(_model.SelectedTarget);
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            Update();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            // Hidden, so a return to the page waits the delay again.
            ShowStartingHint(false);
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.Conversations):
            case nameof(AppViewModel.IsRecording):
            case nameof(AppViewModel.IsStarting):
            case nameof(AppViewModel.Settings):
                Update();
                break;
        }
    }

    /// <summary>Only an actual change reaches the tree: IsRecording is raised at 15 Hz while recording.</summary>
    private void Update()
    {
        // A starting recording keeps the invite, as the sidebar is still empty.
        var first = _model.Conversations.Count == 0 && !_model.IsRecording;
        var starting = _model.IsStarting;
        SetVisible(FirstRecording, first);
        ShowStartingHint(!first && starting);
        SetVisible(PickHint, !first && !starting);
        if (!first) return;

        var settings = _model.Settings;
        // The global shortcut while it is on; else Ctrl+R, which works while EchoDiar is active.
        var hint = settings.GlobalHotKeyEnabled
            ? Loc.Format("Press %@ to start recording from anywhere.", GlobalHotKeys.Describe(settings.GlobalHotKeyWindows))
            : Loc.T("Press Ctrl+R to start recording.");
        if (ShortcutHint.Text != hint) ShortcutHint.Text = hint;

        RecordButton.IsEnabled = !starting;
        SetVisible(RecordText, !starting);
        SetVisible(StartingContent, starting);
        StartingRing.IsActive = starting;
    }

    /// <summary>
    /// The recording page's placeholder until the recording runs: "Starting…" in place of the hint, faded in after
    /// <see cref="SpinnerDelay"/> (macOS StartingHint).
    /// </summary>
    private void ShowStartingHint(bool show)
    {
        if (show == (StartingHint.Visibility == Visibility.Visible)) return;
        _spinnerTimer.Stop();
        StartingHint.OpacityTransition = null;
        StartingHint.Opacity = 0;
        StartingHintRing.IsActive = false;
        SetVisible(StartingHint, show);
        if (show) _spinnerTimer.Start();
    }

    private static void SetVisible(UIElement element, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != visibility) element.Visibility = visibility;
    }
}
