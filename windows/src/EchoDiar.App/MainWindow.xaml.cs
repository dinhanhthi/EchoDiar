using System.ComponentModel;
using EchoDiar.App.Services;
using EchoDiar.App.Themes;
using EchoDiar.App.ViewModels;
using EchoDiar.App.Views;
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

    public MainWindow(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        if (TintedBackdrop.IsSupported)
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
        // One library for both layouts: the whole page on compact, the sidebar on wide.
        _library = new ConversationListView(_model);
        ListHost.Content = _library;

        _frames = new WindowFrames(AppWindow);
        _frames.Place(WindowFrames.For(_model.Route));
        // A move to a display of another DPI changes the presenter's bounds in physical pixels.
        RootGrid.Loaded += OnRootLoaded;
        Closed += OnClosed;
        Activated += OnFirstActivated;

        _model.PropertyChanged += OnModelChanged;
        ShowRoute();
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_xamlRoot is not null || RootGrid.XamlRoot is not { } root) return;
        _xamlRoot = root;
        root.Changed += OnXamlRootChanged;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!_closing) _frames.ApplyBounds();
    }

    /// <summary>Saves the frame while the window still answers, then lets go of everything that would call it.</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closing = true;
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
    /// element makes its glyphs again only when it leaves the tree and enters it again, so once shown and after one
    /// drawn frame, the toolbar's and the library's content are taken out and put back.
    /// </summary>
    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        Activated -= OnFirstActivated;
        AfterNextFrame(() =>
        {
            Reattach(Toolbar);
            Reattach(_library);
        });
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
        if (e.PropertyName == nameof(AppViewModel.Route)) ShowRoute();
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
}
