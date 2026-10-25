using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Storage;

namespace EchoDiar.App.Services;

/// <summary>
/// Places the main window at its saved frame (macOS MainWindowFrames.swift): one size for every route, the library's
/// sidebar beside the detail, so a route switch never resizes the window. Sizes are DIPs, turned into physical pixels
/// with the window's (or the target display's) DPI. The frame is saved (LocalSettings <c>MainWindowFrame.wide</c>, the
/// key of the wide layout from before the window had one size, so the user's frame stays; "x,y,w,h": the outer frame's
/// top-left in physical screen pixels, its size in DIPs) on close and when the window hides to the tray, never while
/// maximized or minimized, nor below the minimum size.
/// </summary>
internal sealed partial class WindowFrames
{
    private const double MinWidth = 860;
    private const double MinHeight = 560;
    private const double DefaultWidth = 1080;
    private const double DefaultHeight = 720;
    private const string Key = "MainWindowFrame.wide";

    private readonly AppWindow _window;
    private readonly OverlappedPresenter _presenter;

    /// <summary>The scale the presenter's physical bounds were computed for.</summary>
    private double? _applied;

    public WindowFrames(AppWindow window)
    {
        _window = window;
        _presenter = (OverlappedPresenter)window.Presenter;
    }

    /// <summary>The window's first frame: the saved frame, else the default size centered on the display.</summary>
    public void Place()
    {
        ApplyBounds();
        _window.MoveAndResize(SavedFrame() ?? CenteredFrame());
        // It may have moved to a display of another DPI.
        ApplyBounds();
    }

    /// <summary>On close and on hiding to the tray; not while maximized or minimized, nor below the minimum size.</summary>
    public void Save()
    {
        if (_presenter.State != OverlappedPresenterState.Restored) return;
        var scale = WindowScale();
        var width = Math.Round(_window.Size.Width / scale);
        var height = Math.Round(_window.Size.Height / scale);
        if (!Fits(width, height)) return;
        var position = _window.Position;
        ApplicationData.Current.LocalSettings.Values[Key] =
            string.Create(CultureInfo.InvariantCulture, $"{position.X},{position.Y},{width},{height}");
    }

    /// <summary>
    /// Sets the presenter's minimum in physical pixels at the window's DPI (no maximum); again after a move to a display
    /// of another DPI.
    /// </summary>
    public void ApplyBounds()
    {
        var scale = WindowScale();
        if (_applied == scale) return;
        _applied = scale;
        _presenter.PreferredMinimumWidth = Px(MinWidth, scale);
        _presenter.PreferredMinimumHeight = Px(MinHeight, scale);
        _presenter.PreferredMaximumWidth = null;
        _presenter.PreferredMaximumHeight = null;
    }

    /// <summary>A frame below the minimum size is never saved or restored.</summary>
    private static bool Fits(double width, double height) => width >= MinWidth && height >= MinHeight;

    /// <summary>The saved frame in physical pixels, when it fits and meets a display; clamped to its work area.</summary>
    private RectInt32? SavedFrame()
    {
        if (Read() is not { } saved) return null;
        var guess = WindowScale();
        var rect = new RectInt32(saved.X, saved.Y, Px(saved.Width, guess), Px(saved.Height, guess));
        if (DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is not { } display) return null;
        var scale = DisplayScale(display);
        return Clamp(new RectInt32(saved.X, saved.Y, Px(saved.Width, scale), Px(saved.Height, scale)), display.WorkArea);
    }

    /// <summary>The default size, centered on the window's display.</summary>
    private RectInt32 CenteredFrame()
    {
        var display = DisplayArea.GetFromWindowId(_window.Id, DisplayAreaFallback.Primary);
        var scale = DisplayScale(display);
        var work = display.WorkArea;
        var width = Math.Min(Px(DefaultWidth, scale), work.Width);
        var height = Math.Min(Px(DefaultHeight, scale), work.Height);
        return new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height);
    }

    /// <summary>"x,y,w,h"; null when missing, unreadable or below the minimum size.</summary>
    private static (int X, int Y, double Width, double Height)? Read()
    {
        if (ApplicationData.Current.LocalSettings.Values[Key] is not string text) return null;
        var parts = text.Split(',');
        var culture = CultureInfo.InvariantCulture;
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.Integer, culture, out var x)
            || !int.TryParse(parts[1], NumberStyles.Integer, culture, out var y)
            || !double.TryParse(parts[2], NumberStyles.Float, culture, out var width)
            || !double.TryParse(parts[3], NumberStyles.Float, culture, out var height)
            || !Fits(width, height))
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
