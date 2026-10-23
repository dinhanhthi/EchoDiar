using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.App.Views;
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
    private FloatingPillController? _pill;
    private OnboardingWindow? _onboarding;
    private TrayIcon? _tray;
    private DispatcherQueue? _dispatcher;

    /// <summary>The main window closes for good (Quit, the session's end): its close is no longer turned into a hide.</summary>
    private bool _quitting;

    /// <summary><see cref="Shutdown"/> ran: the session's end runs it before the window closes.</summary>
    private bool _shutDown;

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

        // Launch at login (Settings › General): only the tray icon, as macOS starts in the menu bar.
        var atLogin = AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _dispatcher = dispatcher;
        AppInstance.GetCurrent().Activated += (_, _) => dispatcher.TryEnqueue(ShowWindow);
        StartupTrace.Write("begin AppServices");
        _services = new AppServices(Paths);
        StartupTrace.Write("end AppServices");
        _model = new AppViewModel(_services, dispatcher);
        _window = new MainWindow(_model);
        _pill = new FloatingPillController(_model, _window, dispatcher);
        // X and Alt+F4 hide the window to the tray (macOS: the menu bar app runs on); Quit closes it.
        _window.AppWindow.Closing += OnMainWindowClosing;
        _window.Closed += (_, _) =>
        {
            _quitting = true;
            // Each apart, and none may stop the recording's stop and the settings write below.
            try
            {
                // No icon is left in the notification area, and no menu opens on a closing app.
                _tray?.Dispose();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not remove the tray icon: {e}");
            }
            _tray = null;
            try
            {
                // First, while the model still answers: a pending drag is saved, and the pill's window closes (an
                // open window, even hidden, would keep the app running).
                _pill?.Dispose();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not close the pill: {e}");
            }
            _pill = null;
            try
            {
                // An open onboarding window would keep the app running too; closing it lets go of its mic and loopback.
                _onboarding?.Close();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not close the onboarding: {e}");
            }
            _onboarding = null;
            // A closed window fails every call: a redirected launch queued meanwhile finds none.
            _window = null;
            Shutdown();
        };
        try
        {
            _tray = new TrayIcon(_model, dispatcher, ShowWindow, Quit, EndSession);
        }
        catch (Exception e)
        {
            // Without the icon the window is the only way back: its close quits, and it shows at login too.
            Debug.WriteLine($"EchoDiar: could not add the tray icon: {e}");
            StartupTrace.Write($"tray icon failed: {e.GetType().Name} {e.Message}");
        }
        // At launch (after the main window shows, once the settings are loaded) and from Settings › General.
        _model.OnboardingRequested += ShowOnboarding;
        if (atLogin && _tray is not null)
        {
            StartupTrace.Write("launched at login: tray only");
        }
        else
        {
            StartupTrace.Write("begin Activate");
            _window.Activate();
            StartupTrace.Write("end Activate");
        }
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

    /// <summary>A redirected launch, the tray icon and its menu: brings the window back (shown if hidden, restored if minimized).</summary>
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

    /// <summary>The onboarding in front: a new window from its first step, or the open one brought back.</summary>
    private void ShowOnboarding()
    {
        if (_window is null || _model is null) return;
        // Raised inside the launch: a failure here must not stop the library from loading.
        try
        {
            if (_onboarding is null)
            {
                var onboarding = new OnboardingWindow(_model, _window.AppWindow.Id);
                onboarding.Closed += (_, _) =>
                {
                    if (_onboarding == onboarding) _onboarding = null;
                };
                _onboarding = onboarding;
            }
            _onboarding.Activate();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not show the onboarding: {e}");
            StartupTrace.Write($"onboarding failed: {e.GetType().Name} {e.Message}");
        }
    }

    /// <summary>
    /// X or Alt+F4: the window hides and the app runs on in the tray (a recording goes on, the pill too); it opens again
    /// on the list (macOS windowWillClose). Without a tray icon, or once quitting, the window closes.
    /// </summary>
    private void OnMainWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_quitting || _tray is null || _window is not { } window || _model is not { } model) return;
        args.Cancel = true;
        window.SaveFrame();
        sender.Hide();
        model.MainWindowHidden();
    }

    /// <summary>"Quit › Sure to quit": the main window closes for good; its Closed stops everything and the app ends.</summary>
    private void Quit()
    {
        _quitting = true;
        _window?.Close();
    }

    /// <summary>
    /// Sign-out, shutdown or restart (WM_ENDSESSION, inside the tray icon's window procedure): the process may end once
    /// this returns, so a running recording is stopped now, synchronously (as before sleep), and the settings written;
    /// the app then quits as usual if it still runs.
    /// </summary>
    private void EndSession()
    {
        StartupTrace.Write("session ending");
        _quitting = true;
        Shutdown();
        _dispatcher?.TryEnqueue(Quit);
    }

    /// <summary>Quit or the session's end: a running recording is stopped so its audio file is finished. Runs once.</summary>
    private void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
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
