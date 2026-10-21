import AppKit
import Observation
import os
import Sparkle

/// Sparkle updater, started only when `SUPublicEDKey` is a base64 Ed25519 public key (32 bytes); an empty or
/// malformed key would show a misconfiguration alert.
/// Background checks (at launch, then every `SUScheduledCheckInterval`) never pop Sparkle's dialog: with
/// `SUAutomaticallyUpdate` they download and prepare the update silently, then set `updateAvailable`, which shows the
/// main window's Update button and turns the menus' item into "Install Update and Relaunch"; both install it and
/// relaunch at once (Sparkle also installs it when the app quits). An update Sparkle does not download silently (a
/// major upgrade, an informational one) sets the flag when Sparkle would show it, and the button opens Sparkle's dialog.
/// The menus read the flags each time they open; the toolbar observes `updateAvailable`.
@MainActor
@Observable
final class Updater: NSObject {
    private(set) var canCheck = false
    /// Set once a click can act on the update, not when it is found: while it downloads, Sparkle's session is busy
    /// and `checkForUpdates` would do nothing.
    private(set) var updateAvailable = false

    @ObservationIgnored private var controller: SPUStandardUpdaterController!
    /// Installs the silently downloaded update and relaunches the app; nil until it is ready.
    @ObservationIgnored private var installNow: (() -> Void)?

    override init() {
        super.init()
        controller = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: self, userDriverDelegate: self)
    }

    /// The app menu's and the menu bar menu's item for `checkForUpdates`.
    var menuTitle: String {
        if installNow != nil { return "Install Update and Relaunch" }
        return updateAvailable ? "Update Available…" : "Check for Updates…"
    }

    func start() {
        #if DEBUG
        // VM check of the Update button.
        if ProcessInfo.processInfo.arguments.contains("--fake-update") { updateAvailable = true }
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
        updateAvailable = true
        return true
    }

    func updaterDidNotFindUpdate(_ updater: SPUUpdater) {
        updateAvailable = false
    }

    func updater(_ updater: SPUUpdater, userDidMake choice: SPUUserUpdateChoice, forUpdate updateItem: SUAppcastItem,
                 state: SPUUserUpdateState) {
        if choice == .skip { updateAvailable = false }
    }
}

extension Updater: @preconcurrency SPUStandardUserDriverDelegate {
    var supportsGentleScheduledUpdateReminders: Bool { true }

    /// The Update button and the menu item stand in for Sparkle's dialog on background checks.
    func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem,
                                                              andInImmediateFocus immediateFocus: Bool) -> Bool {
        false
    }

    /// An update shown in Sparkle's dialog, or on a background check by the Update button.
    func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem,
                                                   state: SPUUserUpdateState) {
        updateAvailable = true
    }
}
