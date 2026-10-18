import EchoCore
import Foundation
import Testing

/// Guards that one stale or malformed provider entry cannot fail the whole settings decode (which resets settings).
struct ProviderConfigDecodingTests {
    @Test func encodedPresetsRoundTrip() throws {
        let data = try JSONEncoder().encode(ProviderConfig.presets)
        #expect(try JSONDecoder().decode([ProviderConfig].self, from: data) == ProviderConfig.presets)
    }

    @Test func missingMalformedAndUnknownValuesFallBackToDefaults() throws {
        let json = #"""
        [
          {"id": "only-id"},
          {"id": "future", "kind": "quantum", "authStyle": "oauth3", "enabled": "yes", "extraHeaders": [1, 2],
           "displayName": 42, "baseURL": "https://h/v1", "defaultModel": "m", "newField": {"x": 1}},
          {"id": "anthropic", "kind": "anthropic", "preset": "anthropic", "displayName": "A", "baseURL": "https://a",
           "authStyle": "xApiKey", "extraHeaders": {"h": "v"}, "defaultModel": "m", "enabled": true}
        ]
        """#
        let configs = try JSONDecoder().decode([ProviderConfig].self, from: Data(json.utf8))

        #expect(configs[0] == ProviderConfig(id: "only-id", kind: .openAICompatible, preset: nil, displayName: "only-id", baseURL: "", authStyle: .none))
        #expect(configs[1] == ProviderConfig(
            id: "future", kind: .openAICompatible, preset: nil, displayName: "future", baseURL: "https://h/v1",
            authStyle: .none, defaultModel: "m"))
        #expect(configs[2] == ProviderConfig(
            id: "anthropic", kind: .anthropic, preset: "anthropic", displayName: "A", baseURL: "https://a",
            authStyle: .xApiKey, extraHeaders: ["h": "v"], defaultModel: "m", enabled: true))
    }

    @Test func entryWithoutIdStillFails() {
        #expect(throws: DecodingError.self) {
            try JSONDecoder().decode(ProviderConfig.self, from: Data(#"{"kind": "local"}"#.utf8))
        }
    }

    @Test func settingsKeepOtherFieldsWhenAProviderIsMalformed() throws {
        let json = #"{"launchAtLogin": true, "providers": [{"id": "x", "kind": 7}]}"#
        let settings = try JSONDecoder().decode(AppSettings.self, from: Data(json.utf8))

        #expect(settings.launchAtLogin)
        #expect(settings.providers.map(\.id) == ["x"])
    }
}
