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

        TitleBarGrid.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TitleBarGrid.LayoutUpdated += (_, _) => UpdateTitleBarRegions();

        Toolbar.Attach(_model);
        // One library for both layouts: the whole page on compact, the sidebar on wide.
        _library = new ConversationListView(_model);
        ListHost.Content = _library;

        _frames = new WindowFrames(AppWindow);
        _frames.Place(WindowFrames.For(_model.Route));
        // A move to a display of another DPI changes the presenter's bounds in physical pixels.
        RootGrid.Loaded += (_, _) =>
        {
            if (RootGrid.XamlRoot is { } root) root.Changed += (_, _) => _frames.ApplyBounds();
        };
        Closed += (_, _) => _frames.Save();
        Activated += OnFirstActivated;

        _model.PropertyChanged += OnModelChanged;
        ShowRoute();
    }

    /// <summary>
    /// The first frame's content can be lost while the window is first shown (seen in a VM: the title bar and the
    /// library header stayed blank, while what changed later drew). WinUI then redraws only the elements that change,
    /// and has no API to redraw everything; a resize does. So once shown and after one drawn frame, the window is
    /// resized by 1 px and back after the next frame.
    /// </summary>
    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        Activated -= OnFirstActivated;
        AfterNextFrame(NudgeSize);
    }

    private void NudgeSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored } presenter) return;
        var size = AppWindow.Size;
        // Inside the presenter's bounds, or it clamps the nudge away.
        var width = size.Width - 1 >= (presenter.PreferredMinimumWidth ?? 0) ? size.Width - 1 : size.Width + 1;
        if (presenter.PreferredMaximumWidth is { } maxWidth && width > maxWidth) return;
        var nudged = new SizeInt32(width, size.Height);
        AppWindow.Resize(nudged);
        AfterNextFrame(() =>
        {
            // Unless the user resized it meanwhile.
            if (AppWindow.Size.Equals(nudged)) AppWindow.Resize(size);
        });
    }

    /// <summary>Runs <paramref name="action"/> once a frame has been drawn.</summary>
    private static void AfterNextFrame(Action action)
    {
        var frames = 0;
        void OnRendering(object? sender, object e)
        {
            // Rendering comes before its frame is drawn: the second one follows a drawn frame.
            if (++frames < 2) return;
            CompositionTarget.Rendering -= OnRendering;
            action();
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

    // Keeps the toolbar's controls clickable: everything else in the title bar row drags the window.
    private void UpdateTitleBarRegions()
    {
        if (Content?.XamlRoot is not { } root)
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
