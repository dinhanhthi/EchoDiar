using System.ComponentModel;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.App.Views;
using EchoDiar.Core.Settings;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

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

    /// <summary>The in-app shortcuts without modifiers (Esc, Space), run from <see cref="OnRootPreviewKeyDown"/>.</summary>
    private readonly Dictionary<VirtualKey, InAppAction> _plainKeys = [];

    /// <summary>The last press of a <see cref="_plainKeys"/> key ran its action: its repeats are taken too.</summary>
    private bool _plainKeyHandled;

    public MainWindow(AppViewModel model)
    {
        StartupTrace.Write("begin MainWindow ctor");
        _model = model;
        InitializeComponent();
        StartupTrace.Write("MainWindow InitializeComponent done");
        // Taskbar, Alt+Tab and title bar icon (windows/tools/make-icons.sh makes it from assets/logo.png).
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "EchoDiar.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
#if DEBUG
        TraceWindowEvents();
#endif
        // Standard Mica, else Desktop Acrylic; with neither, RootGrid keeps the theme's solid background (XAML).
        SystemBackdrop? backdrop = MicaController.IsSupported() ? new MicaBackdrop { Kind = MicaKind.Base }
            : DesktopAcrylicController.IsSupported() ? new DesktopAcrylicBackdrop()
            : null;
#if DEBUG
        if (App.NoBackdrop) backdrop = null;
#endif
        StartupTrace.Write(
            $"backdrop: mica {MicaController.IsSupported()}, acrylic {DesktopAcrylicController.IsSupported()}, used {backdrop?.GetType().Name ?? "solid"}");
        if (backdrop is not null)
        {
            SystemBackdrop = backdrop;
            RootGrid.Background = null;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        ApplyCaptionButtonColors();
        RootGrid.ActualThemeChanged += OnActualThemeChanged;

        TitleBarGrid.SizeChanged += OnTitleBarSizeChanged;
        TitleBarGrid.LayoutUpdated += OnTitleBarLayoutUpdated;

        Toolbar.Attach(_model);
        // One library for both layouts: the whole page on compact, the sidebar on wide. Its deletes ask through the
        // toolbar, which shows the window's dialogs one at a time.
        _library = new ConversationListView(_model) { Delete = Toolbar.DeleteAsync };
        ListHost.Content = _library;
        AddAccelerators();

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

    /// <summary>Windows switched Light and Dark: the caption buttons follow (the views follow through their ThemeResources).</summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_closing) return;
        ApplyCaptionButtonColors();
    }

    /// <summary>
    /// The caption buttons (minimize, maximize, close) in the current theme: transparent at rest, the subtle fills on
    /// hover and press, the primary text colour (disabled text while the window is inactive).
    /// </summary>
    private void ApplyCaptionButtonColors()
    {
        var theme = RootGrid.ActualTheme;
        var titleBar = AppWindow.TitleBar;
        var transparent = ThemeColor("SubtleFillColorTransparent", theme);
        var foreground = ThemeColor("TextFillColorPrimary", theme);
        titleBar.ButtonBackgroundColor = transparent;
        titleBar.ButtonInactiveBackgroundColor = transparent;
        titleBar.ButtonHoverBackgroundColor = ThemeColor("SubtleFillColorSecondary", theme);
        titleBar.ButtonPressedBackgroundColor = ThemeColor("SubtleFillColorTertiary", theme);
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = ThemeColor("TextFillColorSecondary", theme);
        titleBar.ButtonInactiveForegroundColor = ThemeColor("TextFillColorDisabled", theme);
        StartupTrace.Write($"caption colours for {theme}: foreground {foreground}");
    }

    /// <summary>
    /// <paramref name="key"/>'s colour in the theme dictionary of <paramref name="theme"/> (WinUI keeps Dark under
    /// "Default"). The app-level lookup is the fallback only: it keeps the theme the app started in.
    /// </summary>
    internal static Color ThemeColor(string key, ElementTheme theme)
    {
        string[] names = theme == ElementTheme.Light ? ["Light"] : ["Dark", "Default"];
        if (FindThemeValue(Application.Current.Resources, key, names) is Color color) return color;
        StartupTrace.Write($"theme colour {key} for {theme}: app-level lookup");
        return (Color)Application.Current.Resources[key];
    }

    private static object? FindThemeValue(ResourceDictionary dictionary, string key, string[] names)
    {
        foreach (var name in names)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(name, out var found)
                && found is ResourceDictionary themed
                && themed.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (FindThemeValue(merged, key, names) is { } value) return value;
        }

        return null;
    }

    /// <summary>Saves the frame of the shown layout (the window is hidden to the tray: the process may end before it closes).</summary>
    internal void SaveFrame()
    {
        if (!_closing) _frames.Save();
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
        RootGrid.ActualThemeChanged -= OnActualThemeChanged;
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
        // A closed InfoBar keeps its margin: collapsed too, so the page does not move down while it is closed.
        GlobalTapNote.IsOpen = app is { NeedsGlobalTap: true };
        GlobalTapNote.Visibility = GlobalTapNote.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        if (app is { NeedsGlobalTap: true })
        {
            var message = Loc.Format("%@ cannot be recorded on its own: all system audio will be recorded.", app.Name);
            if (GlobalTapNote.Message != message) GlobalTapNote.Message = message;
        }
    }

    /// <summary>
    /// Shows the route's page in its layout, then resizes the window to that layout. Settings takes the whole wide
    /// window, with its own sidebar instead of the library (macOS MainView.swift).
    /// </summary>
    private void ShowRoute()
    {
        var route = _model.Route;
        RouteHost.Content = route switch
        {
            { Kind: RouteKind.Recording } => new RecordingView(_model),
            { Kind: RouteKind.Conversation, ConversationId: { } id } => new ConversationView(_model, id),
            { Kind: RouteKind.Settings } => new SettingsPage(_model),
            _ => null,
        };
        var layout = WindowFrames.For(route);
        var wide = layout == MainWindowLayout.Wide;
        var settings = route.Kind == RouteKind.Settings;
        SidebarColumn.Width = settings ? new GridLength(0) : wide ? new GridLength(360) : new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ListHost.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        RouteHost.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        _library.ShowAsSidebar(wide, route.ConversationId);
        _frames.SwitchTo(layout);
    }

    // In-app shortcuts

    /// <summary>
    /// The in-app shortcuts (<see cref="ShortcutsSettingsPage.InAppShortcuts"/>: the macOS main menu's keys with Ctrl for
    /// ⌘, and its main window's Esc and Space) as the window's keyboard accelerators; Esc and Space in
    /// <see cref="OnRootPreviewKeyDown"/> instead.
    /// </summary>
    private void AddAccelerators()
    {
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        foreach (var shortcut in ShortcutsSettingsPage.InAppShortcuts)
        {
            foreach (var key in shortcut.Keys)
            {
                if (key.Modifiers == 0)
                {
                    _plainKeys[(VirtualKey)key.VirtualKey] = shortcut.Action;
                    continue;
                }
                var accelerator = new KeyboardAccelerator { Key = (VirtualKey)key.VirtualKey, Modifiers = Modifiers(key.Modifiers) };
                var action = shortcut.Action;
                accelerator.Invoked += (_, args) => args.Handled = Run(action, plain: false);
                RootGrid.KeyboardAccelerators.Add(accelerator);
            }
        }
        RootGrid.PreviewKeyDown += OnRootPreviewKeyDown;
    }

    /// <summary>
    /// Esc and Space before the focused control sees them: a list item or a button marks them handled in its KeyDown,
    /// which comes before keyboard accelerators, so a clicked card or button kept Space (<see cref="TakesSpace"/> decides
    /// which control keeps it). Not with a modifier held; a held key's repeats do what its press did, once.
    /// </summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_plainKeys.TryGetValue(e.Key, out var action)) return;
        if (e.KeyStatus.WasKeyDown)
        {
            if (_plainKeyHandled) e.Handled = true;
            return;
        }
        _plainKeyHandled = !ModifierDown(e) && Run(action, plain: true);
        if (_plainKeyHandled) e.Handled = true;
    }

    private static bool ModifierDown(KeyRoutedEventArgs e) => e.KeyStatus.IsMenuKeyDown
        || ConversationListView.IsKeyDown(VirtualKey.Control) || ConversationListView.IsKeyDown(VirtualKey.Menu)
        || ConversationListView.IsKeyDown(VirtualKey.Shift) || ConversationListView.IsKeyDown(VirtualKey.LeftWindows)
        || ConversationListView.IsKeyDown(VirtualKey.RightWindows);

    private static VirtualKeyModifiers Modifiers(uint modifiers)
    {
        var result = VirtualKeyModifiers.None;
        if ((modifiers & WindowsHotKey.ModControl) != 0) result |= VirtualKeyModifiers.Control;
        if ((modifiers & WindowsHotKey.ModAlt) != 0) result |= VirtualKeyModifiers.Menu;
        if ((modifiers & WindowsHotKey.ModShift) != 0) result |= VirtualKeyModifiers.Shift;
        if ((modifiers & WindowsHotKey.ModWin) != 0) result |= VirtualKeyModifiers.Windows;
        return result;
    }

    /// <summary>
    /// Runs <paramref name="action"/> where it applies (macOS validateMenuItem and handleKey); false passes the key on to
    /// the focused control. Nothing runs while a recorder in Settings › Shortcuts takes a new global shortcut, or while a
    /// dialog, menu or flyout is open; Esc and Space (<paramref name="plain"/>) are a text field's while it has the focus.
    /// </summary>
    private bool Run(InAppAction action, bool plain)
    {
        if (_closing || _model.IsCapturingHotKey || PopupOpen()) return false;
        var focused = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        if (plain && focused is TextBox) return false;
        var route = _model.Route.Kind;
        switch (action)
        {
            case InAppAction.RecordStop:
                _model.ToggleRecording();
                return true;
            case InAppAction.PauseResume when _model.IsRecording:
                _ = _model.TogglePauseAsync();
                return true;
            case InAppAction.ShowRecording when _model.IsRecording:
                _model.ShowRecording();
                return true;
            case InAppAction.ShowList:
                _model.ShowLibrary();
                return true;
            case InAppAction.OpenChat when route == RouteKind.Conversation:
                _model.DetailTab = DetailTab.Chat;
                return true;
            case InAppAction.Search:
                Toolbar.FocusSearch();
                return true;
            case InAppAction.Settings:
                _model.OpenSettings();
                return true;
            // Esc first ends a selection of cards (macOS listSelectionActive), wherever the focus is: a Ctrl-click does
            // not move the focus into the list.
            case InAppAction.Back when plain && ListHost.Visibility == Visibility.Visible && _library.HasSelection:
                _library.ClearSelection();
                return true;
            case InAppAction.Back when route != RouteKind.List:
                _model.GoBack();
                return true;
            case InAppAction.PlayPause when route == RouteKind.Conversation && _model.DetailTab == DetailTab.Transcript
                && RouteHost.Content is ConversationView conversation && !TakesSpace(focused):
                conversation.TogglePlayback();
                return true;
            default:
                return false;
        }
    }

    /// <summary>A dialog, menu or flyout is open on the window (tooltips are popups too, and do not count).</summary>
    private bool PopupOpen() =>
        RootGrid.XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(p => p.Child is not ToolTip);

    /// <summary>
    /// A control reached with the keyboard that Space clicks, switches or opens (a button, a switch, a tab, a library card)
    /// keeps Space. One that was clicked does not: Space plays, as on macOS, whose buttons take no focus.
    /// </summary>
    private bool TakesSpace(object? focused)
    {
        if (focused is not Control { FocusState: FocusState.Keyboard } control) return false;
        if (control is ButtonBase or ToggleSwitch or ComboBox or SelectorBarItem) return true;
        for (DependencyObject? e = control; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e == _library) return true;
        }
        return false;
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

        // The scale and the caption insets can be briefly invalid while the window switches layout (0 or NaN scale,
        // negative insets): that pass is skipped or clamped, the next LayoutUpdated has the settled values.
        var scale = root.RasterizationScale;
        var titleBar = AppWindow.TitleBar;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            StartupTrace.Write($"title bar regions skipped: scale {scale}, insets {titleBar.LeftInset},{titleBar.RightInset}");
            return;
        }
        if (titleBar.LeftInset < 0 || titleBar.RightInset < 0)
        {
            StartupTrace.Write($"title bar insets {titleBar.LeftInset},{titleBar.RightInset} clamped (scale {scale})");
        }
        SetWidth(LeftInsetColumn, titleBar.LeftInset / scale);
        SetWidth(RightInsetColumn, titleBar.RightInset / scale);

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

    /// <summary>GridLength throws on a negative, NaN or infinite width: those set 0, so LayoutUpdated never throws.</summary>
    private static void SetWidth(ColumnDefinition column, double width)
    {
        var length = new GridLength(double.IsFinite(width) && width > 0 ? width : 0);
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
