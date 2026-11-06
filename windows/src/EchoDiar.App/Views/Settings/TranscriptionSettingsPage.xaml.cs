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

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › Transcription (macOS ModelsSettingsView.swift), one page without inner tabs: General, Transcription model
/// (the default engine's <see cref="DefaultEnginePicker"/> and, for a cloud engine, its "While recording" group) and
/// Speakers (with the default diarizer's picker). Every setting shows <see cref="SettingsViewModel"/> and changes it;
/// the change is saved after its delay. The local models are downloaded in AI Providers › Local AI and the cloud engines
/// set up in AI Providers › Cloud (macOS: the same).
/// </summary>
public sealed partial class TranscriptionSettingsPage : UserControl
{
    /// <summary>
    /// The cloud engines with a realtime stream ("Live with &lt;Provider&gt; realtime", macOS <c>realtimeNames</c>); before
    /// <see cref="SearchEntries"/>, which reads it.
    /// </summary>
    private static readonly string[] RealtimeNames =
        ["ElevenLabs", "Deepgram", "AssemblyAI", "OpenAI", "xAI", "Speechmatics", "Soniox", "Gladia"];

    /// <summary>
    /// The searchable rows (macOS SettingsSearch.swift, Transcription), each the Tag of its element. The model rows are
    /// AI Providers › Local AI's, the cloud engines AI Providers › Cloud's.
    /// </summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Default language", ["language", "English", "French", "Vietnamese"]),
        new("Remember last record language", ["language", "remember", "last", "record", "chevron", "menu"]),
        new("Transcribe while recording", ["live transcript", "captions", "real-time", "subtitles", "Whisper", "Parakeet"]),
        new("Free unused models after", ["memory", "RAM", "idle", "unload", "pause"]),
        new("Delete audio after transcription", ["remove audio", "Recycle Bin", "trash", "save space"]),
        new("Default transcription model",
            ["default engine", "Whisper", "Parakeet", "cloud", "speech to text", .. SpeechToTextNames()]),
        new("While recording", ["live transcript", "cloud live", "realtime", "real-time", "streaming", "captions", .. RealtimeNames]),
        new("Live with a local model", ["live transcript", "cloud live", "while recording", "Whisper", "Parakeet", "live model"]),
        new(RealtimeSearchTitle,
            ["live transcript", "cloud live", "realtime", "real-time", "streaming", "while recording", "WebSocket", .. RealtimeNames]),
        new("Realtime model", ["realtime", "streaming", "live model", "while recording", .. RealtimeNames]),
        new("Use the realtime result as the transcript",
            ["use as final", "realtime", "streaming", "live transcript", "gap fill", "no upload", .. RealtimeNames]),
        new("Tell speakers apart", ["diarization", "speakers", "who spoke"]),
        new("Number of other speakers", ["speaker count", "diarization", "people"]),
        new("Default diarization model", ["diarization", "speakers", "speaker model", "pyannote", "cloud"]),
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
        + "stream: see Transcription model below.";

    private readonly AppViewModel _model;
    private readonly SettingsViewModel _settings;
    private readonly ModelsViewModel _models;
    private readonly ProvidersViewModel _providers;

    /// <summary>The default transcription model and diarizer (macOS <c>DefaultEnginePicker</c>), made once and updated in place.</summary>
    private readonly DefaultEnginePicker _asrPicker;
    private readonly DefaultEnginePicker _diarizationPicker;

