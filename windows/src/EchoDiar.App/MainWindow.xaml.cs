using System.ComponentModel;
using EchoDiar.App.Services;
using EchoDiar.App.Themes;
using EchoDiar.App.ViewModels;
using EchoDiar.App.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace EchoDiar.App;

public sealed partial class MainWindow : Window
{
    private readonly AppViewModel _model;
    private readonly WindowFrames _frames;
    private readonly ConversationListView _library;
    private IReadOnlyList<RectInt32> _passthrough = [];

    /// <summary>
    /// Set first in <see cref="Window.Closed"/>: the window then puts its content to null and fails every call on its
    /// properties (0x800710DD) while layout and rendering events still fire, so no handler touches it after this.
    /// </summary>
    private bool _closing;

    private XamlRoot? _xamlRoot;

    /// <summary>A re-realization after lost surface contents waits for its frame: more losses meanwhile add none.</summary>
    private bool _reattachQueued;

    /// <summary>The one-shot re-realizations of the first launch, kept so they tick.</summary>
    private DispatcherQueueTimer? _presentedTimer;
    private DispatcherQueueTimer? _backstopTimer;

    public MainWindow(AppViewModel model)
    {
        StartupTrace.Write("begin MainWindow ctor");
        _model = model;
        InitializeComponent();
        StartupTrace.Write("MainWindow InitializeComponent done");
#if DEBUG
        TraceWindowEvents();
#endif
        var backdrop = TintedBackdrop.IsSupported;
#if DEBUG
        if (App.NoBackdrop) backdrop = false;
#endif
        StartupTrace.Write($"backdrop: supported {TintedBackdrop.IsSupported}, used {backdrop}");
        if (backdrop)
        {
            SystemBackdrop = new TintedBackdrop();
        }
        else
        {
            RootGrid.Background = new SolidColorBrush(TintedBackdrop.Tint);
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.ButtonBackgroundColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00);
        titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

        TitleBarGrid.SizeChanged += OnTitleBarSizeChanged;
        TitleBarGrid.LayoutUpdated += OnTitleBarLayoutUpdated;

        Toolbar.Attach(_model);
        // One library for both layouts: the whole page on compact, the sidebar on wide. Its deletes ask through the
        // toolbar, which shows the window's dialogs one at a time.
        _library = new ConversationListView(_model) { Delete = Toolbar.DeleteAsync };
        ListHost.Content = _library;

        _frames = new WindowFrames(AppWindow);
        StartupTrace.Write("begin Place");
        _frames.Place(WindowFrames.For(_model.Route));
        StartupTrace.Write("end Place");
        // A move to a display of another DPI changes the presenter's bounds in physical pixels.
        RootGrid.Loaded += OnRootLoaded;
        Closed += OnClosed;
        Activated += OnFirstActivated;
        CompositionTarget.SurfaceContentsLost += OnSurfaceContentsLost;

        _model.PropertyChanged += OnModelChanged;
        ShowRoute();
        UpdateGlobalTapNote();
        StartupTrace.Write("end MainWindow ctor");
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        StartupTrace.Write("RootGrid.Loaded");
        if (_xamlRoot is not null || RootGrid.XamlRoot is not { } root) return;
        _xamlRoot = root;
        root.Changed += OnXamlRootChanged;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_closing) return;
        StartupTrace.Write($"XamlRoot.Changed scale {sender.RasterizationScale}, size {sender.Size.Width}x{sender.Size.Height}, host visible {sender.IsHostVisible}");
        _frames.ApplyBounds();
    }

    /// <summary>Saves the frame while the window still answers, then lets go of everything that would call it.</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closing = true;
        StartupTrace.Write("Closed");
        CompositionTarget.SurfaceContentsLost -= OnSurfaceContentsLost;
#if DEBUG
        UntraceWindowEvents();
