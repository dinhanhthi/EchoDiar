using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EchoDiar.App.Themes;

/// <summary>
/// The window background, tinted like the macOS window glass (#1C1C1E at 0.6): Mica, or Desktop Acrylic where Mica
/// is unsupported. Where neither is (<see cref="IsSupported"/> false), the window paints <see cref="Tint"/> solid.
/// </summary>
internal sealed partial class TintedBackdrop : SystemBackdrop
{
    public static readonly Color Tint = Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1E);
    private const float TintOpacity = 0.6f;

    private ISystemBackdropControllerWithTargets? _controller;

    public static bool IsSupported => MicaController.IsSupported() || DesktopAcrylicController.IsSupported();

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        // Set the theme before the tint: a theme change resets the tint to the theme defaults.
        var config = new SystemBackdropConfiguration { Theme = SystemBackdropTheme.Dark, IsInputActive = true };
        _controller = CreateController(config);
        _controller?.AddSystemBackdropTarget(connectedTarget);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
    }

    private static ISystemBackdropControllerWithTargets? CreateController(SystemBackdropConfiguration config)
    {
        if (MicaController.IsSupported())
        {
            var mica = new MicaController();
            mica.SetSystemBackdropConfiguration(config);
            mica.TintColor = Tint;
            mica.TintOpacity = TintOpacity;
            mica.FallbackColor = Tint;
            return mica;
        }

        if (DesktopAcrylicController.IsSupported())
        {
            var acrylic = new DesktopAcrylicController();
            acrylic.SetSystemBackdropConfiguration(config);
            acrylic.TintColor = Tint;
            acrylic.TintOpacity = TintOpacity;
            acrylic.FallbackColor = Tint;
            return acrylic;
        }

        return null;
    }
}
