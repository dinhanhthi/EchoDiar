using System.ComponentModel;
using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.SystemServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace EchoDiar.App.Views;

/// <summary>
/// The window's header (macOS MainView.swift toolbar) in two rows. The title row is the window's title bar: the logo,
/// then the conversation's title with its date and duration, or the page's name (Settings, Calendar, Recording while
/// recording, else EchoDiar). The toolbar row: the sidebar button on the library pages (list, conversation, recording),
/// which hides or shows the library sidebar; Back, always shown and enabled off the list (and on it while the library
/// chat shows); Record with its menu of targets and the language (Stop, blinking, and Pause or Resume while recording);
/// Reveal and Delete, always shown and enabled on a conversation; Settings (opens the Settings page, or goes back from
/// it), Calendar (the same for the Calendar page), Ask across conversations (opens or closes the library chat); Update
/// while an App Installer update is available and nothing runs that installing would cut off (macOS hides it while
/// recording). At the right edge: a conversation's tabs (a SelectorBar) or a Settings section's inner tabs. It also
/// hosts the window's dialogs (it lives as long as the window): the delete confirmations (of this conversation, and of
/// the library's cards and selection), "Recording problem", the cloud confirmations (audio for transcription,
/// transcripts for the library index), the update's confirmation and answers, and the Settings pages' confirmations
/// (<see cref="AppViewModel.Confirm"/>), one at a time. The lost-mic question is the pill's end prompt.
/// </summary>
public sealed partial class TitleBarToolbar : UserControl
{
    private const string PauseGlyph = "";
    private const string ResumeGlyph = "";

    /// <summary>The recording indicator dims every other second (macOS RecordingBlink).</summary>
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromSeconds(1);

    private static readonly UISettings UISettings = new();

    private AppViewModel? _model;

    /// <summary>The open dialog: WinUI shows one at a time per window.</summary>
    private ContentDialog? _dialog;

    private DispatcherQueueTimer? _blinkTimer;

    /// <summary>The Settings section the inner tabs were built for.</summary>
    private string? _tabsSection;

    /// <summary>The inner tabs or their selection are being set from the model: not a pick.</summary>
    private bool _syncingSettingsTabs;

    /// <summary>The tabs or their selection are being set from the model: not a pick.</summary>
    private bool _syncingTabs;

    /// <summary>Whether the middle tab was last labeled for the recording (Catch-up) or the conversation (Summary).</summary>
    private bool? _shownRecordingTabs;

    /// <summary>Record's tooltip, "Record" and the target; set again only when the target changes.</summary>
    private string? _recordTip;

    /// <summary>The pause state Pause or Resume was last labeled for.</summary>
    private bool? _shownPaused;

    /// <summary>The sidebar state the sidebar button was last labeled for.</summary>
    private bool? _shownSidebarHidden;

    /// <summary>The Record menu's end: a separator and the Language submenu, kept while the targets are rebuilt.</summary>
    private readonly MenuFlyoutSeparator _languageSeparator = new();
    private readonly MenuFlyoutSubItem _languageItem = new() { Text = Loc.T("Language") };

