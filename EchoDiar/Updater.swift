import AppKit
import Observation
import os
import Sparkle

/// Sparkle updater, started only when `SUPublicEDKey` is a base64 Ed25519 public key (32 bytes); an empty or
/// malformed key would show a misconfiguration alert.
/// Background checks (at launch, then every `SUScheduledCheckInterval`) never pop Sparkle's dialog: with
/// `SUAutomaticallyUpdate` they download and prepare the update silently, then set `availableUpdateVersion`, which
/// shows the main window's Update button and the menu bar icon's download badge, and turns the menus' item into
/// "Install Update and Relaunch"; the button and the item install it and relaunch at once (Sparkle also installs it
/// when the app quits). An update Sparkle does not download silently (a major upgrade, an informational one) sets it
/// when Sparkle would show it, the item reads "Update to EchoDiar <version>…", and the button and the item open
/// Sparkle's dialog; it is cleared when that update session ends. The menus read the flags each time they open; the
/// toolbar observes `updateAvailable`, the menu bar item `onAvailableUpdateChange`.
@MainActor
@Observable
final class Updater: NSObject {
    private(set) var canCheck = false
    /// The update's version, set once a click can act on it, not when it is found: while it downloads, Sparkle's
    /// session is busy and `checkForUpdates` would do nothing.
    private(set) var availableUpdateVersion: String? { didSet { onAvailableUpdateChange?() } }
    var updateAvailable: Bool { availableUpdateVersion != nil }
    /// Called when `availableUpdateVersion` is set. Set by the menu bar item; without it Sparkle shows its own dialog.
    @ObservationIgnored var onAvailableUpdateChange: (() -> Void)?

    @ObservationIgnored private var controller: SPUStandardUpdaterController!
    /// Installs the silently downloaded update and relaunches the app; nil until it is ready.
    @ObservationIgnored private var installNow: (() -> Void)?

    override init() {
        super.init()
        controller = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: self, userDriverDelegate: self)
    }

    /// The app menu's and the menu bar menu's item for `checkForUpdates`.
    var menuTitle: String {
        if installNow != nil { return String(localized: "Install Update and Relaunch") }
        guard let availableUpdateVersion else { return String(localized: "Check for Updates…") }
        return String(localized: "Update to EchoDiar \(availableUpdateVersion)…")
    }

    func start() {
        #if DEBUG
        // VM check of the Update button.
        if ProcessInfo.processInfo.arguments.contains("--fake-update") { availableUpdateVersion = "9.9.9" }
        // Off unless asked: a Debug build left running downloads the release and keeps Sparkle's installer, which is
        // per bundle id, waiting for it to quit, so every other EchoDiar's update fails until then.
        guard ProcessInfo.processInfo.arguments.contains("--sparkle") else { return }
        #endif
        let key = (Bundle.main.object(forInfoDictionaryKey: "SUPublicEDKey") as? String ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard Data(base64Encoded: key)?.count == 32 else {
            Logger(subsystem: "dinh.thi.EchoDiar", category: "Updater")
                .notice("Updater off: SUPublicEDKey is not a base64 32-byte EdDSA public key")
            return
        }
        controller.startUpdater()
        canCheck = true
        if controller.updater.automaticallyChecksForUpdates { controller.updater.checkForUpdatesInBackground() }
    }

    /// Installs a downloaded update without any dialog, else Sparkle's check. Callers keep it from running while
    /// recording: the install quits and relaunches the app.
    func checkForUpdates() {
        if let installNow {
            installNow()
            return
        }
        NSApp.activate(ignoringOtherApps: true)
        controller.checkForUpdates(nil)
    }
}

extension Updater: SPUUpdaterDelegate {
    /// Taking the install stalls Sparkle's update cycle until the app relaunches, so no later check runs.
    func updater(_ updater: SPUUpdater, willInstallUpdateOnQuit item: SUAppcastItem,
                 immediateInstallationBlock immediateInstallHandler: @escaping () -> Void) -> Bool {
        installNow = immediateInstallHandler
        availableUpdateVersion = item.displayVersionString
        return true
    }

    func updaterDidNotFindUpdate(_ updater: SPUUpdater) {
        availableUpdateVersion = nil
    }

    func updater(_ updater: SPUUpdater, userDidMake choice: SPUUserUpdateChoice, forUpdate updateItem: SUAppcastItem,
                 state: SPUUserUpdateState) {
        if choice == .skip { availableUpdateVersion = nil }
    }
}

extension Updater: @preconcurrency SPUStandardUserDriverDelegate {
    var supportsGentleScheduledUpdateReminders: Bool { true }

    /// The menu bar badge, the Update button and the menu item stand in for Sparkle's dialog on background checks;
    /// Sparkle shows it when no menu bar item listens.
    func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem,
                                                              andInImmediateFocus immediateFocus: Bool) -> Bool {
        onAvailableUpdateChange == nil
    }

    /// An update found by a background check, which the badge, the Update button and the menu item show.
    func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem,
                                                   state: SPUUserUpdateState) {
        guard !handleShowingUpdate else { return }
        availableUpdateVersion = update.displayVersionString
    }

    /// After an install, a skip, "Remind Me Later" or an error. Silent downloads (`installNow`) run no such session.
    func standardUserDriverWillFinishUpdateSession() {
        if availableUpdateVersion != nil { availableUpdateVersion = nil }
    }
}
