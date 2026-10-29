using EchoDiar.Core.Settings;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace EchoDiar.Platform.Windowing;

/// <summary>A monitor, as the pill places itself on it.</summary>
/// <param name="Device">The monitor's GDI device name (<c>\\.\DISPLAY1</c>).</param>
/// <param name="WorkArea">The monitor without the taskbar (NSScreen.visibleFrame), in physical pixels.</param>
/// <param name="Scale">The monitor's effective DPI / 96.</param>
public sealed record PillMonitor(string Device, RectInt32 WorkArea, double Scale)
{
    /// <summary>
    /// The <c>pillPositions</c> key: the monitor's device interface name (<c>EnumDisplayDevices</c> with
    /// <c>EDD_GET_DEVICE_INTERFACE_NAME</c>, <c>\\?\DISPLAY#…#{e6f07b5f-…}</c>), which survives reboots and arrangement
    /// changes like the macOS display UUID; <see cref="Device"/> when Windows reports none. Read on each use, only
    /// where a position is saved or looked up: following a window places the pill without it.
    /// </summary>
    public string Key => PillWindowStyle.MonitorKey(Device);
}

/// <summary>
/// The floating pill's window: a topmost tool window that never takes focus, clipped to a capsule, optionally kept out
/// of screen captures. Handles are a WinUI window's HWND (<c>WindowNative.GetWindowHandle</c>). Rects are physical
/// screen pixels.
/// <para>
/// Order for the App (the presenter lives in Microsoft.UI.Windowing, which Platform does not reference):
/// <c>OverlappedPresenter.SetBorderAndTitleBar(false, false)</c> and <c>IsResizable = false</c>, then <see cref="Apply"/>,
/// then <c>AppWindow.Show(activateWindow: false)</c>; never <c>Window.Activate()</c>, which takes focus.
/// <see cref="Apply"/> must run before the first show: Windows reads WS_EX_TOOLWINDOW (no taskbar button, not in
/// Alt+Tab) when the window is shown. Call <see cref="UpdateShape"/> after every size or DPI change
/// (<c>AppWindow.Changed</c> with <c>DidSizeChange</c>).
/// </para>
/// </summary>
public static class PillWindowStyle
{
    /// <summary>Gap between the pill and the work area's or the meeting window's corner, in DIPs (macOS <c>inset</c>).</summary>
    public const double Inset = 8;

