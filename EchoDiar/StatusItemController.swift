import AppKit

/// What the menu bar icon shows.
enum IndicatorState: CaseIterable {
    case idle, recording, paused, processing

    var title: String {
        switch self {
        case .idle: return "Idle"
        case .recording: return "Recording"
        case .paused: return "Paused"
        case .processing: return "Processing"
        }
    }

    var accessibilityLabel: String {
        switch self {
        case .idle: return "EchoDiar"
        case .recording: return "EchoDiar, recording"
        case .paused: return "EchoDiar, recording paused"
        case .processing: return "EchoDiar, processing"
        }
    }
}

/// Recording actions and live values for the menu, set by the app model so the controller never sees it.
struct RecordingMenuControls {
    var elapsed: @MainActor () -> TimeInterval
    var silentTapSuspected: @MainActor () -> Bool
    var toggleRecording: @MainActor () -> Void
    var togglePause: @MainActor () -> Void
    var stop: @MainActor () -> Void
    var language: @MainActor () -> String
    /// `LanguageOptions.groups` for the current language.
    var languageGroups: @MainActor () -> [[String]]
    var setLanguage: @MainActor (String) -> Void
    /// The detected meeting app's name ("Detected: Zoom"); nil when none.
    var detectedApp: @MainActor () -> String?
    var stopAskingForDetectedApp: @MainActor () -> Void
}

/// Menu bar item with a plain AppKit menu (no SwiftUI, for footprint). The menu is rebuilt each time it opens.
@MainActor
final class StatusItemController: NSObject, NSMenuDelegate {
    /// Set once at launch; the menu falls back to `onToggleRecording` without it.
    static var controls: RecordingMenuControls?

    var state: IndicatorState = .idle { didSet { updateIcon() } }
    /// Enables Start/Stop Recording.
    var recordingEnabled = true

    private let onOpenMain: () -> Void
    private let onOpenSettings: () -> Void
    private let onToggleRecording: () -> Void
    private let onAbout: () -> Void
    private let statusItem: NSStatusItem
    private let menu = NSMenu()
    private let symbolConfiguration = NSImage.SymbolConfiguration(pointSize: 15, weight: .regular)
    /// Shown only while processing: `NSStatusBarButton` exposes no image view to run a symbol effect on.
    private var effectView: NSImageView?

    init(onOpenMain: @escaping () -> Void, onOpenSettings: @escaping () -> Void,
         onToggleRecording: @escaping () -> Void, onAbout: @escaping () -> Void) {
        self.onOpenMain = onOpenMain
        self.onOpenSettings = onOpenSettings
        self.onToggleRecording = onToggleRecording
        self.onAbout = onAbout
        // Fixed width, so the item keeps its size while the button's own image is cleared for processing.
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        super.init()
        menu.delegate = self
        menu.autoenablesItems = false
        statusItem.menu = menu
        updateIcon()
    }

    private func updateIcon() {
        guard let button = statusItem.button else { return }
        button.setAccessibilityLabel(state.accessibilityLabel)
        switch state {
        case .idle:
            stopEffect()
            button.image = symbol("waveform", tint: nil)
        case .recording:
            stopEffect()
            button.image = symbol("record.circle.fill", tint: .systemRed)
        case .paused:
            stopEffect()
            button.image = symbol("pause.circle.fill", tint: .systemOrange)
        case .processing:
            button.image = nil
            startEffect(in: button)
        }
    }

    /// A template image when `tint` is nil, so it follows the menu bar's tint.
    private func symbol(_ name: String, tint: NSColor?) -> NSImage? {
        var configuration = symbolConfiguration
        if let tint { configuration = configuration.applying(.init(paletteColors: [tint])) }
        let image = NSImage(systemSymbolName: name, accessibilityDescription: nil)?
            .withSymbolConfiguration(configuration)
        image?.isTemplate = tint == nil
        return image
    }

    /// The system drives the animation; no timer runs.
    private func startEffect(in button: NSStatusBarButton) {
        guard effectView == nil else { return }
        let view = NSImageView(frame: button.bounds)
        view.autoresizingMask = [.width, .height]
        view.imageScaling = .scaleNone
        view.image = symbol("waveform", tint: nil)
        view.setAccessibilityElement(false)
        button.addSubview(view)
        view.addSymbolEffect(.variableColor.iterative, options: .repeating)
        effectView = view
    }

