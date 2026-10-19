import AppKit
import EchoCore
import SwiftUI

/// A sidebar section. Adding one: a case, its row in `label`, its view in `SettingsView.content`.
enum SettingsTab: CaseIterable, Identifiable {
    case general, recording, transcription, providers, storage, shortcuts, about

    var id: Self { self }

    var label: (title: String, icon: String) {
        switch self {
        case .general: ("General", "gearshape")
        case .recording: ("Recording", "record.circle")
        case .transcription: ("Transcription", "waveform")
        case .providers: ("AI & Cloud", "sparkles")
        case .storage: ("Storage", "internaldrive")
        case .shortcuts: ("Shortcuts", "keyboard")
        case .about: ("About", "info.circle")
        }
    }
}

/// The Settings page of the main window: sidebar of sections, the selected one on the right. `AppModel.settingsTab`
/// is the selection, so `openSettings(tab:)` deep-links.
struct SettingsView: View {
    @Bindable var model: AppModel
    /// Sections opened so far: kept alive like TabView's tabs, so leaving AI & Cloud does not rerun its scans.
    @State private var opened: Set<SettingsTab> = []
    @FocusState private var sidebarFocused: Bool

    var body: some View {
        HStack(spacing: 0) {
            List(selection: selection) {
                ForEach(SettingsTab.allCases) { tab in
                    Label(tab.label.title, systemImage: tab.label.icon).tag(tab)
                }
            }
            .listStyle(.sidebar)
            // On the window background, like the rest of the page.
            .scrollContentBackground(.hidden)
            .focused($sidebarFocused)
            .frame(width: 200)
            Divider()
            ZStack {
                ForEach(SettingsTab.allCases.filter(opened.contains)) { tab in
                    let selected = tab == model.settingsTab
                    content(tab)
                        .opacity(selected ? 1 : 0)
                        .allowsHitTesting(selected)
                        .disabled(!selected)
                        .accessibilityHidden(!selected)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .modifier(GlassForm())
        }
        .onAppear { sidebarFocused = true }
        .onChange(of: model.settingsTab, initial: true) { _, new in opened.insert(new) }
    }

    /// Ignores deselection (a click in the empty sidebar): a section is always shown.
    private var selection: Binding<SettingsTab?> {
        Binding(get: { model.settingsTab }, set: { if let tab = $0 { model.settingsTab = tab } })
    }

    @ViewBuilder
    private func content(_ tab: SettingsTab) -> some View {
        switch tab {
        case .general: GeneralSettingsView(model: model)
        case .recording: RecordingSettingsView(model: model)
        case .transcription: ModelsSettingsView(model: model)
        case .providers: ProvidersSettingsView(model: model)
        case .storage: StorageSettingsView(model: model)
        case .shortcuts: ShortcutsSettingsView(model: model)
        case .about: AboutSettingsView()
        }
    }
}

/// The grouped form over the solid window background, on every macOS version: its rows stay the system's elevated
/// rows.
private struct GlassForm: ViewModifier {
    func body(content: Content) -> some View {
        content
            .scrollContentBackground(.hidden)
            .background(Palette.windowBackground.ignoresSafeArea())
    }
}

/// Logo, version, license and the GitHub link.
private struct AboutSettingsView: View {
    var body: some View {
        Form {
            Section {
                VStack(spacing: 10) {
                    if let logo = AboutInfo.logo {
                        Image(nsImage: logo)
                            .resizable()
                            .interpolation(.high)
                            .frame(width: 96, height: 96)
                            .accessibilityHidden(true)
                    }
                    Text("EchoDiar").font(.title2.weight(.semibold))
                    Text("Version \(AboutInfo.version)")
                        .foregroundStyle(.secondary)
                    Text("Licensed under AGPL-3.0")
                        .foregroundStyle(.secondary)
                    Link("github.com/dinhanhthi/EchoDiar", destination: AboutInfo.repository)
                    if let copyright = AboutInfo.copyright {
                        Text(copyright)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 12)
            }
        }
        .formStyle(.grouped)
    }
}

/// Shared by the Settings About section and the About panel.
enum AboutInfo {
    static let repository = URL(string: "https://github.com/dinhanhthi/EchoDiar")!

    /// Loaded by file: `NSImage(named: "logo")` would return the app icon, which shares the name.
    static var logo: NSImage? {
        Bundle.main.url(forResource: "logo", withExtension: "png").flatMap { NSImage(contentsOf: $0) }
    }

    /// "0.1.0 (1)".
    static var version: String {
        let info = Bundle.main.infoDictionary
        let short = info?["CFBundleShortVersionString"] as? String ?? "?"
        let build = info?["CFBundleVersion"] as? String
        return build.map { "\(short) (\($0))" } ?? short
    }

    static var copyright: String? {
        Bundle.main.infoDictionary?["NSHumanReadableCopyright"] as? String
    }
}
