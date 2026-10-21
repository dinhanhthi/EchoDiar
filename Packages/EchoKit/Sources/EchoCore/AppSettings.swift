import Foundation

/// The settings file format, version 1. Every key is optional when read, so files written by older builds
/// still decode; new fields must follow the same `decodeIfPresent` + default rule.
public struct AppSettings: Codable, Equatable, Sendable {
    public var version = 1
    public var launchAtLogin: Bool
    /// A language code, or "auto" to detect it.
    public var defaultLanguage: String
    /// Languages picked for recordings and re-transcriptions, most recent first, at most 5.
    public var recentLanguages: [String]
    /// Total AAC bitrate of the compressed 2-channel `audio.m4a`.
    public var compressionBitrateKbps: Int
    /// Keep the uncompressed CAF next to the m4a after compression.
    public var keepUncompressedAudio: Bool
    /// How the transcript plays the 2-channel recording (mic left, system right).
    public var playbackChannels: PlaybackChannels
    /// Set by "Done" in the first-run onboarding window.
    public var onboardingCompleted: Bool
    /// Live captions while recording (opt-in); the batch transcript after Stop stays the source of truth.
    public var liveTranscript: Bool
    /// Catalog id of the live captions engine: a WhisperKit entry or the Nemotron streaming entry.
    public var liveTranscriptEngine: String
    /// Minutes a loaded on-device model may go unused before it is freed: the live captions model while a recording
    /// stays paused. Every other model is freed as soon as its job ends.
    public var modelIdleMinutes: Int
    /// Catalog id of the ASR engine for new transcriptions, or a cloud engine id (`cloud:<provider id>`); nil = the
    /// catalog default.
    public var defaultASRModelID: String?
    /// Default diarization model: a catalog diarization id or a cloud engine id (`cloud:<provider id>`); nil = the
    /// catalog default.
    public var defaultDiarizerModelID: String?
    /// Configured AI providers; their API keys are in the Keychain, never here.
    public var providers: [ProviderConfig]
    /// Destinations (`<provider id>@<scheme://host[:port]>`) whose one-time "audio leaves this Mac" confirmation for
    /// cloud transcription was accepted; a changed base URL asks again (older bare provider ids no longer match).
    public var cloudSTTConfirmed: [String]
    /// AI tab provider: a `ProviderConfig` id or an LLM catalog entry id (Foundation Models, MLX); nil = none picked.
    public var defaultAIProviderID: String?
    /// Model for `defaultAIProviderID`; nil = that config's `defaultModel`.
    public var defaultAIModel: String?
    /// Summary language: "auto" (the transcript's dominant language), "en", "fr", "vi" or another BCP-47 code.
    public var summaryLanguage: String
    /// Earlier chat messages sent with each question; 0 = the whole chat.
    public var chatContextMessages: Int
    /// Global chat over every transcribed conversation (opt-in); builds and keeps the embedding index.
    public var ragEnabled: Bool
    /// Embedder of the index: an embedding catalog id, `"apple-nl"`, or `"api:<provider id>"`.
    public var embeddingModelID: String
    /// Model name sent to an API embedder (`api:<provider id>`).
    public var embeddingAPIModel: String
    /// Destinations (`<provider id>@<scheme://host[:port]>`) whose one-time "transcripts leave this Mac" confirmation
    /// for cloud embeddings was accepted; a changed base URL asks again.
    public var cloudEmbeddingConfirmed: [String]
    /// Core Audio UID of the recording microphone; nil = the system default input (also used when it is gone).
    public var micDeviceUID: String?
    /// Watch known meeting apps' mic use and offer to record them.
    public var meetingDetection: Bool
    /// What a detected meeting app does, by `MeetingApp.appBundleID`; a missing entry asks.
    public var perAppBehavior: [String: MeetingBehavior]
    /// Where the floating pill was dragged, by display UUID.
    public var pillPositions: [String: PillPosition]
    /// "Show floating indicator" while recording; prompts show either way.
    public var showPill: Bool
    /// Hide the recording pill while an EchoDiar window is key.
    public var hidePillWhenAppActive: Bool
    /// Keep the pill at the meeting window's top-right corner (needs Accessibility).
    public var attachPillToWindow: Bool
    /// Where the attached pill was dragged, from the meeting window's top-right corner; nil = just inside it.
    public var pillWindowOffset: PillPosition?
    /// Size of the recording pill.
    public var pillSize: PillSize
    /// Auto-stop thresholds.
    public var endMonitor: MeetingEndMonitor.Configuration
    /// What losing the recording microphone does.
    public var routeChangeBehavior: RouteChangeBehavior
    /// Queue the batch transcription when a recording stops (after compression).
    public var autoTranscribe: Bool
    /// Transcribe speech chunks during the recording with the local engine, so only what was missed is left at Stop.
    public var transcribeWhileRecording: Bool
    /// Expected number of speakers on the system channel for diarization, 1...20; nil = detect.
    public var speakerCountHint: Int?
    /// "Tell speakers apart": diarize the system channel; off = one "Speaker 1" for the meeting audio.
    public var diarization: Bool
    /// Wait while the Mac is hot or in Low Power Mode before a transcription job starts.
    public var deferTranscriptionWhenConstrained: Bool
    /// Move old transcribed conversations (or their audio) to the Trash daily, see `RetentionPolicy`.
    public var autoDeleteEnabled: Bool
    /// Age in days, 1...3650, after which auto-delete applies.
    public var autoDeleteAfterDays: Int
    public var autoDeleteMode: RetentionMode
    /// Move the audio to the Trash once a transcription succeeds; transcript, summary and chat stay.
    public var deleteAudioAfterTranscription: Bool
    /// The library's Delete moves a conversation to the Trash without asking first.
    public var deleteWithoutConfirmation: Bool
    /// Global record start/stop shortcut, in Carbon terms for `RegisterEventHotKey`.
    public var globalHotKey: HotKey
    public var globalHotKeyEnabled: Bool
    /// Global pause/resume shortcut, same terms as `globalHotKey`.
    public var globalPauseHotKey: HotKey
    public var globalPauseHotKeyEnabled: Bool

