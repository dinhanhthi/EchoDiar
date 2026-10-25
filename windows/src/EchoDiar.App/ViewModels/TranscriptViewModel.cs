using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoDiar.AI;
using EchoDiar.AI.Cloud;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;

namespace EchoDiar.App.ViewModels;

/// <summary>One segment of the shown transcript (macOS <c>SegmentRow</c>).</summary>
/// <param name="Index">The segment's position in the transcript's segments.</param>
/// <param name="Speaker">The speaker id (<c>"me"</c>, <c>"s1"</c>…).</param>
/// <param name="Name">The speaker's display name: the user's name, else "Me" or "Speaker N".</param>
/// <param name="ColorIndex">See <see cref="TranscriptViewModel.ColorIndex"/>.</param>
/// <param name="Time">The start as "mm:ss".</param>
public sealed partial record TranscriptLineItem(
    int Index, Transcript.Segment Segment, string Speaker, string Name, int ColorIndex, string Time, string Text)
{
    public Guid Id => Segment.Id;

    /// <summary>Seconds from the start of the audio file.</summary>
    public double Start => Segment.Start;

    public double End => Segment.End;

    /// <summary>"Name [mm:ss]: text", the list item's name for screen readers.</summary>
    public override string ToString() => $"{Name} [{Time}]: {Text}";
}

/// <summary>A speaker of the shown transcript, for its chip and its rename field.</summary>
/// <param name="Name">The display name.</param>
/// <param name="DefaultName">"Me" or "Speaker N": the rename field's title ("Name for %@") and placeholder.</param>
/// <param name="CustomName">The user's name in the newest transcript, else "": the rename field's starting text.</param>
/// <param name="ColorIndex">See <see cref="TranscriptViewModel.ColorIndex"/>.</param>
public sealed partial record SpeakerChip(string Speaker, string Name, string DefaultName, string CustomName, int ColorIndex)
{
    public override string ToString() => Name;
}

/// <summary>One entry of the version menu.</summary>
/// <param name="Title">"Version N".</param>
/// <param name="Details">The engine and the date, "Whisper Base · 22/10/2026 14:05".</param>
/// <param name="IsShown">The version the list shows (the menu's check mark).</param>
public sealed partial record TranscriptVersionItem(int Number, string Title, string Details, bool IsShown);

/// <summary>
/// The Transcript tab's state (macOS <c>TranscriptView</c>): the newest transcript and the kept versions, the shown
/// version's lines and speakers, speaker rename and re-transcribe. Lives on the UI thread: <see cref="Attach"/> when the
/// page shows, <see cref="Detach"/> when it goes; it reloads whenever the conversation changes in the library (a job
/// finished, a speaker renamed).
/// </summary>
public sealed partial class TranscriptViewModel : ObservableObject
{
    /// <summary>The fixed list of speaker colors <see cref="ColorIndex"/> picks from (macOS: orange, green, pink, purple, teal, yellow, mint, indigo).</summary>
    public const int SpeakerColorCount = 8;

    private readonly AppViewModel _model;
    private bool _attached;

    /// <summary>Bumped at every load and at <see cref="Detach"/>, so an older load that finishes later is dropped.</summary>
    private int _loadGeneration;

    /// <summary>The library record the transcript was loaded for: a new one (any change) loads again.</summary>
    private Conversation? _loadedFor;

    private Conversation? _conversation;

    /// <summary>The newest transcript (<c>transcript.json</c>): rename writes it; null before a load or without one.</summary>
    private Transcript? _transcript;

    /// <summary>Kept transcripts, oldest first, the newest last.</summary>
    private IReadOnlyList<TranscriptVersion> _versions = [];

    /// <summary>The version picked in the version menu; null is the newest. Older ones are view-only.</summary>
    private int? _shownVersion;

    private bool _loadFailed;
    private bool _hasLoaded;
    private IReadOnlyList<TranscriptLineItem> _lines = [];
    private IReadOnlyList<SpeakerChip> _speakers = [];
    private string _retranscribeLanguage = "";

    public TranscriptViewModel(AppViewModel model, Guid id)
    {
        _model = model;
        ConversationId = id;
    }

    public Guid ConversationId { get; }

    /// <summary>The conversation's library record; null while it is not listed (the launch refresh) or once deleted.</summary>
    public Conversation? Conversation
    {
        get => _conversation;
        private set => SetProperty(ref _conversation, value);
    }

