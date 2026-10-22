using System.Globalization;
using System.Runtime.InteropServices;
using EchoDiar.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Storage;

namespace EchoDiar.App.Services;

/// <summary>
/// The main window's two sizes (macOS MainWindowFrames.swift): compact for the list, wide for a conversation and the
/// recording. Each remembers its own frame.
/// </summary>
public enum MainWindowLayout
{
    Compact,
    Wide,
}

/// <summary>
/// Places the main window at its layout's saved frame and resizes it between layouts, keeping its top-left corner.
/// Sizes are DIPs, turned into physical pixels with the window's (or the target display's) DPI. A frame is saved
/// (LocalSettings <c>MainWindowFrame.compact</c> / <c>.wide</c>, "x,y,w,h": the outer frame's top-left in physical
/// screen pixels, its size in DIPs) on each layout switch and on close, never while maximized or minimized, nor outside
/// the layout's bounds. A switch while maximized or minimized waits for the window to be restored.
/// </summary>
internal sealed partial class WindowFrames
{
    /// <summary>A layout's bounds in DIPs (no maximum on wide) and its LocalSettings key.</summary>
    private sealed record Bounds(
        double MinWidth, double MinHeight, double? MaxWidth, double? MaxHeight,
        double DefaultWidth, double DefaultHeight, string Key)
    {
        /// <summary>A frame whose size is not is never saved or restored.</summary>
        public bool Fits(double width, double height) =>
            width >= MinWidth && height >= MinHeight
            && (MaxWidth is not { } maxWidth || width <= maxWidth)
            && (MaxHeight is not { } maxHeight || height <= maxHeight);
    }

    private static readonly Bounds CompactBounds = new(440, 480, 640, 1200, 480, 640, "MainWindowFrame.compact");
    private static readonly Bounds WideBounds = new(860, 560, null, null, 1080, 720, "MainWindowFrame.wide");

    private readonly AppWindow _window;
    private readonly OverlappedPresenter _presenter;
    private MainWindowLayout _layout;

    /// <summary>The layout to apply once the window is restored.</summary>
    private MainWindowLayout? _pending;

    /// <summary>What the presenter's physical bounds were computed for.</summary>
    private (MainWindowLayout Layout, double Scale)? _applied;

    public WindowFrames(AppWindow window)
    {
        _window = window;
        _presenter = (OverlappedPresenter)window.Presenter;
        window.Changed += OnChanged;
    }

    /// <summary>The layout of a route: compact for the list, wide for the others.</summary>
    public static MainWindowLayout For(Route route) =>
        route.Kind == RouteKind.List ? MainWindowLayout.Compact : MainWindowLayout.Wide;

    /// <summary>The window's first frame: the layout's saved frame, else its default size centered on the display.</summary>
    public void Place(MainWindowLayout layout)
    {
        _layout = layout;
        _pending = null;
        ApplyBounds();
        _window.MoveAndResize(SavedFrame(layout) ?? CenteredFrame(layout));
        // It may have moved to a display of another DPI.
        ApplyBounds();
    }

    /// <summary>Saves the old layout's frame, then resizes to <paramref name="layout"/>'s saved size (or default).</summary>
    public void SwitchTo(MainWindowLayout layout)
    {
        if (_presenter.State != OverlappedPresenterState.Restored)
        {
            _pending = layout == _layout ? null : layout;
            return;
        }
        _pending = null;
        if (layout == _layout) return;
        Save();
        _layout = layout;
        // The new bounds first, so the presenter does not clamp the new size against the old ones.
        ApplyBounds();
        var bounds = Of(layout);
        var scale = WindowScale();
        var (width, height) = SavedSize(bounds) ?? (bounds.DefaultWidth, bounds.DefaultHeight);
        var position = _window.Position;
        var work = DisplayArea.GetFromWindowId(_window.Id, DisplayAreaFallback.Nearest).WorkArea;
        _window.MoveAndResize(Clamp(new RectInt32(position.X, position.Y, Px(width, scale), Px(height, scale)), work));
    }

    /// <summary>On a layout switch and on close; not while maximized or minimized, nor outside the bounds.</summary>
    public void Save()
    {
        if (_presenter.State != OverlappedPresenterState.Restored) return;
        var scale = WindowScale();
        var width = Math.Round(_window.Size.Width / scale);
        var height = Math.Round(_window.Size.Height / scale);
        var bounds = Of(_layout);
        if (!bounds.Fits(width, height)) return;
        var position = _window.Position;
        ApplicationData.Current.LocalSettings.Values[bounds.Key] =
            string.Create(CultureInfo.InvariantCulture, $"{position.X},{position.Y},{width},{height}");
    }