    public static let defaultLiveTranscriptEngine = "whisperkit-turbo-632"
    public static let defaultEmbeddingModelID = "mlx-qwen3-embedding-0.6b"

    public init(
        launchAtLogin: Bool = false,
        defaultLanguage: String = "auto",
        recentLanguages: [String] = [],
        compressionBitrateKbps: Int = 64,
        keepUncompressedAudio: Bool = false,
        playbackChannels: PlaybackChannels = .mixed,
        onboardingCompleted: Bool = false,
        liveTranscript: Bool = false,
        liveTranscriptEngine: String = AppSettings.defaultLiveTranscriptEngine,
        modelIdleMinutes: Int = 2,
        defaultASRModelID: String? = nil,
        defaultDiarizerModelID: String? = nil,
        providers: [ProviderConfig] = [],
        cloudSTTConfirmed: [String] = [],
        defaultAIProviderID: String? = nil,
        defaultAIModel: String? = nil,
        summaryLanguage: String = "auto",
        chatContextMessages: Int = 10,
        ragEnabled: Bool = false,
        embeddingModelID: String = AppSettings.defaultEmbeddingModelID,
        embeddingAPIModel: String = "",
        cloudEmbeddingConfirmed: [String] = [],
        micDeviceUID: String? = nil,
        meetingDetection: Bool = true,
        perAppBehavior: [String: MeetingBehavior] = [:],
        pillPositions: [String: PillPosition] = [:],
        showPill: Bool = true,
        hidePillWhenAppActive: Bool = false,
        attachPillToWindow: Bool = false,
        pillWindowOffset: PillPosition? = nil,
        pillSize: PillSize = .medium,
        endMonitor: MeetingEndMonitor.Configuration = MeetingEndMonitor.Configuration(),
        routeChangeBehavior: RouteChangeBehavior = .ask,
        autoTranscribe: Bool = true,
        transcribeWhileRecording: Bool = true,
        speakerCountHint: Int? = nil,
        diarization: Bool = false,
        deferTranscriptionWhenConstrained: Bool = true,
        autoDeleteEnabled: Bool = false,
        autoDeleteAfterDays: Int = 90,
        autoDeleteMode: RetentionMode = .audioOnly,
        deleteAudioAfterTranscription: Bool = false,
        deleteWithoutConfirmation: Bool = false,
        globalHotKey: HotKey = .defaultRecord,
        globalHotKeyEnabled: Bool = true,
        globalPauseHotKey: HotKey = .defaultPause,
        globalPauseHotKeyEnabled: Bool = true
    ) {
        self.launchAtLogin = launchAtLogin
        self.defaultLanguage = defaultLanguage
        self.recentLanguages = recentLanguages
        self.compressionBitrateKbps = compressionBitrateKbps
        self.keepUncompressedAudio = keepUncompressedAudio
        self.playbackChannels = playbackChannels
        self.onboardingCompleted = onboardingCompleted
        self.liveTranscript = liveTranscript
        self.liveTranscriptEngine = liveTranscriptEngine
        self.modelIdleMinutes = modelIdleMinutes
        self.defaultASRModelID = defaultASRModelID
        self.defaultDiarizerModelID = defaultDiarizerModelID
        self.providers = providers
        self.cloudSTTConfirmed = cloudSTTConfirmed
        self.defaultAIProviderID = defaultAIProviderID
        self.defaultAIModel = defaultAIModel
        self.summaryLanguage = summaryLanguage
        self.chatContextMessages = chatContextMessages
        self.ragEnabled = ragEnabled
        self.embeddingModelID = embeddingModelID
        self.embeddingAPIModel = embeddingAPIModel
        self.cloudEmbeddingConfirmed = cloudEmbeddingConfirmed
        self.micDeviceUID = micDeviceUID
        self.meetingDetection = meetingDetection
        self.perAppBehavior = perAppBehavior
        self.pillPositions = pillPositions
        self.showPill = showPill
        self.hidePillWhenAppActive = hidePillWhenAppActive
        self.attachPillToWindow = attachPillToWindow
        self.pillWindowOffset = pillWindowOffset
        self.pillSize = pillSize
        self.endMonitor = endMonitor
        self.routeChangeBehavior = routeChangeBehavior
        self.autoTranscribe = autoTranscribe
        self.transcribeWhileRecording = transcribeWhileRecording
        self.speakerCountHint = speakerCountHint
        self.diarization = diarization
        self.deferTranscriptionWhenConstrained = deferTranscriptionWhenConstrained
        self.autoDeleteEnabled = autoDeleteEnabled
        self.autoDeleteAfterDays = autoDeleteAfterDays
        self.autoDeleteMode = autoDeleteMode
        self.deleteAudioAfterTranscription = deleteAudioAfterTranscription
        self.deleteWithoutConfirmation = deleteWithoutConfirmation
        self.globalHotKey = globalHotKey
        self.globalHotKeyEnabled = globalHotKeyEnabled
        self.globalPauseHotKey = globalPauseHotKey
        self.globalPauseHotKeyEnabled = globalPauseHotKeyEnabled
    }

