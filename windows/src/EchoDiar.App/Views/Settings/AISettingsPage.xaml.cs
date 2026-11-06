using System.ComponentModel;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › AI Settings (macOS AISettingsView.swift), the Chat, Summary, Catch-up and Library chat inner tabs; the title
/// bar picks the tab (<see cref="AppViewModel.SubTabOf"/>). Chat and Summary each pick their own default model (macOS
/// <c>AIModelPicker</c>, <see cref="AIPurpose"/>) and share the AI answer language; Chat also has the chat context.
/// Catch-up is where "Catch up" runs while recording. Library chat is the RAG page (<see cref="RAGSettingsPage"/>), whose
/// providers and keys changed elsewhere are picked up each time it is shown (macOS RAGSettingsView <c>shown</c>). Every
/// change goes through <see cref="ProvidersViewModel"/> and is saved after the settings delay.
/// </summary>
public sealed partial class AISettingsPage : UserControl
{
    // macOS ids (SettingsTab.aiSettings.subTabs); the Transcription tab comes later.
    public const string ChatTab = "chat";
    public const string SummaryTab = "summary";
    public const string CatchUpTab = "catchUp";
    public const string LibraryTab = "library";

    /// <summary>macOS <c>SettingsTab.aiSettings.subTabs</c>.</summary>
    public static IReadOnlyList<SettingsSubTab> SubTabs { get; } =
    [
        new(ChatTab, "Chat"),
        new(SummaryTab, "Summary"),
        new(CatchUpTab, "Catch-up"),
        new(LibraryTab, "Library chat"),
    ];

