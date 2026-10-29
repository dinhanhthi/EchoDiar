using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoDiar.App.ViewModels;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Services.Store;
using Windows.Storage;

namespace EchoDiar.App.Services;

/// <summary>What a check or an install the user asked for ended with; the toolbar's dialog queue shows it.</summary>
internal enum UpdateReportKind { UpToDate, Available, CheckFailed, InstallFailed }

/// <summary>Where updates come from, chosen once at launch: none (a dev run, a sideloaded MSIX), the .appinstaller feed or the Microsoft Store.</summary>
internal enum UpdateSource { None, AppInstaller, Store }

/// <param name="Reason">A short reason for a failure (one line, no exception dump).</param>
internal sealed record UpdateReport(UpdateReportKind Kind, string? Reason = null);

/// <summary>
/// App Installer and Microsoft Store updates (the Windows counterpart of macOS Updater.swift, which drives Sparkle).
/// Active only when the package is signed by the Store (<c>StoreContext</c>, see the Store section below) or was
/// installed from an .appinstaller file (<c>Package.GetAppInstallerInfo</c>); a sideloaded MSIX, or a run without a
/// package, never checks. Background checks (at <see cref="Start"/>, then every <see cref="CheckInterval"/>) run off
/// the UI thread (a Store check hops to it: StoreContext needs it) and never show a dialog: a found update sets
/// <see cref="UpdateAvailable"/>, which shows the toolbar's Update button and turns the tray item into "Update
/// available…". A check the user asks for (<see cref="CheckNowAsync"/>) ends in a <see cref="Report"/> the toolbar
/// shows. <see cref="InstallAsync"/> installs from the .appinstaller with ForceTargetAppShutdown: Windows closes the
/// app (the tray window's WM_ENDSESSION stops everything) and relaunches it, as it was registered with
/// RegisterApplicationRestart just before. The last finished check is kept in LocalSettings (macOS: Sparkle's
/// defaults), not in the shared settings.json. State changes on the UI thread only; <see cref="Dispose"/> cancels a
/// check under way (an install goes on: Windows ends the app for it).
/// </summary>
internal sealed partial class Updater : ObservableObject, IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private const string LastCheckKey = "Updater.lastCheck";

    /// <summary>RESTART_NO_CRASH | RESTART_NO_HANG | RESTART_NO_REBOOT: relaunched only after an update.</summary>
    private const uint RestartOnUpdateOnly = 1 | 2 | 8;

    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>One check at a time: the schedule's and the user's.</summary>
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    /// <summary>The .appinstaller the app was installed from; null on the Store backend and when the updater is off.</summary>
    private readonly Uri? _appInstaller;

    /// <summary>The main window's handle, which the Store's dialogs belong to; 0 until <see cref="SetWindow"/>.</summary>
    private nint _window;

    /// <summary>Made once, on the UI thread, once the window is known (<see cref="StoreContextForWindow"/>).</summary>
    private StoreContext? _storeContext;

    /// <summary>What the last Store check found, installed as is by <see cref="InstallFromStoreAsync"/> (UI thread only).</summary>
    private IReadOnlyList<StorePackageUpdate>? _storeUpdates;

    private bool _started;
    private bool _isChecking;
    private bool _updateAvailable;
    private bool _isInstalling;
    private int _installPercent;
    private DateTimeOffset? _lastCheck;
    private UpdateReport? _report;

    public Updater(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        if (IsStoreSigned())
        {
            Source = UpdateSource.Store;
        }
        else
        {
            _appInstaller = AppInstallerUri();
            if (_appInstaller is not null) Source = UpdateSource.AppInstaller;
        }
        if (IsActive) _lastCheck = ReadLastCheck();
    }

    /// <summary>The backend chosen at launch.</summary>
    public UpdateSource Source { get; }

    /// <summary>Installed from the Store or from an .appinstaller file: checks run and an update can be installed.</summary>
    public bool IsActive => Source != UpdateSource.None;

    public bool IsChecking
    {
        get => _isChecking;
        private set => SetProperty(ref _isChecking, value);
    }

    /// <summary>The last check found a newer version (macOS <c>updateAvailable</c>).</summary>
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set => SetProperty(ref _updateAvailable, value);
    }

    /// <summary>An install is under way (downloading, then installing); <see cref="InstallPercent"/> says how far.</summary>
    public bool IsInstalling
    {
        get => _isInstalling;
        private set => SetProperty(ref _isInstalling, value);
    }

    public int InstallPercent
    {
        get => _installPercent;
        private set => SetProperty(ref _installPercent, value);
    }

    /// <summary>When the last check finished (any answer but an error); null when none has yet.</summary>
    public DateTimeOffset? LastCheck
    {
        get => _lastCheck;
        private set => SetProperty(ref _lastCheck, value);
    }

    /// <summary>The answer to the user's check or install, until the toolbar has shown it (<see cref="DismissReport"/>).</summary>
    public UpdateReport? Report
    {
        get => _report;
        private set => SetProperty(ref _report, value);
    }

    /// <summary>The tray item's text (macOS <c>menuTitle</c>).</summary>
    public string MenuTitle => !IsActive
        ? Loc.T("Updates need an .appinstaller install")
        : IsInstalling ? Loc.T("Installing the update…")
        : UpdateAvailable ? Loc.T("Update available…")
        : Loc.T("Check for updates…");

    /// <summary>Checks now, then every <see cref="CheckInterval"/>; once. Off: nothing runs.</summary>
    public void Start()
    {
#if DEBUG
        // VM check of the Update button and its dialogs without a feed (macOS --fake-update).
        if (Environment.GetCommandLineArgs().Contains("--fake-update")) UpdateAvailable = true;
#endif
        if (!IsActive || _started) return;
        _started = true;
        var token = _stop.Token;
        _ = Task.Run(() => RunScheduleAsync(token));
    }

    /// <summary>
    /// The main window (UI thread, before <see cref="Start"/>): StoreContext needs it for its dialogs. Until it is set,
    /// a Store check fails (logged) and nothing is installed.
    /// </summary>
    public void SetWindow(nint hwnd) => _window = hwnd;

    /// <summary>No more checks, a check under way is cancelled, and no state changes any more.</summary>
    public void Dispose()
    {
        _stop.Cancel();
    }

    /// <summary>The user's check (the tray, Settings › About): its answer becomes <see cref="Report"/>.</summary>
    public async Task CheckNowAsync()
    {
        if (!IsActive || IsInstalling) return;
        var token = _stop.Token;
        try
        {
            var report = await Task.Run(() => CheckAsync(token));
            if (!token.IsCancellationRequested) Report = report;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Shows the update (the tray's "Update available…"): the toolbar asks to install it, as after the user's check.
    /// </summary>
    public void OfferUpdate()
    {
        if (UpdateAvailable && !IsInstalling) Report = new UpdateReport(UpdateReportKind.Available);
    }

    /// <summary>The toolbar has shown <see cref="Report"/>.</summary>
    public void DismissReport() => Report = null;

    /// <summary>
    /// Installs the update from the .appinstaller or the Store. Windows closes the app on success and relaunches it; a
    /// failure ends in a <see cref="Report"/>. The caller (UI thread) has checked that nothing is running that the close
    /// would cut off.
    /// </summary>
    public async Task InstallAsync()
    {
        if (IsInstalling) return;
        if (Source == UpdateSource.Store)
        {
            await InstallFromStoreAsync();
            return;
        }
        if (_appInstaller is not { } uri)
        {
            // Only the DEBUG --fake-update button gets here.
            Report = new UpdateReport(UpdateReportKind.InstallFailed, Loc.T("This copy was not installed from an .appinstaller file."));
            return;
        }
        IsInstalling = true;
        InstallPercent = 0;
        // Not cancelled by Dispose: Windows closing the app for the update ends in Shutdown, which must not stop it.
        var stopped = _stop.Token;
        string? failure = null;
        try
        {
            // Before the update starts: Windows relaunches an app it closed for an update only when it was registered.
            var registered = RegisterApplicationRestart(null, RestartOnUpdateOnly);
            if (registered != 0) Debug.WriteLine($"EchoDiar: RegisterApplicationRestart failed (0x{registered:X8}).");
            // Created on the UI thread, so its reports come back to it.
            var progress = new Progress<DeploymentProgress>(p =>
            {
                if (!stopped.IsCancellationRequested) InstallPercent = (int)Math.Min(p.percentage, 100);
            });
            await Task.Run(async () =>
            {
                var manager = new PackageManager();
                await manager.AddPackageByAppInstallerFileAsync(
                    uri, AddPackageByAppInstallerOptions.ForceTargetAppShutdown, manager.GetDefaultPackageVolume())
                    .AsTask(CancellationToken.None, progress);
            });
            // Still running: Windows did not need to close it (the update waits for the next launch).
            if (!stopped.IsCancellationRequested) UpdateAvailable = false;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: update failed: {e.GetType().Name} 0x{e.HResult:X8}");
            failure = Reason(e);
        }
        finally
        {
            if (!stopped.IsCancellationRequested) IsInstalling = false;
        }
        if (failure is not null && !stopped.IsCancellationRequested) Report = new UpdateReport(UpdateReportKind.InstallFailed, failure);
    }

    private async Task RunScheduleAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            do
            {
                // Silent: a found update shows the button, a failure only the log.
                await CheckAsync(token);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One check, off the UI thread; its state goes to the UI thread.</summary>
    private async Task<UpdateReport> CheckAsync(CancellationToken token)
    {
        await _checkGate.WaitAsync(token);
        Post(() => IsChecking = true);
        try
        {
            if (Source == UpdateSource.Store) return await CheckStoreAsync(token);
            var result = await Package.Current.CheckUpdateAvailabilityAsync().AsTask(token);
            switch (result.Availability)
            {
                case PackageUpdateAvailability.Available:
                case PackageUpdateAvailability.Required:
                    Post(() => Checked(available: true));
                    return new UpdateReport(UpdateReportKind.Available);
                case PackageUpdateAvailability.NoUpdates:
                    Post(() => Checked(available: false));
                    return new UpdateReport(UpdateReportKind.UpToDate);
                default:
                    Debug.WriteLine($"EchoDiar: update check: {result.Availability} 0x{result.ExtendedError?.HResult ?? 0:X8}");
                    return new UpdateReport(UpdateReportKind.CheckFailed, result.ExtendedError is { } error ? Reason(error) : null);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: update check failed: {e.GetType().Name} 0x{e.HResult:X8}");
            return new UpdateReport(UpdateReportKind.CheckFailed, Reason(e));
        }
        finally
        {
            Post(() => IsChecking = false);
            _checkGate.Release();
        }
    }

    /// <summary>A check answered: the button follows, and the time is kept.</summary>
    private void Checked(bool available)
    {
        if (!IsInstalling) UpdateAvailable = available;
        var now = DateTimeOffset.Now;
        LastCheck = now;
        try
        {
            ApplicationData.Current.LocalSettings.Values[LastCheckKey] = now.ToUnixTimeSeconds();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not save the last update check: {e.GetType().Name}");
        }
    }

    private void Post(Action action)
    {
        if (_stop.IsCancellationRequested) return;
        _dispatcher.TryEnqueue(() =>
        {
            if (!_stop.IsCancellationRequested) action();
        });
    }

    // Microsoft Store backend.
    //
    // https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/package-updates-from-store
    // - StoreContext needs package identity (a Store-installed package).
    // - In a desktop app (WinUI 3) the context is tied to a window with WinRT.Interop.InitializeWithWindow before its
    //   first call; calls run on the UI thread (otherwise 0x80070578 ERROR_INVALID_WINDOW_HANDLE).
    // - RequestDownloadAndInstallStorePackageUpdatesAsync reports PackageDownloadProgress from 0 to 0.8 while it
    //   downloads and from 0.8 to 1.0 while it installs; the install may close the app (hence RegisterApplicationRestart).
    // - After a submission is certified, it can take up to a day before a check sees the new version.
    // https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.packagesignaturekind
    // - Store: the package is signed by the Windows Store; a self-signed sideload (the .appinstaller feed, a dev
    //   install) reports Developer.

    /// <summary>One Store check, on the UI thread; a failure is logged and shows no exception text.</summary>
    private async Task<UpdateReport> CheckStoreAsync(CancellationToken token)
    {
        try
        {
            var available = await OnUIThreadAsync<bool?>(async () =>
            {
                if (StoreContextForWindow() is not { } context) return null;
                var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(token);
                _storeUpdates = updates;
                return updates.Count > 0;
            });
            if (available is not { } found)
            {
                Debug.WriteLine("EchoDiar: Store update check: no window yet.");
                return new UpdateReport(UpdateReportKind.CheckFailed);
            }
            Post(() => Checked(available: found));
            return new UpdateReport(found ? UpdateReportKind.Available : UpdateReportKind.UpToDate);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: Store update check failed: {e.GetType().Name} 0x{e.HResult:X8}");
            return new UpdateReport(UpdateReportKind.CheckFailed);
        }
    }

    /// <summary>
    /// Installs what the last Store check found (UI thread). The Store may close the app and relaunch it; Completed with
    /// the app still running means the new version waits for the next launch; Canceled (the user said no in the Store's
    /// dialog) is not a failure.
    /// </summary>
    private async Task InstallFromStoreAsync()
    {
        IsInstalling = true;
        InstallPercent = 0;
        // Not cancelled by Dispose: the Store closing the app for the update ends in Shutdown, which must not stop it.
        var stopped = _stop.Token;
        var failed = false;
        try
        {
            // Before the update starts: Windows relaunches an app it closed for an update only when it was registered.
            var registered = RegisterApplicationRestart(null, RestartOnUpdateOnly);
            if (registered != 0) Debug.WriteLine($"EchoDiar: RegisterApplicationRestart failed (0x{registered:X8}).");
            if (StoreContextForWindow() is not { } context || _storeUpdates is not { Count: > 0 } updates)
            {
                Debug.WriteLine("EchoDiar: Store update: no window or nothing found to install.");
                failed = true;
                return;
            }
            // Download 0 to 0.8, install 0.8 to 1.0: one scale, shown as a percent.
            var progress = new Progress<StorePackageUpdateStatus>(status =>
                Post(() => InstallPercent = (int)Math.Clamp(Math.Round(status.PackageDownloadProgress * 100), 0, 100)));
            var result = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates)
                .AsTask(CancellationToken.None, progress);
            switch (result.OverallState)
            {
                case StorePackageUpdateState.Completed:
                    // Still running: the Store did not need to close it (the update waits for the next launch).
                    _storeUpdates = null;
                    if (!stopped.IsCancellationRequested) UpdateAvailable = false;
                    break;
                case StorePackageUpdateState.Canceled:
                    break;
                default:
                    Debug.WriteLine($"EchoDiar: Store update failed: {result.OverallState}");
                    failed = true;
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: Store update failed: {e.GetType().Name} 0x{e.HResult:X8}");
            failed = true;
        }
        finally
        {
            if (!stopped.IsCancellationRequested) IsInstalling = false;
            if (failed && !stopped.IsCancellationRequested) Report = new UpdateReport(UpdateReportKind.InstallFailed);
        }
    }

    /// <summary>The Store's context for the main window, made once on the UI thread; null until the window is known.</summary>
    private StoreContext? StoreContextForWindow()
    {
        if (_storeContext is not null || _window == 0) return _storeContext;
        try
        {
            var context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, _window);
            _storeContext = context;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not open the Store context: {e.GetType().Name} 0x{e.HResult:X8}");
        }
        return _storeContext;
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread (StoreContext needs it) and hands back its result or error.</summary>
    private Task<T> OnUIThreadAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                done.TrySetResult(await work());
            }
            catch (OperationCanceledException e)
            {
                done.TrySetCanceled(e.CancellationToken);
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        });
        if (!queued) done.TrySetException(new InvalidOperationException("The UI thread has stopped."));
        return done.Task;
    }

    /// <summary>Signed by the Microsoft Store; false for a self-signed package or a run without a package.</summary>
    private static bool IsStoreSigned()
    {
        try
        {
            return Package.Current.SignatureKind == PackageSignatureKind.Store;
        }
        catch (Exception)
        {
            // No package identity (an unpackaged run).
            return false;
        }
    }

    /// <summary>The .appinstaller's address, or null for a sideloaded MSIX or a run without a package.</summary>
    private static Uri? AppInstallerUri()
    {
        try
        {
            return Package.Current.GetAppInstallerInfo()?.Uri;
        }
        catch (Exception)
        {
            // No package identity (an unpackaged run).
            return null;
        }
    }

    private static DateTimeOffset? ReadLastCheck()
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values[LastCheckKey] is long seconds
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The first line of the error, cut short, else its code: never a stack or an inner exception.</summary>
    private static string Reason(Exception error)
    {
        var line = ProvidersViewModel.ShortMessage(error);
        return line.Length > 0 ? line : $"0x{error.HResult:X8}";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, uint flags);
}
