using System.Diagnostics;
using EchoDiar.AI.Cloud;
using EchoDiar.App.Services;
using EchoDiar.App.Views;
using EchoDiar.Core;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using Microsoft.UI.Dispatching;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// The Settings page's state (macOS AppModel <c>settingsTab</c>, <c>settingsSubTabs</c>, <c>openSettings</c>): the
/// section shown, each section's inner tab, the page Back returns to, and the settings changes Settings makes, saved
/// 300 ms after the last one.
/// </summary>
public sealed partial class AppViewModel
{
    /// <summary>Settings saves once its changes pause this long (a slider or typing makes many).</summary>
    private static readonly TimeSpan SettingsSaveDelay = TimeSpan.FromMilliseconds(300);

    private string _settingsSection = SettingsSearch.General;

    /// <summary>The inner tab each section shows; a section missing here shows its first.</summary>
    private readonly Dictionary<string, string> _settingsSubTabs = [];

    /// <summary>The page Settings was opened from.</summary>
    private Route _settingsReturnRoute = Route.List;

    private string _settingsLocalAIGroup = ProvidersSettingsPage.LanguageModelsGroup;

    private DispatcherQueueTimer? _settingsSaveTimer;
    private SettingsViewModel? _settingsModel;

    /// <summary>What the Settings pages bind to; one for the app's lifetime.</summary>
    public SettingsViewModel SettingsModel => _settingsModel ??= new SettingsViewModel(this, _services.LaunchAtLogin);

    /// <summary>The folder of every recording (macOS <c>RecordingSession.conversationsRoot</c>), Settings' data folder.</summary>
    public string ConversationsFolder => _services.Paths.Conversations;

    /// <summary>
    /// Bytes on disk by kind (macOS <c>storageUsage</c>): the library's folders and every installed model, measured off
    /// the UI thread.
    /// </summary>
    public Task<LibraryStore.Usage> StorageUsageAsync() => _services.Library.UsageAsync(_services.Models.TotalInstalledBytes);

    /// <summary>The id of the Settings section shown (<see cref="SettingsSearch.Sections"/>).</summary>
    public string SettingsSection
    {
        get => _settingsSection;
        private set
        {
            if (SetProperty(ref _settingsSection, value)) OnPropertyChanged(nameof(SettingsSubTab));
        }
    }

    /// <summary>The inner tab the shown section shows; null for a section without tabs.</summary>
    public string? SettingsSubTab => SubTabOf(SettingsSection);

    /// <summary>The inner tab <paramref name="section"/> shows: the one picked last, else its first; null without tabs.</summary>
    public string? SubTabOf(string section) =>
        _settingsSubTabs.TryGetValue(section, out var subTab) ? subTab : SettingsSearch.Section(section)?.SubTabs.FirstOrDefault()?.Id;

    /// <summary>Shows <paramref name="subTab"/> of <paramref name="section"/> (the toolbar's tabs, a deep link).</summary>
    public void SetSettingsSubTab(string section, string subTab)
    {
        if (SettingsSearch.Section(section) is not { } found || found.SubTabs.All(t => t.Id != subTab)) return;
        if (SubTabOf(section) == subTab) return;
        _settingsSubTabs[section] = subTab;
        if (section == SettingsSection) OnPropertyChanged(nameof(SettingsSubTab));
    }

    /// <summary>
    /// The group AI Providers › Local AI shows (macOS <c>settingsLocalAIGroup</c>, a <c>ProvidersSettingsPage.*Group</c>
    /// id): picked in its tabs, kept in memory for the app's lifetime, Language models at first.
    /// </summary>
    public string SettingsLocalAIGroup
    {
        get => _settingsLocalAIGroup;
        set => SetProperty(ref _settingsLocalAIGroup, value);
    }

    /// <summary>
    /// Settings › AI Providers › Local AI on <paramref name="group"/> (macOS <c>openLocalAI</c>: a "Download" link, RAG's
    /// Set Up for a local host).
    /// </summary>
    public void OpenLocalAI(string group)
    {
        OpenSettings(SettingsSearch.Providers, ProvidersSettingsPage.LocalAITab);
        SettingsLocalAIGroup = group;
    }

    /// <summary>
    /// The Settings page, on <paramref name="section"/> and its inner tab <paramref name="subTab"/> when given (a deep
    /// link, a search result); unknown ids are ignored. Back returns to the page it was opened from.
    /// </summary>
    public void OpenSettings(string? section = null, string? subTab = null)
    {
        if (section is not null && SettingsSearch.Section(section) is not null) SettingsSection = section;
        if (subTab is not null) SetSettingsSubTab(SettingsSection, subTab);
        if (Route.Kind != RouteKind.Settings) _settingsReturnRoute = Route;
        Route = Route.Settings;
    }

