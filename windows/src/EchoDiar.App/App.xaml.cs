using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace EchoDiar.App;

public partial class App : Application
{
    /// <summary>The library layout under the real %LOCALAPPDATA%\EchoDiar (manifest turns write virtualization off).</summary>
    internal static AppPaths Paths { get; } = new();

    /// <summary>The library root (<see cref="Paths"/>), for the DEBUG harnesses.</summary>
    internal static string DataRoot => Paths.Root;

    private MainWindow? _window;
    private AppServices? _services;
    private AppViewModel? _model;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
#if DEBUG
        WriteLaunchProbe();
        // Headless harnesses (--capture-probe, --native-probe): no window, exit when done. They run before the
        // single-instance key, so they also work while the app is open. The alias echodiar.exe passes its arguments on
        // the process command line.
        if (CaptureProbe.TryRun(Environment.GetCommandLineArgs(), Exit)) return;
#endif
        // Single instance: a second launch activates the running app's window, then exits.
        var main = AppInstance.FindOrRegisterForKey("main");
        if (!main.IsCurrent)
        {
            try
            {
                await main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not redirect the activation: {e}");
            }
            Exit();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        AppInstance.GetCurrent().Activated += (_, _) => dispatcher.TryEnqueue(ShowWindow);
        _services = new AppServices(Paths);
        _model = new AppViewModel(_services, dispatcher);
        _window = new MainWindow(_model);
        _window.Closed += (_, _) => Shutdown();
        _window.Activate();
        try
        {
            await _model.StartAsync();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: launch failed: {e}");
        }
    }

    /// <summary>A redirected launch: brings the window back, restored if minimized.</summary>
    private void ShowWindow()
    {
        if (_window is null) return;
        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        _window.AppWindow.Show();
        _window.Activate();
    }

    /// <summary>The window closed: a running recording is stopped so its audio file is finished.</summary>
    private void Shutdown()
    {
        _model?.Shutdown();
        _services?.Dispose();
    }

#if DEBUG
    // Phase 1 gate: a non-packaged shell must see this file at the real path.
    private static void WriteLaunchProbe()
    {
        try
        {
            Directory.CreateDirectory(Paths.Probes);
            File.WriteAllText(Path.Combine(Paths.Probes, "hello.txt"), DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Launch probe failed: {ex.Message}");
        }
    }
#endif
}
