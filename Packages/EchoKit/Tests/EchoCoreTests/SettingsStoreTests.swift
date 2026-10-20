import EchoCore
import Foundation
import Testing

/// Guards the settings.json format contract. Every test uses its own temporary folder, never Application Support.
struct SettingsStoreTests {
    @Test func saveThenLoadRoundTrips() throws {
        try withTemporaryStore { store in
            let settings = AppSettings(
                launchAtLogin: true, defaultLanguage: "fr", pillWindowOffset: PillPosition(x: -40, y: -12))
            try store.save(settings)
            #expect(store.load() == settings)
        }
    }

    @Test(arguments: [#"{"version":1}"#, "{}"])
    func fileMissingNewerKeysDecodesWithDefaults(json: String) throws {
        try withTemporaryStore { store in
            try Data(json.utf8).write(to: store.fileURL)
            #expect(store.load() == AppSettings())
            #expect(FileManager.default.fileExists(atPath: store.fileURL.path))
        }
    }

    /// Playback plays both voices in both ears unless the user asked to split them.
    @Test(arguments: [#"{}"#, #"{"playbackChannels":"surround"}"#])
    func playbackChannelsDefaultsToMixed(json: String) throws {
        let settings = try JSONDecoder().decode(AppSettings.self, from: Data(json.utf8))
        #expect(settings.playbackChannels == .mixed)
    }

    @Test func playbackChannelsSplitRoundTrips() throws {
        let data = try JSONEncoder().encode(AppSettings(playbackChannels: .split))
        #expect(try JSONDecoder().decode(AppSettings.self, from: data).playbackChannels == .split)
    }

    @Test func corruptFileFallsBackToDefaultsAndIsMovedAside() throws {
        try withTemporaryStore { store in
            let corrupt = Data("not json {".utf8)
            try corrupt.write(to: store.fileURL)

            #expect(store.load() == AppSettings())

            #expect(!FileManager.default.fileExists(atPath: store.fileURL.path))
            let names = try FileManager.default.contentsOfDirectory(atPath: store.directory.path)
            let aside = names.filter { $0.hasPrefix("settings.json.unreadable-") }
            #expect(aside.count == 1)
            let moved = try #require(aside.first)
            #expect(try Data(contentsOf: store.directory.appendingPathComponent(moved)) == corrupt)
        }
    }

    /// Runs `body` with a store in a fresh `tmp/<UUID>` folder, removed afterwards.
    private func withTemporaryStore(_ body: (SettingsStore) throws -> Void) throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try body(SettingsStore(directory: directory))
    }
}
