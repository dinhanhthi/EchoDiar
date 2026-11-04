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
using Windows.System;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › API Keys (macOS APIKeysSettingsView.swift): a card per cloud provider that takes a key, the LLM presets,
/// the speech-to-text APIs and the custom OpenAI-compatible endpoints, each with its key, Test and where to get a key;
/// Azure and custom endpoints also set their base URL. Models are picked in the feature tabs. Every change goes through
/// <see cref="ProvidersViewModel"/>; keys go to Credential Manager only and are never read back into the UI. The cards
/// are made in code and updated in place, so typing in a field never rebuilds the card it is in. A model card's
/// "API key" button (<see cref="ApiKeyStatus"/>, <see cref="ShowApiKey"/>) expands its provider's card and brings it
/// into view.
/// </summary>
public sealed partial class ApiKeysSettingsPage : UserControl
{
    /// <summary>The searchable rows (macOS SettingsSearch.swift, API Keys), each the Tag of its element.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Language models",
            ["API key", "key", "Credential Manager", "cloud", "LLM",
             .. PresetNames(ProviderKind.OpenAICompatible), .. PresetNames(ProviderKind.Anthropic)]),
        new("Speech-to-text APIs",
            ["API key", "key", "Credential Manager", "cloud transcription", .. PresetNames(ProviderKind.SpeechToText)]),
        new("Custom OpenAI-compatible endpoints",
            ["API key", "key", "custom endpoint", "add endpoint", "base URL", "authentication", "headers"]),
    ];

    private static IEnumerable<string> PresetNames(ProviderKind kind) =>
        ProviderConfig.Presets().Where(p => p.Kind == kind).Select(p => p.DisplayName);

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
        };

    /// <summary>
    /// Settings › API Keys with provider <paramref name="id"/>'s card expanded and brought into view (macOS
    /// <c>AppModel.showAPIKey(for:)</c>, a model card's "API key" button). The section opens first, then the request is
    /// set, so the page shown before does not take it.
    /// </summary>
    internal static void ShowApiKey(AppViewModel model, string id)
    {
        model.OpenSettings(SettingsSearch.ApiKeys);
        model.SettingsExpandedProvider = id;
    }

    private readonly AppViewModel _model;
    private readonly ProvidersViewModel _providers;

    /// <summary>One card per provider, made once and updated in place.</summary>
    private readonly Dictionary<string, ApiKeyCard> _cards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    public ApiKeysSettingsPage(AppViewModel model)
    {
        _model = model;
        _providers = model.ProvidersModel;
        InitializeComponent();
        AddEndpointButton.Click += (_, _) => _providers.AddEndpoint();

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _providers.Changed += Refresh;
            Refresh();
            ExpandRequestedProvider();
            _ = _providers.RefreshKeysAsync();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _providers.Changed -= Refresh;
            // macOS: a test stops when its card goes away.
            foreach (var card in _cards.Values) card.Cancel();
        };
    }

    private bool Closing => _model.IsClosing;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.SettingsExpandedProvider) or nameof(AppViewModel.SettingsSection)) ExpandRequestedProvider();
    }

    /// <summary>
    /// A model card's "API key" (macOS <c>settingsExpandedProvider</c>): its provider's card expands and comes into view,
    /// and the request is cleared. Only while this section shows.
    /// </summary>
    private void ExpandRequestedProvider()
    {
        if (Closing || _model.SettingsSection != SettingsSearch.ApiKeys || _model.SettingsExpandedProvider is not { } id
            || !_cards.TryGetValue(id, out var card)) return;
        _model.SettingsExpandedProvider = null;
        card.Expander.IsExpanded = true;
        // After the page's layout: a card of a page just shown has no position yet.
        DispatcherQueue.TryEnqueue(() =>
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
            SyncList(LanguageModelsList,
                [.. configs.Where(c => c.Preset is not null && c.Kind is ProviderKind.OpenAICompatible or ProviderKind.Anthropic)], shown);
            SyncList(SpeechToTextList, [.. configs.Where(c => c.Kind == ProviderKind.SpeechToText)], shown);
            SyncList(CustomList, [.. configs.Where(c => c.Preset is null && c.Kind == ProviderKind.OpenAICompatible)], shown);
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

    // Parts

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private TextBlock Note(string style, string text = "") => new() { Style = (Style)Resources[style], Text = text };

    /// <summary>
    /// A field of an expanded card: its label on the left, its input on the right (macOS <c>FieldRow</c>). Below
    /// <see cref="FieldWrapWidth"/> the card puts the input under its label, as the provider rows of Settings › AI.
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

    /// <summary>A test's error; a custom endpoint answering 404 likely lacks <c>/v1</c>.</summary>
    private static string ErrorMessage(Exception error, ProviderConfig config)
    {
        var message = ProvidersViewModel.ShortMessage(error);
        return ProviderInput.SuggestsV1(config, error) ? $"{message} {Loc.T("Check that the base URL ends with /v1.")}" : message;
    }

    /// <summary>
    /// A cloud provider's card (macOS <c>APIKeyCard</c>): collapsed, its name (a custom endpoint's base URL below it);
    /// expanded, a custom endpoint's name, authentication and extra headers (Windows only), the API key (Credential
    /// Manager only: saved on Enter, on leaving the field, by Save and by Test, then cleared; only "Saved" shows), the
    /// base URL for Azure and custom endpoints, Test, and a custom endpoint's Remove. No model fields and no Enabled
    /// switch: a cloud API is available once its key is saved.
    /// </summary>
    private sealed class ApiKeyCard
    {
        private static readonly AuthStyle[] AuthStyles = [AuthStyle.Bearer, AuthStyle.ApiKeyHeader, AuthStyle.XApiKey, AuthStyle.None];

        private readonly ApiKeysSettingsPage _page;
        private readonly string _id;
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
        private readonly SettingsCard _baseCard;
        private readonly TextBox _baseBox = new();
        private readonly TextBlock _azureNote;
        private readonly TextBlock _httpWarning;
        private readonly Button _testButton = new();
        private readonly Button _saveButton = new();
        private readonly ProgressRing _ring = new()
        {
            Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly TextBlock _ok;
        private readonly TextBlock _testError;
        private readonly Button _removeButton = new() { Visibility = Visibility.Collapsed };
        private CancellationTokenSource? _testing;
        private ProviderConfig? _config;

        public ApiKeyCard(ApiKeysSettingsPage page, string id)
        {
            _page = page;
            _id = id;
            Expander = new SettingsExpander();

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
                if (e.Key != VirtualKey.Enter) return;
                e.Handled = true;
                _ = SaveKeyAsync();
            };
            _keyBox.LostFocus += (_, _) => _ = SaveKeyAsync();
            _keyBox.PasswordChanged += (_, _) =>
            {
                UpdateHttpWarning();
                UpdateSave();
            };
            _savedLabel.Children.Add(new FontIcon { Style = (Style)page.Resources["SuccessIconStyle"], Glyph = "" });
            _savedLabel.Children.Add(page.Note("SuccessNoteStyle", Loc.T("Saved")));
            _removeKeyButton.Content = Loc.T("Remove");
            _removeKeyButton.Click += async (_, _) => ShowKeyError(await _page._providers.DeleteApiKeyAsync(_id));
            if (ProviderLinks.TryGetValue(id, out var links))
            {
                _getKeyLink = new HyperlinkButton { Content = Loc.T("Get an API key"), NavigateUri = new Uri(links.Keys), Padding = new Thickness(0) };
            }
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
            _baseCard = Field("Base URL", new FieldPanel { InputWidth = 320, Children = { _baseBox } });
            _baseCard.Description = baseNotes;

            // Test (macOS TestButton): OK beside it or a short error below; Cancel while it runs.
            _testButton.Content = Loc.T("Test");
            _testButton.Click += (_, _) => _ = TestAsync();
            _saveButton.Content = Loc.T("Save");
            _saveButton.Click += async (_, _) =>
            {
                await SaveKeyAsync();
                UpdateSave();
            };
            _ok = page.Note("SuccessNoteStyle", Loc.T("OK"));
            _ok.Visibility = Visibility.Collapsed;
            _testError = page.Note("CriticalNoteStyle");
            _testError.Visibility = Visibility.Collapsed;
            _testError.MaxLines = 3;
            _removeButton.Content = Loc.T("Remove");
            _removeButton.Click += (_, _) => _ = _page._providers.RemoveProviderAsync(_id);
            var buttons = new FieldPanel();
            buttons.Children.Add(_testButton);
            buttons.Children.Add(_saveButton);
            buttons.Children.Add(_ring);
            buttons.Children.Add(_ok);
            buttons.Children.Add(_removeButton);

            Expander.Items.Add(_nameCard);
            Expander.Items.Add(_authCard);
            Expander.Items.Add(_headersCard);
            Expander.Items.Add(_keyCard);
            Expander.Items.Add(_baseCard);
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
            if (IsCustom && config.BaseURL.Length > 0) Expander.Description = config.BaseURL;
            else Expander.ClearValue(SettingsExpander.DescriptionProperty);
            _nameCard.Visibility = _authCard.Visibility = _headersCard.Visibility = Show(IsCustom);
            SetText(_nameBox, config.DisplayName);
            _authCombo.SelectedIndex = Array.IndexOf(AuthStyles, config.AuthStyle);
            // Saved on leaving the field: a half-typed line must not be replaced while the user types it.
            if (_headersBox.FocusState == FocusState.Unfocused
                && !SameHeaders(ProviderInput.ParseHeaders(_headersBox.Text, out _), config.ExtraHeaders))
                _headersBox.Text = HeadersText(config.ExtraHeaders);
            _keyCard.Visibility = _saveButton.Visibility = Show(TakesKey);
            _baseCard.Visibility = Show(IsCustom || config.Id == "azure");
            SetText(_baseBox, config.BaseURL);
            _baseBox.PlaceholderText = config.Id == "azure" ? "https://<resource>.openai.azure.com/openai/v1" : "";
            _azureNote.Visibility = Show(config.Id == "azure");
            _removeButton.Visibility = Show(IsCustom);
            AutomationProperties.SetName(_removeButton, $"{Loc.T("Remove")}, {name}");
            UpdateKey();
            UpdateSave();
        }

        public void Cancel() => _testing?.Cancel();

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

        /// <summary>Save is on while a key is typed and no test runs.</summary>
        private void UpdateSave() => _saveButton.IsEnabled = _testing is null && !string.IsNullOrWhiteSpace(_keyBox.Password);

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

        private void ShowKeyError(string? error)
        {
            if (_page.Closing) return;
            _keyError.Text = error ?? "";
            _keyError.Visibility = Show(error is not null);
        }

        /// <summary>
        /// Test (macOS <c>APIKeyCard.testButton</c>): a key typed without Enter is saved first and is the one tested. A
        /// speech-to-text API checks its key with a free GET that uploads no audio; the other APIs list their models, a
        /// request that needs a valid key and no model (Windows Settings › AI's Test). Cancel while it runs.
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
            if (_page.Closing || _page._providers.Config(_id) is not { } config) return;
            using var cancellation = _testing = new CancellationTokenSource();
            SetTesting(true);
            string? error = null;
            var ok = false;
            try
            {
                if (config.Kind == ProviderKind.SpeechToText) await _page._providers.TestSpeechKeyAsync(config, cancellation.Token);
                else await _page._providers.TestAsync(config, cancellation.Token);
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
            if (_page.Closing) return;
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
/// URL set), and "API key", which opens its card in Settings › API Keys (<see cref="ApiKeysSettingsPage.ShowApiKey"/>).
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
