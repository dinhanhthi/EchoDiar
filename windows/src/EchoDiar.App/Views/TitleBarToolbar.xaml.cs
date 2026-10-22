using System.ComponentModel;
using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Platform.SystemServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.ViewManagement;

namespace EchoDiar.App.Views;

/// <summary>
/// The title bar's button groups (macOS MainView.swift toolbar), shown by route: Back off the list; Record with its menu of
/// targets and the language (Stop, blinking, and Pause or Resume while recording); on a conversation its title, Reveal, Delete and the
/// tabs (a SelectorBar); Settings (a placeholder until Phase 13) and Search everywhere, an icon button that opens the box
/// where the box does not fit. Update and Ask across conversations stay
/// collapsed until Phases 27 and 26. It also hosts the window's dialogs (it lives as long as the window): the delete
/// confirmations (of this conversation, and of the library's cards and selection), "Recording problem" and the
/// lost-mic prompt, one at a time.
/// </summary>
public sealed partial class TitleBarToolbar : UserControl
{
    private const string PauseGlyph = "";
    private const string ResumeGlyph = "";

    /// <summary>The recording indicator dims every other second (macOS RecordingBlink).</summary>
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromSeconds(1);

    /// <summary>The cards are filtered once typing pauses this long (at once when cleared).</summary>
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>The search box's narrowest width that still shows its placeholder whole.</summary>
    private const double SearchMinWidth = 120;

    private static readonly UISettings UISettings = new();

    private AppViewModel? _model;

    /// <summary>The open dialog: WinUI shows one at a time per window.</summary>
    private ContentDialog? _dialog;

    /// <summary>The lost-mic prompt is open.</summary>
    private ContentDialog? _routePrompt;

    /// <summary>The mic was lost while another dialog was open: asked once that one closes.</summary>
    private bool _routePromptPending;

    private DispatcherQueueTimer? _blinkTimer;
    private DispatcherQueueTimer? _searchTimer;

    /// <summary>The route's page was the list (not a conversation or the recording) when last shown.</summary>
    private bool _onListPage = true;

    /// <summary>Record's tooltip, "Record" and the target; set again only when the target changes.</summary>
    private string? _recordTip;

    /// <summary>The pause state Pause or Resume was last labeled for.</summary>
    private bool? _shownPaused;

    /// <summary>The search box does not fit next to the other columns: Search shows as an icon button.</summary>
    private bool _searchAsIcon;

    /// <summary>The icon button opened the box, which takes the trailing buttons' place until it is left empty.</summary>
    private bool _searchOpened;

    /// <summary>
    /// The width the title bar gives the toolbar: ActualWidth is never less than the desired width (an overflowing
    /// toolbar is clipped), so it cannot tell that the box does not fit.
    /// </summary>
    private double _availableWidth = double.PositiveInfinity;

    /// <summary>The Record menu's end: a separator and the Language submenu, kept while the targets are rebuilt.</summary>
    private readonly MenuFlyoutSeparator _languageSeparator = new();
    private readonly MenuFlyoutSubItem _languageItem = new() { Text = Loc.T("Language") };

