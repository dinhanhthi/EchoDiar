using System.Xml.Linq;
using EchoDiar.Core;

namespace EchoDiar.Tests.Core;

// Guards what the Store's unvirtualizedResources justification promises: file write virtualization stays on everywhere
// except the one library folder (AppPaths.DefaultRoot), and registry writes stay virtualized.
public sealed class PackageManifestTests
{
    private static readonly XNamespace Virtualization = "http://schemas.microsoft.com/appx/manifest/virtualization/windows10";
    private static readonly XNamespace Desktop6 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6";

    private static readonly XDocument Manifest =
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.appxmanifest"));

    [Fact]
    public void ExcludesOnlyTheLibraryFolderFromFileVirtualization()
    {
        var excluded = Assert.Single(Manifest.Descendants(Virtualization + "ExcludedDirectory"));
        Assert.Equal(@"$(KnownFolder:LocalAppData)\" + Path.GetFileName(AppPaths.DefaultRoot), excluded.Value.Trim());
    }

    [Fact]
    public void TurnsNoVirtualizationOffWholesale()
    {
        Assert.Empty(Manifest.Descendants(Desktop6 + "FileSystemWriteVirtualization"));
        Assert.Empty(Manifest.Descendants(Desktop6 + "RegistryWriteVirtualization"));
        Assert.Empty(Manifest.Descendants(Virtualization + "RegistryWriteVirtualization"));
    }
}
