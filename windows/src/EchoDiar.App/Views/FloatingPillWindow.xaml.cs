using System.ComponentModel;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Windowing;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace EchoDiar.App.Views;

/// <summary>
/// The floating pill's window and content (macOS FloatingPillView.swift and PillHostingView): the recording
/// indicator, its controls on hover, and the slot for Phase 20's prompts. <see cref="Services.FloatingPillController"/>
/// owns it: it styles, shows, hides, places and closes it. The window reports its content's size in physical pixels and
/// the user's drags (pointer capture on the capsule: the window has no title bar). A press may make the pill the
/// foreground window: once the press is over it gives the foreground back to the window that had it.
/// </summary>
public sealed partial class FloatingPillWindow : Window
{
    private const string PauseGlyph = "";
    private const string ResumeGlyph = "";

    /// <summary>The controls collapse this long after the pointer leaves (macOS: 1 s).</summary>
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromSeconds(1);

    /// <summary>The recording dot dims every other second (macOS RecordingBlink).</summary>
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromSeconds(1);

    private static readonly UISettings UISettings = new();

    private readonly AppViewModel _model;
    private readonly DispatcherQueueTimer _collapseTimer;
    private readonly DispatcherQueueTimer _blinkTimer;
    private XamlRoot? _xamlRoot;
    private bool _attached;
    private bool _closing;
    private bool _hovering;
    private bool _menuOpen;
    private bool _dragging;
    private Point _pressed;

    /// <summary>The drag moved the window at least once (traced once per drag).</summary>
    private bool _dragMoved;

    /// <summary>The foreground window before the pointer came to the pill, which gets it back after a press; 0 = none.</summary>
    private nint _returnTo;

    /// <summary>Kept to remove the same handler it added.</summary>
    private readonly PointerEventHandler _anyReleased;
    private double _scale;
    private bool? _shownPaused;
    private SizeInt32 _reportedSize;

    public FloatingPillWindow(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        _collapseTimer = DispatcherQueue.CreateTimer();
        _collapseTimer.Interval = CollapseDelay;
        _collapseTimer.IsRepeating = false;
        _collapseTimer.Tick += (_, _) => Collapse();
        _blinkTimer = DispatcherQueue.CreateTimer();
        _blinkTimer.Interval = BlinkInterval;
        _blinkTimer.Tick += (_, _) => Blink();

        PauseButton.Click += (_, _) => _ = _model.TogglePauseAsync();
        StopButton.Click += (_, _) => _ = _model.StopRecordingAsync();
        LanguageMenu.Opening += (_, _) =>
        {
            // At once, not at Opened: a hand-back queued by the button's release must not close the menu.
            _menuOpen = true;
            LanguageOptions.Fill(LanguageMenu.Items, _model);
        };
        LanguageMenu.Closed += OnMenuClosed;

        RootCanvas.PointerEntered += OnPointerEntered;
        RootCanvas.PointerExited += OnPointerExited;
        Capsule.PointerPressed += OnPointerPressed;
        Capsule.PointerMoved += OnPointerMoved;
        Capsule.PointerReleased += OnPointerReleased;
        Capsule.PointerCaptureLost += OnPointerCaptureLost;
        // Every release, the buttons' (handled) too: the press is over.
        _anyReleased = OnAnyPointerReleased;
        RootCanvas.AddHandler(UIElement.PointerReleasedEvent, _anyReleased, true);
        Capsule.SizeChanged += OnCapsuleSizeChanged;
        RootCanvas.Loaded += OnRootLoaded;
        Closed += OnClosed;
    }

    /// <summary>The capsule's size in physical pixels changed (content, size setting, DPI): the window must match.</summary>
    public event Action<SizeInt32>? ContentSizeChanged;

    /// <summary>The user dragged the pill (its window has moved).</summary>
    public event Action? Dragged;

    /// <summary>The capsule's size in physical pixels; empty before its first layout.</summary>
    public SizeInt32 ContentSize => _reportedSize;