    /// <summary>
    /// No frame at all, WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE and topmost (through <c>SetWindowPos(HWND_TOPMOST)</c>:
    /// setting WS_EX_TOPMOST alone does not take), with no DWM corner rounding and no DWM border, so only the region
    /// (<see cref="UpdateShape"/>) shapes the pill. False when Windows refused.
    /// </summary>
    public static unsafe bool Apply(nint hwnd)
    {
        var window = (HWND)hwnd;
        // SetBorderAndTitleBar(false, false) with IsResizable = false keeps WS_DLGFRAME: a 3 px frame on every side
        // that moves the client area (and the capsule) 3 px down and right of the window rect the region clips to.
        var frame = (WINDOW_STYLE)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_STYLE)
            & ~(WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_THICKFRAME | WINDOW_STYLE.WS_DLGFRAME | WINDOW_STYLE.WS_BORDER);
        PInvoke.SetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (int)frame);
        var style = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        style = (style | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOPMOST)
            & ~WINDOW_EX_STYLE.WS_EX_APPWINDOW;
        PInvoke.SetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)style);
        var placed = PInvoke.SetWindowPos(window, HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
        // A region does not stop DWM: measured in a Windows 11 VM, it still drew a 1 px border with its own ~8 px
        // rounding outside the pill's ~17 px region. No DWM rounding and no border color (DWMWA_COLOR_NONE, Windows 11
        // 22000+) leave the shape to the region.
        var corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        var rounded = PInvoke.DwmSetWindowAttribute(
            window, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        uint border = 0xFFFFFFFE; // DWMWA_COLOR_NONE (a COLORREF)
        var bordered = PInvoke.DwmSetWindowAttribute(
            window, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &border, sizeof(uint));
        return placed && rounded.Succeeded && bordered.Succeeded;
    }

    /// <summary>False once the window is destroyed (closed by the system or the user).</summary>
    public static bool IsAlive(nint hwnd) => hwnd != 0 && PInvoke.IsWindow((HWND)hwnd);

    /// <summary>The foreground window; 0 when none.</summary>
    public static nint ForegroundWindow() => PInvoke.GetForegroundWindow();

    /// <summary>
    /// <paramref name="window"/> is the pill or one of the windows it owns (its flyouts' popups); a press on the pill
    /// can still make it the foreground window, as WS_EX_NOACTIVATE does not hold for every activation.
    /// </summary>
    public static bool IsPillOrOwned(nint window, nint pill) =>
        window != 0 && (window == pill || PInvoke.GetAncestor((HWND)window, GET_ANCESTOR_FLAGS.GA_ROOTOWNER) == (HWND)pill);

    /// <summary>
    /// Gives the foreground back to <paramref name="window"/> when it still exists; allowed while this process has
    /// the foreground (the pill took it). False when the window is gone or Windows refused.
    /// </summary>
    public static bool SetForeground(nint window) => IsAlive(window) && PInvoke.SetForegroundWindow((HWND)window);

    /// <summary>
    /// Clips the window to a capsule at its current size: corner radius = half the shorter side. The region is in
    /// window coordinates, so it lines up only once the border and title bar are gone. False when Windows refused.
    /// </summary>
    public static bool UpdateShape(nint hwnd)
    {
        var window = (HWND)hwnd;
        if (!PInvoke.GetWindowRect(window, out var rect)) return false;
        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;
        if (width <= 0 || height <= 0) return false;
        var diameter = Math.Min(width, height);
        // The right and bottom edges are exclusive, hence the + 1.
        var region = PInvoke.CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region.IsNull) return false;
        // On success the system owns the region and frees it with the window or the next region.
        if (PInvoke.SetWindowRgn(window, region, true) != 0) return true;
        PInvoke.DeleteObject(region);
        return false;
    }

    /// <summary>
    /// Shows the window in screenshots and screen recordings, or leaves it out (WDA_EXCLUDEFROMCAPTURE), per
    /// <c>showPillInScreenCaptures</c> (macOS <c>sharingType</c>). False when Windows refused.
    /// </summary>
    public static bool SetShownInCaptures(nint hwnd, bool shown) =>
        PInvoke.SetWindowDisplayAffinity(
            (HWND)hwnd, shown ? WINDOW_DISPLAY_AFFINITY.WDA_NONE : WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);

    /// <summary>The monitor that contains most of <paramref name="rect"/>, else the nearest one; null when none can be read.</summary>
    public static unsafe PillMonitor? MonitorFor(RectInt32 rect)
    {
        var bounds = new RECT { left = rect.X, top = rect.Y, right = rect.X + rect.Width, bottom = rect.Y + rect.Height };
        var monitor = PInvoke.MonitorFromRect(in bounds, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor.IsNull) return null;
        var info = new MONITORINFOEXW { monitorInfo = { cbSize = (uint)sizeof(MONITORINFOEXW) } };
        if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info)) return null;
        var work = info.monitorInfo.rcWork;
        var scale = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded && dpi > 0
            ? dpi / 96.0
            : 1;
        return new PillMonitor(
            info.szDevice.ToString(),
            new RectInt32(work.left, work.top, work.right - work.left, work.bottom - work.top),
            scale);
    }

    /// <summary>
    /// The pill's frame whose top-right corner sits at <paramref name="offset"/> from <paramref name="anchor"/>'s
    /// top-right corner, clamped into <paramref name="workArea"/> (top and right edges win), as macOS <c>place()</c>.
    /// The anchor is the work area for <c>pillPositions</c> and the meeting window for <c>pillWindowOffset</c>.
    /// </summary>
    /// <param name="offset">
    /// In DIPs with the macOS signs: x grows to the right, y grows upward. Null = <see cref="Inset"/> inside the
    /// corner, (-8, -8).
    /// </param>
    /// <param name="width">The pill's width in physical pixels.</param>
    /// <param name="height">The pill's height in physical pixels.</param>
    /// <param name="scale">The anchor monitor's <see cref="PillMonitor.Scale"/>.</param>
    public static RectInt32 FrameAt(RectInt32 anchor, PillPosition? offset, int width, int height, double scale, RectInt32 workArea)
    {
        var right = anchor.X + anchor.Width + (int)Math.Round((offset?.X ?? -Inset) * scale);
        var top = anchor.Y - (int)Math.Round((offset?.Y ?? -Inset) * scale);
        var x = Math.Min(Math.Max(right - width, workArea.X), workArea.X + workArea.Width - width);
        var y = Math.Max(Math.Min(top, workArea.Y + workArea.Height - height), workArea.Y);
        return new RectInt32(x, y, width, height);
    }

    /// <summary>
    /// What to store after a drag: <paramref name="frame"/>'s top-right corner from <paramref name="anchor"/>'s, in DIPs
    /// with the macOS signs (the inverse of <see cref="FrameAt"/>; macOS <c>panelMoved()</c> and <c>savePosition()</c>).
    /// </summary>
    public static PillPosition OffsetOf(RectInt32 frame, RectInt32 anchor, double scale) => new()
    {
        X = (frame.X + frame.Width - (anchor.X + anchor.Width)) / scale,
        Y = (anchor.Y - frame.Y) / scale,
    };

    /// <summary>
    /// The device interface name of the monitor on <paramref name="device"/> (an adapter output, <c>\\.\DISPLAY1</c>),
    /// preferring an active one (a cloned output lists several); <paramref name="device"/> itself when none has one.
    /// </summary>
    internal static unsafe string MonitorKey(string device)
    {
        var display = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        string? first = null;
        for (uint i = 0; PInvoke.EnumDisplayDevices(device, i, ref display, PInvoke.EDD_GET_DEVICE_INTERFACE_NAME); i++)
        {
            var id = display.DeviceID.ToString();
            if (id.Length == 0) continue;
            if (display.StateFlags.HasFlag(DISPLAY_DEVICE_STATE_FLAGS.DISPLAY_DEVICE_ACTIVE)) return id;
            first ??= id;
        }
        return first ?? device;
    }
}
