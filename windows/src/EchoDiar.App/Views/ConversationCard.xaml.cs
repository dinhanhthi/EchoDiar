using System.Globalization;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Model;
using EchoDiar.Transcribe;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.ViewManagement;

namespace EchoDiar.App.Views;

/// <summary>
/// One conversation of the library (macOS ConversationCard.swift, with its DeleteMenuItems, TranscriptionStatus,
/// TranscriptionFailureReason and StatusBadge). A click opens it; Ctrl-click and Shift-click select it instead
/// (<see cref="ConversationListView"/> keeps the selection). The list sets its state with <see cref="Update"/>: cards
/// do not follow the model themselves, so a card that left the list holds no subscription.
/// </summary>
public sealed partial class ConversationCard : UserControl
{
    private static readonly UISettings UISettings = new();

    private readonly AppViewModel _model;
    private readonly ConversationListView _list;

    /// <summary>A click on the title waits out the double click, which renames instead of opening.</summary>
    private readonly DispatcherQueueTimer _openTimer;

    private bool _editing;

    /// <summary>Rename was picked in the context menu: the field opens once the menu has closed and given focus back.</summary>
    private bool _renameAfterMenu;

    /// <summary>The tooltips as last set: one set again while shown closes under the pointer (Update runs at each job percent).</summary>
    private string? _titleTip;
    private string? _failureTip;
    private string? _stepTip;
    private string? _retryTip;
    private string? _transcribeTip;

    internal ConversationCard(AppViewModel model, ConversationListView list, Conversation conversation)
    {
        _model = model;
        _list = list;
        Conversation = conversation;
        InitializeComponent();
        _openTimer = DispatcherQueue.CreateTimer();
        _openTimer.Interval = TimeSpan.FromMilliseconds(UISettings.DoubleClickTime);
        _openTimer.IsRepeating = false;
        _openTimer.Tick += (t, _) =>
        {
            t.Stop();
            Open();
        };

        var reveal = Loc.T("Reveal in Explorer");
        RevealItem.Text = reveal;
        AutomationProperties.SetName(RevealButton, reveal);
        ToolTipService.SetToolTip(RevealButton, reveal);
        RevealItem.Click += (_, _) => AppViewModel.RevealInExplorer(Conversation);
        RevealButton.Click += (_, _) => AppViewModel.RevealInExplorer(Conversation);
        RenameItem.Click += (_, _) => _renameAfterMenu = true;
        CardMenu.Opening += (_, _) => UpdateDeleteItems(MenuDeleteAudioItem, MenuDeleteConversationItem, menu: true);
        CardMenu.Closed += (_, _) =>
        {
            if (!_renameAfterMenu) return;
            _renameAfterMenu = false;
            BeginEditing();
        };
        DeleteMenu.Opening += (_, _) => UpdateDeleteItems(DeleteAudioItem, DeleteConversationItem, menu: false);
        DeleteAudioItem.Click += (_, _) => _ = _list.DeleteAsync([Conversation], audioOnly: true);
        DeleteConversationItem.Click += (_, _) => _ = _list.DeleteAsync([Conversation], audioOnly: false);
        MenuDeleteAudioItem.Click += (_, _) => _ = _list.DeleteAsync([Conversation], audioOnly: true);
        MenuDeleteConversationItem.Click += (_, _) => _ = _list.DeleteAsync([Conversation], audioOnly: false);
        RetryButton.Click += (_, _) => _ = _model.TranscribeAsync(Conversation.Id);
        TranscribeButton.Click += (_, _) => _ = _model.TranscribeAsync(Conversation.Id);
        TranscribeNowButton.Click += (_, _) =>
        {
            if (!_model.IsClosing) _model.TranscribeNow(Conversation.Id);
        };
        EngineMenu.Opening += (_, _) => BuildEngineMenu();

        Tapped += OnTapped;
        TitleText.DoubleTapped += OnTitleDoubleTapped;
        TitleBox.KeyDown += OnTitleBoxKeyDown;
        // Clicking elsewhere cancels, like Esc; only Enter saves.
        TitleBox.LostFocus += (_, _) => EndEditing();
        Unloaded += (_, _) => _openTimer.Stop();
    }

