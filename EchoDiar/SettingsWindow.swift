import AppKit
import EchoCore
import SwiftUI

/// The Settings window: created once, reused on every open. Later phases add sections to `SettingsTab`.
@MainActor
final class SettingsWindow: NSObject, NSWindowDelegate {
    static let shared = SettingsWindow()

    private var window: NSWindow?

    func show(model: AppModel) {
        if window == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 760, height: 540),
                styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                backing: .buffered,
                defer: false
            )
            window.title = "Settings"
            // Full-height sidebar under the traffic lights; the detail shows the section title instead.
            window.titleVisibility = .hidden
            window.titlebarAppearsTransparent = true
            window.contentMinSize = NSSize(width: 760, height: 520)
            window.isReleasedWhenClosed = false
            let hostingView = NSHostingView(rootView: SettingsView(model: model))
            // The window keeps its size on every section; the forms scroll instead.
            hostingView.sizingOptions = []
            window.contentView = hostingView
            // Not "Settings": that name holds the frame of the old fixed 600x540 window.
            if !window.setFrameAutosaveName("SettingsSidebar") || !window.setFrameUsingName("SettingsSidebar") {
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

/// Sidebar of sections, the selected one on the right. `AppModel.settingsTab` is the selection, so
/// `openSettings(tab:)` deep-links.
private struct SettingsView: View {
    @Bindable var model: AppModel
    /// Sections opened so far: kept alive like TabView's tabs, so leaving AI & Cloud does not rerun its scans.
    @State private var opened: Set<SettingsTab> = []
    @FocusState private var sidebarFocused: Bool

    var body: some View {
        NavigationSplitView {
            List(selection: selection) {
                ForEach(SettingsTab.allCases) { tab in
                    Label(tab.label.title, systemImage: tab.label.icon).tag(tab)
                }
            }
            .listStyle(.sidebar)
            .focused($sidebarFocused)
            // The hosted split view ignores the ideal width alone and opens the sidebar too narrow.
            .frame(minWidth: 190)
            .navigationSplitViewColumnWidth(min: 190, ideal: 200, max: 260)
            // No toolbar in this window: keep the sidebar from being collapsed out of reach.
            .toolbar(removing: .sidebarToggle)
        } detail: {
            VStack(alignment: .leading, spacing: 0) {
                // In the title bar row, like System Settings (the detail column is clear of the traffic lights).
                Text(model.settingsTab.label.title)
                    .font(.title3.weight(.semibold))
                    .padding(.horizontal, 20)
                    .frame(height: 32)
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
                // Scrolled rows otherwise slide up under the title.
                .clipped()
            }
            .ignoresSafeArea(.container, edges: .top)
            .modifier(GlassForm())
        }
        .onAppear { sidebarFocused = true }
        .onChange(of: model.settingsTab, initial: true) { opened.insert(model.settingsTab) }
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