    /// <summary>The page <paramref name="saved"/> (Settings' or the Calendar page's), or the list when that conversation is
    /// gone or that recording ended.</summary>
    private Route ReturnRoute(Route saved) => saved switch
    {
        { Kind: RouteKind.Conversation, ConversationId: { } id } when Conversations.All(c => c.Id != id) => Route.List,
        { Kind: RouteKind.Recording } when !IsRecording => Route.List,
        { Kind: RouteKind.Settings } => Route.List,
        var route => route,
    };

    /// <summary>
    /// Applies <paramref name="settings"/> at once (the screens and the next recording read them) and saves them 300 ms
    /// after the last change, through the same ordered writes as every other settings change. Turning auto-delete on or
    /// off starts or stops its schedule at once, a changed global shortcut is registered at once, a changed embedder
    /// restarts the library index, turning the end prompt's notification off removes a shown one, and an explicit pick
    /// of an auto-excluded provider lets automatic picks use it again (macOS <c>updateSettings</c>).
    /// </summary>
    internal void ApplySettings(AppSettings settings)
    {
        if (_closing) return;
        settings = Unexcluding(Settings, settings);
        if (settings == Settings) return;
        var retentionChanged = settings.AutoDeleteEnabled != Settings.AutoDeleteEnabled;
        var hotKeysChanged = HotKeysChanged(Settings, settings);
        var ragChanged = RagChanged(Settings, settings);
        var notificationTurnedOff = !settings.EndPromptNotification && Settings.EndPromptNotification;
        Settings = settings;
        if (retentionChanged) SyncRetention();
        if (hotKeysChanged) SyncHotKeys();
        if (ragChanged) SyncLibraryIndex(restart: true);
        if (notificationTurnedOff) EndPromptNotifier.Dismiss();
        if (_settingsSaveTimer is null)
        {
            _settingsSaveTimer = _dispatcher.CreateTimer();
            _settingsSaveTimer.Interval = SettingsSaveDelay;
            _settingsSaveTimer.IsRepeating = false;
            _settingsSaveTimer.Tick += (_, _) => QueueSettingsWrite();
        }
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    /// <summary>
    /// <paramref name="updated"/> without the auto-excluded ids that one of its picks now points at, where that pick
    /// changed from <paramref name="old"/>: the engine and diarizer (<c>cloud:&lt;id&gt;</c>), the summary, chat and
    /// catch-up providers (the id) and the embedder (<c>api:&lt;id&gt;</c>).
    /// </summary>
    private static AppSettings Unexcluding(AppSettings old, AppSettings updated)
    {
        if (updated.AutoExcludedProviderIDs.Count == 0) return updated;
        List<string?> picked = [];
        if (updated.DefaultASRModelID != old.DefaultASRModelID) picked.Add(CloudTranscription.ProviderId(updated.DefaultASRModelID));
        if (updated.DefaultDiarizerModelID != old.DefaultDiarizerModelID)
            picked.Add(CloudTranscription.ProviderId(updated.DefaultDiarizerModelID));
        if (updated.SummaryAIProviderID != old.SummaryAIProviderID) picked.Add(updated.SummaryAIProviderID);
        if (updated.ChatAIProviderID != old.ChatAIProviderID) picked.Add(updated.ChatAIProviderID);
        if (updated.CatchUpProviderID != old.CatchUpProviderID) picked.Add(updated.CatchUpProviderID);
        if (updated.EmbeddingModelID != old.EmbeddingModelID
            && updated.EmbeddingModelID.StartsWith(ModelCatalog.ApiEmbedderPrefix, StringComparison.Ordinal))
            picked.Add(updated.EmbeddingModelID[ModelCatalog.ApiEmbedderPrefix.Length..]);
        var ids = picked.OfType<string>().Where(id => id.Length > 0 && updated.AutoExcludedProviderIDs.Contains(id)).ToList();
        if (ids.Count == 0) return updated;
        var excluded = new HashSet<string>(updated.AutoExcludedProviderIDs, StringComparer.OrdinalIgnoreCase);
        excluded.ExceptWith(ids);
        return updated with { AutoExcludedProviderIDs = excluded };
    }

    /// <summary>Writes a change still waiting for its delay now (the window closes).</summary>
    private void FlushSettings()
    {
        if (_settingsSaveTimer is { IsRunning: true }) QueueSettingsWrite();
    }

    /// <summary>
    /// Saves the current settings off the UI thread after the writes queued before: every write is the whole file as it
    /// was when queued, so the last one has every change. A change waiting for its delay is part of it.
    /// </summary>
    private void QueueSettingsWrite()
    {
        _settingsSaveTimer?.Stop();
        var settings = Settings;
        _settingsWrite = _settingsWrite.ContinueWith(_ =>
        {
            try
            {
                _services.Settings.Save(settings);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not save the settings: {e}");
            }
        }, TaskScheduler.Default);
    }
}