    public static let autoDeleteDaysRange = 1...3650
    public static let speakerCountHintRange = 1...20
    /// Chat context sizes offered in Settings, besides 0 (the whole chat).
    public static let chatContextOptions = [4, 10, 20, 50]
    /// Idle times offered in Settings; a hand-edited value is clamped to `modelIdleMinutesRange`.
    public static let modelIdleMinutesOptions = [1, 2, 5, 10]
    public static let modelIdleMinutesRange = 1...60

    private enum CodingKeys: String, CodingKey {
        case version, launchAtLogin, defaultLanguage, recentLanguages, compressionBitrateKbps, keepUncompressedAudio
        case playbackChannels, onboardingCompleted, liveTranscript, liveTranscriptEngine, modelIdleMinutes
        case defaultASRModelID, defaultDiarizerModelID, providers
        case cloudSTTConfirmed, defaultAIProviderID, defaultAIModel, summaryLanguage, chatContextMessages
        case ragEnabled, embeddingModelID, embeddingAPIModel, cloudEmbeddingConfirmed
        case micDeviceUID, meetingDetection, perAppBehavior, pillPositions, showPill, hidePillWhenAppActive
        case attachPillToWindow, pillWindowOffset, pillSize, endMonitor, routeChangeBehavior
        case autoTranscribe, transcribeWhileRecording, speakerCountHint, diarization, deferTranscriptionWhenConstrained
        case autoDeleteEnabled, autoDeleteAfterDays, autoDeleteMode, deleteAudioAfterTranscription
        case deleteWithoutConfirmation, globalHotKey, globalHotKeyEnabled, globalPauseHotKey, globalPauseHotKeyEnabled
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        version = try container.decodeIfPresent(Int.self, forKey: .version) ?? 1
        launchAtLogin = try container.decodeIfPresent(Bool.self, forKey: .launchAtLogin) ?? false
        defaultLanguage = try container.decodeIfPresent(String.self, forKey: .defaultLanguage) ?? "auto"
        recentLanguages = try container.decodeIfPresent([String].self, forKey: .recentLanguages) ?? []
        compressionBitrateKbps = try container.decodeIfPresent(Int.self, forKey: .compressionBitrateKbps) ?? 64
        keepUncompressedAudio = try container.decodeIfPresent(Bool.self, forKey: .keepUncompressedAudio) ?? false
        playbackChannels = try container.decodeIfPresent(PlaybackChannels.self, forKey: .playbackChannels) ?? .mixed
        onboardingCompleted = try container.decodeIfPresent(Bool.self, forKey: .onboardingCompleted) ?? false
        liveTranscript = try container.decodeIfPresent(Bool.self, forKey: .liveTranscript) ?? false
        liveTranscriptEngine = try container.decodeIfPresent(String.self, forKey: .liveTranscriptEngine)
            ?? Self.defaultLiveTranscriptEngine
        modelIdleMinutes = Self.clamped(
            try container.decodeIfPresent(Int.self, forKey: .modelIdleMinutes) ?? 2, to: Self.modelIdleMinutesRange)
        defaultASRModelID = try container.decodeIfPresent(String.self, forKey: .defaultASRModelID)
        defaultDiarizerModelID = try container.decodeIfPresent(String.self, forKey: .defaultDiarizerModelID)
        providers = try container.decodeIfPresent([ProviderConfig].self, forKey: .providers) ?? []
        cloudSTTConfirmed = try container.decodeIfPresent([String].self, forKey: .cloudSTTConfirmed) ?? []
        defaultAIProviderID = try container.decodeIfPresent(String.self, forKey: .defaultAIProviderID)
        defaultAIModel = try container.decodeIfPresent(String.self, forKey: .defaultAIModel)
        summaryLanguage = try container.decodeIfPresent(String.self, forKey: .summaryLanguage) ?? "auto"
        chatContextMessages = try container.decodeIfPresent(Int.self, forKey: .chatContextMessages) ?? 10
        ragEnabled = try container.decodeIfPresent(Bool.self, forKey: .ragEnabled) ?? false
        embeddingModelID = try container.decodeIfPresent(String.self, forKey: .embeddingModelID)
            ?? Self.defaultEmbeddingModelID
        embeddingAPIModel = try container.decodeIfPresent(String.self, forKey: .embeddingAPIModel) ?? ""
        cloudEmbeddingConfirmed = try container.decodeIfPresent([String].self, forKey: .cloudEmbeddingConfirmed) ?? []
        micDeviceUID = try container.decodeIfPresent(String.self, forKey: .micDeviceUID)
        meetingDetection = try container.decodeIfPresent(Bool.self, forKey: .meetingDetection) ?? true
        perAppBehavior = try container.decodeIfPresent([String: MeetingBehavior].self, forKey: .perAppBehavior) ?? [:]
        pillPositions = try container.decodeIfPresent([String: PillPosition].self, forKey: .pillPositions) ?? [:]
        showPill = try container.decodeIfPresent(Bool.self, forKey: .showPill) ?? true
        hidePillWhenAppActive = try container.decodeIfPresent(Bool.self, forKey: .hidePillWhenAppActive) ?? false
        attachPillToWindow = try container.decodeIfPresent(Bool.self, forKey: .attachPillToWindow) ?? false
        pillWindowOffset = try container.decodeIfPresent(PillPosition.self, forKey: .pillWindowOffset)
        pillSize = try container.decodeIfPresent(PillSize.self, forKey: .pillSize) ?? .medium
        endMonitor = try container.decodeIfPresent(MeetingEndMonitor.Configuration.self, forKey: .endMonitor)
            ?? MeetingEndMonitor.Configuration()
        routeChangeBehavior = try container.decodeIfPresent(RouteChangeBehavior.self, forKey: .routeChangeBehavior) ?? .ask
        autoTranscribe = try container.decodeIfPresent(Bool.self, forKey: .autoTranscribe) ?? true
        transcribeWhileRecording = try container.decodeIfPresent(Bool.self, forKey: .transcribeWhileRecording) ?? true
        speakerCountHint = try container.decodeIfPresent(Int.self, forKey: .speakerCountHint)
            .map { Self.clamped($0, to: Self.speakerCountHintRange) }
        diarization = try container.decodeIfPresent(Bool.self, forKey: .diarization) ?? false
        deferTranscriptionWhenConstrained = try container.decodeIfPresent(
            Bool.self, forKey: .deferTranscriptionWhenConstrained) ?? true
        autoDeleteEnabled = try container.decodeIfPresent(Bool.self, forKey: .autoDeleteEnabled) ?? false
        autoDeleteAfterDays = Self.clamped(
            try container.decodeIfPresent(Int.self, forKey: .autoDeleteAfterDays) ?? 90, to: Self.autoDeleteDaysRange)
        autoDeleteMode = try container.decodeIfPresent(RetentionMode.self, forKey: .autoDeleteMode) ?? .audioOnly
        deleteAudioAfterTranscription = try container.decodeIfPresent(
            Bool.self, forKey: .deleteAudioAfterTranscription) ?? false
        deleteWithoutConfirmation = try container.decodeIfPresent(Bool.self, forKey: .deleteWithoutConfirmation) ?? false
        // A shortcut without ⌘, ⌥ or ⌃ (a hand-edited file) would take a key away from typing everywhere.
        let hotKey = try container.decodeIfPresent(HotKey.self, forKey: .globalHotKey) ?? .defaultRecord
        globalHotKey = hotKey.hasCommandModifier ? hotKey : .defaultRecord
        globalHotKeyEnabled = try container.decodeIfPresent(Bool.self, forKey: .globalHotKeyEnabled) ?? true
        let pauseHotKey = try container.decodeIfPresent(HotKey.self, forKey: .globalPauseHotKey) ?? .defaultPause
        globalPauseHotKey = pauseHotKey.hasCommandModifier ? pauseHotKey : .defaultPause
        globalPauseHotKeyEnabled = try container.decodeIfPresent(Bool.self, forKey: .globalPauseHotKeyEnabled) ?? true
    }

