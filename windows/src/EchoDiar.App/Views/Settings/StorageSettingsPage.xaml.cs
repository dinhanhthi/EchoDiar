using System.ComponentModel;
using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › Storage (macOS StorageSettingsView.swift), the Usage, Audio and Auto-delete inner tabs; the title bar
/// picks the tab (<see cref="AppViewModel.SubTabOf"/>). Usage is measured off the UI thread each time the Usage or Audio
/// tab shows and after every library or model change while it shows; the auto-delete preview likewise on its tab. Every
/// setting shows <see cref="SettingsViewModel"/> and changes it; the change is saved after its delay. Deletions and the
/// auto-delete changes that delete more ask first, through the window's dialogs (<see cref="AppViewModel.Confirm"/>).
/// </summary>
public sealed partial class StorageSettingsPage : UserControl
{
    public const string UsageTab = "usage";
    public const string AudioTab = "audio";
    public const string AutoDeleteTab = "autoDelete";

    /// <summary>macOS <c>SettingsTab.storage.subTabs</c>.</summary>
    public static IReadOnlyList<SettingsSubTab> SubTabs { get; } =
        [new(UsageTab, "Usage"), new(AudioTab, "Audio"), new(AutoDeleteTab, "Auto-delete")];

    /// <summary>The searchable rows (macOS SettingsSearch.swift, Storage), each the Tag of its card.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Total", ["disk space", "usage", "size", "storage used"], UsageTab),
        new("Largest conversations", ["biggest", "size"], UsageTab),
        new("Data folder", ["File Explorer", "location", "path", "open in File Explorer"], UsageTab),
        new("Free space", ["delete audio", "clean up", "transcribed"], AudioTab),
        new("Delete audio after transcription", ["remove audio", "Recycle Bin", "trash", "save space"], AudioTab),
        new("Compression bitrate", ["kbps", "quality", "compress"], AudioTab),
        new("Keep uncompressed audio", ["CAF", "original", "lossless"], AudioTab),
        new("Playback", ["stereo", "mono", "left", "right", "ear", "headphones", "channels"], AudioTab),
        new("Auto-delete old conversations", ["retention", "clean up", "Recycle Bin", "trash", "older than", "days"], AutoDeleteTab),
    ];

    /// <summary>macOS <c>largestCount</c>.</summary>
    private const int LargestCount = 5;

    /// <summary>The auto-delete messages list this many titles, then a count.</summary>
    private const int ListedTitles = 10;

    private enum Kind { Audio, TranscriptsAndAI, Models }

    private readonly AppViewModel _model;
    private readonly SettingsViewModel _settings;

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    /// <summary>A usage measure is running; <see cref="_measureAgain"/> asks for one more after it.</summary>
    private bool _measuring;

    private bool _measureAgain;

    /// <summary>The last usage measure: "Free space" deletes the audio this saw.</summary>
    private LibraryStore.Usage? _usage;

    /// <summary>A preview is being selected; <see cref="_previewAgain"/> asks for one more after it.</summary>
    private bool _previewing;

    private bool _previewAgain;

    /// <summary>A deletion by kind or of transcribed audio is under way: their buttons are off (macOS <c>deleting</c>).</summary>
    private bool _deleting;

    /// <summary>An auto-delete age or mode change waits for its preview or its confirmation.</summary>
    private bool _changing;

    /// <summary>The Largest conversations rows' Delete buttons, by conversation.</summary>
    private readonly List<(Button Button, Guid Id)> _largestButtons = [];

    public StorageSettingsPage(AppViewModel model)
    {
        _model = model;
        _settings = model.SettingsModel;
        InitializeComponent();
        DataFolderText.Text = _model.ConversationsFolder;
        PlaybackCard.Description = Loc.T("How the transcript plays your microphone and the meeting's sound.");
        // Not through Loc: the text is not in the catalog, and its slug (cut at 60 characters) finds the catalog's Trash text.
        AutoDeleteCard.Description = "Once a day, moves transcribed conversations older than the age below to the Recycle Bin.";
        foreach (var kbps in MediaFoundationAudio.BitratesKbps) BitrateCombo.Items.Add(Loc.Format("%lld kbps", kbps));
        DaysBox.Minimum = AppSettings.AutoDeleteDaysRange.Min;
        DaysBox.Maximum = AppSettings.AutoDeleteDaysRange.Max;

        SetHelp(AudioDeleteButton, "Delete the audio of every conversation");
        SetHelp(TranscriptsDeleteButton, "Delete every transcript, summary and chat");
        SetHelp(ModelsDeleteButton, "Delete every downloaded model");
        AudioDeleteButton.Click += (_, _) => _ = DeleteAllAsync(Kind.Audio);
        TranscriptsDeleteButton.Click += (_, _) => _ = DeleteAllAsync(Kind.TranscriptsAndAI);
        ModelsDeleteButton.Click += (_, _) => _ = DeleteAllAsync(Kind.Models);
        FreeSpaceButton.Click += (_, _) => _ = DeleteTranscribedAudioAsync();
        OpenFolderButton.Click += (_, _) => _ = OpenFolderAsync();

        DeleteAfterToggle.Toggled += (_, _) =>
        {
            if (!_syncing) _settings.DeleteAudioAfterTranscription = DeleteAfterToggle.IsOn;
        };
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

        AutoDeleteToggle.Toggled += (_, _) =>
        {
            if (!_syncing) _ = AutoDeleteToggledAsync();
        };
        DaysBox.ValueChanged += (sender, args) =>
        {
            if (_syncing) return;
            // A cleared box shows the saved age again.
            if (double.IsNaN(args.NewValue))
            {
                Refresh();
                return;
            }
            var (min, max) = AppSettings.AutoDeleteDaysRange;
            var days = Math.Clamp((int)Math.Round(args.NewValue), min, max);
            _ = ChangeAutoDeleteAsync(s => s with { AutoDeleteAfterDays = days });
        };
        ModeCombo.SelectionChanged += (_, _) =>
        {
            if (_syncing || ModeCombo.SelectedIndex < 0) return;
            var mode = (RetentionMode)ModeCombo.SelectedIndex;
            _ = ChangeAutoDeleteAsync(s => s with { AutoDeleteMode = mode });
        };

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _settings.PropertyChanged += OnSettingsChanged;
            Refresh();
            UpdateDeleteButtons();
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
                UpdateDeleteButtons();
                Update();
                break;
            case nameof(AppViewModel.InstalledModelIds):
                UpdateDeleteButtons();
                if (Tab is UsageTab or AudioTab) _ = MeasureAsync();
                break;
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.IsRecording):
            case nameof(AppViewModel.RecordingCaptions):
            case nameof(AppViewModel.AIJob):
            case nameof(AppViewModel.Downloads):
                UpdateDeleteButtons();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Refresh();
        if (Tab == AutoDeleteTab) _ = PreviewAsync();
    }

    /// <summary>The picked tab's panel, then what it shows measured again.</summary>
    private void ShowTab()
    {
        var tab = Tab;
        UsagePanel.Visibility = tab == UsageTab ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = tab == AudioTab ? Visibility.Visible : Visibility.Collapsed;
        AutoDeletePanel.Visibility = tab == AutoDeleteTab ? Visibility.Visible : Visibility.Collapsed;
        Update();
    }

    /// <summary>The usage (Usage and Audio tabs) or the auto-delete preview (its tab), measured again.</summary>
    private void Update()
    {
        switch (Tab)
        {
            case UsageTab or AudioTab:
                _ = MeasureAsync();
                break;
            case AutoDeleteTab:
                _ = PreviewAsync();
                break;
        }
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
            DeleteAfterToggle.IsOn = _settings.DeleteAudioAfterTranscription;
            AutoDeleteToggle.IsOn = _settings.AutoDeleteEnabled;
            DaysBox.Value = _settings.AutoDeleteAfterDays;
            ModeCombo.SelectedIndex = (int)_settings.AutoDeleteMode;
        }
        finally
        {
            _syncing = false;
        }
    }

    // Usage

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
                _usage = usage;
                ShowUsage(usage);
                ShowLargest(usage);
                UpdateDeleteButtons();
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

    /// <summary>The five largest conversations, each over a bar as long as its share of the largest one (audio, then the rest).</summary>
    private void ShowLargest(LibraryStore.Usage usage)
    {
        var largest = usage.Conversations.Take(LargestCount).ToList();
        LargestExpander.Visibility = largest.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LargestList.Children.Clear();
        _largestButtons.Clear();
        var max = largest.FirstOrDefault()?.Total ?? 0;
        foreach (var conversation in largest) LargestList.Children.Add(LargestRow(conversation, max));
    }

    private Grid LargestRow(LibraryStore.ConversationUsage usage, long max)
    {
        var title = new TextBlock { Text = usage.Title, Style = StyleOf("ListTitleStyle") };
        var size = new TextBlock { Text = ConversationCard.FileSize(usage.Total), Style = StyleOf("SizeStyle") };
        var info = new Grid { RowSpacing = 5, ColumnSpacing = 8 };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bar = ShareBar(usage, max);
        Grid.SetColumn(size, 1);
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 2);
        info.Children.Add(title);
        info.Children.Add(size);
        info.Children.Add(bar);

        var menu = new MenuFlyout();
        menu.Opening += (_, _) => FillDeleteMenu(menu, usage.Id);
        var button = new Button
        {
            Style = StyleOf("TrashButtonStyle"),
            Content = new FontIcon { FontSize = 16, Glyph = "" },
            Flyout = menu,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, Loc.T("Delete the audio or the whole conversation"));
        AutomationProperties.SetName(button, Loc.Format("Delete %@", usage.Title));
        _largestButtons.Add((button, usage.Id));

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(button, 1);
        row.Children.Add(info);
        row.Children.Add(button);
        return row;
    }

    /// <summary>A 4 px bar: the audio and the rest of <paramref name="usage"/>, over a track as long as <paramref name="max"/>.</summary>
    private Grid ShareBar(LibraryStore.ConversationUsage usage, long max)
    {
        var rest = usage.Total - usage.Audio;
        var bar = new Grid { Style = StyleOf("TrackStyle") };
        AutomationProperties.SetAccessibilityView(bar, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        foreach (var bytes in new[] { usage.Audio, rest, Math.Max(0, max - usage.Total) })
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = bytes > 0 ? new GridLength(bytes, GridUnitType.Star) : new GridLength(0),
            });
        }
        // The filled part's rounded ends.
        var audio = new Border { Style = StyleOf("AudioPartStyle"), CornerRadius = new CornerRadius(2, rest > 0 ? 0 : 2, rest > 0 ? 0 : 2, 2) };
        var other = new Border { Style = StyleOf("RestPartStyle"), CornerRadius = new CornerRadius(usage.Audio > 0 ? 0 : 2, 2, 2, usage.Audio > 0 ? 0 : 2) };
        Grid.SetColumn(other, 1);
        bar.Children.Add(audio);
        bar.Children.Add(other);
        return bar;
    }

    /// <summary>
    /// macOS DeleteMenuItems: the audio (or "Audio has been removed") and the whole conversation, through the window's
    /// delete flow, which asks first unless "Delete conversations without confirmation" is on.
    /// </summary>
    private void FillDeleteMenu(MenuFlyout menu, Guid id)
    {
        menu.Items.Clear();
        if (_model.Conversations.FirstOrDefault(c => c.Id == id) is not { } conversation) return;
        var asks = !_model.Settings.DeleteWithoutConfirmation;
        if (conversation.HasAudio)
        {
            var audio = new MenuFlyoutItem { Text = Loc.T(asks ? "Delete audio only…" : "Delete audio only") };
            audio.Click += (_, _) => _ = _model.DeleteFlow?.Invoke([conversation], true);
            menu.Items.Add(audio);
        }
        else
        {
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Audio has been removed"), IsEnabled = false });
        }
        var whole = new MenuFlyoutItem { Text = Loc.T(asks ? "Delete conversation…" : "Delete conversation") };
        whole.Click += (_, _) => _ = _model.DeleteFlow?.Invoke([conversation], false);
        menu.Items.Add(whole);
    }

    // Delete by kind

    /// <summary>Conversations with audio that can lose it now (not recorded, compressed or transcribed).</summary>
    private List<Conversation> AudioCandidates() => [.. _model.Conversations.Where(c => c.HasAudio && _model.CanDelete(c))];

    /// <summary>Conversations with a transcript, summary or chat to delete, or without audio (deleted whole).</summary>
    private List<Conversation> TranscriptCandidates() =>
        [.. _model.Conversations.Where(c => _model.CanDelete(c) && (c.HasTranscript || c.HasSummary || !c.HasAudio))];

    /// <summary>Transcribed conversations whose audio is still on disk, per the last usage measure.</summary>
    private List<Conversation> TranscribedWithAudio()
    {
        var withAudio = (_usage?.Conversations ?? []).Where(u => u.Audio > 0).Select(u => u.Id).ToHashSet();
        return [.. _model.Conversations.Where(c => c.Status == ConversationStatus.Transcribed && c.HasTranscript && withAudio.Contains(c.Id))];
    }

    /// <summary>
    /// Models are removed only while nothing records and no model is in use (<see cref="AppViewModel.ModelsInUse"/>, as
    /// the Delete of Settings › AI's model rows): an AI job's GGUF file cannot be deleted.
    /// </summary>
    private bool CanDeleteModels() =>
        _model.DeletableModels.Count > 0 && !_model.IsRecording && !_model.ModelsInUse;

    private void UpdateDeleteButtons()
    {
        AudioDeleteButton.IsEnabled = !_deleting && AudioCandidates().Count > 0;
        TranscriptsDeleteButton.IsEnabled = !_deleting && TranscriptCandidates().Count > 0;
        ModelsDeleteButton.IsEnabled = !_deleting && CanDeleteModels();
        FreeSpaceButton.IsEnabled = !_deleting && TranscribedWithAudio().Count > 0;
        foreach (var (button, id) in _largestButtons)
        {
            button.IsEnabled = _model.Conversations.FirstOrDefault(c => c.Id == id) is { } c && _model.CanDelete(c);
        }
    }

    /// <summary>
    /// A usage row's Delete: asks with what it removes, then removes it from what qualifies when confirmed (busy ones
    /// are skipped). Texts as macOS (not in its catalog), with the Recycle Bin for the Trash.
    /// </summary>
    private async Task DeleteAllAsync(Kind kind)
    {
        string title;
        string message;
        switch (kind)
        {
            case Kind.Audio:
            {
                var candidates = AudioCandidates();
                if (candidates.Count == 0) return;
                var untranscribed = candidates.Count(c => !c.HasTranscript);
                title = $"Delete the audio of {CountText(candidates.Count)}?";
                message = "The audio files are moved to the Recycle Bin. Transcripts, summaries and chats stay; playback and re-transcribing are no longer possible."
                    + (untranscribed == 0 ? ""
                        : $"\n\n{(untranscribed == 1 ? "1 of them has" : $"{untranscribed} of them have")} no transcript yet: nothing of it will be left to read.");
                break;
            }
            case Kind.TranscriptsAndAI:
            {
                var candidates = TranscriptCandidates();
                if (candidates.Count == 0) return;
                var withoutAudio = candidates.Count(c => !c.HasAudio);
                title = $"Delete the transcripts, summaries and chats of {CountText(candidates.Count)}?";
                message = "They are moved to the Recycle Bin. The audio stays, so the conversations can be transcribed again."
                    + (withoutAudio == 0 ? ""
                        : $"\n\n{CountText(withoutAudio)} without audio would be left empty: {(withoutAudio == 1 ? "it is" : "they are")} moved to the Recycle Bin entirely.");
                break;
            }
            default:
            {
                var count = _model.DeletableModels.Count;
                if (count == 0) return;
                title = count == 1 ? "Delete 1 downloaded model?" : $"Delete {count} downloaded models?";
                message = "The transcription, speaker and AI models are removed from this PC (not moved to the Recycle Bin). Download them again in Settings › Transcription and Settings › AI.";
                break;
            }
        }
        if (_model.Confirm is not { } confirm || !await confirm(title, message, Loc.T("Delete")) || _model.IsClosing) return;
        switch (kind)
        {
            case Kind.Audio:
                await DeletingAsync(() => _model.DeleteAudioAsync([.. AudioCandidates().Select(c => c.Id)]));
                break;
            case Kind.TranscriptsAndAI:
                await DeletingAsync(() => _model.DeleteTranscriptsAndAIAsync([.. TranscriptCandidates().Select(c => c.Id)]));
                break;
            default:
                // A recording or a transcription may have started while the dialog was open.
                if (CanDeleteModels()) await _model.DeleteAllModelsAsync();
                break;
        }
    }

    /// <summary>"Delete audio of transcribed conversations…": asks, then moves their audio to the Recycle Bin.</summary>
    private async Task DeleteTranscribedAudioAsync()
    {
        var count = TranscribedWithAudio().Count;
        if (count == 0 || _model.Confirm is not { } confirm) return;
        var confirmed = await confirm(
            Loc.Format("Delete the audio of %lld transcribed conversation%@?", count, count == 1 ? "" : "s"),
            Loc.T("The audio files are moved to the Recycle Bin. Transcripts, summaries and chats stay."),
            Loc.T("Move to Recycle Bin"));
        if (!confirmed || _model.IsClosing) return;
        await DeletingAsync(() => _model.DeleteAudioAsync([.. TranscribedWithAudio().Select(c => c.Id)]));
    }

    /// <summary>Runs a deletion with the delete buttons off.</summary>
    private async Task DeletingAsync(Func<Task> delete)
    {
        _deleting = true;
        UpdateDeleteButtons();
        try
        {
            await delete();
        }
        finally
        {
            _deleting = false;
            if (!_model.IsClosing) UpdateDeleteButtons();
        }
    }

    // Auto-delete

    /// <summary>Selects what auto-delete would delete now; a change meanwhile selects once more after it.</summary>
    private async Task PreviewAsync()
    {
        if (_previewing)
        {
            _previewAgain = true;
            return;
        }
        _previewing = true;
        try
        {
            do
            {
                _previewAgain = false;
                IReadOnlyList<RetentionPolicy.Deletion> preview;
                try
                {
                    preview = await _model.RetentionPreviewAsync(_model.Settings);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"EchoDiar: could not preview auto-delete: {e}");
                    return;
                }
                if (_model.IsClosing) return;
                ShowPreview(preview);
            }
            while (_previewAgain && IsLoaded);
        }
        finally
        {
            _previewing = false;
        }
    }

    /// <summary>"Nothing matches now.", else the count and the titles ("Next run" when on, "Would delete now" when off).</summary>
    private void ShowPreview(IReadOnlyList<RetentionPolicy.Deletion> preview)
    {
        NothingMatchesText.Visibility = preview.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewExpander.Visibility = preview.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewList.Children.Clear();
        if (preview.Count == 0) return;
        PreviewExpander.Header = _model.Settings.AutoDeleteEnabled
            ? Loc.Format("Next run: %@", CountText(preview.Count))
            : Loc.Format("Would delete now: %@", CountText(preview.Count));
        foreach (var deletion in preview)
        {
            PreviewList.Children.Add(new TextBlock { Text = Title(deletion), Style = StyleOf("PreviewTitleStyle") });
        }
    }

    /// <summary>
    /// Turning on asks first with what it deletes right away; the switch stays off until confirmed. Turning off is
    /// immediate (macOS <c>autoDeleteEnabled</c>).
    /// </summary>
    private async Task AutoDeleteToggledAsync()
    {
        if (!AutoDeleteToggle.IsOn)
        {
            _settings.AutoDeleteEnabled = false;
            return;
        }
        if (_settings.AutoDeleteEnabled) return;
        Refresh();
        var settings = _model.Settings;
        IReadOnlyList<RetentionPolicy.Deletion> preview;
        try
        {
            preview = await _model.RetentionPreviewAsync(settings);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not preview auto-delete: {e}");
            return;
        }
        if (_model.IsClosing || _model.Confirm is not { } confirm) return;
        var confirmed = await confirm(
            Loc.T("Turn on auto-delete?"), TurnOnMessage(settings, preview), Loc.T(preview.Count == 0 ? "Turn On" : "Turn On and Delete"));
        if (confirmed && !_model.IsClosing) _settings.AutoDeleteEnabled = true;
    }

    /// <summary>
    /// Off: saved at once. On: saved at once unless the next run would then delete more than now (another conversation,
    /// or whole folders instead of audio), which asks first with the new list; Cancel shows the saved value again.
    /// </summary>
    private async Task ChangeAutoDeleteAsync(Func<AppSettings, AppSettings> change)
    {
        var current = _model.Settings;
        var updated = change(current);
        if (updated == current) return;
        if (!current.AutoDeleteEnabled)
        {
            _settings.Update(change);
            return;
        }
        // One change at a time: another waits for its confirmation.
        if (_changing)
        {
            Refresh();
            return;
        }
        _changing = true;
        try
        {
            var currentPreview = await _model.RetentionPreviewAsync(current);
            var newPreview = await _model.RetentionPreviewAsync(updated);
            if (_model.IsClosing) return;
            if (!DeletesMore(newPreview, currentPreview))
            {
                _settings.Update(change);
                return;
            }
            var confirmed = _model.Confirm is { } confirm
                && await confirm(Loc.T("Delete more at the next run?"), ChangeMessage(updated, newPreview), Loc.T("Change"));
            if (_model.IsClosing) return;
            if (confirmed)
            {
                _settings.Update(s => s with { AutoDeleteAfterDays = updated.AutoDeleteAfterDays, AutoDeleteMode = updated.AutoDeleteMode });
            }
            else
            {
                Refresh();
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not preview auto-delete: {e}");
            if (!_model.IsClosing) Refresh();
        }
        finally
        {
            _changing = false;
        }
    }

    /// <summary><paramref name="next"/> has a conversation <paramref name="now"/> has not, or deletes the whole folder of one whose audio only it deletes.</summary>
    private static bool DeletesMore(IReadOnlyList<RetentionPolicy.Deletion> next, IReadOnlyList<RetentionPolicy.Deletion> now)
    {
        var modes = new Dictionary<Guid, RetentionMode>();
        foreach (var deletion in now) modes.TryAdd(deletion.ConversationId, deletion.Mode);
        return next.Any(deletion => !modes.TryGetValue(deletion.ConversationId, out var mode)
            || (mode == RetentionMode.AudioOnly && deletion.Mode == RetentionMode.WholeConversation));
    }

    // The auto-delete messages: as macOS (not in its catalog), with the Recycle Bin for the Trash.

    private string TurnOnMessage(AppSettings settings, IReadOnlyList<RetentionPolicy.Deletion> preview)
    {
        var what = settings.AutoDeleteMode == RetentionMode.AudioOnly ? "The audio of" : "All files of";
        return preview.Count == 0
            ? $"{what} transcribed conversations older than {settings.AutoDeleteAfterDays} days will be moved to the Recycle Bin once a day. Nothing matches now."
            : $"{what} {CountText(preview.Count)} will be moved to the Recycle Bin now, then daily for older ones:\n\n{TitleList(preview)}";
    }

    private string ChangeMessage(AppSettings settings, IReadOnlyList<RetentionPolicy.Deletion> preview)
    {
        var what = settings.AutoDeleteMode == RetentionMode.AudioOnly ? "The audio of" : "All files of";
        return $"{what} {CountText(preview.Count)} will be moved to the Recycle Bin at the next run (at launch or within a day):\n\n{TitleList(preview)}";
    }

    private string TitleList(IReadOnlyList<RetentionPolicy.Deletion> deletions)
    {
        var titles = string.Join("\n", deletions.Take(ListedTitles).Select(d => $"• {Title(d)}"));
        return deletions.Count > ListedTitles ? $"{titles}\n…and {deletions.Count - ListedTitles} more" : titles;
    }

    private static string CountText(int count) => count == 1 ? "1 conversation" : $"{count} conversations";

    private string Title(RetentionPolicy.Deletion deletion) =>
        _model.Conversations.FirstOrDefault(c => c.Id == deletion.ConversationId)?.Title
        ?? (deletion.Items.Count > 0 ? Path.GetFileName(deletion.Items[0]) : "");

    // Helpers

    private Style StyleOf(string key) => (Style)Resources[key];

    /// <summary>A tooltip and the same accessible name (macOS <c>help</c> and <c>accessibilityLabel</c>).</summary>
    private static void SetHelp(Button button, string text)
    {
        ToolTipService.SetToolTip(button, text);
        AutomationProperties.SetName(button, text);
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
