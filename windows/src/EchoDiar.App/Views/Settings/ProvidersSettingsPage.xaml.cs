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
/// Settings › AI Providers (macOS ProvidersSettingsView.swift), the Local AI, Cloud and CLIs inner tabs; the title bar
/// picks the tab (<see cref="AppViewModel.SubTabOf"/>). Local AI shows one group at a time (macOS <c>LocalAIGroup</c>),
/// picked in the tabs pinned above the page and kept in <see cref="AppViewModel.SettingsLocalAIGroup"/>: the in-app
/// language models (the models each feature uses are picked in <see cref="AISettingsPage"/>), the local transcription, diarization and embedding model downloads, and the local hosts. Cloud holds
/// the cloud provider cards (<see cref="ProviderCardList"/>: keys, base URLs and default models). Every change goes
/// through <see cref="ProvidersViewModel"/> and is saved after the settings delay. The provider rows are made in code and
/// updated in place, so typing in a field never rebuilds the row it is in; so are the model rows, whose state a download
/// changes often. The local hosts are scanned and the CLIs looked up once, when the page first shows (macOS: once per
/// window), then on Scan or on a new path override.
/// </summary>
public sealed partial class ProvidersSettingsPage : UserControl
{
    // macOS ids (deep links and search entries stay the same).
    public const string LocalAITab = "localAI";
    public const string CloudTab = "cloud";
    public const string CliTab = "cli";

    // The groups of Local AI (macOS LocalAIGroup's raw values), kept in AppViewModel.SettingsLocalAIGroup.
    public const string LanguageModelsGroup = "languageModels";
    public const string TranscriptionGroup = "transcription";
    public const string DiarizationGroup = "diarization";
    public const string EmbeddingGroup = "embedding";
    public const string LocalHostsGroup = "localHosts";

    /// <summary>macOS <c>SettingsTab.providers.subTabs</c>.</summary>
    public static IReadOnlyList<SettingsSubTab> SubTabs { get; } =
    [
        new(LocalAITab, "Local AI"),
        new(CloudTab, "Cloud"),
        new(CliTab, "CLIs"),
    ];

    /// <summary>Local AI's groups in tab order, with their macOS titles (macOS <c>LocalAIGroup.allCases</c>, <c>title</c>).</summary>
    private static readonly (string Id, string Title)[] LocalAIGroups =
    [
        (LanguageModelsGroup, "Language models"),
        (TranscriptionGroup, "Transcription"),
        (DiarizationGroup, "Diarization"),
        (EmbeddingGroup, "Embedding"),
        (LocalHostsGroup, "Local hosts"),
    ];