#endif
        TitleBarGrid.SizeChanged -= OnTitleBarSizeChanged;
        TitleBarGrid.LayoutUpdated -= OnTitleBarLayoutUpdated;
        if (_xamlRoot is not null) _xamlRoot.Changed -= OnXamlRootChanged;
        _model.PropertyChanged -= OnModelChanged;
        Toolbar.Detach();
        _frames.Save();
        _frames.Detach();
    }

    /// <summary>
    /// The text of the first frame can be lost while the window is first shown (seen in a VM on warm launches: the
    /// toolbar's icons and labels and the library header stayed blank, while backgrounds and later text drew). A text
    /// element makes its glyphs again only when it leaves the tree and enters it again, so the toolbar's and the
    /// library's content are taken out and put back: 300 ms after the first presented frame (the first Rendering whose
    /// rendering time has started; the loss comes after the earlier ones), and once more 1.5 s after the window shows.
    /// </summary>
    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        Activated -= OnFirstActivated;
        CompositionTarget.Rendering += OnFirstPresentedRendering;
        _backstopTimer = StartOnce(TimeSpan.FromSeconds(1.5), "backstop +1.5 s");
    }

    private void OnFirstPresentedRendering(object? sender, object e)
    {
        if (!_closing && (e is not RenderingEventArgs args || args.RenderingTime <= TimeSpan.Zero)) return;
        CompositionTarget.Rendering -= OnFirstPresentedRendering;
        if (_closing) return;
        StartupTrace.Write("first presented frame");
        _presentedTimer = StartOnce(TimeSpan.FromMilliseconds(300), "after first presented frame +300 ms");
    }

    private DispatcherQueueTimer StartOnce(TimeSpan delay, string reason)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            ReattachIfShown(reason);
        };
        timer.Start();
        return timer;
    }

    /// <summary>Skipped while closing, while minimized or hidden, and while a re-realization after lost contents waits.</summary>
    private void ReattachIfShown(string reason)
    {
        if (_closing) return;
        var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (_reattachQueued || minimized || !AppWindow.IsVisible)
        {
            StartupTrace.Write($"skip reattach {reason} (queued {_reattachQueued}, minimized {minimized}, visible {AppWindow.IsVisible})");
            return;
        }
        ReattachChrome(reason);
    }

    /// <summary>
    /// Lost surface contents (the graphics device was lost or its surfaces were trimmed) leave the same blank text: the
    /// toolbar and the library are put back once the next frame is drawn.
    /// </summary>
    private void OnSurfaceContentsLost(object? sender, object e)
    {
        StartupTrace.Write($"SurfaceContentsLost (closing {_closing}, queued {_reattachQueued})");
        if (_closing || _reattachQueued) return;
        _reattachQueued = true;
        AfterNextFrame(() =>
        {
            _reattachQueued = false;
            ReattachChrome("after lost surface contents");
        });
    }

    private void ReattachChrome(string reason)
    {
        StartupTrace.Write($"begin reattach {reason}");
        Reattach(Toolbar);
        Reattach(_library);
        StartupTrace.Write("end reattach");
    }

    /// <summary>In the same tick, so no layout pass sees it out; the control's own Loaded and Unloaded do not fire.</summary>
    private static void Reattach(UserControl control)
    {
        var content = control.Content;
        control.Content = null;
        control.Content = content;
    }

    /// <summary>Runs <paramref name="action"/> once a frame has been drawn, unless the window closed meanwhile.</summary>
    private void AfterNextFrame(Action action)
    {
        var frames = 0;
        void OnRendering(object? sender, object e)
        {
            // Rendering comes before its frame is drawn: the second one follows a drawn frame.
            if (++frames < 2 && !_closing) return;
            CompositionTarget.Rendering -= OnRendering;
            if (!_closing) action();
        }
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.Route):
                ShowRoute();
                break;
            case nameof(AppViewModel.SelectedTarget):
            case nameof(AppViewModel.RecordingSnapshot):
                UpdateGlobalTapNote();
                break;
        }
    }

    /// <summary>Shown while idle when the picked app needs the global capture (macOS GlobalTapNote).</summary>
    private void UpdateGlobalTapNote()
    {
        var app = _model.IsRecording ? null : _model.SelectedTarget;
        GlobalTapNote.Visibility = app is { NeedsGlobalTap: true } ? Visibility.Visible : Visibility.Collapsed;
        if (app is { NeedsGlobalTap: true })
        {
            RecordingCard.SetText(GlobalTapNoteText, Loc.Format("%@ cannot be recorded on its own: all system audio will be recorded.", app.Name));
        }
    }

    /// <summary>Shows the route's page in its layout, then resizes the window to that layout.</summary>
    private void ShowRoute()
    {
        var route = _model.Route;
        RouteHost.Content = route switch
        {
            { Kind: RouteKind.Recording } => new RecordingView(_model),
            { Kind: RouteKind.Conversation, ConversationId: { } id } => new ConversationView(_model, id),
            _ => null,
        };
        var layout = WindowFrames.For(route);
        var wide = layout == MainWindowLayout.Wide;
        SidebarColumn.Width = wide ? new GridLength(360) : new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RouteHost.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        _library.ShowAsSidebar(wide, route.ConversationId);
        _frames.SwitchTo(layout);
    }

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarRegions();

    private void OnTitleBarLayoutUpdated(object? sender, object e) => UpdateTitleBarRegions();

    // Keeps the toolbar's controls clickable: everything else in the title bar row drags the window.
    private void UpdateTitleBarRegions()
    {
        // RootGrid's, not Content's: Window.Content throws once the window is closed.
        if (_closing || RootGrid.XamlRoot is not { } root)
        {
            return;
        }

        var scale = root.RasterizationScale;
        SetWidth(LeftInsetColumn, AppWindow.TitleBar.LeftInset / scale);
        SetWidth(RightInsetColumn, AppWindow.TitleBar.RightInset / scale);

        // LayoutUpdated fires often: only a change of the rects goes to the window.
        var rects = Toolbar.PassthroughRects(scale);
        if (rects.SequenceEqual(_passthrough))
        {
            return;
        }

        _passthrough = rects;
        StartupTrace.Write($"SetRegionRects ({rects.Count} rects)");
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
    }

    private static void SetWidth(ColumnDefinition column, double width)
    {
        var length = new GridLength(width);
        if (!column.Width.Equals(length))
        {
            column.Width = length;
        }
    }

