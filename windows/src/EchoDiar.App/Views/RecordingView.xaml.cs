using System.ComponentModel;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace EchoDiar.App.Views;

/// <summary>
/// The recording page (macOS RecordingViews.swift RecordingDetailView, RecordingHeader, ChannelMeters and
/// RecordingWave), opened from the recording's card. The meters and the wave move at 15 Hz while the window is shown
/// and the recording is not paused; the wave stands still when Windows animations are off (Reduce Motion).
/// </summary>
public sealed partial class RecordingView : UserControl
{
    private const int BarCount = 24;
    private const double WaveHeight = 64;

    private static readonly UISettings UISettings = new();

    private readonly AppViewModel _model;
    private readonly FrameTicker _ticker;
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _history = new double[BarCount];
    private int _micPercent = -1;
    private int _systemPercent = -1;
    private RecordingEngine.State? _shownState;

    public RecordingView(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        _ticker = new FrameTicker(model, this, OnTick);
        for (var i = 0; i < BarCount; i++)
        {
            _bars[i] = new Rectangle { Width = 4, Height = 4, RadiusX = 2, RadiusY = 2, VerticalAlignment = VerticalAlignment.Center };
            Wave.Children.Add(_bars[i]);
        }
        LanguageHost.Content = new LanguagePicker(model, shortLabel: false);
        GlobalTapText.Text = Loc.T("This app's audio cannot be isolated, so all system audio is being recorded.");
        AutomationProperties.SetName(MicMeter, Loc.Format("%@ level", "Mic"));
        AutomationProperties.SetName(SystemMeter, Loc.Format("%@ level", "System"));
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            Update();
            UpdateReason();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _ticker.Run(false);
            _shownState = null;
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.RecordingSnapshot):
            case nameof(AppViewModel.RecordingSourceName):
                Update();
                break;
            case nameof(AppViewModel.Settings):
                UpdateReason();
                break;
        }
    }

    private void Update()
    {
        var snapshot = _model.RecordingSnapshot;
        var recording = snapshot.State != RecordingEngine.State.Idle;
        var paused = snapshot.State == RecordingEngine.State.Paused;
        RecordingIcon.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        PausedIcon.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        RecordingCard.SetText(ElapsedText, ConversationText.Clock(recording ? snapshot.Elapsed : 0));
        RecordingCard.SetText(StateText, Loc.T(paused ? "Paused" : "Recording"));
        ChannelMeters.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;

        GlobalTapWarning.Visibility = recording && snapshot.GlobalTapFallback ? Visibility.Visible : Visibility.Collapsed;
        SilentTapBanner.Visibility = recording && snapshot.SilentTapSuspected ? Visibility.Visible : Visibility.Collapsed;
        RecordingCard.SetText(SilentTapText,
            $"EchoDiar hears nothing from {_model.RecordingSourceName ?? "the app"}. Pick another source or check that the app is playing.");

        var live = recording && !paused;
        _ticker.Run(live);
        // The snapshot changes at 15 Hz (the elapsed time): the wave is redrawn here only when the state changes.
        if (snapshot.State == _shownState) return;
        _shownState = snapshot.State;
        if (!live) Array.Clear(_history);
        UpdateWave(paused);
    }

    /// <summary>Why there is no live transcript yet; Phase 16 transcribes while recording.</summary>
    private void UpdateReason()
    {
        var settings = _model.Settings;
        ReasonText.Text = !settings.AutoTranscribe
            ? Loc.T("Transcription is off. Turn on Transcribe automatically in Settings › Recording.")
            : !settings.TranscribeWhileRecording
                ? Loc.T("Transcribe while recording is off. Turn it on in Settings › Transcription.")
                : "The recording is transcribed after Stop.";
    }

    private void OnTick()
    {
        var mic = _model.MicLevel;
        var system = _model.SystemLevel;
        MicFill.Width = MicTrack.ActualWidth * mic;
        SystemFill.Width = SystemTrack.ActualWidth * system;
        SetPercent(MicMeter, mic, ref _micPercent);
        SetPercent(SystemMeter, system, ref _systemPercent);
        if (!UISettings.AnimationsEnabled) return;
        // The louder channel's peak; a fall decays from the last bar so the wave does not flicker.
        Array.Copy(_history, 1, _history, 0, BarCount - 1);
        _history[^1] = Math.Max(Math.Max(mic, system), _history[^2] * 0.8);
        UpdateWave(paused: false);
    }

    /// <summary>Flat and grey while paused; a still shape when animations are off; else the last peaks, newest on the right.</summary>
    private void UpdateWave(bool paused)
    {
        var still = !UISettings.AnimationsEnabled;
        var fill = paused ? StateText.Foreground : RecordingIcon.Foreground;
        for (var i = 0; i < BarCount; i++)
        {
            var height = paused ? 0 : still ? 0.2 + 0.4 * Math.Abs(Math.Sin(i * 0.5)) : _history[i];
            _bars[i].Height = Math.Max(4, WaveHeight * height);
            if (_bars[i].Fill != fill) _bars[i].Fill = fill;
        }
    }

    /// <summary>The meter's value for screen readers ("42 percent"), set when the whole percent changes.</summary>
    private static void SetPercent(FrameworkElement meter, double level, ref int last)
    {
        var percent = (int)Math.Round(level * 100);
        if (percent == last) return;
        last = percent;
        AutomationProperties.SetItemStatus(meter, Loc.Format("%lld percent", percent));
    }
}
