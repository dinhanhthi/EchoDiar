using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › Transcription (macOS ModelsSettingsView.swift), the General, Transcription model and Diarization model
/// inner tabs; the title bar picks the tab (<see cref="AppViewModel.SubTabOf"/>). Every setting shows
/// <see cref="SettingsViewModel"/> and changes it; the change is saved after its delay. The model rows show
/// <see cref="ModelsViewModel"/>'s state, kept up to date in place (a download's progress changes them often).
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
    /// The searchable rows (macOS SettingsSearch.swift, Transcription), each the Tag of its element. The cloud engines,
    /// the speech-to-text APIs and cloud diarization come with task 26.3.
    /// </summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Default language", ["language", "English", "French", "Vietnamese"], GeneralTab),
        new("Transcribe while recording", ["live transcript", "captions", "real-time", "subtitles", "Whisper", "Parakeet"], GeneralTab),
        new("Free unused models after", ["memory", "RAM", "idle", "unload", "pause"], GeneralTab),
        new("Delete audio after transcription", ["remove audio", "Recycle Bin", "trash", "save space"], GeneralTab),
        new("Transcription models", ["download", "Whisper", "Parakeet", "fast", "balanced", "quality"], TranscriptionModelsTab),
        new("Diarization models", ["speaker model", "speakers", "pyannote", "download"], DiarizationModelsTab),
        new("Tell speakers apart", ["diarization", "speakers", "who spoke"], DiarizationModelsTab),
        new("Number of other speakers", ["speaker count", "diarization", "people"], DiarizationModelsTab),
    ];

    /// <summary>
    /// macOS's text with Windows' engines. Not through Loc: its slug (cut at 60 characters) is the macOS text's, which
    /// names WhisperKit.
    /// </summary>
    private const string LiveDescription =
        "Shows the transcript during the recording and keeps it, so it is ready within seconds of Stop. Uses more CPU and "
        + "memory while recording. Whisper and Parakeet models only; other engines transcribe after Stop.";

    private readonly AppViewModel _model;
    private readonly SettingsViewModel _settings;
    private readonly ModelsViewModel _models;

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
        InitializeComponent();
        LiveCard.Description = LiveDescription;
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
            Refresh();
            UpdateRows();
            ShowTab();
            InstalledText.Text = _models.InstalledText;
            _ = _models.LoadAsync();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _settings.PropertyChanged -= OnSettingsChanged;
            _models.PropertyChanged -= OnModelsChanged;
            _models.StatesChanged -= UpdateRows;
        };
    }

    private string? Tab => _model.SubTabOf(SettingsSearch.Transcription);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SettingsSubTab)) ShowTab();
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
            LiveToggle.IsOn = _settings.TranscribeWhileRecording;
            RefreshIdleMinutes();
            DeleteAfterToggle.IsOn = _settings.DeleteAudioAfterTranscription;
            DiarizationToggle.IsOn = _settings.Diarization;
            // macOS shows the number only while speakers are told apart.
            SpeakerCountCard.Visibility = _settings.Diarization ? Visibility.Visible : Visibility.Collapsed;
            var (min, _) = AppSettings.SpeakerCountHintRange;
            SpeakerCountCombo.SelectedIndex = _settings.SpeakerCountHint is { } hint ? hint - min + 1 : 0;
            SpeakerCountCard.Description = Loc.T(_models.DiarizerAvailable
                ? "A hint for telling the system audio's speakers apart."
                : "Needs the speaker model: download it in Transcription › Diarization model.");
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
}
