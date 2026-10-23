import AppKit
import EchoCore
import SwiftUI

/// A sidebar section. Adding one: a case, its row in `label`, its view in `SettingsView.content`.
enum SettingsTab: CaseIterable, Identifiable {
    case general, recording, transcription, providers, rag, storage, shortcuts, about, acknowledgements

    var id: Self { self }

    /// With `return`: from a switch expression of tuples, only the first title reaches the string catalog.
    var label: (title: LocalizedStringResource, icon: String) {
        switch self {
        case .general: return ("General", "gearshape")
        case .recording: return ("Recording", "record.circle")
        case .transcription: return ("Transcription", "waveform")
        case .providers: return ("AI", "sparkles")
        case .rag: return ("RAG", "sparkle.magnifyingglass")
        case .storage: return ("Storage", "internaldrive")
        case .shortcuts: return ("Shortcuts", "keyboard")
        case .about: return ("About", "info.circle")
        case .acknowledgements: return ("Acknowledgements", "doc.text")
        }
    }

    /// The section's inner tabs, shown in the toolbar after the record capsule: the ids deep links and
    /// `AppModel.settingsSubTabs` use, with their titles. Empty for a section without tabs.
    var subTabs: [SettingsSubTab] {
        switch self {
        case .recording:
            [.init(id: "microphone", title: "Microphone"), .init(id: "meetings", title: "Meetings"),
             .init(id: "calendar", title: "Calendar"), .init(id: "autoStop", title: "Auto-stop"),
             .init(id: "afterRecording", title: "After recording")]
        case .transcription:
            [.init(id: "general", title: "General"), .init(id: "transcriptionModels", title: "Transcription model"),
             .init(id: "diarizationModels", title: "Diarization model")]
        case .providers:
            [.init(id: "onThisMac", title: "On this Mac"), .init(id: "cloudAPIs", title: "Cloud APIs"),
             .init(id: "cli", title: "CLIs")]
        case .storage:
            [.init(id: "usage", title: "Usage"), .init(id: "audio", title: "Audio"),
             .init(id: "autoDelete", title: "Auto-delete")]
        case .general, .rag, .shortcuts, .about, .acknowledgements:
            []
        }
    }
}

/// An inner tab of a Settings section.
struct SettingsSubTab {
    let id: String
    let title: LocalizedStringResource
}

/// A Settings section's header: its title, then what the section is about right under it (not in a footer, where it
/// reads as part of the next section). Without a title, the description alone.
struct SectionHeader: View {
    var title: LocalizedStringKey?
    let description: LocalizedStringKey

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            if let title { Text(title) }
            Text(description)
                .font(.caption)
                .fontWeight(.regular)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// The Settings page of the main window: sidebar of sections, the selected one on the right. `AppModel.settingsTab`
/// is the selection, so `openSettings(tab:)` deep-links.
struct SettingsView: View {
    @Bindable var model: AppModel
    /// The toolbar's search query on this page: while not empty, the sidebar lists the sections with hits and the
    /// detail shows the matching settings.
    @Binding var query: String
    /// Sections opened so far: kept alive like TabView's tabs, so leaving AI does not rerun its scans.
    @State private var opened: Set<SettingsTab> = []
    @FocusState private var sidebarFocused: Bool

    var body: some View {
        HStack(spacing: 0) {
            List(selection: selection) {
                ForEach(visibleTabs) { tab in
                    Label { Text(tab.label.title) } icon: { Image(systemName: tab.label.icon) }.tag(tab)
                }
            }
            .listStyle(.sidebar)
            // On the card, not a sidebar material of its own.
            .scrollContentBackground(.hidden)
            .focused($sidebarFocused)
            .frame(width: 200)
            // A raised card like the toolbar's capsules, instead of a divider beside the sections.
            .modifier(GlassCard(cornerRadius: 20, raised: true))
            // On top as far as the form's first section (`GlassForm`'s top margin), so the card and the form start level.
            .padding(.top, 12)
            .padding([.leading, .bottom], 12)
            ZStack {
                ForEach(SettingsTab.allCases.filter(opened.contains)) { tab in
                    let selected = tab == model.settingsTab && !searching
                    content(tab)
                        .opacity(selected ? 1 : 0)
                        .allowsHitTesting(selected)
                        .disabled(!selected)
                        .accessibilityHidden(!selected)
                }
                if searching { SettingsSearchResults(model: model, query: $query) }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .modifier(GlassForm())
        }
        .onAppear { sidebarFocused = true }
        .onChange(of: model.settingsTab, initial: true) { _, new in opened.insert(new) }
    }

    private var searching: Bool { !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    /// Every section, or while searching only those with a matching setting.
    private var visibleTabs: [SettingsTab] {
        guard searching else { return SettingsTab.allCases }
        let hits = Set(SettingsSearchItem.all.filter { $0.matches(query) }.map(\.tab))
        return SettingsTab.allCases.filter(hits.contains)
    }

    /// Ignores deselection (a click in the empty sidebar): a section is always shown. While searching nothing is
    /// highlighted, and picking a section clears the search and opens it.
    private var selection: Binding<SettingsTab?> {
        Binding(get: { searching ? nil : model.settingsTab }, set: {
            guard let tab = $0 else { return }
            query = ""
            model.settingsTab = tab
        })
    }

    @ViewBuilder
    private func content(_ tab: SettingsTab) -> some View {
        switch tab {
        case .general: GeneralSettingsView(model: model)
        case .recording: RecordingSettingsView(model: model)
        case .transcription: ModelsSettingsView(model: model)
        case .providers: ProvidersSettingsView(model: model)
        case .rag: RAGSettingsView(model: model)
        case .storage: StorageSettingsView(model: model)
        case .shortcuts: ShortcutsSettingsView(model: model)
        case .about: AboutSettingsView()
        case .acknowledgements: AcknowledgementsSettingsView()
        }
    }
}

/// The grouped form over the window's glass, on every macOS version: its rows stay the system's elevated
/// rows.
private struct GlassForm: ViewModifier {
    func body(content: Content) -> some View {
        content
            .scrollContentBackground(.hidden)
            .contentMargins(.top, 12, for: .scrollContent)
            .background(WindowGlass().ignoresSafeArea())
    }
}

/// Logo, version, license and the GitHub link.
private struct AboutSettingsView: View {
    var body: some View {
        // Straight on the window's glass, without a grouped section's card.
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
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Shared by the Settings About section and the About panel.
enum AboutInfo {
    static let repository = URL(string: "https://github.com/dinhanhthi/EchoDiar")!

    static var logo: NSImage? { NSImage(named: "logo_square_512") }

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
