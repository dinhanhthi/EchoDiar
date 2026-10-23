using EchoDiar.Core.Settings;
using EchoDiar.Platform.SystemServices;
using Windows.Win32;

namespace EchoDiar.Platform.Tests;

// Registration on the real system (a combo nothing else uses), a combo taken twice, and the press through same-thread
// SendMessage of WM_HOTKEY (no message loop needed), as SystemEventsTests. A real key press is checked in the VM (21.4).
// Synchronous: the window must be disposed on the thread that created it.
public sealed class GlobalHotKeysTests
{
    /// <summary>Ctrl+Alt+Shift+F24: no keyboard has the key, so no app on the runner registers it.</summary>
    private static readonly WindowsHotKey Exotic = new()
    {
        VirtualKey = 0x87,
        Modifiers = WindowsHotKey.ModControl | WindowsHotKey.ModAlt | WindowsHotKey.ModShift,
    };

    [Fact]
    public void ASecondRegistrationOfTheSameComboIsInUseUntilTheFirstIsGone()
    {
        using var first = new GlobalHotKeys();
        using var second = new GlobalHotKeys();

        Assert.Equal(0, first.Register(1, Exotic));
        Assert.Equal(GlobalHotKeys.ErrorAlreadyRegistered, second.Register(1, Exotic));
        Assert.Equal(GlobalHotKeys.ErrorAlreadyRegistered, first.Register(2, Exotic));

        first.Unregister(1);
        Assert.Equal(0, second.Register(1, Exotic));

        second.Dispose();
        Assert.Equal(0, first.Register(1, Exotic)); // Dispose let it go
    }

    [Fact]
    public void ReRegisteringAnIdReplacesItsCombo()
    {
        using var hotKeys = new GlobalHotKeys();
        using var other = new GlobalHotKeys();
        var f23 = Exotic with { VirtualKey = 0x86 };

        Assert.Equal(0, hotKeys.Register(1, Exotic));
        Assert.Equal(0, hotKeys.Register(1, f23));

        Assert.Equal(0, other.Register(1, Exotic)); // the old combo is free again
    }

    [Fact]
    public void WmHotKeyRaisesPressedForRegisteredIdsOnly()
    {
        var hotKeys = new GlobalHotKeys();
        var handle = hotKeys.Handle;
        var pressed = new List<int>();
        hotKeys.Pressed += pressed.Add;
        Assert.Equal(0, hotKeys.Register(7, Exotic));

        PInvoke.SendMessage(handle, PInvoke.WM_HOTKEY, 7, LParam(Exotic));
        PInvoke.SendMessage(handle, PInvoke.WM_HOTKEY, 8, LParam(Exotic)); // never registered
        hotKeys.Unregister(7);
        PInvoke.SendMessage(handle, PInvoke.WM_HOTKEY, 7, LParam(Exotic)); // posted before the unregister
        hotKeys.Dispose();

        Assert.Equal([7], pressed);
        Assert.False(PInvoke.IsWindow(handle));
    }

    [Fact]
    public void WmHotKeyWithOtherKeysIsIgnored()
    {
        using var hotKeys = new GlobalHotKeys();
        var pressed = new List<int>();
        hotKeys.Pressed += pressed.Add;
        Assert.Equal(0, hotKeys.Register(7, Exotic));

        PInvoke.SendMessage(hotKeys.Handle, PInvoke.WM_HOTKEY, 7, 0);
        PInvoke.SendMessage(hotKeys.Handle, PInvoke.WM_HOTKEY, 7, LParam(Exotic with { VirtualKey = 0x86 }));
        PInvoke.SendMessage(hotKeys.Handle, PInvoke.WM_HOTKEY, 7, LParam(Exotic with { Modifiers = WindowsHotKey.ModControl }));
        Assert.Empty(pressed);

        PInvoke.SendMessage(hotKeys.Handle, PInvoke.WM_HOTKEY, 7, LParam(Exotic));
        Assert.Equal([7], pressed);
    }

    /// <summary>WM_HOTKEY's lParam as Windows sends it: the modifiers in LOWORD, the key in HIWORD.</summary>
    private static nint LParam(WindowsHotKey key) => (nint)(key.VirtualKey << 16 | key.Modifiers);

    [Fact]
    public void DescribeWritesTheWindowsForm()
    {
        Assert.Equal("Ctrl+Alt+R", GlobalHotKeys.Describe(WindowsHotKey.DefaultRecord));
        Assert.Equal("Ctrl+Alt+Shift+F24", GlobalHotKeys.Describe(Exotic));
        Assert.Equal("Alt+Win+5", GlobalHotKeys.Describe(new() { VirtualKey = '5', Modifiers = WindowsHotKey.ModAlt | WindowsHotKey.ModWin }));
    }
}
