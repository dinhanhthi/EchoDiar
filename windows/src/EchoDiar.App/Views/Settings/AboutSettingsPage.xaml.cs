using EchoDiar.App.Services;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;
using Windows.System;

namespace EchoDiar.App.Views;

/// <summary>Settings › About (macOS SettingsWindow.swift <c>AboutSettingsView</c> and <c>AboutInfo</c>).</summary>
public sealed partial class AboutSettingsPage : UserControl
{
    /// <summary>macOS <c>AboutInfo.repository</c>.</summary>
    private static readonly Uri Repository = new("https://github.com/dinhanhthi/EchoDiar");

    /// <summary>The searchable rows (macOS SettingsSearch.swift, About), each the Tag of its card.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Version", ["about", "build", "update"]),
        new("Licensed under AGPL-3.0", ["license", "open source"]),
        new("github.com/dinhanhthi/EchoDiar", ["GitHub", "source code", "repository"]),
    ];

    public AboutSettingsPage()
    {
        InitializeComponent();
        VersionCard.Description = Loc.Format("Version %@", Version);
        RepositoryCard.Click += (_, _) => _ = Launcher.LaunchUriAsync(Repository);
    }

    /// <summary>The package version ("0.0.12.0", set by CI), or the app's assembly version when run without a package.</summary>
    private static string Version
    {
        get
        {
            try
            {
                var v = Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch (Exception)
            {
                // No package identity (an unpackaged run).
                return typeof(AboutSettingsPage).Assembly.GetName().Version?.ToString() ?? "?";
            }
        }
    }
}