    /// <summary>The newest transcript, with the renamed speakers; null before a load, without one, or unreadable.</summary>
    public Transcript? Transcript => _transcript;

    /// <summary>The picked older version's transcript, else the newest: the lines, speakers, notes and Copy show it.</summary>
    public Transcript? ShownTranscript =>
        _shownVersion is { } number ? _versions.FirstOrDefault(v => v.Number == number)?.Transcript ?? _transcript : _transcript;

    /// <summary>The lines of <see cref="ShownTranscript"/>, in its order.</summary>
    public IReadOnlyList<TranscriptLineItem> Lines
    {
        get => _lines;
        private set => SetProperty(ref _lines, value);
    }

    /// <summary>The speakers of <see cref="ShownTranscript"/>: me first, then s1, s2… by number, then any other id.</summary>
    public IReadOnlyList<SpeakerChip> Speakers
    {
        get => _speakers;
        private set => SetProperty(ref _speakers, value);
    }

    /// <summary>The kept transcripts for the version menu, newest first, each with its engine and date.</summary>
    public IReadOnlyList<TranscriptVersionItem> Versions
    {
        get
        {
            var shown = ShownVersionNumber;
            return _versions.Reverse().Select(v => new TranscriptVersionItem(
                v.Number, VersionTitle(v.Number), VersionDetails(v.Transcript), v.Number == shown)).ToList();
        }
    }

    /// <summary>The version menu shows from two versions, and not while a job runs.</summary>
    public bool ShowsVersionMenu => _versions.Count >= 2 && !IsTranscribing;

    /// <summary>The version menu's label: "Version N · engine" of the shown version.</summary>
    public string VersionLabel =>
        string.Join(" · ", new[] { VersionTitle(ShownVersionNumber), ShownTranscript is { } t ? EngineName(t) : null }.OfType<string>());

    /// <summary>An older version is shown: it is view-only (no rename).</summary>
    public bool IsViewingOlderVersion => _shownVersion is not null;

    /// <summary>The transcript is being read.</summary>
    public bool IsLoading => _conversation is { HasTranscript: true } && _transcript is null && !_loadFailed;

    /// <summary><c>transcript.json</c> exists but could not be read.</summary>
    public bool LoadFailed => _loadFailed;

