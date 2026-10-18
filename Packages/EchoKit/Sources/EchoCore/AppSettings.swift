import Foundation

/// The settings file format, version 1. Every key is optional when read, so files written by older builds
/// still decode; new fields must follow the same `decodeIfPresent` + default rule.
public struct AppSettings: Codable, Equatable, Sendable {
    public var version = 1
    public var launchAtLogin: Bool
    /// A language code, or "auto" to detect it.
    public var defaultLanguage: String

    public init(launchAtLogin: Bool = false, defaultLanguage: String = "auto") {
        self.launchAtLogin = launchAtLogin
        self.defaultLanguage = defaultLanguage
    }

    private enum CodingKeys: String, CodingKey { case version, launchAtLogin, defaultLanguage }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        version = try container.decodeIfPresent(Int.self, forKey: .version) ?? 1
        launchAtLogin = try container.decodeIfPresent(Bool.self, forKey: .launchAtLogin) ?? false
        defaultLanguage = try container.decodeIfPresent(String.self, forKey: .defaultLanguage) ?? "auto"
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
