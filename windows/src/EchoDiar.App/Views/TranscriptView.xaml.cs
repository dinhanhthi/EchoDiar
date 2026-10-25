using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Transcribe;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace EchoDiar.App.Views;

/// <summary>
/// The Transcript tab of the conversation page (macOS TranscriptView.swift) for a <see cref="TranscriptViewModel"/> and
/// the page's <see cref="PlaybackController"/>: the segments (a click plays from one; the one under the play head is
/// selected, and the list follows it while playing until the user scrolls), the version menu, the transcription's
/// state, the speaker notes, playback, the speaker chips with their rename field, Copy and Re-transcribe.
/// </summary>
public sealed partial class TranscriptView : UserControl
{
    /// <summary>Up to three speaker chips; "+N" lists the others.</summary>
    private const int MaxSpeakerChips = 3;

    private AppViewModel? _model;
    private TranscriptViewModel? _transcript;
    private PlaybackController? _playback;
    private ObservableCollection<TranscriptLineItem> _lines = [];
    private ScrollViewer? _scroller;

    /// <summary>
    /// The list follows the play head: set by Play and by a click on a segment, cleared when the user scrolls
    /// (macOS SegmentList <c>following</c>).
    /// </summary>
    private bool _following;

    /// <summary>The segment shown as playing (its speaker icon), and whether playback was on at the last update.</summary>
    private TranscriptLineItem? _speakingLine;
    private bool _wasPlaying;

    /// <summary>The speaker whose rename field is open.</summary>
    private SpeakerChip? _renaming;

    /// <summary>What the chips were last built from: they are rebuilt only when it changes, not on every job update.</summary>
    private IReadOnlyList<SpeakerChip>? _chipSpeakers;
    private bool _chipsEnabled;

    private string? _retranscribeTip;
    private string? _stepTip;
    private string? _failureTip;
    private string? _retryTip;
    private string? _transcribeTip;

    public TranscriptView()
    {
        InitializeComponent();
        LineList.ItemsSource = _lines;
        LineList.ItemClick += OnLineClick;
        LineList.ContainerContentChanging += OnContainerContentChanging;
        LineList.Loaded += (_, _) => FollowUserScrolling();
        // Loaded while collapsed (the page's loading pane) comes before the template: its first layout finds the scroller.
        LineList.SizeChanged += (_, _) => FollowUserScrolling();
        VersionMenu.Opening += (_, _) => BuildVersionMenu();
        RetranscribeMenu.Opening += (_, _) => BuildRetranscribeMenu();
        RetryMenu.Opening += (_, _) => BuildRetryMenu();
        RetryButton.Click += (_, _) => Transcribe(null);
        TranscribeButton.Click += (_, _) => Transcribe(null);
        CopyButton.Click += (_, _) => Copy();
        RenameBox.KeyDown += OnRenameKeyDown;
        RenameFlyout.Opened += (_, _) =>
        {
            RenameBox.Focus(FocusState.Programmatic);
            RenameBox.SelectAll();
        };
        RenameFlyout.Closed += (_, _) => _renaming = null;
    }

    /// <summary>Shows <paramref name="transcript"/> and plays with <paramref name="playback"/>; call when the page shows.</summary>
    public void Attach(AppViewModel model, TranscriptViewModel transcript, PlaybackController playback)
    {
        Detach();
        _model = model;
        _transcript = transcript;
        _playback = playback;
        model.PropertyChanged += OnModelChanged;
        transcript.PropertyChanged += OnTranscriptChanged;
        playback.PropertyChanged += OnPlaybackChanged;
        Player.Attach(model, playback);
        UpdateLines();
        _chipSpeakers = null;
        UpdateChips();
        Update();
    }