    /// <summary>
    /// The searchable rows (macOS SettingsSearch.swift, AI Providers), each the Tag of its element. The Cloud ones are
    /// macOS APIKeysSettingsView's, whose cards Cloud now shows.
    /// </summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Language models",
            ["LLM", "Windows AI", "Phi Silica", "local", "summary", "chat", "download", "GGUF", "Qwen", "Gemma", "offline"],
            LocalAITab, LanguageModelsGroup),
        new("Transcription models",
            ["download", "Whisper", "Parakeet", "speech to text", "local", "fast", "balanced", "quality"], LocalAITab,
            TranscriptionGroup),
        new("Diarization models", ["speaker model", "speakers", "pyannote", "download"], LocalAITab, DiarizationGroup),
        // macOS also lists its other embedders (E5, BGE-M3); the Windows catalog has Qwen3 in GGUF.
        new("Embedding models", ["embedder", "Qwen3", "GGUF", "RAG", "download"], LocalAITab, EmbeddingGroup),
        new("Local hosts", ["scan", "custom URL", "server", .. PresetNames(ProviderKind.Local)], LocalAITab, LocalHostsGroup),
        new("Language models",
            ["API key", "key", "Credential Manager", "cloud", "provider", "LLM", "model",
             .. PresetNames(ProviderKind.OpenAICompatible), .. PresetNames(ProviderKind.Anthropic)], CloudTab),
        new("Speech-to-text APIs",
            ["API key", "key", "Credential Manager", "cloud transcription", .. PresetNames(ProviderKind.SpeechToText)], CloudTab),
        // The speech-to-text cards, with their transcription and diarization models (ProviderCardList's Tags).
        new("Cloud transcription engines", ["upload", "API", "default engine", "xAI", .. PresetNames(ProviderKind.SpeechToText)], CloudTab),
        new("Cloud diarization",
            ["OpenAI", "ElevenLabs", "Deepgram", "AssemblyAI", "Gemini", "xAI", "Speechmatics", "Soniox", "Gladia", "speakers", "cloud"],
            CloudTab),
        new("Custom OpenAI-compatible endpoints",
            ["API key", "key", "custom endpoint", "add endpoint", "base URL", "authentication", "headers", "model"], CloudTab),
        new("CLIs", ["subscription", "command line", "terminal", .. PresetNames(ProviderKind.Cli)], CliTab),
    ];

    private static IEnumerable<string> PresetNames(ProviderKind kind) =>
        ProviderConfig.Presets().Where(p => p.Kind == kind).Select(p => p.DisplayName);

    private readonly AppViewModel _model;
    private readonly ProvidersViewModel _providers;
    private readonly ModelsViewModel _models;

    private readonly Dictionary<string, ApiRow> _apiRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CLIPreset, CliRow> _cliRows = [];

    /// <summary>The Cloud tab's provider cards (macOS <c>ProviderCardSections</c>).</summary>
    private readonly ProviderCardList _cards;

    /// <summary>One row per GGUF chat model and per embedding model, made once and updated in place.</summary>
    private readonly List<LocalModelRow> _modelRows = [];

    /// <summary>The local transcription and diarization model rows (macOS <c>ModelTierSections</c>), made once and updated in place.</summary>
    private readonly LocalModelRows _localRows;

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    /// <summary>The group tabs are being set from the model: their selection change is not the user's.</summary>
    private bool _syncingGroups;

    private string _foundHostsKey = "";
    private bool _scanned;

    public ProvidersSettingsPage(AppViewModel model)
    {
        _model = model;
        _providers = model.ProvidersModel;
        _models = model.ModelsModel;
        InitializeComponent();
        foreach (var entry in ProvidersViewModel.LocalModelEntries) LocalModelsList.Children.Add(CreateModelRow(entry));
        // Their downloads only (macOS embeddingSection): the one in use is picked in AI Settings › Library chat.
        foreach (var entry in ModelCatalog.Embedding) EmbeddingList.Children.Add(CreateModelRow(entry));
        _localRows = new LocalModelRows(_models, Resources);
        _localRows.AddSections(TranscriptionList, ModelKind.Asr);
        _localRows.AddSections(DiarizationList, ModelKind.Diarization);
        _cards = new ProviderCardList(model, Resources, CloudCardsPanel,
            () => _model.SettingsSection == SettingsSearch.Providers && _model.SubTabOf(SettingsSearch.Providers) == CloudTab);
        foreach (var (id, title) in LocalAIGroups) LocalAIGroupsBar.Items.Add(new SelectorBarItem { Text = Loc.T(title), Tag = id });
        // The model's group selected first, so the bar never picks one of its own.
        ShowGroup();
        // Instant (click first): the group's panel shows in the same frame.
        LocalAIGroupsBar.SelectionChanged += (_, _) => OnGroupSelected();

        WindowsAICheckButton.Click += (_, _) => _ = _providers.RefreshWindowsAIAsync();
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
            _models.StatesChanged += _localRows.Update;
            // It also reads the saved keys again.
            _cards.Attach();
            Refresh();
            ShowTab();
            UpdateScan();
            UpdateCliStatus();
            UpdateModelRows();
            _localRows.Update();
            // The installed models read again (every model row's state), then their size.
            _ = _models.LoadAsync();
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
            _models.StatesChanged -= _localRows.Update;
            _cards.Detach();
            // macOS: a test or a model list stops when its row goes away.
            foreach (var row in _apiRows.Values) row.Cancel();
            foreach (var row in _cliRows.Values) row.Cancel();
        };
    }

    private bool Closing => _model.IsClosing;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.SettingsSubTab) or nameof(AppViewModel.SettingsExpandedProvider)
            or nameof(AppViewModel.SettingsLocalAIGroup)) ShowTab();
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

    /// <summary>The picked tab's panel, with Local AI's group tabs above it and its picked group.</summary>
    private void ShowTab()
    {
        var tab = _model.SubTabOf(SettingsSearch.Providers);
        LocalAIPanel.Visibility = LocalAIGroupsBar.Visibility = Show(tab == LocalAITab);
        CloudPanel.Visibility = Show(tab == CloudTab);
        CliPanel.Visibility = Show(tab == CliTab);
        ExpandRequestedProvider(tab);
        ShowGroup();
    }

    /// <summary>Local AI's picked group (macOS <c>settingsLocalAIGroup</c>): its panel shows and its tab is selected.</summary>
    private void ShowGroup()
    {
        var group = _model.SettingsLocalAIGroup;
        LanguageModelsPanel.Visibility = Show(group == LanguageModelsGroup);
        TranscriptionPanel.Visibility = Show(group == TranscriptionGroup);
        DiarizationPanel.Visibility = Show(group == DiarizationGroup);
        EmbeddingPanel.Visibility = Show(group == EmbeddingGroup);
        LocalHostsPanel.Visibility = Show(group == LocalHostsGroup);
        var selected = LocalAIGroupsBar.Items.FirstOrDefault(i => i.Tag as string == group);
        if (LocalAIGroupsBar.SelectedItem == selected) return;
        _syncingGroups = true;
        try
        {
            LocalAIGroupsBar.SelectedItem = selected;
        }
        finally
        {
            _syncingGroups = false;
        }
    }

    /// <summary>A picked group tab shows its group.</summary>
    private void OnGroupSelected()
    {
        if (_syncingGroups || Closing || LocalAIGroupsBar.SelectedItem is not { Tag: string id }) return;
        _model.SettingsLocalAIGroup = id;
    }

    /// <summary>
    /// A Set Up in Settings › AI Settings › Library chat (macOS <c>settingsExpandedProvider</c>): its saved local host's row in Local AI › Local
    /// hosts (picked first, as macOS does) expands and comes into view, and the request is cleared. A cloud provider's
    /// request is the Cloud cards' (<see cref="ProviderCardList"/>).
    /// </summary>
    private void ExpandRequestedProvider(string? tab)
    {
        // Only while this section shows: a page just left may still hear a request meant for another.
        if (Closing || _model.SettingsSection != SettingsSearch.Providers || _model.SettingsExpandedProvider is not { } id) return;
        // Only on the tab its row is on.
        if (tab != LocalAITab || _apiRows.GetValueOrDefault(id)?.Expander is not { } expander) return;
        _model.SettingsExpandedProvider = null;
        _model.SettingsLocalAIGroup = LocalHostsGroup;
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
            UpdateFoundHosts();
            RefreshRows();
        }
        finally
        {
            _syncing = false;
        }
    }

    // Local AI

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
    /// A GGUF model's card (macOS <c>ModelRow</c> of an LLM): its name and Default badge (the catalog default; AI Settings
    /// picks the one used); its details, quality, memory warning and last error below; its actions on the
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
    /// The saved local hosts and the CLIs, each a row made once per provider and updated in place; a list is rebuilt only
    /// when its providers change. The cloud providers are the cards' (<see cref="ProviderCardList"/>).
    /// </summary>
    private void RefreshRows()
    {
        var configs = _providers.ProviderConfigs;
        var localShown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SyncList(LocalHostsList, _providers.SavedLocalHosts, _apiRows, id => new ApiRow(this, id), localShown);
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

    /// <summary>A provider's <see cref="ModelField"/>, with this page's view model, sync and closing state and styles.</summary>
    private ModelField NewModelField(string id, string prompt, string? browseUrl) =>
        new(_providers, () => _syncing, () => Closing, Resources, id, prompt, browseUrl);

    /// <summary>
    /// An expanded row's fields in one item, so their inputs line up past the labels (macOS <c>fieldLabelColumn()</c>,
    /// as the Cloud cards).
    /// </summary>
    private static SettingsCard FieldsCard(FieldGrid fields) => new()
    {
        ContentAlignment = ContentAlignment.Vertical,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Content = fields.Element,
    };

    /// <summary>
    /// A row's model id with its notes, then the checklist of the models the AI pane's model menu offers, in the input
    /// column (macOS <c>ModelField</c> in a row's <c>fieldLabelColumn()</c>).
    /// </summary>
    private void AddModelRows(FieldGrid fields, ModelField field)
    {
        fields.Add(Loc.T("Model"), field.Input, field.Notes);
        fields.Add(null, Note("NoteStyle", Loc.T("In the AI pane's model menu")), field.ShownPanel);
    }

    /// <summary>Sets a text box's text unless it already shows it (the caret stays where the user types).</summary>
    private static void SetText(TextBox box, string text)
    {
        if (box.Text != text) box.Text = text;
    }

    private static ProgressRing SmallRing() => new()
    {
        Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
    };

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
    /// A saved local host (macOS <c>APIProviderRow</c>): the Enabled switch; expanded, its base URL, model, the models
    /// shown in the AI pane's menu and its embedding model in a label column and an input column; below, Test and Remove.
    /// </summary>
    private sealed class ApiRow : IProviderRow
    {
        private readonly ProvidersSettingsPage _page;
        private readonly string _id;
        private readonly ToggleSwitch _enabled = new();
        private readonly TextBox _baseBox = new();
        private readonly FieldGrid _fields = new();
        private readonly ModelField _modelField;
        private readonly EmbeddingModelField _embeddingField;
        private readonly TestField _test;

        public ApiRow(ProvidersSettingsPage page, string id)
        {
            _page = page;
            _id = id;
            Expander = new SettingsExpander { Content = _enabled };
            _enabled.Toggled += (_, _) => Change(c => c.Enabled != _enabled.IsOn, c => c.Enabled = _enabled.IsOn);

            AutomationProperties.SetName(_baseBox, Loc.T("Base URL"));
            _baseBox.TextChanged += (_, _) => Change(c => c.BaseURL != _baseBox.Text, c => c.BaseURL = _baseBox.Text);
            _fields.Add(Loc.T("Base URL"), new FieldPanel { InputWidth = 320, Children = { _baseBox } });

            _modelField = page.NewModelField(id, "Model id", null);
            page.AddModelRows(_fields, _modelField);

            // Every local host can embed (macOS RAGSettingsView.embeddingConfigs).
            _embeddingField = new EmbeddingModelField(page._providers, () => page._syncing, () => page.Closing, page.Resources, id);
            _fields.Add(Loc.T("Embedding model"), _embeddingField.Input, _embeddingField.Notes);

            var remove = new Button { Content = Loc.T("Remove") };
            remove.Click += (_, _) => _ = _page._providers.RemoveProviderAsync(_id);
            _test = new TestField(page, id, "Test", remove);

            Expander.Items.Add(FieldsCard(_fields));
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
            _embeddingField.Update(config);
        }

        public void Cancel()
        {
            _test.Cancel();
            _modelField.Cancel();
            _embeddingField.Cancel();
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
    /// override, its model and shown models in a label column and an input column; below, what it can still do despite
    /// its flags, and Test.
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
        private readonly FieldGrid _fields = new();
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
            _fields.Add(Loc.T("Path override"), new FieldPanel { InputWidth = 320, Children = { _pathBox } }, _pathNote);

            _modelField = page.NewModelField(_id, preset switch
            {
                CLIPreset.OpenCode => "provider/model",
                CLIPreset.Claude => "sonnet, opus or haiku (empty = default)",
                _ => "Empty = the CLI's default",
            }, null);
            page.AddModelRows(_fields, _modelField);
            _test = new TestField(page, _id, "Test (uses your subscription)", null, CLIRunner.ContainmentNote(preset));

            Expander.Items.Add(FieldsCard(_fields));
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