    private static func clamped(_ value: Int, to range: ClosedRange<Int>) -> Int {
        min(max(value, range.lowerBound), range.upperBound)
    }
}

/// What a detected meeting app does.
public enum MeetingBehavior: String, Codable, Sendable {
    /// The pill asks "Record?".
    case ask
    /// Recording starts on its own (the pill still shows).
    case always
    /// No prompt.
    case ignore
}

/// What losing the recording microphone (AirPods disconnect) does; the mic moves to the default input either way.
public enum RouteChangeBehavior: String, Codable, Sendable {
    /// The pill asks to stop, with a countdown.
    case ask
    case `continue`
    case stop
}

/// How the transcript plays a recording's mic (left) and system (right) channels.
public enum PlaybackChannels: String, Codable, Sendable {
    /// Both voices in both ears.
    case mixed
    /// My voice in the left ear, the others in the right, as recorded.
    case split

    /// An unrecognized raw value (written by another build) falls back to `mixed`.
    public init(from decoder: Decoder) throws {
        self = PlaybackChannels(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .mixed
    }
}

/// What auto-delete moves to the Trash.
public enum RetentionMode: String, Codable, Sendable {
    /// The audio files only; transcript, summary and chat stay.
    case audioOnly
    /// The whole conversation folder.
    case wholeConversation

