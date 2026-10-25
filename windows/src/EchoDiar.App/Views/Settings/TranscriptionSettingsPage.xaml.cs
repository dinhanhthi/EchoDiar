using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using EchoDiar.AI.Cloud;
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
/// Settings › Transcription (macOS ModelsSettingsView.swift), the General, Transcription model and Diarization model
/// inner tabs; the title bar picks the tab (<see cref="AppViewModel.SubTabOf"/>). Every setting shows
/// <see cref="SettingsViewModel"/> and changes it; the change is saved after its delay. The model rows show
/// <see cref="ModelsViewModel"/>'s state, kept up to date in place (a download's progress changes them often). The cloud
/// rows and the speech-to-text APIs (macOS <c>cloudSection</c>, <c>speechToTextSection</c>) show
/// <see cref="ProvidersViewModel"/>'s configs and <see cref="AppViewModel.CloudEngineConfigs"/>, made once per provider
/// and updated in place; API keys go to Credential Manager only and are never read back into the UI.
/// </summary>
public sealed partial class TranscriptionSettingsPage : UserControl
{
    public const string GeneralTab = "general";
    public const string TranscriptionModelsTab = "transcriptionModels";
    public const string DiarizationModelsTab = "diarizationModels";

    /// <summary>macOS <c>SettingsTab.transcription.subTabs</c>.</summary>
    public static IReadOnlyList<SettingsSubTab> SubTabs { get; } =
    [
        new(GeneralTab, "General"),
        new(TranscriptionModelsTab, "Transcription model"),
        new(DiarizationModelsTab, "Diarization model"),
    ];