    public TitleBarToolbar()
    {
        InitializeComponent();
        SidebarButton.Click += (_, _) => _model?.ToggleSidebar();
        BackButton.Click += (_, _) => _model?.GoBack();
        SettingsButton.Click += (_, _) => ToggleSettings();
        CalendarButton.Click += (_, _) => ToggleCalendar();
        AskButton.Click += (_, _) => ToggleGlobalChat();
        RecordSplitButton.Click += (_, _) => _ = _model?.StartRecordingAsync(_model.SelectedTarget);
        TargetMenu.Items.Add(_languageSeparator);
        TargetMenu.Items.Add(_languageItem);
        TargetMenu.Opening += (_, _) => OnTargetMenuOpening();
        StopButton.Click += (_, _) => _ = _model?.StopRecordingAsync();
        PauseButton.Click += (_, _) => _ = _model?.TogglePauseAsync();
        RevealButton.Click += (_, _) => Reveal();
        UpdateButton.Click += (_, _) => _ = ConfirmUpdateAsync();
        DeleteMenu.Opening += (_, _) => UpdateDeleteMenu();
        DeleteAudioItem.Click += async (_, _) => await DeleteShownAsync(audioOnly: true);
        DeleteConversationItem.Click += async (_, _) => await DeleteShownAsync(audioOnly: false);
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
        model.Updater.PropertyChanged += OnUpdaterChanged;
        // Settings pages ask through here too, so the window never has two dialogs.
        model.Confirm = ConfirmAsync;
        model.DeleteFlow = (conversations, audioOnly) => DeleteAsync(conversations, audioOnly);
        _blinkTimer = DispatcherQueue.CreateTimer();
        _blinkTimer.Interval = BlinkInterval;
        _blinkTimer.Tick += (_, _) => Blink();
        UpdateSidebarButton();
        Update();
    }

    /// <summary>The window closed: its controls fail every call, so the model's changes no longer reach them.</summary>
    public void Detach()
    {
        if (_model is null) return;
        _model.PropertyChanged -= OnModelChanged;
        _model.Updater.PropertyChanged -= OnUpdaterChanged;
        _model.Confirm = null;
        _model.DeleteFlow = null;
        _model = null;
        _blinkTimer?.Stop();
        _blinkTimer = null;
    }

    /// <summary>The title row, the window's title bar (<c>MainWindow.SetTitleBar</c>): all of it drags the window.</summary>
    public FrameworkElement TitleBar => TitleBarGrid;

    /// <summary>Keeps the title off the caption buttons (the title bar's insets, in view pixels).</summary>
    public void SetCaptionInsets(double left, double right)
    {
        SetWidth(LeftInsetColumn, left);
        SetWidth(RightInsetColumn, right);
    }

    /// <summary>GridLength throws on a negative, NaN or infinite width: those set 0, so LayoutUpdated never throws.</summary>
    private static void SetWidth(ColumnDefinition column, double width)
    {
        var length = new GridLength(double.IsFinite(width) && width > 0 ? width : 0);
        if (!column.Width.Equals(length))
        {
            column.Width = length;
        }
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
            case nameof(AppViewModel.RecordingTab):
            // The recording layout: Tabs shows the recording's tabs here, side by side none.
            case nameof(AppViewModel.Settings):
                UpdateTabs();
                break;
            case nameof(AppViewModel.AIJobs):
                UpdateUpdateButton();
                break;
            case nameof(AppViewModel.SettingsSection):
            case nameof(AppViewModel.SettingsSubTab):
            case nameof(AppViewModel.SettingsSearchTerm):
                UpdateSettingsTabs();
                break;
            case nameof(AppViewModel.SidebarHidden):
                UpdateSidebarButton();
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
        // The library pages (macOS Page.library: the list, a conversation, the recording).
        SidebarButton.Visibility = Shown(_model.Route.Kind is RouteKind.List or RouteKind.Conversation or RouteKind.Recording);
        BackButton.IsEnabled = _model.Route.Kind != RouteKind.List || _model.GlobalChatOpen;
        UpdateSettingsTabs();

        var recording = _model.IsRecording;
        var paused = _model.RecordingSnapshot.State == RecordingEngine.State.Paused;
        RecordSplitButton.Visibility = Shown(!recording);
        // Installing the update closes the app: no recording starts meanwhile.
        var installing = _model.Updater.IsInstalling;
        RecordSplitButton.IsEnabled = !_model.IsStarting && !installing;
        var recordTip = installing ? Loc.T("Installing the update…") : Loc.Format("Record %@", _model.SelectedTarget?.Name ?? Loc.T("All system audio"));
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
        UpdateUpdateButton();
        var conversation = ShownConversation();
        var onConversation = conversation is not null;
        RevealButton.IsEnabled = onConversation;
        DeleteButton.IsEnabled = conversation is not null && _model.CanDelete(conversation);
        UpdateTabs();
        TitleText.Text = conversation?.Title ?? _model.Route.Kind switch
        {
            RouteKind.Settings => Loc.T("Settings"),
            RouteKind.Calendar => Loc.T("Calendar"),
            RouteKind.Recording when recording => Loc.T("Recording"),
            _ => "EchoDiar",
        };
        DetailText.Text = conversation is null ? "" : ConversationText.When(conversation);
        DetailText.Visibility = Shown(onConversation);
    }

