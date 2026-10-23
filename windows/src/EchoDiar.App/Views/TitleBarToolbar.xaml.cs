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
/// The title bar's button groups (macOS MainView.swift toolbar), shown by route: Back off the list (and on it while the
/// library chat shows); Record with its menu of targets and the language (Stop, blinking, and Pause or Resume while
/// recording); on a conversation its title, Reveal, Delete and the tabs (a SelectorBar); on Settings the section's inner
/// tabs after Record; Settings (opens the Settings page, or goes back from it), Ask across conversations (opens or
/// closes the library chat) and Search everywhere, an icon button that opens the box where the box does not fit. Update
/// stays collapsed until Phase 28. It also hosts the window's dialogs (it lives as long as the window): the delete
/// confirmations (of this conversation, and of the library's cards and selection), "Recording problem", the cloud
/// confirmations (audio for transcription, transcripts for the library index) and the Settings pages' confirmations
/// (<see cref="AppViewModel.Confirm"/>), one at a time. The lost-mic question is the pill's end prompt.
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

    private DispatcherQueueTimer? _blinkTimer;
    private DispatcherQueueTimer? _searchTimer;

    /// <summary>The route's page (<see cref="AppViewModel.PageOf"/>) when last shown.</summary>
    private int _page;

    /// <summary>The Settings section the inner tabs were built for.</summary>
    private string? _tabsSection;

    /// <summary>The inner tabs or their selection are being set from the model: not a pick.</summary>
    private bool _syncingSettingsTabs;

    /// <summary>Record's tooltip, "Record" and the target; set again only when the target changes.</summary>
    private string? _recordTip;

    /// <summary>The pause state Pause or Resume was last labeled for.</summary>
    private bool? _shownPaused;

    /// <summary>The search box does not fit next to the other columns: Search shows as an icon button.</summary>
    private bool _searchAsIcon;

    /// <summary>
    /// The icon button (or narrowing with text in the box) opened the box, which takes the trailing buttons' place until
    /// it is left empty.
    /// </summary>
    private bool _searchOpened;

    /// <summary>The trailing buttons' width when last shown, which the opened box takes over.</summary>
    private double _trailingWidth;

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
        SettingsButton.Click += (_, _) => ToggleSettings();
        AskButton.Click += (_, _) => ToggleGlobalChat();
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
        // Search filters the cards: they come back in place of the library chat. Settings search keeps it, for the page
        // Settings returns to (macOS searchPresented).
        SearchBox.GotFocus += (_, _) =>
        {
            if (_model is { Route.Kind: not RouteKind.Settings }) _model.GlobalChatOpen = false;
        };
        SearchButton.Click += (_, _) => OpenSearch();
        LayoutUpdated += (_, _) => UpdateSearchMode();
        TabsBar.SelectedItem = TranscriptTab;
        TabsBar.SelectionChanged += (_, _) => OnTabSelected();
        SettingsTabsBar.SelectionChanged += (_, _) => OnSettingsTabSelected();
        // A start that failed before the window was shown still gets its dialog.
        Loaded += (_, _) => ShowNextDialog();
    }

    /// <summary>Follows <paramref name="model"/> for as long as the window lives.</summary>
    public void Attach(AppViewModel model)
    {
        _model = model;
        model.PropertyChanged += OnModelChanged;
        // Settings pages ask through here too, so the window never has two dialogs.
        model.Confirm = ConfirmAsync;
        model.DeleteFlow = DeleteAsync;
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
        _model.Confirm = null;
        _model.DeleteFlow = null;
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
        FrameworkElement[] elements = [BackButton, RecordGroup, SettingsTabsBar, UpdateButton, FileGroup, AppGroup, SearchBox, SearchButton, TabsBar];
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
            case nameof(AppViewModel.GlobalChatOpen):
            case nameof(AppViewModel.RecordingSnapshot):
            case nameof(AppViewModel.IsStarting):
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.TranscriptionErrors):
            case nameof(AppViewModel.SelectedTarget):
                Update();
                break;
            case nameof(AppViewModel.RecordingError):
            case nameof(AppViewModel.PendingCloudRuns):
            case nameof(AppViewModel.PendingEmbeddingConsent):
                ShowNextDialog();
                break;
            case nameof(AppViewModel.DetailTab):
                UpdateTabs();
                break;
            case nameof(AppViewModel.SettingsSection):
            case nameof(AppViewModel.SettingsSubTab):
                UpdateSettingsTabs();
                break;
            case nameof(AppViewModel.SearchTerm):
                OnSearchTermChanged();
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
        BackButton.Visibility = Shown(_model.Route.Kind != RouteKind.List || _model.GlobalChatOpen);
        // Another page (list, entry, Settings) clears the search (the model has cleared the term: AppViewModel.Route);
        // another conversation keeps it, for the sidebar it filters. Typing not applied yet is dropped too.
        var page = AppViewModel.PageOf(_model.Route);
        if (page != _page)
        {
            _page = page;
            _searchTimer?.Stop();
            CloseSearch();
            SearchBox.Text = "";
            var searchLabel = Loc.T(_model.Route.Kind == RouteKind.Settings ? "Search settings" : "Search conversations");
            AutomationProperties.SetName(SearchBox, searchLabel);
            AutomationProperties.SetName(SearchButton, searchLabel);
        }
        UpdateSettingsTabs();

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
        var conversation = ShownConversation();
        var onConversation = conversation is not null;
        TitlePanel.Visibility = Shown(onConversation);
        FileGroup.Visibility = Shown(onConversation);
        TabsBar.Visibility = Shown(onConversation);
        UpdateTabs();
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

    // Tabs

    /// <summary>Selects the model's tab.</summary>
    private void UpdateTabs()
    {
        if (_model is null) return;
        var item = _model.DetailTab switch
        {
            DetailTab.Summary => SummaryTab,
            DetailTab.Chat => ChatTab,
            _ => TranscriptTab,
        };
        if (TabsBar.SelectedItem != item) TabsBar.SelectedItem = item;
    }

    /// <summary>A picked tab shows on the conversation page.</summary>
    private void OnTabSelected()
    {
        if (_model is null) return;
        _model.DetailTab = TabsBar.SelectedItem == SummaryTab ? DetailTab.Summary
            : TabsBar.SelectedItem == ChatTab ? DetailTab.Chat
            : DetailTab.Transcript;
    }

    // Settings

    /// <summary>macOS's Settings toggle: opens Settings, or on Settings goes back to the page it was opened from.</summary>
    private void ToggleSettings()
    {
        if (_model is null) return;
        if (_model.Route.Kind == RouteKind.Settings) _model.GoBack();
        else _model.OpenSettings();
    }

    /// <summary>
    /// The shown section's inner tabs, built again when another section shows (its own tabs, not the old ones'
    /// selection), with the model's tab selected. Hidden off Settings, for a section without tabs, and while the search
    /// lists results in the section's place.
    /// </summary>
    private void UpdateSettingsTabs()
    {
        if (_model is null) return;
        var section = _model.Route.Kind == RouteKind.Settings ? SettingsSearch.Section(_model.SettingsSection) : null;
        var hasTabs = section is { SubTabs.Count: > 0 };
        SettingsTabsBar.Visibility = Shown(hasTabs && _model.SearchTerm.Length == 0);
        if (section is null || !hasTabs) return;
        _syncingSettingsTabs = true;
        try
        {
            if (section.Id != _tabsSection)
            {
                _tabsSection = section.Id;
                SettingsTabsBar.Items.Clear();
                foreach (var tab in section.SubTabs) SettingsTabsBar.Items.Add(new SelectorBarItem { Text = Loc.T(tab.Title), Tag = tab.Id });
            }
            var selected = SettingsTabsBar.Items.FirstOrDefault(i => i.Tag as string == _model.SettingsSubTab);
            if (SettingsTabsBar.SelectedItem != selected) SettingsTabsBar.SelectedItem = selected;
        }
        finally
        {
            _syncingSettingsTabs = false;
        }
    }

    /// <summary>A picked inner tab shows in the section.</summary>
    private void OnSettingsTabSelected()
    {
        if (_syncingSettingsTabs || _model is null || SettingsTabsBar.SelectedItem is not { Tag: string id }) return;
        _model.SetSettingsSubTab(_model.SettingsSection, id);
    }

    // Library chat

    /// <summary>
    /// macOS's Ask across conversations toggle: on Settings it goes back first; opening the chat clears the search, which
    /// would filter the cards it replaces.
    /// </summary>
    private void ToggleGlobalChat()
    {
        if (_model is null) return;
        var open = _model.Route.Kind == RouteKind.Settings || !_model.GlobalChatOpen;
        if (_model.Route.Kind == RouteKind.Settings) _model.GoBack();
        _model.GlobalChatOpen = open;
        if (!open) return;
        _searchTimer?.Stop();
        CloseSearch();
        SearchBox.Text = "";
        _model.SearchTerm = "";
    }

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

    /// <summary>
    /// A confirmation for a page without a window of its own (Settings › Storage): the confirming button and Cancel,
    /// Cancel the default. False when cancelled, when another dialog is open, or when the window closed meanwhile.
    /// </summary>
    private async Task<bool> ConfirmAsync(string title, string message, string confirm)
    {
        if (_model is null || _dialog is not null || XamlRoot is null) return false;
        var dialog = NewDialog(title, message);
        dialog.PrimaryButtonText = confirm;
        dialog.CloseButtonText = Loc.T("Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        var confirmed = false;
        await ShowAsync(dialog, result => confirmed = result == ContentDialogResult.Primary);
        return confirmed;
    }

    // Search

    /// <summary>
    /// The model cleared the search (a Settings result or section was picked): the box follows, and typing not applied
    /// yet is dropped. The box's own clearing, or a blank term, leaves it as it is.
    /// </summary>
    private void OnSearchTermChanged()
    {
        if (_model is null) return;
        UpdateSettingsTabs();
        if (_model.SearchTerm.Length > 0 || SearchBox.Text.Trim().Length == 0) return;
        _searchTimer?.Stop();
        SearchBox.Text = "";
    }

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
    /// (the compact minimum, if the caption buttons take more than expected), and the box again once it fits. A box with
    /// text stays shown, opened in the trailing buttons' place; once the normal box fits again it goes back to it.
    /// </summary>
    private void UpdateSearchMode()
    {
        if (_model is null || double.IsInfinity(_availableWidth) || ToolbarGrid.ActualWidth <= 0) return;
        if (!_searchOpened) _trailingWidth = TrailingColumn.ActualWidth;
        var others = ToolbarGrid.Padding.Left + LeadingColumn.ActualWidth + _trailingWidth
            + TabsColumn.ActualWidth + DragColumn.ActualWidth;
        var asIcon = _availableWidth - others < SearchMinWidth;
        if (_searchOpened)
        {
            if (asIcon) return;
            _searchOpened = false;
        }
        else if (asIcon == _searchAsIcon)
        {
            return;
        }
        else if (asIcon && SearchBox.Text.Length > 0)
        {
            // The filter stays visible; LostFocus and Esc close the box once it is empty.
            _searchOpened = true;
        }
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

    /// <summary>Ctrl+F (macOS <c>showSearch</c>): the focus goes to the page's search box, opened first where Search is an icon.</summary>
    public void FocusSearch()
    {
        if (SearchBox.Visibility == Visibility.Visible) SearchBox.Focus(FocusState.Programmatic);
        else OpenSearch();
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

    /// <summary>Shows a recording problem that waits, else a cloud confirmation that waits, unless a dialog is open.</summary>
    private void ShowNextDialog()
    {
        if (_model is null || _dialog is not null || XamlRoot is null) return;
        if (_model.RecordingError is { } error) _ = ShowRecordingProblemAsync(error);
        else if (_model.PendingCloudRuns.FirstOrDefault() is { } run) _ = ShowCloudConsentAsync(run);
        else if (_model.PendingEmbeddingConsent is { } consent) _ = ShowEmbeddingConsentAsync(consent);
    }

    /// <summary>
    /// "Send transcripts to …?" (macOS RAGSettingsView alert): Understood remembers the destination and indexes; Cancel
    /// sends nothing.
    /// </summary>
    private Task ShowEmbeddingConsentAsync(PendingEmbeddingConsent consent) =>
        ShowAsync(CloudConsent.EmbeddingDialog(XamlRoot, consent),
            result => _model?.ResolveCloudEmbeddingConsent(result == ContentDialogResult.Primary));

    /// <summary>
    /// "Send audio to …?" (macOS MainView.swift alert on pendingCloudRuns): Send submits every run for that destination
    /// and remembers it; Cancel drops them, uploading nothing.
    /// </summary>
    private Task ShowCloudConsentAsync(PendingCloudRun run) =>
        ShowAsync(CloudConsent.Dialog(XamlRoot, run), result => _model?.ResolvePendingCloudRuns(result == ContentDialogResult.Primary));

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
