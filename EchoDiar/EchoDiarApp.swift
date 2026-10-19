import AppKit
import EchoAudio
import SwiftUI

/// AppKit entry point: no SwiftUI `App`, so SwiftUI is only loaded inside the windows.
@main
@MainActor
enum EchoDiarApp {
    // NSApplication.delegate is weak; keep the delegate alive for the app lifetime.
    private static let delegate = AppDelegate()

    static func main() {
        let app = NSApplication.shared
        app.delegate = delegate
        app.run()
    }
}

/// Owns the app state for the app lifetime. Under XCTest it sets nothing up.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuItemValidation {
    let model = AppModel()

    private var statusItem: StatusItemController?
    private var window: NSWindow?
    private var sigtermSource: DispatchSourceSignal?
    /// Crash recovery of interrupted sessions; capture must not start before it finishes.
    private(set) var recovery: Task<[RecoveredResult], Never>?

    private var isTesting: Bool { ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil }

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard !isTesting else { return }
        #if DEBUG
        if CaptureProbe.isRequested {
            CaptureProbe.run()
            return
        }
        #endif
        recovery = Task.detached(priority: .background) { SessionRecovery.recover() }
        // Dark only.
        NSApp.appearance = NSAppearance(named: .darkAqua)
        NSApp.mainMenu = makeMainMenu()
        statusItem = StatusItemController(
            onOpenMain: { [weak self] in self?.showMainWindow() },
            onOpenSettings: { [weak self] in self?.showSettings() },
            onToggleRecording: {},  // Recording arrives in Phase 3.
            onAbout: { [weak self] in self?.showAbout() })
        observeIndicatorState()
        // SIGTERM (`killall`, logout) goes through terminate, so applicationWillTerminate runs.
        signal(SIGTERM, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        source.setEventHandler { MainActor.assumeIsolated { NSApp.terminate(nil) } }
        source.resume()
        sigtermSource = source
        // A login-item launch (or a scripted `--background` start) stays in the menu bar.
        let event = NSAppleEventManager.shared().currentAppleEvent
        let loginLaunch = event?.eventID == AEEventID(kAEOpenApplication)
            && event?.paramDescriptor(forKeyword: AEKeyword(keyAEPropData))?.enumCodeValue == OSType(keyAELaunchedAsLogInItem)
        if !loginLaunch && !ProcessInfo.processInfo.arguments.contains("--background") {
            showMainWindow()
        }
        model.start()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showMainWindow()
        return true
    }

    /// Mirrors `model.indicatorState` on the menu bar icon; re-armed after each change, nothing polls.
    private func observeIndicatorState() {
        withObservationTracking {
            statusItem?.state = model.indicatorState
        } onChange: { [weak self] in
            // onChange fires before the new value is stored; read it on the next turn.
            Task { @MainActor in self?.observeIndicatorState() }
        }
    }

    func showMainWindow() {
        if window == nil {
            // MainView (task 1.7) is expected as `struct MainView: View { init(model: AppModel) }`.
            let hosting = NSHostingController(rootView: MainView(model: model))
            // The SwiftUI toolbar (Record, Settings, AI) becomes the window's toolbar, in the traffic-light row.
            hosting.sceneBridgingOptions = [.toolbars]
            let window = NSWindow(contentViewController: hosting)
            window.title = "EchoDiar"
            window.titleVisibility = .hidden
            window.toolbarStyle = .unified
            // One background for the toolbar and the content, without a line between them.
            window.titlebarAppearsTransparent = true
            window.titlebarSeparatorStyle = .none
            // Full size content: the translucent background runs under the toolbar too.
            window.styleMask = [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView]
            window.isReleasedWhenClosed = false
            if !window.setFrameAutosaveName("Main") || !window.setFrameUsingName("Main") {
                window.center()
            }
            window.delegate = self
            self.window = window
        }
        bringToFront(window)
    }

    /// SettingsWindow (task 1.8) is expected as a `@MainActor final class` with `static let shared` and
    /// `func show(model: AppModel)` that creates its window once and brings it to the front.
    @objc func showSettings() {
        SettingsWindow.shared.show(model: model)
    }

    /// Shows the Dock icon and activates the app on the next turn (the `.regular` switch from LSUIElement is
    /// not effective before), then fronts `window`. On macOS 14+ a plain `activate()` is denied on a fresh launch
    /// from LSUIElement; the `ignoringOtherApps:` form is still honored.
    private func bringToFront(_ window: NSWindow?) {
        NSApp.setActivationPolicy(.regular)
        Task { @MainActor in
            NSApp.activate(ignoringOtherApps: true)
            window?.makeKeyAndOrderFront(nil)
            window?.orderFrontRegardless()
        }
    }

    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow, closing === window else { return }
        // The next window opens without the search field focused.
        model.searchPresented = false
        // Release the window and its SwiftUI hierarchy after it finishes closing; it is recreated on the next open.
        Task { @MainActor [weak self] in
            self?.window = nil
            let otherWindowOpen = NSApp.windows.contains {
                $0 !== closing && $0.isVisible && $0.styleMask.contains(.titled)
            }
            if !otherWindowOpen { NSApp.setActivationPolicy(.accessory) }
        }
    }

    @objc func showAbout() {
        if window == nil {
            // No main window: drop the Dock icon again once the user leaves the app.
            NotificationCenter.default.addObserver(self, selector: #selector(resignedActiveAfterAbout),
                                                   name: NSApplication.didResignActiveNotification, object: nil)
        }
        bringToFront(nil)
        let logo = AboutInfo.logo
        // Queued after bringToFront's activation. Version and copyright come from the Info.plist.
        Task { @MainActor in
            NSApp.orderFrontStandardAboutPanel(options: [.applicationIcon: logo as Any, .credits: Self.aboutCredits])
        }
    }

    @objc private func resignedActiveAfterAbout() {
        NotificationCenter.default.removeObserver(self, name: NSApplication.didResignActiveNotification, object: nil)
        if window == nil { NSApp.setActivationPolicy(.accessory) }
    }

    /// "Licensed under AGPL-3.0" and the GitHub link, centered under the version.
    private static var aboutCredits: NSAttributedString {
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: NSFont.smallSystemFontSize), .paragraphStyle: paragraph,
            .foregroundColor: NSColor.secondaryLabelColor,
        ]
        let credits = NSMutableAttributedString(string: "Licensed under AGPL-3.0\n", attributes: attributes)
        var link = attributes
        link[.link] = AboutInfo.repository
        credits.append(NSAttributedString(string: "github.com/dinhanhthi/EchoDiar", attributes: link))
        return credits
    }

    // MARK: - Main menu actions (the menu owns ⌘R, ⇧⌘P, ⌘I and ⌘F, so they work from every window)

    @objc private func toggleRecording() { model.toggleRecording() }

    @objc private func togglePause() { model.togglePause() }

    /// A hidden main window opens on the AI pane.
    @objc private func toggleAI() {
        let shown = window?.isVisible == true
        showMainWindow()
        model.selectedTab = shown && model.selectedTab == .ai ? .library : .ai
    }

    @objc private func showSearch() {
        showMainWindow()
        model.showSearch()
    }

    /// Titles follow the recording and the shown pane.
    func validateMenuItem(_ item: NSMenuItem) -> Bool {
        switch item.action {
        case #selector(toggleRecording):
            item.title = model.isRecording ? "Stop Recording" : "Start Recording"
            return !model.isStarting
        case #selector(togglePause):
            item.title = model.snapshot.state == .paused ? "Resume Recording" : "Pause Recording"
            return model.isRecording
        case #selector(toggleAI):
            item.title = model.selectedTab == .ai && window?.isVisible == true ? "Show Library" : "Show AI"
            return true
        default:
            return true
        }
    }

    /// App menu (About, Settings, Hide, Quit), File (recording), Edit, View (AI, search) and Window menus.
    private func makeMainMenu() -> NSMenu {
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "About EchoDiar", action: #selector(showAbout), keyEquivalent: "").target = self
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Settings…", action: #selector(showSettings), keyEquivalent: ",").target = self
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Hide EchoDiar", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Quit EchoDiar", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        let fileMenu = NSMenu(title: "File")
        fileMenu.addItem(withTitle: "Start Recording", action: #selector(toggleRecording), keyEquivalent: "r").target = self
        fileMenu.addItem(withTitle: "Pause Recording", action: #selector(togglePause), keyEquivalent: "P").target = self
        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        let viewMenu = NSMenu(title: "View")
        viewMenu.addItem(withTitle: "Show AI", action: #selector(toggleAI), keyEquivalent: "i").target = self
        viewMenu.addItem(withTitle: "Search Conversations", action: #selector(showSearch), keyEquivalent: "f").target = self
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowMenu.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        NSApp.windowsMenu = windowMenu
        let mainMenu = NSMenu()
        for submenu in [appMenu, fileMenu, editMenu, viewMenu, windowMenu] {
            let item = NSMenuItem()
            item.submenu = submenu
            mainMenu.addItem(item)
        }
        return mainMenu
    }
}