    /// <summary>The searchable rows (macOS SettingsSearch.swift, Transcription), each the Tag of its element.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Default language", ["language", "English", "French", "Vietnamese"], GeneralTab),
        new("Transcribe while recording", ["live transcript", "captions", "real-time", "subtitles", "Whisper", "Parakeet"], GeneralTab),
        new("Free unused models after", ["memory", "RAM", "idle", "unload", "pause"], GeneralTab),
        new("Delete audio after transcription", ["remove audio", "Recycle Bin", "trash", "save space"], GeneralTab),
        new("Transcription models", ["download", "Whisper", "Parakeet", "fast", "balanced", "quality"], TranscriptionModelsTab),
        new("Cloud transcription engines", ["upload", "API", "default engine", .. SpeechToTextNames()], TranscriptionModelsTab),
        new("Speech-to-text APIs", ["API key", "cloud transcription", .. SpeechToTextNames()], TranscriptionModelsTab),
        new("Diarization models", ["speaker model", "speakers", "pyannote", "download"], DiarizationModelsTab),
        new("Cloud diarization", ["OpenAI", "ElevenLabs", "Deepgram", "AssemblyAI", "Gemini", "speakers", "cloud"], DiarizationModelsTab),
        new("Tell speakers apart", ["diarization", "speakers", "who spoke"], DiarizationModelsTab),
        new("Number of other speakers", ["speaker count", "diarization", "people"], DiarizationModelsTab),
    ];

    /// <summary>macOS's text with Windows' engines (a Windows-only key: the macOS text names WhisperKit).</summary>
    private const string LiveDescription =
        "Shows the transcript during the recording and keeps it, so it is ready within seconds of Stop. Uses more CPU and "
        + "memory while recording. Whisper and Parakeet models only; other engines transcribe after Stop.";

    private readonly AppViewModel _model;
    private readonly SettingsViewModel _settings;
    private readonly ModelsViewModel _models;
    private readonly ProvidersViewModel _providers;
    private readonly CloudSection _transcriptionCloud;
    private readonly CloudSection _diarizationCloud;

    /// <summary>One row per speech-to-text API, made once and updated in place.</summary>
    private readonly Dictionary<string, SpeechProviderRow> _speechRows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One row per listed model, made once and updated in place.</summary>
    private readonly List<ModelRow> _rows = [];

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    /// <summary>The language menu's codes, in item order; rebuilt when the choices change.</summary>
    private List<string> _languageCodes = [];

    /// <summary>The idle menu's minutes, in item order.</summary>
    private IReadOnlyList<int> _idleMinutes = [];

    public TranscriptionSettingsPage(AppViewModel model)
    {
        _model = model;
        _settings = model.SettingsModel;
        _models = model.ModelsModel;
        _providers = model.ProvidersModel;
        InitializeComponent();
        _transcriptionCloud = new CloudSection(this, TranscriptionCloudList, diarization: false);
        _diarizationCloud = new CloudSection(this, DiarizationCloudList, diarization: true);
        SpeechToTextList.Children.Add(SectionHeader(
            "Speech-to-text APIs",
            "OpenAI, Groq, Mistral, Google Gemini and custom endpoints transcribe too, with their transcription model: set them up in Settings › AI.",
            "Speech-to-text APIs"));
        LiveCard.Description = Loc.T(LiveDescription);
        SpeakerCountCombo.Items.Add(Loc.T("Auto"));
        var (min, max) = AppSettings.SpeakerCountHintRange;
        for (var count = min; count <= max; count++) SpeakerCountCombo.Items.Add(count.ToString(CultureInfo.CurrentCulture));
        AddSections(TranscriptionList, ModelKind.Asr);
        AddSections(DiarizationList, ModelKind.Diarization);

        LanguageCombo.SelectionChanged += (_, _) =>
        {
            var index = LanguageCombo.SelectedIndex;
            if (!_syncing && index >= 0 && index < _languageCodes.Count) _settings.DefaultLanguage = _languageCodes[index];
        };
        LiveToggle.Toggled += (_, _) => Set(() => _settings.TranscribeWhileRecording = LiveToggle.IsOn);
        IdleCombo.SelectionChanged += (_, _) =>
        {
            var index = IdleCombo.SelectedIndex;
            if (!_syncing && index >= 0 && index < _idleMinutes.Count) _settings.ModelIdleMinutes = _idleMinutes[index];
        };
        DeleteAfterToggle.Toggled += (_, _) => Set(() => _settings.DeleteAudioAfterTranscription = DeleteAfterToggle.IsOn);
        DiarizationToggle.Toggled += (_, _) => Set(() => _settings.Diarization = DiarizationToggle.IsOn);
        SpeakerCountCombo.SelectionChanged += (_, _) =>
        {
            // Item 0 is Auto (no hint), item n is n speakers from the range's start.
            var index = SpeakerCountCombo.SelectedIndex;
            if (!_syncing && index >= 0) _settings.SpeakerCountHint = index == 0 ? null : min + index - 1;
        };

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _settings.PropertyChanged += OnSettingsChanged;
            _models.PropertyChanged += OnModelsChanged;
            _models.StatesChanged += UpdateRows;
            _providers.Changed += RefreshCloud;
            Refresh();
            UpdateRows();
            RefreshCloud();
            ShowTab();
            InstalledText.Text = _models.InstalledText;
            _ = _models.LoadAsync();
            _ = _providers.RefreshKeysAsync();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _settings.PropertyChanged -= OnSettingsChanged;
            _models.PropertyChanged -= OnModelsChanged;
            _models.StatesChanged -= UpdateRows;
            _providers.Changed -= RefreshCloud;
            // macOS: a test or a model list stops when its row goes away.
            _transcriptionCloud.Cancel();
            _diarizationCloud.Cancel();
            foreach (var row in _speechRows.Values) row.Cancel();
        };
    }

    private string? Tab => _model.SubTabOf(SettingsSearch.Transcription);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.SettingsSubTab):
                ShowTab();
                break;
            case nameof(AppViewModel.SettingsExpandedProvider):
                ExpandRequestedProvider();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void OnModelsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ModelsViewModel.InstalledText):
                InstalledText.Text = _models.InstalledText;
                break;
            case nameof(ModelsViewModel.DiarizerAvailable):
                Refresh();
                break;
        }
    }

    private void Set(Action change)
    {
        if (!_syncing) change();
    }

    /// <summary>The picked tab's panel.</summary>
    private void ShowTab()
    {
        var tab = Tab;
        GeneralPanel.Visibility = tab == GeneralTab ? Visibility.Visible : Visibility.Collapsed;
        TranscriptionPanel.Visibility = tab == TranscriptionModelsTab ? Visibility.Visible : Visibility.Collapsed;
        DiarizationPanel.Visibility = tab == DiarizationModelsTab ? Visibility.Visible : Visibility.Collapsed;
        ExpandRequestedProvider();
    }

    /// <summary>Every setting control shows the current settings.</summary>
    private void Refresh()
    {
        _syncing = true;
        try
        {
            RefreshLanguages();
            LiveToggle.IsOn = _settings.TranscribeWhileRecording;
            RefreshIdleMinutes();
            DeleteAfterToggle.IsOn = _settings.DeleteAudioAfterTranscription;
            DiarizationToggle.IsOn = _settings.Diarization;
            // macOS shows the number only while speakers are told apart.
            SpeakerCountCard.Visibility = _settings.Diarization ? Visibility.Visible : Visibility.Collapsed;
            var (min, _) = AppSettings.SpeakerCountHintRange;
            SpeakerCountCombo.SelectedIndex = _settings.SpeakerCountHint is { } hint ? hint - min + 1 : 0;
            SpeakerCountCard.Description = !_models.DiarizerAvailable
                ? Loc.T("Needs the speaker model: download it in Transcription › Diarization model.")
                : _model.DefaultCloudDiarizer() is not null
                    ? $"{Loc.T("A hint for telling the system audio's speakers apart.")} {Loc.T("Cloud diarization does not take this number.")}"
                    : Loc.T("A hint for telling the system audio's speakers apart.");
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// The default language menu in LanguageOptions order (Auto, the pinned languages, the recent ones, every other
    /// supported one), flattened: a ComboBox has no separators.
    /// </summary>
    private void RefreshLanguages()
    {
        var selection = _settings.DefaultLanguage;
        List<string> codes = [.. LanguageOptions.Groups(selection, _settings.Settings.RecentLanguages, _model.SupportedLanguages()).SelectMany(g => g)];
        if (!codes.SequenceEqual(_languageCodes))
        {
            _languageCodes = codes;
            LanguageCombo.Items.Clear();
            foreach (var code in codes) LanguageCombo.Items.Add(LanguageOptions.Name(code));
        }
        LanguageCombo.SelectedIndex = _languageCodes.IndexOf(selection);
    }

    /// <summary>The offered minutes, plus a hand-edited value.</summary>
    private void RefreshIdleMinutes()
    {
        var current = _settings.ModelIdleMinutes;
        var options = ModelsViewModel.IdleMinutesOptions(current);
        if (!options.SequenceEqual(_idleMinutes))
        {
            _idleMinutes = options;
            IdleCombo.Items.Clear();
            foreach (var minutes in options) IdleCombo.Items.Add(Loc.Format("%lld min", minutes));
        }
        IdleCombo.SelectedIndex = _idleMinutes.ToList().IndexOf(current);
    }

    // Model rows

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

    /// <summary>A header per tier that has <paramref name="kind"/> models, each followed by its rows.</summary>
    private void AddSections(StackPanel list, ModelKind kind)
    {
        foreach (var (tier, entries) in _models.Sections(kind))
        {
            var header = new StackPanel { Style = (Style)Resources["SectionHeaderStyle"] };
            header.Children.Add(new TextBlock
            {
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Text = ModelsViewModel.TierName(tier),
            });
            list.Children.Add(header);
            foreach (var entry in entries) list.Children.Add(CreateRow(entry));
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
            Style = (Style)Resources["DefaultBadgeStyle"],
            Child = new TextBlock { Style = (Style)Resources["DefaultBadgeTextStyle"], Text = Loc.T("Default") },
            Visibility = Visibility.Collapsed,
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(name);
        header.Children.Add(badge);

        var description = new StackPanel { Spacing = 2 };
        description.Children.Add(Note(ModelsViewModel.Details(entry), "NoteStyle"));
        if (ModelsViewModel.QualityNote(entry) is { } quality) description.Children.Add(Note(quality, "NoteStyle"));
        var unsupported = Note("", "CautionNoteStyle");
        unsupported.Visibility = Visibility.Collapsed;
        description.Children.Add(unsupported);
        if (ModelsViewModel.NeedsMuchMemory(entry))
        {
            var warning = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            warning.Children.Add(new FontIcon { Style = (Style)Resources["CautionIconStyle"], Glyph = "" });
            warning.Children.Add(Note("Needs more than half of this PC's memory.", "CautionNoteStyle"));
            description.Children.Add(warning);
        }
        var error = Note("", "CriticalNoteStyle");
        error.Visibility = Visibility.Collapsed;
        description.Children.Add(error);

        var setDefault = ActionButton("Set default", entry, () => _models.SetDefault(entry));
        var progress = new ProgressBar { Width = 90, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(progress, entry.DisplayName);
        var percent = new TextBlock
        {
            Style = (Style)Resources["NoteStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 32,
            TextAlignment = TextAlignment.Right,
        };
        var cancel = ActionButton("Cancel", entry, () => _models.CancelDownload(entry));
        var installed = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        installed.Children.Add(new FontIcon { Style = (Style)Resources["SuccessIconStyle"], Glyph = "" });
        installed.Children.Add(new TextBlock { Style = (Style)Resources["SuccessNoteStyle"], Text = Loc.T("Installed") });
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

    private TextBlock Note(string text, string style) =>
        new() { Style = (Style)Resources[style], Text = text.Length > 0 ? Loc.T(text) : "" };

    /// <summary>Every row shows its model's state (macOS <c>ModelRow.actions</c>).</summary>
    private void UpdateRows()
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

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // Cloud (macOS cloudSection, CloudEngineRow, CloudModelField) and the speech-to-text APIs (speechToTextSection,
    // SpeechProviderRow)

    /// <summary>Below this width a field's card puts its input under its label, as the provider rows of Settings › AI.</summary>
    private const double FieldWrapWidth = 640;

    /// <summary>Approximate pay-as-you-go prices per audio hour (macOS <c>CloudEngineRow.prices</c>, 2026-10-18).</summary>
    private static readonly Dictionary<string, string> Prices = new()
    {
        ["openai"] = "~$0.36/h", ["groq"] = "~$0.04/h", ["mistral"] = "~$0.18/h", ["gemini"] = "~$0.23/h",
        ["elevenlabs"] = "~$0.22/h", ["deepgram"] = "~$0.26/h", ["assemblyai"] = "~$0.23/h",
    };

    /// <summary>Where to get a speech-to-text API's key and look up its models (macOS <c>providerLinks</c>).</summary>
    private static readonly Dictionary<string, (string Keys, string Models)> SpeechLinks = new()
    {
        ["elevenlabs"] = ("https://elevenlabs.io/app/developers/api-keys", "https://elevenlabs.io/docs/overview/models"),
        ["deepgram"] = ("https://console.deepgram.com", "https://developers.deepgram.com/docs/models-languages-overview"),
        ["assemblyai"] = ("https://www.assemblyai.com/dashboard/api-keys", "https://www.assemblyai.com/docs/getting-started/models"),
    };

    private static IEnumerable<string> SpeechToTextNames() =>
        ProviderConfig.Presets().Where(p => p.Kind == ProviderKind.SpeechToText).Select(p => p.DisplayName);

    private bool Closing => _model.IsClosing;

    /// <summary>Both cloud sections and the speech-to-text rows show the current configs, keys and defaults.</summary>
    private void RefreshCloud()
    {
        if (Closing) return;
        _transcriptionCloud.Update();
        _diarizationCloud.Update();
        List<UIElement> rows = [SpeechToTextList.Children[0]];
        foreach (var config in _providers.ProviderConfigs.Where(c => c.Kind == ProviderKind.SpeechToText))
        {
            if (!_speechRows.TryGetValue(config.Id, out var row)) _speechRows[config.Id] = row = new SpeechProviderRow(this, config.Id);
            row.Update(config);
            rows.Add(row.Expander);
        }
        if (!SpeechToTextList.Children.SequenceEqual(rows))
        {
            SpeechToTextList.Children.Clear();
            foreach (var row in rows) SpeechToTextList.Children.Add(row);
        }
    }

    /// <summary>
    /// A cloud row's Set Up on this page (macOS <c>settingsExpandedProvider</c>): the speech-to-text API's row expands and
    /// comes into view once the Transcription model tab shows, and the request is cleared.
    /// </summary>
    private void ExpandRequestedProvider()
    {
        if (Closing || Tab != TranscriptionModelsTab || _model.SettingsExpandedProvider is not { } id
            || !_speechRows.TryGetValue(id, out var row)) return;
        _model.SettingsExpandedProvider = null;
        row.Expander.IsExpanded = true;
        // After the panel's layout: a row of a tab just shown has no position yet.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!Closing) row.Expander.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0 });
        });
    }

    /// <summary>A section title with its description (macOS <c>SectionHeader</c>); <paramref name="tag"/> is its search entry.</summary>
    private StackPanel SectionHeader(string title, string description, string tag)
    {
        var header = new StackPanel { Style = (Style)Resources["SectionHeaderStyle"], Tag = tag };
        header.Children.Add(new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Text = Loc.T(title) });
        header.Children.Add(Note(description, "NoteStyle"));
        return header;
    }

    /// <summary>A field of a row: its label on the left, its input on the right; below <see cref="FieldWrapWidth"/>, under it.</summary>
    private static SettingsCard Field(string label, object content)
    {
        var card = new SettingsCard { Header = Loc.T(label), Content = content };
        card.Resources["SettingsCardWrapThreshold"] = FieldWrapWidth;
        return card;
    }

    private static ProgressRing SmallRing() => new()
    {
        Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// "Cloud" on one tab (macOS <c>cloudSection(diarization:)</c>): the header, the default cloud engine's model (its own
    /// field, as it follows the default) when the default is a configured cloud engine, the "audio leaves this PC" note,
    /// then every provider that transcribes (with <c>diarization</c>: that labels speakers), Set default once configured
    /// or Set Up.
    /// </summary>
    private sealed class CloudSection
    {
        private readonly TranscriptionSettingsPage _page;
        private readonly StackPanel _list;
        private readonly bool _diarization;
        private readonly CloudModelField _modelField;
        private readonly UIElement _header;
        private readonly UIElement _caution;
        private readonly Dictionary<string, CloudEngineRow> _rows = new(StringComparer.OrdinalIgnoreCase);

        public CloudSection(TranscriptionSettingsPage page, StackPanel list, bool diarization)
        {
            _page = page;
            _list = list;
            _diarization = diarization;
            _header = page.SectionHeader(
                "Cloud",
                diarization
                    ? "Only the system channel is uploaded, as one extra transcription billed by the provider; only the speaker labels are kept. Not used when the transcription model already labels speakers. Prices are approximate list prices per audio hour."
                    : "Prices are approximate list prices per audio hour; the mic and system channels are sent and billed separately.",
                diarization ? "Cloud diarization" : "Cloud transcription engines");
            _modelField = new CloudModelField(page, diarization);
            var caution = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(1, 4, 0, 4) };
            caution.Children.Add(new FontIcon { Style = (Style)page.Resources["CautionIconStyle"], Glyph = "\uE753" });
            caution.Children.Add(new TextBlock
            {
                Style = (Style)page.Resources["CautionNoteStyle"],
                // The macOS text, with "this PC" for "this Mac".
                Text = Loc.T(diarization
                    ? "Audio leaves this PC: the cloud diarizer uploads the system channel to the provider."
                    : "Audio leaves this PC: cloud engines upload the recording to the provider."),
            });
            _caution = caution;
        }

        public void Cancel() => _modelField.Cancel();

        public void Update()
        {
            var app = _page._model;
            var configured = (_diarization ? app.CloudDiarizerConfigs : app.CloudEngineConfigs)
                .Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var defaultId = _diarization ? _page._models.DefaultDiarizerModelID : _page._models.DefaultASRModelID;
            var defaultConfig = CloudTranscription.ProviderId(defaultId) is { } providerId && configured.Contains(providerId)
                ? _page._providers.Config(providerId)
                : null;
            _modelField.Update(defaultConfig);

            List<UIElement> children = [_header];
            if (defaultConfig is not null) children.Add(_modelField.Card);
            children.Add(_caution);
            foreach (var config in _page._providers.ProviderConfigs.Where(c =>
                         CloudTranscription.Supports(c)
                         && (!_diarization || CloudTranscription.DiarizationPresets.Contains(c.Preset ?? ""))))
            {
                if (!_rows.TryGetValue(config.Id, out var row)) _rows[config.Id] = row = new CloudEngineRow(_page, _diarization);
                row.Update(config, configured.Contains(config.Id), defaultId);
                children.Add(row.Card);
            }
            if (_list.Children.SequenceEqual(children)) return;
            _list.Children.Clear();
            foreach (var child in children) _list.Children.Add(child);
        }
    }

    /// <summary>
    /// A cloud engine (macOS <c>CloudEngineRow</c>): its name with the model it runs once configured, the Default badge,
    /// Cloud and its approximate price, Gemini's note; Set default once configured (enabled, with a key and a model; a key
    /// alone with <c>diarization</c>), else Set Up, which opens where it is configured.
    /// </summary>
    private sealed class CloudEngineRow
    {
        private readonly TranscriptionSettingsPage _page;
        private readonly bool _diarization;
        private readonly TextBlock _name = new()
        {
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly Border _badge;
        private readonly TextBlock _details;
        private readonly TextBlock _geminiNote;
        private readonly Button _setUp = new();
        private readonly Button _setDefault = new();
        private ProviderConfig? _config;

        public CloudEngineRow(TranscriptionSettingsPage page, bool diarization)
        {
            _page = page;
            _diarization = diarization;
            _badge = new Border
            {
                Style = (Style)page.Resources["DefaultBadgeStyle"],
                Child = new TextBlock { Style = (Style)page.Resources["DefaultBadgeTextStyle"], Text = Loc.T("Default") },
                Visibility = Visibility.Collapsed,
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(_name);
            header.Children.Add(_badge);
            _details = page.Note("", "NoteStyle");
            _geminiNote = page.Note(diarization
                ? "LLM diarization — timestamps approximate, speakers can be mislabeled."
                : "LLM transcription — timestamps approximate.", "NoteStyle");
            var description = new StackPanel { Spacing = 2 };
            description.Children.Add(_details);
            description.Children.Add(_geminiNote);
            _setUp.Content = Loc.T("Set Up…");
            _setUp.Click += (_, _) => SetUp();
            _setDefault.Content = Loc.T("Set default");
            _setDefault.Click += (_, _) =>
            {
                if (_config is { } config) _page._models.SetDefault(CloudTranscription.EngineId(config.Id), _diarization);
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(_setUp);
            actions.Children.Add(_setDefault);
            Card = new SettingsCard { Header = header, Description = description, Content = actions };
        }

        public SettingsCard Card { get; }

        public void Update(ProviderConfig config, bool configured, string defaultId)
        {
            _config = config;
            var engineId = CloudTranscription.EngineId(config.Id);
            var name = configured ? Name(config) : config.DisplayName;
            _name.Text = name;
            ToolTipService.SetToolTip(_name, name);
            var isDefault = configured && ProvidersViewModel.SameId(defaultId, engineId);
            _badge.Visibility = Show(isDefault);
            _setUp.Visibility = Show(!configured);
            _setDefault.Visibility = Show(configured && !isDefault);
            AutomationProperties.SetName(_setUp, $"{Loc.T("Set Up…")}, {config.DisplayName}");
            AutomationProperties.SetName(_setDefault, $"{Loc.T("Set default")}, {config.DisplayName}");
            ToolTipService.SetToolTip(_setUp, config.Kind != ProviderKind.SpeechToText
                ? Loc.Format("Turn on %@ in Settings › AI", config.DisplayName)
                : _diarization
                    ? Loc.Format("Add the key of %@ in Transcription models", config.DisplayName)
                    : Loc.Format("Add the key of %@ below", config.DisplayName));
            List<string> details = [Loc.T("Cloud")];
            if (config.Preset is { } preset && Prices.TryGetValue(preset, out var price))
            {
                details.Add(Loc.Format(preset == "gemini" ? "%@ + output (approx.)" : "%@ (approx.)", price));
            }
            _details.Text = string.Join(" · ", details);
            _geminiNote.Visibility = Show(config.Preset == "gemini");
        }

        /// <summary>"Deepgram (nova-3)" with the model it runs; the provider name alone when it picks the model.</summary>
        private string Name(ProviderConfig config)
        {
            if (!_diarization) return AppViewModel.CloudEngineName(config);
            var model = CloudTranscription.DiarizationModel(config);
            return model.Length == 0 ? config.DisplayName : $"{config.DisplayName} ({model})";
        }

        /// <summary>
        /// Speech-to-text APIs are set up in their row on the Transcription model tab (from Diarization model, that tab is
        /// opened first), the other APIs in Settings › AI › Cloud APIs.
        /// </summary>
        private void SetUp()
        {
            if (_config is not { } config) return;
            var app = _page._model;
            app.SettingsExpandedProvider = config.Id;
            if (config.Kind != ProviderKind.SpeechToText) app.OpenSettings(SettingsSearch.Providers, ProvidersSettingsPage.CloudAPIsTab);
            else if (_diarization) app.OpenSettings(SettingsSearch.Transcription, TranscriptionModelsTab);
            else _page.ExpandRequestedProvider();
        }
    }

    /// <summary>
    /// The default cloud engine's transcription model (macOS <c>CloudModelField</c>): free text, a menu of the known
    /// models and, for the APIs that list their models, Load models (only the ones that transcribe). With
    /// <c>diarization</c>, the default cloud diarizer's diarization model: only models that label speakers are listed,
    /// and a note says when the typed one does not. AssemblyAI picks its model.
    /// </summary>
    private sealed class CloudModelField
    {
        /// <summary>The speech-to-text APIs have no model list; their known models are offered.</summary>
        private static readonly Dictionary<string, string[]> KnownModels = new(OpenAICompatibleTranscriber.PresetModels)
        {
            ["elevenlabs"] = [ElevenLabsTranscriber.DefaultModel],
            ["deepgram"] = [DeepgramTranscriber.DefaultModel],
            ["gemini"] = OpenAICompatibleTranscriber.PresetModels.TryGetValue("gemini", out var gemini) ? gemini : [GeminiAudioTranscriber.DefaultModel],
        };

        private readonly TranscriptionSettingsPage _page;
        private readonly bool _diarization;
        private readonly TextBox _box = new();
        private readonly TextBlock _picked;
        private readonly DropDownButton _modelsButton = new() { Visibility = Visibility.Collapsed };
        private readonly MenuFlyout _modelsMenu = new();
        private readonly Button _loadButton = new();
        private readonly ProgressRing _ring = SmallRing();
        private readonly TextBlock _error;
        private readonly TextBlock _empty;
        private readonly TextBlock _noSpeakers;
        private readonly FieldPanel _input;
        private ProviderConfig? _config;
        private List<string>? _loaded;
        private string? _loadError;
        private List<string> _choices = [];
        private CancellationTokenSource? _loading;

        public CloudModelField(TranscriptionSettingsPage page, bool diarization)
        {
            _page = page;
            _diarization = diarization;
            AutomationProperties.SetName(_box, Loc.T("Model"));
            _box.TextChanged += (_, _) => SetModel(_box.Text);
            _modelsButton.Flyout = _modelsMenu;
            _loadButton.Content = Loc.T("Load models");
            _loadButton.Click += (_, _) => _ = LoadAsync();
            _input = new FieldPanel { InputWidth = 260, Children = { _box, _modelsButton, _loadButton, _ring } };
            _picked = page.Note("", "NoteStyle");
            _error = page.Note("", "CriticalNoteStyle");
            _error.MaxLines = 2;
            _empty = page.Note(diarization
                ? "No diarization models listed; type the model id."
                : "No transcription models listed; type the model id.", "NoteStyle");
            // The macOS text, with "this PC" for "this Mac".
            _noSpeakers = new TextBlock
            {
                Style = (Style)page.Resources["CautionNoteStyle"],
                Text = Loc.T("This model does not label speakers; a model on this PC is used instead."),
            };
            var notes = new StackPanel { Spacing = 2 };
            notes.Children.Add(_error);
            notes.Children.Add(_empty);
            notes.Children.Add(_noSpeakers);
            Card = Field("Model", _input);
            Card.Description = notes;
        }

        public SettingsCard Card { get; }

        public void Cancel() => _loading?.Cancel();

        /// <summary>Shows the default engine's <paramref name="config"/> (null: none); loaded models belong to the provider they came from.</summary>
        public void Update(ProviderConfig? config)
        {
            if (config is null || _config is null || !ProvidersViewModel.SameId(config.Id, _config.Id))
            {
                _loading?.Cancel();
                _loaded = null;
                _loadError = null;
            }
            _config = config;
            if (config is null) return;
            if (config.Preset == "assemblyai")
            {
                _picked.Text = Loc.T(_diarization
                    ? "Picked by AssemblyAI (Universal, speaker labels)"
                    : "Picked by AssemblyAI for the language (Universal)");
                Card.Content = _picked;
                ((StackPanel)Card.Description).Visibility = Visibility.Collapsed;
                return;
            }
            Card.Content = _input;
            ((StackPanel)Card.Description).Visibility = Visibility.Visible;
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
            _loadButton.Visibility = Show(config.Kind == ProviderKind.OpenAICompatible);
            _error.Text = _loadError ?? "";
            _error.Visibility = Show(_loadError is not null);
            _empty.Visibility = Show(_loadError is null && _loaded is { Count: 0 });
            _noSpeakers.Visibility = Show(_diarization && !CloudTranscription.Diarizes(config, CloudTranscription.DiarizationModel(config)));
        }

        private void SetModel(string id)
        {
            if (_page._syncing || _page.Closing || _config is not { } shown || _page._providers.Config(shown.Id) is not { } config) return;
            if ((_diarization ? config.DiarizationModel : config.TranscriptionModel) == id) return;
            _page._providers.UpdateProvider(config.Id, c =>
            {
                if (_diarization) c.DiarizationModel = id;
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
                var all = await _page._providers.ListModelsAsync(config, cancellation.Token);
                _loaded = [.. all.Where(id => Transcribes(id, config.Preset) && (!_diarization || CloudTranscription.Diarizes(config, id)))
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
            if (_page.Closing) return;
            _loadButton.IsEnabled = true;
            _ring.IsActive = false;
            _ring.Visibility = Visibility.Collapsed;
            Update(_config);
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
    /// A speech-to-text API (macOS <c>SpeechProviderRow</c>): the Enabled switch; expanded, its API key (Credential
    /// Manager only: saved on Enter, on leaving the field, by Save and by Test, then cleared; only "Saved" shows), its
    /// transcription model (AssemblyAI picks its own), Browse models, and Test: a free request that needs a valid key and
    /// uploads no audio.
    /// </summary>
    private sealed class SpeechProviderRow
    {
        private readonly TranscriptionSettingsPage _page;
        private readonly string _id;
        private readonly ToggleSwitch _enabled = new();
        private readonly PasswordBox _keyBox = new();
        private readonly StackPanel _savedLabel = new() { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
        private readonly Button _removeKeyButton = new() { Visibility = Visibility.Collapsed };
        private readonly HyperlinkButton? _getKeyLink;
        private readonly TextBlock _keyError;
        private readonly SettingsCard _assemblyNote;
        private readonly TranscriptionModelField _modelField;
        private readonly Button _testButton = new();
        private readonly Button _saveButton = new();
        private readonly ProgressRing _ring = SmallRing();
        private readonly TextBlock _ok;
        private readonly TextBlock _testError;
        private CancellationTokenSource? _testing;

        public SpeechProviderRow(TranscriptionSettingsPage page, string id)
        {
            _page = page;
            _id = id;
            Expander = new SettingsExpander { Content = _enabled };
            _enabled.Toggled += (_, _) =>
            {
                if (_page._syncing || _page.Closing || _page._providers.Config(_id) is not { } config || config.Enabled == _enabled.IsOn) return;
                _page._providers.UpdateProvider(_id, c => c.Enabled = _enabled.IsOn);
            };

            AutomationProperties.SetName(_keyBox, Loc.T("API key"));
            _keyBox.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter) return;
                e.Handled = true;
                _ = SaveKeyAsync();
            };
            _keyBox.LostFocus += (_, _) => _ = SaveKeyAsync();
            _keyBox.PasswordChanged += (_, _) => UpdateSave();
            _savedLabel.Children.Add(new FontIcon { Style = (Style)page.Resources["SuccessIconStyle"], Glyph = "\uE73E" });
            _savedLabel.Children.Add(page.Note("Saved", "SuccessNoteStyle"));
            _removeKeyButton.Content = Loc.T("Remove");
            _removeKeyButton.Click += async (_, _) => ShowKeyError(await _page._providers.DeleteApiKeyAsync(_id));
            var links = SpeechLinks.TryGetValue(id, out var found) ? found : ((string Keys, string Models)?)null;
            if (links is { } keyLinks)
            {
                _getKeyLink = new HyperlinkButton { Content = Loc.T("Get an API key"), NavigateUri = new Uri(keyLinks.Keys), Padding = new Thickness(0) };
            }
            var keyPanel = new FieldPanel { InputWidth = 260 };
            keyPanel.Children.Add(_keyBox);
            keyPanel.Children.Add(_savedLabel);
            keyPanel.Children.Add(_removeKeyButton);
            if (_getKeyLink is not null) keyPanel.Children.Add(_getKeyLink);
            _keyError = page.Note("", "CriticalNoteStyle");
            _keyError.Visibility = Visibility.Collapsed;
            var keyCard = Field("API key", keyPanel);
            keyCard.Description = _keyError;

            _assemblyNote = new SettingsCard { Header = page.Note("AssemblyAI picks its model for the language (Universal).", "NoteStyle") };
            _modelField = new TranscriptionModelField(page._providers, id, () => page._syncing || page.Closing);

            _testButton.Content = Loc.T("Test");
            _testButton.Click += (_, _) => _ = TestAsync();
            _saveButton.Content = Loc.T("Save");
            _saveButton.Click += async (_, _) =>
            {
                await SaveKeyAsync();
                UpdateSave();
            };
            _ok = page.Note("OK", "SuccessNoteStyle");
            _ok.Visibility = Visibility.Collapsed;
            _testError = page.Note("", "CriticalNoteStyle");
            _testError.Visibility = Visibility.Collapsed;
            _testError.MaxLines = 3;
            var buttons = new FieldPanel();
            buttons.Children.Add(_testButton);
            buttons.Children.Add(_saveButton);
            buttons.Children.Add(_ring);
            buttons.Children.Add(_ok);

            Expander.Items.Add(keyCard);
            Expander.Items.Add(_assemblyNote);
            Expander.Items.Add(_modelField.Card);
            if (links is { } modelLinks)
            {
                Expander.Items.Add(new SettingsCard
                {
                    Header = new HyperlinkButton { Content = Loc.T("Browse models"), NavigateUri = new Uri(modelLinks.Models), Padding = new Thickness(0) },
                });
            }
            Expander.Items.Add(new SettingsCard { Header = buttons, Description = _testError });
        }

        public SettingsExpander Expander { get; }

        /// <summary>Shows <paramref name="config"/>; the key typed and the model being typed stay as they are.</summary>
        public void Update(ProviderConfig config)
        {
            Expander.Header = config.DisplayName;
            AutomationProperties.SetName(_enabled, config.DisplayName);
            _enabled.IsOn = config.Enabled;
            var assembly = config.Preset == "assemblyai";
            _assemblyNote.Visibility = Show(assembly);
            _modelField.Card.Visibility = Show(!assembly);
            _modelField.Update(config);
            var saved = _page._providers.HasApiKey(_id);
            _savedLabel.Visibility = _removeKeyButton.Visibility = Show(saved);
            if (_getKeyLink is not null) _getKeyLink.Visibility = Show(!saved);
            _keyBox.PlaceholderText = Loc.T(saved ? "Enter a new key to replace it" : "Paste the key, then Save");
            UpdateSave();
        }

        public void Cancel() => _testing?.Cancel();

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

        /// <summary>Test: a key typed without Enter is saved first and is the one tested (macOS <c>TestButton.run</c>); Cancel while it runs.</summary>
        private async Task TestAsync()
        {
            if (_testing is { } running)
            {
                running.Cancel();
                return;
            }
            _ok.Visibility = _testError.Visibility = Visibility.Collapsed;
            if (!await SaveKeyAsync() || _page.Closing || _page._providers.Config(_id) is not { } config) return;
            using var cancellation = _testing = new CancellationTokenSource();
            SetTesting(true);
            string? error = null;
            var ok = false;
            try
            {
                await _page._providers.TestSpeechKeyAsync(config, cancellation.Token);
                ok = true;
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested)
            {
                error = ProvidersViewModel.ShortMessage(e);
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
    }
}
