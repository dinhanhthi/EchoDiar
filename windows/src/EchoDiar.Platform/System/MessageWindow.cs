using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

// Not EchoDiar.Platform.System: that namespace would hide System for every System.-qualified name under EchoDiar.Platform.
namespace EchoDiar.Platform.SystemServices;

/// <summary>
/// A hidden message-only window (parent HWND_MESSAGE): a target for Windows notifications sent to a window handle
/// (session changes, suspend and resume; Phase 21: hot keys, tray icon callbacks). Handlers are added per message.
/// With <c>topLevel</c>, a hidden top-level window instead (a popup that is never shown): only those get the messages
/// Windows broadcasts or sends to every top-level window (WM_QUERYENDSESSION, WM_ENDSESSION, "TaskbarCreated"), and
/// only such a window can be made the foreground window for a notification icon's menu.
/// <para>
/// Threading: create, add or remove handlers and <see cref="Dispose"/> on one thread that pumps messages (the WinUI
/// UI thread). Windows delivers the window's messages on that thread, so handlers run there too. A handler that
/// throws is logged and the message goes to DefWindowProc: an exception must not cross the native frame (FailFast).
/// </para>
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    /// <summary>Gets a message's result, or null to pass it on (to the next handler, then DefWindowProc).</summary>
    internal delegate LRESULT? Handler(WPARAM wParam, LPARAM lParam);

    /// <summary>Rooted for the process's life: a collected delegate behind a live window class would crash.</summary>
    private static readonly WNDPROC Callback = WndProc;

    /// <summary>Live windows by handle, which also keeps each one alive until disposed.</summary>
    private static readonly Dictionary<HWND, MessageWindow> Windows = [];

    private readonly Dictionary<uint, List<Handler>> _handlers = [];
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly HINSTANCE _instance;
    private readonly string _className = "EchoDiar.MessageWindow." + Guid.NewGuid().ToString("N");
    private readonly Action<string>? _log;

    /// <summary>Creates the window. Throws <see cref="Win32Exception"/> if Windows refuses.</summary>
    /// <param name="log">Gets failures: handler exceptions, a failed destroy.</param>
    /// <param name="topLevel">A hidden top-level window instead of a message-only one (see the class summary).</param>
    internal unsafe MessageWindow(Action<string>? log = null, bool topLevel = false)
    {
        _log = log;
        _instance = new HINSTANCE(PInvoke.GetModuleHandle(default(PCWSTR)).Value);
        fixed (char* className = _className)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Callback,
                hInstance = _instance,
                lpszClassName = className,
            };
            // A class per window: a shared name would fail to register twice, and could not be unregistered while
            // another window uses it.
            if (PInvoke.RegisterClassEx(windowClass) == 0) throw new Win32Exception();

            // A tool window: never in the taskbar or Alt+Tab, even if something showed it.
            Handle = PInvoke.CreateWindowEx(
                topLevel ? WINDOW_EX_STYLE.WS_EX_TOOLWINDOW : 0, className, default(PCWSTR), topLevel ? WINDOW_STYLE.WS_POPUP : 0,
                0, 0, 0, 0, topLevel ? HWND.Null : HWND.HWND_MESSAGE, HMENU.Null, _instance, null);
            if (Handle.IsNull)
            {
                var error = new Win32Exception();
                PInvoke.UnregisterClass(className, _instance);
                throw error;
            }
        }
        // Messages sent while CreateWindowEx runs (WM_NCCREATE, WM_CREATE) find no entry and go to DefWindowProc.
        lock (Windows) Windows[Handle] = this;
    }

    internal HWND Handle { get; }

    /// <summary>
    /// Adds <paramref name="handler"/> for <paramref name="message"/>, after any handler already added for it.
    /// Thread of the window only.
    /// </summary>
    internal void AddHandler(uint message, Handler handler)
    {
        CheckThread();
        if (!_handlers.TryGetValue(message, out var list)) _handlers[message] = list = [];
        list.Add(handler);
    }

    /// <summary>Removes <paramref name="handler"/> for <paramref name="message"/>. Thread of the window only.</summary>
    internal void RemoveHandler(uint message, Handler handler)
    {
        CheckThread();
        if (_handlers.TryGetValue(message, out var list) && list.Remove(handler) && list.Count == 0) _handlers.Remove(message);
    }

    /// <summary>Destroys the window and unregisters its class; no handler runs afterwards. Thread of the window only.</summary>
    public unsafe void Dispose()
    {
        CheckThread();
        lock (Windows)
        {
            if (!Windows.Remove(Handle)) return; // disposed already
        }
        _handlers.Clear();
        if (!PInvoke.DestroyWindow(Handle)) _log?.Invoke($"message window: DestroyWindow failed, {Marshal.GetLastPInvokeError()}");
        fixed (char* className = _className)
        {
            if (!PInvoke.UnregisterClass(className, _instance))
            {
                _log?.Invoke($"message window: UnregisterClass failed, {Marshal.GetLastPInvokeError()}");
            }
        }
    }

    /// <summary>Throws unless called on the thread that created the window.</summary>
    internal void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException("MessageWindow must be used on the thread that created it.");
        }
    }

    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        MessageWindow? window;
        lock (Windows) Windows.TryGetValue(hwnd, out window);
        if (window is not null && window._handlers.TryGetValue(message, out var list))
        {
            // A copy: a handler may add or remove handlers.
            foreach (var handler in list.ToArray())
            {
                try
                {
                    if (handler(wParam, lParam) is { } result) return result;
                }
                catch (Exception e)
                {
                    window._log?.Invoke($"message window: a handler for 0x{message:X4} threw, {e.GetType().Name} {e.Message}");
                }
            }
        }
        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