    /// <summary>The conversation as last listed. Internal: the XAML type info would try to create a public property's type.</summary>
    internal Conversation Conversation { get; private set; }

    /// <summary>Shows <paramref name="conversation"/>; compact for the wide layout's sidebar; selected adds the accent fill and edge.</summary>
    internal void Update(Conversation conversation, bool compact, bool selected)
    {
        Conversation = conversation;
        ContentGrid.Padding = compact ? new Thickness(12, 8, 12, 8) : new Thickness(16, 12, 16, 12);
        ButtonsPanel.Visibility = Shown(!compact);
        ActionRow.Margin = new Thickness(0, compact ? 0 : 6, 0, 0);
        SelectionFill.Visibility = Shown(selected);
        SelectionStroke.Visibility = Shown(selected);

        RecordingCard.SetText(TitleText, conversation.Title);
        SetTip(TitleText, conversation.Title, ref _titleTip);
        UpdateDetails(conversation);
        DeleteButton.IsEnabled = _model.CanDelete(conversation);
        UpdateStatus(conversation);
        // Compact cards have no buttons: an empty row would only add space.
        ActionRow.Visibility = Shown(!compact || ProgressPanel.Visibility == Visibility.Visible
            || TranscribeNowButton.Visibility == Visibility.Visible
            || NeedsModelBadge.Visibility == Visibility.Visible || RetryButton.Visibility == Visibility.Visible
            || TranscribeButton.Visibility == Visibility.Visible);
    }

