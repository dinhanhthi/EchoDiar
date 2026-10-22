using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
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
        new("Data folder", ["File Explorer", "location", "path", "open in File Explorer"]),
    ];

    private readonly string _folder;

    public AboutSettingsPage(AppViewModel model)
    {
        _folder = model.ConversationsFolder;
        InitializeComponent();
        VersionCard.Description = Loc.Format("Version %@", Version);
        DataFolderText.Text = _folder;
        RepositoryCard.Click += (_, _) => _ = Launcher.LaunchUriAsync(Repository);
        OpenFolderButton.Click += (_, _) => _ = OpenFolderAsync();
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

    /// <summary>The folder in File Explorer, created first: it exists only after the first recording (macOS).</summary>
    private async Task OpenFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            await Launcher.LaunchFolderPathAsync(_folder);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not open {_folder}: {e}");
        }
    }
}
