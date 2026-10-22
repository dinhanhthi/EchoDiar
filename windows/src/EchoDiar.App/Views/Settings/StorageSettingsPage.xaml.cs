using System.ComponentModel;
using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › Storage (macOS StorageSettingsView.swift), the Usage and Audio inner tabs; the title bar picks the tab
/// (<see cref="AppViewModel.SubTabOf"/>). Usage is measured off the UI thread each time the tab shows and after every
/// library change while it shows. Every setting shows <see cref="SettingsViewModel"/> and changes it; the change is
/// saved after its delay.
/// </summary>
public sealed partial class StorageSettingsPage : UserControl
{
    public const string UsageTab = "usage";
    public const string AudioTab = "audio";

    /// <summary>macOS <c>SettingsTab.storage.subTabs</c>; Auto-delete ("autoDelete") comes with Phase 16.</summary>
    public static IReadOnlyList<SettingsSubTab> SubTabs { get; } = [new(UsageTab, "Usage"), new(AudioTab, "Audio")];

    /// <summary>The searchable rows (macOS SettingsSearch.swift, Storage), each the Tag of its card.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Total", ["disk space", "usage", "size", "storage used"], UsageTab),
        new("Data folder", ["File Explorer", "location", "path", "open in File Explorer"], UsageTab),
        new("Compression bitrate", ["kbps", "quality", "compress"], AudioTab),
        new("Keep uncompressed audio", ["CAF", "original", "lossless"], AudioTab),
        new("Playback", ["stereo", "mono", "left", "right", "ear", "headphones", "channels"], AudioTab),
    ];

    private readonly AppViewModel _model;
    private readonly SettingsViewModel _settings;

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    /// <summary>A usage measure is running; <see cref="_measureAgain"/> asks for one more after it.</summary>
    private bool _measuring;

    private bool _measureAgain;

    public StorageSettingsPage(AppViewModel model)
    {
        _model = model;
        _settings = model.SettingsModel;
        InitializeComponent();
        DataFolderText.Text = _model.ConversationsFolder;
        PlaybackCard.Description = Loc.T("How the transcript plays your microphone and the meeting's sound.");
        foreach (var kbps in MediaFoundationAudio.BitratesKbps) BitrateCombo.Items.Add(Loc.Format("%lld kbps", kbps));

        OpenFolderButton.Click += (_, _) => _ = OpenFolderAsync();
        BitrateCombo.SelectionChanged += (_, _) =>
        {
            var index = BitrateCombo.SelectedIndex;
            if (!_syncing && index >= 0) _settings.CompressionBitrateKbps = MediaFoundationAudio.BitratesKbps[index];
        };
        KeepUncompressedToggle.Toggled += (_, _) =>
        {
            if (!_syncing) _settings.KeepUncompressedAudio = KeepUncompressedToggle.IsOn;
        };
        PlaybackCombo.SelectionChanged += (_, _) =>
        {
            if (!_syncing && PlaybackCombo.SelectedIndex >= 0) _settings.PlaybackChannels = (PlaybackChannels)PlaybackCombo.SelectedIndex;
        };

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _settings.PropertyChanged += OnSettingsChanged;
            Refresh();
            ShowTab();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _settings.PropertyChanged -= OnSettingsChanged;
        };
    }

    private string? Tab => _model.SubTabOf(SettingsSearch.Storage);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.SettingsSubTab):
                ShowTab();
                break;
            case nameof(AppViewModel.Conversations):
                if (Tab == UsageTab) _ = MeasureAsync();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    /// <summary>The picked tab's panel; Usage is measured again each time it shows.</summary>
    private void ShowTab()
    {
        var tab = Tab;
        UsagePanel.Visibility = tab == UsageTab ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = tab == AudioTab ? Visibility.Visible : Visibility.Collapsed;
        if (tab == UsageTab) _ = MeasureAsync();
    }

    /// <summary>Every setting control shows the current settings.</summary>
    private void Refresh()
    {
        _syncing = true;
        try
        {
            // A stored rate the encoder does not offer (the shared default 64) shows as the one used (macOS lists it as is).
            var bitrate = MediaFoundationAudio.SnapKbps(_settings.CompressionBitrateKbps);
            BitrateCombo.SelectedIndex = MediaFoundationAudio.BitratesKbps.ToList().IndexOf(bitrate);
            KeepUncompressedToggle.IsOn = _settings.KeepUncompressedAudio;
            PlaybackCombo.SelectedIndex = (int)_settings.PlaybackChannels;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Measures the library and the models off the UI thread; a change during a measure measures once more after it.</summary>
    private async Task MeasureAsync()
    {
        if (_measuring)
        {
            _measureAgain = true;
            return;
        }
        _measuring = true;
        try
        {
            do
            {
                _measureAgain = false;
                LibraryStore.Usage usage;
                try
                {
                    usage = await _model.StorageUsageAsync();
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"EchoDiar: could not measure the storage: {e}");
                    return;
                }
                if (_model.IsClosing) return;
                ShowUsage(usage);
            }
            while (_measureAgain && IsLoaded);
        }
        finally
        {
            _measuring = false;
        }
    }

    /// <summary>The bar (each kind its share of the total) and the sizes.</summary>
    private void ShowUsage(LibraryStore.Usage usage)
    {
        UsageRing.IsActive = false;
        UsageRing.Visibility = Visibility.Collapsed;
        UsageBar.Visibility = Visibility.Visible;
        var total = usage.Total;
        AutomationProperties.SetName(UsageBar, Loc.Format("Storage used: %@", ConversationCard.FileSize(total)));
        (ColumnDefinition Column, Border Part, long Bytes)[] parts =
        [
            (AudioColumn, AudioPart, usage.Audio),
            (TranscriptsColumn, TranscriptsPart, usage.TranscriptsAndAI),
            (ModelsColumn, ModelsPart, usage.Models),
        ];
        var shown = parts.Where(p => p.Bytes > 0).Select(p => p.Part).ToList();
        foreach (var (column, part, bytes) in parts)
        {
            column.Width = bytes > 0 ? new GridLength(bytes, GridUnitType.Star) : new GridLength(0);
            // The track's rounded ends: the first and the last part shown round their outer corners.
            var left = ReferenceEquals(part, shown.FirstOrDefault()) ? 5 : 0;
            var right = ReferenceEquals(part, shown.LastOrDefault()) ? 5 : 0;
            part.CornerRadius = new CornerRadius(left, right, right, left);
        }
        FreeColumn.Width = total > 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        AudioSizeText.Text = ConversationCard.FileSize(usage.Audio);
        TranscriptsSizeText.Text = ConversationCard.FileSize(usage.TranscriptsAndAI);
        ModelsSizeText.Text = ConversationCard.FileSize(usage.Models);
        TotalSizeText.Text = ConversationCard.FileSize(total);
    }

    /// <summary>The folder in File Explorer, created first: it exists only after the first recording (macOS).</summary>
    private async Task OpenFolderAsync()
    {
        var folder = _model.ConversationsFolder;
        try
        {
            Directory.CreateDirectory(folder);
            await Launcher.LaunchFolderPathAsync(folder);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not open {folder}: {e}");
        }
    }
}