    private func stopEffect() {
        effectView?.removeAllSymbolEffects()
        effectView?.removeFromSuperview()
        effectView = nil
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        menu.addItem(item("Open EchoDiar", #selector(openMain)))
        let active = state == .recording || state == .paused
        if active, let controls = Self.controls {
            addRecordingItems(controls)
        } else {
            let record = item("Start Recording", #selector(toggleRecording))
            record.isEnabled = recordingEnabled
            menu.addItem(record)
        }
        if let controls = Self.controls, let app = controls.detectedApp() {
            let detected = NSMenuItem(title: "Detected: \(app)", action: nil, keyEquivalent: "")
            detected.isEnabled = false
            menu.addItem(detected)
            menu.addItem(item("Stop asking for this app", #selector(stopAskingForDetectedApp)))
            menu.addItem(.separator())
        }
        menu.addItem(item("Settings…", #selector(openSettings)))
        #if DEBUG
        let simulate = NSMenuItem(title: "Simulate state", action: nil, keyEquivalent: "")
        simulate.submenu = NSMenu()
        for candidate in IndicatorState.allCases {
            let option = item(candidate.title, #selector(simulateState(_:)))
            option.representedObject = candidate
            option.state = candidate == state ? .on : .off
            simulate.submenu?.addItem(option)
        }
        menu.addItem(simulate)
        #endif
        menu.addItem(.separator())
        menu.addItem(item("About EchoDiar", #selector(about)))
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "Quit", action: nil, keyEquivalent: "")
        quit.submenu = NSMenu()
        quit.submenu?.addItem(item("Sure to quit", #selector(terminate)))
        menu.addItem(quit)
    }

    /// Elapsed time, Pause/Resume, Stop, Language and the silent-tap fix. The values are read when the menu opens.
    private func addRecordingItems(_ controls: RecordingMenuControls) {
        let elapsed = controls.elapsed()
        let pattern: Duration.TimeFormatStyle.Pattern = elapsed >= 3_600 ? .hourMinuteSecond : .minuteSecond
        let time = Duration.seconds(elapsed).formatted(.time(pattern: pattern))
        let status = NSMenuItem(title: "\(state == .paused ? "Paused" : "Recording") \(time)", action: nil, keyEquivalent: "")
        status.isEnabled = false
        menu.addItem(status)
        menu.addItem(item(state == .paused ? "Resume" : "Pause", #selector(togglePause)))
        let stop = item("Stop Recording", #selector(stopRecording))
        stop.isEnabled = recordingEnabled
        menu.addItem(stop)
        menu.addItem(languageItem(controls))
        if controls.silentTapSuspected() {
            menu.addItem(item("System audio is silent — Fix…", #selector(fixSilentTap)))
        }
        menu.addItem(.separator())
    }

    /// A submenu in `LanguageOptions` order; picking a language adds a marker at the current recording time.
    private func languageItem(_ controls: RecordingMenuControls) -> NSMenuItem {
        let current = controls.language()
        let submenu = NSMenu()
        for (index, group) in controls.languageGroups().enumerated() {
            if index > 0 { submenu.addItem(.separator()) }
            for code in group {
                let option = item(LanguageOptions.name(code), #selector(setLanguage(_:)))
                option.representedObject = code
                option.state = code == current ? .on : .off
                submenu.addItem(option)
            }
        }
        let language = NSMenuItem(title: "Language", action: nil, keyEquivalent: "")
        language.submenu = submenu
        return language
    }

    private func item(_ title: String, _ action: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        return item
    }

    @objc private func openMain() { onOpenMain() }
    @objc private func toggleRecording() {
        if let controls = Self.controls { controls.toggleRecording() } else { onToggleRecording() }
    }
    @objc private func togglePause() { Self.controls?.togglePause() }
    @objc private func stopRecording() { Self.controls?.stop() }
    @objc private func setLanguage(_ sender: NSMenuItem) {
        if let code = sender.representedObject as? String { Self.controls?.setLanguage(code) }
    }
    @objc private func stopAskingForDetectedApp() { Self.controls?.stopAskingForDetectedApp() }
    @objc private func fixSilentTap() { PrivacySettings.openSystemAudioRecording() }
    @objc private func openSettings() { onOpenSettings() }
    @objc private func about() { onAbout() }
    @objc private func terminate() { NSApp.terminate(nil) }

    #if DEBUG
    @objc private func simulateState(_ sender: NSMenuItem) {
        if let simulated = sender.representedObject as? IndicatorState { state = simulated }
    }
    #endif
}
