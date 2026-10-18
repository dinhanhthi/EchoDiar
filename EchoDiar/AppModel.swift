import EchoCore
import Foundation
import Observation

/// App-wide state shared by the main window, the settings window and the menu bar item.
@MainActor
@Observable
final class AppModel {
    enum Tab { case library, ai }

    private(set) var settings = AppSettings()
    var indicatorState: IndicatorState = .idle
    var selectedTab: Tab = .library
    var selectedConversationID: String?

    @ObservationIgnored private let store: SettingsStore

    init(store: SettingsStore = SettingsStore()) {
        self.store = store
    }

    /// Called once by the AppDelegate after the main window is built. Later phases dispatch the DEBUG harness
    /// launch args (`--transcribe`, `--download-model`, `--delete-model`) from here.
    func start() {
        settings = store.load()
    }

    /// Applies `change` and saves the settings file.
    func updateSettings(_ change: (inout AppSettings) -> Void) {
        var updated = settings
        change(&updated)
        guard updated != settings else { return }
        settings = updated
        do {
            try store.save(updated)
        } catch {
            NSLog("EchoDiar: could not save settings: \(error)")
        }
    }
}