#if DEBUG
    private int _renderingTicks;

    /// <summary>The window's events for <see cref="StartupTrace"/>; the first ten frames only, so the trace keeps few frames forced.</summary>
    private void TraceWindowEvents()
    {
        CompositionTarget.Rendering += OnTraceRendering;
        Activated += OnTraceActivated;
        VisibilityChanged += OnTraceVisibilityChanged;
        AppWindow.Changed += OnTraceAppWindowChanged;
    }

    private void UntraceWindowEvents()
    {
        CompositionTarget.Rendering -= OnTraceRendering;
        Activated -= OnTraceActivated;
        VisibilityChanged -= OnTraceVisibilityChanged;
        AppWindow.Changed -= OnTraceAppWindowChanged;
    }

    private void OnTraceRendering(object? sender, object e)
    {
        var time = e is RenderingEventArgs args ? args.RenderingTime.TotalMilliseconds : -1;
        StartupTrace.Write($"Rendering tick {++_renderingTicks} (rendering time {time:0.0} ms)");
        if (_renderingTicks >= 10) CompositionTarget.Rendering -= OnTraceRendering;
    }

    private void OnTraceActivated(object sender, WindowActivatedEventArgs args) =>
        StartupTrace.Write($"Activated {args.WindowActivationState}");

    private void OnTraceVisibilityChanged(object sender, WindowVisibilityChangedEventArgs args) =>
        StartupTrace.Write($"VisibilityChanged visible {args.Visible}");

    private void OnTraceAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_closing) return;
        var state = sender.Presenter is OverlappedPresenter presenter ? presenter.State.ToString() : sender.Presenter.Kind.ToString();
        StartupTrace.Write(
            $"AppWindow.Changed size {args.DidSizeChange} position {args.DidPositionChange} presenter {args.DidPresenterChange} " +
            $"visibility {args.DidVisibilityChange}: {sender.Position.X},{sender.Position.Y} {sender.Size.Width}x{sender.Size.Height} " +
            $"{state} visible {sender.IsVisible}");
    }
#endif
}
