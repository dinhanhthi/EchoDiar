import AppKit
import EchoCore
import ServiceManagement
import SwiftUI

/// The Settings window: created once, reused on every open. Later phases add tabs to `SettingsView`.
@MainActor
final class SettingsWindow: NSObject, NSWindowDelegate {
    static let shared = SettingsWindow()

    private var window: NSWindow?

    func show(model: AppModel) {
        if window == nil {
            let window = NSWindow(contentViewController: NSHostingController(rootView: SettingsView(model: model)))
            window.title = "Settings"
            window.styleMask = [.titled, .closable]
            window.isReleasedWhenClosed = false
            if !window.setFrameAutosaveName("Settings") || !window.setFrameUsingName("Settings") {
                window.center()
            }
            window.delegate = self
            self.window = window
        }
        // Same as the main window: a Dock icon while open, so Settings does not open behind other apps.
        NSApp.setActivationPolicy(.regular)
        Task { @MainActor [window] in
            NSApp.activate(ignoringOtherApps: true)
            window?.makeKeyAndOrderFront(nil)
            window?.orderFrontRegardless()
        }
    }

    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow else { return }
        Task { @MainActor in
            let otherWindowOpen = NSApp.windows.contains {
                $0 !== closing && $0.isVisible && $0.styleMask.contains(.titled)
            }
            if !otherWindowOpen { NSApp.setActivationPolicy(.accessory) }
        }
    }
}

enum SettingsTab: Hashable {
    case general, transcription
}

private struct SettingsView: View {
    @Bindable var model: AppModel

    var body: some View {
        TabView(selection: $model.settingsTab) {
            GeneralSettingsTab(model: model)
                .tabItem { Label("General", systemImage: "gearshape") }
                .tag(SettingsTab.general)
            ModelsSettingsView(model: model)
                .modifier(GlassForm())
                .tabItem { Label("Transcription", systemImage: "waveform") }
                .tag(SettingsTab.transcription)
        }
        .frame(width: 560, height: 480)
    }
}

private struct GeneralSettingsTab: View {
    let model: AppModel
    @State private var loginItemNote: String?

    /// Auto first, then the first-class content languages. The full list arrives in Phase 5.
    private static let languages: [(code: String, name: String)] = [
        ("auto", "Auto"),
        ("en", "English"), ("fr", "French"), ("vi", "Vietnamese"),
        ("de", "German"), ("es", "Spanish"), ("it", "Italian"), ("pt", "Portuguese"),
        ("ja", "Japanese"), ("zh", "Chinese"),
    ]

    var body: some View {
        Form {
            Section {
                Toggle("Launch at login", isOn: launchAtLogin)
                if let loginItemNote {
                    Text(loginItemNote)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            Section {
                Picker("Default language", selection: defaultLanguage) {
                    ForEach(Self.languages, id: \.code) { Text($0.name).tag($0.code) }
                }
            }
        }
        .formStyle(.grouped)
        .modifier(GlassForm())
        .onAppear(perform: syncLaunchAtLogin)
    }

    private var defaultLanguage: Binding<String> {
        Binding(get: { model.settings.defaultLanguage },
                set: { code in model.updateSettings { $0.defaultLanguage = code } })
    }

    private var launchAtLogin: Binding<Bool> {
        Binding(get: { model.settings.launchAtLogin }, set: { setLaunchAtLogin($0) })
    }

    private func setLaunchAtLogin(_ enabled: Bool) {
        loginItemNote = nil
        do {
            if enabled {
                try SMAppService.mainApp.register()
                if SMAppService.mainApp.status == .requiresApproval {
                    loginItemNote = "Approve EchoDiar in System Settings > General > Login Items."
                    SMAppService.openSystemSettingsLoginItems()
                }
            } else {
                try SMAppService.mainApp.unregister()
            }
        } catch {
            loginItemNote = error.localizedDescription
        }
        syncLaunchAtLogin()
    }

    /// The login item can also be changed in System Settings; the toggle follows its real state.
    private func syncLaunchAtLogin() {
        let status = SMAppService.mainApp.status
        let registered = status == .enabled || status == .requiresApproval
        model.updateSettings { $0.launchAtLogin = registered }
    }
}

/// The grouped form over the translucent window background on macOS 26+ (unless `--force-fallbacks`).
private struct GlassForm: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 26, *), PlatformFeatures.liquidGlass {
            content
                .scrollContentBackground(.hidden)
                .background(WindowGlass().ignoresSafeArea())
        } else {
            content
        }
    }
}
