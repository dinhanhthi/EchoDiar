using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using EchoDiar.AI;
using EchoDiar.AI.Cloud;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using static EchoDiar.App.Views.ProviderModelFields;

namespace EchoDiar.App.Views;

/// <summary>
/// The parts of the provider model fields (<see cref="ModelField"/>, <see cref="CloudModelField"/>,
/// <see cref="EmbeddingModelField"/>) and the local model
/// rows (<see cref="LocalModelRows"/>), shared by the settings pages that show them. Each field takes what it uses (the
/// view model, whether its page is syncing or closing, the page's styles) instead of its host page.
/// </summary>
internal static class ProviderModelFields
{
    /// <summary>
    /// Below this width a field's card puts its input under its label (the toolkit's default is 476), so the widest
    /// field line (the model id, its models menu and Load models) keeps its buttons in view in a narrow window.
    /// </summary>
    private const double FieldWrapWidth = 640;

    public static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A text in one of the page's note styles (<paramref name="resources"/>); <paramref name="text"/> as given.</summary>
    public static TextBlock Note(ResourceDictionary resources, string style, string text = "") =>
        new() { Style = (Style)resources[style], Text = text };

    /// <summary>A field of an expanded row: its label on the left, its input on the right (macOS <c>FieldRow</c>).</summary>
    public static SettingsCard Field(string label, object content)
    {
        var card = new SettingsCard { Header = Loc.T(label), Content = content };
        card.Resources["SettingsCardWrapThreshold"] = FieldWrapWidth;
        return card;
    }

    /// <summary>Sets a text box's text unless it already shows it (the caret stays where the user types).</summary>
    public static void SetText(TextBox box, string text)
    {
        if (box.Text != text) box.Text = text;
    }

    public static ProgressRing SmallRing() => new()
    {
        Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
    };

    public static HyperlinkButton Link(string text, string url) => new() { Content = Loc.T(text), NavigateUri = new Uri(url), Padding = new Thickness(0) };

    public static bool IsEnter(KeyRoutedEventArgs e) => e.Key == VirtualKey.Enter;

    /// <summary>A model list's error; a local host or custom endpoint answering 404 likely lacks <c>/v1</c>.</summary>
    public static string ErrorMessage(Exception error, ProviderConfig config)
    {
        var message = ProvidersViewModel.ShortMessage(error);
        return ProviderInput.SuggestsV1(config, error) ? $"{message} {Loc.T("Check that the base URL ends with /v1.")}" : message;
    }
}

/// <summary>
/// An expanded card's fields in two columns (macOS <c>FieldRow</c> inside <c>fieldLabelColumn()</c>): the labels, as wide
/// as the card's longest shown one, then the inputs, each with its notes below it; a row without a label lines up with
/// the inputs. Below <see cref="StackWidth"/> each label goes above its input. Rows are added once; the host shows or
/// hides them (<see cref="Row.Visible"/>), then calls <see cref="Layout"/>.
/// </summary>
internal sealed class FieldGrid
{
    /// <summary>A field: its label (none for a row of the input column alone), then its input with its notes.</summary>
    public sealed class Row(TextBlock? label, StackPanel content)
    {
        public TextBlock? Label { get; } = label;
        public StackPanel Content { get; } = content;
        public bool Visible { get; set; } = true;
    }

    /// <summary>Below this width a label goes above its input, so a narrow window keeps the inputs' buttons in view.</summary>
    private const double StackWidth = 520;

    /// <summary>Between two rows (macOS: 12).</summary>
    private const double RowGap = 12;

    /// <summary>Lowers a label onto the text of the input beside it (a text box's border and top padding).</summary>
    private const double LabelOffset = 6;

    private readonly Grid _grid = new() { ColumnSpacing = 12 };
    private readonly ColumnDefinition _labelColumn = new() { Width = GridLength.Auto };
    private readonly List<Row> _rows = [];
    private bool _stacked;

    public FieldGrid()
    {
        _grid.ColumnDefinitions.Add(_labelColumn);
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _grid.SizeChanged += (_, e) =>
        {
            var stacked = e.NewSize.Width < StackWidth;
            if (stacked == _stacked) return;
            _stacked = stacked;
            Layout();
        };
    }

    /// <summary>The grid, for the host's card (a <c>SettingsCard</c> with vertical, stretched content).</summary>
    public Grid Element => _grid;

    /// <summary>
    /// A row: <paramref name="label"/> (translated; null for the input column alone), then <paramref name="input"/> with
    /// <paramref name="notes"/> below it.
    /// </summary>
    public Row Add(string? label, UIElement input, params UIElement[] notes)
    {
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(input);
        foreach (var note in notes) content.Children.Add(note);
        var text = label is null ? null : new TextBlock
        {
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Text = label,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (text is not null) _grid.Children.Add(text);
        _grid.Children.Add(content);
        var row = new Row(text, content);
        _rows.Add(row);
        Layout();
        return row;
    }

    /// <summary>Shows the visible rows, side by side or stacked; the first shown row has no gap above it.</summary>
    public void Layout()
    {
        _labelColumn.Width = _stacked ? new GridLength(0) : GridLength.Auto;
        var first = true;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var top = row.Visible && !first ? RowGap : 0;
            if (row.Visible) first = false;
            // Stacked, a labelled row's input goes on the line below its label.
            var below = _stacked && row.Label is not null;
            row.Content.Visibility = Show(row.Visible);
            row.Content.Margin = new Thickness(0, below ? 4 : top, 0, 0);
            Grid.SetRow(row.Content, 2 * i + (below ? 1 : 0));
            Grid.SetColumn(row.Content, _stacked ? 0 : 1);
            Grid.SetColumnSpan(row.Content, _stacked ? 2 : 1);
            if (row.Label is not { } label) continue;
            label.Visibility = Show(row.Visible);
            label.Margin = new Thickness(0, top + (_stacked ? 0 : LabelOffset), 0, 0);
            Grid.SetRow(label, 2 * i);
            Grid.SetColumnSpan(label, _stacked ? 2 : 1);
        }
    }
}