    /// An unrecognized raw value (written by another build) falls back to the less destructive `audioOnly`.
    public init(from decoder: Decoder) throws {
        self = RetentionMode(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .audioOnly
    }
}

/// A global keyboard shortcut in Carbon terms: a virtual key code (`kVK_*`) and a Carbon modifier mask
/// (`cmdKey` 1 << 8, `shiftKey` 1 << 9, `optionKey` 1 << 11, `controlKey` 1 << 12).
public struct HotKey: Codable, Equatable, Sendable {
    public var keyCode: UInt32
    public var modifiers: UInt32

    /// ⌥⌘R: `kVK_ANSI_R` with `optionKey | cmdKey`.
    public static let defaultRecord = HotKey(keyCode: 0x0F, modifiers: 1 << 11 | 1 << 8)
    /// ⌥⌘P: `kVK_ANSI_P` with `optionKey | cmdKey`.
    public static let defaultPause = HotKey(keyCode: 0x23, modifiers: 1 << 11 | 1 << 8)

    public init(keyCode: UInt32, modifiers: UInt32) {
        self.keyCode = keyCode
        self.modifiers = modifiers
    }

    /// Has ⌘, ⌥ or ⌃: a shortcut with ⇧ alone (or none) would take a key away from typing everywhere.
    public var hasCommandModifier: Bool {
        modifiers & (1 << 8 | 1 << 11 | 1 << 12) != 0
    }
}

/// How large the recording pill is drawn; `small` is the original size.
public enum PillSize: String, Codable, Sendable {
    case small
    case medium
    case large

    /// An unrecognized raw value (written by another build) falls back to `medium`.
    public init(from decoder: Decoder) throws {
        self = PillSize(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .medium
    }
}

/// The pill's top-right corner, in points from the top-right corner of its display's visible frame (x, y ≤ 0) or,
/// for `pillWindowOffset`, of the meeting window.
public struct PillPosition: Codable, Equatable, Sendable {
    public var x: Double
    public var y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }
}

/// The single place for macOS 26 feature gates. Call sites still need `if #available(macOS 26, *)` for the
/// compiler; these flags add the DEBUG `--force-fallbacks` override on top, so pre-26 paths can be checked on 26+.
public enum PlatformFeatures {
    public static let forceFallbacks: Bool = {
        #if DEBUG
        return ProcessInfo.processInfo.arguments.contains("--force-fallbacks")
        #else
        return false
        #endif
    }()

    public static var liquidGlass: Bool { macOS26 }
    public static var speechAnalyzer: Bool { macOS26 }
    public static var foundationModels: Bool { macOS26 }

    private static var macOS26: Bool {
        if #available(macOS 26, *) { return !forceFallbacks }
        return false
    }
}
