using System.ComponentModel;
using CommunityToolkit.WinUI.Controls;
using EchoDiar.AI;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › AI (macOS ProvidersSettingsView.swift), the On this PC, Cloud APIs and CLIs inner tabs; the title bar
/// picks the tab (<see cref="AppViewModel.SubTabOf"/>). Every change goes through <see cref="ProvidersViewModel"/> and
/// is saved after the settings delay; API keys go to Credential Manager only and are never read back into the UI. The
/// provider rows are made in code and updated in place, so typing in a field never rebuilds the row it is in. The local
/// hosts are scanned and the CLIs looked up once, when the page first shows (macOS: once per window), then on Scan or
/// on a new path override.
/// </summary>
public sealed partial class ProvidersSettingsPage : UserControl
{
    // macOS ids (deep links and search entries stay the same); "On this PC" is the Windows title.
    public const string OnThisPCTab = "onThisMac";
    public const string CloudAPIsTab = "cloudAPIs";
    public const string CliTab = "cli";

    /// <summary>macOS <c>SettingsTab.providers.subTabs</c>.</summary>
    public static IReadOnlyList<SettingsSubTab> SubTabs { get; } =
    [
        new(OnThisPCTab, "On this PC"),
        new(CloudAPIsTab, "Cloud APIs"),
        new(CliTab, "CLIs"),
    ];

