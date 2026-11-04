using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using EchoDiar.AI;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › AI (macOS ProvidersSettingsView.swift), the On this PC, Cloud and CLIs inner tabs; the title bar picks the
/// tab (<see cref="AppViewModel.SubTabOf"/>). Cloud holds the models of the cloud API presets and custom endpoints; their
/// keys and endpoints are set in Settings › API Keys (<see cref="ApiKeysSettingsPage"/>). Every change goes through
/// <see cref="ProvidersViewModel"/> and is saved after the settings delay. The provider rows are made in code and updated
/// in place, so typing in a field never rebuilds the row it is in; so are the GGUF model rows, whose state a download
/// changes often. The local
/// hosts are scanned and the CLIs looked up once, when the page first shows (macOS: once per window), then on Scan or
/// on a new path override.
/// </summary>
public sealed partial class ProvidersSettingsPage : UserControl
{
    // macOS ids (deep links and search entries stay the same); "On this PC" is the Windows title.
    public const string OnThisPCTab = "onThisMac";
    public const string CloudTab = "cloud";
    public const string CliTab = "cli";

    /// <summary>macOS <c>SettingsTab.providers.subTabs</c>.</summary>
    public static IReadOnlyList<SettingsSubTab> SubTabs { get; } =
    [
        new(OnThisPCTab, "On this PC"),
        new(CloudTab, "Cloud"),
        new(CliTab, "CLIs"),
    ];

