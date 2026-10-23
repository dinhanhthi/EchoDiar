using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using Color = Windows.UI.Color;

// Not EchoDiar.Platform.System: that namespace would hide System for every System.-qualified name under EchoDiar.Platform.
namespace EchoDiar.Platform.SystemServices;

/// <summary>An item of <see cref="NotifyIcon"/>'s menu; <see cref="Text"/> null is a separator.</summary>
/// <param name="Text">The title; an "&amp;" shows as itself (no access key).</param>
/// <param name="Click">Runs after the menu closed, inside the icon's window procedure (see <see cref="NotifyIcon"/>).</param>
/// <param name="Submenu">Items of a submenu; <paramref name="Click"/> is then unused.</param>
public sealed record NotifyIconMenuItem(
    string? Text, Action? Click = null, bool Enabled = true, bool Checked = false, IReadOnlyList<NotifyIconMenuItem>? Submenu = null)
{
    public static NotifyIconMenuItem Separator { get; } = new((string?)null);
}

/// <summary>What <see cref="NotifyIcon"/> shows.</summary>
public enum NotifyIconGlyph
{
    /// <summary>The icon file given to the constructor.</summary>
    Logo,

    /// <summary>A filled disc with a white dot (macOS record.circle.fill).</summary>
    Record,

    /// <summary>A filled disc with two white bars (macOS pause.circle.fill).</summary>
    Pause,
}

/// <summary>
/// A notification area (tray) icon on a hidden top-level <see cref="MessageWindow"/>: a left click or Enter raises
/// <see cref="Clicked"/>; a right click, Shift+F10 or the menu key opens a menu built by <see cref="MenuItems"/> at that
/// moment. The window also gets the session's end (sign-out, shutdown, restart, or an installer closing the app):
/// <see cref="SessionEnding"/>. The icon comes back when Explorer restarts ("TaskbarCreated"); an icon Explorer refuses
/// (no taskbar yet early in a logon) is logged and added then.
/// <para>
/// Threading: create, use and <see cref="Dispose"/> on the UI thread (it pumps the window's messages). Events, menu
/// builds and clicks run synchronously inside the window procedure: a handler must not dispose the icon (post that
/// work instead), and a handler that throws is logged.
/// </para>
/// </summary>
public sealed class NotifyIcon : IDisposable
{
    /// <summary>The icon's callback message (NOTIFYICON_VERSION_4: the event in LOWORD(lParam), the anchor in wParam).</summary>
    internal const uint CallbackMessage = PInvoke.WM_APP + 1;

    /// <summary>NIN_KEYSELECT: Enter or Space on the focused icon.</summary>
    internal const uint KeySelect = PInvoke.NIN_SELECT | PInvoke.NINF_KEY;

    private const uint IconId = 1;

    /// <summary>szTip holds 128 characters with the terminating null.</summary>
    private const int MaxTooltip = 127;

    private readonly MessageWindow _window;
    private readonly Action<string>? _log;
    private readonly uint _taskbarCreated;
    private HICON _logo;

    /// <summary>The drawn glyph shown now, destroyed when replaced; null while the logo shows.</summary>
    private HICON _drawn;

    private string _tooltip;
    private bool _disposed;