    /// <summary>The searchable rows (macOS SettingsSearch.swift, AI), each the Tag of its element.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("AI models (summaries and chat)",
            ["LLM", "Windows AI", "Phi Silica", "local", "summary", "chat", "default model", "language"], OnThisPCTab),
        new("Chat context", ["chat", "history", "context", "messages", "memory"], OnThisPCTab),
        new("Local hosts", ["scan", "custom URL", "server", .. PresetNames(ProviderKind.Local)], OnThisPCTab),
        new("Cloud APIs",
            ["API key", "provider", "LLM", .. PresetNames(ProviderKind.OpenAICompatible), .. PresetNames(ProviderKind.Anthropic)],
            CloudAPIsTab),
        new("Custom OpenAI-compatible endpoints", ["add endpoint", "base URL", "API key", "headers"], CloudAPIsTab),
        new("CLIs", ["subscription", "command line", "terminal", .. PresetNames(ProviderKind.Cli)], CliTab),
    ];

    private static IEnumerable<string> PresetNames(ProviderKind kind) =>
        ProviderConfig.Presets().Where(p => p.Kind == kind).Select(p => p.DisplayName);

    /// <summary>Where to get a preset's API key and look up its model ids (macOS <c>providerLinks</c>).</summary>
    private static readonly Dictionary<string, (string Keys, string Models)> ProviderLinks = new()
    {
        ["openai"] = ("https://platform.openai.com/api-keys", "https://developers.openai.com/api/docs/models"),
        ["anthropic"] = ("https://platform.claude.com/settings/keys", "https://platform.claude.com/docs/en/models/overview"),
        ["xai"] = ("https://console.x.ai", "https://docs.x.ai/developers/models"),
        ["gemini"] = ("https://aistudio.google.com/apikey", "https://ai.google.dev/gemini-api/docs/models"),
        ["mistral"] = ("https://console.mistral.ai/api-keys", "https://docs.mistral.ai/models"),
        ["azure"] = ("https://ai.azure.com", "https://learn.microsoft.com/azure/ai-foundry/openai/concepts/models"),
        ["openrouter"] = ("https://openrouter.ai/settings/keys", "https://openrouter.ai/models"),
        ["groq"] = ("https://console.groq.com/keys", "https://console.groq.com/docs/models"),
        ["deepseek"] = ("https://platform.deepseek.com/api_keys", "https://api-docs.deepseek.com/quick_start/pricing"),
    };

    /// <summary>The summary languages offered (macOS <c>AILanguageMenu</c>), plus a hand-edited one.</summary>
    private static readonly (string Code, string Name)[] Languages =
        [("auto", "Auto"), ("en", "English"), ("fr", "French"), ("vi", "Vietnamese")];

    private readonly AppViewModel _model;
    private readonly ProvidersViewModel _providers;

    private readonly Dictionary<string, ApiRow> _apiRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CLIPreset, CliRow> _cliRows = [];

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    private List<(AIProviderInfo Provider, string Model)> _modelChoices = [];
    /// <summary>Each model choice's title and why it cannot run, as listed.</summary>
    private List<string> _modelKeys = [];
    private List<string> _languageCodes = [];
    private List<int> _chatContextValues = [];
    private string _foundHostsKey = "";
    private bool _scanned;

    public ProvidersSettingsPage(AppViewModel model)
    {
        _model = model;
        _providers = model.ProvidersModel;
        InitializeComponent();
        LanguageCard.Description = Loc.T("Auto = the transcript's language for summaries, the question's for answers.");

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
        ScanButton.Click += (_, _) => _ = _providers.ScanAsync();
        CustomUrlBox.TextChanged += (_, _) => AddCustomButton.IsEnabled = ProviderInput.HostAndPort(CustomUrlBox.Text) is not null;
        CustomUrlBox.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            AddCustomLocal();
        };
        AddCustomButton.Click += (_, _) => AddCustomLocal();
        AddEndpointButton.Click += (_, _) => _providers.AddEndpoint();

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _providers.PropertyChanged += OnProvidersPropertyChanged;
            _providers.Changed += Refresh;
            _providers.CliStatusChanged += UpdateCliStatus;
            Refresh();
            ShowTab();
            UpdateScan();
            UpdateCliStatus();
            _ = _providers.RefreshKeysAsync();
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
            // macOS: a test or a model list stops when its row goes away.
            foreach (var row in _apiRows.Values) row.Cancel();
            foreach (var row in _cliRows.Values) row.Cancel();
        };
    }

    private bool Closing => _model.IsClosing;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SettingsSubTab)) ShowTab();
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
        CloudPanel.Visibility = Show(tab == CloudAPIsTab);
        CliPanel.Visibility = Show(tab == CliTab);
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
                ChatContextCombo.Items.Add(value == 0 ? Loc.T("Whole chat") : Loc.Format("%lld messages", value));
            }
        }
        ChatContextCombo.SelectedIndex = _chatContextValues.IndexOf(current);
    }

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
                Description = Loc.Format("%@ - %lld model%@", host.BaseURL, count, count == 1 ? "" : "s"),
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
        var local = _providers.SavedLocalHosts;
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SyncList(CloudPresetsList, cloud, shown);
        SyncList(CustomList, custom, shown);
        SyncList(LocalHostsList, local, shown);
        foreach (var id in _apiRows.Keys.Where(id => !shown.Contains(id)).ToList())
        {
            _apiRows[id].Cancel();
            _apiRows.Remove(id);
        }

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

    private void SyncList(StackPanel list, IReadOnlyList<ProviderConfig> configs, HashSet<string> shown)
    {
        List<ApiRow> rows = [];
        foreach (var config in configs)
        {
            if (!shown.Add(config.Id)) continue;
            if (!_apiRows.TryGetValue(config.Id, out var row)) _apiRows[config.Id] = row = new ApiRow(this, config.Id);
            row.Update(config);
            rows.Add(row);
        }
        if (list.Children.SequenceEqual(rows.Select(r => (UIElement)r.Expander))) return;
        list.Children.Clear();
        foreach (var row in rows)
        {
            // A row moved from another list (its kind changed) leaves that one first.
            if (row.Expander.Parent is Panel parent) parent.Children.Remove(row.Expander);
            list.Children.Add(row.Expander);
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

    /// <summary>
    /// A cloud preset, local host or custom endpoint (macOS <c>APIProviderRow</c>): the Enabled switch; expanded, its
    /// name and authentication (custom endpoints), extra headers (custom endpoints), API key, base URL, model, the
    /// models shown in the AI pane's menu, Test, and Remove (local hosts and custom endpoints).
    /// </summary>
    private sealed class ApiRow
    {
        private static readonly AuthStyle[] AuthStyles = [AuthStyle.Bearer, AuthStyle.ApiKeyHeader, AuthStyle.XApiKey, AuthStyle.None];

        private readonly ProvidersSettingsPage _page;
        private readonly string _id;
        private readonly ToggleSwitch _enabled = new();
        private readonly SettingsCard _nameCard;
        private readonly TextBox _nameBox = new() { MinWidth = 260 };
        private readonly SettingsCard _authCard;
        private readonly ComboBox _authCombo = new() { MinWidth = 180 };
        private readonly SettingsCard _headersCard;
        private readonly TextBox _headersBox = new() { MinWidth = 260, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "HTTP-Referer: https://example.com" };
        private readonly TextBlock _headersRefused;
        private readonly SettingsCard _keyCard;
        private readonly PasswordBox _keyBox = new();
        private readonly StackPanel _savedLabel = new() { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
        private readonly Button _removeKeyButton = new() { Visibility = Visibility.Collapsed };
        private readonly HyperlinkButton? _getKeyLink;
        private readonly TextBlock _keyError;
        private readonly TextBox _baseBox = new();
        private readonly TextBlock _azureNote;
        private readonly TextBlock _httpWarning;
        private readonly ModelField _modelField;
        private readonly TestField _test;
        private readonly Button _removeButton = new() { Visibility = Visibility.Collapsed };
        private ProviderConfig? _config;

        public ApiRow(ProvidersSettingsPage page, string id)
        {
            _page = page;
            _id = id;
            Expander = new SettingsExpander { Content = _enabled };
            _enabled.Toggled += (_, _) => Change(c => c.Enabled != _enabled.IsOn, c => c.Enabled = _enabled.IsOn);

            _nameCard = Field("Name", _nameBox);
            AutomationProperties.SetName(_nameBox, Loc.T("Name"));
            _nameBox.TextChanged += (_, _) => Change(c => c.DisplayName != _nameBox.Text, c => c.DisplayName = _nameBox.Text);

            foreach (var title in new[] { "Bearer token", "api-key header", "x-api-key header", "None" }) _authCombo.Items.Add(Loc.T(title));
            AutomationProperties.SetName(_authCombo, Loc.T("Authentication"));
            _authCard = Field("Authentication", _authCombo);
            _authCombo.SelectionChanged += (_, _) =>
            {
                var index = _authCombo.SelectedIndex;
                if (index < 0) return;
                Change(c => c.AuthStyle != AuthStyles[index], c => c.AuthStyle = AuthStyles[index]);
            };

            // Windows addition: macOS has no field for them. Saved in settings.json, never a key.
            _headersCard = Field("Extra headers", _headersBox);
            // A line naming a key header is not saved; this says why it went away.
            _headersRefused = page.Note("CautionNoteStyle");
            _headersRefused.Visibility = Visibility.Collapsed;
            var headerNotes = new StackPanel { Spacing = 2 };
            headerNotes.Children.Add(page.Note("NoteStyle", Loc.T("One per line, Name: value. Saved in the settings file: put API keys in the API key field.")));
            headerNotes.Children.Add(_headersRefused);
            _headersCard.Description = headerNotes;
            AutomationProperties.SetName(_headersBox, Loc.T("Extra headers"));
            _headersBox.LostFocus += (_, _) =>
            {
                var headers = ProviderInput.ParseHeaders(_headersBox.Text, out var refused);
                _headersRefused.Text = refused.Count == 0 ? "" : Loc.Format(
                    "%@ not saved: put the key in the API key field, which keeps it in Credential Manager.", string.Join(", ", refused));
                _headersRefused.Visibility = Show(refused.Count > 0);
                Change(c => !SameHeaders(c.ExtraHeaders, headers), c => c.ExtraHeaders = headers);
                // The refused lines leave the box even when nothing else changed.
                if (refused.Count > 0) _headersBox.Text = HeadersText(headers);
            };

            // The key (macOS APIKeyField): saved on Enter, on leaving the field, by Save and by Test; only "Saved" shows.
            AutomationProperties.SetName(_keyBox, Loc.T("API key"));
            _keyBox.KeyDown += (_, e) =>
            {
                if (!IsEnter(e)) return;
                e.Handled = true;
                _ = SaveKeyAsync();
            };
            _keyBox.LostFocus += (_, _) => _ = SaveKeyAsync();
            _keyBox.PasswordChanged += (_, _) =>
            {
                UpdateHttpWarning();
                // Null only while the constructor runs.
                _test?.UpdateSave();
            };
            _savedLabel.Children.Add(new FontIcon { Style = (Style)page.Resources["SuccessIconStyle"], Glyph = "" });
            _savedLabel.Children.Add(page.Note("SuccessNoteStyle", Loc.T("Saved")));
            _removeKeyButton.Content = Loc.T("Remove");
            _removeKeyButton.Click += async (_, _) => ShowKeyError(await _page._providers.DeleteApiKeyAsync(_id));
            if (ProviderLinks.TryGetValue(id, out var links)) _getKeyLink = Link("Get an API key", links.Keys);
            var keyPanel = new FieldPanel { InputWidth = 260 };
            keyPanel.Children.Add(_keyBox);
            keyPanel.Children.Add(_savedLabel);
            keyPanel.Children.Add(_removeKeyButton);
            if (_getKeyLink is not null) keyPanel.Children.Add(_getKeyLink);
            _keyError = page.Note("CriticalNoteStyle");
            _keyError.Visibility = Visibility.Collapsed;
            _keyCard = Field("API key", keyPanel);
            _keyCard.Description = _keyError;

            AutomationProperties.SetName(_baseBox, Loc.T("Base URL"));
            _baseBox.TextChanged += (_, _) => Change(c => c.BaseURL != _baseBox.Text, c => c.BaseURL = _baseBox.Text);
            _azureNote = page.Note("NoteStyle", Loc.T("Required: your resource's https://<resource>.openai.azure.com/openai/v1 URL."));
            _httpWarning = page.Note("CautionNoteStyle");
            var baseNotes = new StackPanel { Spacing = 2 };
            baseNotes.Children.Add(_azureNote);
            baseNotes.Children.Add(_httpWarning);
            var baseCard = Field("Base URL", new FieldPanel { InputWidth = 320, Children = { _baseBox } });
            baseCard.Description = baseNotes;

            _modelField = new ModelField(page, id, id == "azure" ? "Deployment name" : "Model id",
                ProviderLinks.TryGetValue(id, out var modelLinks) ? modelLinks.Models : null);

            _removeButton.Content = Loc.T("Remove");
            _removeButton.Click += (_, _) => _ = _page._providers.RemoveProviderAsync(_id);
            _test = new TestField(page, id, "Test", PendingKeyAsync, () => HasPendingKey, _removeButton);

            Expander.Items.Add(_nameCard);
            Expander.Items.Add(_authCard);
            Expander.Items.Add(_headersCard);
            Expander.Items.Add(_keyCard);
            Expander.Items.Add(baseCard);
            Expander.Items.Add(_modelField.ModelCard);
            Expander.Items.Add(_modelField.ShownCard);
            Expander.Items.Add(_test.Card);
        }

        public SettingsExpander Expander { get; }

        private bool IsCustom => _config is { Preset: null };

        /// <summary>Shows <paramref name="config"/>; fields the user is editing already show their own text.</summary>
        public void Update(ProviderConfig config)
        {
            _config = config;
            var name = config.DisplayName.Length == 0 ? Loc.T("Untitled") : config.DisplayName;
            Expander.Header = name;
            if (config.Kind == ProviderKind.Local || IsCustom && config.BaseURL.Length > 0) Expander.Description = config.BaseURL;
            else Expander.ClearValue(SettingsExpander.DescriptionProperty);
            AutomationProperties.SetName(_enabled, name);
            _enabled.IsOn = config.Enabled;
            var customApi = IsCustom && config.Kind == ProviderKind.OpenAICompatible;
            _nameCard.Visibility = _authCard.Visibility = _headersCard.Visibility = Show(customApi);
            SetText(_nameBox, config.DisplayName);
            _authCombo.SelectedIndex = Array.IndexOf(AuthStyles, config.AuthStyle);
            // Saved on leaving the field: a half-typed line must not be replaced while the user types it.
            if (_headersBox.FocusState == FocusState.Unfocused
                && !SameHeaders(ProviderInput.ParseHeaders(_headersBox.Text, out _), config.ExtraHeaders))
                _headersBox.Text = HeadersText(config.ExtraHeaders);
            _keyCard.Visibility = Show(config.AuthStyle != AuthStyle.None);
            SetText(_baseBox, config.BaseURL);
            _baseBox.PlaceholderText = config.Id == "azure" ? "https://<resource>.openai.azure.com/openai/v1" : "";
            _azureNote.Visibility = Show(config.Id == "azure");
            UpdateKey();
            _modelField.Update(config);
            _removeButton.Visibility = Show(config.Kind == ProviderKind.Local || IsCustom);
            _test.Update(config);
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

        private void UpdateKey()
        {
            var saved = _page._providers.HasApiKey(_id);
            _savedLabel.Visibility = _removeKeyButton.Visibility = Show(saved);
            if (_getKeyLink is not null) _getKeyLink.Visibility = Show(!saved);
            _keyBox.PlaceholderText = Loc.T(saved ? "Enter a new key to replace it" : "Paste the key, then Save");
            UpdateHttpWarning();
        }

        /// <summary>The key would go unencrypted to another machine (Phase 22 security review): say so under the URL.</summary>
        private void UpdateHttpWarning()
        {
            if (_config is not { } config) return;
            var sendsKey = _page._providers.HasApiKey(_id) || _keyBox.Password.Length > 0;
            var clear = ProviderInput.SendsKeyInClear(config, sendsKey);
            _httpWarning.Visibility = Show(clear);
            if (clear && Uri.TryCreate(config.BaseURL.Trim(), UriKind.Absolute, out var url))
            {
                _httpWarning.Text = Loc.Format("The API key is sent unencrypted over http to %@. Use https, or a host on this PC.", url.Host);
            }
        }

        /// <summary>Saves the typed key (nothing when blank) and clears the field; false, with the error shown, when refused.</summary>
        private async Task<bool> SaveKeyAsync()
        {
            var text = _keyBox.Password;
            if (string.IsNullOrWhiteSpace(text)) return true;
            var error = await _page._providers.SaveApiKeyAsync(_id, text);
            if (_page.Closing) return false;
            if (error is null && _keyBox.Password == text) _keyBox.Password = "";
            ShowKeyError(error);
            return error is null;
        }

        /// <summary>Save, and Test before it runs: a key typed without Enter is the one tested (macOS <c>TestButton.run</c>).</summary>
        private Task<bool> PendingKeyAsync() =>
            _config is { AuthStyle: not AuthStyle.None } ? SaveKeyAsync() : Task.FromResult(true);

        private void ShowKeyError(string? error)
        {
            if (_page.Closing) return;
            _keyError.Text = error ?? "";
            _keyError.Visibility = Show(error is not null);
        }

        /// <summary>Whether a key is typed and not saved yet (Save is on then).</summary>
        private bool HasPendingKey => !string.IsNullOrWhiteSpace(_keyBox.Password);

        private static bool SameHeaders(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
            a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);

        private static string HeadersText(Dictionary<string, string> headers) =>
            string.Join("\r", headers.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key}: {h.Value}"));
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
            _test = new TestField(page, _id, "Test (uses your subscription)", null, null, null, CLIRunner.ContainmentNote(preset));

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
            _test.Update(config);
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
            _modelsButton.Content = Loc.Format("%lld model%@", count, count == 1 ? "" : "s");
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
    /// Test, which shows OK beside it or a short error below (macOS <c>TestButton</c>); Cancel while it runs. With a key
    /// field, Save writes the typed key and Test saves it first. A row's Remove and a CLI's containment note sit here too.
    /// </summary>
    private sealed class TestField
    {
        private readonly ProvidersSettingsPage _page;
        private readonly string _id;
        private readonly string _title;
        private readonly Func<Task<bool>>? _saveKey;
        private readonly Func<bool>? _hasPendingKey;
        private readonly Button _testButton = new();
        private readonly Button _saveButton = new() { Visibility = Visibility.Collapsed };
        private readonly ProgressRing _ring = SmallRing();
        private readonly TextBlock _ok;
        private readonly TextBlock _error;
        private CancellationTokenSource? _running;
        private bool _enabled = true;
        private ProviderConfig? _config;

        public TestField(
            ProvidersSettingsPage page, string id, string title, Func<Task<bool>>? saveKey, Func<bool>? hasPendingKey, Button? remove,
            string? note = null)
        {
            _page = page;
            _id = id;
            _title = Loc.T(title);
            _saveKey = saveKey;
            _hasPendingKey = hasPendingKey;
            _testButton.Content = _title;
            _testButton.Click += (_, _) => _ = RunAsync();
            _saveButton.Content = Loc.T("Save");
            _saveButton.Click += async (_, _) =>
            {
                if (_saveKey is not null) await _saveKey();
                UpdateSave();
            };
            _ok = page.Note("SuccessNoteStyle", Loc.T("OK"));
            _ok.Visibility = Visibility.Collapsed;
            _error = page.Note("CriticalNoteStyle");
            _error.Visibility = Visibility.Collapsed;
            _error.MaxLines = 3;

            var buttons = new FieldPanel();
            buttons.Children.Add(_testButton);
            buttons.Children.Add(_saveButton);
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

        public void Update(ProviderConfig config)
        {
            _config = config;
            _saveButton.Visibility = Show(_saveKey is not null && config.AuthStyle != AuthStyle.None);
            UpdateSave();
        }

        /// <summary>Save is on while a key is typed and no test runs.</summary>
        public void UpdateSave() => _saveButton.IsEnabled = _running is null && _hasPendingKey?.Invoke() == true;

        public void Cancel() => _running?.Cancel();

        private async Task RunAsync()
        {
            if (_running is { } running)
            {
                running.Cancel();
                return;
            }
            _ok.Visibility = _error.Visibility = Visibility.Collapsed;
            // A key pasted without Enter is tested, not the one already saved.
            if (_saveKey is not null && !await _saveKey()) return;
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
            UpdateSave();
        }
    }
}
