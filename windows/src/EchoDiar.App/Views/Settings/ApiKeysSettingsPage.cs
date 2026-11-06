using System.ComponentModel;
using CommunityToolkit.WinUI.Controls;
using EchoDiar.AI;
using EchoDiar.AI.Cloud;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static EchoDiar.App.Views.ProviderModelFields;

namespace EchoDiar.App.Views;

/// <summary>
/// The cloud provider cards of Settings › AI Providers › Cloud (macOS APIKeysSettingsView.swift, which no longer is a
/// sidebar section): a card per cloud provider that takes a key, the LLM presets, the speech-to-text APIs and the custom
/// OpenAI-compatible endpoints, each with its key, Test and where to get a key, and its default models; Azure and custom
/// endpoints also set their base URL. The cards are <see cref="ProviderCardList"/>, which
/// <see cref="ProvidersSettingsPage"/> hosts on its Cloud tab. Every change goes through <see cref="ProvidersViewModel"/>;
/// keys go to Credential Manager only and are never read back into the UI. A model card's "API key" button
/// (<see cref="ApiKeyStatus"/>, <see cref="ShowApiKey"/>) expands its provider's card and brings it into view.
/// </summary>
internal static class ApiKeysSettingsPage
{
    /// <summary>
    /// Where to get a preset's API key and look up its model ids (macOS <c>providerLinks</c>); custom endpoints, local
    /// hosts and CLIs have none.
    /// </summary>
    internal static IReadOnlyDictionary<string, (string Keys, string Models)> ProviderLinks { get; } =
        new Dictionary<string, (string Keys, string Models)>
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
            ["elevenlabs"] = ("https://elevenlabs.io/app/developers/api-keys", "https://elevenlabs.io/docs/overview/models"),
            ["deepgram"] = ("https://console.deepgram.com", "https://developers.deepgram.com/docs/models-languages-overview"),
            ["assemblyai"] = ("https://www.assemblyai.com/dashboard/api-keys", "https://www.assemblyai.com/docs/getting-started/models"),
            ["speechmatics"] = ("https://portal.speechmatics.com/settings/api-keys/", "https://docs.speechmatics.com/speech-to-text/models"),
            ["soniox"] = ("https://console.soniox.com/org/projects/api/keys/", "https://docs.soniox.com/stt/models"),
            ["gladia"] = ("https://app.gladia.io/apikeys", "https://docs.gladia.io/chapters/introduction/models"),
        };

    /// <summary>
    /// Settings › AI Providers › Cloud with provider <paramref name="id"/>'s card expanded and brought into view (macOS
    /// <c>AppModel.showAPIKey(for:)</c>, a model card's "API key" button): the <see cref="ProviderCardList"/> shown there
    /// takes the request. The section and tab open first, then the request is set, so the page shown before does not
    /// take it.
    /// </summary>
    internal static void ShowApiKey(AppViewModel model, string id)
    {
        model.OpenSettings(SettingsSearch.Providers, ProvidersSettingsPage.CloudTab);
        model.SettingsExpandedProvider = id;
    }
}

/// <summary>
/// The cloud provider cards in their three groups (macOS <c>ProviderCardSections</c>), made into a host panel: Language
/// models (the LLM presets), Speech-to-text APIs, then Custom OpenAI-compatible endpoints with Add Endpoint. One card per
/// provider, made once and updated in place, so typing in a field never rebuilds the card it is in. The host calls
/// <see cref="Attach"/> when it loads and <see cref="Detach"/> when it unloads; <c>isShowing</c> says whether the cards
/// are on screen (their section, and tab), for a model card's "API key" request (<see cref="ApiKeysSettingsPage.ShowApiKey"/>).
/// The styles come from the host's <c>resources</c>: SectionHeaderStyle, NoteStyle, CautionNoteStyle, CriticalNoteStyle,
/// SuccessNoteStyle, SuccessIconStyle and PriceBadgeStyle (ProvidersSettingsPage.xaml).
/// </summary>
internal sealed class ProviderCardList
{
    private readonly AppViewModel _model;
    private readonly ProvidersViewModel _providers;
    private readonly ResourceDictionary _resources;
    private readonly Func<bool> _isShowing;
    private readonly StackPanel _languageModels = new() { Spacing = 4 };
    private readonly StackPanel _speechToText = new() { Spacing = 4 };
    private readonly StackPanel _custom = new() { Spacing = 4 };

