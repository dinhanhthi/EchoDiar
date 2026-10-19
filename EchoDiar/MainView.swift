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
                if let live = model.liveTranscript {
                    LiveTranscriptView(snapshot: live)
                        .padding([.horizontal, .top], 12)
                }
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
                .keyboardShortcut("i", modifiers: .command)
                .help(model.selectedTab == .ai ? "Show the library (⌘I)" : "Show the AI pane (⌘I)")
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
        // One-time per provider and destination (`AppSettings.cloudSTTConfirmed`); answered for every pending run
        // of that destination.
        .alert(
            "Send audio to \(model.pendingCloudRuns.first?.providerName ?? "")?",
            isPresented: Binding(get: { !model.pendingCloudRuns.isEmpty }, set: { _ in })
        ) {
            Button("Send") { model.resolvePendingCloudRuns(allowed: true) }
            Button("Cancel", role: .cancel) { model.resolvePendingCloudRuns(allowed: false) }
        } message: {
            Text("Audio leaves this Mac: the recording's mic and system channels are uploaded to \(model.pendingCloudRuns.first?.providerName ?? "the provider") (\(model.pendingCloudRuns.first?.host ?? "")) for transcription and billed by them. The provider may keep the audio under its own retention policy. You are asked once per provider and destination.")
        }
    }

    /// While idle, one Record split button whose chevron picks the target and the language; while recording,
    /// Stop and Pause/Resume in one group, like Settings and AI. ⌘R and ⇧⌘P come from `shortcutButtons`.
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
                        // Menu content is built when the menu opens: apps that started playing since show up.
                        .onAppear { model.refreshTargets() }
                    if !model.targetGroups.isEmpty {
                        Divider()
                        ForEach(model.targetGroups, id: \.bundleID) { group in
                            Toggle(group.needsGlobalTap ? "\(group.name) (records all system audio)" : group.name,
                                   isOn: targetBinding(.app(group)))
                        }
                    }
                    Divider()
                    LanguagePicker(title: "Language", selection: languageBinding, model: model)
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

    private var languageBinding: Binding<String> {
        Binding(get: { model.recordingLanguage }, set: { model.setRecordingLanguage($0) })
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

    /// The cards, or the opened conversation's transcript sliding in over them.
    private var libraryPane: some View {
        ZStack {
            if let conversation = openedConversation {
                TranscriptView(conversation: conversation, model: model)
                    .id(conversation.id)
                    .transition(paneTransition(from: .trailing))
            } else {
                cardsPane.transition(paneTransition(from: .leading))
            }
        }
    }

    /// nil when none is opened or it was deleted.
    private var openedConversation: Conversation? {
        model.openedConversationID.flatMap { id in model.conversations.first { $0.id == id } }
    }

    @ViewBuilder private var cardsPane: some View {
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
        AIPanelView(model: model)
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
                    ChannelMeters(feed: model.levels)
                }
                languageMenu
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

    /// Picking a language adds a marker at the current recording time.
    private var languageMenu: some View {
        Menu {
            LanguagePicker(
                title: "Language",
                selection: Binding(get: { model.recordingLanguage }, set: { model.setRecordingLanguage($0) }),
                model: model)
                .pickerStyle(.inline)
                .labelsHidden()
        } label: {
            Label(LanguageOptions.name(model.recordingLanguage), systemImage: "globe")
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .foregroundStyle(.secondary)
        .help("Language from now on")
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

/// Mic and system peak bars, from the shared `LevelFeed` (15 Hz while shown).
private struct ChannelMeters: View {
    let feed: LevelFeed

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            bar("Mic", feed.mic)
            bar("System", feed.system)
        }
        .frame(width: 110)
        .task { await feed.attach() }
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
                    Capsule().fill(Color.green).frame(width: proxy.size.width * LevelFeed.fraction(level))
                }
            }
            .frame(height: 5)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(name) level")
        .accessibilityValue("\(Int((LevelFeed.fraction(level) * 100).rounded())) percent")
    }
}
