using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoDiar.App.ViewModels;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Storage;

namespace EchoDiar.App.Services;

/// <summary>What a check or an install the user asked for ended with; the toolbar's dialog queue shows it.</summary>
internal enum UpdateReportKind { UpToDate, Available, CheckFailed, InstallFailed }

/// <param name="Reason">A short reason for a failure (one line, no exception dump).</param>
internal sealed record UpdateReport(UpdateReportKind Kind, string? Reason = null);

/// <summary>
/// App Installer updates (the Windows counterpart of macOS Updater.swift, which drives Sparkle). Active only when the
/// app was installed from an .appinstaller file (<c>Package.GetAppInstallerInfo</c>); a sideloaded MSIX, or a run
/// without a package, never checks. Background checks (at <see cref="Start"/>, then every <see cref="CheckInterval"/>)
/// run off the UI thread and never show a dialog: a found update sets <see cref="UpdateAvailable"/>, which shows the
/// toolbar's Update button and turns the tray item into "Update available…". A check the user asks for
/// (<see cref="CheckNowAsync"/>) ends in a <see cref="Report"/> the toolbar shows. <see cref="InstallAsync"/> installs
/// from the .appinstaller with ForceTargetAppShutdown: Windows closes the app (the tray window's WM_ENDSESSION stops
/// everything) and relaunches it, as it was registered with RegisterApplicationRestart just before. The last finished
/// check is kept in LocalSettings (macOS: Sparkle's defaults), not in the shared settings.json. State changes on the UI
/// thread only; <see cref="Dispose"/> cancels a check under way (an install goes on: Windows ends the app for it).
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

    /// <summary>The .appinstaller the app was installed from; null: the updater is off.</summary>
    private readonly Uri? _appInstaller;

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
        _appInstaller = AppInstallerUri();
        if (_appInstaller is not null) _lastCheck = ReadLastCheck();
    }

    /// <summary>Installed from an .appinstaller file: checks run and an update can be installed.</summary>
    public bool IsActive => _appInstaller is not null;

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
    /// Installs the update from the .appinstaller. Windows closes the app on success and relaunches it; a failure ends
    /// in a <see cref="Report"/>. The caller has checked that nothing is running that the close would cut off.
    /// </summary>
    public async Task InstallAsync()
    {
        if (IsInstalling) return;
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
