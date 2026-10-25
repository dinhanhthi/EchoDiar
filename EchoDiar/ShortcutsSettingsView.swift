import AppKit
import EchoCore
import SwiftUI

/// The Shortcuts tab: the global record and pause shortcuts (recorder field, on/off, why it failed) and the in-app
/// shortcuts.
struct ShortcutsSettingsView: View {
    let model: AppModel

    /// The table of shortcuts used inside EchoDiar.
    static let inAppShortcuts: [(action: LocalizedStringResource, keys: String)] = [
        ("Record / Stop", "⌘R"),
        ("Pause / Resume", "⇧⌘P"),
        ("Show the recording", "⌘1"),
        ("Show the recordings list", "⌘2"),
        ("Open the chat of the open conversation", "⌘I"),
        ("Search conversations or settings", "⌘F"),
        ("Settings", "⌘,"),
        ("Back", "⌘[ / Esc"),
        ("Play / Pause the transcript audio", String(localized: "Space", comment: "The Space bar, in a shortcut such as ⌥⌘Space")),
    ]

    var body: some View {
        Form {
            Section {
                Toggle(isOn: model.setting(\.globalHotKeyEnabled)) {
                    Text("Global record shortcut")
                    Text("Starts or stops recording from any app.")
                }
                LabeledContent("Shortcut") {
                    HotKeyRecorder(
                        model: model, key: \.globalHotKey, defaultKey: .defaultRecord,
                        other: \.globalPauseHotKey, otherIsPause: true)
                }
                .disabled(!model.settings.globalHotKeyEnabled)
                if let error = model.hotKeyError {
                    errorLabel(error)
                }
            }
            Section {
                Toggle(isOn: model.setting(\.globalPauseHotKeyEnabled)) {
                    Text("Global pause shortcut")
                    Text("Pauses or resumes the recording from any app.")
                }
                LabeledContent("Shortcut") {
                    HotKeyRecorder(
                        model: model, key: \.globalPauseHotKey, defaultKey: .defaultPause,
                        other: \.globalHotKey, otherIsPause: false)
                }
                .disabled(!model.settings.globalPauseHotKeyEnabled)
                if let error = model.pauseHotKeyError {
                    errorLabel(error)
                }
            }
            Section("In EchoDiar") {
                ForEach(Self.inAppShortcuts, id: \.keys) { shortcut in
                    LabeledContent {
                        keysText(shortcut.keys).monospaced()
                    } label: {
                        Text(shortcut.action)
                    }
                }
            }
        }
        .formStyle(.grouped)
    }

    private func errorLabel(_ error: String) -> some View {
        Label(error, systemImage: "exclamationmark.triangle.fill")
            .font(.callout)
            .foregroundStyle(.yellow)
    }
}

/// The keys with a small gap after each modifier symbol, so "⇧⌘P" does not read as one glyph.
private func keysText(_ keys: String) -> Text {
    var text = AttributedString(keys)
    for index in text.characters.indices where "⌃⌥⇧⌘".contains(text.characters[index]) {
        text[index..<text.characters.index(after: index)].kern = 3
    }
    return Text(text)
}

/// Click, then press the new shortcut for `key`; Esc cancels. The registered shortcuts are off while recording, so
/// pressing one lands here instead of toggling a recording. One recorder records at a time.
private struct HotKeyRecorder: View {
    let model: AppModel
    let key: WritableKeyPath<AppSettings, HotKey>
    let defaultKey: HotKey
    /// The other global shortcut, which this one may not equal.
    let other: KeyPath<AppSettings, HotKey>
    /// The other one is the global pause shortcut (else the global record shortcut).
    let otherIsPause: Bool
    @State private var monitor: Any?
    @State private var note: LocalizedStringKey?

    var body: some View {
        VStack(alignment: .trailing, spacing: 4) {
            HStack(spacing: 6) {
                Button(action: toggle) {
                    Group {
                        if monitor == nil { keysText(model.settings[keyPath: key].displayString) } else { Text("Press a shortcut…") }
                    }
                    .monospaced()
                        .frame(minWidth: 120)
                }
                .help(monitor == nil ? "Click to record a new shortcut" : "Press Esc to cancel")
                .disabled(monitor == nil && model.isCapturingHotKey)
                if model.settings[keyPath: key] != defaultKey {
                    Button("Reset") { reset() }
                }
            }
            if let note {
                Text(note)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        // The monitor takes every key of the app: it stops when the user leaves the tab (kept alive, so no
        // onDisappear), the Settings page or the window.
        .onDisappear(perform: stop)
        .onChange(of: model.settingsTab) { stop() }
        .onChange(of: model.route) { stop() }
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
        let pressed = HotKey(keyCode: event.keyCode, flags: event.modifierFlags)
        guard pressed.hasCommandModifier else {
            note = "Use ⌘, ⌥ or ⌃ with the key."
            return
        }
        // A global shortcut takes the keys from every app: none of the standard menu shortcuts (⌘C, ⌘Q…).
        if let taken = Self.menuItem(for: pressed, in: NSApp.mainMenu) {
            note = "\(pressed.displayString) is the \"\(taken.title)\" menu shortcut."
            return
        }
        if pressed == model.settings[keyPath: other] {
            note = takenByOther(pressed)
            return
        }
        stop()
        model.updateSettings { $0[keyPath: key] = pressed }
    }

    /// Back to `defaultKey`, unless the other shortcut has taken it.
    private func reset() {
        guard defaultKey != model.settings[keyPath: other] else {
            note = takenByOther(defaultKey)
            return
        }
        note = nil
        model.updateSettings { $0[keyPath: key] = defaultKey }
    }

    /// "⇧⌘P is the global pause shortcut.", a whole sentence per shortcut.
    private func takenByOther(_ key: HotKey) -> LocalizedStringKey {
        otherIsPause ? "\(key.displayString) is the global pause shortcut."
            : "\(key.displayString) is the global record shortcut."
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