    /// <summary>
    /// The first load has returned (or there is nothing to read); until then the page shows its loading pane (macOS
    /// TranscriptView <c>loaded</c>). Stays true across later loads.
    /// </summary>
    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => SetProperty(ref _hasLoaded, value);
    }

    /// <summary>What the card shows while there are no lines.</summary>
    public string EmptyText
    {
        get
        {
            if (_loadFailed) return "The transcript could not be read.";
            if (ShownTranscript is not null) return "No speech was found in this recording.";
            return _conversation?.Status switch
            {
                ConversationStatus.Recording => "Recording in progress.",
                ConversationStatus.Compressing => "Compressing the audio…",
                ConversationStatus.Failed => "The recording failed.",
                _ => "No transcript yet.",
            };
        }
    }

    /// <summary>A job for the conversation is queued or running.</summary>
    public bool IsTranscribing => _model.IsTranscribing(ConversationId);

    /// <summary>Speaker chips are enabled: the newest transcript is shown and no job runs (it would overwrite the name).</summary>
    public bool CanRename => _transcript is not null && !IsTranscribing && !IsViewingOlderVersion;

    /// <summary>
    /// Re-transcribe is enabled: not while a job runs, the audio is still being recorded or compressed, the recording
    /// failed, or the audio was deleted.
    /// </summary>
    public bool CanRetranscribe =>
        _conversation is { HasAudio: true } c && !IsTranscribing
        && c.Status is not (ConversationStatus.Recording or ConversationStatus.Compressing or ConversationStatus.Failed);

    /// <summary>The engines of "Transcribe again with": the installed local speech engines in catalog order, then the configured cloud ones.</summary>
    public IReadOnlyList<EngineChoice> RetranscribeEngines => _model.InstalledBatchEngines;

    /// <summary>The language of the next Re-transcribe; "" keeps the recording's language markers.</summary>
    public string RetranscribeLanguage
    {
        get => _retranscribeLanguage;
        set => SetProperty(ref _retranscribeLanguage, value);
    }

    /// <summary>Follows the library and the jobs, and loads the transcript. Call when the page shows.</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _model.PropertyChanged += OnModelChanged;
        _loadedFor = null;
        UpdateConversation();
    }

    /// <summary>Stops following the library; a load still reading is dropped. Call when the page goes.</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _model.PropertyChanged -= OnModelChanged;
        _loadGeneration++;
    }

    /// <summary>Shows version <paramref name="number"/>; the newest shows <see cref="Transcript"/> with its renamed speakers.</summary>
    public void ShowVersion(int number)
    {
        if (_versions.Count == 0 || _versions.All(v => v.Number != number)) return;
        var shown = number == _versions[^1].Number ? (int?)null : number;
        if (shown == _shownVersion) return;
        _shownVersion = shown;
        ApplyShownVersion();
    }

    /// <summary>
    /// Saves <paramref name="speaker"/>'s name into <c>transcript.json</c> (read fresh, only this name changes, no
    /// version kept), off the UI thread; a blank name restores the default. The library then lists the conversation
    /// again, which reloads the transcript.
    /// </summary>
    public async Task RenameSpeakerAsync(string speaker, string name)
    {
        name = name.Trim();
        if (!CanRename || _transcript is not { } current || _conversation is not { } conversation) return;
        var newName = name.Length == 0 ? null : name;
        if (current.SpeakerNames.GetValueOrDefault(speaker) == newName) return;
        _transcript = current with { SpeakerNames = WithName(current.SpeakerNames, speaker, newName) };
        ApplyShownVersion();
        var folder = conversation.Folder;
        try
        {
            await Task.Run(() =>
            {
                var stored = Transcript.Read(folder);
                stored.SpeakerNames = WithName(stored.SpeakerNames, speaker, newName);
                stored.Write(folder, keepingPrevious: false);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Debug.WriteLine($"EchoDiar: could not save the speaker name in {folder}: {e}");
            return;
        }
        if (_model.IsClosing) return;
        await _model.TranscriptEditedAsync(ConversationId);
    }

    /// <summary>
    /// Transcribes the conversation again with <paramref name="engineId"/> in <see cref="RetranscribeLanguage"/>; the
    /// pipeline keeps the current transcript as a version.
    /// </summary>
    public Task RetranscribeAsync(string engineId)
    {
        if (!CanRetranscribe) return Task.CompletedTask;
        var language = RetranscribeLanguage.Length == 0 ? null : RetranscribeLanguage;
        return _model.TranscribeAsync(ConversationId, engineId, language);
    }

    /// <summary>Copy transcript: one "Name [mm:ss]: text" line per segment of the shown transcript, with the renamed names.</summary>
    public string TranscriptText()
    {
        if (ShownTranscript is not { } transcript) return "";
        return string.Join("\n", transcript.Segments.Select(s => $"{transcript.DisplayName(s.Speaker)} [{Timestamp(s.Start)}]: {s.Text}"));
    }

    /// <summary>
    /// The speaker's color: -1 for me (the accent color), else an index into the <see cref="SpeakerColorCount"/> fixed
    /// colors by speaker number, so a speaker keeps its color.
    /// </summary>
    public static int ColorIndex(string speaker)
    {
        if (speaker == Transcript.Speaker.Me) return -1;
        var index = SpeakerNumber(speaker) is { } number ? number - 1 : speaker.Sum(c => (int)c);
        return Math.Max(index, 0) % SpeakerColorCount;
    }

    /// <summary>
    /// "mm:ss"; minutes go past 59 in recordings longer than an hour. Rounded to the nearest second like the prompts, so
    /// a cited [00:07] shows as 00:07 (macOS uses the same Duration format for both).
    /// </summary>
    public static string Timestamp(double seconds) => PromptBuilder.Timestamp(seconds);

    /// <summary>Silent once the window closed, like <see cref="AppViewModel"/>.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_model.IsClosing) base.OnPropertyChanged(e);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.Conversations):
                UpdateConversation();
                break;
            case nameof(AppViewModel.TranscriptionProgress):
                NotifyJobState();
                break;
            case nameof(AppViewModel.InstalledModelIds):
            case nameof(AppViewModel.CloudEngineConfigs):
                OnPropertyChanged(nameof(RetranscribeEngines));
                break;
        }
    }

    private void UpdateConversation()
    {
        var conversation = _model.Conversations.FirstOrDefault(c => c.Id == ConversationId);
        Conversation = conversation;
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(CanRetranscribe));
        // Not listed (deleted, or not yet listed at launch): nothing to read, the panes show why.
        if (conversation is null) HasLoaded = true;
        if (conversation is null || conversation == _loadedFor) return;
        _loadedFor = conversation;
        _ = LoadAsync(conversation);
    }

    /// <summary>Reads every version off the UI thread; the newest is shown again (macOS <c>load</c>).</summary>
    private async Task LoadAsync(Conversation conversation)
    {
        var generation = ++_loadGeneration;
        var folder = conversation.Folder;
        var wantsTranscript = conversation.HasTranscript;
        IReadOnlyList<TranscriptVersion> versions;
        try
        {
            versions = wantsTranscript ? await Task.Run(() => Transcript.ReadVersions(folder)) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"EchoDiar: could not read the transcripts in {folder}: {e}");
            versions = [];
        }
        if (generation != _loadGeneration || !_attached || _model.IsClosing) return;
        _versions = versions;
        _transcript = versions.Count > 0 ? versions[^1].Transcript : null;
        _shownVersion = null;
        _loadFailed = wantsTranscript && versions.Count == 0;
        ApplyShownVersion();
        HasLoaded = true;
    }

    /// <summary>The lines and speakers of <see cref="ShownTranscript"/>, and every state that depends on it.</summary>
    private void ApplyShownVersion()
    {
        var shown = ShownTranscript;
        Lines = shown is null
            ? []
            : shown.Segments.Select((s, i) => new TranscriptLineItem(
                i, s, s.Speaker, shown.DisplayName(s.Speaker), ColorIndex(s.Speaker), Timestamp(s.Start), s.Text.Trim())).ToList();
        Speakers = shown is null
            ? []
            : SpeakerIds(shown).Select(id => new SpeakerChip(
                id, shown.DisplayName(id), Transcript.DefaultName(id),
                _transcript?.SpeakerNames.GetValueOrDefault(id) ?? "", ColorIndex(id))).ToList();
        OnPropertyChanged(nameof(Transcript));
        OnPropertyChanged(nameof(ShownTranscript));
        OnPropertyChanged(nameof(Versions));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(IsViewingOlderVersion));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(LoadFailed));
        OnPropertyChanged(nameof(EmptyText));
        NotifyJobState();
    }

    private void NotifyJobState()
    {
        OnPropertyChanged(nameof(IsTranscribing));
        OnPropertyChanged(nameof(ShowsVersionMenu));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanRetranscribe));
    }

    /// <summary>The newest when no older one is picked; 0 without versions.</summary>
    private int ShownVersionNumber => _shownVersion ?? (_versions.Count > 0 ? _versions[^1].Number : 0);

    private static string VersionTitle(int number) => Loc.Format("Version %lld", number);

    /// <summary>The engine and the date of a version, for its menu entry.</summary>
    private string VersionDetails(Transcript transcript) =>
        string.Join(" · ", new[] { EngineName(transcript), transcript.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) }.OfType<string>());

    /// <summary>
    /// The engine that made <paramref name="transcript"/> (macOS <c>transcriptEngineName</c>): "OpenAI (whisper-1)" with
    /// the stored model, or the catalog name; the raw id when the provider or entry is gone, null when unknown.
    /// </summary>
    private string? EngineName(Transcript transcript)
    {
        if (transcript.EngineID is not { Length: > 0 } id) return null;
        if (CloudTranscription.ProviderId(id) is not { } providerId) return ModelCatalog.Entry(id)?.DisplayName ?? id;
        var name = _model.ProvidersModel.Config(providerId)?.DisplayName ?? providerId;
        return transcript.ModelID is { Length: > 0 } model ? $"{name} ({model})" : name;
    }

    /// <summary>me, then s1, s2… by number, then any other id (macOS <c>speakers(in:)</c>).</summary>
    private static List<string> SpeakerIds(Transcript transcript) =>
        transcript.Segments.Select(s => s.Speaker).Distinct()
            .OrderBy(s => s == Transcript.Speaker.Me ? 0 : SpeakerNumber(s) is null ? 2 : 1)
            .ThenBy(s => SpeakerNumber(s) ?? 0)
            .ThenBy(s => s, StringComparer.Ordinal)
            .ToList();

    /// <summary>N for <c>"sN"</c>, else null.</summary>
    private static int? SpeakerNumber(string speaker) =>
        speaker.StartsWith('s') && int.TryParse(speaker.AsSpan(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static Dictionary<string, string> WithName(Dictionary<string, string> names, string speaker, string? name)
    {
        var copy = new Dictionary<string, string>(names);
        if (name is null) copy.Remove(speaker);
        else copy[speaker] = name;
        return copy;
    }
}
