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
/// the user's drags (a press on the capsule, then the pointer read from Windows until the button is released: the window
/// has no title bar and never activates).
/// </summary>
public sealed partial class FloatingPillWindow : Window
{
    private const string PauseGlyph = "";
    private const string ResumeGlyph = "";

    /// <summary>The controls collapse this long after the pointer leaves (macOS: 1 s).</summary>
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromSeconds(1);

    /// <summary>The recording dot dims every other second (macOS RecordingBlink).</summary>
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromSeconds(1);

    /// <summary>How often a drag reads the pointer (about a frame).</summary>
    private static readonly TimeSpan DragInterval = TimeSpan.FromMilliseconds(15);

    private static readonly UISettings UISettings = new();

    private readonly AppViewModel _model;
    private readonly DispatcherQueueTimer _collapseTimer;
    private readonly DispatcherQueueTimer _blinkTimer;
    private readonly DispatcherQueueTimer _dragTimer;
    private XamlRoot? _xamlRoot;
    private bool _attached;
    private bool _closing;
    private bool _hovering;
    private bool _menuOpen;

    /// <summary>The pointer's and the window's position when the drag's press came, in physical screen pixels.</summary>
    private PointInt32 _dragStart;
    private PointInt32 _dragOrigin;

    /// <summary>How far the drag has moved the window so far.</summary>
    private PointInt32 _dragMoved;
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
        _dragTimer = DispatcherQueue.CreateTimer();
        _dragTimer.Interval = DragInterval;
        _dragTimer.Tick += (_, _) => DragStep();

        PauseButton.Click += (_, _) => _ = _model.TogglePauseAsync();
        StopButton.Click += (_, _) => _ = _model.StopRecordingAsync();
        LanguageMenu.Opening += (_, _) => LanguageOptions.Fill(LanguageMenu.Items, _model);
        LanguageMenu.Opened += (_, _) => _menuOpen = true;
        LanguageMenu.Closed += OnMenuClosed;

        RootCanvas.PointerEntered += OnPointerEntered;
        RootCanvas.PointerExited += OnPointerExited;
        Capsule.PointerPressed += OnPointerPressed;
        Capsule.SizeChanged += OnCapsuleSizeChanged;
        RootCanvas.Loaded += OnRootLoaded;
        PillWindowStyle.PrimaryPressed += OnNativePressed;
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
        _dragTimer.Stop();
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

    // Drag: the window has no title bar, so a press outside the buttons moves the window by how far the pointer went
    // from where it was pressed, until the button is released. Read from Windows, not from XAML's pointer capture: the
    // window refuses activation, which cancels XAML's capture (and its moves) on a press. The press comes from Windows
    // (PillWindowStyle.PrimaryPressed, seen before XAML) or from XAML's PointerPressed, whichever comes first.

    private nint Hwnd => Win32Interop.GetWindowFromWindowId(AppWindow.Id);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_closing || !_attached) return;
        var inButton = IsInButton(e.OriginalSource as DependencyObject);
        var left = e.GetCurrentPoint(Capsule).Properties.IsLeftButtonPressed;
        StartupTrace.Write($"Pill press (XAML): left button {left}, in a button {inButton}, dragging {_dragTimer.IsRunning}.");
        if (inButton || !left || PillWindowStyle.CursorPosition() is not { } cursor) return;
        StartDrag(cursor, "XAML");
        e.Handled = true;
    }

    /// <summary>A press as Windows delivered it to the pill's windows, in physical screen pixels.</summary>
    private void OnNativePressed(nint hwnd, PointInt32 point)
    {
        if (_closing || !_attached || hwnd != Hwnd) return;
        // After the window procedure returns: XAML is not asked from inside it.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || !_attached) return;
            var inButton = IsButtonAt(point);
            StartupTrace.Write($"Pill press (Windows) at {point.X},{point.Y}: in a button {inButton?.ToString() ?? "(outside the capsule)"}, dragging {_dragTimer.IsRunning}.");
            if (inButton == false) StartDrag(point, "Windows");
        });
    }

    /// <summary>Whether a button is at the screen point; null when the point is outside the capsule or cannot be mapped.</summary>
    private bool? IsButtonAt(PointInt32 screen)
    {
        if (_xamlRoot is null || PillWindowStyle.ToClient(Hwnd, screen) is not { } client) return null;
        var scale = _xamlRoot.RasterizationScale;
        if (scale <= 0 || !double.IsFinite(scale)) return null;
        var inCapsule = false;
        foreach (var hit in VisualTreeHelper.FindElementsInHostCoordinates(new Point(client.X / scale, client.Y / scale), Capsule))
        {
            if (hit is ButtonBase) return true;
            inCapsule |= hit == Capsule;
        }
        return inCapsule ? false : null;
    }

    private void StartDrag(PointInt32 cursor, string source)
    {
        if (_dragTimer.IsRunning) return;
        _dragStart = cursor;
        _dragOrigin = AppWindow.Position;
        _dragMoved = new PointInt32(0, 0);
        _dragTimer.Start();
        StartupTrace.Write($"Pill drag started ({source}) at {cursor.X},{cursor.Y}, the window at {_dragOrigin.X},{_dragOrigin.Y}.");
    }

    private void DragStep()
    {
        var live = !_closing && _attached;
        var down = live && PillWindowStyle.IsPrimaryButtonDown();
        if (!down || PillWindowStyle.CursorPosition() is not { } cursor)
        {
            _dragTimer.Stop();
            var reason = !live ? "the pill hid or closed" : down ? "no pointer position" : "button up";
            StartupTrace.Write($"Pill drag ended ({reason}), moved {_dragMoved.X},{_dragMoved.Y}.");
            return;
        }
        var moved = new PointInt32(cursor.X - _dragStart.X, cursor.Y - _dragStart.Y);
        if (moved.Equals(_dragMoved)) return;
        var first = _dragMoved.X == 0 && _dragMoved.Y == 0;
        _dragMoved = moved;
        var placed = PillWindowStyle.MoveTo(Hwnd, new PointInt32(_dragOrigin.X + moved.X, _dragOrigin.Y + moved.Y));
        if (first)
        {
            var at = AppWindow.Position;
            StartupTrace.Write($"Pill drag first move by {moved.X},{moved.Y}: {(placed ? "moved" : "Windows refused")}, the window at {at.X},{at.Y}.");
        }
        if (placed) Dragged?.Invoke();
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
        _dragTimer.Stop();
        RootCanvas.PointerEntered -= OnPointerEntered;
        RootCanvas.PointerExited -= OnPointerExited;
        Capsule.PointerPressed -= OnPointerPressed;
        Capsule.SizeChanged -= OnCapsuleSizeChanged;
        RootCanvas.Loaded -= OnRootLoaded;
        PillWindowStyle.PrimaryPressed -= OnNativePressed;
        LanguageMenu.Closed -= OnMenuClosed;
        if (_xamlRoot is not null) _xamlRoot.Changed -= OnXamlRootChanged;
        _xamlRoot = null;
        ContentSizeChanged = null;
        Dragged = null;
    }
}
