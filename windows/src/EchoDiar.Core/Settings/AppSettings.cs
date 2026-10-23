using System.Text.Json;
using System.Text.Json.Serialization;
using EchoDiar.Core.Json;

namespace EchoDiar.Core.Settings;

/// <summary>
/// The <c>settings.json</c> file format, version 1, shared with macOS. Every key is optional when read; property
/// defaults are the read defaults of Swift's <c>init(from:)</c>, and the clamps and shortcut resets apply only when
/// read (as in Swift). Keys this type does not model (the macOS-only Carbon <c>globalHotKey</c> and
/// <c>globalPauseHotKey</c>, keys of newer builds) are kept in <see cref="Extra"/> and written back unchanged.
/// </summary>
public sealed record AppSettings : IJsonOnDeserialized
{
    /// <summary>
    /// The Windows catalog's default embedder (macOS: <c>mlx-qwen3-embedding-0.6b</c>). Only a missing key gets it:
    /// a macOS id read from <c>settings.json</c> is kept as is, and each OS treats the other's id as unknown.
    /// </summary>
    public const string DefaultEmbeddingModelID = "gguf-qwen3-embedding-0.6b";
    public static readonly (int Min, int Max) AutoDeleteDaysRange = (1, 3650);
    public static readonly (int Min, int Max) SpeakerCountHintRange = (1, 20);
    /// <summary>Chat context sizes offered in Settings, besides 0 (the whole chat).</summary>
    public static readonly IReadOnlyList<int> ChatContextOptions = [4, 10, 20, 50];
    /// <summary>Idle times offered in Settings; a hand-edited value is clamped to <see cref="ModelIdleMinutesRange"/>.</summary>
    public static readonly IReadOnlyList<int> ModelIdleMinutesOptions = [1, 2, 5, 10];
    public static readonly (int Min, int Max) ModelIdleMinutesRange = (1, 60);

    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("launchAtLogin")] public bool LaunchAtLogin { get; set; }
    /// <summary>A language code, or "auto" to detect it.</summary>
    [JsonPropertyName("defaultLanguage")] public string DefaultLanguage { get; set; } = "auto";
    /// <summary>Languages picked for recordings and re-transcriptions, most recent first, at most 5.</summary>
    [JsonPropertyName("recentLanguages")] public List<string> RecentLanguages { get; set; } = [];
    /// <summary>Total AAC bitrate of the compressed 2-channel <c>audio.m4a</c>.</summary>
    [JsonPropertyName("compressionBitrateKbps")] public int CompressionBitrateKbps { get; set; } = 64;
    [JsonPropertyName("keepUncompressedAudio")] public bool KeepUncompressedAudio { get; set; }
    [JsonPropertyName("playbackChannels")] public PlaybackChannels PlaybackChannels { get; set; } = PlaybackChannels.Mixed;
    [JsonPropertyName("onboardingCompleted")] public bool OnboardingCompleted { get; set; }
    /// <summary>Minutes a loaded on-device model may go unused before it is freed.</summary>
    [JsonPropertyName("modelIdleMinutes")] public int ModelIdleMinutes { get; set; } = 2;
    /// <summary>Catalog id of the ASR engine, or <c>cloud:&lt;provider id&gt;</c>; null = the catalog default.</summary>
    [JsonPropertyName("defaultASRModelID")] public string? DefaultASRModelID { get; set; }
    /// <summary>Catalog diarization id, or <c>cloud:&lt;provider id&gt;</c>; null = the catalog default.</summary>
    [JsonPropertyName("defaultDiarizerModelID")] public string? DefaultDiarizerModelID { get; set; }
    /// <summary>Configured AI providers; their API keys are in the credential store, never here.</summary>
    [JsonPropertyName("providers")] public List<ProviderConfig> Providers { get; set; } = [];
    /// <summary><c>&lt;provider id&gt;@&lt;scheme://host[:port]&gt;</c> confirmed for cloud transcription.</summary>
    [JsonPropertyName("cloudSTTConfirmed")] public List<string> CloudSTTConfirmed { get; set; } = [];
    [JsonPropertyName("defaultAIProviderID")] public string? DefaultAIProviderID { get; set; }
    /// <summary>Model for <see cref="DefaultAIProviderID"/>; null = that config's default model.</summary>
    [JsonPropertyName("defaultAIModel")] public string? DefaultAIModel { get; set; }
    [JsonPropertyName("summaryLanguage")] public string SummaryLanguage { get; set; } = "auto";
    /// <summary>Earlier chat messages sent with each question; 0 = the whole chat.</summary>
    [JsonPropertyName("chatContextMessages")] public int ChatContextMessages { get; set; } = 10;
    [JsonPropertyName("ragEnabled")] public bool RagEnabled { get; set; }
    /// <summary>An embedding catalog id, <c>apple-nl</c>, or <c>api:&lt;provider id&gt;</c>.</summary>
    [JsonPropertyName("embeddingModelID")] public string EmbeddingModelID { get; set; } = DefaultEmbeddingModelID;
    [JsonPropertyName("embeddingAPIModel")] public string EmbeddingAPIModel { get; set; } = "";
    [JsonPropertyName("cloudEmbeddingConfirmed")] public List<string> CloudEmbeddingConfirmed { get; set; } = [];
    /// <summary>The WASAPI endpoint ID of the recording microphone (a Core Audio UID on macOS); null = the default input.</summary>
    [JsonPropertyName("micDeviceUID")] public string? MicDeviceUID { get; set; }
    [JsonPropertyName("meetingDetection")] public bool MeetingDetection { get; set; } = true;
    /// <summary>By lowercase exe name (a bundle ID on macOS); a missing entry asks.</summary>
    [JsonPropertyName("perAppBehavior")] public Dictionary<string, MeetingBehavior> PerAppBehavior { get; set; } = [];
    /// <summary>Where the floating pill was dragged, by monitor device interface name (a display UUID on macOS).</summary>
    [JsonPropertyName("pillPositions")] public Dictionary<string, PillPosition> PillPositions { get; set; } = [];
    [JsonPropertyName("showPill")] public bool ShowPill { get; set; } = true;
    [JsonPropertyName("hidePillWhenAppActive")] public bool HidePillWhenAppActive { get; set; }
    [JsonPropertyName("showPillInScreenCaptures")] public bool ShowPillInScreenCaptures { get; set; }
    [JsonPropertyName("attachPillToWindow")] public bool AttachPillToWindow { get; set; }
    /// <summary>From the meeting window's top-right corner; null = just inside it.</summary>
    [JsonPropertyName("pillWindowOffset")] public PillPosition? PillWindowOffset { get; set; }
    [JsonPropertyName("pillSize")] public PillSize PillSize { get; set; } = PillSize.Medium;
    [JsonPropertyName("endMonitor")] public MeetingEndMonitorConfiguration EndMonitor { get; set; } = new();
    [JsonPropertyName("routeChangeBehavior")] public RouteChangeBehavior RouteChangeBehavior { get; set; } = RouteChangeBehavior.Ask;
    [JsonPropertyName("autoTranscribe")] public bool AutoTranscribe { get; set; } = true;
    [JsonPropertyName("transcribeWhileRecording")] public bool TranscribeWhileRecording { get; set; } = true;
    /// <summary>Expected speakers on the system channel, 1...20; null = detect.</summary>
    [JsonPropertyName("speakerCountHint")] public int? SpeakerCountHint { get; set; }
    [JsonPropertyName("diarization")] public bool Diarization { get; set; }
    [JsonPropertyName("deferTranscriptionWhenConstrained")] public bool DeferTranscriptionWhenConstrained { get; set; } = true;
    [JsonPropertyName("autoDeleteEnabled")] public bool AutoDeleteEnabled { get; set; }
    /// <summary>Age in days, 1...3650, after which auto-delete applies.</summary>
    [JsonPropertyName("autoDeleteAfterDays")] public int AutoDeleteAfterDays { get; set; } = 90;
    [JsonPropertyName("autoDeleteMode")] public RetentionMode AutoDeleteMode { get; set; } = RetentionMode.AudioOnly;
    [JsonPropertyName("deleteAudioAfterTranscription")] public bool DeleteAudioAfterTranscription { get; set; }
    [JsonPropertyName("deleteWithoutConfirmation")] public bool DeleteWithoutConfirmation { get; set; }
    /// <summary>Windows-only record start/stop shortcut (macOS keeps its Carbon <c>globalHotKey</c> in <see cref="Extra"/>).</summary>
    [JsonPropertyName("globalHotKeyWindows")] public WindowsHotKey GlobalHotKeyWindows { get; set; } = WindowsHotKey.DefaultRecord;
    [JsonPropertyName("globalHotKeyEnabled")] public bool GlobalHotKeyEnabled { get; set; } = true;
    /// <summary>Windows-only pause/resume shortcut.</summary>
    [JsonPropertyName("globalPauseHotKeyWindows")] public WindowsHotKey GlobalPauseHotKeyWindows { get; set; } = WindowsHotKey.DefaultPause;
    [JsonPropertyName("globalPauseHotKeyEnabled")] public bool GlobalPauseHotKeyEnabled { get; set; } = true;

    /// <summary>Keys not modeled here, written back as read.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; } = [];

    void IJsonOnDeserialized.OnDeserialized()
    {
        ModelIdleMinutes = Clamp(ModelIdleMinutes, ModelIdleMinutesRange);
        AutoDeleteAfterDays = Clamp(AutoDeleteAfterDays, AutoDeleteDaysRange);
        if (SpeakerCountHint is { } hint) SpeakerCountHint = Clamp(hint, SpeakerCountHintRange);
        // A shortcut the recorder refuses (a hand-edited or synced file) would take keys away from every app.
        if (!GlobalHotKeyWindows.IsAllowedGlobal) GlobalHotKeyWindows = WindowsHotKey.DefaultRecord;
        if (!GlobalPauseHotKeyWindows.IsAllowedGlobal) GlobalPauseHotKeyWindows = WindowsHotKey.DefaultPause;
        // Credential Manager names keys case-insensitively: ids differing only in case would share one API key and send
        // it to each other's host. The first wins, like Swift's uniquingKeysWith { first, _ in first }.
        Providers = Providers.DistinctBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static int Clamp(int value, (int Min, int Max) range) => Math.Clamp(value, range.Min, range.Max);
}

