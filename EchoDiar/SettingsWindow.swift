import AppKit
import EchoCore
import SwiftUI

/// A sidebar section. Adding one: a case, its row in `label`, its view in `SettingsView.content`.
enum SettingsTab: CaseIterable, Identifiable {
    case general, recording, calendar, transcription, providers, aiSettings, storage, shortcuts, about, acknowledgements

    var id: Self { self }

    /// With `return`: from a switch expression of tuples, only the first title reaches the string catalog.
    var label: (title: LocalizedStringResource, icon: String) {
        switch self {
        case .general: return ("General", "gearshape")
        case .recording: return ("Recording", "record.circle")
        case .calendar: return ("Calendar", "calendar")
        case .transcription: return ("Transcription", "waveform")
        case .providers: return ("AI Providers", "cpu")
        case .aiSettings: return ("AI Settings", "sparkles")
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
             .init(id: "autoStop", title: "Auto-stop"), .init(id: "afterRecording", title: "After recording")]
        case .providers:
            [.init(id: "localAI", title: "Local AI"), .init(id: "cloud", title: "Cloud"),
             .init(id: "cli", title: "CLIs")]
        case .aiSettings:
            [.init(id: "chat", title: "Chat"), .init(id: "summary", title: "Summary"),
             .init(id: "catchUp", title: "Catch-up"), .init(id: "transcription", title: "Transcription"),
             .init(id: "library", title: "Library chat")]
        case .storage:
            [.init(id: "usage", title: "Usage"), .init(id: "audio", title: "Audio"),
             .init(id: "autoDelete", title: "Auto-delete")]
        case .general, .calendar, .transcription, .shortcuts, .about, .acknowledgements:
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
            // Custom rows, not a sidebar `List`: no public API hides its rounded-rect selection highlight, and the
            // selected row is an accent capsule like the toolbar's tabs.
            ScrollView {
                VStack(spacing: 4) {
                    ForEach(visibleTabs) { tab in
                        SidebarRow(tab: tab, selected: tab == model.settingsTab && !searching) { select(tab) }
                    }
                }
                .padding(8)
            }
            // `.edit`: focusable on click and on appear even with the system's Keyboard Navigation off, as a List is.
            .focusable(interactions: .edit)
            .focusEffectDisabled()
            .focused($sidebarFocused)
            .onMoveCommand(perform: move)
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

    /// While searching nothing is highlighted, and picking a section clears the search and opens it.
    private func select(_ tab: SettingsTab) {
        query = ""
        model.settingsTab = tab
        sidebarFocused = true
    }

    /// Up/down arrows: the previous/next section, stopping at the ends. While searching, the first hit.
    private func move(_ direction: MoveCommandDirection) {
        let tabs = visibleTabs
        guard !tabs.isEmpty, direction == .up || direction == .down else { return }
        guard !searching, let index = tabs.firstIndex(of: model.settingsTab) else { return select(tabs[0]) }
        select(tabs[direction == .up ? max(index - 1, 0) : min(index + 1, tabs.count - 1)])
    }

    @ViewBuilder
    private func content(_ tab: SettingsTab) -> some View {
        switch tab {
        case .general: GeneralSettingsView(model: model)
        case .recording: RecordingSettingsView(model: model)
        case .calendar: CalendarSettingsView(model: model)
        case .transcription: ModelsSettingsView(model: model)
        case .providers: ProvidersSettingsView(model: model)
        case .aiSettings: AISettingsView(model: model)
        case .storage: StorageSettingsView(model: model)
        case .shortcuts: ShortcutsSettingsView(model: model)
        case .about: AboutSettingsView()
        case .acknowledgements: AcknowledgementsSettingsView()
        }
    }
}

/// A sidebar section's row: icon and title, on an accent capsule while selected, a faint capsule on hover.
private struct SidebarRow: View {
    let tab: SettingsTab
    let selected: Bool
    let action: () -> Void
    @State private var hovered = false

    var body: some View {
        Button(action: action) {
            Label {
                Text(tab.label.title).foregroundStyle(selected ? Color.white : Color.primary)
            } icon: {
                Image(systemName: tab.label.icon)
                    .foregroundStyle(selected ? Color.white : Color.accentColor)
                    // One column, so the titles line up whatever the symbol's width.
                    .frame(width: 18)
            }
            .lineLimit(1)
            .padding(.horizontal, 10)
            .frame(maxWidth: .infinity, minHeight: 32, alignment: .leading)
            .background {
                if selected {
                    Capsule().fill(Color.accentColor)
                } else if hovered {
                    Capsule().fill(Palette.hoverFill)
                }
            }
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .onHover { hovered = $0 }
        .animation(Motion.hover, value: hovered)
        .accessibilityAddTraits(selected ? .isSelected : [])
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