    /// <summary>
    /// The source app's name when known, the date, the duration and size when known, the status badge, and whether
    /// the audio was removed.
    /// </summary>
    private void UpdateDetails(Conversation c)
    {
        SourceAppText.Visibility = Shown(!string.IsNullOrWhiteSpace(c.SourceAppName));
        RecordingCard.SetText(SourceAppText, c.SourceAppName ?? "");
        RecordingCard.SetText(DateText, c.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        DurationText.Visibility = Shown(c.Duration > 0);
        RecordingCard.SetText(DurationText, ConversationText.Clock(c.Duration));
        SizeText.Visibility = Shown(c.SizeBytes > 0);
        RecordingCard.SetText(SizeText, FileSize(c.SizeBytes));

        // macOS StatusBadge: red, orange or blue, here the theme's critical, caution and attention colors.
        (string Text, Border Badge, TextBlock Label)? badge = c.Status switch
        {
            ConversationStatus.Recording => (Loc.T("Recording"), CriticalBadge, CriticalBadgeText),
            ConversationStatus.Recovered => (Loc.T("Recovered"), CautionBadge, CautionBadgeText),
            ConversationStatus.Compressing => (Loc.T("Compressing…"), AttentionBadge, AttentionBadgeText),
            ConversationStatus.CompressionFailed => (Loc.T("Compression failed"), CriticalBadge, CriticalBadgeText),
            ConversationStatus.Failed => (Loc.T("Failed"), CriticalBadge, CriticalBadgeText),
            _ => null,
        };
        foreach (var each in new[] { CriticalBadge, CautionBadge, AttentionBadge })
        {
            each.Visibility = Shown(badge is { } b && b.Badge == each);
        }
        if (badge is { } shown) RecordingCard.SetText(shown.Label, shown.Text);

        // A failed recording may never have had audio: its badge says so.
        var removed = !c.HasAudio && c.Status is not (ConversationStatus.Recording or ConversationStatus.Failed);
        if (removed && NoAudioIcon.Visibility != Visibility.Visible)
        {
            var text = Loc.T("Audio has been removed");
            ToolTipService.SetToolTip(NoAudioIcon, text);
            AutomationProperties.SetName(NoAudioIcon, text);
        }
        NoAudioIcon.Visibility = Shown(removed);
    }

    /// <summary>
    /// macOS TranscriptionStatus: progress first (a queued job's conversation is still ready), with "Transcribe now" while
    /// it waits for energy saver; "Needs model"; Retry and its engine menu after a failed transcription, with the reason
    /// above; Transcribe for a ready conversation without a transcript (a Transcribing status without a job was left by a
    /// quit or crash). A failed recording offers nothing.
    /// </summary>
    private void UpdateStatus(Conversation c)
    {
        _model.TranscriptionProgress.TryGetValue(c.Id, out var progress);
        var running = progress is not null && progress.Step != JobStep.NeedsModel;
        var failed = progress is null && c.Status == ConversationStatus.TranscriptionFailed;
        var transcribe = progress is null && !failed && !c.HasTranscript
            && c.Status is ConversationStatus.Ready or ConversationStatus.Transcribing;

        ProgressPanel.Visibility = Shown(running);
        TranscribeNowButton.Visibility = Shown(running && progress!.Step == JobStep.Deferred);
        NeedsModelBadge.Visibility = Shown(progress is not null && !running);
        RetryButton.Visibility = Shown(failed);
        TranscribeButton.Visibility = Shown(transcribe);
        FailureText.Visibility = Shown(failed);

        if (running)
        {
            var queued = progress!.Step is JobStep.Queued or JobStep.Deferred;
            JobRing.IsIndeterminate = queued;
            JobRing.Value = queued ? 0 : progress.Fraction;
            RecordingCard.SetText(StepText, StepTitle(progress.Step));
            // Waiting has no percent: the step says why.
            SetTip(ProgressPanel, progress.Step == JobStep.Deferred
                ? StepTitle(progress.Step)
                : progress.Fraction.ToString("P0", CultureInfo.CurrentCulture), ref _stepTip);
        }
        if (failed)
        {
            var reason = _model.TranscriptionErrors.GetValueOrDefault(c.Id) ?? Loc.T("Transcription failed");
            RecordingCard.SetText(FailureText, reason);
            SetTip(FailureText, reason, ref _failureTip);
            RetryButton.IsEnabled = c.HasAudio;
            SetTip(RetryButton, Loc.T(c.HasAudio ? "Transcription failed — transcribe this conversation again" : "Audio has been removed"), ref _retryTip);
        }
        if (transcribe)
        {
            TranscribeButton.IsEnabled = c.HasAudio;
            SetTip(TranscribeButton, Loc.T(c.HasAudio ? "Transcribe this conversation" : "Audio has been removed"), ref _transcribeTip);
        }
    }

    internal static string StepTitle(JobStep step) => step switch
    {
        JobStep.Queued => Loc.T("Queued"),
        JobStep.Deferred => Loc.T("Waiting: energy saver is on"),
        JobStep.Extracting => Loc.T("Preparing audio…"),
        JobStep.TranscribingMic or JobStep.TranscribingSystem => Loc.T("Transcribing…"),
        JobStep.Diarizing => Loc.T("Identifying speakers…"),
        JobStep.Merging or JobStep.Finished => Loc.T("Finishing…"),
        _ => "",
    };

    /// <summary>
    /// macOS EngineMenuItems: the installed local engines under "Retry with another engine" (cloud engines come with
    /// Phase 25), or "Download a model…" (Settings, Phase 13) when there is none.
    /// </summary>
    private void BuildEngineMenu()
    {
        EngineMenu.Items.Clear();
        var engines = _model.InstalledBatchEngines;
        if (engines.Count == 0)
        {
            EngineMenu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Download a model…"), IsEnabled = false });
            return;
        }
        EngineMenu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Retry with another engine"), IsEnabled = false });
        foreach (var engine in engines)
        {
            var item = new MenuFlyoutItem { Text = engine.DisplayName };
            var id = engine.Id;
            item.Click += (_, _) => _ = _model.TranscribeAsync(Conversation.Id, id);
            EngineMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// macOS DeleteMenuItems: the audio only (or "Audio has been removed", disabled) and the whole conversation, each
    /// with "…" when it is asked first; the context menu's are disabled while it cannot be deleted.
    /// </summary>
    private void UpdateDeleteItems(MenuFlyoutItem audio, MenuFlyoutItem conversation, bool menu)
    {
        var asks = !_model.Settings.DeleteWithoutConfirmation;
        var hasAudio = Conversation.HasAudio;
        var deletable = !menu || _model.CanDelete(Conversation);
        audio.Text = hasAudio ? Loc.T(asks ? "Delete audio only…" : "Delete audio only") : Loc.T("Audio has been removed");
        audio.IsEnabled = hasAudio && deletable;
        conversation.Text = Loc.T(asks ? "Delete conversation…" : "Delete conversation");
        conversation.IsEnabled = deletable;
    }

    // Clicks and rename

    /// <summary>
    /// Ctrl-click toggles the card, Shift-click adds the span from the anchor; a click opens it, on the title once the
    /// double click is ruled out. Buttons and the title field take their own clicks.
    /// </summary>
    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != this; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase or SplitButton or TextBox) return;
        }
        if (ConversationListView.IsKeyDown(VirtualKey.Control) || ConversationListView.IsKeyDown(VirtualKey.Shift))
        {
            _openTimer.Stop();
            _list.Select(this, extend: ConversationListView.IsKeyDown(VirtualKey.Shift));
            return;
        }
        if (ReferenceEquals(e.OriginalSource, TitleText)) _openTimer.Start();
        else Open();
    }

