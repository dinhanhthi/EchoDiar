using System.Globalization;
using EchoDiar.AI;
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

namespace EchoDiar.App.Views;

/// <summary>
/// One conversation of the library (macOS ConversationCard.swift, with its DeleteMenuItems, TranscriptionStatus,
/// TranscriptionFailureReason and StatusBadge). A click opens it; Ctrl-click and Shift-click select it instead
/// (<see cref="ConversationListView"/> keeps the selection). The list sets its state with <see cref="Update"/>: cards
/// do not follow the model themselves, so a card that left the list holds no subscription.
/// </summary>
public sealed partial class ConversationCard : UserControl
{
    private readonly AppViewModel _model;
    private readonly ConversationListView _list;

    private bool _editing;

    /// <summary>Rename was picked in the context menu: the field opens once the menu has closed and given focus back.</summary>
    private bool _renameAfterMenu;

    /// <summary>The tooltips as last set: one set again while shown closes under the pointer (Update runs at each job percent).</summary>
    private string? _titleTip;
    private string? _failureTip;
    private string? _stepTip;
    private string? _retryTip;
    private string? _transcribeTip;
    private string? _badgeTip;

    internal ConversationCard(AppViewModel model, ConversationListView list, Conversation conversation)
    {
        _model = model;
        _list = list;
        Conversation = conversation;
        InitializeComponent();
        RevealItem.Text = Loc.T("Reveal in Explorer");
        RevealItem.Click += (_, _) => AppViewModel.RevealInExplorer(Conversation);
        RenameItem.Click += (_, _) => _renameAfterMenu = true;
        CardMenu.Opening += (_, _) =>
        {
            StopTranscriptionItem.Visibility = Shown(_model.CanStopTranscription(Conversation.Id));
            UpdateDeleteItems();
        };
        StopTranscriptionItem.Click += (_, _) => _model.StopTranscription(Conversation.Id);
        CardMenu.Closed += (_, _) =>
        {
            if (!_renameAfterMenu) return;
            _renameAfterMenu = false;
            BeginEditing();
        };
        MenuDeleteAudioItem.Click += (_, _) => _ = _list.DeleteAsync([Conversation], audioOnly: true);
        MenuDeleteConversationItem.Click += (_, _) => _ = _list.DeleteAsync([Conversation], audioOnly: false);
        RetryButton.Click += (_, _) => _ = _model.TranscribeAsync(Conversation.Id);
        TranscribeButton.Click += (_, _) => _ = _model.TranscribeAsync(Conversation.Id);
        NeedsModelBadge.Click += (_, _) => _model.OpenLocalAI(ProvidersSettingsPage.TranscriptionGroup);
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
        // Leaving the page or closing the window mid-rename never ends the edit.
        Unloaded += (_, _) => EndRenamingFlag();
    }

    /// <summary>The conversation as last listed. Internal: the XAML type info would try to create a public property's type.</summary>
    internal Conversation Conversation { get; private set; }

