using EchoDiar.Core.Settings;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
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

    /// <summary>Rooted for the process's life: a collected delegate behind a live hook would crash.</summary>
    private static readonly HOOKPROC CbtCallback = OnCbt;

    /// <summary>The pill windows whose activation is refused. UI thread only.</summary>
    private static readonly HashSet<HWND> Guarded = [];

    /// <summary>The UI thread's CBT hook while <see cref="Guarded"/> has a window.</summary>
    private static HHOOK _cbtHook;

    /// <summary>
    /// Something on the UI thread tried to activate a pill and was refused; raised on the stack that asked for it:
    /// WS_EX_NOACTIVATE only stops activation by a click, not by <c>SetFocus</c> or <c>SetWindowPos</c>.
    /// </summary>
    public static event Action? ActivationRefused;

    /// <summary>
    /// WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE and topmost (through <c>SetWindowPos(HWND_TOPMOST)</c>: setting
    /// WS_EX_TOPMOST alone does not take), plus the DWM round corner preference; from then on the UI thread refuses to
    /// activate the window, so it never takes the foreground (or Alt+F4) from the meeting app or the main window.
    /// Call it on the window's thread; <see cref="Release"/> when the window closes. False when Windows refused.
    /// </summary>
    public static unsafe bool Apply(nint hwnd)
    {
        var window = (HWND)hwnd;
        var guarded = Guard(window);
        var style = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        style = (style | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOPMOST)
            & ~WINDOW_EX_STYLE.WS_EX_APPWINDOW;
        PInvoke.SetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)style);
        var placed = PInvoke.SetWindowPos(window, HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
        // Rounds what the region below leaves to DWM; a window with a region gets no DWM corners, border or shadow.
        var corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        var rounded = PInvoke.DwmSetWindowAttribute(
            window, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        return guarded && placed && rounded.Succeeded;
    }

    /// <summary>The window closed or is being dropped: its activation is no longer refused (handles are reused).</summary>
    public static void Release(nint hwnd)
    {
        if (!Guarded.Remove((HWND)hwnd) || Guarded.Count > 0 || _cbtHook.IsNull) return;
        PInvoke.UnhookWindowsHookEx(_cbtHook);
        _cbtHook = default;
    }

    /// <summary>
    /// Moves the window's top-left corner to <paramref name="origin"/> (physical screen pixels) without activating it
    /// or changing its size or z-order. <c>AppWindow.Move</c> may ask for activation, which the CBT hook refuses.
    /// False when Windows refused.
    /// </summary>
    public static bool MoveTo(nint hwnd, PointInt32 origin) =>
        PInvoke.SetWindowPos((HWND)hwnd, HWND.Null, origin.X, origin.Y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    /// <summary>The pointer's position in physical screen pixels; null when Windows cannot tell.</summary>
    public static PointInt32? CursorPosition() =>
        PInvoke.GetCursorPos(out var point) ? new PointInt32(point.X, point.Y) : null;

    /// <summary>
    /// The primary (logical left) mouse button is down now, whichever window the pointer is over: GetAsyncKeyState
    /// reads the physical buttons, so swapped buttons read the right one.
    /// </summary>
    public static bool IsPrimaryButtonDown()
    {
        var key = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0
            ? VIRTUAL_KEY.VK_RBUTTON
            : VIRTUAL_KEY.VK_LBUTTON;
        return (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;
    }

    /// <summary>False once the window is destroyed (closed by the system or the user).</summary>
    public static bool IsAlive(nint hwnd) => hwnd != 0 && PInvoke.IsWindow((HWND)hwnd);

    /// <summary>
    /// Refuses the window's activation through a CBT hook on the calling thread, which sees every activation of its
    /// windows before it happens, whatever asks for it (XAML focusing its content on a press, a move without
    /// SWP_NOACTIVATE). False when the hook could not be set.
    /// </summary>
    private static bool Guard(HWND window)
    {
        if (_cbtHook.IsNull)
        {
            _cbtHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_CBT, CbtCallback, HINSTANCE.Null, PInvoke.GetCurrentThreadId());
            if (_cbtHook.IsNull) return false;
        }
        Guarded.Add(window);
        return true;
    }

    /// <summary>A nonzero result for HCBT_ACTIVATE prevents the activation. Never throws (a native callback).</summary>
    private static LRESULT OnCbt(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code == PInvoke.HCBT_ACTIVATE && Guarded.Contains((HWND)(nint)(nuint)wParam))
        {
            try
            {
                ActivationRefused?.Invoke();
            }
            catch
            {
                // A logging failure must not reach native code; the activation is refused anyway.
            }
            return (LRESULT)1;
        }
        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

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
