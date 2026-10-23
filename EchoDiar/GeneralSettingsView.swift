import AppKit
import EchoCore
import ServiceManagement
import SwiftUI

/// The General tab: login item, app language, floating indicator, delete confirmation, the consent message and the
/// onboarding again.
struct GeneralSettingsView: View {
    let model: AppModel
    @State private var loginItemNote: String?
    @State private var language = AppLanguage.saved
    @State private var copied = false

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
                Picker("App language", selection: $language) {
                    ForEach(AppLanguage.allCases) { Text(verbatim: $0.name).tag($0) }
                }
                .onChange(of: language) { _, language in language.save() }
                if language != AppLanguage.atLaunch {
                    LabeledContent("Restart EchoDiar to apply.") {
                        // Checked again on click: a recording may have started since the view rendered.
                        Button("Relaunch now") { if relaunchBlocker == nil { AppLanguage.relaunch() } }
                            .disabled(relaunchBlocker != nil)
                            .help(relaunchBlocker.map { Text($0) } ?? Text(verbatim: ""))
                    }
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
                    Text(verbatim: "\"\(ConsentMessage.text)\"")
                        .textSelection(.enabled)
                    Button(copied ? "Copied" : "Copy consent message", systemImage: copied ? "checkmark" : "square.on.square") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(ConsentMessage.text, forType: .string)
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

    /// Why "Relaunch now" is off: a recording would stop, and Sparkle installs a ready update on quit, racing the relaunch.
    private var relaunchBlocker: LocalizedStringKey? {
        if model.isRecording || model.isStarting { return "Stop the recording first" }
        if (NSApp.delegate as? AppDelegate)?.updater.updateAvailable == true { return "Install the update first" }
        return nil
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
                    loginItemNote = String(localized: "Approve EchoDiar in System Settings > General > Login Items.")
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

/// The app's interface language: `AppleLanguages` (and `AppleLocale`, so dates and numbers follow) in the app's own
/// defaults domain, read by the system at launch only. "System" removes both keys.
enum AppLanguage: String, CaseIterable, Identifiable {
    case system, en, vi, fr, es, zhHans = "zh-Hans"

    var id: Self { self }

    /// The endonym, the same in every language; only "System" is translated.
    var name: String {
        switch self {
        case .system: String(localized: "System")
        case .en: "English"
        case .vi: "Tiếng Việt"
        case .fr: "Français"
        case .es: "Español"
        case .zhHans: "简体中文"
        }
    }

    /// The choice in effect for this run; read in applicationDidFinishLaunching, before anything writes the key.
    static let atLaunch = saved

    /// The app's own `AppleLanguages`; `array(forKey:)` would fall back to the global list, so "System" could never show.
    static var saved: AppLanguage {
        let domain = UserDefaults.standard.persistentDomain(forName: Bundle.main.bundleIdentifier!)
        return matching((domain?["AppleLanguages"] as? [String])?.first)
    }

    /// The language of `code` by prefix ("zh-Hans-FR" is zhHans, "fr-FR" is fr); none or another one is System.
    static func matching(_ code: String?) -> AppLanguage {
        guard let code else { return .system }
        return allCases.first { $0 != .system && (code == $0.rawValue || code.hasPrefix($0.rawValue + "-")) } ?? .system
    }

    /// `AppleLocale` for `code` in the current region, in Foundation's canonical form: `vi_FR`, `zh-Hans_FR`.
    static func locale(for code: String, region: String?) -> String {
        code + "_" + (region ?? "US")
    }

    func save() {
        let defaults = UserDefaults.standard
        guard self != .system else {
            defaults.removeObject(forKey: "AppleLanguages")
            defaults.removeObject(forKey: "AppleLocale")
            return
        }
        defaults.set([rawValue], forKey: "AppleLanguages")
        defaults.set(Self.locale(for: rawValue, region: Locale.current.region?.identifier), forKey: "AppleLocale")
    }

    /// Quits and opens the app again. `open` on a running app only activates it, so a shell waits for this process to
    /// exit first, up to 10 s, and opens the app only if it is gone (a cancelled quit leaves it running).
    /// NSApp.terminate quits as ⌘Q does, so AppModel's willTerminate cleanup runs.
    @MainActor static func relaunch() {
        let shell = Process()
        shell.executableURL = URL(fileURLWithPath: "/bin/sh")
        shell.arguments = [
            "-c", #"i=0; while /bin/kill -0 "$1" 2>/dev/null && [ $i -lt 100 ]; do /bin/sleep 0.1; i=$((i+1)); done; "#
                + #"/bin/kill -0 "$1" 2>/dev/null || /usr/bin/open "$0""#,
            Bundle.main.bundleURL.path, String(ProcessInfo.processInfo.processIdentifier),
        ]
        guard (try? shell.run()) != nil else { return }
        NSApp.terminate(nil)
    }
}

extension AppModel {
    /// A settings field for a control; each change is saved.
    func setting<Value>(_ keyPath: WritableKeyPath<AppSettings, Value>) -> Binding<Value> {
        Binding(get: { self.settings[keyPath: keyPath] }, set: { value in self.updateSettings { $0[keyPath: keyPath] = value } })
    }
}
