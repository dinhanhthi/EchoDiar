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
    /// Set by "Done" in the first-run onboarding window.
    public var onboardingCompleted: Bool
    /// Live captions while recording (opt-in); the batch transcript after Stop stays the source of truth.
    public var liveTranscript: Bool
    /// Catalog id of the live captions engine: a WhisperKit entry or the Nemotron streaming entry.
    public var liveTranscriptEngine: String
    /// Catalog id of the ASR engine for new transcriptions, or a cloud engine id (`cloud:<provider id>`); nil = the
    /// catalog default.
    public var defaultASRModelID: String?
    /// Configured AI providers; their API keys are in the Keychain, never here.
    public var providers: [ProviderConfig]
    /// Destinations (`<provider id>@<scheme://host[:port]>`) whose one-time "audio leaves this Mac" confirmation for
    /// cloud transcription was accepted; a changed base URL asks again (older bare provider ids no longer match).
    public var cloudSTTConfirmed: [String]

    public static let defaultLiveTranscriptEngine = "whisperkit-turbo-632"

    public init(
        launchAtLogin: Bool = false,
        defaultLanguage: String = "auto",
        recentLanguages: [String] = [],
        compressionBitrateKbps: Int = 64,
        keepUncompressedAudio: Bool = false,
        onboardingCompleted: Bool = false,
        liveTranscript: Bool = false,
        liveTranscriptEngine: String = AppSettings.defaultLiveTranscriptEngine,
        defaultASRModelID: String? = nil,
        providers: [ProviderConfig] = [],
        cloudSTTConfirmed: [String] = []
    ) {
        self.launchAtLogin = launchAtLogin
        self.defaultLanguage = defaultLanguage
        self.recentLanguages = recentLanguages
        self.compressionBitrateKbps = compressionBitrateKbps
        self.keepUncompressedAudio = keepUncompressedAudio
        self.onboardingCompleted = onboardingCompleted
        self.liveTranscript = liveTranscript
        self.liveTranscriptEngine = liveTranscriptEngine
        self.defaultASRModelID = defaultASRModelID
        self.providers = providers
        self.cloudSTTConfirmed = cloudSTTConfirmed
    }

    private enum CodingKeys: String, CodingKey {
        case version, launchAtLogin, defaultLanguage, recentLanguages, compressionBitrateKbps, keepUncompressedAudio
        case onboardingCompleted, liveTranscript, liveTranscriptEngine, defaultASRModelID, providers
        case cloudSTTConfirmed
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        version = try container.decodeIfPresent(Int.self, forKey: .version) ?? 1
        launchAtLogin = try container.decodeIfPresent(Bool.self, forKey: .launchAtLogin) ?? false
        defaultLanguage = try container.decodeIfPresent(String.self, forKey: .defaultLanguage) ?? "auto"
        recentLanguages = try container.decodeIfPresent([String].self, forKey: .recentLanguages) ?? []
        compressionBitrateKbps = try container.decodeIfPresent(Int.self, forKey: .compressionBitrateKbps) ?? 64
        keepUncompressedAudio = try container.decodeIfPresent(Bool.self, forKey: .keepUncompressedAudio) ?? false
        onboardingCompleted = try container.decodeIfPresent(Bool.self, forKey: .onboardingCompleted) ?? false
        liveTranscript = try container.decodeIfPresent(Bool.self, forKey: .liveTranscript) ?? false
        liveTranscriptEngine = try container.decodeIfPresent(String.self, forKey: .liveTranscriptEngine)
            ?? Self.defaultLiveTranscriptEngine
        defaultASRModelID = try container.decodeIfPresent(String.self, forKey: .defaultASRModelID)
        providers = try container.decodeIfPresent([ProviderConfig].self, forKey: .providers) ?? []
        cloudSTTConfirmed = try container.decodeIfPresent([String].self, forKey: .cloudSTTConfirmed) ?? []
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
