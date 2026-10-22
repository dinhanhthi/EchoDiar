using System.ComponentModel;
using System.Diagnostics;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EchoDiar.App.Views;

/// <summary>
/// The recording's card at the top of the library (macOS RecordingViews.swift RecordingCard), while recording: Stop,
/// Pause or Resume and the language in its row. The mini meters move at 15 Hz while the window is shown.
/// </summary>
public sealed partial class RecordingCard : UserControl
{
    private const string PauseGlyph = "\uE769";
    private const string ResumeGlyph = "\uE768";
    private const double MiniMeterWidth = 44;

    private readonly AppViewModel _model;
    private readonly FrameTicker _ticker;
    private bool _compact;
    private bool _selected;
    private bool? _shownPaused;

    public RecordingCard(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        _ticker = new FrameTicker(model, this, UpdateMeters);
        StopButton.Click += (_, _) => _ = _model.StopRecordingAsync();
        StopItem.Click += (_, _) => _ = _model.StopRecordingAsync();
        PauseButton.Click += (_, _) => _ = _model.TogglePauseAsync();
        PauseItem.Click += (_, _) => _ = _model.TogglePauseAsync();
        ButtonRow.Children.Add(new LanguagePicker(model, shortLabel: true));
        Tapped += OnTapped;
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            Update();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _ticker.Run(false);
        };
    }

    /// <summary>The wide layout's sidebar: smaller padding and no button row.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            ContentGrid.Padding = value ? new Thickness(12, 8, 12, 8) : new Thickness(16, 12, 16, 12);
            ButtonRow.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>The recording page is shown: the accent fill and edge.</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            SelectionFill.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            SelectionStroke.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.RecordingSnapshot):
            case nameof(AppViewModel.IsStarting):
            case nameof(AppViewModel.RecordingSourceName):
                Update();
                break;
        }
    }

    private void Update()
    {
        var snapshot = _model.RecordingSnapshot;
        var recording = snapshot.State != RecordingEngine.State.Idle;
        var paused = snapshot.State == RecordingEngine.State.Paused;
        RecordingDot.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        PausedDot.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        SetText(StateText, Loc.T(paused ? "Paused" : "Recording…"));
        SetText(ElapsedText, ConversationText.Clock(recording ? snapshot.Elapsed : 0));
        SetText(SourceText, _model.RecordingSourceName ?? Loc.T("All system audio"));
        MiniMeters.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;

        StopButton.IsEnabled = !_model.IsStarting;
        StopItem.IsEnabled = !_model.IsStarting;
        _ticker.Run(recording && !paused);
        if (!recording || paused) UpdateMeters();

        // The snapshot changes at 15 Hz: a tooltip set again each time would close under the pointer.
        if (paused == _shownPaused) return;
        _shownPaused = paused;
        PauseIcon.Glyph = paused ? ResumeGlyph : PauseGlyph;
        var pauseTitle = Loc.T(paused ? "Resume" : "Pause");
        AutomationProperties.SetName(PauseButton, pauseTitle);
        ToolTipService.SetToolTip(PauseButton, Loc.T(paused ? "Resume recording" : "Pause recording"));
        PauseItem.Text = pauseTitle;
    }

    /// <summary>The mic and system peaks the model reads at 15 Hz; empty while paused or idle.</summary>
    private void UpdateMeters()
    {
        var live = _model.RecordingSnapshot.State == RecordingEngine.State.Recording;
        MicFill.Width = MiniMeterWidth * (live ? _model.MicLevel : 0);
        SystemFill.Width = MiniMeterWidth * (live ? _model.SystemLevel : 0);
    }

    /// <summary>A click outside the buttons opens the recording page.</summary>
    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != this; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase) return;
        }
        _model.ShowRecording();
    }

    /// <summary>Only an actual change reaches the text: the elapsed time is set at 15 Hz but changes once a second.</summary>
    internal static void SetText(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }
}

/// <summary>
/// Calls <c>tick</c> from <see cref="CompositionTarget.Rendering"/> at most 15 times per second, while it runs and
/// the window is shown. Unsubscribed while stopped, while the window is hidden or minimized (back when it shows), and
/// for good once the window closes: its controls then fail every call.
/// </summary>
internal sealed class FrameTicker(AppViewModel model, FrameworkElement owner, Action tick)
{
    private static readonly long Interval = Stopwatch.Frequency * 67 / 1000;

    private bool _wanted;
    private bool _rendering;
    private XamlRoot? _root;
    private long _last;

    /// <summary>Starts or stops the ticks.</summary>
    public void Run(bool run)
    {
        _wanted = run;
        var root = run && !model.IsClosing ? owner.XamlRoot : null;
        if (root != _root)
        {
            if (_root is not null) _root.Changed -= OnRootChanged;
            _root = root;
            if (_root is not null) _root.Changed += OnRootChanged;
        }
        Sync();
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Sync();

    private void Sync()
    {
        var rendering = _wanted && !model.IsClosing && _root is { IsHostVisible: true };
        if (model.IsClosing && _root is not null)
        {
            _root.Changed -= OnRootChanged;
            _root = null;
        }
        if (rendering == _rendering) return;
        _rendering = rendering;
        if (rendering)
        {
            _last = 0;
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        if (model.IsClosing)
        {
            Sync();
            return;
        }
        var now = Stopwatch.GetTimestamp();
        if (now - _last < Interval) return;
        _last = now;
        tick();
    }
}