    /// <summary>The searchable rows (macOS SettingsSearch.swift, AI Settings), each the Tag of its element.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Chat model", ["chat", "questions", "LLM", "default model", "library chat"], ChatTab),
        new("Chat context", ["chat", "history", "context", "messages", "memory"], ChatTab),
        new("AI answer language", ["language", "output language", "answers", "summaries"], ChatTab),
        new("Summary model", ["summary", "summaries", "LLM", "default model"], SummaryTab),
        new("AI answer language", ["language", "output language", "answers", "summaries"], SummaryTab),
        new("Catch-up", ["catch up", "live summary", "recording", "meeting so far"], CatchUpTab),
        new("Catch-up provider", ["catch up", "live summary", "cloud", "CLI", "local server"], CatchUpTab),
        new("Catch-up model", ["catch up", "live summary", "LLM"], CatchUpTab),
        new("Re-summarize from the start each time", ["catch up", "incremental", "whole recording", "from start"], CatchUpTab),
        .. RAGSettingsPage.SearchEntries,
    ];

    /// <summary>The summary languages offered (macOS <c>AILanguageMenu</c>), plus a hand-edited one.</summary>
    private static readonly (string Code, string Name)[] Languages =
        [("auto", "Auto"), ("en", "English"), ("fr", "French"), ("vi", "Vietnamese")];

    private readonly AppViewModel _model;
    private readonly ProvidersViewModel _providers;

    /// <summary>Library chat's page, shown on its tab.</summary>
    private readonly RAGSettingsPage _library;

    private readonly ModelPicker _chatModel;
    private readonly ModelPicker _summaryModel;

    /// <summary>A picked cloud catch-up provider's key status.</summary>
    private readonly ApiKeyStatus _catchUpKeyStatus;

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    /// <summary>Library chat showed at the last check: it is refreshed when it becomes shown (macOS <c>onChange(of: shown)</c>).</summary>
    private bool _libraryShown;

    /// <summary>Each language box's codes as listed, rebuilt only when these change.</summary>
    private readonly Dictionary<ComboBox, List<string>> _languageCodes = [];
    private List<int> _chatContextValues = [];

    /// <summary>The catch-up pickers' values as listed ("" = Automatic) and their titles, rebuilt only when these change.</summary>
    private List<string> _catchUpProviderIds = [];
    private List<string> _catchUpProviderTitles = [];
    private List<string> _catchUpModels = [];

    public AISettingsPage(AppViewModel model)
    {
        _model = model;
        _providers = model.ProvidersModel;
        InitializeComponent();
        _library = new RAGSettingsPage(model);
        LibraryHost.Children.Add(_library);
        var languageNote = Loc.T("Auto = the transcript's language for summaries, the question's for answers.");
        ChatLanguageCard.Description = languageNote;
        SummaryLanguageCard.Description = languageNote;
        _chatModel = new ModelPicker(ChatModelCombo, _providers, AIPurpose.Chat, () => _syncing);
        _summaryModel = new ModelPicker(SummaryModelCombo, _providers, AIPurpose.Summary, () => _syncing);
        _catchUpKeyStatus = new ApiKeyStatus(model, (Style)Resources["SuccessIconStyle"]);
        CatchUpKeyCard.Content = _catchUpKeyStatus.Element;

        ChatLanguageCombo.SelectionChanged += (_, _) => OnLanguagePicked(ChatLanguageCombo);
        SummaryLanguageCombo.SelectionChanged += (_, _) => OnLanguagePicked(SummaryLanguageCombo);
        ChatContextCombo.SelectionChanged += (_, _) =>
        {
            var index = ChatContextCombo.SelectedIndex;
            if (_syncing || index < 0 || index >= _chatContextValues.Count
                || _chatContextValues[index] == _providers.ChatContextMessages) return;
            _providers.ChatContextMessages = _chatContextValues[index];
        };
        CatchUpProviderCombo.SelectionChanged += (_, _) =>
        {
            var index = CatchUpProviderCombo.SelectedIndex;
            if (_syncing || index < 0 || index >= _catchUpProviderIds.Count
                || _catchUpProviderIds[index] == ShownCatchUpProviderId(CatchUpProviders())) return;
            _providers.SetCatchUpProvider(_catchUpProviderIds[index]);
        };
        CatchUpModelCombo.SelectionChanged += (_, _) =>
        {
            var index = CatchUpModelCombo.SelectedIndex;
            if (_syncing || index < 0 || index >= _catchUpModels.Count || _catchUpModels[index] == _providers.CatchUpModel) return;
            _providers.CatchUpModel = _catchUpModels[index];
        };
        CatchUpFromStartToggle.Toggled += (_, _) =>
        {
            if (_syncing || CatchUpFromStartToggle.IsOn == _providers.CatchUpFromStart) return;
            _providers.CatchUpFromStart = CatchUpFromStartToggle.IsOn;
        };

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _providers.Changed += Refresh;
            Refresh();
            ShowTab();
            // The saved keys read again, for the catch-up key status (Changed follows).
            _ = _providers.RefreshKeysAsync();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _providers.Changed -= Refresh;
            _libraryShown = false;
        };
    }

    private bool Closing => _model.IsClosing;

    private string? Tab => _model.SubTabOf(SettingsSearch.AISettings);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SettingsSubTab)) ShowTab();
    }

    /// <summary>
    /// The picked tab's panel, in the same frame (click first). Library chat, once it becomes shown, reads the providers,
    /// keys and models changed elsewhere again: the tabs switch by visibility, so its page's Loaded does not run then.
    /// </summary>
    private void ShowTab()
    {
        if (Closing) return;
        var tab = Tab;
        var library = tab == LibraryTab;
        FormScroller.Visibility = Show(!library);
        LibraryHost.Visibility = Show(library);
        ChatPanel.Visibility = Show(tab == ChatTab);
        SummaryPanel.Visibility = Show(tab == SummaryTab);
        CatchUpPanel.Visibility = Show(tab == CatchUpTab);

        // Called while the page is loaded only (Loaded, then the model's changes until Unloaded).
        var shown = library && _model.SettingsSection == SettingsSearch.AISettings;
        if (shown && !_libraryShown) _library.Shown();
        _libraryShown = shown;
    }

    /// <summary>Every control shows the current settings and providers.</summary>
    private void Refresh()
    {
        if (Closing) return;
        _syncing = true;
        try
        {
            _chatModel.Refresh();
            _summaryModel.Refresh();
            RefreshLanguages(ChatLanguageCombo);
            RefreshLanguages(SummaryLanguageCombo);
            RefreshChatContext();
            RefreshCatchUp();
        }
        finally
        {
            _syncing = false;
        }
    }

    // Chat and summary

    /// <summary>
    /// <c>summaryLanguage</c>, one setting for summaries and answers, with the options of the AI pane's language menu
    /// (macOS <c>languagePicker</c>), plus a hand-edited code.
    /// </summary>
    private void RefreshLanguages(ComboBox combo)
    {
        var selection = _providers.SummaryLanguage;
        List<string> codes = [.. Languages.Select(l => l.Code)];
        if (!codes.Contains(selection)) codes.Add(selection);
        if (!_languageCodes.TryGetValue(combo, out var listed) || !codes.SequenceEqual(listed))
        {
            _languageCodes[combo] = listed = codes;
            combo.Items.Clear();
            foreach (var code in codes)
            {
                combo.Items.Add(Languages.FirstOrDefault(l => l.Code == code).Name is { } name ? Loc.T(name) : code);
            }
        }
        combo.SelectedIndex = listed.IndexOf(selection);
    }

    private void OnLanguagePicked(ComboBox combo)
    {
        var index = combo.SelectedIndex;
        if (_syncing || !_languageCodes.TryGetValue(combo, out var codes) || index < 0 || index >= codes.Count
            || codes[index] == _providers.SummaryLanguage) return;
        _providers.SummaryLanguage = codes[index];
    }

    /// <summary>The offered sizes, the whole chat, plus a hand-edited value (macOS <c>chatSection</c>).</summary>
    private void RefreshChatContext()
    {
        var current = _providers.ChatContextMessages;
        List<int> values = [.. AppSettings.ChatContextOptions, 0];
        if (!values.Contains(current)) values.Add(current);
        if (!values.SequenceEqual(_chatContextValues))
        {
            _chatContextValues = values;
            ChatContextCombo.Items.Clear();
            foreach (var value in values)
            {
                ChatContextCombo.Items.Add(value == 0 ? Loc.T("Whole chat") : Loc.Plural("%lld messages", value));
            }
        }
        ChatContextCombo.SelectedIndex = _chatContextValues.IndexOf(current);
    }

    // Catch-up

    /// <summary>
    /// The Catch-up tab (macOS <c>catchUpSection</c>): the provider a catch-up runs on, among the usable ones outside the
    /// app ("Automatic" names what it runs, <see cref="AppViewModel.AutomaticCatchUpProvider"/>); the model of a picked
    /// one, and the key status of a picked cloud API; the notes of the provider it runs on; why none can; from the start
    /// or not.
    /// </summary>
    private void RefreshCatchUp()
    {
        var external = CatchUpProviders();
        List<string> ids = [""];
        List<string> titles = [_model.AutomaticCatchUpProvider is { } automatic
            ? WithLeavesPC(Loc.Format("Automatic (%@)", automatic.Name), automatic)
            : Loc.T("Automatic")];
        foreach (var provider in external)
        {
            ids.Add(provider.Id);
            titles.Add(WithLeavesPC(provider.Name, provider));
        }
        // Rebuilt only when the choices change: rebuilding closes an open list.
        if (!titles.SequenceEqual(_catchUpProviderTitles) || !ids.SequenceEqual(_catchUpProviderIds))
        {
            _catchUpProviderIds = ids;
            _catchUpProviderTitles = titles;
            CatchUpProviderCombo.Items.Clear();
            foreach (var title in titles) CatchUpProviderCombo.Items.Add(title);
        }
        CatchUpProviderCombo.IsEnabled = external.Count > 0;
        var pickedId = ShownCatchUpProviderId(external);
        CatchUpProviderCombo.SelectedIndex = _catchUpProviderIds.IndexOf(pickedId);

        var picked = external.FirstOrDefault(p => p.Id == pickedId);
        CatchUpModelCombo.Visibility = Show(picked is not null);
        CatchUpModelAutoText.Visibility = Show(picked is null);
        CatchUpModelCard.Description = picked is null
            ? Loc.T("The Summary model. Pick a provider above to choose another.")
            : null!; // null hides it
        if (picked is null)
        {
            // Automatic runs the Summary model: shown, not picked (macOS automaticCatchUpModelLabel).
            var automaticModel = _model.CatchUpProvider is { } runs ? _model.CatchUpModel(runs) : "";
            CatchUpModelAutoText.Text = automaticModel.Length == 0
                ? Loc.T("Automatic")
                : Loc.Format("Automatic (%@)", automaticModel);
        }
        else
        {
            // Its models without a CLI's own default ("Automatic" covers it), then the saved pick when no longer listed.
            var saved = _providers.CatchUpModel;
            List<string> models = ["", .. picked.Models.Where(m => m.Length > 0)];
            if (!models.Contains(saved)) models.Add(saved);
            if (!models.SequenceEqual(_catchUpModels))
            {
                _catchUpModels = models;
                CatchUpModelCombo.Items.Clear();
                foreach (var model in models) CatchUpModelCombo.Items.Add(model.Length == 0 ? Loc.T("Automatic") : model);
            }
            CatchUpModelCombo.SelectedIndex = _catchUpModels.IndexOf(saved);
        }
        var pickedCloud = picked is { Kind: AIProviderKind.Cloud } ? _providers.Config(picked.Id) : null;
        CatchUpKeyCard.Visibility = Show(pickedCloud is not null);
        if (pickedCloud is not null)
        {
            CatchUpKeyCard.Header = picked!.Name;
            _catchUpKeyStatus.Update(pickedCloud);
        }

        var runsOn = _model.CatchUpProvider;
        CatchUpLeavesText.Text = runsOn?.LeavesPCNote ?? "";
        CatchUpLeavesRow.Visibility = Show(runsOn?.LeavesPCNote is not null);
        CatchUpContainmentText.Text = runsOn?.ContainmentNote ?? "";
        CatchUpContainmentRow.Visibility = Show(runsOn?.ContainmentNote is not null);
        CatchUpNoProviderText.Visibility = Show(external.Count == 0);
        CatchUpFromStartToggle.IsOn = _providers.CatchUpFromStart;
    }

    /// <summary>The usable providers outside the app, as <see cref="AppViewModel.CatchUpProvider"/> filters them.</summary>
    private List<AIProviderInfo> CatchUpProviders() =>
        [.. _providers.AIProviders.Where(p => p.Kind != AIProviderKind.InApp && p.UnavailableReason is null)];

    /// <summary>The picked provider, "" (Automatic) when it is not usable now, as a catch-up then runs on Automatic's.</summary>
    private string ShownCatchUpProviderId(List<AIProviderInfo> external)
    {
        var id = _model.Settings.CatchUpProviderID;
        return external.Any(p => p.Id == id) ? id : "";
    }

    /// <summary>The model menu's "(leaves this PC)" after <paramref name="title"/> when <paramref name="provider"/>'s data does.</summary>
    private static string WithLeavesPC(string title, AIProviderInfo provider) =>
        provider.LeavesPC ? Loc.Format("%@ (leaves this PC)", title) : title;

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// A purpose's default model in a combo box (macOS <c>AIModelPicker</c>), with the choices of the AI
    /// pane's model menu that can run now: the in-app models first, then each host's listed models, then the providers
    /// whose data leaves this PC; what runs now is added to its provider when not listed, so the box always shows it.
    /// "No AI model" (the placeholder) while no provider can run.
    /// </summary>
    private sealed class ModelPicker
    {
        private readonly ComboBox _combo;
        private readonly ProvidersViewModel _providers;
        private readonly Func<bool> _isSyncing;
        private List<(AIProviderInfo Provider, string Model)> _choices = [];
        /// <summary>The choices' titles as listed: the list is rebuilt only when they change (rebuilding closes it).</summary>
        private List<string> _titles = [];

        public ModelPicker(ComboBox combo, ProvidersViewModel providers, AIPurpose purpose, Func<bool> isSyncing)
        {
            _combo = combo;
            _providers = providers;
            Purpose = purpose;
            _isSyncing = isSyncing;
            combo.SelectionChanged += (_, _) => OnPicked();
        }

        public AIPurpose Purpose { get; }

        public void Refresh()
        {
            var current = _providers.CurrentAIProvider(Purpose);
            var currentModel = current is null ? null : _providers.AIModelFor(current, Purpose);
            var usable = _providers.AIProviders.Where(p => p.UnavailableReason is null).ToList();
            var ordered = usable.Where(p => p.Kind == AIProviderKind.InApp)
                .Concat(usable.Where(p => p.Kind != AIProviderKind.InApp && !p.LeavesPC))
                .Concat(usable.Where(p => p.LeavesPC));
            List<(AIProviderInfo Provider, string Model)> choices = [];
            List<string> titles = [];
            foreach (var provider in ordered)
            {
                var isCurrent = provider.Id == current?.Id;
                if (provider.Kind == AIProviderKind.InApp)
                {
                    choices.Add((provider, isCurrent ? currentModel! : provider.DefaultModel));
                    titles.Add(provider.Name);
                    continue;
                }
                List<string> ids = [.. provider.Models];
                if (isCurrent && !ids.Contains(currentModel!)) ids.Add(currentModel!);
                var name = provider.LeavesPC ? Loc.Format("%@ (leaves this PC)", provider.Name) : provider.Name;
                foreach (var id in ids)
                {
                    choices.Add((provider, id));
                    titles.Add($"{name} · {(id.Length == 0 ? Loc.T("Default") : id)}");
                }
            }
            if (!titles.SequenceEqual(_titles))
            {
                _titles = titles;
                _combo.Items.Clear();
                foreach (var title in titles) _combo.Items.Add(title);
            }
            _choices = choices;
            _combo.IsEnabled = choices.Count > 0;
            _combo.SelectedIndex = current is null ? -1 : choices.FindIndex(c => c.Provider.Id == current.Id && c.Model == currentModel);
        }

        private void OnPicked()
        {
            var index = _combo.SelectedIndex;
            if (_isSyncing() || index < 0 || index >= _choices.Count) return;
            var (provider, model) = _choices[index];
            var current = _providers.CurrentAIProvider(Purpose);
            if (current?.Id == provider.Id && _providers.AIModelFor(current, Purpose) == model) return;
            _providers.SetAIModel(model, provider, Purpose);
        }
    }
}
