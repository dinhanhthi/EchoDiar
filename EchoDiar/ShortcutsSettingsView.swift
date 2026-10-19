import AppKit
import EchoCore
import SwiftUI

/// The Shortcuts tab: the global record shortcut (recorder field, on/off, why it failed) and the in-app shortcuts.
struct ShortcutsSettingsView: View {
    let model: AppModel

    /// The table of shortcuts used inside EchoDiar.
    static let inAppShortcuts: [(action: String, keys: String)] = [
        ("Record / Stop", "⌘R"),
        ("Pause / Resume", "⇧⌘P"),
        ("Show AI / Library", "⌘I"),
        ("Search conversations", "⌘F"),
        ("Settings", "⌘,"),
        ("Back to the library", "Esc"),
        ("Play / Pause the transcript audio", "Space"),
    ]

    var body: some View {
        Form {
            Section {
                Toggle(isOn: model.setting(\.globalHotKeyEnabled)) {
                    Text("Global record shortcut")
                    Text("Starts or stops recording from any app.")
                }
                LabeledContent("Shortcut") {
                    HotKeyRecorder(model: model)
                }
                .disabled(!model.settings.globalHotKeyEnabled)
                if let error = model.hotKeyError {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.callout)
                        .foregroundStyle(.yellow)
                }
            }
            Section("In EchoDiar") {
                ForEach(Self.inAppShortcuts, id: \.action) { shortcut in
                    LabeledContent(shortcut.action) {
                        Text(shortcut.keys).monospaced()
                    }
                }
            }
        }
        .formStyle(.grouped)
    }
}

/// Click, then press the new shortcut; Esc cancels. The registered shortcut is off while recording, so pressing it
/// lands here instead of toggling a recording.
private struct HotKeyRecorder: View {
    let model: AppModel
    @State private var monitor: Any?
    @State private var note: String?

    var body: some View {
        VStack(alignment: .trailing, spacing: 4) {
            HStack(spacing: 6) {
                Button(action: toggle) {
                    Text(monitor == nil ? model.settings.globalHotKey.displayString : "Press a shortcut…")
                        .monospaced()
                        .frame(minWidth: 120)
                }
                .help(monitor == nil ? "Click to record a new shortcut" : "Press Esc to cancel")
                if model.settings.globalHotKey != .defaultRecord {
                    Button("Reset") { model.updateSettings { $0.globalHotKey = .defaultRecord } }
                }
            }
            if let note {
                Text(note)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        // The monitor takes every key of the app: it stops when the user leaves the tab or the window.
        .onDisappear(perform: stop)
        .onChange(of: model.settingsTab) { stop() }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didResignKeyNotification)) { _ in stop() }
    }

    private func toggle() {
        if monitor != nil {
            stop()
            return
        }
        note = nil
        model.setHotKeyCapture(true)
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            MainActor.assumeIsolated { handle(event) }
            return nil
        }
    }

    private func handle(_ event: NSEvent) {
        if event.keyCode == 53 {  // kVK_Escape
            stop()
            return
        }
        let key = HotKey(keyCode: event.keyCode, flags: event.modifierFlags)
        guard key.hasCommandModifier else {
            note = "Use ⌘, ⌥ or ⌃ with the key."
            return
        }
        // A global shortcut takes the keys from every app: none of the standard menu shortcuts (⌘C, ⌘Q…).
        if let taken = Self.menuItem(for: key, in: NSApp.mainMenu) {
            note = "\(key.displayString) is the \"\(taken.title)\" menu shortcut."
            return
        }
        stop()
        model.updateSettings { $0.globalHotKey = key }
    }

    /// The main menu item (submenus included) with the same shortcut.
    private static func menuItem(for key: HotKey, in menu: NSMenu?) -> NSMenuItem? {
        for item in menu?.items ?? [] {
            if let found = menuItem(for: key, in: item.submenu) { return found }
            guard !item.keyEquivalent.isEmpty else { continue }
            var flags = item.keyEquivalentModifierMask
            // An uppercase key equivalent implies ⇧.
            if item.keyEquivalent != item.keyEquivalent.lowercased() { flags.insert(.shift) }
            let shortcut = HotKey(keyCode: 0, flags: flags).modifierSymbols + item.keyEquivalent.uppercased()
            if shortcut == key.displayString { return item }
        }
        return nil
    }

    private func stop() {
        guard let monitor else { return }
        NSEvent.removeMonitor(monitor)
        self.monitor = nil
        model.setHotKeyCapture(false)
    }
}