/// <summary>Swift <c>MeetingEndMonitor.Configuration</c>: auto-stop thresholds, clamped when read.</summary>
public sealed record MeetingEndMonitorConfiguration : IJsonOnDeserialized
{
    public static readonly (int Min, int Max) SourceInactiveRange = (20, 60);
    public static readonly (int Min, int Max) CallEndedRange = (30, 300);
    public static readonly (int Min, int Max) SilenceRange = (1, 120);
    public static readonly (int Min, int Max) MaxDurationRange = (1, 1440);

    /// <summary>Seconds the source app's input and output must both be inactive.</summary>
    [JsonPropertyName("sourceInactiveSeconds")] public int SourceInactiveSeconds { get; set; } = 25;
    /// <summary>Seconds the call-ended conditions must hold.</summary>
    [JsonPropertyName("callEndedSeconds")] public int CallEndedSeconds { get; set; } = 60;
    /// <summary>Minutes both channels must be silent.</summary>
    [JsonPropertyName("silenceMinutes")] public int SilenceMinutes { get; set; } = 10;
    /// <summary>Hard cap on the recording length, in minutes.</summary>
    [JsonPropertyName("maxDurationMinutes")] public int MaxDurationMinutes { get; set; } = 240;

    void IJsonOnDeserialized.OnDeserialized()
    {
        SourceInactiveSeconds = AppSettings.Clamp(SourceInactiveSeconds, SourceInactiveRange);
        CallEndedSeconds = AppSettings.Clamp(CallEndedSeconds, CallEndedRange);
        SilenceMinutes = AppSettings.Clamp(SilenceMinutes, SilenceRange);
        MaxDurationMinutes = AppSettings.Clamp(MaxDurationMinutes, MaxDurationRange);
    }
}