    private void OnTitleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ConversationListView.IsKeyDown(VirtualKey.Control) || ConversationListView.IsKeyDown(VirtualKey.Shift)) return;
        _openTimer.Stop();
        e.Handled = true;
        BeginEditing();
    }

    /// <summary>Not while the title is edited: clicks in its field must not open the conversation.</summary>
    private void Open()
    {
        if (!_editing) _list.Open(this);
    }

    private void BeginEditing()
    {
        if (_editing) return;
        _editing = true;
        TitleBox.Text = Conversation.Title;
        TitleText.Visibility = Visibility.Collapsed;
        TitleBox.Visibility = Visibility.Visible;
        // Once the field is laid out, so it can take the focus.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_editing) return;
            TitleBox.Focus(FocusState.Programmatic);
            TitleBox.SelectAll();
        });
    }

    private void EndEditing()
    {
        if (!_editing) return;
        _editing = false;
        TitleBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    /// <summary>Enter saves a changed title, Esc cancels; neither reaches the list (Enter would open the card).</summary>
    private void OnTitleBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                var draft = TitleBox.Text;
                var id = Conversation.Id;
                var changed = draft != Conversation.Title;
                EndEditing();
                if (changed) _ = _model.RenameConversationAsync(id, draft);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                EndEditing();
                break;
        }
    }

    // Helpers

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static void SetTip(DependencyObject element, string text, ref string? shown)
    {
        if (text == shown) return;
        shown = text;
        ToolTipService.SetToolTip(element, text);
    }

    /// <summary>Like ByteCountFormatter's file style: decimal units, "12 KB", "3.4 MB", "1.23 GB".</summary>
    internal static string FileSize(long bytes)
    {
        var culture = CultureInfo.CurrentCulture;
        if (bytes < 1_000) return string.Format(culture, "{0} bytes", bytes);
        if (bytes < 1_000_000) return string.Format(culture, "{0:0} KB", bytes / 1e3);
        if (bytes < 1_000_000_000) return string.Format(culture, "{0:0.#} MB", bytes / 1e6);
        return string.Format(culture, "{0:0.##} GB", bytes / 1e9);
    }
}