    /// <summary>Shows <paramref name="conversation"/>; selected adds the accent fill and edge.</summary>
    internal void Update(Conversation conversation, bool selected)
    {
        Conversation = conversation;
        SelectionFill.Visibility = Shown(selected);
        SelectionStroke.Visibility = Shown(selected);

        RecordingCard.SetText(TitleText, conversation.Title);
        SetTip(TitleText, conversation.Title, ref _titleTip);
        UpdateDetails(conversation);
        UpdateStatus(conversation);
        UpdateBadge(conversation);
        // An empty row would only add space.
        ActionRow.Visibility = Shown(ProgressPanel.Visibility == Visibility.Visible
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
        // After the failure, as macOS: Retry stays.
        var aboutToTranscribe = !failed && IsAboutToTranscribe(c);
        var transcribe = progress is null && !failed && !aboutToTranscribe && !c.HasTranscript
            && c.Status is ConversationStatus.Ready or ConversationStatus.Transcribing;
        // The transcription's state first; else the Stop sequence's "Transcribing…", else the AI job's.
        Activity? waiting = running ? null : aboutToTranscribe ? Activity.Transcribing : AIJobActivity(c.Id);

        ProgressPanel.Visibility = Shown(running || waiting is not null);
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
            // "Stopping…" from the Stop click until the job ends (macOS ProgressLabel).
            var title = _model.IsStopping(c.Id) ? Loc.T("Stopping…") : StepTitle(progress.Step);
            RecordingCard.SetText(StepText, title);
            // Waiting has no percent: the step says why.
            SetTip(ProgressPanel, progress.Step == JobStep.Deferred
                ? title
                : progress.Fraction.ToString("P0", CultureInfo.CurrentCulture), ref _stepTip);
        }
        else if (waiting is { } activity)
        {
            JobRing.IsIndeterminate = true;
            JobRing.Value = 0;
            var text = ActivityText(activity);
            RecordingCard.SetText(StepText, text);
            SetTip(ProgressPanel, text, ref _stepTip);
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

    /// <summary>
    /// macOS CardActivity, most important first: a transcription (queued, running or in the Stop sequence), then the AI
    /// job. Read from <see cref="AppViewModel.AIJobs"/> only, never the streamed text, so answers do not redraw the cards.
    /// </summary>
    private enum Activity { Transcribing, Summarizing, Answering, Queued }

    /// <summary>The activity on the icon's corner, its text as tooltip and name; hidden without one.</summary>
    private void UpdateBadge(Conversation c)
    {
        Activity? activity = _model.TranscriptionProgress.TryGetValue(c.Id, out var progress) && progress.Step != JobStep.NeedsModel
            || IsAboutToTranscribe(c)
            ? Activity.Transcribing
            : AIJobActivity(c.Id);
        ActivityBadge.Visibility = Shown(activity is not null);
        if (activity is not { } shown) return;
        ActivityGlyph.Glyph = shown switch
        {
            Activity.Transcribing => "\uE720",
            Activity.Summarizing => "\uE945",
            Activity.Answering => "\uE8BD",
            _ => "\uE823",
        };
        var text = ActivityText(shown);
        if (text == _badgeTip) return;
        SetTip(ActivityBadge, text, ref _badgeTip);
        AutomationProperties.SetName(ActivityBadge, text);
        AutomationProperties.SetName(ActivityGlyph, text);
    }

    /// <summary>
    /// macOS isAboutToTranscribe: still in the Stop sequence with no job yet, and an automatic transcription about to
    /// run; not compressing (the badge says so), nor a failed recording (never transcribed), nor transcribed already.
    /// </summary>
    private bool IsAboutToTranscribe(Conversation c) =>
        !_model.TranscriptionProgress.ContainsKey(c.Id) && _model.IsTranscribing(c.Id) && _model.Settings.AutoTranscribe
        && c.Status is not (ConversationStatus.Compressing or ConversationStatus.Failed) && !c.HasTranscript;

    /// <summary>The conversation's summary or question job: "Queued" behind another in-app job first; null without one.</summary>
    private Activity? AIJobActivity(Guid id)
    {
        if (_model.AIJobs.EntryFor(id) is not { } entry) return null;
        if (_model.AIJobs.IsQueued(id)) return Activity.Queued;
        return entry.Kind switch
        {
            AIJobTable.Kind.Summary => Activity.Summarizing,
            AIJobTable.Kind.Question => Activity.Answering,
            _ => null,
        };
    }

    private static string ActivityText(Activity activity) => activity switch
    {
        Activity.Transcribing => Loc.T("Transcribing…"),
        Activity.Summarizing => Loc.T("Summarizing…"),
        Activity.Answering => Loc.T("Answering…"),
        _ => Loc.T("Queued"),
    };

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
    /// macOS EngineMenuItems: the installed local and configured cloud engines under "Retry with another engine", the
    /// default one first, marked "(default)" (<see cref="AppViewModel.InstalledBatchEngines"/>), or, when there is
    /// none, "Download a model…", which opens AI Providers › Local AI › Transcription (macOS
    /// <c>openLocalAI(.transcription)</c>).
    /// </summary>
    private void BuildEngineMenu()
    {
        EngineMenu.Items.Clear();
        var engines = _model.InstalledBatchEngines;
        if (engines.Count == 0)
        {
            var download = new MenuFlyoutItem { Text = Loc.T("Download a model…") };
            download.Click += (_, _) => _model.OpenLocalAI(ProvidersSettingsPage.TranscriptionGroup);
            EngineMenu.Items.Add(download);
            return;
        }
        EngineMenu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Retry with another engine"), IsEnabled = false });
        foreach (var engine in engines)
        {
            var item = new MenuFlyoutItem { Text = engine.MenuText };
            var id = engine.Id;
            item.Click += (_, _) => _ = _model.TranscribeAsync(Conversation.Id, id);
            EngineMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// macOS DeleteMenuItems: the audio only (or "Audio has been removed", disabled) and the whole conversation, each
    /// with "…" when it is asked first; disabled while it cannot be deleted.
    /// </summary>
    private void UpdateDeleteItems()
    {
        var asks = !_model.Settings.DeleteWithoutConfirmation;
        var hasAudio = Conversation.HasAudio;
        var deletable = _model.CanDelete(Conversation);
        MenuDeleteAudioItem.Text = hasAudio ? Loc.T(asks ? "Delete audio only…" : "Delete audio only") : Loc.T("Audio has been removed");
        MenuDeleteAudioItem.IsEnabled = hasAudio && deletable;
        MenuDeleteConversationItem.Text = Loc.T(asks ? "Delete conversation…" : "Delete conversation");
        MenuDeleteConversationItem.IsEnabled = deletable;
    }

    // Clicks and rename

    /// <summary>
    /// Ctrl-click toggles the card, Shift-click adds the span from the anchor; a click opens it at once, also on the
    /// title, whose double click then renames the opened conversation (macOS c45b593). Buttons and the title field take
    /// their own clicks.
    /// </summary>
    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != this; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase or SplitButton or TextBox) return;
        }
        if (ConversationListView.IsKeyDown(VirtualKey.Control) || ConversationListView.IsKeyDown(VirtualKey.Shift))
        {
            _list.Select(this, extend: ConversationListView.IsKeyDown(VirtualKey.Shift));
            return;
        }
        Open();
    }

    private void OnTitleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ConversationListView.IsKeyDown(VirtualKey.Control) || ConversationListView.IsKeyDown(VirtualKey.Shift)) return;
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
        _model.RenamingConversation = Conversation.Id;
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
        EndRenamingFlag();
        TitleBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    /// <summary>Only if it is still this card's: another card's rename may have started since.</summary>
    private void EndRenamingFlag()
    {
        if (_model.RenamingConversation == Conversation.Id) _model.RenamingConversation = null;
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
        if (bytes < 1_000) return Loc.Plural("%lld bytes", bytes);
        if (bytes < 1_000_000) return string.Format(culture, "{0:0} KB", bytes / 1e3);
        if (bytes < 1_000_000_000) return string.Format(culture, "{0:0.#} MB", bytes / 1e6);
        return string.Format(culture, "{0:0.##} GB", bytes / 1e9);
    }
}
