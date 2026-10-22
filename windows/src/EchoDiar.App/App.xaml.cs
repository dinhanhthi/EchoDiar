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

#if DEBUG
    /// <summary>DEBUG <c>--no-backdrop</c>: the window paints the theme's solid background instead of setting a system backdrop (A/B in a VM).</summary>
    internal static bool NoBackdrop { get; } = Environment.GetCommandLineArgs().Contains("--no-backdrop");
#endif

    public App()
    {
        StartupTrace.Start(Paths.Root);
        StartupTrace.Write("App ctor");
        InitializeComponent();
        // Logged, not handled: the app still ends as it would.
        UnhandledException += (_, e) => CrashLog.Write(Paths.Root, $"Application.UnhandledException: {e.Message}", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write(Paths.Root, "AppDomain.UnhandledException", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            CrashLog.Write(Paths.Root, "TaskScheduler.UnobservedTaskException", e.Exception);
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        StartupTrace.Write("OnLaunched");
#if DEBUG
        // Off the UI thread: no file work runs there while the window is first shown.
        _ = Task.Run(WriteLaunchProbe);
        // Headless harnesses (--capture-probe, --native-probe, --seed-library): no window, exit when done. They run
        // before the single-instance key, so they also work while the app is open. The alias echodiar.exe passes its
        // arguments on the process command line.
        if (CaptureProbe.TryRun(Environment.GetCommandLineArgs(), Exit)) return;
        if (DebugLibrarySeed.TryRun(Environment.GetCommandLineArgs(), Exit)) return;
#endif
        // Single instance: a second launch activates the running app's window, then exits.
        StartupTrace.Write("begin FindOrRegisterForKey");
        var main = AppInstance.FindOrRegisterForKey("main");
        StartupTrace.Write($"end FindOrRegisterForKey (current {main.IsCurrent})");
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
        StartupTrace.Write("begin AppServices");
        _services = new AppServices(Paths);
        StartupTrace.Write("end AppServices");
        _model = new AppViewModel(_services, dispatcher);
        _window = new MainWindow(_model);
        _window.Closed += (_, _) =>
        {
            // A closed window fails every call: a redirected launch queued meanwhile finds none.
            _window = null;
            Shutdown();
        };
        StartupTrace.Write("begin Activate");
        _window.Activate();
        StartupTrace.Write("end Activate");
        try
        {
            await _model.StartAsync();
            StartupTrace.Write("StartAsync done");
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: launch failed: {e}");
            StartupTrace.Write($"StartAsync failed: {e.GetType().Name} {e.Message}");
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
        // A stop that did not end in time still holds the engine: disposing it would block on it.
        if (_model is { } model && !model.Shutdown()) return;
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
