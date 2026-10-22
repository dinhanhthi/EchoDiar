using System.ComponentModel;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// The recording page (MVP; Phase 10 completes it): what to record, Start, Pause or Resume, Stop, the elapsed time
/// and the mic and system levels.
/// </summary>
public sealed partial class RecordingView : UserControl
{
    private const string AllSystemAudio = "All system audio";

    private readonly AppViewModel _model;

    public RecordingView(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        TargetPicker.Items.Add(new ComboBoxItem { Content = AllSystemAudio });
        TargetPicker.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            UpdateTargets();
            UpdateState();
            UpdateLevels();
            _ = _model.RefreshRecordTargetsAsync();
        };
        Unloaded += (_, _) => _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.RecordTargets):
                UpdateTargets();
                break;
            case nameof(AppViewModel.RecordingSnapshot):
            case nameof(AppViewModel.IsStarting):
            case nameof(AppViewModel.RecordingError):
                UpdateState();
                break;
            case nameof(AppViewModel.MicLevel):
            case nameof(AppViewModel.SystemLevel):
                UpdateLevels();
                break;
        }
    }

    /// <summary>"All system audio" first, then the apps with audio; the picked app stays picked when it is still listed.</summary>
    private void UpdateTargets()
    {
        var selected = ((TargetPicker.SelectedItem as ComboBoxItem)?.Tag as AudioProcessGroup)?.Exe;
        while (TargetPicker.Items.Count > 1) TargetPicker.Items.RemoveAt(TargetPicker.Items.Count - 1);
        var index = 0;
        foreach (var group in _model.RecordTargets)
        {
            TargetPicker.Items.Add(new ComboBoxItem { Content = group.Name, Tag = group });
            if (group.Exe == selected) index = TargetPicker.Items.Count - 1;
        }
        TargetPicker.SelectedIndex = index;
    }

    private void UpdateState()
    {
        var snapshot = _model.RecordingSnapshot;
        var recording = snapshot.State != RecordingEngine.State.Idle;
        var paused = snapshot.State == RecordingEngine.State.Paused;
        TargetPicker.IsEnabled = !recording && !_model.IsStarting;
        RefreshButton.IsEnabled = !recording && !_model.IsStarting;
        StartButton.IsEnabled = !recording && !_model.IsStarting;
        PauseButton.IsEnabled = recording;
        PauseButton.Content = paused ? "Resume" : "Pause";
        StopButton.IsEnabled = recording;

        var elapsed = ConversationText.Clock(recording ? snapshot.Elapsed : 0);
        ElapsedText.Text = _model.IsStarting ? "Starting…" : paused ? $"{elapsed} (paused)" : elapsed;

        var warnings = new List<string>();
        if (recording && snapshot.GlobalTapFallback) warnings.Add("This app's audio cannot be isolated, so all system audio is being recorded.");
        if (recording && snapshot.SilentTapSuspected) warnings.Add("The app is playing, but no system audio is being recorded.");
        if (recording && snapshot.LowDisk) warnings.Add("Less than 1 GB of disk space is left.");
        WarningText.Text = string.Join(Environment.NewLine, warnings);
        WarningText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ErrorText.Text = _model.RecordingError ?? "";
        ErrorText.Visibility = _model.RecordingError is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateLevels()
    {
        MicLevelBar.Value = _model.MicLevel;
        SystemLevelBar.Value = _model.SystemLevel;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = _model.RefreshRecordTargetsAsync();

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        var app = (TargetPicker.SelectedItem as ComboBoxItem)?.Tag as AudioProcessGroup;
        _ = _model.StartRecordingAsync(app);
    }

    private void OnPauseClick(object sender, RoutedEventArgs e) => _ = _model.TogglePauseAsync();

    private void OnStopClick(object sender, RoutedEventArgs e) => _ = _model.StopRecordingAsync();
}