    /// <summary>One card per provider, made once and updated in place.</summary>
    private readonly Dictionary<string, ApiKeyCard> _cards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    /// <param name="model">The app, whose providers the cards show and change.</param>
    /// <param name="resources">The host page's resources, with the styles above.</param>
    /// <param name="host">The panel the group titles, their cards and Add Endpoint are added to.</param>
    /// <param name="isShowing">Whether the host's section (and tab) shows: only then does a card take an "API key" request.</param>
    public ProviderCardList(AppViewModel model, ResourceDictionary resources, Panel host, Func<bool> isShowing)
    {
        _model = model;
        _providers = model.ProvidersModel;
        _resources = resources;
        _isShowing = isShowing;
        host.Children.Add(Header(Loc.T("Language models"), "Language models", first: true));
        host.Children.Add(_languageModels);
        host.Children.Add(Header(Loc.T("Speech-to-text APIs"), "Speech-to-text APIs"));
        host.Children.Add(_speechToText);
        host.Children.Add(Header(Loc.T("Custom OpenAI-compatible endpoints"), "Custom OpenAI-compatible endpoints"));
        host.Children.Add(_custom);
        var addEndpoint = new Button { Margin = new Thickness(0, 4, 0, 0), Content = Loc.T("Add Endpoint") };
        addEndpoint.Click += (_, _) => _providers.AddEndpoint();
        host.Children.Add(addEndpoint);
    }

    private bool Closing => _model.IsClosing;

    /// <summary>The host loaded: the cards follow the providers and keys, and take a pending "API key" request.</summary>
    public void Attach()
    {
        _model.PropertyChanged += OnModelChanged;
        _providers.Changed += Refresh;
        Refresh();
        ExpandRequestedProvider();
        _ = _providers.RefreshKeysAsync();
    }

    /// <summary>The host unloaded: no more updates, and the tests and model lists stop.</summary>
    public void Detach()
    {
        _model.PropertyChanged -= OnModelChanged;
        _providers.Changed -= Refresh;
        // macOS: a test stops when its card goes away.
        foreach (var card in _cards.Values) card.Cancel();
    }

