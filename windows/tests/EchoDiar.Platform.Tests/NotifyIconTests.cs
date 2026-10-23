using EchoDiar.Platform.SystemServices;
using Windows.Win32;

namespace EchoDiar.Platform.Tests;

// The tray icon's window through same-thread SendMessage, as SystemEventsTests: clicks and the session's end. Explorer
// may refuse the icon on a CI runner; the window must work either way. The menu (modal) is checked in the VM (21.4).
public sealed class NotifyIconTests
{
    private static readonly string MissingIcon = Path.Combine(Path.GetTempPath(), "echodiar-missing.ico");

    [Fact]
    public void ClickAndKeySelectRaiseClicked()
    {
        var log = new List<string>();
        using var icon = new NotifyIcon(MissingIcon, "EchoDiar", log.Add);
        var clicks = 0;
        icon.Clicked += () => clicks++;

        PInvoke.SendMessage(icon.Handle, NotifyIcon.CallbackMessage, 0, (nint)PInvoke.NIN_SELECT);
        PInvoke.SendMessage(icon.Handle, NotifyIcon.CallbackMessage, 0, (nint)NotifyIcon.KeySelect);
        PInvoke.SendMessage(icon.Handle, NotifyIcon.CallbackMessage, 0, 0x0200); // WM_MOUSEMOVE

        Assert.Equal(2, clicks);
        Assert.Contains(log, line => line.Contains("LoadImage"));
    }

    [Fact]
    public void SessionEndingIsRaisedOnlyWhenTheSessionEnds()
    {
        using var icon = new NotifyIcon(MissingIcon, "EchoDiar");
        var ended = 0;
        icon.SessionEnding += () => ended++;

        var query = PInvoke.SendMessage(icon.Handle, PInvoke.WM_QUERYENDSESSION, 0, 0);
        PInvoke.SendMessage(icon.Handle, PInvoke.WM_ENDSESSION, 0, 0); // cancelled
        Assert.Equal(0, ended);
        PInvoke.SendMessage(icon.Handle, PInvoke.WM_ENDSESSION, 1, 0);

        Assert.Equal(1, query.Value);
        Assert.Equal(1, ended);
    }

    [Fact]
    public void NotifyIconDataHasTheWindowsLayout()
    {
        // NOTIFYICONDATAW is declared by hand for 64-bit processes (the app ships x64 and ARM64 only).
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(976, NotifyIcon.DataSize);
    }

    [Fact]
    public void DisposeDestroysTheWindow()
    {
        var icon = new NotifyIcon(MissingIcon, "EchoDiar");
        icon.SetGlyph(NotifyIconGlyph.Record, Windows.UI.Color.FromArgb(255, 196, 43, 28));
        icon.SetTooltip("EchoDiar, recording");
        var handle = icon.Handle;

        icon.Dispose();

        Assert.False(PInvoke.IsWindow(handle));
        Assert.False(icon.IsAdded);
    }
}
