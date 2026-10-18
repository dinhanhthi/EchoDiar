import EchoCore
import SwiftUI

/// The main window: the conversation library or the AI pane, with Record, Settings and AI in the toolbar.
struct MainView: View {
    let model: AppModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(model: AppModel) {
        self.model = model
    }

    var body: some View {
        ZStack {
            switch model.selectedTab {
            case .library: libraryPane.transition(paneTransition(from: .leading))
            case .ai: aiPane.transition(paneTransition(from: .trailing))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(WindowGlass().ignoresSafeArea())
        .frame(minWidth: 440, minHeight: 480)
        .toolbar {
            // In a narrow window the other buttons go to the overflow menu first.
            if #available(macOS 26.1, *) {
                recordItem.visibilityPriority(.high)
            } else {
                recordItem
            }
            // Pushes Settings and AI to the trailing edge.
            if #available(macOS 26, *) {
                ToolbarSpacer(.flexible)
            } else {
                ToolbarItem { Spacer() }
            }
            ToolbarItemGroup {
                Button { SettingsWindow.shared.show(model: model) } label: {
                    Label("Settings", systemImage: "slider.horizontal.3")
                }
                .modifier(ToolbarGlassButton())
                .help("Settings")
                Button(action: toggleAI) {
                    Label(model.selectedTab == .ai ? "Library" : "AI", systemImage: "sparkles")
                }
                .modifier(ToolbarGlassButton())
                .help(model.selectedTab == .ai ? "Show the library" : "Show the AI pane")
            }
        }
    }

    // Recording arrives in Phase 3.
    private var recordItem: some ToolbarContent {
        ToolbarItem(placement: .navigation) {
            Button {} label: { Label("Record", systemImage: "record.circle") }
                .modifier(ToolbarGlassButton())
                .disabled(true)
                .help("Start recording")
        }
    }

    private func toggleAI() {
        withAnimation(.snappy) {
            model.selectedTab = model.selectedTab == .ai ? .library : .ai
        }
    }

    /// A slide from `edge` with a fade; only the fade with Reduce Motion.
    private func paneTransition(from edge: Edge) -> AnyTransition {
        reduceMotion ? .opacity : .move(edge: edge).combined(with: .opacity)
    }

    private var libraryPane: some View {
        GlassCards {
            Text("No conversations yet — press ⌘R to record")
                .foregroundStyle(.secondary)
                .padding(.horizontal, 20)
                .padding(.vertical, 16)
                .modifier(GlassCard(cornerRadius: 20))
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var aiPane: some View {
        GlassCards {
            Label("AI summaries and questions arrive in a later version", systemImage: "sparkles")
                .foregroundStyle(.secondary)
                .padding(.horizontal, 20)
                .padding(.vertical, 16)
                .modifier(GlassCard(cornerRadius: 20))
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Glass toolbar buttons on macOS 26+ (unless `--force-fallbacks`), the default style otherwise.
private struct ToolbarGlassButton: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 26, *), PlatformFeatures.liquidGlass {
            content.buttonStyle(.glass)
        } else {
            content
        }
    }
}