/// <summary>
/// The model id, "Load models" with the loaded models' menu, and the checklist of the models the AI pane's model menu
/// offers besides it (macOS <c>ModelField</c>, <c>ProviderConfig.shownModels</c>). <c>label</c> is the model
/// card's label ("Model" by default). A host shows either the cards (<see cref="ModelCard"/>, <see cref="ShownCard"/>) or
/// their parts in its own layout (<see cref="Input"/>, <see cref="Notes"/>, <see cref="ShownPanel"/>), never both: an
/// element has one parent.
/// </summary>
internal sealed class ModelField
{
    /// <summary>Above this many models the checklist scrolls and gets a filter field.</summary>
    private const int LongList = 8;

    private readonly ProvidersViewModel _providers;
    private readonly Func<bool> _isSyncing;
    private readonly Func<bool> _isClosing;
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
    private readonly string _label;
    private SettingsCard? _modelCard;
    private SettingsCard? _shownCard;
    private List<string>? _models;
    private List<string> _shownIds = [];
    private CancellationTokenSource? _loading;
    private ProviderConfig? _config;

    /// <param name="isSyncing">The page is setting its controls from the settings: their change events are not the user's.</param>
    /// <param name="isClosing">The window is closing: nothing more is changed or shown.</param>
    /// <param name="resources">The page's resources, with its note styles.</param>
    public ModelField(
        ProvidersViewModel providers, Func<bool> isSyncing, Func<bool> isClosing, ResourceDictionary resources,
        string id, string prompt, string? browseUrl, string label = "Model")
    {
        _providers = providers;
        _isSyncing = isSyncing;
        _isClosing = isClosing;
        _id = id;
        _label = label;
        _modelBox.PlaceholderText = Loc.T(prompt);
        AutomationProperties.SetName(_modelBox, Loc.T(label));
        _modelBox.TextChanged += (_, _) => Change(c => c.DefaultModel != _modelBox.Text, c => c.DefaultModel = _modelBox.Text);
        _modelsButton.Flyout = _modelsMenu;
        _loadButton.Content = Loc.T("Load models");
        _loadButton.Click += (_, _) => _ = LoadAsync();
        Input = new FieldPanel { InputWidth = 220 };
        Input.Children.Add(_modelBox);
        Input.Children.Add(_modelsButton);
        Input.Children.Add(_loadButton);
        Input.Children.Add(_loadRing);

        var notes = Notes = new StackPanel { Spacing = 2 };
        if (browseUrl is not null) notes.Children.Add(Link("Browse models", browseUrl));
        _error = Note(resources, "CriticalNoteStyle");
        _error.Visibility = Visibility.Collapsed;
        _error.MaxLines = 2;
        _noModels = Note(resources, "NoteStyle", Loc.T("No models listed; type the model id."));
        _noModels.Visibility = Visibility.Collapsed;
        notes.Children.Add(_error);
        notes.Children.Add(_noModels);

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
        _hint = Note(resources, "NoteStyle", Loc.T("Type a model id and press Return, or load the models to pick from."));
        ShownPanel = new StackPanel { Spacing = 8 };
        ShownPanel.Children.Add(_filterBox);
        ShownPanel.Children.Add(_scroller);
        ShownPanel.Children.Add(_addBox);
        ShownPanel.Children.Add(_hint);
    }

    /// <summary>The model id, its models menu, Load models and the ring.</summary>
    public FieldPanel Input { get; }

    /// <summary>Browse models, the load error and "No models listed", below <see cref="Input"/>.</summary>
    public StackPanel Notes { get; }

    /// <summary>The checklist of the models the AI pane's model menu offers, its filter and "Add a model id".</summary>
    public StackPanel ShownPanel { get; }

    /// <summary>The label, then <see cref="Input"/> with <see cref="Notes"/> below it, in a card made on first use.</summary>
    public SettingsCard ModelCard
    {
        get
        {
            if (_modelCard is null)
            {
                _modelCard = Field(_label, Input);
                _modelCard.Description = Notes;
            }
            return _modelCard;
        }
    }

    /// <summary><see cref="ShownPanel"/> under "In the AI pane's model menu", in a card made on first use.</summary>
    public SettingsCard ShownCard => _shownCard ??= new SettingsCard
    {
        Header = Loc.T("In the AI pane's model menu"),
        ContentAlignment = ContentAlignment.Vertical,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Content = ShownPanel,
    };