    /// <summary>
    /// Sets the presenter's minimum and maximum (cleared on wide) in physical pixels at the window's DPI; again after a
    /// move to a display of another DPI.
    /// </summary>
    public void ApplyBounds()
    {
        var scale = WindowScale();
        if (_applied == (_layout, scale)) return;
        _applied = (_layout, scale);
        var bounds = Of(_layout);
        _presenter.PreferredMinimumWidth = Px(bounds.MinWidth, scale);
        _presenter.PreferredMinimumHeight = Px(bounds.MinHeight, scale);
        _presenter.PreferredMaximumWidth = bounds.MaxWidth is { } maxWidth ? Px(maxWidth, scale) : null;
        _presenter.PreferredMaximumHeight = bounds.MaxHeight is { } maxHeight ? Px(maxHeight, scale) : null;
    }

    /// <summary>A switch made while maximized or minimized applies once the window is restored.</summary>
    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_pending is { } layout && _presenter.State == OverlappedPresenterState.Restored
            && (args.DidSizeChange || args.DidPresenterChange || args.DidPositionChange))
        {
            SwitchTo(layout);
        }
    }

    private static Bounds Of(MainWindowLayout layout) => layout == MainWindowLayout.Compact ? CompactBounds : WideBounds;

    /// <summary>The saved frame in physical pixels, when it fits the bounds and meets a display; clamped to its work area.</summary>
    private RectInt32? SavedFrame(MainWindowLayout layout)
    {
        var bounds = Of(layout);
        if (Read(bounds) is not { } saved) return null;
        var guess = WindowScale();
        var rect = new RectInt32(saved.X, saved.Y, Px(saved.Width, guess), Px(saved.Height, guess));
        if (DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is not { } display) return null;
        var scale = DisplayScale(display);
        return Clamp(new RectInt32(saved.X, saved.Y, Px(saved.Width, scale), Px(saved.Height, scale)), display.WorkArea);
    }

    /// <summary>The default size, centered on the window's display.</summary>
    private RectInt32 CenteredFrame(MainWindowLayout layout)
    {
        var bounds = Of(layout);
        var display = DisplayArea.GetFromWindowId(_window.Id, DisplayAreaFallback.Primary);
        var scale = DisplayScale(display);
        var work = display.WorkArea;
        var width = Math.Min(Px(bounds.DefaultWidth, scale), work.Width);
        var height = Math.Min(Px(bounds.DefaultHeight, scale), work.Height);
        return new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height);
    }

    private static (double Width, double Height)? SavedSize(Bounds bounds) =>
        Read(bounds) is { } saved ? (saved.Width, saved.Height) : null;

    /// <summary>"x,y,w,h"; null when missing, unreadable or outside the bounds (saved under the other layout).</summary>
    private static (int X, int Y, double Width, double Height)? Read(Bounds bounds)
    {
        if (ApplicationData.Current.LocalSettings.Values[bounds.Key] is not string text) return null;
        var parts = text.Split(',');
        var culture = CultureInfo.InvariantCulture;
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.Integer, culture, out var x)
            || !int.TryParse(parts[1], NumberStyles.Integer, culture, out var y)
            || !double.TryParse(parts[2], NumberStyles.Float, culture, out var width)
            || !double.TryParse(parts[3], NumberStyles.Float, culture, out var height)
            || !bounds.Fits(width, height))
        {
            return null;
        }
        return (x, y, width, height);
    }

    /// <summary>Inside the work area: the size first, then the origin.</summary>
    private static RectInt32 Clamp(RectInt32 frame, RectInt32 work)
    {
        if (work.Width <= 0 || work.Height <= 0) return frame;
        var width = Math.Min(frame.Width, work.Width);
        var height = Math.Min(frame.Height, work.Height);
        var x = Math.Clamp(frame.X, work.X, work.X + work.Width - width);
        var y = Math.Clamp(frame.Y, work.Y, work.Y + work.Height - height);
        return new RectInt32(x, y, width, height);
    }

    private static int Px(double dips, double scale) => (int)Math.Round(dips * scale);

    private double WindowScale()
    {
        var dpi = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(_window.Id));
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    private double DisplayScale(DisplayArea display)
    {
        var monitor = Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        return GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpi, out _) >= 0 && dpi > 0 ? dpi / 96.0 : WindowScale();
    }

    private const int MdtEffectiveDpi = 0;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