    /// <summary>A group's title; <paramref name="tag"/> is its search entry. The first sits right under the tabs.</summary>
    private StackPanel Header(string title, string tag, bool first = false)
    {
        var header = new StackPanel { Tag = tag };
        if (first) header.Margin = new Thickness(1, 4, 0, 4);
        else header.Style = (Style)_resources["SectionHeaderStyle"];
        header.Children.Add(new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Text = title });
        return header;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.SettingsExpandedProvider) or nameof(AppViewModel.SettingsSection)
            or nameof(AppViewModel.SettingsSubTab)) ExpandRequestedProvider();
    }

    /// <summary>
    /// A model card's "API key" (macOS <c>settingsExpandedProvider</c>): its provider's card expands and comes into view,
    /// and the request is cleared. Only while the cards show.
    /// </summary>
    private void ExpandRequestedProvider()
    {
        if (Closing || !_isShowing() || _model.SettingsExpandedProvider is not { } id || !_cards.TryGetValue(id, out var card)) return;
        _model.SettingsExpandedProvider = null;
        card.Expander.IsExpanded = true;
        // After the page's layout: a card of a page just shown has no position yet.
        card.Expander.DispatcherQueue.TryEnqueue(() =>
        {
            if (!Closing) card.Expander.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0 });
        });
    }

    /// <summary>
    /// The three groups, each a card made once per provider and updated in place; a list is rebuilt only when its
    /// providers change.
    /// </summary>
    private void Refresh()
    {
        if (Closing) return;
        _syncing = true;
        try
        {
            var configs = _providers.ProviderConfigs;
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SyncList(_languageModels,
                [.. configs.Where(c => c.Preset is not null && c.Kind is ProviderKind.OpenAICompatible or ProviderKind.Anthropic)], shown);
            SyncList(_speechToText, [.. configs.Where(c => c.Kind == ProviderKind.SpeechToText)], shown);
            SyncList(_custom, [.. configs.Where(c => c.Preset is null && c.Kind == ProviderKind.OpenAICompatible)], shown);
            foreach (var id in _cards.Keys.Where(id => !shown.Contains(id)).ToList())
            {
                _cards[id].Cancel();
                _cards.Remove(id);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncList(StackPanel list, IReadOnlyList<ProviderConfig> configs, HashSet<string> shown)
    {
        List<ApiKeyCard> cards = [];
        foreach (var config in configs)
        {
            if (!shown.Add(config.Id)) continue;
            if (!_cards.TryGetValue(config.Id, out var card)) _cards[config.Id] = card = new ApiKeyCard(this, config.Id);
            card.Update(config);
            cards.Add(card);
        }
        if (list.Children.SequenceEqual(cards.Select(c => (UIElement)c.Expander))) return;
        list.Children.Clear();
        foreach (var card in cards)
        {
            // A card moved from another list (its kind changed) leaves that one first.
            if (card.Expander.Parent is Panel parent) parent.Children.Remove(card.Expander);
            list.Children.Add(card.Expander);
        }
    }

    /// <summary>
    /// A cloud provider's card (macOS <c>APIKeyCard</c>): collapsed, its name, "Base URL not set" for Azure or a custom
    /// endpoint without one (else a custom endpoint's base URL), and a checkmark once it is available; expanded, its
    /// fields in a label column and an input column (<see cref="FieldGrid"/>): a custom endpoint's name, authentication
    /// and extra headers (Windows only), the API key (Credential Manager only: saved on Enter, on leaving the field, by
    /// Save and by Test, then cleared; only "Saved" shows), the base URL for Azure and custom endpoints, then its default
    /// model for each kind of work it can do (text generation, transcription, diarization, embedding); below, Test and a
    /// custom endpoint's Remove. No Enabled switch: a cloud API is available once its key is saved.
    /// </summary>
    private sealed class ApiKeyCard
    {
        private static readonly AuthStyle[] AuthStyles = [AuthStyle.Bearer, AuthStyle.ApiKeyHeader, AuthStyle.XApiKey, AuthStyle.None];

        private readonly ProviderCardList _list;
        private readonly string _id;
        private readonly FontIcon _available;
        private readonly FieldGrid _fields = new();
        private readonly FieldGrid.Row _nameRow;
        private readonly TextBox _nameBox = new();
        private readonly FieldGrid.Row _authRow;
        private readonly ComboBox _authCombo = new() { MinWidth = 180 };
        private readonly FieldGrid.Row _headersRow;
        private readonly TextBox _headersBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "HTTP-Referer: https://example.com" };
        private readonly TextBlock _headersRefused;
        private readonly FieldGrid.Row _keyRow;
        private readonly PasswordBox _keyBox = new();
        private readonly StackPanel _savedLabel = new() { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
        private readonly Button _removeKeyButton = new() { Visibility = Visibility.Collapsed };
        private readonly HyperlinkButton? _getKeyLink;
        private readonly TextBlock _keyError;
        private readonly FieldGrid.Row _baseRow;
        private readonly TextBox _baseBox = new();
        private readonly TextBlock _azureNote;
        private readonly TextBlock _httpWarning;
        private readonly ModelField _textModel;
        private readonly FieldGrid.Row _textModelRow;
        private readonly FieldGrid.Row _shownModelsRow;
        private readonly CloudModelField _transcriptionModel;
        private readonly FieldGrid.Row _transcriptionRow;
        private readonly Border _price;
        private readonly TextBlock _priceText;
        private readonly TextBlock _geminiTranscription;
        private readonly TextBlock _noBaseURL;
        private readonly CloudModelField _diarizationModel;
        private readonly FieldGrid.Row _diarizationRow;
        private readonly TextBlock _geminiDiarization;
        private readonly EmbeddingModelField _embeddingModel;
        private readonly FieldGrid.Row _embeddingRow;
        private readonly Button _testButton = new();
        private readonly Button _saveButton = new();
        private readonly ProgressRing _ring = SmallRing();
        private readonly TextBlock _ok;
        private readonly TextBlock _testError;
        private readonly Button _removeButton = new() { Visibility = Visibility.Collapsed };
        private CancellationTokenSource? _testing;
        private ProviderConfig? _config;

        public ApiKeyCard(ProviderCardList list, string id)
        {
            _list = list;
            _id = id;
            var resources = list._resources;
            Func<bool> isSyncing = () => list._syncing;
            Func<bool> isClosing = () => list.Closing;

            // Collapsed, it still says whether the provider is set up (macOS AvailabilityCheckmark).
            _available = new FontIcon { Style = (Style)resources["SuccessIconStyle"], Glyph = "", Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(_available, Loc.T("Saved"));
            ToolTipService.SetToolTip(_available, Loc.T("Saved"));
            Expander = new SettingsExpander { Content = _available };

            AutomationProperties.SetName(_nameBox, Loc.T("Name"));
            _nameBox.TextChanged += (_, _) => Change(c => c.DisplayName != _nameBox.Text, c => c.DisplayName = _nameBox.Text);
            _nameRow = _fields.Add(Loc.T("Name"), new FieldPanel { InputWidth = 260, Children = { _nameBox } });

            foreach (var title in new[] { "Bearer token", "api-key header", "x-api-key header", "None" }) _authCombo.Items.Add(Loc.T(title));
            AutomationProperties.SetName(_authCombo, Loc.T("Authentication"));
            _authCombo.SelectionChanged += (_, _) =>
            {
                var index = _authCombo.SelectedIndex;
                if (index < 0) return;
                Change(c => c.AuthStyle != AuthStyles[index], c => c.AuthStyle = AuthStyles[index]);
            };
            _authRow = _fields.Add(Loc.T("Authentication"), _authCombo);

            // Windows addition: macOS has no field for them. Saved in settings.json, never a key.
            // A line naming a key header is not saved; _headersRefused says why it went away.
            _headersRefused = Note(resources, "CautionNoteStyle");
            _headersRefused.Visibility = Visibility.Collapsed;
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
            _headersRow = _fields.Add(Loc.T("Extra headers"), new FieldPanel { InputWidth = 320, Children = { _headersBox } },
                Note(resources, "NoteStyle", Loc.T("One per line, Name: value. Saved in the settings file: put API keys in the API key field.")),
                _headersRefused);

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
                UpdateSave();
            };
            _savedLabel.Children.Add(new FontIcon { Style = (Style)resources["SuccessIconStyle"], Glyph = "" });
            _savedLabel.Children.Add(Note(resources, "SuccessNoteStyle", Loc.T("Saved")));
            _removeKeyButton.Content = Loc.T("Remove");
            _removeKeyButton.Click += async (_, _) => ShowKeyError(await _list._providers.DeleteApiKeyAsync(_id));
            if (ApiKeysSettingsPage.ProviderLinks.TryGetValue(id, out var links)) _getKeyLink = Link("Get an API key", links.Keys);
            var keyPanel = new FieldPanel { InputWidth = 260 };
            keyPanel.Children.Add(_keyBox);
            keyPanel.Children.Add(_savedLabel);
            keyPanel.Children.Add(_removeKeyButton);
            if (_getKeyLink is not null) keyPanel.Children.Add(_getKeyLink);
            _keyError = Note(resources, "CriticalNoteStyle");
            _keyError.Visibility = Visibility.Collapsed;
            _keyRow = _fields.Add(Loc.T("API key"), keyPanel, _keyError);

            AutomationProperties.SetName(_baseBox, Loc.T("Base URL"));
            _baseBox.TextChanged += (_, _) => Change(c => c.BaseURL != _baseBox.Text, c => c.BaseURL = _baseBox.Text);
            _azureNote = Note(resources, "NoteStyle", Loc.T("Required: your resource's https://<resource>.openai.azure.com/openai/v1 URL."));
            _httpWarning = Note(resources, "CautionNoteStyle");
            _baseRow = _fields.Add(Loc.T("Base URL"), new FieldPanel { InputWidth = 320, Children = { _baseBox } }, _azureNote, _httpWarning);

            // The default models (macOS APIKeyCard.models), each shown where the provider can do that work.
            _textModel = new ModelField(list._providers, isSyncing, isClosing, resources, id, id == "azure" ? "Deployment name" : "Model id",
                ApiKeysSettingsPage.ProviderLinks.TryGetValue(id, out var modelLinks) ? modelLinks.Models : null, "Text generation model");
            _textModelRow = _fields.Add(Loc.T("Text generation model"), _textModel.Input, _textModel.Notes);
            _shownModelsRow = _fields.Add(null, Note(resources, "NoteStyle", Loc.T("In the AI pane's model menu")), _textModel.ShownPanel);

            _transcriptionModel = new CloudModelField(list._providers, isSyncing, isClosing, resources, diarization: false, label: "Transcription model");
            _priceText = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
            _price = new Border { Style = (Style)resources["PriceBadgeStyle"], Child = _priceText, Visibility = Visibility.Collapsed };
            _geminiTranscription = Note(resources, "NoteStyle", Loc.T("LLM transcription — timestamps approximate."));
            // A custom endpoint transcribes once its base URL and its model are set.
            _noBaseURL = Note(resources, "NoteStyle", Loc.T("Base URL not set"));
            _transcriptionRow = _fields.Add(Loc.T("Transcription model"), _transcriptionModel.Input, _transcriptionModel.Notes, _price,
                Note(resources, "NoteStyle", Loc.T("Prices are approximate list prices per audio hour; the mic and system channels are sent and billed separately.")),
                _geminiTranscription, _noBaseURL);

            _diarizationModel = new CloudModelField(list._providers, isSyncing, isClosing, resources, diarization: true, label: "Diarization model");
            _geminiDiarization = Note(resources, "NoteStyle", Loc.T("LLM diarization — timestamps approximate, speakers can be mislabeled."));
            _diarizationRow = _fields.Add(Loc.T("Diarization model"), _diarizationModel.Input, _diarizationModel.Notes,
                Note(resources, "NoteStyle", Loc.T("Only the system channel is uploaded, as one extra transcription billed by the provider; only the speaker labels are kept. Not used when the transcription model already labels speakers. Prices are approximate list prices per audio hour.")),
                _geminiDiarization);

            _embeddingModel = new EmbeddingModelField(list._providers, isSyncing, isClosing, resources, id);
            _embeddingRow = _fields.Add(Loc.T("Embedding model"), _embeddingModel.Input, _embeddingModel.Notes);

            // Test (macOS TestButton): OK beside it or a short error below; Cancel while it runs.
            _testButton.Content = Loc.T("Test");
            _testButton.Click += (_, _) => _ = TestAsync();
            _saveButton.Content = Loc.T("Save");
            _saveButton.Click += async (_, _) =>
            {
                await SaveKeyAsync();
                UpdateSave();
            };
            _ok = Note(resources, "SuccessNoteStyle", Loc.T("OK"));
            _ok.Visibility = Visibility.Collapsed;
            _testError = Note(resources, "CriticalNoteStyle");
            _testError.Visibility = Visibility.Collapsed;
            _testError.MaxLines = 3;
            _removeButton.Content = Loc.T("Remove");
            _removeButton.Click += (_, _) => _ = _list._providers.RemoveProviderAsync(_id);
            var buttons = new FieldPanel();
            buttons.Children.Add(_testButton);
            buttons.Children.Add(_saveButton);
            buttons.Children.Add(_ring);
            buttons.Children.Add(_ok);
            buttons.Children.Add(_removeButton);

            // One item for the fields, so their inputs line up past the card's own labels (macOS fieldLabelColumn).
            Expander.Items.Add(new SettingsCard
            {
                ContentAlignment = ContentAlignment.Vertical,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = _fields.Element,
            });
            Expander.Items.Add(new SettingsCard { Header = buttons, Description = _testError });
        }

        public SettingsExpander Expander { get; }

        private bool IsCustom => _config is { Preset: null };

        /// <summary>Whether the provider sends a key (macOS <c>CloudTranscription.needsKey</c>): its key field and Save show.</summary>
        private bool TakesKey => _config is { } config && CloudTranscription.NeedsKey(config);

        /// <summary>Shows <paramref name="config"/>; fields the user is editing already show their own text.</summary>
        public void Update(ProviderConfig config)
        {
            _config = config;
            var name = config.DisplayName.Length == 0 ? Loc.T("Untitled") : config.DisplayName;
            Expander.Header = name;
            if ((IsCustom || config.Id == "azure") && config.BaseURL.Trim().Length == 0) Expander.Description = Loc.T("Base URL not set");
            else if (IsCustom) Expander.Description = config.BaseURL;
            else Expander.ClearValue(SettingsExpander.DescriptionProperty);
            _nameRow.Visible = _authRow.Visible = _headersRow.Visible = IsCustom;
            SetText(_nameBox, config.DisplayName);
            _authCombo.SelectedIndex = Array.IndexOf(AuthStyles, config.AuthStyle);
            // Saved on leaving the field: a half-typed line must not be replaced while the user types it.
            if (_headersBox.FocusState == FocusState.Unfocused
                && !SameHeaders(ProviderInput.ParseHeaders(_headersBox.Text, out _), config.ExtraHeaders))
                _headersBox.Text = HeadersText(config.ExtraHeaders);
            _keyRow.Visible = TakesKey;
            _saveButton.Visibility = Show(TakesKey);
            _baseRow.Visible = IsCustom || config.Id == "azure";
            SetText(_baseBox, config.BaseURL);
            _baseBox.PlaceholderText = config.Id == "azure" ? "https://<resource>.openai.azure.com/openai/v1" : "";
            _azureNote.Visibility = Show(config.Id == "azure");
            UpdateModels(config);
            _fields.Layout();
            _removeButton.Visibility = Show(IsCustom);
            AutomationProperties.SetName(_removeButton, $"{Loc.T("Remove")}, {name}");
            UpdateKey();
            UpdateSave();
        }

        public void Cancel()
        {
            _testing?.Cancel();
            _textModel.Cancel();
            _transcriptionModel.Cancel();
            _diarizationModel.Cancel();
            _embeddingModel.Cancel();
        }

        /// <summary>
        /// The default model for each kind of work the provider can do (macOS <c>APIKeyCard.models</c>): text generation
        /// for an LLM API, transcription where cloud transcription takes it (its price, Gemini's note, a custom
        /// endpoint's missing base URL), diarization for a diarization preset, embedding for an OpenAI-compatible API
        /// with an embeddings endpoint (macOS <c>embeds</c>, custom endpoints included).
        /// </summary>
        private void UpdateModels(ProviderConfig config)
        {
            var generatesText = config.Kind is ProviderKind.OpenAICompatible or ProviderKind.Anthropic;
            _textModelRow.Visible = _shownModelsRow.Visible = generatesText;
            if (generatesText) _textModel.Update(config);

            var transcribes = CloudTranscription.Supports(config);
            _transcriptionRow.Visible = transcribes;
            if (transcribes)
            {
                _transcriptionModel.Update(config);
                var price = config.Preset is { } preset ? TranscriptionSettingsPage.Prices.GetValueOrDefault(preset) : null;
                // Gemini also bills its output.
                _priceText.Text = price is null ? "" : Loc.Format(config.Preset == "gemini" ? "%@ + output (approx.)" : "%@ (approx.)", price);
                _price.Visibility = Show(price is not null);
                _geminiTranscription.Visibility = Show(config.Preset == "gemini");
                _noBaseURL.Visibility = Show(IsCustom && config.BaseURL.Trim().Length == 0);
            }

            var diarizes = CloudTranscription.DiarizationPresets.Contains(config.Preset ?? "");
            _diarizationRow.Visible = diarizes;
            if (diarizes)
            {
                _diarizationModel.Update(config);
                _geminiDiarization.Visibility = Show(config.Preset == "gemini");
            }

            var embeds = config.Kind == ProviderKind.OpenAICompatible
                && (config.Preset is not { } embedPreset || RAGSettingsPage.EmbeddingPresets.Contains(embedPreset));
            _embeddingRow.Visible = embeds;
            if (embeds) _embeddingModel.Update(config);
        }

        /// <summary>Applies <paramref name="change"/> when <paramref name="differs"/>: a control showing the saved value is not a change.</summary>
        private void Change(Func<ProviderConfig, bool> differs, Action<ProviderConfig> change)
        {
            if (_list._syncing || _list.Closing || _list._providers.Config(_id) is not { } config || !differs(config)) return;
            _list._providers.UpdateProvider(_id, change);
        }

        private void UpdateKey()
        {
            var saved = _list._providers.HasApiKey(_id);
            _savedLabel.Visibility = _removeKeyButton.Visibility = Show(saved);
            if (_getKeyLink is not null) _getKeyLink.Visibility = Show(!saved);
            _keyBox.PlaceholderText = Loc.T(saved ? "Enter a new key to replace it" : "Paste the key, then Save");
            // Its key saved, or a custom endpoint without auth with its base URL set (macOS AvailabilityCheckmark).
            _available.Visibility = Show(_config is { } config && config.IsAvailable(saved));
            UpdateHttpWarning();
        }

        /// <summary>The key would go unencrypted to another machine (Phase 22 security review): say so under the URL.</summary>
        private void UpdateHttpWarning()
        {
            if (_config is not { } config) return;
            var sendsKey = _list._providers.HasApiKey(_id) || _keyBox.Password.Length > 0;
            var clear = ProviderInput.SendsKeyInClear(config, sendsKey);
            _httpWarning.Visibility = Show(clear);
            if (clear && Uri.TryCreate(config.BaseURL.Trim(), UriKind.Absolute, out var url))
            {
                _httpWarning.Text = Loc.Format("The API key is sent unencrypted over http to %@. Use https, or a host on this PC.", url.Host);
            }
        }

        /// <summary>Save is on while a key is typed and no test runs.</summary>
        private void UpdateSave() => _saveButton.IsEnabled = _testing is null && !string.IsNullOrWhiteSpace(_keyBox.Password);

        /// <summary>Saves the typed key (nothing when blank) and clears the field; false, with the error shown, when refused.</summary>
        private async Task<bool> SaveKeyAsync()
        {
            var text = _keyBox.Password;
            if (string.IsNullOrWhiteSpace(text)) return true;
            var error = await _list._providers.SaveApiKeyAsync(_id, text);
            if (_list.Closing) return false;
            if (error is null && _keyBox.Password == text) _keyBox.Password = "";
            ShowKeyError(error);
            return error is null;
        }

        private void ShowKeyError(string? error)
        {
            if (_list.Closing) return;
            _keyError.Text = error ?? "";
            _keyError.Visibility = Show(error is not null);
        }

        /// <summary>
        /// Test (macOS <c>APIKeyCard.testButton</c>): a key typed without Enter is saved first and is the one tested. A
        /// speech-to-text API checks its key with a free GET that uploads no audio; the other APIs list their models, a
        /// request that needs a valid key and no model (Windows Settings › AI Providers' Test). Cancel while it runs.
        /// </summary>
        private async Task TestAsync()
        {
            if (_testing is { } running)
            {
                running.Cancel();
                return;
            }
            _ok.Visibility = _testError.Visibility = Visibility.Collapsed;
            if (TakesKey && !await SaveKeyAsync()) return;
            if (_list.Closing || _list._providers.Config(_id) is not { } config) return;
            using var cancellation = _testing = new CancellationTokenSource();
            SetTesting(true);
            string? error = null;
            var ok = false;
            try
            {
                if (config.Kind == ProviderKind.SpeechToText) await _list._providers.TestSpeechKeyAsync(config, cancellation.Token);
                else await _list._providers.TestAsync(config, cancellation.Token);
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
            _testing = null;
            if (_list.Closing) return;
            SetTesting(false);
            _ok.Visibility = Show(ok);
            _testError.Text = error ?? "";
            _testError.Visibility = Show(error is not null);
        }

        private void SetTesting(bool testing)
        {
            _testButton.Content = Loc.T(testing ? "Cancel" : "Test");
            _ring.IsActive = testing;
            _ring.Visibility = Show(testing);
            UpdateSave();
        }

        private static bool SameHeaders(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
            a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);

        private static string HeadersText(Dictionary<string, string> headers) =>
            string.Join("\r", headers.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key}: {h.Value}"));
    }
}

/// <summary>
/// A cloud model card's key status, for the feature tabs (macOS <c>APIKeyStatus</c>): a checkmark once the provider is
/// available (<see cref="ProviderConfig.IsAvailable"/>: its key saved, or a custom endpoint without auth with its base
/// URL set), and "API key", which opens its card in Settings › AI Providers › Cloud (<see cref="ApiKeysSettingsPage.ShowApiKey"/>).
/// The host adds <see cref="Element"/> to its card and calls <see cref="Update"/> with the provider's config whenever its
/// cards refresh (<see cref="ProvidersViewModel.Changed"/> is raised after every key change).
/// </summary>
public sealed class ApiKeyStatus
{
    private readonly AppViewModel _model;
    private readonly FontIcon _saved;
    private readonly Button _button = new();
    private string? _id;

    /// <param name="model">The app, whose saved keys are read and whose Settings are opened.</param>
    /// <param name="successIconStyle">The host page's success icon style (theme colours that follow Light and Dark).</param>
    public ApiKeyStatus(AppViewModel model, Style successIconStyle)
    {
        _model = model;
        _saved = new FontIcon { Style = successIconStyle, Glyph = "", Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_saved, Loc.T("Saved"));
        ToolTipService.SetToolTip(_saved, Loc.T("Saved"));
        _button.Content = Loc.T("API key");
        _button.Click += (_, _) =>
        {
            if (_id is { } id) ApiKeysSettingsPage.ShowApiKey(_model, id);
        };
        Element = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        Element.Children.Add(_saved);
        Element.Children.Add(_button);
    }

    /// <summary>The checkmark, then the "API key" button.</summary>
    public StackPanel Element { get; }

    /// <summary>Shows <paramref name="config"/>'s state: the checkmark while it is available; the button opens its card.</summary>
    public void Update(ProviderConfig config)
    {
        _id = config.Id;
        _saved.Visibility = config.IsAvailable(_model.ProvidersModel.HasApiKey(config.Id)) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_button, $"{Loc.T("API key")}, {config.DisplayName}");
    }
}
