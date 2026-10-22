using System.ComponentModel;
using EchoDiar.App.Themes;
using EchoDiar.App.ViewModels;
using EchoDiar.App.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace EchoDiar.App;

public sealed partial class MainWindow : Window
{
    private readonly AppViewModel _model;
    private RectInt32 _passthrough;

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

        BackButton.Click += (_, _) => _model.GoBack();
        _model.PropertyChanged += OnModelChanged;
        ShowRoute();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.Route)) ShowRoute();
    }

    /// <summary>Shows the route's page; Back returns to the list from the other pages.</summary>
    private void ShowRoute()
    {
        var route = _model.Route;
        BackButton.IsEnabled = route.Kind != RouteKind.List;
        RouteHost.Content = route switch
        {
            { Kind: RouteKind.Recording } => new RecordingView(_model),
            { Kind: RouteKind.Conversation, ConversationId: { } id } => new ConversationView(_model, id),
            _ => new ConversationListView(_model),
        };
    }

    // Keeps the capsule clickable: everything else in the title bar row drags the window.
    private void UpdateTitleBarRegions()
    {
        if (Content?.XamlRoot is not { } root)
        {
            return;
        }

        var scale = root.RasterizationScale;
        SetWidth(LeftInsetColumn, AppWindow.TitleBar.LeftInset / scale);
        SetWidth(RightInsetColumn, AppWindow.TitleBar.RightInset / scale);

        var bounds = ToolbarCapsule.TransformToVisual(null)
            .TransformBounds(new Rect(0, 0, ToolbarCapsule.ActualWidth, ToolbarCapsule.ActualHeight));
        var rect = new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));
        if (rect.Equals(_passthrough))
        {
            return;
        }

        _passthrough = rect;
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, [rect]);
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