    public void Update(ProviderConfig config)
    {
        _config = config;
        SetText(_modelBox, config.DefaultModel);
        UpdateChecklist();
    }

    public void Cancel() => _loading?.Cancel();

    private void Change(Func<ProviderConfig, bool> differs, Action<ProviderConfig> change)
    {
        if (_isSyncing() || _isClosing() || _providers.Config(_id) is not { } config || !differs(config)) return;
        _providers.UpdateProvider(_id, change);
    }

    /// <summary>Fills the menu from the provider's model list, off the UI thread (macOS <c>loadModels</c>).</summary>
    private async Task LoadAsync()
    {
        if (_loading is not null || _providers.Config(_id) is not { } config) return;
        using var loading = _loading = new CancellationTokenSource();
        _loadButton.IsEnabled = false;
        _loadRing.IsActive = true;
        _loadRing.Visibility = Visibility.Visible;
        string? error = null;
        try
        {
            _models = [.. await _providers.ListModelsAsync(config, loading.Token)];
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
        if (_isClosing()) return;
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
/// A cloud engine's transcription model (macOS <c>CloudModelField</c>): free text, a menu of the known models and, for
/// the APIs that list their models, Load models (only the ones that transcribe). With <c>diarization</c>, the cloud
/// diarizer's diarization model: only models that label speakers are listed, and a note says when the typed one does
/// not. AssemblyAI picks its model. With <c>realtime</c>, the realtime model streamed while recording ("Realtime
/// model"): its known models only, and the host it streams to below. <c>label</c> is the card's label ("Model", or
/// "Realtime model" with <c>realtime</c>, by default). A host shows either <see cref="Card"/> or its parts in its own
/// layout (<see cref="Input"/>, <see cref="Notes"/>; not with <c>realtime</c>), never both: an element has one parent.
/// </summary>
internal sealed class CloudModelField
{
    /// <summary>The speech-to-text APIs have no model list; their known models are offered.</summary>
    private static readonly Dictionary<string, string[]> KnownModels = new(OpenAICompatibleTranscriber.PresetModels)
    {
        ["elevenlabs"] = [ElevenLabsTranscriber.DefaultModel],
        ["deepgram"] = [DeepgramTranscriber.DefaultModel],
        ["gemini"] = OpenAICompatibleTranscriber.PresetModels.TryGetValue("gemini", out var gemini) ? gemini : [GeminiAudioTranscriber.DefaultModel],
        ["xai"] = [XAITranscriber.DefaultModel],
        ["speechmatics"] = [SpeechmaticsTranscriber.DefaultModel, "standard", "melia-1"],
        ["soniox"] = [SonioxTranscriber.DefaultModel],
        ["gladia"] = [GladiaTranscriber.DefaultModel],
    };

    private readonly ProvidersViewModel _providers;
    private readonly Func<bool> _isSyncing;
    private readonly Func<bool> _isClosing;
    private readonly bool _diarization;
    /// <summary>Edits <see cref="ProviderConfig.RealtimeModel"/> instead of the transcription model.</summary>
    private readonly bool _realtime;
    private readonly TextBlock? _host;
    private readonly TextBox _box = new();
    private readonly TextBlock _picked;
    private readonly DropDownButton _modelsButton = new() { Visibility = Visibility.Collapsed };
    private readonly MenuFlyout _modelsMenu = new();
    private readonly Button _loadButton = new();
    private readonly ProgressRing _ring = SmallRing();
    private readonly TextBlock _error;
    private readonly TextBlock _empty;
    private readonly TextBlock _noSpeakers;
    private readonly TextBlock _streamingOnly;
    private readonly FieldPanel _input;
    private readonly string _label;
    private SettingsCard? _card;
    private ProviderConfig? _config;
    private List<string>? _loaded;
    private string? _loadError;
    private List<string> _choices = [];
    private CancellationTokenSource? _loading;

    /// <param name="isSyncing">The page is setting its controls from the settings: their change events are not the user's.</param>
    /// <param name="isClosing">The window is closing: nothing more is changed or shown.</param>
    /// <param name="resources">The page's resources, with its note styles.</param>
    public CloudModelField(
        ProvidersViewModel providers, Func<bool> isSyncing, Func<bool> isClosing, ResourceDictionary resources,
        bool diarization, bool realtime = false, string? label = null)
    {
        _providers = providers;
        _isSyncing = isSyncing;
        _isClosing = isClosing;
        _diarization = diarization;
        _realtime = realtime;
        _label = label ??= realtime ? "Realtime model" : "Model";
        AutomationProperties.SetName(_box, Loc.T(label));
        _box.TextChanged += (_, _) => SetModel(_box.Text);
        _modelsButton.Flyout = _modelsMenu;
        _loadButton.Content = Loc.T("Load models");
        _loadButton.Click += (_, _) => _ = LoadAsync();
        _input = new FieldPanel { InputWidth = 260, Children = { _box, _modelsButton, _loadButton, _ring } };
        _picked = Note(resources, "NoteStyle");
        _picked.Visibility = Visibility.Collapsed;
        // The input, or AssemblyAI's "Picked by" note in its place.
        Input = new Grid { Children = { _input, _picked } };
        _error = Note(resources, "CriticalNoteStyle");
        _error.MaxLines = 2;
        _empty = Note(resources, "NoteStyle", diarization
            ? Loc.T("No diarization models listed; type the model id.")
            : Loc.T("No transcription models listed; type the model id."));
        // The macOS text, with "this PC" for "this Mac".
        _noSpeakers = new TextBlock
        {
            Style = (Style)resources["CautionNoteStyle"],
            Text = Loc.T("This model does not label speakers; a model on this PC is used instead."),
        };
        _streamingOnly = Note(resources, "CriticalNoteStyle",
            Loc.T("This model only streams live audio and can't transcribe a recording. Pick another model."));
        Notes = new StackPanel { Spacing = 2 };
        Notes.Children.Add(_error);
        Notes.Children.Add(_streamingOnly);
        Notes.Children.Add(_empty);
        Notes.Children.Add(_noSpeakers);
        // The host in place of the notes, which are about the transcription model.
        if (realtime) _host = Note(resources, "NoteStyle");
    }

    /// <summary>The model id, its models menu, Load models and the ring; for AssemblyAI, the note that it picks the model.</summary>
    public Grid Input { get; }

    /// <summary>The load error and the model's warnings, below <see cref="Input"/>.</summary>
    public StackPanel Notes { get; }

    /// <summary>The label, then <see cref="Input"/> with <see cref="Notes"/> (the host with <c>realtime</c>) below it, made on first use.</summary>
    public SettingsCard Card
    {
        get
        {
            if (_card is null)
            {
                _card = Field(_label, Input);
                _card.Description = _host ?? (UIElement)Notes;
                // Its search entry.
                if (_realtime) _card.Tag = "Realtime model";
            }
            return _card;
        }
    }

    /// <summary>The streaming-only or "does not label speakers" warning shows.</summary>
    public bool Warns { get; private set; }

    public void Cancel() => _loading?.Cancel();

    /// <summary>Shows its engine's <paramref name="config"/>; the text the user is typing stays as it is.</summary>
    public void Update(ProviderConfig config)
    {
        _config = config;
        if (_host is not null)
        {
            UpdateRealtime(config, _host);
            return;
        }
        if (config.Preset == "assemblyai")
        {
            _picked.Text = Loc.T(_diarization
                ? "Picked by AssemblyAI (Universal, speaker labels)"
                : "Picked by AssemblyAI for the language (Universal)");
            _input.Visibility = Notes.Visibility = Visibility.Collapsed;
            _picked.Visibility = Visibility.Visible;
            Warns = false;
            return;
        }
        _input.Visibility = Notes.Visibility = Visibility.Visible;
        _picked.Visibility = Visibility.Collapsed;
        var model = _diarization ? config.DiarizationModel : config.TranscriptionModel;
        if (_box.Text != model) _box.Text = model;
        var fallback = _diarization ? CloudTranscription.DefaultDiarizationModel(config) : CloudTranscription.DefaultModel(config);
        _box.PlaceholderText = fallback.Length > 0
            ? fallback
            : Loc.T(_diarization ? "Diarization model id" : "Transcription model id, e.g. whisper-1");
        IEnumerable<string> known = _diarization
            ? [CloudTranscription.DefaultDiarizationModel(config)]
            : KnownModels.TryGetValue(config.Preset ?? "", out var models) ? models : [];
        var choices = known.Concat(_loaded ?? []).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        ShowChoices(choices);
        _loadButton.Visibility = Show(config.Kind == ProviderKind.OpenAICompatible);
        _error.Text = _loadError ?? "";
        _error.Visibility = Show(_loadError is not null);
        _empty.Visibility = Show(_loadError is null && _loaded is { Count: 0 });
        // A streaming-only diarization model falls back to this PC's diarizer: _noSpeakers says so.
        _streamingOnly.Visibility = Show(!_diarization && CloudTranscription.IsStreamingOnly(CloudTranscription.Model(config)));
        _noSpeakers.Visibility = Show(_diarization && !CloudTranscription.Diarizes(config, CloudTranscription.DiarizationModel(config)));
        Warns = _streamingOnly.Visibility == Visibility.Visible || _noSpeakers.Visibility == Visibility.Visible;
    }

    /// <summary>
    /// The realtime model (macOS <c>CloudModelField(realtime: true)</c>): the default as the placeholder and the known
    /// models in the menu; no AssemblyAI note, no Load models, no streaming-only warning; "Streams to" the host.
    /// </summary>
    private void UpdateRealtime(ProviderConfig config, TextBlock host)
    {
        if (_box.Text != config.RealtimeModel) _box.Text = config.RealtimeModel;
        _box.PlaceholderText = AppViewModel.RealtimeDefaultModel(config);
        List<string> choices = [.. AppViewModel.RealtimeModels(config).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)];
        ShowChoices(choices);
        _loadButton.Visibility = Visibility.Collapsed;
        var hostName = AppViewModel.RealtimeHost(config);
        host.Text = hostName is null ? "" : Loc.Format("Streams to %@", hostName);
        host.Visibility = Show(hostName is not null);
        Warns = false;
    }

    /// <summary>The models menu offers <paramref name="choices"/>, rebuilt only when they change.</summary>
    private void ShowChoices(List<string> choices)
    {
        if (!choices.SequenceEqual(_choices))
        {
            _choices = choices;
            _modelsMenu.Items.Clear();
            foreach (var id in choices)
            {
                var item = new MenuFlyoutItem { Text = id };
                item.Click += (_, _) => SetModel(id);
                _modelsMenu.Items.Add(item);
            }
        }
        _modelsButton.Content = Loc.Plural("%lld models", choices.Count);
        _modelsButton.Visibility = Show(choices.Count > 0);
    }

    private void SetModel(string id)
    {
        if (_isSyncing() || _isClosing() || _config is not { } shown || _providers.Config(shown.Id) is not { } config) return;
        if ((_realtime ? config.RealtimeModel : _diarization ? config.DiarizationModel : config.TranscriptionModel) == id) return;
        _providers.UpdateProvider(config.Id, c =>
        {
            if (_realtime) c.RealtimeModel = id;
            else if (_diarization) c.DiarizationModel = id;
            else c.TranscriptionModel = id;
        });
    }

    /// <summary>The provider's models that transcribe (with <c>diarization</c>: that label speakers), sorted; off the UI thread.</summary>
    private async Task LoadAsync()
    {
        if (_config is not { } config || _loading is not null) return;
        using var cancellation = _loading = new CancellationTokenSource();
        _loadButton.IsEnabled = false;
        _ring.IsActive = true;
        _ring.Visibility = Visibility.Visible;
        try
        {
            var all = await _providers.ListModelsAsync(config, cancellation.Token);
            _loaded = [.. all.Where(id => Transcribes(id, config.Preset) && !CloudTranscription.IsStreamingOnly(id) && (!_diarization || CloudTranscription.Diarizes(config, id)))
                .Order(StringComparer.Ordinal)];
            _loadError = null;
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested)
        {
            _loadError = ProvidersViewModel.ShortMessage(e);
        }
        catch (Exception)
        {
            // Cancelled (the page closed or the default changed): no result.
        }
        finally
        {
            _loading = null;
        }
        if (_isClosing()) return;
        _loadButton.IsEnabled = true;
        _ring.IsActive = false;
        _ring.Visibility = Visibility.Collapsed;
        if (_providers.Config(config.Id) is { } current) Update(current);
    }

    /// <summary>
    /// Transcription models are named after it (whisper-1, gpt-4o-transcribe, voxtral-mini); Gemini transcribes with
    /// its own models through the native audio API.
    /// </summary>
    private static bool Transcribes(string id, string? preset)
    {
        id = id.ToLowerInvariant();
        if (preset == "gemini") return id.Contains("gemini", StringComparison.Ordinal);
        return new[] { "whisper", "transcribe", "voxtral", "scribe", "nova", "speech" }.Any(part => id.Contains(part, StringComparison.Ordinal));
    }
}

/// <summary>
/// The default transcription model, or with <c>diarization</c> the default diarizer, of new recordings (macOS
/// <c>DefaultEnginePicker</c>): the installed local models of that kind that can transcribe the next recording's language
/// (the rule of a local row's Set default, <see cref="ModelRowState.CanSetDefault"/>), then the configured cloud engines
/// (<see cref="AppViewModel.CloudEngineConfigs"/>, <see cref="AppViewModel.CloudDiarizerConfigs"/>). A default that is
/// not offered (no longer installed or configured, or not for that language) stays listed, so the menu is never blank.
/// A cloud default shows the "Audio leaves this PC" caution below the label. The host places <see cref="Card"/> and calls
/// <see cref="Update"/> when it shows and on <see cref="ModelsViewModel.StatesChanged"/> (installs, the defaults, the
/// cloud configs and keys, the next recording's language). Used by Settings › Transcription and AI Settings ›
/// Transcription.
/// </summary>
internal sealed class DefaultEnginePicker
{
    private readonly AppViewModel _model;
    private readonly ModelsViewModel _models;
    private readonly bool _diarization;
    private readonly ComboBox _combo = new() { MinWidth = 260, MaxWidth = 420 };
    private readonly StackPanel _caution;

    /// <summary>The menu's engine ids and names, in item order; rebuilt when they change.</summary>
    private List<(string Id, string Name)> _choices = [];

    /// <summary>The menu is being set from the settings: its change events are not the user's.</summary>
    private bool _syncing;

    /// <param name="resources">The host page's resources, with its <c>CautionNoteStyle</c> and <c>CautionIconStyle</c>.</param>
    public DefaultEnginePicker(AppViewModel model, bool diarization, ResourceDictionary resources)
    {
        _model = model;
        _models = model.ModelsModel;
        _diarization = diarization;
        var title = diarization ? "Default diarization model" : "Default transcription model";
        AutomationProperties.SetName(_combo, Loc.T(title));
        _combo.SelectionChanged += (_, _) =>
        {
            var index = _combo.SelectedIndex;
            if (!_syncing && !_model.IsClosing && index >= 0 && index < _choices.Count) SetDefault(_choices[index].Id);
        };
        _caution = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _caution.Children.Add(new FontIcon { Style = (Style)resources["CautionIconStyle"], Glyph = "\uE753" });
        _caution.Children.Add(new TextBlock
        {
            Style = (Style)resources["CautionNoteStyle"],
            // The macOS text, with "this PC" for "this Mac".
            Text = Loc.T(diarization
                ? "Audio leaves this PC: the cloud diarizer uploads the system channel to the provider."
                : "Audio leaves this PC: cloud engines upload the recording to the provider."),
        });
        Card = new SettingsCard { Header = Loc.T(title), Content = _combo, Tag = title };
    }

    /// <summary>The label, the caution below it and the menu; its Tag is its search entry's title.</summary>
    public SettingsCard Card { get; }

    /// <summary>The menu shows the current choices and default; the caution shows for a cloud default.</summary>
    public void Update()
    {
        if (_model.IsClosing) return;
        var selection = _diarization ? _models.DefaultDiarizerModelID : _models.DefaultASRModelID;
        var kind = _diarization ? ModelKind.Diarization : ModelKind.Asr;
        List<(string Id, string Name)> choices =
        [
            .. _models.Entries
                .Where(e => e.Kind == kind && _models.State(e) is { IsInstalled: true, CanSetDefault: true })
                .Select(e => (e.Id, e.DisplayName)),
            .. (_diarization ? _model.CloudDiarizerConfigs : _model.CloudEngineConfigs)
                .Select(c => (CloudTranscription.EngineId(c.Id), CloudName(c))),
        ];
        if (!choices.Any(c => ProvidersViewModel.SameId(c.Id, selection))) choices.Add((selection, FallbackName(selection)));
        _syncing = true;
        try
        {
            if (!choices.SequenceEqual(_choices))
            {
                _choices = choices;
                _combo.Items.Clear();
                foreach (var (_, name) in choices) _combo.Items.Add(name);
            }
            _combo.SelectedIndex = _choices.FindIndex(c => ProvidersViewModel.SameId(c.Id, selection));
        }
        finally
        {
            _syncing = false;
        }
        // Set or cleared (not collapsed), so a local default leaves no empty description line.
        Card.Description = CloudTranscription.ProviderId(selection) is not null ? _caution : null!; // null hides it
    }

    /// <summary>
    /// A cloud engine through <see cref="ModelsViewModel.SetDefault(string, bool)"/>; a local model through
    /// <see cref="ModelsViewModel.SetDefault(ModelEntry)"/>, which stores the catalog default as none (macOS
    /// <c>setDefaultASRModel</c>, <c>setDefaultDiarizer</c>).
    /// </summary>
    private void SetDefault(string id)
    {
        var current = _diarization ? _models.DefaultDiarizerModelID : _models.DefaultASRModelID;
        if (ProvidersViewModel.SameId(id, current)) return;
        if (CloudTranscription.ProviderId(id) is not null) _models.SetDefault(id, _diarization);
        else if (_models.Entries.FirstOrDefault(e => e.Id == id) is { } entry) _models.SetDefault(entry);
    }

    /// <summary>"Deepgram (nova-3)" with the model it runs; the provider name alone when it picks the model.</summary>
    private string CloudName(ProviderConfig config)
    {
        if (!_diarization) return AppViewModel.CloudEngineName(config);
        var model = CloudTranscription.DiarizationModel(config);
        return model.Length == 0 ? config.DisplayName : $"{config.DisplayName} ({model})";
    }

    /// <summary>The catalog or provider name of a default that is not offered; the raw id when it is gone.</summary>
    private string FallbackName(string id)
    {
        if (CloudTranscription.ProviderId(id) is { } providerId)
        {
            return _model.ProvidersModel.Config(providerId)?.DisplayName ?? providerId;
        }
        return ModelCatalog.Entry(id)?.DisplayName ?? id;
    }
}

/// <summary>
/// A provider's embedding model for Library chat (macOS <c>EmbeddingModelField</c>, <see cref="ProviderConfig.EmbeddingModel"/>),
/// saved on Enter, on leaving the field and when the field unloads (its page left), so a typed model is not lost without
/// Enter. Not on each keystroke: each change restarts the index while the provider embeds it. The preset's suggested
/// model is the placeholder (<see cref="RAGSettingsPage.SuggestedModels"/>); "Load models" lists the ones that embed.
/// </summary>
internal sealed class EmbeddingModelField
{
    private readonly ProvidersViewModel _providers;
    private readonly Func<bool> _isSyncing;
    private readonly Func<bool> _isClosing;
    private readonly string _id;
    private readonly TextBox _box = new();
    private readonly DropDownButton _modelsButton = new() { Visibility = Visibility.Collapsed };
    private readonly MenuFlyout _modelsMenu = new();
    private readonly Button _loadButton = new();
    private readonly ProgressRing _ring = SmallRing();
    private readonly TextBlock _error;
    private readonly TextBlock _empty;
    /// <summary>The saved model the field last showed (macOS <c>onChange(of: config.embeddingModel)</c>): typing stays until it changes.</summary>
    private string? _shown;
    private CancellationTokenSource? _loading;

    /// <param name="isSyncing">The page is setting its controls from the settings: their change events are not the user's.</param>
    /// <param name="isClosing">The window is closing: nothing more is changed or shown.</param>
    /// <param name="resources">The page's resources, with its note styles.</param>
    public EmbeddingModelField(
        ProvidersViewModel providers, Func<bool> isSyncing, Func<bool> isClosing, ResourceDictionary resources, string id)
    {
        _providers = providers;
        _isSyncing = isSyncing;
        _isClosing = isClosing;
        _id = id;
        AutomationProperties.SetName(_box, Loc.T("Embedding model"));
        _box.KeyDown += (_, e) =>
        {
            if (!IsEnter(e)) return;
            e.Handled = true;
            Save();
        };
        _box.LostFocus += (_, _) => Save();
        _box.Unloaded += (_, _) => Save();
        _modelsButton.Flyout = _modelsMenu;
        _loadButton.Content = Loc.T("Load models");
        _loadButton.Click += (_, _) => _ = LoadAsync();
        Input = new FieldPanel { InputWidth = 260, Children = { _box, _modelsButton, _loadButton, _ring } };
        _error = Note(resources, "CriticalNoteStyle");
        _error.Visibility = Visibility.Collapsed;
        _error.MaxLines = 2;
        _empty = Note(resources, "NoteStyle", Loc.T("No embedding models listed; type the model id."));
        _empty.Visibility = Visibility.Collapsed;
        Notes = new StackPanel { Spacing = 2, Children = { _error, _empty } };
    }

    /// <summary>The model id, its models menu once loaded, Load models and the ring.</summary>
    public FieldPanel Input { get; }

    /// <summary>The load error or "No embedding models listed", below <see cref="Input"/>.</summary>
    public StackPanel Notes { get; }

    public void Cancel() => _loading?.Cancel();

    /// <summary>Shows <paramref name="config"/>'s embedding model when it changed; the text the user is typing stays.</summary>
    public void Update(ProviderConfig config)
    {
        _box.PlaceholderText = RAGSettingsPage.SuggestedModels.GetValueOrDefault(config.Preset ?? "") ?? Loc.T("Embedding model id");
        if (_shown == config.EmbeddingModel) return;
        _shown = config.EmbeddingModel;
        SetText(_box, config.EmbeddingModel);
    }

    /// <summary>Saves the typed model, trimmed, when it differs (macOS <c>save</c>).</summary>
    private void Save()
    {
        if (_isSyncing() || _isClosing() || _providers.Config(_id) is not { } config) return;
        // A removed local host is not brought back by its preset (macOS).
        if (!config.IsCloud && !_providers.SavedLocalHosts.Any(h => ProvidersViewModel.SameId(h.Id, _id))) return;
        var trimmed = _box.Text.Trim();
        SetText(_box, trimmed);
        if (trimmed == config.EmbeddingModel) return;
        _providers.UpdateProvider(_id, c => c.EmbeddingModel = trimmed);
    }

    /// <summary>The provider's models that embed, by name (its chat models can't), sorted; off the UI thread (macOS <c>load</c>).</summary>
    private async Task LoadAsync()
    {
        if (_loading is not null || _providers.Config(_id) is not { } config) return;
        using var loading = _loading = new CancellationTokenSource();
        _loadButton.IsEnabled = false;
        _ring.IsActive = true;
        _ring.Visibility = Visibility.Visible;
        List<string>? models = null;
        string? error = null;
        try
        {
            var all = await _providers.ListModelsAsync(config, loading.Token);
            models = [.. all.Where(RAGSettingsPage.IsEmbeddingModel).Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (!loading.IsCancellationRequested)
        {
            error = ProvidersViewModel.ShortMessage(e);
        }
        catch (Exception)
        {
            // Cancelled: the page went away.
        }
        _loading = null;
        if (_isClosing()) return;
        _loadButton.IsEnabled = true;
        _ring.IsActive = false;
        _ring.Visibility = Visibility.Collapsed;
        if (loading.IsCancellationRequested) return;
        _error.Text = error ?? "";
        _error.Visibility = Show(error is not null);
        _empty.Visibility = Show(error is null && models is { Count: 0 });
        if (models is null) return;
        _modelsMenu.Items.Clear();
        foreach (var id in models)
        {
            var item = new MenuFlyoutItem { Text = id };
            item.Click += (_, _) =>
            {
                _box.Text = id;
                Save();
            };
            _modelsMenu.Items.Add(item);
        }
        _modelsButton.Content = Loc.Plural("%lld models", models.Count);
        _modelsButton.Visibility = Show(models.Count > 0);
    }
}

/// <summary>
/// The local speech and speaker models' rows (macOS <c>ModelRow</c>), a header per tier: made once by
/// <see cref="AddSections"/>, then updated in place by <see cref="Update"/> (a download's progress changes them often).
/// </summary>
internal sealed class LocalModelRows
{
    /// <summary>The parts of a model's row that change with its state.</summary>
    private sealed record ModelRow(
        ModelEntry Entry,
        Border DefaultBadge,
        TextBlock UnsupportedText,
        TextBlock ErrorText,
        Button SetDefaultButton,
        ProgressBar Progress,
        TextBlock PercentText,
        Button CancelButton,
        StackPanel InstalledLabel,
        Button DeleteButton,
        Button DownloadButton);

    private readonly ModelsViewModel _models;
    private readonly ResourceDictionary _resources;

    /// <summary>One row per listed model, made once and updated in place.</summary>
    private readonly List<ModelRow> _rows = [];

    /// <param name="resources">The page's resources, with its section, badge, note and icon styles.</param>
    public LocalModelRows(ModelsViewModel models, ResourceDictionary resources)
    {
        _models = models;
        _resources = resources;
    }

    /// <summary>A header per tier that has <paramref name="kind"/> models, each followed by its rows.</summary>
    public void AddSections(StackPanel list, ModelKind kind)
    {
        foreach (var (tier, entries) in _models.Sections(kind))
        {
            var header = new StackPanel { Style = (Style)_resources["SectionHeaderStyle"] };
            header.Children.Add(new TextBlock
            {
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Text = ModelsViewModel.TierName(tier),
            });
            list.Children.Add(header);
            foreach (var entry in entries) list.Children.Add(CreateRow(entry));
        }
    }

    /// <summary>Every row shows its model's state (macOS <c>ModelRow.actions</c>).</summary>
    public void Update()
    {
        foreach (var row in _rows)
        {
            var state = _models.State(row.Entry);
            var downloading = state.Download is not null;
            var installed = !downloading && state.IsInstalled;
            row.DefaultBadge.Visibility = Show(state.IsDefault);
            row.SetDefaultButton.Visibility = Show(state.ShowSetDefault);
            row.SetDefaultButton.IsEnabled = state.CanSetDefault;
            row.Progress.Visibility = row.PercentText.Visibility = row.CancelButton.Visibility = Show(downloading);
            if (state.Download is { } fraction)
            {
                row.Progress.Value = fraction;
                row.PercentText.Text = fraction.ToString("P0", CultureInfo.CurrentCulture);
            }
            row.InstalledLabel.Visibility = row.DeleteButton.Visibility = Show(installed);
            row.DeleteButton.IsEnabled = state.CanDelete;
            row.DownloadButton.Visibility = Show(!downloading && !state.IsInstalled);
            row.DownloadButton.IsEnabled = state.CanDownload;
            row.UnsupportedText.Text = state.UnsupportedNote ?? "";
            row.UnsupportedText.Visibility = Show(state.UnsupportedNote is not null);
            row.ErrorText.Text = state.Error ?? "";
            row.ErrorText.Visibility = Show(state.Error is not null);
        }
    }

    /// <summary>
    /// A model's card (macOS <c>ModelRow</c>): its name and Default badge; its details, quality, unsupported language,
    /// memory warning and last error below; its actions on the right.
    /// </summary>
    private SettingsCard CreateRow(ModelEntry entry)
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
            Style = (Style)_resources["DefaultBadgeStyle"],
            Child = new TextBlock { Style = (Style)_resources["DefaultBadgeTextStyle"], Text = Loc.T("Default") },
            Visibility = Visibility.Collapsed,
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(name);
        header.Children.Add(badge);

        var description = new StackPanel { Spacing = 2 };
        description.Children.Add(Note(_resources, "NoteStyle", Loc.T(ModelsViewModel.Details(entry))));
        if (ModelsViewModel.QualityNote(entry) is { } quality) description.Children.Add(Note(_resources, "NoteStyle", Loc.T(quality)));
        var unsupported = Note(_resources, "CautionNoteStyle");
        unsupported.Visibility = Visibility.Collapsed;
        description.Children.Add(unsupported);
        if (ModelsViewModel.NeedsMuchMemory(entry))
        {
            var warning = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            warning.Children.Add(new FontIcon { Style = (Style)_resources["CautionIconStyle"], Glyph = "" });
            warning.Children.Add(Note(_resources, "CautionNoteStyle", Loc.T("Needs more than half of this PC's memory.")));
            description.Children.Add(warning);
        }
        var error = Note(_resources, "CriticalNoteStyle");
        error.Visibility = Visibility.Collapsed;
        description.Children.Add(error);

        var setDefault = ActionButton("Set default", entry, () => _models.SetDefault(entry));
        var progress = new ProgressBar { Width = 90, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(progress, entry.DisplayName);
        var percent = new TextBlock
        {
            Style = (Style)_resources["NoteStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 32,
            TextAlignment = TextAlignment.Right,
        };
        var cancel = ActionButton("Cancel", entry, () => _models.CancelDownload(entry));
        var installed = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        installed.Children.Add(new FontIcon { Style = (Style)_resources["SuccessIconStyle"], Glyph = "" });
        installed.Children.Add(new TextBlock { Style = (Style)_resources["SuccessNoteStyle"], Text = Loc.T("Installed") });
        var delete = ActionButton("Delete", entry, () => _ = _models.DeleteAsync(entry));
        var download = ActionButton("Download", entry, () => _models.Download(entry));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var part in new UIElement[] { setDefault, progress, percent, cancel, installed, delete, download })
        {
            part.Visibility = Visibility.Collapsed;
            actions.Children.Add(part);
        }

        _rows.Add(new ModelRow(entry, badge, unsupported, error, setDefault, progress, percent, cancel, installed, delete, download));
        return new SettingsCard { Header = header, Description = description, Content = actions };
    }

    /// <summary>A row's button, named with its model for screen readers ("Download, Whisper Base (141 MB)").</summary>
    private static Button ActionButton(string text, ModelEntry entry, Action click)
    {
        var button = new Button { Content = Loc.T(text) };
        AutomationProperties.SetName(button, $"{Loc.T(text)}, {entry.DisplayName}");
        button.Click += (_, _) => click();
        return button;
    }
}