    /// <summary>The default cloud engine's "While recording" group, below the transcription model's picker.</summary>
    private readonly CloudLiveOptions _live;

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
        _asrPicker = new DefaultEnginePicker(model, diarization: false, Resources);
        _diarizationPicker = new DefaultEnginePicker(model, diarization: true, Resources);
        _live = new CloudLiveOptions(this);
        DiarizationModelList.Children.Add(_diarizationPicker.Card);
        RememberLanguageCard.Description = Loc.T(
            "Uses the language picked in the Record menu for the next recordings instead of the default language. Languages changed during a recording don't count.");
        LiveCard.Description = Loc.T(LiveDescription);
        SpeakerCountCombo.Items.Add(Loc.T("Auto"));
        var (min, max) = AppSettings.SpeakerCountHintRange;
        for (var count = min; count <= max; count++) SpeakerCountCombo.Items.Add(count.ToString(CultureInfo.CurrentCulture));

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
            _models.StatesChanged += UpdatePickers;
            _providers.Changed += RefreshCloud;
            Refresh();
            UpdatePickers();
            RefreshCloud();
            // The pickers offer the installed models (macOS refreshInstalledModels).
            _ = _models.LoadAsync();
            _ = _providers.RefreshKeysAsync();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _settings.PropertyChanged -= OnSettingsChanged;
            _models.PropertyChanged -= OnModelsChanged;
            _models.StatesChanged -= UpdatePickers;
            _providers.Changed -= RefreshCloud;
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The default cloud engine's "While recording" group follows it (settings and models: _providers.Changed).
        if (e.PropertyName == nameof(AppViewModel.CloudEngineConfigs)) RefreshCloud();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void OnModelsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelsViewModel.DiarizerAvailable)) Refresh();
    }

    private void Set(Action change)
    {
        if (!_syncing) change();
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
                ? Loc.T("Needs the speaker model: download it in Settings › AI Providers › Local AI.")
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

    private TextBlock Note(string text, string style) =>
        new() { Style = (Style)Resources[style], Text = text.Length > 0 ? Loc.T(text) : "" };

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Approximate pay-as-you-go prices per audio hour (macOS <c>CloudPriceBadge.prices</c>, 2026-10-18; xAI, Speechmatics
    /// Enhanced, Soniox and Gladia solaria-1 2026-11-04), shown on AI Providers › Cloud's cards.
    /// </summary>
    internal static readonly Dictionary<string, string> Prices = new()
    {
        ["openai"] = "~$0.36/h", ["groq"] = "~$0.04/h", ["mistral"] = "~$0.18/h", ["gemini"] = "~$0.23/h",
        ["elevenlabs"] = "~$0.22/h", ["deepgram"] = "~$0.26/h", ["assemblyai"] = "~$0.23/h",
        ["xai"] = "~$0.10/h", ["speechmatics"] = "~$0.38/h", ["soniox"] = "~$0.10/h", ["gladia"] = "~€0.27/h",
    };

    /// <summary>The speech-to-text APIs' names (macOS <c>speechToTextNames</c>), also AI Settings › Transcription's keywords.</summary>
    internal static IEnumerable<string> SpeechToTextNames() =>
        ProviderConfig.Presets().Where(p => p.Kind == ProviderKind.SpeechToText).Select(p => p.DisplayName);

    private bool Closing => _model.IsClosing;

    /// <summary>A cloud engine's <see cref="CloudModelField"/>, with this page's view model, sync and closing state and styles.</summary>
    private CloudModelField NewCloudModelField(bool diarization, bool realtime = false) =>
        new(_providers, () => _syncing, () => Closing, Resources, diarization, realtime);

    /// <summary>Both default engine pickers show the current choices and defaults.</summary>
    private void UpdatePickers()
    {
        _asrPicker.Update();
        _diarizationPicker.Update();
    }

    /// <summary>
    /// "Transcription model" (macOS <c>Section("Transcription model")</c>): the default engine's picker, then the default
    /// cloud engine's "While recording" group when the default is an available cloud engine.
    /// </summary>
    private void RefreshCloud()
    {
        if (Closing) return;
        List<UIElement> children = [_asrPicker.Card];
        if (_model.DefaultCloudConfig is { } config)
        {
            _live.Update(config);
            children.AddRange(_live.Elements);
        }
        if (TranscriptionModelList.Children.SequenceEqual(children)) return;
        TranscriptionModelList.Children.Clear();
        foreach (var child in children) TranscriptionModelList.Children.Add(child);
    }

    /// <summary>
    /// "While recording" below the default engine picker when the default is a cloud engine (macOS
    /// <c>CloudLiveOptions</c>): one live source at a time (off, a local model, or the provider's realtime stream;
    /// <see cref="AppViewModel.SetCloudLiveSource"/>), the realtime model and host, and "Use the realtime result as the
    /// transcript", each with what it costs. Greyed with the reason while "Transcribe automatically" or "Transcribe while
    /// recording" is off.
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
                Text = Loc.T("No live model installed. Download a Whisper or Parakeet model in AI Providers › Local AI."),
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
            _realtimeModel = page.NewCloudModelField(diarization: false, realtime: true);

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

        /// <summary>The group's elements, in order, placed right below the default engine picker; the same instances each time.</summary>
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
}
