using Microsoft.UI.Xaml;

namespace EchoDiar.App;

public partial class App : Application
{
    /// <summary>The library root, the real %LOCALAPPDATA%\EchoDiar (manifest turns write virtualization off).</summary>
    internal static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoDiar");

    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
#if DEBUG
        WriteLaunchProbe();
        // Headless harnesses (--capture-probe, --native-probe): no window, exit when done.
        // The alias echodiar.exe passes its arguments on the process command line.
        if (CaptureProbe.TryRun(Environment.GetCommandLineArgs(), Exit)) return;
#endif
        _window = new MainWindow();
        _window.Activate();
    }

#if DEBUG
    // Phase 1 gate: a non-packaged shell must see this file at the real path.
    private static void WriteLaunchProbe()
    {
        try
        {
            var probes = Path.Combine(DataRoot, "Probes");
            Directory.CreateDirectory(probes);
            File.WriteAllText(Path.Combine(probes, "hello.txt"), DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Launch probe failed: {ex.Message}");
        }
    }
#endif
}
