import AppKit
import EchoAudio
import EchoCore
import SwiftUI

/// The main window: the conversation library or the AI pane, with Record, Settings and AI in the toolbar.
struct MainView: View {
    let model: AppModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// False while the window is hidden, minimized or covered, so the header stops its timers.
    @State private var windowVisible = true

    init(model: AppModel) {
        self.model = model
    }

    var body: some View {
        VStack(spacing: 0) {
            if model.isRecording {
                RecordingHeader(model: model, live: windowVisible)
                    .padding([.horizontal, .top], 12)
            } else if model.selectedTarget.globalTapFallback {
                GlobalTapNote(appName: targetName)
                    .padding([.horizontal, .top], 12)
            }
            ZStack {
                switch model.selectedTab {
                case .library: libraryPane.transition(paneTransition(from: .leading))
                case .ai: aiPane.transition(paneTransition(from: .trailing))
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(WindowGlass().ignoresSafeArea())
        .background(shortcutButtons)
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
                .help("Settings")
                Button(action: toggleAI) {
                    Label(model.selectedTab == .ai ? "Library" : "AI", systemImage: "sparkles")
                }
                .help(model.selectedTab == .ai ? "Show the library" : "Show the AI pane")
            }
        }
        .task { model.refreshTargets() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            if !model.isRecording { model.refreshTargets() }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didChangeOcclusionStateNotification)) { note in
            guard let window = note.object as? NSWindow, window.contentViewController is NSHostingController<MainView> else { return }
            windowVisible = window.occlusionState.contains(.visible)
        }
        .alert("Recording problem", isPresented: Binding(
            get: { model.recordingError != nil },
            set: { if !$0 { model.recordingError = nil } }
        )) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(model.recordingError ?? "")
        }
    }

    /// While idle, one Record split button whose chevron picks the target; while recording, Stop and
    /// Pause/Resume in one group, like Settings and AI. ⌘R and ⇧⌘P come from `shortcutButtons`.
    @ToolbarContentBuilder private var recordItem: some ToolbarContent {
        if model.isRecording {
            let paused = model.snapshot.state == .paused
            ToolbarItemGroup(placement: .navigation) {
                Button(action: model.toggleRecording) {
                    Label("Stop", systemImage: "stop.circle")
                }
                .disabled(model.isStarting)
                .help("Stop recording (⌘R)")
                Button(action: model.togglePause) {
                    Label(paused ? "Resume" : "Pause", systemImage: paused ? "play.circle" : "pause.circle")
                }
                .help(paused ? "Resume recording (⇧⌘P)" : "Pause recording (⇧⌘P)")
            }
        } else {
            ToolbarItem(placement: .navigation) {
                Menu {
                    Toggle("All system audio", isOn: targetBinding(.global))
                    if !model.targetGroups.isEmpty {
                        Divider()
                        ForEach(model.targetGroups, id: \.bundleID) { group in
                            Toggle(group.needsGlobalTap ? "\(group.name) (records all system audio)" : group.name,
                                   isOn: targetBinding(.app(group)))
                        }
                    }
                } label: {
                    Label("Record", systemImage: "record.circle")
                } primaryAction: {
                    model.toggleRecording()
                }
                .disabled(model.isStarting)
                .help("Record \(targetName) (⌘R)")
            }
        }
    }

    /// Hidden buttons for ⌘R (Record/Stop) and ⇧⌘P (Pause/Resume, only while recording), since a
    /// shortcut on `Menu(primaryAction:)` is not reliable.
    private var shortcutButtons: some View {
        ZStack {
            Button("Record or stop", action: model.toggleRecording)
                .keyboardShortcut("r", modifiers: .command)
                .disabled(model.isStarting)
            Button("Pause or resume", action: model.togglePause)
                .keyboardShortcut("p", modifiers: [.command, .shift])
                .disabled(!model.isRecording)
        }
        .frame(width: 0, height: 0)
        .opacity(0)
        .accessibilityHidden(true)
    }

    /// Checked when `target` is the selected one; matched by app, since process lists change.
    private func targetBinding(_ target: CaptureTarget) -> Binding<Bool> {
        Binding(
            get: { Self.targetID(model.selectedTarget) == Self.targetID(target) },
            set: { if $0 { model.selectedTarget = target } }
        )
    }

    private static func targetID(_ target: CaptureTarget) -> String? {
        if case .app(let group) = target { return group.bundleID }
        return nil
    }

    private var targetName: String {
        if case .app(let group) = model.selectedTarget { return group.name }
        return "All system audio"
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

    @ViewBuilder private var libraryPane: some View {
        let groups = DayGroup.groups(from: model.conversations)
        if groups.isEmpty {
            GlassCards {
                Text("No conversations yet — press ⌘R to record")
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 20)
                    .padding(.vertical, 16)
                    .modifier(GlassCard(cornerRadius: 20))
            }
            .padding(12)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            ConversationListView(model: model, groups: groups)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
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

/// Elapsed time, two-channel level meter and capture warnings while a recording is open.
private struct RecordingHeader: View {
    let model: AppModel
    /// The window is visible: only then the clock and the meter update.
    let live: Bool

    var body: some View {
        let paused = model.snapshot.state == .paused
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Image(systemName: paused ? "pause.circle.fill" : "record.circle.fill")
                    .foregroundStyle(paused ? .orange : .red)
                if live {
                    TimelineView(.periodic(from: .now, by: 1)) { _ in elapsedText }
                } else {
                    elapsedText
                }
                Text(paused ? "Paused" : "Recording")
                    .foregroundStyle(.secondary)
                Spacer(minLength: 8)
                if live && !paused {
                    ChannelMeters(engine: model.engine)
                }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .modifier(GlassCard(cornerRadius: 14))
            if model.snapshot.globalTapFallback {
                Label("This app's audio cannot be isolated, so all system audio is being recorded.",
                      systemImage: "exclamationmark.triangle.fill")
                    .font(.callout)
                    .foregroundStyle(.yellow)
            }
            if model.snapshot.silentTapSuspected {
                SilentTapBanner { PrivacySettings.openSystemAudioRecording() }
            }
        }
    }

    private var elapsedText: some View {
        let elapsed = model.engine.currentSnapshot().elapsed
        let pattern: Duration.TimeFormatStyle.Pattern = elapsed >= 3_600 ? .hourMinuteSecond : .minuteSecond
        return Text(Duration.seconds(elapsed).formatted(.time(pattern: pattern)))
            .font(.body.monospacedDigit().weight(.semibold))
    }
}

/// Shown before recording when the picked app needs a global tap.
private struct GlobalTapNote: View {
    let appName: String

    var body: some View {
        Label("\(appName) cannot be recorded on its own: all system audio will be recorded.",
              systemImage: "info.circle")
            .font(.callout)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// Mic and system peak bars. The only reader of `RecordingEngine.currentLevels()`, at 15 Hz while shown.
private struct ChannelMeters: View {
    let engine: RecordingEngine
    @State private var levels: (mic: Float, system: Float) = (0, 0)

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            bar("Mic", levels.mic)
            bar("System", levels.system)
        }
        .frame(width: 110)
        .task {
            while !Task.isCancelled {
                levels = engine.currentLevels()
                try? await Task.sleep(for: .milliseconds(67))
            }
        }
    }

    private func bar(_ name: String, _ level: Float) -> some View {
        HStack(spacing: 6) {
            Text(name)
                .font(.caption2)
                .foregroundStyle(.secondary)
                .frame(width: 38, alignment: .leading)
            GeometryReader { proxy in
                ZStack(alignment: .leading) {
                    Capsule().fill(Palette.hoverFill)
                    Capsule().fill(Color.green).frame(width: proxy.size.width * Self.fraction(level))
                }
            }
            .frame(height: 5)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(name) level")
        .accessibilityValue("\(Int((Self.fraction(level) * 100).rounded())) percent")
    }

    /// -60...0 dBFS.
    private static func fraction(_ level: Float) -> CGFloat {
        guard level > 0 else { return 0 }
        return CGFloat(min(max((20 * log10(Double(level)) + 60) / 60, 0), 1))
    }
}
