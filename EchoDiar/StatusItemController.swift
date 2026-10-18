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

/// Menu bar item with a plain AppKit menu (no SwiftUI, for footprint). The menu is rebuilt each time it opens.
@MainActor
final class StatusItemController: NSObject, NSMenuDelegate {
    var state: IndicatorState = .idle { didSet { updateIcon() } }
    /// Enables Start/Stop Recording; the owner turns it on once recording exists.
    var recordingEnabled = false

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
        let record = item(active ? "Stop Recording" : "Start Recording", #selector(toggleRecording))
        record.isEnabled = recordingEnabled
        menu.addItem(record)
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

    private func item(_ title: String, _ action: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        return item
    }

    @objc private func openMain() { onOpenMain() }
    @objc private func toggleRecording() { onToggleRecording() }
    @objc private func openSettings() { onOpenSettings() }
    @objc private func about() { onAbout() }
    @objc private func terminate() { NSApp.terminate(nil) }

    #if DEBUG
    @objc private func simulateState(_ sender: NSMenuItem) {
        if let simulated = sender.representedObject as? IndicatorState { state = simulated }
    }
    #endif
}