    /// <summary>The pill shows: it follows the model until <see cref="Detach"/>.</summary>
    public void Attach()
    {
        if (_attached || _closing) return;
        _attached = true;
        _model.PropertyChanged += OnModelChanged;
        _shownPaused = null;
        Update();
    }

    /// <summary>The pill hides or closes: no more model changes, timers or drag; the controls collapse.</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _model.PropertyChanged -= OnModelChanged;
        _blinkTimer.Stop();
        _collapseTimer.Stop();
        _hovering = false;
        _dragging = false;
        if (!_closing)
        {
            RecordingDot.Opacity = 1;
            Controls.Visibility = Visibility.Collapsed;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closing || _model.IsClosing) return;
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.MicLevel):
            case nameof(AppViewModel.SystemLevel):
                UpdateMeter();
                break;
            case nameof(AppViewModel.RecordingSnapshot):
            case nameof(AppViewModel.Settings):
            case nameof(AppViewModel.AutoStartedAppName):
            case nameof(AppViewModel.PillPrompt):
                Update();
                break;
        }
    }

    private void Update()
    {
        var prompt = _model.PillPrompt;
        Indicator.Visibility = prompt is null ? Visibility.Visible : Visibility.Collapsed;
        PromptHost.Visibility = prompt is null ? Visibility.Collapsed : Visibility.Visible;
        // Prompts keep one size (Phase 20 fills PromptHost).
        ApplyScale(prompt is null ? Scale(_model.Settings.PillSize) : 1);
        Capsule.Height = prompt is null ? 28 * _scale : double.NaN;
        if (prompt is not null)
        {
            UpdateBlink(false);
            return;
        }

        var snapshot = _model.RecordingSnapshot;
        var recording = snapshot.State != RecordingEngine.State.Idle;
        var paused = snapshot.State == RecordingEngine.State.Paused;
        RecordingDot.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        PausedDot.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        if (_model.AutoStartedAppName is { } name)
        {
            RecordingCard.SetText(SourceText, Loc.Format("Recording %@", name));
            SourceText.Visibility = Visibility.Visible;
        }
        else
        {
            SourceText.Visibility = Visibility.Collapsed;
        }
        var elapsed = ConversationText.Clock(recording ? snapshot.Elapsed : 0);
        RecordingCard.SetText(ElapsedText, elapsed);
        RecordingCard.SetText(PausedElapsedText, elapsed);
        ElapsedText.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        PausedElapsedText.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        MeterTrack.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        UpdateBlink(recording && !paused);
        UpdateMeter();

        // The snapshot changes at 15 Hz: a tooltip set again each time would close under the pointer.
        if (paused == _shownPaused) return;
        _shownPaused = paused;
        AutomationProperties.SetName(DotGrid, Loc.T(paused ? "Paused" : "Recording"));
        PauseIcon.Glyph = paused ? ResumeGlyph : PauseGlyph;
        AutomationProperties.SetName(PauseButton, Loc.T(paused ? "Resume" : "Pause"));
        ToolTipService.SetToolTip(PauseButton, Loc.T(paused ? "Resume recording" : "Pause recording"));
    }

    /// <summary>The louder channel's peak (macOS PillMeter), from the levels the model reads at 15 Hz.</summary>
    private void UpdateMeter()
    {
        var live = _model.RecordingSnapshot.State == RecordingEngine.State.Recording;
        MeterFill.Width = MeterTrack.Width * (live ? Math.Max(_model.MicLevel, _model.SystemLevel) : 0);
    }

    /// <summary>The indicator's metrics for the size setting (macOS <c>PillSize.scale</c>; small is the original look).</summary>
    private static double Scale(PillSize size) => size switch
    {
        PillSize.Small => 1,
        PillSize.Large => 1.5,
        _ => 1.25,
    };

    private void ApplyScale(double scale)
    {
        if (scale == _scale) return;
        _scale = scale;
        Capsule.CornerRadius = new CornerRadius(14 * scale);
        Capsule.Padding = new Thickness(12 * scale, 0, 12 * scale, 0);
        Indicator.Spacing = 8 * scale;
        Logo.Width = Logo.Height = 16 * scale;
        RecordingDot.Width = RecordingDot.Height = 9 * scale;
        PausedDot.Width = PausedDot.Height = 9 * scale;
        SourceText.FontSize = ElapsedText.FontSize = PausedElapsedText.FontSize = 12 * scale;
        MeterTrack.Width = 28 * scale;
        MeterTrack.Height = 4 * scale;
        MeterTrack.CornerRadius = MeterFill.CornerRadius = new CornerRadius(2 * scale);
        // Icon-only buttons: their padding stands in for macOS's 12 pt spacing.
        Controls.Spacing = 4 * scale;
        foreach (var button in new[] { PauseButton, StopButton, LanguageButton }) button.Padding = new Thickness(4 * scale);
        PauseIcon.FontSize = StopIcon.FontSize = GlobeIcon.FontSize = 13 * scale;
        UpdateMeter();
    }

    // Hover: the controls show at once and collapse 1 s after the pointer leaves, not while the language menu is open.

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_closing || !_attached) return;
        _hovering = true;
        RememberForeground();
        _collapseTimer.Stop();
        Controls.Visibility = Visibility.Visible;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_closing || !_attached) return;
        _hovering = false;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void OnMenuClosed(object? sender, object e)
    {
        _menuOpen = false;
        if (!_closing) HandBackForeground("the language menu closed");
        if (_closing || !_attached || _hovering) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void Collapse()
    {
        if (_closing || _hovering || _menuOpen) return;
        Controls.Visibility = Visibility.Collapsed;
    }

    /// <summary>The recording dot blinks while recording, unless paused or Windows animations are off.</summary>
    private void UpdateBlink(bool active)
    {
        active &= UISettings.AnimationsEnabled;
        if (active && !_blinkTimer.IsRunning) _blinkTimer.Start();
        if (!active)
        {
            _blinkTimer.Stop();
            RecordingDot.Opacity = 1;
        }
    }

    private void Blink()
    {
        if (_closing || !_attached) return;
        RecordingDot.Opacity = RecordingDot.Opacity < 1 ? 1 : 0.35;
    }

    // Drag: the window has no title bar, so a press outside the buttons captures the pointer and moves the window by
    // how far the pointer left its press point (the window moves under it, so that point stays put relative to it).

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_closing || !_attached) return;
        RememberForeground();
        var inButton = IsInButton(e.OriginalSource as DependencyObject);
        var point = e.GetCurrentPoint(Capsule);
        StartupTrace.Write($"Pill press: left button {point.Properties.IsLeftButtonPressed}, in a button {inButton}.");
        if (inButton || !point.Properties.IsLeftButtonPressed) return;
        if (!Capsule.CapturePointer(e.Pointer))
        {
            StartupTrace.Write("Pill drag not started: the pointer could not be captured.");
            return;
        }
        _pressed = point.Position;
        _dragging = true;
        _dragMoved = false;
        StartupTrace.Write($"Pill drag started at {AppWindow.Position.X},{AppWindow.Position.Y}.");
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _closing || _xamlRoot is null) return;
        var position = e.GetCurrentPoint(Capsule).Position;
        var scale = _xamlRoot.RasterizationScale;
        var dx = (int)Math.Round((position.X - _pressed.X) * scale);
        var dy = (int)Math.Round((position.Y - _pressed.Y) * scale);
        if (dx == 0 && dy == 0) return;
        var origin = AppWindow.Position;
        AppWindow.Move(new PointInt32(origin.X + dx, origin.Y + dy));
        if (!_dragMoved)
        {
            _dragMoved = true;
            StartupTrace.Write($"Pill drag first move by {dx},{dy}: the window at {AppWindow.Position.X},{AppWindow.Position.Y}.");
        }
        Dragged?.Invoke();
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (!_closing) Capsule.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        EndDrag("released");
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        EndDrag("capture lost");
    }

    private void EndDrag(string how)
    {
        StartupTrace.Write($"Pill drag ended ({how}), the window at {(_closing ? "?" : $"{AppWindow.Position.X},{AppWindow.Position.Y}")}.");
        if (!_closing) HandBackForeground("a drag");
    }

    /// <summary>
    /// A press on the pill (a button's included) is over: after the click is handled, the foreground goes back. Not
    /// for the language button, whose menu opens now: its closing gives the foreground back.
    /// </summary>
    private void OnAnyPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_closing || !_attached || IsIn(e.OriginalSource as DependencyObject, LanguageButton)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing) HandBackForeground("a press");
        });
    }

    // Foreground: a press can make the pill the foreground window (WS_EX_NOACTIVATE does not hold for every
    // activation), which would take Alt+F4 and typing from the meeting app. The window that had the foreground before
    // the pointer came gets it back once the press is over, unless the language menu is open (it would close).

    private nint Hwnd => Win32Interop.GetWindowFromWindowId(AppWindow.Id);

    private void RememberForeground()
    {
        var foreground = PillWindowStyle.ForegroundWindow();
        if (foreground != 0 && !PillWindowStyle.IsPillOrOwned(foreground, Hwnd)) _returnTo = foreground;
    }

    private void HandBackForeground(string after)
    {
        if (_dragging || _menuOpen || _returnTo == 0) return;
        if (!PillWindowStyle.IsPillOrOwned(PillWindowStyle.ForegroundWindow(), Hwnd)) return;
        var given = PillWindowStyle.SetForeground(_returnTo);
        StartupTrace.Write($"Pill hand-back after {after}: {(given ? "the window before has the foreground again" : "the window before is gone or Windows refused")}.");
    }

    private static bool IsIn(DependencyObject? element, DependencyObject ancestor)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element == ancestor) return true;
        }
        return false;
    }

    private bool IsInButton(DependencyObject? element)
    {
        for (; element is not null && element != Capsule; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase) return true;
        }
        return false;
    }

    // Size

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_closing || _xamlRoot is not null || RootCanvas.XamlRoot is not { } root) return;
        _xamlRoot = root;
        root.Changed += OnXamlRootChanged;
        ReportSize();
    }

    /// <summary>A move to a monitor of another DPI: the same DIPs are another number of pixels.</summary>
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!_closing) ReportSize();
    }

    private void OnCapsuleSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_closing) ReportSize();
    }

    private void ReportSize()
    {
        if (_xamlRoot is null) return;
        var scale = _xamlRoot.RasterizationScale;
        if (scale <= 0 || !double.IsFinite(scale)) return;
        var size = new SizeInt32(Pixels(Capsule.ActualWidth, scale), Pixels(Capsule.ActualHeight, scale));
        if (size.Width <= 0 || size.Height <= 0 || size.Equals(_reportedSize)) return;
        _reportedSize = size;
        ContentSizeChanged?.Invoke(size);
    }

    /// <summary>Layout rounding leaves whole pixels; the tolerance keeps a rounding error from adding one.</summary>
    private static int Pixels(double dips, double scale) => (int)Math.Ceiling(dips * scale - 0.01);

    /// <summary>Set first: the window then fails every call on its content, so nothing touches it after this.</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closing = true;
        Detach();
        _collapseTimer.Stop();
        _blinkTimer.Stop();
        RootCanvas.PointerEntered -= OnPointerEntered;
        RootCanvas.PointerExited -= OnPointerExited;
        Capsule.PointerPressed -= OnPointerPressed;
        Capsule.PointerMoved -= OnPointerMoved;
        Capsule.PointerReleased -= OnPointerReleased;
        Capsule.PointerCaptureLost -= OnPointerCaptureLost;
        RootCanvas.RemoveHandler(UIElement.PointerReleasedEvent, _anyReleased);
        Capsule.SizeChanged -= OnCapsuleSizeChanged;
        RootCanvas.Loaded -= OnRootLoaded;
        LanguageMenu.Closed -= OnMenuClosed;
        if (_xamlRoot is not null) _xamlRoot.Changed -= OnXamlRootChanged;
        _xamlRoot = null;
        ContentSizeChanged = null;
        Dragged = null;
    }
}
