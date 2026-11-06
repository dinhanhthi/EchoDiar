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

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › AI Settings › Library chat (macOS RAGSettingsView.swift), shown by <see cref="AISettingsPage"/> on its tab:
/// the opt-in switch of the library chat, the index's status with Rebuild, then the embedder of the index: the catalog's
/// GGUF embedding models (downloaded in AI Providers › Local AI › Embedding), or an enabled OpenAI-compatible or
/// local-host provider, whose model is set in its card in AI Providers. Picking a provider whose destination leaves this
/// PC asks once per provider and destination (<see cref="AppViewModel.PickAPIEmbedder"/>, <c>cloudEmbeddingConfirmed</c>);
/// the window shows the question. The Apple NL embedder is not built on Windows: a settings file holding <c>apple-nl</c>
/// shows no embedder picked, and the index says to pick one. A macOS id of a catalog model
/// (<c>mlx-qwen3-embedding-0.6b</c>) shows its GGUF counterpart picked, and stays as it is until another is picked.
/// </summary>
public sealed partial class RAGSettingsPage : UserControl
{
    /// <summary>
    /// The searchable rows (macOS SettingsSearch.swift, AI Settings › Library chat), each the Tag of its element; listed
    /// in <see cref="AISettingsPage.SearchEntries"/>.
    /// </summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Ask across conversations", ["RAG", "global chat", "library", "search", "index"], AISettingsPage.LibraryTab),
        // macOS also lists its other embedders (E5, BGE-M3, Apple); the Windows catalog has Qwen3 in GGUF.
        new("Embedding model", ["embedder", "Qwen3", "GGUF", "API", "RAG"], AISettingsPage.LibraryTab),
        new("Rebuild index", ["reindex", "index status", "indexed conversations", "RAG"], AISettingsPage.LibraryTab),
    ];

    /// <summary>The model filled in when a provider is picked, by preset (macOS <c>suggestedModels</c>).</summary>
    internal static readonly Dictionary<string, string> SuggestedModels = new()
    {
        ["openai"] = "text-embedding-3-small",
        ["gemini"] = "gemini-embedding-001",
        ["mistral"] = "mistral-embed",
        ["ollama"] = "nomic-embed-text",
    };

    /// <summary>The OpenAI-compatible presets with an embeddings endpoint (macOS <c>embeddingConfigs</c>).</summary>
    internal static readonly HashSet<string> EmbeddingPresets = ["openai", "gemini", "mistral", "azure"];

    private readonly AppViewModel _model;
    private readonly ProvidersViewModel _providers;

    /// <summary>One row per catalog embedding model, made once and updated in place.</summary>
    private readonly List<EmbedderRow> _embedderRows = [];

    /// <summary>One row per provider that can embed, updated in place; the list is rebuilt when the providers change.</summary>
    private readonly Dictionary<string, ProviderRow> _providerRows = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _providerIds = [];

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    public RAGSettingsPage(AppViewModel model)
    {
        _model = model;
        _providers = model.ProvidersModel;
        InitializeComponent();
        EnableCard.Description = Loc.T(
            "Answers questions about all your transcribed conversations, citing the conversation and the moment. The index stays on this PC unless you pick an API embedder.");
        foreach (var entry in ModelCatalog.Embedding)
        {
            var row = new EmbedderRow(this, entry);
            _embedderRows.Add(row);
            EmbedderList.Children.Add(row.Card);
        }

        EnableToggle.Toggled += (_, _) => OnEnableToggled();
        RebuildButton.Click += (_, _) => _model.RebuildLibraryIndex();
        ConfirmButton.Click += (_, _) => _model.RequestCloudEmbeddingConsent();
        MissingModelSetUpButton.Click += (_, _) =>
        {
            if (CurrentAPIConfig is { } config) SetUp(config);
        };

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _providers.Changed += Refresh;
            _providers.LocalModelStatesChanged += UpdateEmbedderRows;
            Refresh();
            UpdateEmbedderRows();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _providers.Changed -= Refresh;
            _providers.LocalModelStatesChanged -= UpdateEmbedderRows;
        };
    }

    /// <summary>
    /// Library chat became shown (its inner tab picked, or the Settings page and AI Settings shown again; macOS
    /// <c>onChange(of: shown)</c>): the installed models are read again, and provider, key and model changes made elsewhere
    /// are picked up.
    /// </summary>
    internal void Shown()
    {
        if (Closing) return;
        Refresh();
        UpdateEmbedderRows();
        _ = _providers.RefreshLocalModelsAsync();
        _model.SyncLibraryIndex();
    }

    private bool Closing => _model.IsClosing;

    private AppSettings Settings => _model.Settings;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.Settings) or nameof(AppViewModel.RAGStatus)) Refresh();
    }

    /// <summary>Every control shows the current settings, providers and index status.</summary>
    private void Refresh()
    {
        if (Closing) return;
        _syncing = true;
        try
        {
            EnableToggle.IsOn = Settings.RagEnabled;
            UpdateStatus();
            foreach (var row in _embedderRows) row.UpdateSelection(IsSelected(row.Entry));
            UpdateMissingModel();
            UpdateProviderRows();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnEnableToggled()
    {
        if (_syncing || EnableToggle.IsOn == Settings.RagEnabled) return;
        var on = EnableToggle.IsOn;
        _model.ApplySettings(Settings with { RagEnabled = on });
        if (on) _ = AskToDownloadIfMissingAsync();
    }

    // Index

    /// <summary>
    /// While RAG is on: "Indexed N of M conversations" with a ring while a pass runs, Paused, the cloud embedder's
    /// confirmation with Confirm…, and why indexing stopped (macOS <c>statusSection</c>). Rebuild only while on.
    /// </summary>
    private void UpdateStatus()
    {
        var on = Settings.RagEnabled;
        var status = _model.RAGStatus;
        StatusCard.Visibility = Show(on);
        StatusCard.Header = Loc.Plural("Indexed %lld of %lld conversations", status.Total, status.Indexed, status.Total);
        var spinning = on && status.Running && !status.Paused;
        StatusRing.IsActive = spinning;
        StatusRing.Visibility = Show(spinning);
        PausedText.Visibility = Show(status.Paused);
        MessageText.Text = status.Message ?? "";
        MessageText.Visibility = Show(status.Message is not null);

        var config = on && status.NeedsConsent ? CurrentAPIConfig : null;
        ConsentCard.Visibility = Show(config is not null);
        if (config is not null)
        {
            // A base URL with no destination can never be confirmed: the AI settings must fix it first.
            var confirmable = LibraryChat.EmbeddingDestination(config) is not null;
            ConfirmButton.IsEnabled = confirmable;
            ConsentCard.Description = confirmable || Host(config).Length > 0
                ? Loc.Format("Nothing is sent to %@ until you confirm.", Host(config))
                : Loc.T("Base URL not set");
        }
        RebuildButton.IsEnabled = on;
    }

    // Embedding model

    /// <summary>
    /// The GGUF model is the embedder: its own id, or the macOS id it stands for (an API embedder or an id the Windows
    /// catalog does not have, such as <c>apple-nl</c>, picks none).
    /// </summary>
    private bool IsSelected(ModelEntry entry) => ModelCatalog.EmbeddingEntry(Settings.EmbeddingModelID)?.Id == entry.Id;

    /// <summary>A pick of a catalog model (macOS <c>select</c>); picking the one in use writes nothing.</summary>
    private void Select(ModelEntry entry)
    {
        if (!IsSelected(entry)) _model.ApplySettings(Settings with { EmbeddingModelID = entry.Id });
        // A click checks the radio button itself: it shows the setting again.
        Refresh();
    }

    /// <summary>
    /// RAG was turned on with a catalog embedder that is neither installed nor downloading: asks to download it, and
    /// shows its row once it downloads (macOS <c>askToDownloadIfMissing</c>; Cancel stands for macOS's Not Now).
    /// </summary>
    private async Task AskToDownloadIfMissingAsync()
    {
        if (ModelCatalog.EmbeddingEntry(Settings.EmbeddingModelID) is not { } entry
            || _model.InstalledModelIds.Contains(entry.Id) || _model.Downloads.ContainsKey(entry.Id)
            || _model.Confirm is not { } confirm) return;
        var message = Loc.Format(
            "Indexing your conversations on this PC needs this embedding model (%@). You can also pick another model below.",
            ModelsViewModel.SizeText(entry.DiskMB));
        if (!await confirm(Loc.Format("Download %@?", entry.DisplayName), message, Loc.T("Download")) || Closing) return;
        _providers.DownloadLocalModel(entry);
        if (_embedderRows.FirstOrDefault(r => r.Entry == entry) is { } row)
            row.Card.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0 });
    }

    private void UpdateEmbedderRows()
    {
        if (Closing) return;
        foreach (var row in _embedderRows) row.Update(_providers.LocalModelState(row.Entry));
    }

    /// <summary>
    /// The providers that can embed, enabled or not: OpenAI-compatible APIs with an embeddings endpoint (custom endpoints
    /// included) and the local hosts (macOS <c>embeddingConfigs</c>).
    /// </summary>
    private IReadOnlyList<ProviderConfig> EmbeddingConfigs =>
    [
        .. _providers.ProviderConfigs.Where(c => c.Kind switch
        {
            ProviderKind.Local => true,
            ProviderKind.OpenAICompatible => c.Preset is not { } preset || EmbeddingPresets.Contains(preset),
            _ => false,
        }),
    ];

    /// <summary>The config of the API embedder in use, when its provider is still available (macOS <c>currentAPIConfig</c>).</summary>
    private ProviderConfig? CurrentAPIConfig =>
        AppServices.EmbeddingConfig(Settings, _providers.ProviderConfigs, _providers.HasApiKey);

    private void UpdateProviderRows()
    {
        var configs = EmbeddingConfigs;
        List<string> ids = [.. configs.Select(c => c.Id)];
        foreach (var config in configs)
        {
            if (!_providerRows.TryGetValue(config.Id, out var row)) _providerRows[config.Id] = row = new ProviderRow(this);
            row.Update(config, Settings.EmbeddingModelID == ModelCatalog.ApiEmbedderPrefix + config.Id);
        }
        if (ids.SequenceEqual(_providerIds, StringComparer.OrdinalIgnoreCase)) return;
        _providerIds = ids;
        foreach (var id in _providerRows.Keys.Where(id => !ids.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _providerRows.Remove(id);
        }
        ProviderList.Children.Clear();
        foreach (var id in ids) ProviderList.Children.Add(_providerRows[id].Card);
    }

    /// <summary>
    /// A pick of a provider (macOS <c>selectAPI</c>): its preset's suggested model fills its embedding model only when
    /// empty; a model typed before stays.
    /// </summary>
    private void SelectAPI(ProviderConfig config)
    {
        _model.PickAPIEmbedder(config, SuggestedModels.GetValueOrDefault(config.Preset ?? "", ""));
        Refresh();
    }

    /// <summary>
    /// The API embedder in use has no embedding model: "Not set for …" with Set Up…, which opens its card (macOS
    /// <c>apiSection</c>).
    /// </summary>
    private void UpdateMissingModel()
    {
        var config = CurrentAPIConfig;
        var missing = config is not null && config.EmbeddingModel.Trim().Length == 0;
        MissingModelCard.Visibility = Show(missing);
        if (!missing) return;
        var name = config!.DisplayName;
        MissingModelCard.Description = Loc.Format("Not set for %@. Enter it in its card in AI Providers.", name);
        AutomationProperties.SetName(MissingModelSetUpButton, $"{Loc.T("Set Up…")}, {name}");
    }

    /// <summary>
    /// AI Providers on <paramref name="config"/>'s row, expanded (macOS <c>setUp</c>): Local AI › Local hosts for a local
    /// host (which has a row only once added, by a scan or by URL), Cloud for an API.
    /// </summary>
    private void SetUp(ProviderConfig config)
    {
        var local = config.Kind == ProviderKind.Local;
        if (!local || _providers.SavedLocalHosts.Any(h => ProvidersViewModel.SameId(h.Id, config.Id)))
            _model.SettingsExpandedProvider = config.Id;
        if (local) _model.OpenLocalAI(ProvidersSettingsPage.LocalHostsGroup);
        else _model.OpenSettings(SettingsSearch.Providers, ProvidersSettingsPage.CloudTab);
    }

    /// <summary>Embedding models are named after it (text-embedding-3-small, mistral-embed, nomic-embed-text) or their family.</summary>
    internal static bool IsEmbeddingModel(string id)
    {
        var lower = id.ToLowerInvariant();
        return new[] { "embed", "bge", "e5-", "gte-", "minilm", "arctic" }.Any(lower.Contains);
    }

    // Parts

    /// <summary>The host a provider sends to (macOS <c>host</c>), or its base URL as typed when it has none.</summary>
    private static string Host(ProviderConfig config) =>
        Uri.TryCreate(config.BaseURL.Trim(), UriKind.Absolute, out var url) ? url.Host : config.BaseURL;

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private TextBlock Note(string style, string text = "") => new() { Style = (Style)Resources[style], Text = text };

    /// <summary>A radio button that picks an embedder, named "Use …" for screen readers and its tooltip (macOS <c>radio</c>).</summary>
    private static RadioButton Radio(string name)
    {
        var radio = new RadioButton
        {
            GroupName = "Embedder",
            Content = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap },
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(radio, Loc.Format("Use %@", name));
        ToolTipService.SetToolTip(radio, Loc.Format("Use %@", name));
        return radio;
    }

    /// <summary>A model row's button, named with its model for screen readers ("Download, Qwen3 Embedding 0.6B").</summary>
    private static Button ModelButton(string text, ModelEntry entry, Action click)
    {
        var button = new Button { Content = Loc.T(text) };
        AutomationProperties.SetName(button, $"{Loc.T(text)}, {entry.DisplayName}");
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>
    /// A catalog embedding model (macOS <c>localSection</c>'s row: its radio button and <c>localState</c>): picked or not,
    /// its name; Installed, the download's progress, or the way to its download in AI Providers › Local AI › Embedding,
    /// where it is downloaded and deleted.
    /// </summary>
    private sealed class EmbedderRow
    {
        private readonly RadioButton _radio;
        private readonly ProgressBar _progress = new() { Width = 90, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _percent;
        private readonly StackPanel _installed = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        private readonly Button _download;

        public EmbedderRow(RAGSettingsPage page, ModelEntry entry)
        {
            Entry = entry;
            _radio = Radio(entry.DisplayName);
            _radio.Click += (_, _) => page.Select(entry);

            AutomationProperties.SetName(_progress, entry.DisplayName);
            _percent = new TextBlock
            {
                Style = (Style)page.Resources["NoteStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 32,
                TextAlignment = TextAlignment.Right,
            };
            _installed.Children.Add(new FontIcon { Style = (Style)page.Resources["SuccessIconStyle"], Glyph = "\uE930" });
            _installed.Children.Add(new TextBlock { Style = (Style)page.Resources["SuccessNoteStyle"], Text = Loc.T("Installed") });
            _download = ModelButton("Download in AI Providers › Local AI", entry,
                () => page._model.OpenLocalAI(ProvidersSettingsPage.EmbeddingGroup));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var part in new UIElement[] { _progress, _percent, _installed, _download })
            {
                part.Visibility = Visibility.Collapsed;
                actions.Children.Add(part);
            }
            Card = new SettingsCard { Header = _radio, Content = actions };
        }

        public ModelEntry Entry { get; }

        public SettingsCard Card { get; }

        public void UpdateSelection(bool selected)
        {
            if (_radio.IsChecked != selected) _radio.IsChecked = selected;
        }

        /// <summary>The row shows its model's state (macOS <c>localState</c>).</summary>
        public void Update(ModelRowState state)
        {
            var downloading = state.Download is not null;
            _progress.Visibility = _percent.Visibility = Show(downloading);
            if (state.Download is { } fraction)
            {
                _progress.Value = fraction;
                _percent.Text = fraction.ToString("P0", CultureInfo.CurrentCulture);
            }
            _installed.Visibility = Show(!downloading && state.IsInstalled);
            _download.Visibility = Show(!downloading && !state.IsInstalled);
        }
    }

    /// <summary>
    /// A provider that can embed (macOS <c>providersSection</c>'s row): picked or not (off while the provider is), its
    /// name and host, then whether its data stays on this PC, or for a local host Set Up…, which opens where it is turned
    /// on; a cloud API's key status ("API key" opens its card in Settings › AI Providers › Cloud).
    /// </summary>
    private sealed class ProviderRow
    {
        private readonly RAGSettingsPage _page;
        private readonly RadioButton _radio = Radio("");
        private readonly TextBlock _host;
        private readonly TextBlock _where;
        private readonly Button _setUp = new();
        private readonly ApiKeyStatus _status;
        private ProviderConfig? _config;

        public ProviderRow(RAGSettingsPage page)
        {
            _page = page;
            _radio.Click += (_, _) =>
            {
                if (_config is { } config) page.SelectAPI(config);
            };
            _host = page.Note("NoteStyle");
            _where = page.Note("NoteStyle");
            _where.VerticalAlignment = VerticalAlignment.Center;
            _setUp.Content = Loc.T("Set Up…");
            _setUp.Click += (_, _) => SetUp();
            _status = new ApiKeyStatus(page._model, (Style)page.Resources["SuccessIconStyle"]);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(_where);
            actions.Children.Add(_setUp);
            actions.Children.Add(_status.Element);
            Card = new SettingsCard { Header = _radio, Description = _host, Content = actions };
        }

        public SettingsCard Card { get; }

        public void Update(ProviderConfig config, bool selected)
        {
            _config = config;
            var name = config.DisplayName;
            if (_radio.Content is TextBlock label && label.Text != name)
            {
                label.Text = name;
                AutomationProperties.SetName(_radio, Loc.Format("Use %@", name));
                ToolTipService.SetToolTip(_radio, Loc.Format("Use %@", name));
            }
            // A cloud API once its key is saved, a local host when enabled (macOS isAvailable(hasKey:)).
            var available = config.IsAvailable(_page._providers.HasApiKey(config.Id));
            _radio.IsEnabled = available;
            if (_radio.IsChecked != selected) _radio.IsChecked = selected;
            var host = Host(config);
            _host.Text = host.Length == 0 ? Loc.T("Base URL not set") : host;
            _where.Text = Loc.T(ProviderInput.IsLoopback(config.BaseURL) ? "This PC" : "Leaves this PC");
            var local = config.Kind == ProviderKind.Local;
            _where.Visibility = Show(available);
            _setUp.Visibility = Show(!available && local);
            AutomationProperties.SetName(_setUp, $"{Loc.T("Set Up…")}, {name}");
            ToolTipService.SetToolTip(_setUp, Loc.Format("Turn on %@ in Settings › AI Providers › Local AI", name));
            _status.Element.Visibility = Show(!local);
            if (!local) _status.Update(config);
        }

        /// <summary>Opens Settings › AI Providers › Local AI › Local hosts where the local host is turned on (macOS <c>setUp</c>).</summary>
        private void SetUp()
        {
            if (_config is { } config) _page.SetUp(config);
        }
    }
}