/// <summary>
/// A global shortcut for <c>RegisterHotKey</c>: a virtual-key code and a <c>MOD_*</c> mask. Both keys are required
/// when read. Stored under Windows-only keys: Carbon key codes do not map to virtual keys.
/// </summary>
public readonly record struct WindowsHotKey
{
    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;
    public const uint ModWin = 0x8;

    /// <summary>Ctrl+Alt+R.</summary>
    public static readonly WindowsHotKey DefaultRecord = new() { VirtualKey = 0x52, Modifiers = ModControl | ModAlt };
    /// <summary>Ctrl+Alt+P.</summary>
    public static readonly WindowsHotKey DefaultPause = new() { VirtualKey = 0x50, Modifiers = ModControl | ModAlt };

    /// <summary>
    /// Standard edit and window shortcuts, by English title, that a global one would take from every app (the recorder
    /// also refuses EchoDiar's own in-app shortcuts).
    /// </summary>
    public static IReadOnlyList<(WindowsHotKey Key, string Title)> StandardShortcuts { get; } =
    [
        (Ctrl('C'), "Copy"),
        (Ctrl('X'), "Cut"),
        (Ctrl('V'), "Paste"),
        (Ctrl('Z'), "Undo"),
        (Ctrl('Y'), "Redo"),
        (Ctrl('A'), "Select All"),
        (new() { VirtualKey = 0x73, Modifiers = ModAlt }, "Close"), // Alt+F4
    ];

    [JsonPropertyName("virtualKey")] public required uint VirtualKey { get; init; }
    [JsonPropertyName("modifiers")] public required uint Modifiers { get; init; }

    /// <summary>Has Ctrl or Alt: a shortcut with Shift or Win alone (or none) would take a key away from typing.</summary>
    [JsonIgnore] public bool HasCtrlOrAlt => (Modifiers & (ModControl | ModAlt)) != 0;

    /// <summary>
    /// May be read as a global shortcut: a virtual key (1...254), only <c>MOD_ALT</c>, <c>MOD_CONTROL</c>,
    /// <c>MOD_SHIFT</c> and <c>MOD_WIN</c>, Ctrl or Alt among them, and not a <see cref="StandardShortcuts"/> one.
    /// </summary>
    [JsonIgnore]
    public bool IsAllowedGlobal
    {
        get
        {
            var key = this;
            return VirtualKey is >= 1 and <= 254 && (Modifiers & ~(ModAlt | ModControl | ModShift | ModWin)) == 0
                && HasCtrlOrAlt && !StandardShortcuts.Any(s => s.Key == key);
        }
    }

    private static WindowsHotKey Ctrl(char key) => new() { VirtualKey = key, Modifiers = ModControl };
}

