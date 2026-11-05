using System.ComponentModel;
using System.Diagnostics;
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

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › Transcription (macOS ModelsSettingsView.swift), the General, Transcription model and Diarization model
/// inner tabs; the title bar picks the tab (<see cref="AppViewModel.SubTabOf"/>). Every setting shows
/// <see cref="SettingsViewModel"/> and changes it; the change is saved after its delay. The model rows show
/// <see cref="ModelsViewModel"/>'s state, kept up to date in place (a download's progress changes them often). The cloud
/// rows (macOS <c>cloudSection</c>) show <see cref="ProvidersViewModel"/>'s configs with their model and key status, and
/// <see cref="AppViewModel.CloudEngineConfigs"/>, made once per provider and updated in place; keys are saved in
/// Settings › API Keys, which a row's "API key" button opens.
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

    /// <summary>
    /// The cloud engines with a realtime stream ("Live with &lt;Provider&gt; realtime", macOS <c>realtimeNames</c>); before
    /// <see cref="SearchEntries"/>, which reads it.
    /// </summary>
    private static readonly string[] RealtimeNames =
        ["ElevenLabs", "Deepgram", "AssemblyAI", "OpenAI", "xAI", "Speechmatics", "Soniox", "Gladia"];

    /// <summary>The searchable rows (macOS SettingsSearch.swift, Transcription), each the Tag of its element.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Default language", ["language", "English", "French", "Vietnamese"], GeneralTab),
        new("Remember last record language", ["language", "remember", "last", "record", "chevron", "menu"], GeneralTab),
        new("Transcribe while recording", ["live transcript", "captions", "real-time", "subtitles", "Whisper", "Parakeet"], GeneralTab),
        new("Free unused models after", ["memory", "RAM", "idle", "unload", "pause"], GeneralTab),
        new("Delete audio after transcription", ["remove audio", "Recycle Bin", "trash", "save space"], GeneralTab),
        new("Transcription models", ["download", "Whisper", "Parakeet", "fast", "balanced", "quality"], TranscriptionModelsTab),
        new("Cloud transcription engines", ["upload", "API", "default engine", "xAI", .. SpeechToTextNames()], TranscriptionModelsTab),
        new("While recording",
            ["live transcript", "cloud live", "realtime", "real-time", "streaming", "captions", .. RealtimeNames],
            TranscriptionModelsTab),
        new("Live with a local model",
            ["live transcript", "cloud live", "while recording", "Whisper", "Parakeet", "live model"], TranscriptionModelsTab),
        new(RealtimeSearchTitle,
            ["live transcript", "cloud live", "realtime", "real-time", "streaming", "while recording", "WebSocket", .. RealtimeNames],
            TranscriptionModelsTab),
        new("Realtime model", ["realtime", "streaming", "live model", "while recording", .. RealtimeNames], TranscriptionModelsTab),
        new("Use the realtime result as the transcript",
            ["use as final", "realtime", "streaming", "live transcript", "gap fill", "no upload", .. RealtimeNames],
            TranscriptionModelsTab),
        new("Diarization models", ["speaker model", "speakers", "pyannote", "download"], DiarizationModelsTab),
        new("Cloud diarization",
            ["OpenAI", "ElevenLabs", "Deepgram", "AssemblyAI", "Gemini", "xAI", "Speechmatics", "Soniox", "Gladia", "speakers", "cloud"],
            DiarizationModelsTab),
        new("Tell speakers apart", ["diarization", "speakers", "who spoke"], DiarizationModelsTab),
        new("Number of other speakers", ["speaker count", "diarization", "people"], DiarizationModelsTab),
    ];

    /// <summary>
    /// "Live with … realtime", the realtime row's search title and Tag (macOS <c>"Live with \("…") realtime"</c>): already
    /// localized, so <c>Loc.T</c> leaves it as it is on both sides.
    /// </summary>
    private static string RealtimeSearchTitle => Loc.Format("Live with %@ realtime", "…");

    /// <summary>
    /// macOS's text with Windows' engines (a Windows-only key: the macOS text names WhisperKit and Apple Speech).
    /// </summary>
    private const string LiveDescription =
        "Shows the transcript during the recording. Whisper and Parakeet models keep it, so it is ready within seconds of "
        + "Stop. Uses more CPU and memory while recording. A cloud engine can show it with a local model or its realtime "
        + "stream: see Transcription model › Cloud.";

    private readonly AppViewModel _model;
    private readonly SettingsViewModel _settings;
    private readonly ModelsViewModel _models;
    private readonly ProvidersViewModel _providers;
    private readonly CloudSection _transcriptionCloud;
    private readonly CloudSection _diarizationCloud;

    /// <summary>One row per listed model, made once and updated in place.</summary>
    private readonly List<ModelRow> _rows = [];

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    /// <summary>The language menu's codes, in item order; rebuilt when the choices change.</summary>
    private List<string> _languageCodes = [];

    /// <summary>The idle menu's minutes, in item order.</summary>
    private IReadOnlyList<int> _idleMinutes = [];

    /// <summary>Shows the realtime consent in the toolbar's dialog queue; false when another dialog is open.</summary>
    internal Func<ContentDialog, Task<bool>>? ShowDialog { get; set; }

    public TranscriptionSettingsPage(AppViewModel model)
    {
        _model = model;
        _settings = model.SettingsModel;
        _models = model.ModelsModel;
        _providers = model.ProvidersModel;
        InitializeComponent();
        _transcriptionCloud = new CloudSection(this, TranscriptionCloudList, diarization: false);
        _diarizationCloud = new CloudSection(this, DiarizationCloudList, diarization: true);
        RememberLanguageCard.Description = Loc.T(
            "Uses the language picked in the Record menu for the next recordings instead of the default language. Languages changed during a recording don't count.");
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
        RememberLanguageToggle.Toggled += (_, _) =>
            Set(() => _settings.Update(s => s with { RememberRecordingLanguage = RememberLanguageToggle.IsOn }));
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
            // macOS: a model list stops when its row goes away.
            _transcriptionCloud.Cancel();
            _diarizationCloud.Cancel();
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
            // The default cloud engine's "While recording" group follows it (settings and models: _providers.Changed).
            case nameof(AppViewModel.CloudEngineConfigs):
                RefreshCloud();
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
    }

    /// <summary>Every setting control shows the current settings.</summary>
    private void Refresh()
    {
        _syncing = true;
        try
        {
            RefreshLanguages();
            RememberLanguageToggle.IsOn = _settings.Settings.RememberRecordingLanguage;
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

    // Cloud (macOS cloudSection, CloudEngineRow, CloudModelField)

    /// <summary>Below this width a field's card puts its input under its label, as the provider rows of Settings › AI.</summary>
    private const double FieldWrapWidth = 640;

    /// <summary>
    /// Approximate pay-as-you-go prices per audio hour (macOS <c>CloudEngineRow.prices</c>, 2026-10-18; xAI, Speechmatics
    /// Enhanced, Soniox and Gladia solaria-1 2026-11-04).
    /// </summary>
    private static readonly Dictionary<string, string> Prices = new()
    {
        ["openai"] = "~$0.36/h", ["groq"] = "~$0.04/h", ["mistral"] = "~$0.18/h", ["gemini"] = "~$0.23/h",
        ["elevenlabs"] = "~$0.22/h", ["deepgram"] = "~$0.26/h", ["assemblyai"] = "~$0.23/h",
        ["xai"] = "~$0.10/h", ["speechmatics"] = "~$0.38/h", ["soniox"] = "~$0.10/h", ["gladia"] = "~€0.27/h",
    };

    private static IEnumerable<string> SpeechToTextNames() =>
        ProviderConfig.Presets().Where(p => p.Kind == ProviderKind.SpeechToText).Select(p => p.DisplayName);

    private bool Closing => _model.IsClosing;

    /// <summary>Both cloud sections show the current configs, keys and defaults.</summary>
    private void RefreshCloud()
    {
        if (Closing) return;
        _transcriptionCloud.Update();
        _diarizationCloud.Update();
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
    /// "Cloud" on one tab (macOS <c>cloudSection(diarization:)</c>): the header, the "audio leaves this PC" note, then
    /// every provider that transcribes (with <c>diarization</c>: that labels speakers), each with its model, its key
    /// status and Set default once configured; without <c>diarization</c>, the default engine's "While recording" group
    /// right below its row.
    /// </summary>
    private sealed class CloudSection
    {
        private readonly TranscriptionSettingsPage _page;
        private readonly StackPanel _list;
        private readonly bool _diarization;
        private readonly UIElement _header;
        private readonly UIElement _caution;
        private readonly Dictionary<string, CloudEngineRow> _rows = new(StringComparer.OrdinalIgnoreCase);
        private readonly CloudLiveOptions? _live;

        public CloudSection(TranscriptionSettingsPage page, StackPanel list, bool diarization)
        {
            _page = page;
            _list = list;
            _diarization = diarization;
            if (!diarization) _live = new CloudLiveOptions(page);
            _header = page.SectionHeader(
                "Cloud",
                diarization
                    ? "Only the system channel is uploaded, as one extra transcription billed by the provider; only the speaker labels are kept. Not used when the transcription model already labels speakers. Prices are approximate list prices per audio hour."
                    : "Prices are approximate list prices per audio hour; the mic and system channels are sent and billed separately.",
                diarization ? "Cloud diarization" : "Cloud transcription engines");
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

        public void Cancel()
        {
            foreach (var row in _rows.Values) row.Cancel();
        }

        public void Update()
        {
            var app = _page._model;
            var configured = (_diarization ? app.CloudDiarizerConfigs : app.CloudEngineConfigs)
                .Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var defaultId = _diarization ? _page._models.DefaultDiarizerModelID : _page._models.DefaultASRModelID;
            var liveConfig = _live is null ? null : app.DefaultCloudConfig;

            List<UIElement> children = [_header, _caution];
            foreach (var config in _page._providers.ProviderConfigs.Where(c =>
                         CloudTranscription.Supports(c)
                         && (!_diarization || CloudTranscription.DiarizationPresets.Contains(c.Preset ?? ""))))
            {
                if (!_rows.TryGetValue(config.Id, out var row)) _rows[config.Id] = row = new CloudEngineRow(_page, _diarization);
                row.Update(config, configured.Contains(config.Id), defaultId);
                children.Add(row.Expander);
                if (_live is not null && liveConfig is not null && ProvidersViewModel.SameId(liveConfig.Id, config.Id))
                {
                    _live.Update(liveConfig);
                    children.AddRange(_live.Elements);
                }
            }
            if (_list.Children.SequenceEqual(children)) return;
            _list.Children.Clear();
            foreach (var child in children) _list.Children.Add(child);
        }
    }

    /// <summary>
    /// A cloud engine (macOS <c>CloudEngineRow</c>): its name with the model it runs once configured, the Default badge,
    /// Cloud and its approximate price, Gemini's note, "Base URL not set" for a custom endpoint without one; Set default
    /// once configured (available, with a model; available alone with <c>diarization</c>) and its key status ("API key"
    /// opens its card in Settings › API Keys); expanded, its model.
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
        private readonly TextBlock _noBaseURL;
        private readonly Button _setDefault = new();
        private readonly ApiKeyStatus _status;
        private readonly CloudModelField _modelField;
        private ProviderConfig? _config;
        private bool _warned;

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
            // A custom endpoint transcribes once its base URL (set in API Keys) and its model are set.
            _noBaseURL = page.Note("Base URL not set", "NoteStyle");
            var description = new StackPanel { Spacing = 2 };
            description.Children.Add(_details);
            description.Children.Add(_geminiNote);
            description.Children.Add(_noBaseURL);
            _setDefault.Content = Loc.T("Set default");
            _setDefault.Click += (_, _) =>
            {
                if (_config is { } config) _page._models.SetDefault(CloudTranscription.EngineId(config.Id), _diarization);
            };
            _status = new ApiKeyStatus(page._model, (Style)page.Resources["SuccessIconStyle"]);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(_setDefault);
            actions.Children.Add(_status.Element);
            _modelField = new CloudModelField(page, diarization);
            Expander = new SettingsExpander { Header = header, Description = description, Content = actions };
            Expander.Items.Add(_modelField.Card);
        }

        public SettingsExpander Expander { get; }

        public void Cancel() => _modelField.Cancel();

        public void Update(ProviderConfig config, bool configured, string defaultId)
        {
            _config = config;
            var engineId = CloudTranscription.EngineId(config.Id);
            var name = configured ? Name(config) : config.DisplayName;
            _name.Text = name;
            ToolTipService.SetToolTip(_name, name);
            var isDefault = configured && ProvidersViewModel.SameId(defaultId, engineId);
            _badge.Visibility = Show(isDefault);
            _setDefault.Visibility = Show(configured && !isDefault);
            AutomationProperties.SetName(_setDefault, $"{Loc.T("Set default")}, {config.DisplayName}");
            _status.Update(config);
            List<string> details = [Loc.T("Cloud")];
            if (config.Preset is { } preset && Prices.TryGetValue(preset, out var price))
            {
                details.Add(Loc.Format(preset == "gemini" ? "%@ + output (approx.)" : "%@ (approx.)", price));
            }
            _details.Text = string.Join(" · ", details);
            _geminiNote.Visibility = Show(config.Preset == "gemini");
            _noBaseURL.Visibility = Show(config.Preset is null && config.BaseURL.Trim().Length == 0);
            _modelField.Update(config);
            // Its model's warning shows without a click: the row opens when one appears (the user can close it again).
            if (_modelField.Warns && !_warned) Expander.IsExpanded = true;
            _warned = _modelField.Warns;
        }

        /// <summary>"Deepgram (nova-3)" with the model it runs; the provider name alone when it picks the model.</summary>
        private string Name(ProviderConfig config)
        {
            if (!_diarization) return AppViewModel.CloudEngineName(config);
            var model = CloudTranscription.DiarizationModel(config);
            return model.Length == 0 ? config.DisplayName : $"{config.DisplayName} ({model})";
        }
    }

    /// <summary>
    /// "While recording" below the default cloud engine's row (macOS <c>CloudLiveOptions</c>): one live source at a time
    /// (off, a local model, or the provider's realtime stream; <see cref="AppViewModel.SetCloudLiveSource"/>), the
    /// realtime model and host, and "Use the realtime result as the transcript", each with what it costs. Greyed with the
    /// reason while "Transcribe automatically" or "Transcribe while recording" is off.
    /// </summary>
    private sealed class CloudLiveOptions
    {
        /// <summary>Billing basis of each realtime API (<c>stt-protocols.md</c>); not stated for xAI, Speechmatics and Gladia.</summary>
        private enum Billing { Connection, Audio }

        private static readonly Dictionary<string, Billing> BillingBasis = new()
        {
            ["assemblyai"] = Billing.Connection, ["soniox"] = Billing.Connection,
            ["elevenlabs"] = Billing.Audio, ["deepgram"] = Billing.Audio, ["openai"] = Billing.Audio,
        };

        /// <summary>
        /// Approximate realtime list prices per hour of one connection, by "&lt;preset&gt;/&lt;model&gt;" (macOS
        /// <c>CloudLiveOptions.prices</c>, docs/research/asr-diarization-models.md F.1, 2026-11-04).
        /// </summary>
        private static readonly Dictionary<string, string> RealtimePrices = new()
        {
            ["elevenlabs/scribe_v2_realtime"] = "$0.39", ["deepgram/nova-3"] = "$0.46",
            ["assemblyai/universal-3-6-pro"] = "$0.45", ["assemblyai/universal-streaming-multilingual"] = "$0.15",
            ["assemblyai/universal-streaming-english"] = "$0.15", ["openai/gpt-live-transcribe"] = "$1.02",
            ["openai/gpt-transcribe"] = "$0.27", ["openai/gpt-realtime-whisper"] = "$1.02", ["xai/grok-voice-transcribe-2.0"] = "$0.20",
            ["speechmatics/enhanced"] = "$0.40", ["speechmatics/standard"] = "$0.23", ["speechmatics/melia-1"] = "$0.20",
            ["speechmatics/oak-1"] = "$0.23", ["soniox/stt-rt-v6"] = "$0.12", ["gladia/solaria-1"] = "€0.68",
        };

        /// <summary>With streaming speaker labels ("Tell speakers apart"): Deepgram and AssemblyAI add $0.12/h.</summary>
        private static readonly Dictionary<string, string> SpeakerPrices = new()
        {
            ["deepgram/nova-3"] = "$0.58", ["assemblyai/universal-3-6-pro"] = "$0.57",
            ["assemblyai/universal-streaming-multilingual"] = "$0.27", ["assemblyai/universal-streaming-english"] = "$0.27",
        };

        private readonly TranscriptionSettingsPage _page;
        private readonly TextBlock _reason;
        private readonly TextBlock _localCopy;
        private readonly TextBlock _noLiveModel;
        private readonly ToggleSwitch _localToggle = new();
        private readonly SettingsCard _localCard;
        private readonly ComboBox _liveModelCombo = new() { MinWidth = 200 };
        private readonly SettingsCard _liveModelCard;
        private readonly ToggleSwitch _realtimeToggle = new();
        private readonly SettingsCard _realtimeCard;
        private readonly CloudModelField _realtimeModel;
        private readonly ToggleSwitch _finalToggle = new();
        private readonly SettingsCard _finalCard;
        private ProviderConfig? _config;

        /// <summary>The live model menu's catalog ids, in item order after "Automatic".</summary>
        private List<string> _choiceIds = [];

        /// <summary>The realtime consent dialog is open: the switch stays on until its answer.</summary>
        private bool _asking;

        public CloudLiveOptions(TranscriptionSettingsPage page)
        {
            _page = page;
            var header = new StackPanel { Margin = new Thickness(1, 12, 0, 4), Spacing = 2, Tag = "While recording" };
            header.Children.Add(new TextBlock
            {
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Text = Loc.T("While recording"),
            });
            _reason = page.Note("", "NoteStyle");
            header.Children.Add(_reason);

            _localCopy = page.Note("", "NoteStyle");
            // The macOS hint names WhisperKit; Windows has Whisper (a Windows-only key).
            _noLiveModel = new TextBlock
            {
                Style = (Style)page.Resources["CautionNoteStyle"],
                Text = Loc.T("No live model installed. Download a Whisper or Parakeet model above."),
            };
            var localDescription = new StackPanel { Spacing = 2 };
            localDescription.Children.Add(_localCopy);
            localDescription.Children.Add(_noLiveModel);
            AutomationProperties.SetName(_localToggle, Loc.T("Live with a local model"));
            _localCard = new SettingsCard
            {
                Header = Loc.T("Live with a local model"),
                Description = localDescription,
                Content = _localToggle,
                Tag = "Live with a local model",
            };
            _localToggle.Toggled += (_, _) =>
                page.Set(() => page._model.SetCloudLiveSource(_localToggle.IsOn ? CloudLiveSource.Local : CloudLiveSource.Off));

            _liveModelCombo.Items.Add(Loc.T("Automatic"));
            AutomationProperties.SetName(_liveModelCombo, Loc.T("Live model"));
            _liveModelCard = new SettingsCard { Header = Loc.T("Live model"), Content = _liveModelCombo };
            _liveModelCombo.SelectionChanged += (_, _) =>
            {
                // Item 0 is Automatic (no model picked), item n the n-th installed live model.
                var index = _liveModelCombo.SelectedIndex;
                if (page._syncing || index < 0 || index > _choiceIds.Count) return;
                var id = index == 0 ? null : _choiceIds[index - 1];
                if (page._model.Settings.CloudLiveModelID != id) page._settings.Update(s => s with { CloudLiveModelID = id });
            };

            _realtimeCard = new SettingsCard { Content = _realtimeToggle, Tag = RealtimeSearchTitle };
            _realtimeToggle.Toggled += (_, _) => page.Set(() => SetRealtime(_realtimeToggle.IsOn));
            _realtimeModel = new CloudModelField(page, diarization: false, realtime: true);

            AutomationProperties.SetName(_finalToggle, Loc.T("Use the realtime result as the transcript"));
            _finalCard = new SettingsCard
            {
                Header = Loc.T("Use the realtime result as the transcript"),
                Content = _finalToggle,
                Tag = "Use the realtime result as the transcript",
            };
            _finalToggle.Toggled += (_, _) => page.Set(() => page._model.SetRealtimeAsFinal(_finalToggle.IsOn));

            Elements = [header, _localCard, _liveModelCard, _realtimeCard, _realtimeModel.Card, _finalCard];
        }

        /// <summary>The group's elements, in order, placed right below the default engine's row; the same instances each time.</summary>
        public IReadOnlyList<UIElement> Elements { get; }

        /// <summary>Shows <paramref name="config"/>'s options from the current settings; the switches' events are not the user's.</summary>
        public void Update(ProviderConfig config)
        {
            _config = config;
            var settings = _page._model.Settings;
            var source = settings.CloudLiveSource;
            var name = config.DisplayName;
            var enabled = settings.AutoTranscribe && settings.TranscribeWhileRecording;
            var syncing = _page._syncing;
            _page._syncing = true;
            try
            {
                _reason.Text = enabled ? "" : RecordingView.NoCaptionsReason(settings);
                _reason.Visibility = Show(!enabled);

                // The macOS text, with "this PC" for "this Mac" (a Windows-only key).
                _localCopy.Text = Loc.Format(
                    "Shows the transcript while recording with a model on this PC. Free: nothing is uploaded while recording, but it uses CPU, memory and battery. The saved transcript still comes from %@ after Stop, so it can differ from what you saw.",
                    name);
                _localToggle.IsOn = source == CloudLiveSource.Local;
                _localCard.IsEnabled = enabled;
                var choices = _page._model.CloudLiveModelChoices;
                _noLiveModel.Visibility = Show(choices.Count == 0);
                ShowChoices(choices, settings.CloudLiveModelID);
                _liveModelCard.Visibility = Show(choices.Count > 0 && source == CloudLiveSource.Local);
                _liveModelCard.IsEnabled = enabled;

                var realtime = AppViewModel.RealtimeAvailable(config);
                var title = Loc.Format("Live with %@ realtime", name);
                _realtimeCard.Header = title;
                AutomationProperties.SetName(_realtimeToggle, title);
                _realtimeCard.Description = RealtimeCopy(config, settings);
                _realtimeToggle.IsOn = source == CloudLiveSource.Realtime || _asking;
                _realtimeCard.IsEnabled = enabled;
                _realtimeCard.Visibility = _finalCard.Visibility = Show(realtime);
                _realtimeModel.Card.Visibility = Show(realtime && source == CloudLiveSource.Realtime);
                _realtimeModel.Card.IsEnabled = enabled;
                if (realtime) _realtimeModel.Update(config);
                _finalCard.Description = FinalCopy(config, settings);
                _finalToggle.IsOn = settings.RealtimeAsFinal;
                _finalCard.IsEnabled = enabled && source == CloudLiveSource.Realtime;
            }
            finally
            {
                _page._syncing = syncing;
            }
        }

        /// <summary>"Automatic", then the installed live models; rebuilt only when they change.</summary>
        private void ShowChoices(IReadOnlyList<ModelEntry> choices, string? selected)
        {
            List<string> ids = [.. choices.Select(e => e.Id)];
            if (!ids.SequenceEqual(_choiceIds))
            {
                _choiceIds = ids;
                _liveModelCombo.Items.Clear();
                _liveModelCombo.Items.Add(Loc.T("Automatic"));
                foreach (var entry in choices) _liveModelCombo.Items.Add(entry.DisplayName);
            }
            var index = selected is null ? -1 : _choiceIds.IndexOf(selected);
            _liveModelCombo.SelectedIndex = index + 1;
        }

        /// <summary>
        /// Turning realtime on asks the destination's consent first when it is not confirmed; the source stays as it is
        /// until Send (macOS <c>setRealtime</c>).
        /// </summary>
        private void SetRealtime(bool on)
        {
            if (_page.Closing || _config is not { } config) return;
            if (on && !_page._model.RealtimeDestinationConfirmed(config))
            {
                _ = ConfirmRealtimeAsync(config);
                return;
            }
            _page._model.SetCloudLiveSource(on ? CloudLiveSource.Realtime : CloudLiveSource.Off);
        }

        /// <summary>
        /// "Send audio to …?" for the realtime destination, in the toolbar's dialog queue: Send remembers it and switches
        /// to realtime, Cancel (or another dialog open) changes nothing. Not confirmed either when the config's realtime
        /// consent key changed meanwhile (the dialog named the old destination) or it is no longer the default engine.
        /// </summary>
        private async Task ConfirmRealtimeAsync(ProviderConfig config)
        {
            if (_asking || _page.XamlRoot is not { } root || _page.ShowDialog is not { } show)
            {
                Update(config);
                return;
            }
            var destination = CloudRealtime.Destination(config);
            var key = destination?.ConsentKey;
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                Title = Loc.Format("Send audio to %@?", config.DisplayName),
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    // The macOS text, with "this PC" for "this Mac" (a Windows-only key, as CloudConsent's batch text).
                    Text = Loc.Format(
                        "Audio leaves this PC: while recording, the mic and system channels are streamed live to %@ (%@) for transcription and billed by them. The provider may keep the audio under its own retention policy. You are asked once per provider and destination.",
                        config.DisplayName, destination?.Host ?? ""),
                },
                PrimaryButtonText = Loc.T("Send"),
                CloseButtonText = Loc.T("Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            // The queue throws the result away: Send is read from its click.
            var sent = false;
            dialog.PrimaryButtonClick += (_, _) => sent = true;
            _asking = true;
            bool opened;
            try
            {
                opened = await show(dialog);
            }
            finally
            {
                _asking = false;
            }
            if (_page.Closing) return;
            if (!opened)
            {
                // Another dialog is open: nothing is confirmed.
                Debug.WriteLine("EchoDiar: could not show the realtime consent: another dialog is open");
            }
            else if (sent && key is not null && _page._providers.Config(config.Id) is { } current
                && CloudRealtime.Destination(current)?.ConsentKey == key
                && _page._model.DefaultCloudConfig is { } live && ProvidersViewModel.SameId(live.Id, current.Id))
            {
                _page._model.ConfirmRealtimeDestination(current);
                _page._model.SetCloudLiveSource(CloudLiveSource.Realtime);
            }
            // Cancel changes no setting: the switch goes back by itself.
            if (_config is { } shown) Update(shown);
        }

        private static string RealtimeCopy(ProviderConfig config, AppSettings settings) =>
            Loc.Format(
                "Streams the mic and the system audio to %@ as two live connections for the whole recording. Costs money: %@, on top of the normal transcription after Stop. Long recordings reconnect before the provider's session limit and stream about 30 s twice at each reconnect so speakers keep their labels. A 1-hour meeting costs about 2 hours of streaming plus 2 hours of transcription.",
                config.DisplayName, BillingText(config, settings));

        /// <summary>
        /// "billed by connection time, about $0.45 per hour for each connection", from the streamed model's price (the
        /// default model's when it has none listed); the speaker-label add-on included with "Tell speakers apart".
        /// </summary>
        private static string BillingText(ProviderConfig config, AppSettings settings)
        {
            var preset = config.Preset ?? "";
            var key = $"{preset}/{AppViewModel.RealtimeModel(config)}";
            var defaultKey = $"{preset}/{AppViewModel.RealtimeDefaultModel(config)}";
            var speakers = settings.Diarization;
            var price = (speakers ? SpeakerPrices.GetValueOrDefault(key) : null) ?? RealtimePrices.GetValueOrDefault(key)
                ?? (speakers ? SpeakerPrices.GetValueOrDefault(defaultKey) : null) ?? RealtimePrices.GetValueOrDefault(defaultKey);
            if (price is null) return Loc.Format("billed by %@", config.DisplayName);
            if (!BillingBasis.TryGetValue(preset, out var basis)) return Loc.Format("about %@ per hour of streaming for each connection", price);
            return basis == Billing.Connection
                ? Loc.Format("billed by connection time, about %@ per hour for each connection", price)
                : Loc.Format("billed per streamed minute, about %@ per hour for each connection", price);
        }

        private static string FinalCopy(ProviderConfig config, AppSettings settings)
        {
            var copy = Loc.Format(
                "Saves the streamed transcript instead of uploading the recording again after Stop, so you pay for streaming only. Parts missed while the connection was down are sent to %@ after Stop, with about 30 s of audio on each side, and around each pause, so speakers keep their labels. A cloud diarizer is billed separately. Realtime models can be less accurate than %@.",
                config.DisplayName, AppViewModel.CloudEngineName(config));
            // The batch model labelled the speakers itself; the realtime one does not, so the diarizer comes back. The
            // adapter is only built to read its flag, with no key.
            IRealtimeAdapter? adapter = CloudRealtime.Make(config, "", null, diarize: true);
            var streamsSpeakers = adapter?.StreamsSpeakers ?? false;
            if (!settings.Diarization || streamsSpeakers || !CloudTranscription.Diarizes(config, CloudTranscription.Model(config))) return copy;
            return $"{copy} {Loc.T("This realtime model labels no speakers, so the diarizer runs on the recording after Stop; a cloud diarizer asks for its own consent.")}";
        }
    }

    /// <summary>
    /// A cloud engine's transcription model (macOS <c>CloudModelField</c>): free text, a menu of the known models and, for
    /// the APIs that list their models, Load models (only the ones that transcribe). With <c>diarization</c>, the cloud
    /// diarizer's diarization model: only models that label speakers are listed, and a note says when the typed one does
    /// not. AssemblyAI picks its model. With <c>realtime</c>, the realtime model streamed while recording ("Realtime
    /// model"): its known models only, and the host it streams to below.
    /// </summary>
    private sealed class CloudModelField
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

        private readonly TranscriptionSettingsPage _page;
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
        private ProviderConfig? _config;
        private List<string>? _loaded;
        private string? _loadError;
        private List<string> _choices = [];
        private CancellationTokenSource? _loading;

        public CloudModelField(TranscriptionSettingsPage page, bool diarization, bool realtime = false)
        {
            _page = page;
            _diarization = diarization;
            _realtime = realtime;
            AutomationProperties.SetName(_box, realtime ? Loc.T("Realtime model") : Loc.T("Model"));
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
            _streamingOnly = page.Note(
                "This model only streams live audio and can't transcribe a recording. Pick another model.", "CriticalNoteStyle");
            var notes = new StackPanel { Spacing = 2 };
            notes.Children.Add(_error);
            notes.Children.Add(_streamingOnly);
            notes.Children.Add(_empty);
            notes.Children.Add(_noSpeakers);
            Card = Field(realtime ? "Realtime model" : "Model", _input);
            Card.Description = notes;
            if (realtime)
            {
                // Its search entry; the host in place of the notes, which are about the transcription model.
                Card.Tag = "Realtime model";
                _host = page.Note("", "NoteStyle");
                Card.Description = _host;
            }
        }

        public SettingsCard Card { get; }

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
                Card.Content = _picked;
                ((StackPanel)Card.Description).Visibility = Visibility.Collapsed;
                Warns = false;
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
            if (_page._syncing || _page.Closing || _config is not { } shown || _page._providers.Config(shown.Id) is not { } config) return;
            if ((_realtime ? config.RealtimeModel : _diarization ? config.DiarizationModel : config.TranscriptionModel) == id) return;
            _page._providers.UpdateProvider(config.Id, c =>
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
                var all = await _page._providers.ListModelsAsync(config, cancellation.Token);
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
            if (_page.Closing) return;
            _loadButton.IsEnabled = true;
            _ring.IsActive = false;
            _ring.Visibility = Visibility.Collapsed;
            if (_page._providers.Config(config.Id) is { } current) Update(current);
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
}
