using System.Runtime.InteropServices;
using EchoDiar.Core.Settings;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

// Not EchoDiar.Platform.System: that namespace would hide System for every System.-qualified name under EchoDiar.Platform.
namespace EchoDiar.Platform.SystemServices;

/// <summary>
/// Global shortcuts (macOS GlobalHotKey.swift), through <c>RegisterHotKey</c> on a hidden message-only
/// <see cref="MessageWindow"/>: they work while other apps are in front and while EchoDiar's windows are hidden, and need
/// no keyboard hook. Each shortcut has an id of the caller's choice; a press raises <see cref="Pressed"/> with it. A
/// held shortcut does not repeat (MOD_NOREPEAT).
/// <para>
/// Threading: create, register and <see cref="Dispose"/> on the UI thread, which pumps the window's messages (Windows
/// posts WM_HOTKEY to the window that registered). <see cref="Pressed"/> is raised synchronously on that thread inside
/// the window procedure; a handler that throws is logged.
/// </para>
/// </summary>
public sealed class GlobalHotKeys : IDisposable
{
    /// <summary>ERROR_HOTKEY_ALREADY_REGISTERED: another app (or another id here) has the shortcut.</summary>
    public const int ErrorAlreadyRegistered = 1409;

    private const uint ModNoRepeat = 0x4000;

    private readonly MessageWindow _window;
    /// <summary>The registered shortcuts by id.</summary>
    private readonly Dictionary<int, WindowsHotKey> _registered = [];
    private bool _disposed;

    /// <summary>Creates the window. Throws <see cref="System.ComponentModel.Win32Exception"/> if Windows refuses.</summary>
    public GlobalHotKeys(Action<string>? log = null)
    {
        _window = new MessageWindow(log);
        _window.AddHandler(PInvoke.WM_HOTKEY, OnHotKey);
    }

    /// <summary>The window (tests send it messages).</summary>
    internal HWND Handle => _window.Handle;

    /// <summary>A registered shortcut was pressed: its id. UI thread, inside the window procedure.</summary>
    public event Action<int>? Pressed;

    /// <summary>
    /// Replaces the shortcut <paramref name="id"/> with <paramref name="key"/>. Returns 0, or the Win32 error when Windows
    /// refuses (<see cref="ErrorAlreadyRegistered"/>: in use); the id then has no shortcut. UI thread only.
    /// </summary>
    public int Register(int id, WindowsHotKey key)
    {
        _window.CheckThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        Unregister(id);
        if (!PInvoke.RegisterHotKey(_window.Handle, id, (HOT_KEY_MODIFIERS)(key.Modifiers | ModNoRepeat), key.VirtualKey))
        {
            return Marshal.GetLastPInvokeError();
        }
        _registered[id] = key;
        return 0;
    }

    /// <summary>Removes the shortcut <paramref name="id"/>, if any. UI thread only.</summary>
    public void Unregister(int id)
    {
        _window.CheckThread();
        if (_registered.Remove(id)) PInvoke.UnregisterHotKey(_window.Handle, id);
    }

    /// <summary>Unregisters every shortcut and destroys the window; no press follows. UI thread only.</summary>
    public void Dispose()
    {
        _window.CheckThread();
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _registered.Keys) PInvoke.UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
        _window.Dispose();
    }

    /// <summary>
    /// How Windows writes a shortcut: "Ctrl+Alt+R" (modifiers in the order Ctrl, Alt, Shift, Win); keys other than
    /// letters, digits and function keys by their name on the current keyboard layout (";", "Space", "Page Down").
    /// </summary>
    public static string Describe(WindowsHotKey key)
    {
        var text = "";
        if ((key.Modifiers & WindowsHotKey.ModControl) != 0) text += "Ctrl+";
        if ((key.Modifiers & WindowsHotKey.ModAlt) != 0) text += "Alt+";
        if ((key.Modifiers & WindowsHotKey.ModShift) != 0) text += "Shift+";
        if ((key.Modifiers & WindowsHotKey.ModWin) != 0) text += "Win+";
        return text + KeyName(key.VirtualKey);
    }

    private static string KeyName(uint virtualKey)
    {
        switch (virtualKey)
        {
            case >= 'A' and <= 'Z':
            case >= '0' and <= '9':
                return ((char)virtualKey).ToString();
            case >= 0x70 and <= 0x87: // VK_F1…VK_F24
                return $"F{virtualKey - 0x6F}";
        }
        // The scan code with the 0xE0 prefix of extended keys (arrows, Insert, Delete, Home, End, Page Up/Down…),
        // which GetKeyNameText tells apart by bit 24.
        var scanCode = PInvoke.MapVirtualKey(virtualKey, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC_EX);
        if (scanCode != 0)
        {
            var lParam = (int)((scanCode & 0xFF) << 16 | ((scanCode & 0xFF00) == 0xE000 ? 1u << 24 : 0));
            Span<char> name = stackalloc char[64];
            var length = PInvoke.GetKeyNameText(lParam, name);
            if (length > 0) return name[..length].ToString();
        }
        return $"Key {virtualKey}";
    }

    private LRESULT? OnHotKey(WPARAM wParam, LPARAM lParam)
    {
        var id = (int)wParam.Value;
        // A press of an id unregistered meanwhile (already posted) is dropped, and so is one whose keys (the modifiers in
        // LOWORD, the key in HIWORD) are not the id's: a message another process posted.
        if (!_registered.TryGetValue(id, out var key)) return null;
        var modifiers = (uint)(lParam.Value & 0xFFFF) & ~ModNoRepeat;
        var virtualKey = (uint)((lParam.Value >> 16) & 0xFFFF);
        if (modifiers != key.Modifiers || virtualKey != key.VirtualKey) return null;
        Pressed?.Invoke(id);
        return (LRESULT)0;
    }
}