/// <summary>The pill's top-right corner, in points from the top-right corner of its monitor's work area (or of the meeting window).</summary>
public sealed record PillPosition
{
    [JsonPropertyName("x")] public required double X { get; set; }
    [JsonPropertyName("y")] public required double Y { get; set; }
}

/// <summary>What a detected meeting app does. An unknown value fails the read, as in Swift.</summary>
[JsonConverter(typeof(StrictCamelCaseEnumConverter<MeetingBehavior>))]
public enum MeetingBehavior
{
    /// <summary>The pill asks "Record?".</summary>
    Ask,
    /// <summary>Recording starts on its own.</summary>
    Always,
    Ignore,
}

/// <summary>What losing the recording microphone does. An unknown value fails the read, as in Swift.</summary>
[JsonConverter(typeof(StrictCamelCaseEnumConverter<RouteChangeBehavior>))]
public enum RouteChangeBehavior { Ask, Continue, Stop }

/// <summary>How playback uses the mic (left) and system (right) channels. An unknown value reads as <see cref="Mixed"/>.</summary>
[JsonConverter(typeof(PlaybackChannelsConverter))]
public enum PlaybackChannels { Mixed, Split }

public sealed class PlaybackChannelsConverter() : TolerantEnumConverter<PlaybackChannels>(PlaybackChannels.Mixed);

/// <summary>What auto-delete moves to the trash. An unknown value reads as the less destructive <see cref="AudioOnly"/>.</summary>
[JsonConverter(typeof(RetentionModeConverter))]
public enum RetentionMode { AudioOnly, WholeConversation }

public sealed class RetentionModeConverter() : TolerantEnumConverter<RetentionMode>(RetentionMode.AudioOnly);

/// <summary>Size of the recording pill. An unknown value reads as <see cref="Medium"/>.</summary>
[JsonConverter(typeof(PillSizeConverter))]
public enum PillSize { Small, Medium, Large }

public sealed class PillSizeConverter() : TolerantEnumConverter<PillSize>(PillSize.Medium);

/// <summary>A synthesized Swift <c>String</c> enum: camelCase raw values; an unknown value or a number throws.</summary>
public sealed class StrictCamelCaseEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where T : struct, Enum;