    /// <summary>The conversation page's conversation, while it exists; null on the other pages.</summary>
    private Conversation? ShownConversation() =>
        _model?.Route is { Kind: RouteKind.Conversation, ConversationId: { } id }
            ? _model.Conversations.FirstOrDefault(c => c.Id == id)
            : null;

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"Hide Sidebar" or "Show Sidebar", set again only when the state changes.</summary>
    private void UpdateSidebarButton()
    {
        if (_model is null || _model.SidebarHidden == _shownSidebarHidden) return;
        _shownSidebarHidden = _model.SidebarHidden;
        var label = Loc.T(_model.SidebarHidden ? "Show Sidebar" : "Hide Sidebar");
        AutomationProperties.SetName(SidebarButton, label);
        ToolTipService.SetToolTip(SidebarButton, label);
    }

    // Tabs

    /// <summary>
    /// The recording page's tabs take the conversation's place (macOS MainView <c>recordingTabs</c>): on the recording
    /// page, while recording, in the Tabs layout; side by side has its own Catch-up | Chat on the page.
    /// </summary>
    private bool RecordingTabsShown() =>
        _model is { Route.Kind: RouteKind.Recording, IsRecording: true }
        && _model.Settings.RecordingLayout == RecordingLayout.Tabs;

    /// <summary>
    /// Shows the conversation's or the recording's tabs, the middle one labeled Summary or Catch-up, with the model's
    /// tab selected; each set only when it changes (the recording's snapshot calls this at 15 Hz).
    /// </summary>
    private void UpdateTabs()
    {
        if (_model is null) return;
        var recordingTabs = RecordingTabsShown();
        var visibility = Shown(recordingTabs || ShownConversation() is not null);
        if (TabsBar.Visibility != visibility) TabsBar.Visibility = visibility;
        var item = recordingTabs
            ? _model.RecordingTab switch
            {
                RecordingTab.CatchUp => SummaryTab,
                RecordingTab.Chat => ChatTab,
                _ => TranscriptTab,
            }
            : _model.DetailTab switch
            {
                DetailTab.Summary => SummaryTab,
                DetailTab.Chat => ChatTab,
                _ => TranscriptTab,
            };
        _syncingTabs = true;
        try
        {
            if (recordingTabs != _shownRecordingTabs)
            {
                _shownRecordingTabs = recordingTabs;
                SummaryTab.Text = recordingTabs ? Loc.T("Catch-up") : Loc.T("Summary");
            }
            if (TabsBar.SelectedItem != item) TabsBar.SelectedItem = item;
        }
        finally
        {
            _syncingTabs = false;
        }
    }

    /// <summary>A picked tab shows on the recording page, or else on the conversation page.</summary>
    private void OnTabSelected()
    {
        if (_syncingTabs || _model is null || TabsBar.Visibility != Visibility.Visible
            || TabsBar.SelectedItem is not { } item) return;
        if (RecordingTabsShown())
        {
            _model.RecordingTab = item == SummaryTab ? RecordingTab.CatchUp
                : item == ChatTab ? RecordingTab.Chat
                : RecordingTab.Transcript;
        }
        else
        {
            _model.DetailTab = item == SummaryTab ? DetailTab.Summary
                : item == ChatTab ? DetailTab.Chat
                : DetailTab.Transcript;
        }
    }

    // Update

