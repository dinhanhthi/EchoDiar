import EchoCore
import Foundation
import Testing

/// Guards "key saved = available" for cloud providers and its one-time settings migration. Same rows as the Windows
/// `SettingsTests` (ProviderAvailability / MigratingProviderAvailability).
struct ProviderAvailabilityTests {
    static let customID = "3F2504E0-4F89-11D3-9A0C-0305E82C3301"

    /// A preset by id, or a custom OpenAI-compatible endpoint with `customID` for "custom".
    static func config(_ id: String, enabled: Bool, auth: ProviderConfig.AuthStyle? = nil, baseURL: String? = nil) -> ProviderConfig {
        var config = ProviderConfig.presets.first { $0.id == id } ?? ProviderConfig.custom(baseURL: "https://h/v1")
        if id == "custom" || id == customID { config.id = customID }
        config.enabled = enabled
        if let auth { config.authStyle = auth }
        if let baseURL { config.baseURL = baseURL }
        return config
    }

    // (provider, enabled, auth override, baseURL override, hasKey, isCloud, isAvailable)
    @Test(arguments: [
        // Cloud preset: the key decides, `enabled` does not.
        ("openai", false, nil, nil, true, true, true),
        ("openai", true, nil, nil, false, true, false),
        ("openai", false, nil, nil, false, true, false),
        ("anthropic", true, nil, nil, false, true, false),
        ("deepgram", false, nil, nil, true, true, true),
        ("deepgram", true, nil, nil, false, true, false),
        // Custom endpoint with auth: the key decides.
        ("custom", false, nil, nil, true, true, true),
        ("custom", true, nil, nil, false, true, false),
        // Custom endpoint without auth: the base URL decides, the key does not.
        ("custom", false, ProviderConfig.AuthStyle.none, "https://h/v1", false, true, true),
        ("custom", false, ProviderConfig.AuthStyle.none, "https://h/v1", true, true, true),
        ("custom", true, ProviderConfig.AuthStyle.none, "", true, true, false),
        ("custom", true, ProviderConfig.AuthStyle.none, "  \n", true, true, false),
        // Local server and CLI: `enabled` decides, the key does not.
        ("ollama", true, nil, nil, false, false, true),
        ("ollama", false, nil, nil, true, false, false),
        ("claude-cli", true, nil, nil, false, false, true),
        ("claude-cli", false, nil, nil, true, false, false),
    ] as [(String, Bool, ProviderConfig.AuthStyle?, String?, Bool, Bool, Bool)])
    func availability(
        id: String, enabled: Bool, auth: ProviderConfig.AuthStyle?, baseURL: String?, hasKey: Bool, cloud: Bool,
        available: Bool
    ) {
        let config = Self.config(id, enabled: enabled, auth: auth, baseURL: baseURL)
        #expect(config.isCloud == cloud)
        #expect(config.isAvailable(hasKey: hasKey) == available)
    }

    /// Every reference points at `id`; `saved` nil = the provider was never saved to `providers` (its preset applies).
    // (provider, saved config enabled (nil = not saved), references cleared and id excluded)
    @Test(arguments: [
        ("openai", false, true),
        ("openai", true, false),
        ("deepgram", nil, true),
        ("deepgram", false, true),
        ("deepgram", true, false),
        (customID, false, true),
        (customID, true, false),
        ("ollama", false, false),
        ("claude-cli", false, false),
    ] as [(String, Bool?, Bool)])
    func migration(id: String, savedEnabled: Bool?, cleared: Bool) {
        let settings = AppSettings(
            defaultASRModelID: "cloud:" + id, defaultDiarizerModelID: "cloud:" + id,
            providers: savedEnabled.map { [Self.config(id, enabled: $0)] } ?? [],
            defaultAIProviderID: id, catchUpProviderID: id, embeddingModelID: "api:" + id,
            providerAvailabilityMigrated: false)

        let migrated = settings.migratingProviderAvailability()

        #expect(migrated.providerAvailabilityMigrated)
        #expect(migrated.autoExcludedProviderIDs.contains(id) == cleared)
        #expect(migrated.defaultASRModelID == (cleared ? nil : "cloud:" + id))
        #expect(migrated.defaultDiarizerModelID == (cleared ? nil : "cloud:" + id))
        #expect(migrated.defaultAIProviderID == (cleared ? nil : id))
        #expect(migrated.catchUpProviderID == (cleared ? "" : id))
        #expect(migrated.embeddingModelID == (cleared ? AppSettings.defaultEmbeddingModelID : "api:" + id))
        #expect(migrated.providers == settings.providers)
    }

    @Test func migrationRunsOnce() {
        let settings = AppSettings(
            defaultASRModelID: "cloud:openai", providers: [Self.config("openai", enabled: false)],
            catchUpProviderID: "openai", providerAvailabilityMigrated: false)
        let once = settings.migratingProviderAvailability()
        #expect(once.migratingProviderAvailability() == once)

        // Already migrated (a fresh install): a disabled cloud config keeps its references.
        var fresh = settings
        fresh.providerAvailabilityMigrated = true
        #expect(fresh.migratingProviderAvailability() == fresh)
    }

    @Test func decodingSetsTheMigrationFlag() throws {
        #expect(AppSettings().providerAvailabilityMigrated)
        #expect(try !JSONDecoder().decode(AppSettings.self, from: Data(#"{"version":1}"#.utf8)).providerAvailabilityMigrated)

        let migrated = AppSettings(providerAvailabilityMigrated: false).migratingProviderAvailability()
        let decoded = try JSONDecoder().decode(AppSettings.self, from: JSONEncoder().encode(migrated))
        #expect(decoded == migrated)
        #expect(decoded.providerAvailabilityMigrated)
        #expect(decoded.autoExcludedProviderIDs.contains("openai"))
    }
}