    /// <summary>
    /// A clicked AI citation (macOS <c>scrollTarget</c>): scrolls to the segment <paramref name="id"/>, once the list is laid
    /// out (its tab has just shown).
    /// </summary>
    internal void ShowSegment(Guid id) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (_model is { IsClosing: false } && _lines.FirstOrDefault(l => l.Id == id) is { } line)
        {
            LineList.ScrollIntoView(line, ScrollIntoViewAlignment.Leading);
        }
    });

    /// <summary>Stops following the models; call when the page goes.</summary>
    public void Detach()
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        if (_transcript is not null) _transcript.PropertyChanged -= OnTranscriptChanged;
        if (_playback is not null) _playback.PropertyChanged -= OnPlaybackChanged;
        Player.Detach();
        RenameFlyout.Hide();
        MoreSpeakersFlyout.Hide();
        _model = null;
        _transcript = null;
        _playback = null;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.TranscriptionErrors):
            case nameof(AppViewModel.InstalledModelIds):
                Update();
                break;
        }
    }

    private void OnTranscriptChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TranscriptViewModel.Lines):
                UpdateLines();
                break;
            case nameof(TranscriptViewModel.Speakers):
            case nameof(TranscriptViewModel.CanRename):
                UpdateChips();
                break;
            default:
                Update();
                break;
        }
    }

    /// <summary>Not once the window closed: its controls fail every call.</summary>
    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_model is not { IsClosing: false }) return;
        switch (e.PropertyName)
        {
            case nameof(PlaybackController.ActiveSegmentId):
            case nameof(PlaybackController.IsPlaying):
                UpdateActive();
                break;
        }
    }

    // Segments

    /// <summary>
    /// The shown version's lines. The same segments (a speaker renamed) are replaced in place, so the list keeps its
    /// scroll position; other segments (another version, a new transcript) start from the top.
    /// </summary>
    private void UpdateLines()
    {
        if (_transcript is null) return;
        var lines = _transcript.Lines;
        if (lines.Count == _lines.Count && lines.Count > 0 && lines.Select(l => l.Id).SequenceEqual(_lines.Select(l => l.Id)))
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (!Equals(_lines[i], lines[i])) _lines[i] = lines[i];
            }
        }
        else
        {
            _lines = [.. lines];
            LineList.ItemsSource = _lines;
        }
        _speakingLine = null;
        CopyButton.IsEnabled = lines.Count > 0;
        UpdateContent();
        UpdateActive();
    }

    /// <summary>
    /// Selects the segment under the play head; the list scrolls to it while following, only once it is out of
    /// view. Play starts following (macOS SegmentList).
    /// </summary>
    private void UpdateActive()
    {
        if (_playback is not { } playback) return;
        var id = playback.ActiveSegmentId;
        var active = id is null ? null : _lines.FirstOrDefault(l => l.Id == id);
        if (!Equals(LineList.SelectedItem, active)) LineList.SelectedItem = active;

        var speaking = playback.IsPlaying ? active : null;
        if (!Equals(speaking, _speakingLine))
        {
            if (_speakingLine is not null) SetSpeaking(_speakingLine, false);
            if (speaking is not null) SetSpeaking(speaking, true);
            _speakingLine = speaking;
        }

        if (playback.IsPlaying && !_wasPlaying) _following = true;
        _wasPlaying = playback.IsPlaying;
        if (_following && playback.IsPlaying && active is not null) ScrollIntoViewIfOutside(active);
    }

    private void ScrollIntoViewIfOutside(TranscriptLineItem line)
    {
        if (LineList.ContainerFromItem(line) is FrameworkElement container && _scroller is not null && container.ActualHeight > 0)
        {
            var bounds = container.TransformToVisual(_scroller).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Top >= 0 && bounds.Bottom <= _scroller.ViewportHeight) return;
        }
        LineList.ScrollIntoView(line, ScrollIntoViewAlignment.Leading);
    }

    /// <summary>The user's own scrolling (wheel, touch, scroll bar, keys) stops following the play head.</summary>
    private void FollowUserScrolling()
    {
        if (_scroller is not null) return;
        _scroller = FindChild<ScrollViewer>(LineList);
        if (_scroller is null) return;
        _scroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => _following = false), handledEventsToo: true);
        _scroller.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _following = false), handledEventsToo: true);
        _scroller.AddHandler(KeyDownEvent, new KeyEventHandler(OnListKeyDown), handledEventsToo: true);
        _scroller.DirectManipulationStarted += (_, _) => _following = false;
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
        {
            _following = false;
        }
    }

    /// <summary>Plays from the segment's start and follows it; without audio the selection stays on the play head.</summary>
    private void OnLineClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TranscriptLineItem line && _playback is { IsReady: true } playback)
        {
            _following = true;
            playback.SeekTo(line.Segment);
        }
        // The click also selected the row; the selection shows the play head only.
        DispatcherQueue.TryEnqueue(UpdateActive);
    }

    /// <summary>A row is shown for an item: its speaker's color and whether it plays.</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not TranscriptLineItem line) return;
        if (args.ItemContainer.ContentTemplateRoot is not FrameworkElement root) return;
        var name = root.FindName("NameText") as TextBlock;
        if (name is not null) name.Style = SpeakerStyle(line.ColorIndex);
        if (root.FindName("SpeakingIcon") is FontIcon icon) ShowSpeaking(icon, name, Equals(line, _speakingLine));
    }

    private void SetSpeaking(TranscriptLineItem line, bool speaking)
    {
        if (LineList.ContainerFromItem(line) is not ListViewItem { ContentTemplateRoot: FrameworkElement root }) return;
        if (root.FindName("SpeakingIcon") is FontIcon icon) ShowSpeaking(icon, root.FindName("NameText") as TextBlock, speaking);
    }

    /// <summary>The speaker icon under the start, in the speaker's color, while the segment plays.</summary>
    private static void ShowSpeaking(FontIcon icon, TextBlock? name, bool speaking)
    {
        icon.Visibility = speaking ? Visibility.Visible : Visibility.Collapsed;
        if (speaking && name is not null) icon.Foreground = name.Foreground;
    }

    /// <summary>The speaker's color as a caption style (SpeakerMe, Speaker0…7 in the resources).</summary>
    private Style SpeakerStyle(int colorIndex) => (Style)Resources[colorIndex < 0 ? "SpeakerMe" : $"Speaker{colorIndex}"];

    // State

    /// <summary>Everything but the lines and chips: the version menu, the empty note, the status, notes and actions.</summary>
    private void Update()
    {
        if (_transcript is null) return;
        VersionButton.Visibility = Shown(_transcript.ShowsVersionMenu);
        VersionText.Text = _transcript.VersionLabel;
        AutomationProperties.SetName(VersionButton, _transcript.VersionLabel);
        // Older versions are view-only: a rename edits the newest.
        if (!_transcript.CanRename)
        {
            RenameFlyout.Hide();
            MoreSpeakersFlyout.Hide();
        }
        UpdateContent();
        UpdateStatus();
        UpdateNotice();
        UpdateActions();
    }

    /// <summary>The lines, a ring while reading, or why there are none.</summary>
    private void UpdateContent()
    {
        if (_transcript is null) return;
        var hasLines = _lines.Count > 0;
        var loading = !hasLines && _transcript.IsLoading;
        LineList.Visibility = Shown(hasLines);
        LoadingRing.Visibility = Shown(loading);
        LoadingRing.IsActive = loading;
        EmptyPanel.Visibility = Shown(!hasLines && !loading);
        EmptyText.Text = _transcript.EmptyText;
    }

    /// <summary>
    /// macOS TranscriptionStatus and TranscriptionFailureReason: in the empty card without a transcript; in the bar
    /// with one, where a running job shows on Re-transcribe instead.
    /// </summary>
    private void UpdateStatus()
    {
        if (_model is null || _transcript?.Conversation is not { } c)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            BarStatusHost.Visibility = Visibility.Collapsed;
            return;
        }
        _model.TranscriptionProgress.TryGetValue(c.Id, out var progress);
        var running = progress is not null && progress.Step != JobStep.NeedsModel;
        var failed = progress is null && c.Status == ConversationStatus.TranscriptionFailed;
        var transcribe = progress is null && !failed && !c.HasTranscript
            && c.Status is ConversationStatus.Ready or ConversationStatus.Transcribing;
        var showsProgress = running && !c.HasTranscript;

        ProgressPanel.Visibility = Shown(showsProgress);
        NeedsModelBadge.Visibility = Shown(progress is not null && !running);
        FailureText.Visibility = Shown(failed);
        RetryButton.Visibility = Shown(failed);
        TranscribeButton.Visibility = Shown(transcribe);
        StatusPanel.Visibility = Shown(showsProgress || (progress is not null && !running) || failed || transcribe);

        if (showsProgress)
        {
            var queued = progress!.Step is JobStep.Queued or JobStep.Deferred;
            JobRing.IsIndeterminate = queued;
            JobRing.Value = queued ? 0 : progress.Fraction;
            StepText.Text = ConversationCard.StepTitle(progress.Step);
            SetTip(ProgressPanel, progress.Fraction.ToString("P0", CultureInfo.CurrentCulture), ref _stepTip);
        }
        if (failed)
        {
            var reason = _model.TranscriptionErrors.GetValueOrDefault(c.Id) ?? Loc.T("Transcription failed");
            FailureText.Text = reason;
            SetTip(FailureText, reason, ref _failureTip);
            RetryButton.IsEnabled = c.HasAudio;
            SetTip(RetryButton, Loc.T(c.HasAudio ? "Transcription failed — transcribe this conversation again" : "Audio has been removed"), ref _retryTip);
        }
        if (transcribe)
        {
            TranscribeButton.IsEnabled = c.HasAudio;
            SetTip(TranscribeButton, Loc.T(c.HasAudio ? "Transcribe this conversation" : "Audio has been removed"), ref _transcribeTip);
        }

        // Stacked under the empty note, in a row in the bar.
        var host = c.HasTranscript ? BarStatusHost : EmptyStatusHost;
        if (host.Child != StatusPanel)
        {
            BarStatusHost.Child = null;
            EmptyStatusHost.Child = null;
            host.Child = StatusPanel;
        }
        StatusPanel.Orientation = c.HasTranscript ? Orientation.Horizontal : Orientation.Vertical;
        StatusPanel.HorizontalAlignment = c.HasTranscript ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        FailureText.TextAlignment = c.HasTranscript ? TextAlignment.Left : TextAlignment.Center;
        BarStatusHost.Visibility = Shown(c.HasTranscript && StatusPanel.Visibility == Visibility.Visible);
    }

    /// <summary>The shown transcript was made without telling the speakers apart (macOS diarization notices).</summary>
    private void UpdateNotice()
    {
        var notes = _transcript?.ShownTranscript?.Notes ?? [];
        var localMissing = notes.Contains(Transcript.DiarizationMissingNote);
        string? text = null;
        var download = false;
        if (notes.Contains(Transcript.CloudDiarizationFailedNote))
        {
            NoticeIcon.Glyph = "";
            text = Loc.T(localMissing
                ? "Cloud diarization failed and no diarization model is installed on this Mac."
                : "Cloud diarization failed; speakers were told apart on this Mac.");
            download = localMissing;
        }
        else if (localMissing && _model is not null)
        {
            NoticeIcon.Glyph = "";
            var installed = ModelCatalog.Diarization.Any(e => _model.InstalledModelIds.Contains(e.Id));
            text = Loc.T(installed
                ? "Speakers were not told apart. The speaker model is installed now: re-transcribe."
                : "Speakers were not told apart: the speaker model is not installed (Transcription › Diarization model).");
            download = !installed;
        }
        NoticePanel.Visibility = Shown(text is not null);
        NoticeText.Text = text ?? "";
        NoticeDownloadButton.Visibility = Shown(download);
    }

    /// <summary>Copy, Re-transcribe (or "Transcribing…" while a job runs), playback or the removed-audio note.</summary>
    private void UpdateActions()
    {
        if (_transcript is null) return;
        var hasAudio = _transcript.Conversation?.HasAudio ?? false;
        var transcribing = _transcript.IsTranscribing;
        TranscribingPanel.Visibility = Shown(transcribing);
        RetranscribeButton.Visibility = Shown(!transcribing);
        RetranscribeButton.IsEnabled = _transcript.CanRetranscribe;
        SetTip(RetranscribeButton, Loc.T(hasAudio ? "Re-transcribe with another model or language" : "Audio has been removed"), ref _retranscribeTip);
        Player.Visibility = Shown(hasAudio);
        AudioRemovedNote.Visibility = Shown(!hasAudio && _transcript.Conversation is not null);
    }

    // Menus

    /// <summary>The kept transcripts, newest first, each with its engine and date; the shown one is checked.</summary>
    private void BuildVersionMenu()
    {
        VersionMenu.Items.Clear();
        if (_transcript is null) return;
        foreach (var version in _transcript.Versions)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = version.Details.Length > 0 ? $"{version.Title} · {version.Details}" : version.Title,
                GroupName = "TranscriptVersion",
                IsChecked = version.IsShown,
            };
            var number = version.Number;
            item.Click += (_, _) => _transcript?.ShowVersion(number);
            VersionMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// macOS EngineMenuItems under "Transcribe again with" (cloud engines come with Phase 26), then the language of
    /// the next run with "As recorded" first; "Download a model…" (Settings, a later phase) when no engine is installed.
    /// </summary>
    private void BuildRetranscribeMenu()
    {
        RetranscribeMenu.Items.Clear();
        if (_transcript is not { } transcript || _model is not { } model) return;
        if (!AddEngineItems(RetranscribeMenu, "Transcribe again with", id => _ = transcript.RetranscribeAsync(id))) return;
        RetranscribeMenu.Items.Add(new MenuFlyoutSeparator());
        var language = new MenuFlyoutSubItem { Text = Loc.T("Language") };
        var selection = transcript.RetranscribeLanguage;
        language.Items.Add(LanguageItem(Loc.T("As recorded"), "", selection));
        foreach (var group in LanguageOptions.Groups(selection, model.Settings.RecentLanguages, model.SupportedLanguages()))
        {
            language.Items.Add(new MenuFlyoutSeparator());
            foreach (var code in group) language.Items.Add(LanguageItem(LanguageOptions.Name(code), code, selection));
        }
        RetranscribeMenu.Items.Add(language);
    }

    private RadioMenuFlyoutItem LanguageItem(string text, string code, string selection)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "RetranscribeLanguage", IsChecked = code == selection };
        item.Click += (_, _) =>
        {
            if (_transcript is not null) _transcript.RetranscribeLanguage = code;
        };
        return item;
    }

    private void BuildRetryMenu()
    {
        RetryMenu.Items.Clear();
        AddEngineItems(RetryMenu, "Retry with another engine", id => Transcribe(id));
    }

    /// <summary>The installed local engines under <paramref name="title"/>; false (and "Download a model…") without any.</summary>
    private bool AddEngineItems(MenuFlyout menu, string title, Action<string> pick)
    {
        var engines = _model?.InstalledBatchEngines ?? [];
        if (engines.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Download a model…"), IsEnabled = false });
            return false;
        }
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T(title), IsEnabled = false });
        foreach (var engine in engines)
        {
            var item = new MenuFlyoutItem { Text = engine.DisplayName };
            var id = engine.Id;
            item.Click += (_, _) => pick(id);
            menu.Items.Add(item);
        }
        return true;
    }

    /// <summary>Transcribe and Retry: with the default engine, or <paramref name="engineId"/>.</summary>
    private void Transcribe(string? engineId)
    {
        if (_model is not null && _transcript is not null) _ = _model.TranscribeAsync(_transcript.ConversationId, engineId);
    }

    /// <summary>One "Name [mm:ss]: text" line per segment of the shown version.</summary>
    private void Copy()
    {
        if (_transcript is null) return;
        var package = new DataPackage();
        package.SetText(_transcript.TranscriptText());
        try
        {
            Clipboard.SetContent(package);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"EchoDiar: could not copy the transcript: {e}");
        }
    }

    // Speakers

    /// <summary>Up to three chips, then "+N" for the others; disabled while a rename could not be saved.</summary>
    private void UpdateChips()
    {
        if (_transcript is null) return;
        var speakers = _transcript.Speakers;
        var enabled = _transcript.CanRename;
        if (ReferenceEquals(speakers, _chipSpeakers) && enabled == _chipsEnabled) return;
        _chipSpeakers = speakers;
        _chipsEnabled = enabled;
        ChipsPanel.Children.Clear();
        foreach (var chip in speakers.Take(MaxSpeakerChips))
        {
            var button = new Button { Content = SpeakerLabel(chip), IsEnabled = enabled };
            AutomationProperties.SetName(button, chip.Name);
            ToolTipService.SetToolTip(button, Loc.T("Rename this speaker"));
            button.Click += (_, _) => OpenRename(chip, button);
            ChipsPanel.Children.Add(button);
        }
        if (speakers.Count > MaxSpeakerChips)
        {
            var hidden = speakers.Skip(MaxSpeakerChips).ToList();
            var more = new Button { Content = $"+{hidden.Count}", IsEnabled = enabled };
            AutomationProperties.SetName(more, Loc.T("Show the other speakers"));
            ToolTipService.SetToolTip(more, Loc.T("Show the other speakers"));
            more.Click += (_, _) => OpenMoreSpeakers(hidden, more);
            ChipsPanel.Children.Add(more);
        }
        if (!enabled)
        {
            RenameFlyout.Hide();
            MoreSpeakersFlyout.Hide();
        }
    }

    /// <summary>A dot in the speaker's color, then the name.</summary>
    private StackPanel SpeakerLabel(SpeakerChip chip)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        panel.Children.Add(new TextBlock { Text = "●", Style = SpeakerStyle(chip.ColorIndex), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = chip.Name, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void OpenMoreSpeakers(IReadOnlyList<SpeakerChip> hidden, Button target)
    {
        MoreSpeakersList.Children.Clear();
        foreach (var chip in hidden)
        {
            var button = new Button
            {
                Content = SpeakerLabel(chip),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            };
            AutomationProperties.SetName(button, chip.Name);
            ToolTipService.SetToolTip(button, Loc.T("Rename this speaker"));
            button.Click += (_, _) =>
            {
                MoreSpeakersFlyout.Hide();
                OpenRename(chip, target);
            };
            MoreSpeakersList.Children.Add(button);
        }
        MoreSpeakersFlyout.ShowAt(target);
    }

    /// <summary>The speaker's name field ("Name for Speaker 1"), starting with the name the user gave.</summary>
    private void OpenRename(SpeakerChip chip, FrameworkElement target)
    {
        if (_transcript is not { CanRename: true }) return;
        _renaming = chip;
        var title = Loc.Format("Name for %@", chip.DefaultName);
        RenameTitle.Text = title;
        RenameBox.PlaceholderText = chip.DefaultName;
        RenameBox.Text = chip.CustomName;
        AutomationProperties.SetName(RenameBox, title);
        RenameFlyout.ShowAt(target);
    }

    /// <summary>Enter saves (a blank name restores the default); Esc closes the flyout, which cancels.</summary>
    private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _renaming is not { } chip || _transcript is not { } transcript) return;
        e.Handled = true;
        var name = RenameBox.Text;
        RenameFlyout.Hide();
        _ = transcript.RenameSpeakerAsync(chip.Speaker, name);
    }

    // Helpers

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static void SetTip(DependencyObject element, string text, ref string? shown)
    {
        if (text == shown) return;
        shown = text;
        ToolTipService.SetToolTip(element, text);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
