import AppKit
import EchoCore
import ServiceManagement
import SwiftUI

/// The General tab: login item, floating indicator, delete confirmation, the consent message and the onboarding again.
struct GeneralSettingsView: View {
    let model: AppModel
    @State private var loginItemNote: String?
    @State private var copied = false

    /// Same text as the onboarding's consent step.
    private static let consentMessage = "Heads up: I'm recording this call to take notes. The recording stays on my Mac."

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
            Section("Floating indicator") {
                Toggle(isOn: model.setting(\.showPill)) {
                    Text("Show floating indicator while recording")
                    Text("Meeting prompts show either way.")
                }
                Toggle("Hide it while an EchoDiar window is in front", isOn: model.setting(\.hidePillWhenAppActive))
                    .disabled(!model.settings.showPill)
                Toggle(isOn: model.setting(\.showPillInScreenCaptures)) {
                    Text("Show it in screenshots and screen recordings")
                    Text("Screen sharing in meeting apps shows it too.")
                }
                Picker("Indicator size", selection: model.setting(\.pillSize)) {
                    Text("Small").tag(PillSize.small)
                    Text("Medium").tag(PillSize.medium)
                    Text("Large").tag(PillSize.large)
                }
            }
            Section("Library") {
                Toggle(isOn: model.setting(\.deleteWithoutConfirmation)) {
                    Text("Delete conversations without confirmation")
                    Text("They still go to the Trash.")
                }
            }
            Section("Consent") {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Tell the people you record. You are responsible for their consent.")
                        .foregroundStyle(.secondary)
                    Text("\"\(Self.consentMessage)\"")
                        .textSelection(.enabled)
                    Button(copied ? "Copied" : "Copy consent message", systemImage: copied ? "checkmark" : "square.on.square") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(Self.consentMessage, forType: .string)
                        copied = true
                    }
                }
            }
            Section {
                LabeledContent("Permissions and consent") {
                    Button("Show onboarding again", action: model.showOnboarding)
                }
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: syncLaunchAtLogin)
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

extension AppModel {
    /// A settings field for a control; each change is saved.
    func setting<Value>(_ keyPath: WritableKeyPath<AppSettings, Value>) -> Binding<Value> {
        Binding(get: { self.settings[keyPath: keyPath] }, set: { value in self.updateSettings { $0[keyPath: keyPath] = value } })
    }
}