    private void OnUpdaterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Updater.Report)) ShowNextDialog();
        else UpdateUpdateButton();
        // The Record button is off while the update installs.
        if (e.PropertyName == nameof(Updater.IsInstalling)) Update();
    }

    /// <summary>
    /// macOS <c>showsUpdate</c>, also hidden while work runs that installing would cut off; shown while installing, with
    /// a ring for how far it is.
    /// </summary>
    private void UpdateUpdateButton()
    {
        if (_model is null) return;
        var updater = _model.Updater;
        var installing = updater.IsInstalling;
        UpdateButton.Visibility = Shown(installing || updater.UpdateAvailable && !_model.UpdateBlocked);
        UpdateIcon.Visibility = Shown(!installing);
        UpdateProgress.Visibility = Shown(installing);
        UpdateProgress.Value = updater.InstallPercent;
        var tip = installing ? Loc.Format("Installing the update… %lld%%", updater.InstallPercent) : Loc.T("Install the available update");
        if (ToolTipService.GetToolTip(UpdateButton) as string != tip) ToolTipService.SetToolTip(UpdateButton, tip);
    }

    /// <summary>
    /// Update: asks first, then installs (Windows closes EchoDiar and opens the new version). A Store update asks in the
    /// Store's own dialog, so it installs without this one. Work that started meanwhile is not cut off: it is said instead.
    /// </summary>
    private async Task ConfirmUpdateAsync()
    {
        if (_model is not { } model || _dialog is not null || XamlRoot is null || model.Updater.IsInstalling) return;
        if (model.UpdateBlocked)
        {
            await ShowUpdateBlockedAsync();
            return;
        }
        if (model.Updater.Source == UpdateSource.Store)
        {
            if (!await model.InstallUpdateAsync() && _model is not null) await ShowUpdateBlockedAsync();
            return;
        }
        var dialog = NewDialog(Loc.T("Install the update?"), Loc.T("EchoDiar closes, installs the new version and opens again."));
        dialog.PrimaryButtonText = Loc.T("Install and Relaunch");
        dialog.CloseButtonText = Loc.T("Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        var confirmed = false;
        await ShowAsync(dialog, result => confirmed = result == ContentDialogResult.Primary);
        if (!confirmed || _model is null) return;
        if (!await model.InstallUpdateAsync() && _model is not null) await ShowUpdateBlockedAsync();
    }

    /// <summary>A recording or a job runs: the update waits (macOS hides Update while recording).</summary>
    private Task ShowUpdateBlockedAsync()
    {
        if (_dialog is not null || XamlRoot is null) return Task.CompletedTask;
        var dialog = NewDialog(Loc.T("Update after the current work"),
            Loc.T("EchoDiar installs the update once the recording, transcriptions and AI answers have finished."));
        dialog.CloseButtonText = Loc.T("OK");
        dialog.DefaultButton = ContentDialogButton.Close;
        return ShowAsync(dialog, _ => { });
    }

    /// <summary>The answer to the user's check or a failed install; a found update is offered at once.</summary>
    private Task ShowUpdateReportAsync(UpdateReport report)
    {
        if (_model is not { } model) return Task.CompletedTask;
        if (report.Kind == UpdateReportKind.Available)
        {
            model.Updater.DismissReport();
            return ConfirmUpdateAsync();
        }
        var (title, message) = report.Kind switch
        {
            UpdateReportKind.UpToDate => (Loc.T("You're up to date!"),
                Loc.Format("EchoDiar %@ is currently the newest version available.", AboutSettingsPage.Version)),
            UpdateReportKind.CheckFailed => (Loc.T("Could not check for updates"), report.Reason ?? Loc.T("Try again later.")),
            _ => (Loc.T("Could not install the update"), report.Reason ?? Loc.T("Try again later.")),
        };
        var dialog = NewDialog(title, message);
        dialog.CloseButtonText = Loc.T("OK");
        dialog.DefaultButton = ContentDialogButton.Close;
        return ShowAsync(dialog, _ => _model?.Updater.DismissReport());
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
    /// selection), with the model's tab selected. Hidden off Settings, for a section without tabs, and while the Settings
    /// search lists results in the section's place.
    /// </summary>
    private void UpdateSettingsTabs()
    {
        if (_model is null) return;
        var section = _model.Route.Kind == RouteKind.Settings ? SettingsSearch.Section(_model.SettingsSection) : null;
        var hasTabs = section is { SubTabs.Count: > 0 };
        SettingsTabsBar.Visibility = Shown(hasTabs && _model.SettingsSearchTerm.Length == 0);
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

    // Calendar

    /// <summary>macOS's Calendar toggle: opens the Calendar page, or on it goes back to the page it was opened from.</summary>
    private void ToggleCalendar()
    {
        if (_model is null) return;
        if (_model.Route.Kind == RouteKind.Calendar) _model.GoBack();
        else _model.OpenCalendar();
    }

    // Library chat

    /// <summary>
    /// macOS's Ask across conversations toggle: on Settings or the Calendar page it goes back first (twice from Settings
    /// opened from the Calendar page); opening the chat clears the search, which would filter the cards it replaces.
    /// </summary>
    private void ToggleGlobalChat()
    {
        if (_model is null) return;
        var open = _model.Route.Kind is RouteKind.Settings or RouteKind.Calendar || !_model.GlobalChatOpen;
        if (_model.Route.Kind == RouteKind.Settings) _model.GoBack();
        if (_model.Route.Kind == RouteKind.Calendar) _model.GoBack();
        _model.GlobalChatOpen = open;
        // The library's search box follows the cleared term (ConversationListView).
        if (open) _model.SearchTerm = "";
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
            TargetMenu.Items.Insert(index++, TargetItem(group.NeedsGlobalTap ? Loc.Format("%@ (records all system audio)", group.Name) : group.Name, group));
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
    /// when any was deleted (not cancelled, not none). <paramref name="replacement"/>: the conversation that opens in place of
    /// the open one when it is deleted (a delete from the keyboard), instead of the list.
    /// </summary>
    public async Task<bool> DeleteAsync(IReadOnlyList<Conversation> conversations, bool audioOnly, Guid? replacement = null)
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
        // Gone, or deleted with them, while the dialog was open: the list.
        if (replacement is { } next && (ids.Contains(next) || !model.Conversations.Any(c => c.Id == next))) replacement = null;
        // Each delete first has the open player let go of that conversation's audio (ReleasingAudio).
        foreach (var id in ids)
        {
            if (audioOnly) await model.DeleteAudioAsync(id);
            else await model.DeleteConversationAsync(id, replacement);
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
            ({ } c, false) => Loc.Format("Delete \"%@\"?", c.Title),
            (null, true) => Loc.Plural("Delete the audio of %lld conversations?", conversations.Count),
            (null, false) => Loc.Plural("Delete %lld conversations?", conversations.Count),
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

    // Recording dialogs and indicator

    /// <summary>
    /// Shows a recording problem that waits, else a cloud confirmation that waits, else the update's answer, unless a
    /// dialog is open.
    /// </summary>
    private void ShowNextDialog()
    {
        if (_model is null || _dialog is not null || XamlRoot is null) return;
        if (_model.RecordingError is { } error) _ = ShowRecordingProblemAsync(error);
        else if (_model.PendingCloudRuns.FirstOrDefault() is { } run) _ = ShowCloudConsentAsync(run);
        else if (_model.PendingEmbeddingConsent is { } consent) _ = ShowEmbeddingConsentAsync(consent);
        else if (_model.Updater.Report is { } report) _ = ShowUpdateReportAsync(report);
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

    /// <summary>
    /// Shows a page's own dialog (the event editor) as the open one, so the dialogs above wait for it; false when
    /// another dialog is open.
    /// </summary>
    internal async Task<bool> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialog is not null || XamlRoot is null) return false;
        await ShowAsync(dialog, _ => { });
        return true;
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