    public TitleBarToolbar()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => _model?.GoBack();
        RecordSplitButton.Click += (_, _) => _ = _model?.StartRecordingAsync(_model.SelectedTarget);
        TargetMenu.Items.Add(_languageSeparator);
        TargetMenu.Items.Add(_languageItem);
        TargetMenu.Opening += (_, _) => OnTargetMenuOpening();
        StopButton.Click += (_, _) => _ = _model?.StopRecordingAsync();
        PauseButton.Click += (_, _) => _ = _model?.TogglePauseAsync();
        RevealButton.Click += (_, _) => Reveal();
        DeleteMenu.Opening += (_, _) => UpdateDeleteMenu();
        DeleteAudioItem.Click += async (_, _) => await DeleteShownAsync(audioOnly: true);
        DeleteConversationItem.Click += async (_, _) => await DeleteShownAsync(audioOnly: false);
        SearchBox.TextChanged += (_, _) => OnSearchTextChanged();
        // The inner text field handles Esc first.
        SearchBox.AddHandler(KeyDownEvent, new KeyEventHandler(OnSearchKeyDown), handledEventsToo: true);
        SearchBox.LostFocus += (_, _) => OnSearchLostFocus();
        SearchButton.Click += (_, _) => OpenSearch();
        LayoutUpdated += (_, _) => UpdateSearchMode();
        // Summary and Chat stay disabled until their phases.
        TabsBar.SelectedItem = TranscriptTab;
        // A start that failed before the window was shown still gets its dialog.
        Loaded += (_, _) => ShowNextDialog();
    }

    /// <summary>Follows <paramref name="model"/> for as long as the window lives.</summary>
    public void Attach(AppViewModel model)
    {
        _model = model;
        model.PropertyChanged += OnModelChanged;
        model.MicRouteLostAsk += OnMicRouteLostAsk;
        _blinkTimer = DispatcherQueue.CreateTimer();
        _blinkTimer.Interval = BlinkInterval;
        _blinkTimer.Tick += (_, _) => Blink();
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = SearchDelay;
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (timer, _) =>
        {
            timer.Stop();
            if (_model is not null) _model.SearchTerm = SearchBox.Text;
        };
        Update();
    }

    /// <summary>The window closed: its controls fail every call, so the model's changes no longer reach them.</summary>
    public void Detach()
    {
        if (_model is null) return;
        _model.PropertyChanged -= OnModelChanged;
        _model.MicRouteLostAsk -= OnMicRouteLostAsk;
        _model = null;
        _blinkTimer?.Stop();
        _blinkTimer = null;
        _searchTimer?.Stop();
        _searchTimer = null;
    }

    /// <summary>
    /// The window-space rects (physical pixels at <paramref name="scale"/>) of every visible interactive element: the
    /// title bar passes clicks there to the controls, and drags the window everywhere else.
    /// </summary>
    public IReadOnlyList<RectInt32> PassthroughRects(double scale)
    {
        FrameworkElement[] elements = [BackButton, RecordGroup, UpdateButton, FileGroup, AppGroup, SearchBox, SearchButton, TabsBar];
        var rects = new List<RectInt32>();
        foreach (var element in elements)
        {
            if (!IsShown(element) || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
            var bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            rects.Add(new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)));
        }
        return rects;
    }

    /// <summary>The element and its parents up to the toolbar are visible: a hidden panel's children keep their last size.</summary>
    private bool IsShown(FrameworkElement element)
    {
        for (DependencyObject? e = element; e is not null && e != this; e = VisualTreeHelper.GetParent(e))
        {
            if (e is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.Conversations):
                StartupTrace.Write($"toolbar update ({_model?.Conversations.Count} conversations)");
                Update();
                break;
            case nameof(AppViewModel.Route):
            case nameof(AppViewModel.RecordingSnapshot):
            case nameof(AppViewModel.IsStarting):
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.TranscriptionErrors):
            case nameof(AppViewModel.SelectedTarget):
                Update();
                break;
            case nameof(AppViewModel.RecordingError):
                ShowNextDialog();
                break;
            case nameof(AppViewModel.RecordTargets):
                // Apps that started playing since the menu opened show up in it.
                if (TargetMenu.IsOpen) BuildTargetMenu();
                break;
        }
    }

    private void Update()
    {
        if (_model is null) return;
        BackButton.Visibility = Shown(_model.Route.Kind != RouteKind.List);
        // Another page clears the search (macOS MainView.swift); another conversation keeps it, for the sidebar it filters.
        var onListPage = _model.Route.Kind == RouteKind.List;
        if (onListPage != _onListPage)
        {
            _onListPage = onListPage;
            CloseSearch();
            SearchBox.Text = "";
        }

        var recording = _model.IsRecording;
        var paused = _model.RecordingSnapshot.State == RecordingEngine.State.Paused;
        RecordSplitButton.Visibility = Shown(!recording);
        RecordSplitButton.IsEnabled = !_model.IsStarting;
        var recordTip = $"Record {_model.SelectedTarget?.Name ?? Loc.T("All system audio")}";
        if (recordTip != _recordTip) ToolTipService.SetToolTip(RecordSplitButton, _recordTip = recordTip);
        StopButton.Visibility = Shown(recording);
        StopButton.IsEnabled = !_model.IsStarting;
        PauseButton.Visibility = Shown(recording);
        // The snapshot changes at 15 Hz: a tooltip set again each time would close under the pointer.
        if (paused != _shownPaused)
        {
            _shownPaused = paused;
            PauseIcon.Glyph = paused ? ResumeGlyph : PauseGlyph;
            var pauseLabel = Loc.T(paused ? "Resume recording" : "Pause recording");
            AutomationProperties.SetName(PauseButton, pauseLabel);
            ToolTipService.SetToolTip(PauseButton, pauseLabel);
        }
        UpdateBlink(recording && !paused);
        // The recording ended (stopped elsewhere): the lost-mic question no longer applies.
        if (!recording)
        {
            _routePromptPending = false;
            _routePrompt?.Hide();
        }

        var conversation = ShownConversation();
        var onConversation = conversation is not null;
        TitlePanel.Visibility = Shown(onConversation);
        FileGroup.Visibility = Shown(onConversation);
        TabsBar.Visibility = Shown(onConversation);
        if (conversation is null) return;
        TitleText.Text = conversation.Title;
        DetailText.Text = ConversationText.Detail(conversation, _model);
        DeleteButton.IsEnabled = _model.CanDelete(conversation);
    }

    /// <summary>The conversation page's conversation, while it exists; null on the other pages.</summary>
    private Conversation? ShownConversation() =>
        _model?.Route is { Kind: RouteKind.Conversation, ConversationId: { } id }
            ? _model.Conversations.FirstOrDefault(c => c.Id == id)
            : null;

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // Record menu

    /// <summary>Built when it opens, then refreshed: apps that started playing since show up.</summary>
    private void OnTargetMenuOpening()
    {
        if (_model is not null) LanguageOptions.Fill(_languageItem.Items, _model);
        BuildTargetMenu();
        _ = _model?.RefreshRecordTargetsAsync();
    }

    /// <summary>
    /// "All system audio" first, then the apps with audio (the picked one is checked), then Language. Only the targets
    /// are rebuilt, so an open Language submenu stays open.
    /// </summary>
    private void BuildTargetMenu()
    {
        if (_model is null) return;
        while (TargetMenu.Items[0] != _languageSeparator) TargetMenu.Items.RemoveAt(0);
        var index = 0;
        TargetMenu.Items.Insert(index++, TargetItem(Loc.T("All system audio"), null));
        if (_model.RecordTargets.Count == 0) return;
        TargetMenu.Items.Insert(index++, new MenuFlyoutSeparator());
        foreach (var group in _model.RecordTargets)
        {
            TargetMenu.Items.Insert(index++, TargetItem(group.NeedsGlobalTap ? $"{group.Name} (records all system audio)" : group.Name, group));
        }
    }

    /// <summary>Matched by app, since process lists change.</summary>
    private RadioMenuFlyoutItem TargetItem(string text, AudioProcessGroup? group)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = text,
            GroupName = "RecordTarget",
            IsChecked = _model?.SelectedTarget?.Exe == group?.Exe,
        };
        item.Click += (_, _) =>
        {
            if (_model is not null) _model.SelectedTarget = group;
        };
        return item;
    }

    // Reveal and Delete

    private void Reveal()
    {
        if (ShownConversation() is not { } conversation) return;
        try
        {
            ShellFiles.Reveal(conversation.Folder);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not reveal {conversation.Folder}: {e}");
        }
    }

    /// <summary>Delete's choices: the audio only, or the whole conversation; "…" when each is asked first.</summary>
    private void UpdateDeleteMenu()
    {
        if (_model is null || ShownConversation() is not { } conversation) return;
        var asks = !_model.Settings.DeleteWithoutConfirmation;
        DeleteAudioItem.Text = conversation.HasAudio
            ? Loc.T(asks ? "Delete audio only…" : "Delete audio only")
            : Loc.T("Audio has been removed");
        DeleteAudioItem.IsEnabled = conversation.HasAudio;
        DeleteConversationItem.Text = Loc.T(asks ? "Delete conversation…" : "Delete conversation");
    }

    private async Task DeleteShownAsync(bool audioOnly)
    {
        if (ShownConversation() is { } conversation) await DeleteAsync([conversation], audioOnly);
    }

    /// <summary>
    /// macOS DeleteMenuItems.delete, for this conversation and the library's cards and selection: only the
    /// conversations that can be deleted now (and have audio, for the audio only), asked once for all unless "Delete
    /// conversations without confirmation" is on; one is asked by its title. Then they are deleted one by one. True
    /// when any was deleted (not cancelled, not none).
    /// </summary>
    public async Task<bool> DeleteAsync(IReadOnlyList<Conversation> conversations, bool audioOnly)
    {
        if (_model is not { } model) return false;
        bool Eligible(Conversation c) => model.CanDelete(c) && (!audioOnly || c.HasAudio);
        var eligible = conversations.Where(Eligible).ToList();
        if (eligible.Count == 0) return false;
        if (!model.Settings.DeleteWithoutConfirmation && !await ConfirmDeleteAsync(eligible, audioOnly)) return false;
        // The window closed while the dialog was open.
        if (_model is null) return false;
        // A job may have started, or a conversation gone, while the dialog was open.
        var ids = eligible
            .Select(c => model.Conversations.FirstOrDefault(current => current.Id == c.Id))
            .OfType<Conversation>()
            .Where(Eligible)
            .Select(c => c.Id)
            .ToList();
        // Each delete first has the open player let go of that conversation's audio (ReleasingAudio).
        foreach (var id in ids)
        {
            if (audioOnly) await model.DeleteAudioAsync(id);
            else await model.DeleteConversationAsync(id);
        }
        return true;
    }

    /// <summary>The macOS alerts' texts, with the Recycle Bin for the Trash.</summary>
    private async Task<bool> ConfirmDeleteAsync(IReadOnlyList<Conversation> conversations, bool audioOnly)
    {
        if (_dialog is not null) return false;
        var one = conversations.Count == 1 ? conversations[0] : null;
        var title = (one, audioOnly) switch
        {
            ({ } c, true) => Loc.Format("Delete the audio of \"%@\"?", c.Title),
            // Not through Loc: the text is not in the catalog, and its slug finds the catalog's "Delete".
            ({ } c, false) => $"Delete \"{c.Title}\"?",
            (null, true) => Loc.Format("Delete the audio of %lld conversations?", conversations.Count),
            (null, false) => Loc.Format("Delete %lld conversations?", conversations.Count),
        };
        var body = audioOnly
            ? Loc.T("The audio is moved to the Recycle Bin. The transcript, summary and chat stay; playback and re-transcribing are no longer possible.")
            : one is not null
                ? Loc.T("The recording and its files are moved to the Recycle Bin.")
                : Loc.T("The recordings and their files are moved to the Recycle Bin.");
        var dialog = NewDialog(title, body);
        dialog.PrimaryButtonText = Loc.T("Delete");
        dialog.CloseButtonText = Loc.T("Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        var confirmed = false;
        await ShowAsync(dialog, result => confirmed = result == ContentDialogResult.Primary);
        return confirmed;
    }

    // Search

    /// <summary>Filters once typing pauses; a cleared field shows every conversation at once.</summary>
    private void OnSearchTextChanged()
    {
        if (_model is null || _searchTimer is null) return;
        _searchTimer.Stop();
        if (SearchBox.Text.Length == 0) _model.SearchTerm = "";
        else _searchTimer.Start();
    }

    /// <summary>Esc clears the search (macOS closes its search bar); on an empty opened box it goes back to the icon.</summary>
    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        if (SearchBox.Text.Length > 0)
        {
            e.Handled = true;
            SearchBox.Text = "";
        }
        else if (_searchOpened)
        {
            e.Handled = true;
            CloseSearch();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_model is not null) SearchButton.Focus(FocusState.Keyboard);
            });
        }
    }

    /// <summary>
    /// Search is an icon button where the box would get less than <see cref="SearchMinWidth"/> next to the other columns
    /// (the compact minimum, if the caption buttons take more than expected), and the box again once it fits. Not while
    /// the opened box hides the trailing buttons, whose width it needs.
    /// </summary>
    private void UpdateSearchMode()
    {
        if (_model is null || _searchOpened || double.IsInfinity(_availableWidth) || ToolbarGrid.ActualWidth <= 0) return;
        var others = ToolbarGrid.Padding.Left + LeadingColumn.ActualWidth + TrailingColumn.ActualWidth
            + TabsColumn.ActualWidth + DragColumn.ActualWidth;
        var asIcon = _availableWidth - others < SearchMinWidth;
        if (asIcon == _searchAsIcon) return;
        _searchAsIcon = asIcon;
        StartupTrace.Write($"search as icon {asIcon} (available {_availableWidth:0}, other columns {others:0})");
        ApplySearchMode();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _availableWidth = availableSize.Width;
        return base.MeasureOverride(availableSize);
    }

    private void ApplySearchMode()
    {
        var box = !_searchAsIcon || _searchOpened;
        SearchBox.Visibility = Shown(box);
        SearchButton.Visibility = Shown(!box);
        SearchColumn.Width = box ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        SearchColumn.MinWidth = box ? SearchMinWidth : 0;
        TrailingPanel.Visibility = Shown(!_searchOpened);
    }

    private void OpenSearch()
    {
        _searchOpened = true;
        ApplySearchMode();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is not null) SearchBox.Focus(FocusState.Programmatic);
        });
    }

    private void CloseSearch()
    {
        if (!_searchOpened) return;
        _searchOpened = false;
        ApplySearchMode();
    }

    /// <summary>The opened box, left empty, goes back to the icon.</summary>
    private void OnSearchLostFocus()
    {
        if (!_searchOpened) return;
        // Focus moving inside the box (its text field, its buttons) is not leaving it.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is not null && SearchBox.Text.Length == 0 && !SearchHasFocus()) CloseSearch();
        });
    }

    private bool SearchHasFocus()
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        for (var e = focused; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e == SearchBox) return true;
        }
        return false;
    }

    // Recording dialogs and indicator

    /// <summary>Shows what waits, unless a dialog is open: a recording problem first, then the lost-mic prompt.</summary>
    private void ShowNextDialog()
    {
        if (_model is null || _dialog is not null || XamlRoot is null) return;
        if (_model.RecordingError is { } error)
        {
            _ = ShowRecordingProblemAsync(error);
        }
        else if (_routePromptPending)
        {
            _routePromptPending = false;
            StartupTrace.Write($"lost-mic prompt: {(_model.IsRecording ? "shown" : "dropped, not recording")}");
            if (_model.IsRecording) _ = AskRouteLostAsync();
        }
    }

    /// <summary>
    /// "Recording problem" (macOS MainView.swift alert on recordingError): the only UI for a start or resume that
    /// failed and for a recording the engine stopped on its own.
    /// </summary>
    private Task ShowRecordingProblemAsync(string error)
    {
        var dialog = NewDialog(Loc.T("Recording problem"), error);
        dialog.CloseButtonText = Loc.T("OK");
        dialog.DefaultButton = ContentDialogButton.Close;
        return ShowAsync(dialog, _ => _model?.DismissRecordingError(error));
    }

    private void OnMicRouteLostAsk()
    {
        StartupTrace.Write($"lost-mic prompt asked: open prompt {_routePrompt is not null}, open dialog {_dialog is not null}, XamlRoot {XamlRoot is not null}");
        if (_routePrompt is not null) return;
        _routePromptPending = true;
        ShowNextDialog();
    }

    /// <summary>
    /// The lost mic with "Ask to stop" (macOS: the pill's end prompt for inputRouteLost, its countdown comes with
    /// Phase 19): Stop now stops as Stop does, Keep recording carries on with the default input.
    /// </summary>
    private async Task AskRouteLostAsync()
    {
        var dialog = NewDialog("Microphone disconnected — stop recording?", null);
        dialog.PrimaryButtonText = Loc.T("Stop now");
        dialog.CloseButtonText = Loc.T("Keep recording");
        dialog.DefaultButton = ContentDialogButton.Primary;
        _routePrompt = dialog;
        await ShowAsync(dialog, result =>
        {
            StartupTrace.Write($"lost-mic prompt answered: {result}");
            if (result == ContentDialogResult.Primary && _model is { IsRecording: true } model) _ = model.StopRecordingAsync();
        });
        _routePrompt = null;
    }

    private ContentDialog NewDialog(string title, string? content) => new()
    {
        XamlRoot = XamlRoot,
        Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
        Title = title,
        Content = content,
    };

    /// <summary>
    /// Shows <paramref name="dialog"/> as the open one, hands its result to <paramref name="answered"/> (not once the
    /// window closed), then shows what waits.
    /// </summary>
    private async Task ShowAsync(ContentDialog dialog, Action<ContentDialogResult> answered)
    {
        _dialog = dialog;
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not show a dialog: {e}");
            result = ContentDialogResult.None;
        }
        finally
        {
            _dialog = null;
        }
        if (_model is null) return;
        answered(result);
        ShowNextDialog();
    }

    /// <summary>Stop blinks while recording, unless paused, the window is hidden or Windows animations are off.</summary>
    private void UpdateBlink(bool active)
    {
        if (_blinkTimer is null) return;
        active &= UISettings.AnimationsEnabled;
        if (active && !_blinkTimer.IsRunning) _blinkTimer.Start();
        if (!active)
        {
            _blinkTimer.Stop();
            StopIcon.Opacity = 1;
        }
    }

    private void Blink()
    {
        if (_model is null) return;
        StopIcon.Opacity = StopIcon.Opacity < 1 || XamlRoot is not { IsHostVisible: true } ? 1 : 0.35;
    }
}
