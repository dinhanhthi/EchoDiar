using System.ComponentModel;
using EchoDiar.App.ViewModels;
using EchoDiar.Platform.SystemServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// The detail when no entry is shown (macOS WelcomeView.swift): the list, or the recording page while it starts or
/// after a start failed. With an empty library and nothing recording, it invites the first recording: the shortcut
/// that starts one and Record ("Starting…" while the recording starts). Nothing loads, so it shows at once.
/// </summary>
public sealed partial class WelcomeView : UserControl
{
    private readonly AppViewModel _model;

    public WelcomeView(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        // The toolbar's Record, then the recording page: this pane stays while the recording starts (MainWindow).
        RecordButton.Click += (_, _) =>
        {
            _ = _model.StartRecordingAsync(_model.SelectedTarget);
            _model.ShowRecording();
        };
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            Update();
        };
        Unloaded += (_, _) => _model.PropertyChanged -= OnModelChanged;
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
        SetVisible(FirstRecording, first);
        SetVisible(PickHint, !first);
        if (!first) return;

        var settings = _model.Settings;
        // The global shortcut while it is on; else Ctrl+R, which works while EchoDiar is active.
        var hint = settings.GlobalHotKeyEnabled
            ? Loc.Format("Press %@ to start recording from anywhere.", GlobalHotKeys.Describe(settings.GlobalHotKeyWindows))
            : Loc.T("Press Ctrl+R to start recording.");
        if (ShortcutHint.Text != hint) ShortcutHint.Text = hint;

        var starting = _model.IsStarting;
        RecordButton.IsEnabled = !starting;
        SetVisible(RecordText, !starting);
        SetVisible(StartingContent, starting);
        StartingRing.IsActive = starting;
    }

    private static void SetVisible(UIElement element, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != visibility) element.Visibility = visibility;
    }
}
