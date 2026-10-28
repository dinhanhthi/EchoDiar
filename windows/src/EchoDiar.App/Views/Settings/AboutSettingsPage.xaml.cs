using System.ComponentModel;
using System.Globalization;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;
using Windows.System;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › About (macOS SettingsWindow.swift <c>AboutSettingsView</c> and <c>AboutInfo</c>), plus the App Installer
/// updates (<see cref="Updater"/>): the last check and Check now, or why this copy does not update itself.
/// </summary>
public sealed partial class AboutSettingsPage : UserControl
{
    /// <summary>macOS <c>AboutInfo.repository</c>.</summary>
    private static readonly Uri Repository = new("https://github.com/dinhanhthi/EchoDiar");

    /// <summary>The website's explanation of the certificate import and its warnings (Windows only).</summary>
    private static readonly Uri WindowsWarnings = new("https://ed.dinhanhthi.com/docs.html#windows-warnings");

    /// <summary>The searchable rows (macOS SettingsSearch.swift, About), each the Tag of its card.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Version", ["about", "build", "update"]),
        new("Updates", ["update", "check for updates", "App Installer", "appinstaller", "last check"]),
        new(
            "Why Windows warns about EchoDiar",
            ["SmartScreen", "certificate", "signature", "unknown publisher", "UAC", "security"]),
        new("Licensed under AGPL-3.0", ["license", "open source"]),
        new("github.com/dinhanhthi/EchoDiar", ["GitHub", "source code", "repository"]),
    ];

    private readonly AppViewModel _model;

    public AboutSettingsPage(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        VersionCard.Description = Loc.Format("Version %@", Version);
        WarningsCard.Description = Loc.T(
            "Windows shows a warning when you install EchoDiar because it is not yet signed with a certificate from a public authority. EchoDiar is open source and built by GitHub Actions; every release lists its SHA-256 checksums. EchoDiar's own certificate is trusted on this PC so that updates install.");
        WarningsCard.Click += (_, _) => _ = Launcher.LaunchUriAsync(WindowsWarnings);
        RepositoryCard.Click += (_, _) => _ = Launcher.LaunchUriAsync(Repository);
        CheckUpdatesButton.Click += (_, _) => _ = _model.Updater.CheckNowAsync();
        Loaded += (_, _) =>
        {
            _model.Updater.PropertyChanged += OnUpdaterChanged;
            RefreshUpdates();
        };
        Unloaded += (_, _) => _model.Updater.PropertyChanged -= OnUpdaterChanged;
    }

    /// <summary>
    /// The package version as X.Y.Z, the same version as macOS ("1.2.0" for the package version 1.2.0.0; dev builds
    /// "0.0.N"). A non-zero revision is kept ("1.2.0.3"). Run without a package, the app's assembly version, formatted
    /// the same way.
    /// </summary>
    internal static string Version
    {
        get
        {
            try
            {
                var v = Package.Current.Id.Version;
                return FormatVersion(v.Major, v.Minor, v.Build, v.Revision);
            }
            catch (Exception)
            {
                // No package identity (an unpackaged run).
                return typeof(AboutSettingsPage).Assembly.GetName().Version is { } v
                    ? FormatVersion(v.Major, v.Minor, v.Build, v.Revision)
                    : "?";
            }
        }
    }

    private static string FormatVersion(int major, int minor, int build, int revision) =>
        revision > 0 ? $"{major}.{minor}.{build}.{revision}" : $"{major}.{minor}.{build}";

    private void OnUpdaterChanged(object? sender, PropertyChangedEventArgs e) => RefreshUpdates();

    private void RefreshUpdates()
    {
        var updater = _model.Updater;
        CheckUpdatesButton.Visibility = updater.IsActive ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdatesButton.IsEnabled = !updater.IsChecking && !updater.IsInstalling;
        UpdatesCard.Description = !updater.IsActive
            ? Loc.T("This copy was not installed from an .appinstaller file, so it does not update itself.")
            : updater.IsInstalling ? Loc.Format("Installing the update… %lld%%", updater.InstallPercent)
            : updater.IsChecking ? Loc.T("Checking for updates…")
            : updater.UpdateAvailable ? Loc.T("An update is available: Update in the toolbar installs it.")
            : updater.LastCheck is { } last ? Loc.Format("Last checked %@", last.ToString("g", CultureInfo.CurrentCulture))
            : Loc.T("Not checked yet");
    }
}