    /// <summary>The searchable rows (macOS SettingsSearch.swift, AI), each the Tag of its element.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("AI models (summaries and chat)",
            ["LLM", "Windows AI", "Phi Silica", "local", "summary", "chat", "default model", "language", "download",
             "GGUF", "Qwen", "Gemma", "offline"], OnThisPCTab),
        new("Chat context", ["chat", "history", "context", "messages", "memory"], OnThisPCTab),
        new("Catch-up", ["catch up", "live summary", "recording", "meeting so far"], OnThisPCTab),
        new("Catch-up provider", ["catch up", "live summary", "cloud", "CLI", "local server"], OnThisPCTab),
        new("Catch-up model", ["catch up", "live summary", "LLM"], OnThisPCTab),
        new("Re-summarize from the start each time", ["catch up", "incremental", "whole recording", "from start"], OnThisPCTab),
        new("Local hosts", ["scan", "custom URL", "server", .. PresetNames(ProviderKind.Local)], OnThisPCTab),
        new("Cloud APIs",
            ["provider", "LLM", "model", .. PresetNames(ProviderKind.OpenAICompatible), .. PresetNames(ProviderKind.Anthropic)],
            CloudTab),
        new("Custom OpenAI-compatible endpoints", ["custom endpoint", "model"], CloudTab),
        new("CLIs", ["subscription", "command line", "terminal", .. PresetNames(ProviderKind.Cli)], CliTab),
    ];

    private static IEnumerable<string> PresetNames(ProviderKind kind) =>
        ProviderConfig.Presets().Where(p => p.Kind == kind).Select(p => p.DisplayName);

    /// <summary>The summary languages offered (macOS <c>AILanguageMenu</c>), plus a hand-edited one.</summary>
    private static readonly (string Code, string Name)[] Languages =
        [("auto", "Auto"), ("en", "English"), ("fr", "French"), ("vi", "Vietnamese")];

    private readonly AppViewModel _model;
    private readonly ProvidersViewModel _providers;

    private readonly Dictionary<string, ApiRow> _apiRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CloudRow> _cloudRows = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A picked cloud catch-up provider's key status.</summary>
    private readonly ApiKeyStatus _catchUpKeyStatus;
    private readonly Dictionary<CLIPreset, CliRow> _cliRows = [];

    /// <summary>One row per GGUF model, made once and updated in place.</summary>
    private readonly List<LocalModelRow> _modelRows = [];

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    private List<(AIProviderInfo Provider, string Model)> _modelChoices = [];
    /// <summary>Each model choice's title and why it cannot run, as listed.</summary>
    private List<string> _modelKeys = [];
    private List<string> _languageCodes = [];
    private List<int> _chatContextValues = [];

    /// <summary>The catch-up pickers' values as listed ("" = Automatic) and their titles, rebuilt only when these change.</summary>
    private List<string> _catchUpProviderIds = [];
    private List<string> _catchUpProviderTitles = [];
    private List<string> _catchUpModels = [];
    private string _foundHostsKey = "";
    private bool _scanned;

    public ProvidersSettingsPage(AppViewModel model)
    {
        _model = model;
        _providers = model.ProvidersModel;
        InitializeComponent();
        LanguageCard.Description = Loc.T("Auto = the transcript's language for summaries, the question's for answers.");
        _catchUpKeyStatus = new ApiKeyStatus(model, (Style)Resources["SuccessIconStyle"]);
        CatchUpKeyCard.Content = _catchUpKeyStatus.Element;
        foreach (var entry in ProvidersViewModel.LocalModelEntries) LocalModelsList.Children.Add(CreateModelRow(entry));

        WindowsAICheckButton.Click += (_, _) => _ = _providers.RefreshWindowsAIAsync();
        ModelCombo.SelectionChanged += (_, _) => OnModelPicked();
        LanguageCombo.SelectionChanged += (_, _) =>
        {
            var index = LanguageCombo.SelectedIndex;
            if (_syncing || index < 0 || index >= _languageCodes.Count || _languageCodes[index] == _providers.SummaryLanguage) return;
            _providers.SummaryLanguage = _languageCodes[index];
        };
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
        ScanButton.Click += (_, _) => _ = _providers.ScanAsync();
        CustomUrlBox.TextChanged += (_, _) => AddCustomButton.IsEnabled = ProviderInput.HostAndPort(CustomUrlBox.Text) is not null;
        CustomUrlBox.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            AddCustomLocal();
        };
        AddCustomButton.Click += (_, _) => AddCustomLocal();

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _providers.PropertyChanged += OnProvidersPropertyChanged;
            _providers.Changed += Refresh;
            _providers.CliStatusChanged += UpdateCliStatus;
            _providers.LocalModelStatesChanged += UpdateModelRows;
            Refresh();
            ShowTab();
            UpdateScan();
            UpdateCliStatus();
            UpdateModelRows();
            _ = _providers.RefreshKeysAsync();
            _ = _providers.RefreshLocalModelsAsync();
            if (!_scanned)
            {
                _scanned = true;
                _ = _providers.ScanAsync();
            }
            _providers.DetectAllOnce();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _providers.PropertyChanged -= OnProvidersPropertyChanged;
            _providers.Changed -= Refresh;
            _providers.CliStatusChanged -= UpdateCliStatus;
            _providers.LocalModelStatesChanged -= UpdateModelRows;
            // macOS: a test or a model list stops when its row goes away.
            foreach (var row in _apiRows.Values) row.Cancel();
            foreach (var row in _cloudRows.Values) row.Cancel();
            foreach (var row in _cliRows.Values) row.Cancel();
        };
    }

    private bool Closing => _model.IsClosing;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.SettingsSubTab) or nameof(AppViewModel.SettingsExpandedProvider)) ShowTab();
    }

    private void OnProvidersPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProvidersViewModel.Hosts):
            case nameof(ProvidersViewModel.Scanning):
                UpdateScan();
                break;
            case nameof(ProvidersViewModel.WindowsAI):
                UpdateWindowsAI();
                break;
        }
    }

    /// <summary>The picked tab's panel.</summary>
    private void ShowTab()
    {
        var tab = _model.SubTabOf(SettingsSearch.Providers);
        OnThisPCPanel.Visibility = Show(tab == OnThisPCTab);
        CloudPanel.Visibility = Show(tab == CloudTab);
        CliPanel.Visibility = Show(tab == CliTab);
        ExpandRequestedProvider(tab);
    }

    /// <summary>
    /// A Set Up in Settings › Transcription or RAG (macOS <c>settingsExpandedProvider</c>): its model row on Cloud, or
    /// its saved local host's row on On this PC, expands and comes into view, and the request is cleared.
    /// </summary>
    private void ExpandRequestedProvider(string? tab)
    {
        // Only while this section shows: a page just left may still hear a request meant for API Keys.
        if (Closing || _model.SettingsSection != SettingsSearch.Providers || _model.SettingsExpandedProvider is not { } id) return;
        // Only on the tab its row is on.
        SettingsExpander? expander = tab switch
        {
            OnThisPCTab => _apiRows.GetValueOrDefault(id)?.Expander,
            CloudTab => _cloudRows.GetValueOrDefault(id)?.Expander,
            _ => null,
        };
        if (expander is null) return;
        _model.SettingsExpandedProvider = null;
        expander.IsExpanded = true;
        // After the panel's layout: a row of a tab just shown has no position yet.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!Closing) expander.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0 });
        });
    }

    /// <summary>Every control shows the current settings, keys and states.</summary>
    private void Refresh()
    {
        if (Closing) return;
        _syncing = true;
        try
        {
            UpdateWindowsAI();
            RefreshModelChoices();
            RefreshLanguages();
            RefreshChatContext();
            RefreshCatchUp();
            UpdateFoundHosts();
            RefreshRows();
        }
        finally
        {
            _syncing = false;
        }
    }

    // On this PC

    /// <summary>Windows AI (macOS Apple Foundation Models' box): hidden when this PC can never run it.</summary>
    private void UpdateWindowsAI()
    {
        if (_providers.WindowsAI is not { Hidden: false } state)
        {
            WindowsAICard.Visibility = Visibility.Collapsed;
            return;
        }
        WindowsAICard.Visibility = Visibility.Visible;
        WindowsAIIcon.Visibility = Show(state.Reason is null);
        WindowsAICheckButton.Visibility = Show(state.Reason is not null);
        WindowsAIText.Text = state.Reason is { } reason ? Loc.T(reason) : Loc.T("Available");
        WindowsAIText.Style = state.Reason is null ? (Style)Resources["SuccessNoteStyle"] : (Style)Resources["NoteStyle"];
    }

    /// <summary>
    /// Every provider with the models it offers (macOS <c>AIModelMenu</c>): on this PC first, then the ones whose data
    /// leaves it; a provider that cannot run now is shown off, with why.
    /// </summary>
    private void RefreshModelChoices()
    {
        var providers = _providers.AIProviders;
        var ordered = providers.Where(p => p.Kind == AIProviderKind.InApp)
            .Concat(providers.Where(p => p.Kind != AIProviderKind.InApp && !p.LeavesPC))
            .Concat(providers.Where(p => p.LeavesPC));
        List<(AIProviderInfo Provider, string Model)> choices = [];
        List<string> titles = [];
        foreach (var provider in ordered)
        {
            if (provider.Kind == AIProviderKind.InApp)
            {
                choices.Add((provider, provider.DefaultModel));
                titles.Add(provider.Name);
                continue;
            }
            var name = provider.LeavesPC ? Loc.Format("%@ (leaves this PC)", provider.Name) : provider.Name;
            foreach (var id in provider.Models)
            {
                choices.Add((provider, id));
                titles.Add($"{name} · {(id.Length == 0 ? Loc.T("Default") : id)}");
            }
        }
        // Rebuilt only when the choices change: rebuilding closes an open list.
        List<string> keys = [.. titles.Select((title, i) => $"{title}\u0001{choices[i].Provider.UnavailableReason}")];
        if (!keys.SequenceEqual(_modelKeys))
        {
            _modelKeys = keys;
            ModelCombo.Items.Clear();
            for (var i = 0; i < choices.Count; i++)
            {
                var reason = choices[i].Provider.UnavailableReason;
                var item = new ComboBoxItem { Content = titles[i], IsEnabled = reason is null };
                if (reason is not null) ToolTipService.SetToolTip(item, reason);
                ModelCombo.Items.Add(item);
            }
        }
        _modelChoices = choices;
        ModelCombo.IsEnabled = choices.Count > 0;
        var current = _providers.CurrentAIProvider;
        var model = current is null ? null : _providers.AIModelFor(current);
        ModelCombo.SelectedIndex = current is null ? -1 : choices.FindIndex(c => c.Provider.Id == current.Id && c.Model == model);
    }

    /// <summary>The parts of a GGUF model's row that change with its state.</summary>
    private sealed record LocalModelRow(
        ModelEntry Entry,
        Border DefaultBadge,
        TextBlock ErrorText,
        ProgressBar Progress,
        TextBlock PercentText,
        Button CancelButton,
        StackPanel InstalledLabel,
        Button DeleteButton,
        Button DownloadButton);

    /// <summary>
    /// A GGUF model's card (macOS <c>ModelRow</c> of an LLM): its name and Default badge (the catalog default; the model
    /// menu below picks the one used); its details, quality, memory warning and last error below; its actions on the
    /// right. No Set default: macOS offers it for speech and speaker models only.
    /// </summary>
    private SettingsCard CreateModelRow(ModelEntry entry)
    {
        var name = new TextBlock
        {
            Text = entry.DisplayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(name, entry.DisplayName);
        var badge = new Border
        {
            Style = (Style)Resources["DefaultBadgeStyle"],
            Child = new TextBlock { Style = (Style)Resources["DefaultBadgeTextStyle"], Text = Loc.T("Default") },
            Visibility = Visibility.Collapsed,
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(name);
        header.Children.Add(badge);

        var description = new StackPanel { Spacing = 2 };
        description.Children.Add(Note("NoteStyle", ModelsViewModel.Details(entry)));
        if (ModelsViewModel.QualityNote(entry) is { } quality) description.Children.Add(Note("NoteStyle", quality));
        if (ModelsViewModel.NeedsMuchMemory(entry))
        {
            // A warning only, as on macOS: the model still downloads and runs.
            var warning = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            warning.Children.Add(new FontIcon { Style = (Style)Resources["CautionIconStyle"], Glyph = "\uE7BA" });
            warning.Children.Add(Note("CautionNoteStyle", Loc.T("Needs more than half of this PC's memory.")));
            description.Children.Add(warning);
        }
        var error = Note("CriticalNoteStyle");
        error.Visibility = Visibility.Collapsed;
        description.Children.Add(error);

        var progress = new ProgressBar { Width = 90, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(progress, entry.DisplayName);
        var percent = new TextBlock
        {
            Style = (Style)Resources["NoteStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 32,
            TextAlignment = TextAlignment.Right,
        };
        var cancel = ModelButton("Cancel", entry, () => _providers.CancelLocalModelDownload(entry));
        var installed = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        installed.Children.Add(new FontIcon { Style = (Style)Resources["SuccessIconStyle"], Glyph = "\uE930" });
        installed.Children.Add(new TextBlock { Style = (Style)Resources["SuccessNoteStyle"], Text = Loc.T("Installed") });
        var delete = ModelButton("Delete", entry, () => _ = _providers.DeleteLocalModelAsync(entry));
        var download = ModelButton("Download", entry, () => _providers.DownloadLocalModel(entry));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var part in new UIElement[] { progress, percent, cancel, installed, delete, download })
        {
            part.Visibility = Visibility.Collapsed;
            actions.Children.Add(part);
        }

        _modelRows.Add(new LocalModelRow(entry, badge, error, progress, percent, cancel, installed, delete, download));
        return new SettingsCard { Header = header, Description = description, Content = actions };
    }

    /// <summary>A model row's button, named with its model for screen readers ("Download, Qwen3.5 2B").</summary>
    private static Button ModelButton(string text, ModelEntry entry, Action click)
    {
        var button = new Button { Content = Loc.T(text) };
        AutomationProperties.SetName(button, $"{Loc.T(text)}, {entry.DisplayName}");
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Every GGUF row shows its model's state (macOS <c>ModelRow.actions</c>).</summary>
    private void UpdateModelRows()
    {
        if (Closing) return;
        foreach (var row in _modelRows)
        {
            var state = _providers.LocalModelState(row.Entry);
            var downloading = state.Download is not null;
            var installed = !downloading && state.IsInstalled;
            row.DefaultBadge.Visibility = Show(state.IsDefault);
            row.Progress.Visibility = row.PercentText.Visibility = row.CancelButton.Visibility = Show(downloading);
            if (state.Download is { } fraction)
            {
                row.Progress.Value = fraction;
                row.PercentText.Text = fraction.ToString("P0", CultureInfo.CurrentCulture);
            }
            row.InstalledLabel.Visibility = row.DeleteButton.Visibility = Show(installed);
            row.DeleteButton.IsEnabled = state.CanDelete;
            row.DownloadButton.Visibility = Show(!downloading && !state.IsInstalled);
            row.ErrorText.Text = state.Error ?? "";
            row.ErrorText.Visibility = Show(state.Error is not null);
        }
    }

    private void OnModelPicked()
    {
        var index = ModelCombo.SelectedIndex;
        if (_syncing || index < 0 || index >= _modelChoices.Count) return;
        var (provider, model) = _modelChoices[index];
        var current = _providers.CurrentAIProvider;
        if (current?.Id == provider.Id && _providers.AIModelFor(current) == model) return;
        _providers.SetAIModel(model, provider);
    }

    private void RefreshLanguages()
    {
        var selection = _providers.SummaryLanguage;
        List<string> codes = [.. Languages.Select(l => l.Code)];
        if (!codes.Contains(selection)) codes.Add(selection);
        if (!codes.SequenceEqual(_languageCodes))
        {
            _languageCodes = codes;
            LanguageCombo.Items.Clear();
            foreach (var code in codes)
            {
                LanguageCombo.Items.Add(Languages.FirstOrDefault(l => l.Code == code).Name is { } name ? Loc.T(name) : code);
            }
        }
        LanguageCombo.SelectedIndex = _languageCodes.IndexOf(selection);
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

    /// <summary>
    /// The Catch-up group (macOS <c>catchUpSection</c>): the provider a catch-up runs on, among the usable ones outside
    /// the app ("Automatic" names what it runs, <see cref="AppViewModel.AutomaticCatchUpProvider"/>); the model of a
    /// picked one, and the key status of a picked cloud API; the notes of the provider it runs on; why none can; from the
    /// start or not.
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
        CatchUpModelCard.Visibility = Show(picked is not null);
        if (picked is not null)
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

    /// <summary>The scan's state in the Local hosts header: its ring, "None found", Scan off while it runs.</summary>
    private void UpdateScan()
    {
        if (Closing) return;
        var scanning = _providers.Scanning;
        ScanRing.IsActive = scanning;
        ScanRing.Visibility = Show(scanning);
        NoneFoundText.Visibility = Show(!scanning && _providers.Hosts is { Count: 0 });
        ScanButton.IsEnabled = !scanning;
        UpdateFoundHosts();
    }

    /// <summary>A row per found host: its name, URL and model count, and Add or Added (macOS <c>localHosts</c>).</summary>
    private void UpdateFoundHosts()
    {
        var hosts = _providers.Hosts ?? [];
        var key = string.Join("\n", hosts.Select(h => $"{h.Preset}|{h.BaseURL}|{h.Models.Count}|{_providers.IsAdded(h)}"));
        if (key == _foundHostsKey) return;
        _foundHostsKey = key;
        FoundHostsList.Children.Clear();
        var presets = ProviderConfig.Presets();
        foreach (var host in hosts)
        {
            var name = presets.FirstOrDefault(p => p.Id == host.Preset)?.DisplayName ?? host.Preset;
            var count = host.Models.Count;
            object action;
            if (_providers.IsAdded(host))
            {
                action = new TextBlock { Style = (Style)Resources["NoteStyle"], Text = Loc.T("Added"), VerticalAlignment = VerticalAlignment.Center };
            }
            else
            {
                var add = new Button { Content = Loc.T("Add") };
                AutomationProperties.SetName(add, $"{Loc.T("Add")}, {name}");
                add.Click += (_, _) => _providers.AddHost(host);
                action = add;
            }
            FoundHostsList.Children.Add(new SettingsCard
            {
                Header = name,
                Description = Loc.Plural("%@ - %lld models", count, host.BaseURL, count),
                Content = action,
            });
        }
    }

    private void AddCustomLocal()
    {
        if (_providers.AddCustomLocal(CustomUrlBox.Text)) CustomUrlBox.Text = "";
    }

    // Rows

    /// <summary>
    /// The API presets, the custom endpoints, the saved local hosts and the CLIs, each a row made once per provider and
    /// updated in place; a list is rebuilt only when its providers change.
    /// </summary>
    private void RefreshRows()
    {
        var configs = _providers.ProviderConfigs;
        var cloud = configs.Where(c => c.Preset is not null && c.Kind is ProviderKind.OpenAICompatible or ProviderKind.Anthropic).ToList();
        var custom = configs.Where(c => c.Preset is null && c.Kind == ProviderKind.OpenAICompatible).ToList();
        var cloudShown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SyncList(CloudPresetsList, cloud, _cloudRows, id => new CloudRow(this, id), cloudShown);
        SyncList(CustomList, custom, _cloudRows, id => new CloudRow(this, id), cloudShown);
        CustomHeader.Visibility = Show(custom.Count > 0);
        var localShown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SyncList(LocalHostsList, _providers.SavedLocalHosts, _apiRows, id => new ApiRow(this, id), localShown);
        DropRows(_cloudRows, cloudShown);
        DropRows(_apiRows, localShown);

        List<CliRow> cliRows = [];
        foreach (var config in configs.Where(c => c.Kind == ProviderKind.Cli))
        {
            if (CLIPresets.FromSlug(config.Id) is not { } preset) continue;
            if (!_cliRows.TryGetValue(preset, out var row)) _cliRows[preset] = row = new CliRow(this, preset);
            row.Update(config);
            cliRows.Add(row);
        }
        if (!CliList.Children.SequenceEqual(cliRows.Select(r => r.Expander)))
        {
            CliList.Children.Clear();
            foreach (var row in cliRows) CliList.Children.Add(row.Expander);
        }
    }

    /// <summary>
    /// <paramref name="list"/> shows a row per config, made once (<paramref name="create"/>) and kept in
    /// <paramref name="rows"/>; each id is shown once, and added to <paramref name="shown"/>.
    /// </summary>
    private static void SyncList<TRow>(
        StackPanel list, IReadOnlyList<ProviderConfig> configs, Dictionary<string, TRow> rows, Func<string, TRow> create,
        HashSet<string> shown) where TRow : IProviderRow
    {
        List<TRow> listed = [];
        foreach (var config in configs)
        {
            if (!shown.Add(config.Id)) continue;
            if (!rows.TryGetValue(config.Id, out var row)) rows[config.Id] = row = create(config.Id);
            row.Update(config);
            listed.Add(row);
        }
        if (list.Children.SequenceEqual(listed.Select(r => (UIElement)r.Expander))) return;
        list.Children.Clear();
        foreach (var row in listed)
        {
            // A row moved from another list (its kind changed) leaves that one first.
            if (row.Expander.Parent is Panel parent) parent.Children.Remove(row.Expander);
            list.Children.Add(row.Expander);
        }
    }

    /// <summary>The rows of providers no longer listed stop their work and are dropped.</summary>
    private static void DropRows<TRow>(Dictionary<string, TRow> rows, HashSet<string> shown) where TRow : IProviderRow
    {
        foreach (var id in rows.Keys.Where(id => !shown.Contains(id)).ToList())
        {
            rows[id].Cancel();
            rows.Remove(id);
        }
    }

    private void UpdateCliStatus()
    {
        if (Closing) return;
        foreach (var (preset, row) in _cliRows) row.UpdateStatus(_providers.CliStatus(preset));
    }

    // Parts

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private TextBlock Note(string style, string text = "") => new() { Style = (Style)Resources[style], Text = text };

    /// <summary>
    /// A field of an expanded row: its label on the left, its input on the right (macOS <c>FieldRow</c>). Below
    /// <see cref="FieldWrapWidth"/> the card puts the input under its label (the toolkit's default is 476), so the widest
    /// field line (the model id, its models menu and Load models) keeps its buttons in view in a narrow window.
    /// </summary>
    private static SettingsCard Field(string label, object content)
    {
        var card = new SettingsCard { Header = Loc.T(label), Content = content };
        card.Resources["SettingsCardWrapThreshold"] = FieldWrapWidth;
        return card;
    }

    private const double FieldWrapWidth = 640;

    /// <summary>Sets a text box's text unless it already shows it (the caret stays where the user types).</summary>
    private static void SetText(TextBox box, string text)
    {
        if (box.Text != text) box.Text = text;
    }

    private static ProgressRing SmallRing() => new()
    {
        Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
    };

    private static HyperlinkButton Link(string text, string url) => new() { Content = Loc.T(text), NavigateUri = new Uri(url), Padding = new Thickness(0) };

    private static bool IsEnter(KeyRoutedEventArgs e) => e.Key == VirtualKey.Enter;

    /// <summary>A test's or model list's error; a local host or custom endpoint answering 404 likely lacks <c>/v1</c>.</summary>
    private static string ErrorMessage(Exception error, ProviderConfig config)
    {
        var message = ProvidersViewModel.ShortMessage(error);
        return ProviderInput.SuggestsV1(config, error) ? $"{message} {Loc.T("Check that the base URL ends with /v1.")}" : message;
    }

    /// <summary>A provider row a list shows: its expander, updated in place, and its work stopped when it goes away.</summary>
    private interface IProviderRow
    {
        SettingsExpander Expander { get; }
        void Update(ProviderConfig config);
        void Cancel();
    }

    /// <summary>
    /// A cloud LLM preset or custom endpoint on the Cloud tab (macOS <c>CloudModelRow</c>): its key status ("API key"
    /// opens its card in Settings › API Keys); expanded, its model and the models shown in the AI pane's menu. A missing
    /// base URL (Azure, a custom endpoint) is said under its name.
    /// </summary>
    private sealed class CloudRow : IProviderRow
    {
        private readonly ApiKeyStatus _status;
        private readonly ModelField _modelField;

        public CloudRow(ProvidersSettingsPage page, string id)
        {
            _status = new ApiKeyStatus(page._model, (Style)page.Resources["SuccessIconStyle"]);
            _modelField = new ModelField(page, id, id == "azure" ? "Deployment name" : "Model id",
                ApiKeysSettingsPage.ProviderLinks.TryGetValue(id, out var links) ? links.Models : null);
            Expander = new SettingsExpander { Content = _status.Element };
            Expander.Items.Add(_modelField.ModelCard);
            Expander.Items.Add(_modelField.ShownCard);
        }

        public SettingsExpander Expander { get; }

        public void Update(ProviderConfig config)
        {
            Expander.Header = config.DisplayName.Length == 0 ? Loc.T("Untitled") : config.DisplayName;
            if (config.BaseURL.Trim().Length == 0) Expander.Description = Loc.T("Base URL not set");
            else if (config.Preset is null) Expander.Description = config.BaseURL;
            else Expander.ClearValue(SettingsExpander.DescriptionProperty);
            _status.Update(config);
            _modelField.Update(config);
        }

        public void Cancel() => _modelField.Cancel();
    }

    /// <summary>
    /// A saved local host (macOS <c>APIProviderRow</c>): the Enabled switch; expanded, its base URL, model, the models
    /// shown in the AI pane's menu, Test and Remove.
    /// </summary>
    private sealed class ApiRow : IProviderRow
    {
        private readonly ProvidersSettingsPage _page;
        private readonly string _id;
        private readonly ToggleSwitch _enabled = new();
        private readonly TextBox _baseBox = new();
        private readonly ModelField _modelField;
        private readonly TestField _test;

        public ApiRow(ProvidersSettingsPage page, string id)
        {
            _page = page;
            _id = id;
            Expander = new SettingsExpander { Content = _enabled };
            _enabled.Toggled += (_, _) => Change(c => c.Enabled != _enabled.IsOn, c => c.Enabled = _enabled.IsOn);

            AutomationProperties.SetName(_baseBox, Loc.T("Base URL"));
            _baseBox.TextChanged += (_, _) => Change(c => c.BaseURL != _baseBox.Text, c => c.BaseURL = _baseBox.Text);
            var baseCard = Field("Base URL", new FieldPanel { InputWidth = 320, Children = { _baseBox } });

            _modelField = new ModelField(page, id, "Model id", null);

            var remove = new Button { Content = Loc.T("Remove") };
            remove.Click += (_, _) => _ = _page._providers.RemoveProviderAsync(_id);
            _test = new TestField(page, id, "Test", remove);

            Expander.Items.Add(baseCard);
            Expander.Items.Add(_modelField.ModelCard);
            Expander.Items.Add(_modelField.ShownCard);
            Expander.Items.Add(_test.Card);
        }

        public SettingsExpander Expander { get; }

        /// <summary>Shows <paramref name="config"/>; fields the user is editing already show their own text.</summary>
        public void Update(ProviderConfig config)
        {
            var name = config.DisplayName.Length == 0 ? Loc.T("Untitled") : config.DisplayName;
            Expander.Header = name;
            Expander.Description = config.BaseURL;
            AutomationProperties.SetName(_enabled, name);
            _enabled.IsOn = config.Enabled;
            SetText(_baseBox, config.BaseURL);
            _modelField.Update(config);
        }

        public void Cancel()
        {
            _test.Cancel();
            _modelField.Cancel();
        }

        /// <summary>Applies <paramref name="change"/> when <paramref name="differs"/>: a control showing the saved value is not a change.</summary>
        private void Change(Func<ProviderConfig, bool> differs, Action<ProviderConfig> change)
        {
            if (_page._syncing || _page.Closing || _page._providers.Config(_id) is not { } config || !differs(config)) return;
            _page._providers.UpdateProvider(_id, change);
        }
    }

    /// <summary>
    /// A subscription CLI (macOS <c>CLIProviderRow</c>): its found path and version, the Enabled switch; expanded, the path
    /// override, its model and shown models, what it can still do despite its flags, and Test.
    /// </summary>
    private sealed class CliRow
    {
        private readonly ProvidersSettingsPage _page;
        private readonly CLIPreset _preset;
        private readonly string _id;
        private readonly ToggleSwitch _enabled = new();
        private readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly ProgressRing _checkingRing = SmallRing();
        private readonly TextBox _pathBox = new();
        private readonly TextBlock _pathNote;
        private readonly ModelField _modelField;
        private readonly TestField _test;
        private string _detectedOverride = "";
        private bool _found;

        public CliRow(ProvidersSettingsPage page, CLIPreset preset)
        {
            _page = page;
            _preset = preset;
            _id = preset.Slug();
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(_name);
            header.Children.Add(_checkingRing);
            Expander = new SettingsExpander { Header = header, Content = _enabled };
            _enabled.Toggled += (_, _) => Change(c => c.Enabled != _enabled.IsOn, c => c.Enabled = _enabled.IsOn);

            _pathBox.PlaceholderText = Loc.T("Search PATH");
            AutomationProperties.SetName(_pathBox, Loc.T("Path override"));
            _pathBox.TextChanged += (_, _) => Change(c => c.BaseURL != _pathBox.Text, c => c.BaseURL = _pathBox.Text);
            // A new path is looked up on Enter (macOS onSubmit) and on leaving the field.
            _pathBox.KeyDown += (_, e) =>
            {
                if (!IsEnter(e)) return;
                e.Handled = true;
                Redetect();
            };
            _pathBox.LostFocus += (_, _) => Redetect();
            _pathNote = page.Note("CautionNoteStyle", Loc.T("Not found: give the full path to a .exe, or to an npm .cmd shim."));
            _pathNote.Visibility = Visibility.Collapsed;
            var pathCard = Field("Path override", new FieldPanel { InputWidth = 320, Children = { _pathBox } });
            pathCard.Description = _pathNote;

            _modelField = new ModelField(page, _id, preset switch
            {
                CLIPreset.OpenCode => "provider/model",
                CLIPreset.Claude => "sonnet, opus or haiku (empty = default)",
                _ => "Empty = the CLI's default",
            }, null);
            _test = new TestField(page, _id, "Test (uses your subscription)", null, CLIRunner.ContainmentNote(preset));

            Expander.Items.Add(pathCard);
            Expander.Items.Add(_modelField.ModelCard);
            Expander.Items.Add(_modelField.ShownCard);
            Expander.Items.Add(_test.Card);
        }

        public SettingsExpander Expander { get; }

        public void Update(ProviderConfig config)
        {
            _name.Text = config.DisplayName;
            AutomationProperties.SetName(_enabled, config.DisplayName);
            _enabled.IsOn = config.Enabled;
            SetText(_pathBox, config.BaseURL);
            _modelField.Update(config);
            _test.Enabled = _found;
        }

        public void UpdateStatus(CLIStatus? status)
        {
            var checking = status is null or { State: CLIState.Checking };
            _checkingRing.IsActive = checking;
            _checkingRing.Visibility = Show(checking);
            Expander.Description = status switch
            {
                { State: CLIState.Found, Path: var path, Version: var version } =>
                    string.Join(" - ", new[] { path, version }.Where(s => !string.IsNullOrEmpty(s))),
                { State: CLIState.NotInstalled } => Loc.T("Not installed"),
                _ => Loc.Format("Looking for %@...", _preset.ExecutableName()),
            };
            _pathNote.Visibility = Show(status is { InvalidOverride: true });
            _found = status is { State: CLIState.Found };
            _test.Enabled = _found;
            if (!checking) _detectedOverride = _page._providers.Config(_id)?.BaseURL.Trim() ?? "";
        }

        public void Cancel()
        {
            _test.Cancel();
            _modelField.Cancel();
        }

        private void Redetect()
        {
            var current = _page._providers.Config(_id)?.BaseURL.Trim() ?? "";
            if (current == _detectedOverride && _page._providers.CliStatus(_preset) is { State: not CLIState.Checking }) return;
            _detectedOverride = current;
            _ = _page._providers.DetectAsync(_preset);
        }

        private void Change(Func<ProviderConfig, bool> differs, Action<ProviderConfig> change)
        {
            if (_page._syncing || _page.Closing || _page._providers.Config(_id) is not { } config || !differs(config)) return;
            _page._providers.UpdateProvider(_id, change);
        }
    }

    /// <summary>
    /// The model id, "Load models" with the loaded models' menu, and the checklist of the models the AI pane's model menu
    /// offers besides it (macOS <c>ModelField</c>, <c>ProviderConfig.shownModels</c>).
    /// </summary>
    private sealed class ModelField
    {
        /// <summary>Above this many models the checklist scrolls and gets a filter field.</summary>
        private const int LongList = 8;

        private readonly ProvidersSettingsPage _page;
        private readonly string _id;
        private readonly TextBox _modelBox = new();
        private readonly DropDownButton _modelsButton = new() { Visibility = Visibility.Collapsed };
        private readonly MenuFlyout _modelsMenu = new();
        private readonly Button _loadButton = new();
        private readonly ProgressRing _loadRing = SmallRing();
        private readonly TextBlock _error;
        private readonly TextBlock _noModels;
        private readonly TextBox _filterBox = new() { Visibility = Visibility.Collapsed };
        private readonly ScrollViewer _scroller = new();
        private readonly StackPanel _checklist = new() { Spacing = 2 };
        private readonly TextBox _addBox = new() { MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock _hint;
        private List<string>? _models;
        private List<string> _shownIds = [];
        private CancellationTokenSource? _loading;
        private ProviderConfig? _config;

        public ModelField(ProvidersSettingsPage page, string id, string prompt, string? browseUrl)
        {
            _page = page;
            _id = id;
            _modelBox.PlaceholderText = Loc.T(prompt);
            AutomationProperties.SetName(_modelBox, Loc.T("Model"));
            _modelBox.TextChanged += (_, _) => Change(c => c.DefaultModel != _modelBox.Text, c => c.DefaultModel = _modelBox.Text);
            _modelsButton.Flyout = _modelsMenu;
            _loadButton.Content = Loc.T("Load models");
            _loadButton.Click += (_, _) => _ = LoadAsync();
            var inputs = new FieldPanel { InputWidth = 220 };
            inputs.Children.Add(_modelBox);
            inputs.Children.Add(_modelsButton);
            inputs.Children.Add(_loadButton);
            inputs.Children.Add(_loadRing);

            var notes = new StackPanel { Spacing = 2 };
            if (browseUrl is not null) notes.Children.Add(Link("Browse models", browseUrl));
            _error = page.Note("CriticalNoteStyle");
            _error.Visibility = Visibility.Collapsed;
            _error.MaxLines = 2;
            _noModels = page.Note("NoteStyle", Loc.T("No models listed; type the model id."));
            _noModels.Visibility = Visibility.Collapsed;
            notes.Children.Add(_error);
            notes.Children.Add(_noModels);
            ModelCard = Field("Model", inputs);
            ModelCard.Description = notes;

            _filterBox.PlaceholderText = Loc.T("Filter models");
            AutomationProperties.SetName(_filterBox, Loc.T("Filter"));
            _filterBox.TextChanged += (_, _) => UpdateChecklist();
            _scroller.Content = _checklist;
            _addBox.PlaceholderText = Loc.T("Add a model id");
            AutomationProperties.SetName(_addBox, Loc.T("Add a model"));
            _addBox.KeyDown += (_, e) =>
            {
                if (!IsEnter(e)) return;
                e.Handled = true;
                AddModel();
            };
            _hint = page.Note("NoteStyle", Loc.T("Type a model id and press Return, or load the models to pick from."));
            var shown = new StackPanel { Spacing = 8 };
            shown.Children.Add(_filterBox);
            shown.Children.Add(_scroller);
            shown.Children.Add(_addBox);
            shown.Children.Add(_hint);
            ShownCard = new SettingsCard
            {
                Header = Loc.T("In the AI pane's model menu"),
                ContentAlignment = ContentAlignment.Vertical,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = shown,
            };
        }

        public SettingsCard ModelCard { get; }
        public SettingsCard ShownCard { get; }

        public void Update(ProviderConfig config)
        {
            _config = config;
            SetText(_modelBox, config.DefaultModel);
            UpdateChecklist();
        }

        public void Cancel() => _loading?.Cancel();

        private void Change(Func<ProviderConfig, bool> differs, Action<ProviderConfig> change)
        {
            if (_page._syncing || _page.Closing || _page._providers.Config(_id) is not { } config || !differs(config)) return;
            _page._providers.UpdateProvider(_id, change);
        }

        /// <summary>Fills the menu from the provider's model list, off the UI thread (macOS <c>loadModels</c>).</summary>
        private async Task LoadAsync()
        {
            if (_loading is not null || _page._providers.Config(_id) is not { } config) return;
            using var loading = _loading = new CancellationTokenSource();
            _loadButton.IsEnabled = false;
            _loadRing.IsActive = true;
            _loadRing.Visibility = Visibility.Visible;
            string? error = null;
            try
            {
                _models = [.. await _page._providers.ListModelsAsync(config, loading.Token)];
            }
            catch (Exception e) when (!loading.IsCancellationRequested)
            {
                error = ErrorMessage(e, config);
            }
            catch (Exception)
            {
                // Cancelled: no result.
            }
            _loading = null;
            if (_page.Closing) return;
            _loadButton.IsEnabled = true;
            _loadRing.IsActive = false;
            _loadRing.Visibility = Visibility.Collapsed;
            _error.Text = error ?? "";
            _error.Visibility = Show(error is not null);
            _noModels.Visibility = Show(error is null && _models is { Count: 0 });
            _modelsMenu.Items.Clear();
            foreach (var id in _models ?? [])
            {
                var item = new MenuFlyoutItem { Text = id };
                item.Click += (_, _) => Change(c => c.DefaultModel != id, c => c.DefaultModel = id);
                _modelsMenu.Items.Add(item);
            }
            var count = _models?.Count ?? 0;
            _modelsButton.Content = Loc.Plural("%lld models", count);
            _modelsButton.Visibility = Show(count > 0);
            UpdateChecklist();
        }

        private void AddModel()
        {
            var id = _addBox.Text.Trim();
            _addBox.Text = "";
            if (id.Length == 0) return;
            Change(c => !c.ShownModels.Contains(id), c => c.ShownModels.Add(id));
        }

        /// <summary>The default model (always offered), the shown ones and the loaded ones, each with its checkbox.</summary>
        private void UpdateChecklist()
        {
            if (_config is not { } config) return;
            var defaultId = config.DefaultModel.Trim();
            List<string> choices = [];
            foreach (var id in config.ShownModels.Prepend(defaultId).Concat(_models ?? []))
            {
                if (id.Length > 0 && !choices.Contains(id)) choices.Add(id);
            }
            var isLong = choices.Count > LongList;
            _filterBox.Visibility = Show(isLong);
            _scroller.MaxHeight = isLong ? 160 : double.PositiveInfinity;
            // Only while its field shows: a filter left over from a longer list would hide checkboxes for good.
            var term = isLong ? _filterBox.Text.Trim() : "";
            List<string> shown = term.Length == 0 ? choices : [.. choices.Where(c => c.Contains(term, StringComparison.CurrentCultureIgnoreCase))];
            if (!shown.SequenceEqual(_shownIds))
            {
                _shownIds = shown;
                _checklist.Children.Clear();
                foreach (var id in shown) _checklist.Children.Add(CreateCheckBox(id));
            }
            foreach (var box in _checklist.Children.OfType<CheckBox>())
            {
                var id = (string)box.Tag;
                var isDefault = id == defaultId;
                box.Content = isDefault ? Loc.Format("%@ (default)", id) : id;
                box.IsChecked = isDefault || config.ShownModels.Contains(id);
                box.IsEnabled = !isDefault;
            }
            _hint.Visibility = Show(_models is null);
        }

        private CheckBox CreateCheckBox(string id)
        {
            var box = new CheckBox { Tag = id, Content = id };
            void Toggle()
            {
                var on = box.IsChecked == true;
                Change(
                    c => id != c.DefaultModel.Trim() && c.ShownModels.Contains(id) != on,
                    c =>
                    {
                        c.ShownModels.RemoveAll(m => m == id);
                        if (on) c.ShownModels.Add(id);
                    });
            }
            box.Checked += (_, _) => Toggle();
            box.Unchecked += (_, _) => Toggle();
            return box;
        }
    }

    /// <summary>
    /// Test, which shows OK beside it or a short error below (macOS <c>TestButton</c>); Cancel while it runs. A local
    /// host's Remove and a CLI's containment note sit here too.
    /// </summary>
    private sealed class TestField
    {
        private readonly ProvidersSettingsPage _page;
        private readonly string _id;
        private readonly string _title;
        private readonly Button _testButton = new();
        private readonly ProgressRing _ring = SmallRing();
        private readonly TextBlock _ok;
        private readonly TextBlock _error;
        private CancellationTokenSource? _running;
        private bool _enabled = true;

        public TestField(ProvidersSettingsPage page, string id, string title, Button? remove, string? note = null)
        {
            _page = page;
            _id = id;
            _title = Loc.T(title);
            _testButton.Content = _title;
            _testButton.Click += (_, _) => _ = RunAsync();
            _ok = page.Note("SuccessNoteStyle", Loc.T("OK"));
            _ok.Visibility = Visibility.Collapsed;
            _error = page.Note("CriticalNoteStyle");
            _error.Visibility = Visibility.Collapsed;
            _error.MaxLines = 3;

            var buttons = new FieldPanel();
            buttons.Children.Add(_testButton);
            buttons.Children.Add(_ring);
            buttons.Children.Add(_ok);
            if (remove is not null) buttons.Children.Add(remove);

            var notes = new StackPanel { Spacing = 2 };
            if (note is not null) notes.Children.Add(page.Note("NoteStyle", Loc.T(note)));
            notes.Children.Add(_error);
            Card = new SettingsCard { Header = buttons, Description = notes };
        }

        public SettingsCard Card { get; }

        /// <summary>Off for a CLI that is not installed.</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                _testButton.IsEnabled = value || _running is not null;
            }
        }

        public void Cancel() => _running?.Cancel();

        private async Task RunAsync()
        {
            if (_running is { } running)
            {
                running.Cancel();
                return;
            }
            _ok.Visibility = _error.Visibility = Visibility.Collapsed;
            if (_page.Closing || _page._providers.Config(_id) is not { } config) return;
            using var cancellation = _running = new CancellationTokenSource();
            SetRunning(true);
            string? error = null;
            var ok = false;
            try
            {
                await _page._providers.TestAsync(config, cancellation.Token);
                ok = true;
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested)
            {
                error = ErrorMessage(e, config);
            }
            catch (Exception)
            {
                // Cancelled: no result.
            }
            _running = null;
            if (_page.Closing) return;
            SetRunning(false);
            _ok.Visibility = Show(ok);
            _error.Text = error ?? "";
            _error.Visibility = Show(error is not null);
        }

        private void SetRunning(bool running)
        {
            _testButton.Content = running ? Loc.T("Cancel") : _title;
            _testButton.IsEnabled = running || _enabled;
            _ring.IsActive = running;
            _ring.Visibility = Show(running);
        }
    }
}
