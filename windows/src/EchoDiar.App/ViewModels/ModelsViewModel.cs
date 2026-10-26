using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoDiar.App.Views;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Transcribe;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// What a model's row on Settings › Transcription shows and offers now (macOS ModelsSettingsView.swift
/// <c>ModelRow</c>): the Default badge, Set default (installed, not the default; off when it cannot transcribe the
/// default language), the download's fraction with Cancel, Installed with Delete (off while a transcription or the
/// recording's transcriber runs), or Download (off when it cannot transcribe the default language).
/// </summary>
public sealed record ModelRowState(
    bool IsDefault,
    bool IsInstalled,
    double? Download,
    string? Error,
    string? UnsupportedNote,
    bool ShowSetDefault,
    bool CanSetDefault,
    bool CanDownload,
    bool CanDelete);

/// <summary>
/// The local models of Settings › Transcription (macOS ModelsSettingsView.swift and AppModel's model functions): the
/// catalog's speech and speaker models by tier, their state, Download, Cancel, Delete and Set default, and the size of
/// every installed model. Downloads run in <see cref="AppViewModel.DownloadModel"/> (off the UI thread, progress
/// posted back). The cloud engines' rows read <see cref="AppViewModel.CloudEngineConfigs"/> and set their default with
/// <see cref="SetDefault(string, bool)"/>. Lives as long as the app
/// (<see cref="AppViewModel.ModelsModel"/>); on the UI thread.
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject
{
    /// <summary>This PC's memory, for the "more than half of this PC's memory" warning.</summary>
    private static readonly long PhysicalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    private readonly AppViewModel _app;
    private readonly ModelStore _store;

    /// <summary>Models whose Delete is under way: their Delete is off.</summary>
    private readonly HashSet<string> _deleting = [];

    private long? _installedBytes;
    private bool _measuring;
    private bool _measureAgain;

    internal ModelsViewModel(AppViewModel app, ModelStore store, IReadOnlySet<ModelRuntime> registeredRuntimes)
    {
        _app = app;
        _store = store;
        Entries = [.. ModelCatalog.VisibleEntries(registeredRuntimes).Where(e => e.Kind is ModelKind.Asr or ModelKind.Diarization)];
        app.PropertyChanged += OnAppChanged;
    }

    /// <summary>A row's state may have changed (installs, downloads, errors, jobs, the defaults, the default language).</summary>
    public event Action? StatesChanged;

    /// <summary>The speech and speaker models the page lists, in catalog order (macOS <c>ModelsSettingsView.entries</c>).</summary>
    public IReadOnlyList<ModelEntry> Entries { get; }

    /// <summary>One section per tier (Fast, Balanced, Quality) that has <paramref name="kind"/> entries.</summary>
    public IReadOnlyList<(ModelTier Tier, IReadOnlyList<ModelEntry> Entries)> Sections(ModelKind kind) =>
    [
        .. Enum.GetValues<ModelTier>()
            .Select(tier => (tier, (IReadOnlyList<ModelEntry>)[.. Entries.Where(e => e.Kind == kind && e.Tier == tier)]))
            .Where(section => section.Item2.Count > 0),
    ];

    /// <summary>"Installed: 1.2 GB", every installed model's size; empty until measured.</summary>
    public string InstalledText =>
        _installedBytes is { } bytes ? Loc.Format("Installed: %@", ConversationCard.FileSize(bytes)) : "";

    /// <summary>A speaker model is installed, or the default diarizer is a configured cloud one (macOS <c>diarizerAvailable</c>).</summary>
    public bool DiarizerAvailable =>
        ModelCatalog.Diarization.Any(e => _app.InstalledModelIds.Contains(e.Id)) || _app.CloudDiarizerAvailable;

    /// <summary>The speech model new recordings use: the one set here, else the catalog default (macOS <c>defaultASRModelID</c>).</summary>
    public string DefaultASRModelID => _app.Settings.DefaultASRModelID ?? ModelCatalog.DefaultASR.Id;

    /// <summary>The speaker model new recordings use: the one set here, else the catalog default.</summary>
    public string DefaultDiarizerModelID => _app.Settings.DefaultDiarizerModelID ?? ModelCatalog.DefaultDiarizer.Id;

    /// <summary>The row of <paramref name="entry"/> as it is now.</summary>
    public ModelRowState State(ModelEntry entry)
    {
        var installed = _app.InstalledModelIds.Contains(entry.Id);
        double? download = _app.Downloads.TryGetValue(entry.Id, out var fraction) ? fraction : null;
        var isDefault = entry.Kind == ModelKind.Asr ? DefaultASRModelID == entry.Id : DefaultDiarizerModelID == entry.Id;
        var unsupported = UnsupportedNote(entry);
        // The recording's transcriber loads its model again after a long pause: deleting it meanwhile would end it.
        var busy = _app.TranscriptionProgress.Keys.Any(_app.IsTranscribing) || _app.RecordingCaptions is not null;
        return new ModelRowState(
            isDefault,
            installed,
            download,
            _app.DownloadErrors.GetValueOrDefault(entry.Id),
            unsupported,
            ShowSetDefault: installed && !isDefault,
            CanSetDefault: unsupported is null,
            CanDownload: unsupported is null,
            CanDelete: !busy && !_deleting.Contains(entry.Id));
    }

    /// <summary>The installed models read again, then their size (macOS: when the section shows).</summary>
    public async Task LoadAsync()
    {
        await _app.RefreshModelsAsync();
        if (_app.IsClosing) return;
        await MeasureAsync();
    }

    public void Download(ModelEntry entry) => _app.DownloadModel(entry.Id);

    public void CancelDownload(ModelEntry entry) => _app.CancelDownload(entry.Id);

    /// <summary>Removes the model's files (a default model is no longer the default); its Delete is off meanwhile.</summary>
    public async Task DeleteAsync(ModelEntry entry)
    {
        if (!_deleting.Add(entry.Id)) return;
        StatesChanged?.Invoke();
        try
        {
            await _app.DeleteModelAsync(entry.Id);
        }
        finally
        {
            _deleting.Remove(entry.Id);
            if (!_app.IsClosing) StatesChanged?.Invoke();
        }
    }

    /// <summary>
    /// Makes <paramref name="entry"/> the default of its kind; the catalog default is stored as none, so the pipeline's
    /// own choice applies again (macOS <c>setDefaultASRModel</c>, <c>setDefaultDiarizer</c>).
    /// </summary>
    public void SetDefault(ModelEntry entry)
    {
        var settings = _app.SettingsModel;
        if (entry.Kind == ModelKind.Asr)
        {
            var id = entry.Id == ModelCatalog.DefaultASR.Id ? null : entry.Id;
            settings.Update(s => s with { DefaultASRModelID = id });
        }
        else if (entry.Kind == ModelKind.Diarization)
        {
            var id = entry.Id == ModelCatalog.DefaultDiarizer.Id ? null : entry.Id;
            settings.Update(s => s with { DefaultDiarizerModelID = id });
        }
    }

    /// <summary>
    /// Makes the engine <paramref name="engineId"/> (a cloud engine id) the default transcription model, or with
    /// <paramref name="diarization"/> the default diarizer (macOS <c>setDefaultASRModel</c>, <c>setDefaultDiarizer</c>).
    /// </summary>
    public void SetDefault(string engineId, bool diarization)
    {
        if (diarization) _app.SettingsModel.Update(s => s with { DefaultDiarizerModelID = engineId });
        else _app.SettingsModel.Update(s => s with { DefaultASRModelID = engineId });
    }

    /// <summary>
    /// "No Vietnamese" for a speech model that cannot transcribe the next recording's language; null for Auto, which
    /// never disables a row, and for speaker models.
    /// </summary>
    private string? UnsupportedNote(ModelEntry entry)
    {
        var language = _app.RecordingLanguage;
        if (entry.Kind != ModelKind.Asr || language == LanguageOptions.Auto || language.Length == 0) return null;
        if (TranscriptionPipeline.Supports(entry, language)) return null;
        return Loc.Format("No %@", LanguageOptions.Name(WhisperLanguages.Primary(language)));
    }

    // What a row says about its model (macOS ModelRow's badges and notes)

    /// <summary>"Balanced · 547 MiB · ~1000 MB RAM · Multilingual · MIT": the tier, size, memory, languages and licence.</summary>
    public static string Details(ModelEntry entry)
    {
        List<string> parts = [TierName(entry.Tier)];
        if (entry.Kind == ModelKind.Diarization) parts.Add(Loc.T("Speakers"));
        if (entry.Repo is null)
        {
            parts.Add(Loc.T("System"));
        }
        else
        {
            parts.Add(SizeText(entry.DiskMB));
            parts.Add(Loc.Format("%@ RAM", (entry.RamIsEstimate ? "~" : "") + RamText(entry.RamMB)));
        }
        parts.Add(LanguagesText(entry.Languages));
        parts.Add(entry.License);
        return string.Join(" · ", parts);
    }

    /// <summary>The tier as its section and its row name it: "Fast", "Balanced", "Quality".</summary>
    public static string TierName(ModelTier tier) => Loc.T(tier.ToString());

    /// <summary>
    /// The quality in the app's three main languages on one line, languages with the same note joined ("Quality in
    /// English and French: Good · Vietnamese: Fair"); a speaker model's English note alone; null without notes.
    /// </summary>
    public static string? QualityNote(ModelEntry entry)
    {
        var notes = entry.QualityNotes;
        if (entry.Kind == ModelKind.Diarization) return notes.En.Length > 0 ? notes.En : null;
        var groups = new List<(List<string> Languages, string Note)>();
        foreach (var (language, note) in new[] { ("English", notes.En), ("French", notes.Fr), ("Vietnamese", notes.Vi) })
        {
            if (note.Length == 0) continue;
            var index = groups.FindIndex(g => g.Note == note);
            if (index >= 0) groups[index].Languages.Add(Loc.T(language));
            else groups.Add(([Loc.T(language)], note));
        }
        if (groups.Count == 0) return null;
        return Loc.Format("Quality in %@", string.Join(" · ", groups.Select(g => $"{JoinList(g.Languages)}: {g.Note}")));
    }

    /// <summary>The model needs more than half of this PC's memory while it runs.</summary>
    public static bool NeedsMuchMemory(ModelEntry entry) => PhysicalMemory > 0 && entry.RamMB * 1_048_576L > PhysicalMemory / 2;

    /// <summary>"Free unused models after": the offered minutes, plus a hand-edited value (the menu never shows none).</summary>
    public static IReadOnlyList<int> IdleMinutesOptions(int current)
    {
        var options = AppSettings.ModelIdleMinutesOptions;
        return options.Contains(current) ? options : [.. options.Append(current).Order()];
    }

    /// <summary>A model's download size as its row shows it ("547 MiB", "2.6 GB"; macOS <c>ModelRow.sizeText</c>).</summary>
    internal static string SizeText(int mib) =>
        mib < 1024
            ? Loc.Format("%lld MiB", mib)
            : Loc.Format("%@ GB", (mib / 1024.0).ToString("0.0", CultureInfo.CurrentCulture));

    private static string RamText(int mb) =>
        mb < 1024
            ? Loc.Format("%lld MB", mb)
            : Loc.Format("%@ GB", (mb / 1024.0).ToString("0.0", CultureInfo.CurrentCulture));

    private static string LanguagesText(IReadOnlyList<string>? languages) => languages switch
    {
        null => Loc.T("Multilingual"),
        [var only] => LanguageOptions.Name(only),
        _ => Loc.Plural("%lld languages", languages.Count),
    };

    /// <summary>"English", "English and French", "English, French and Vietnamese".</summary>
    private static string JoinList(List<string> items) =>
        items.Count == 1 ? items[0] : Loc.Format("%@ and %@", string.Join(", ", items[..^1]), items[^1]);

    // Following the app

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.InstalledModelIds):
                StatesChanged?.Invoke();
                OnPropertyChanged(nameof(DiarizerAvailable));
                _ = MeasureAsync();
                break;
            case nameof(AppViewModel.Settings):
            case nameof(AppViewModel.CloudEngineConfigs):
                // The default may be a cloud diarizer, configured or not.
                StatesChanged?.Invoke();
                OnPropertyChanged(nameof(DiarizerAvailable));
                break;
            // The unsupported note follows the next recording's language (a pick in the Record menu).
            case nameof(AppViewModel.RecordingLanguage):
            case nameof(AppViewModel.Downloads):
            case nameof(AppViewModel.DownloadErrors):
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.RecordingCaptions):
                StatesChanged?.Invoke();
                break;
        }
    }

    /// <summary>Measures the installed models off the UI thread; a change during a measure measures once more after it.</summary>
    private async Task MeasureAsync()
    {
        if (_measuring)
        {
            _measureAgain = true;
            return;
        }
        _measuring = true;
        try
        {
            do
            {
                _measureAgain = false;
                long bytes;
                try
                {
                    bytes = await Task.Run(_store.TotalInstalledBytes);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"EchoDiar: could not measure the installed models: {e}");
                    return;
                }
                if (_app.IsClosing) return;
                _installedBytes = bytes;
                OnPropertyChanged(nameof(InstalledText));
            }
            while (_measureAgain);
        }
        finally
        {
            _measuring = false;
        }
    }
}