    /// <summary>Creates the window and adds the icon. Throws <see cref="System.ComponentModel.Win32Exception"/> only if the window cannot be created.</summary>
    /// <param name="iconPath">An .ico file; the frame for the small icon size is used.</param>
    public NotifyIcon(string iconPath, string tooltip, Action<string>? log = null)
    {
        _log = log;
        _tooltip = tooltip;
        _window = new MessageWindow(log, topLevel: true);
        _logo = LoadIcon(iconPath);
        _window.AddHandler(CallbackMessage, OnCallback);
        _window.AddHandler(PInvoke.WM_QUERYENDSESSION, (_, _) => (LRESULT)1); // TRUE: never blocks the session's end
        _window.AddHandler(PInvoke.WM_ENDSESSION, OnEndSession);
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreated != 0) _window.AddHandler(_taskbarCreated, OnTaskbarCreated);
        else Log("RegisterWindowMessage");
        Add();
    }

    /// <summary>The window (tests send it messages).</summary>
    internal HWND Handle => _window.Handle;

    /// <summary>The size of the hand-declared NOTIFYICONDATAW (tests check the 64-bit layout).</summary>
    internal static unsafe int DataSize => sizeof(NotifyIconData);

    /// <summary>Explorer took the icon.</summary>
    internal bool IsAdded { get; private set; }

    /// <summary>The icon was clicked, or Enter or Space pressed on it. UI thread, inside the window procedure.</summary>
    public event Action? Clicked;

    /// <summary>
    /// The session ends (WM_ENDSESSION, after every app agreed): the process may be ended as soon as the handler
    /// returns, so it must finish its work before (Windows waits about 5 s). UI thread, inside the window procedure.
    /// </summary>
    public event Action? SessionEnding;

    /// <summary>Builds the menu each time it opens; null or no items: no menu.</summary>
    public Func<IReadOnlyList<NotifyIconMenuItem>>? MenuItems { get; set; }

    /// <summary>The hover text, cut to 127 characters.</summary>
    public void SetTooltip(string tooltip)
    {
        _window.CheckThread();
        if (_disposed || tooltip == _tooltip) return;
        _tooltip = tooltip;
        if (IsAdded && !Notify(NOTIFY_ICON_MESSAGE.NIM_MODIFY)) Log("Shell_NotifyIcon(NIM_MODIFY)");
    }

    /// <summary>Shows <paramref name="glyph"/>; <paramref name="fill"/> is the disc's colour (unused for the logo).</summary>
    public void SetGlyph(NotifyIconGlyph glyph, Color fill = default)
    {
        _window.CheckThread();
        if (_disposed) return;
        var previous = _drawn;
        _drawn = glyph == NotifyIconGlyph.Logo ? default : CreateGlyphIcon(glyph, fill, IconSize());
        if (IsAdded && !Notify(NOTIFY_ICON_MESSAGE.NIM_MODIFY)) Log("Shell_NotifyIcon(NIM_MODIFY)");
        if (!previous.IsNull) PInvoke.DestroyIcon(previous);
    }

    /// <summary>Removes the icon and destroys the window. UI thread only, never from one of its own handlers.</summary>
    public void Dispose()
    {
        _window.CheckThread();
        if (_disposed) return;
        _disposed = true;
        if (IsAdded && !Notify(NOTIFY_ICON_MESSAGE.NIM_DELETE)) Log("Shell_NotifyIcon(NIM_DELETE)");
        IsAdded = false;
        if (!_drawn.IsNull) PInvoke.DestroyIcon(_drawn);
        if (!_logo.IsNull) PInvoke.DestroyIcon(_logo);
        _drawn = default;
        _logo = default;
        _window.Dispose();
    }

    /// <summary>
    /// The pixels of a drawn glyph, <paramref name="size"/> square, row by row from the top, as 0xAARRGGBB with straight
    /// alpha (a 32 bpp DIB's B, G, R, A bytes): a disc filling the square less 1/16 on each side, its edge antialiased.
    /// </summary>
    internal static uint[] GlyphPixels(NotifyIconGlyph glyph, Color fill, int size)
    {
        var pixels = new uint[size * size];
        var center = size / 2.0;
        var radius = center - size / 16.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                var distance = Math.Sqrt((px - center) * (px - center) + (py - center) * (py - center));
                var disc = Math.Clamp(radius - distance + 0.5, 0, 1);
                if (disc <= 0) continue;
                var white = glyph switch
                {
                    NotifyIconGlyph.Record => Math.Clamp(radius * 0.45 - distance + 0.5, 0, 1),
                    NotifyIconGlyph.Pause => Math.Max(Bar(x, y, center - radius * 0.3, center, radius),
                                                      Bar(x, y, center + radius * 0.3, center, radius)),
                    _ => 0,
                };
                var alpha = (uint)Math.Round(disc * fill.A);
                var r = (uint)Math.Round(fill.R + (255 - fill.R) * white);
                var g = (uint)Math.Round(fill.G + (255 - fill.G) * white);
                var b = (uint)Math.Round(fill.B + (255 - fill.B) * white);
                pixels[y * size + x] = alpha << 24 | r << 16 | g << 8 | b;
            }
        }
        return pixels;
    }

    /// <summary>How much of pixel (x, y) a pause bar centred on (<paramref name="cx"/>, <paramref name="cy"/>) covers.</summary>
    private static double Bar(int x, int y, double cx, double cy, double radius)
    {
        double halfWidth = radius * 0.13, halfHeight = radius * 0.45;
        var across = Math.Clamp(Math.Min(x + 1, cx + halfWidth) - Math.Max(x, cx - halfWidth), 0, 1);
        var down = Math.Clamp(Math.Min(y + 1, cy + halfHeight) - Math.Max(y, cy - halfHeight), 0, 1);
        return across * down;
    }

    /// <summary>The small icon size, which the notification area shows.</summary>
    private static int IconSize() => Math.Max(16, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON));

    private unsafe HICON LoadIcon(string path)
    {
        var size = IconSize();
        HANDLE handle;
        fixed (char* name = path)
        {
            handle = PInvoke.LoadImage(HINSTANCE.Null, name, GDI_IMAGE_TYPE.IMAGE_ICON, size, size, IMAGE_FLAGS.LR_LOADFROMFILE);
        }
        if (handle.IsNull) Log($"LoadImage({Path.GetFileName(path)})");
        return new HICON(handle.Value);
    }

    /// <summary>A 32 bpp icon from <see cref="GlyphPixels"/>; null (the logo shows) if GDI refuses.</summary>
    private unsafe HICON CreateGlyphIcon(NotifyIconGlyph glyph, Color fill, int size)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = size,
            biHeight = -size, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };
        var info = new BITMAPINFO { bmiHeader = header };
        void* bits;
        var color = PInvoke.CreateDIBSection(default, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
        if (color.IsNull)
        {
            Log("CreateDIBSection");
            return default;
        }
        // The mask is unused with per-pixel alpha, but must exist: all zero (monochrome rows are WORD-aligned).
        HBITMAP mask;
        fixed (byte* zero = new byte[(size + 15) / 16 * 2 * size]) mask = PInvoke.CreateBitmap(size, size, 1, 1, zero);
        try
        {
            if (mask.IsNull)
            {
                Log("CreateBitmap");
                return default;
            }
            GlyphPixels(glyph, fill, size).AsSpan().CopyTo(new Span<uint>(bits, size * size));
            var iconInfo = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
            var icon = PInvoke.CreateIconIndirect(&iconInfo);
            if (icon.IsNull) Log("CreateIconIndirect");
            return icon;
        }
        finally
        {
            // CreateIconIndirect copied both bitmaps.
            PInvoke.DeleteObject(color);
            if (!mask.IsNull) PInvoke.DeleteObject(mask);
        }
    }

    private void Add()
    {
        // An icon still there (TaskbarCreated also comes when the taskbar only changed) refuses NIM_ADD: updated instead.
        IsAdded = Notify(NOTIFY_ICON_MESSAGE.NIM_ADD) || Notify(NOTIFY_ICON_MESSAGE.NIM_MODIFY);
        if (!IsAdded)
        {
            Log("Shell_NotifyIcon(NIM_ADD)");
            return;
        }
        if (!Notify(NOTIFY_ICON_MESSAGE.NIM_SETVERSION)) Log("Shell_NotifyIcon(NIM_SETVERSION)");
    }

    private unsafe bool Notify(NOTIFY_ICON_MESSAGE message)
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)sizeof(NotifyIconData),
            hWnd = _window.Handle,
            uID = IconId,
            // NIF_SHOWTIP: version 4 shows the standard tooltip only with it.
            uFlags = (uint)(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP
                | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP),
            uCallbackMessage = CallbackMessage,
            hIcon = _drawn.IsNull ? _logo : _drawn,
            uVersion = message == NOTIFY_ICON_MESSAGE.NIM_SETVERSION ? PInvoke.NOTIFYICON_VERSION_4 : 0,
        };
        var tooltip = _tooltip.Length > MaxTooltip ? _tooltip[..MaxTooltip] : _tooltip;
        tooltip.AsSpan().CopyTo(new Span<char>(data.szTip, MaxTooltip + 1));
        return Shell_NotifyIconW((uint)message, &data) != 0;
    }

    private LRESULT? OnCallback(WPARAM wParam, LPARAM lParam)
    {
        var notification = (uint)(lParam.Value & 0xFFFF);
        if (notification is PInvoke.NIN_SELECT or KeySelect)
        {
            // Now, while the shell's foreground right from the click lasts (as ShowMenu): a window the handler shows
            // later (posted) can then come in front, since the process owns the foreground.
            PInvoke.SetForegroundWindow(_window.Handle);
            Clicked?.Invoke();
        }
        else if (notification == PInvoke.WM_CONTEXTMENU)
        {
            // The anchor in screen coordinates: GET_X_LPARAM / GET_Y_LPARAM of wParam.
            var anchor = (nint)wParam.Value;
            ShowMenu((short)(anchor & 0xFFFF), (short)((anchor >> 16) & 0xFFFF));
        }
        return (LRESULT)0;
    }

    private LRESULT? OnEndSession(WPARAM wParam, LPARAM lParam)
    {
        // wParam FALSE: an app or the user cancelled the end.
        if (wParam.Value != 0) SessionEnding?.Invoke();
        return (LRESULT)0;
    }

    private LRESULT? OnTaskbarCreated(WPARAM wParam, LPARAM lParam)
    {
        if (!_disposed) Add();
        return null;
    }

    /// <summary>The menu at (<paramref name="x"/>, <paramref name="y"/>), modal until it closes; then the picked item's click.</summary>
    private void ShowMenu(int x, int y)
    {
        if (MenuItems?.Invoke() is not { Count: > 0 } items) return;
        var clicks = new List<Action?>();
        var menu = BuildMenu(items, clicks);
        if (menu.IsNull) return;
        int picked;
        try
        {
            // The menu closes on a click elsewhere only while its window is the foreground window, and the posted
            // WM_NULL makes the next click on the icon open it again (KB135788).
            PInvoke.SetForegroundWindow(_window.Handle);
            var flags = TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON
                | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN
                | (PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_MENUDROPALIGNMENT) != 0
                    ? TRACK_POPUP_MENU_FLAGS.TPM_RIGHTALIGN : TRACK_POPUP_MENU_FLAGS.TPM_LEFTALIGN);
            unsafe
            {
                picked = PInvoke.TrackPopupMenuEx(menu, (uint)flags, x, y, _window.Handle, null).Value;
            }
            PInvoke.PostMessage(_window.Handle, PInvoke.WM_NULL, 0, 0);
        }
        finally
        {
            // Destroys the submenus too.
            PInvoke.DestroyMenu(menu);
        }
        // Ids are 1-based: 0 means closed without a pick.
        if (picked > 0 && picked <= clicks.Count) clicks[picked - 1]?.Invoke();
    }

    /// <summary>A popup menu of <paramref name="items"/>; each item's id is its index in <paramref name="clicks"/> plus 1.</summary>
    private unsafe HMENU BuildMenu(IReadOnlyList<NotifyIconMenuItem> items, List<Action?> clicks)
    {
        var menu = PInvoke.CreatePopupMenu();
        if (menu.IsNull)
        {
            Log("CreatePopupMenu");
            return menu;
        }
        foreach (var item in items)
        {
            if (item.Text is null)
            {
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, default(PCWSTR));
                continue;
            }
            var flags = item.Enabled ? MENU_ITEM_FLAGS.MF_STRING : MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_GRAYED;
            if (item.Checked) flags |= MENU_ITEM_FLAGS.MF_CHECKED;
            nuint id;
            if (item.Submenu is { Count: > 0 } children)
            {
                var submenu = BuildMenu(children, clicks);
                if (submenu.IsNull) continue;
                flags |= MENU_ITEM_FLAGS.MF_POPUP;
                id = (nuint)submenu.Value;
            }
            else
            {
                clicks.Add(item.Click);
                id = (nuint)clicks.Count;
            }
            // "&" marks an access key: an app name such as "AT&T" keeps its "&".
            fixed (char* text = item.Text.Replace("&", "&&"))
            {
                if (!PInvoke.AppendMenu(menu, flags, id, text)) Log("AppendMenu");
            }
        }
        return menu;
    }

    private void Log(string call) => _log?.Invoke($"notify icon: {call} failed, {Marshal.GetLastPInvokeError()}");

    /// <summary>
    /// NOTIFYICONDATAW, 64-bit layout (976 bytes; the app ships x64 and ARM64 only). Declared here: CsWin32 generates it
    /// and Shell_NotifyIconW only for a fixed CPU architecture, and Platform builds as AnyCPU.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct NotifyIconData
    {
        public uint cbSize;
        public HWND hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public HICON hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion; // union with uTimeout
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern unsafe int Shell_NotifyIconW(uint message, NotifyIconData* data);
}
