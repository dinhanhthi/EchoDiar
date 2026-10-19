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
    @State private var query = ""
    /// `query` once typing pauses for 200 ms (at once when cleared): what the cards are filtered by.
    @State private var debouncedQuery = ""
    /// Transcript text by conversation for the search, read off the main thread only once a query is typed; read
    /// again when the conversation's size changes (a new transcript). Released with the window.
    @State private var transcriptTexts: [UUID: SearchText] = [:]
    /// The search bar fills the toolbar in place of its buttons, from the Search button or ⌘F until it is closed.
    @State private var searchExpanded = false
    /// The window's width, which the search bar fills from its leading edge.
    @State private var windowWidth: CGFloat = 0
    /// Where the closed search bar was, in the window, while it shrinks back into the Search button.
    @State private var searchBarGhost: CGRect?

    private struct SearchText {
        let size: Int64
        let text: String
    }

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
            // Whoever switches the pane (toolbar, menu, card, citation), it moves the same way.
            .animation(Self.paneAnimation, value: model.selectedTab)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(WindowGlass().ignoresSafeArea())
        .overlay {
            if let searchBarGhost {
                Color.clear
                    .overlay(alignment: .topLeading) {
                        SearchBarGhost(frame: searchBarGhost) { self.searchBarGhost = nil }
                    }
                    .ignoresSafeArea()
                    .allowsHitTesting(false)
            }
        }
        .frame(minWidth: 440, minHeight: 480)
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { windowWidth = $0 }
        .task(id: query) {
            if !query.isEmpty { try? await Task.sleep(for: .milliseconds(200)) }
            if !Task.isCancelled { debouncedQuery = query }
        }
        .task(id: debouncedQuery.isEmpty ? nil : model.conversations) { await loadTranscriptTexts() }
        .onChange(of: model.searchPresented, initial: true) { _, presented in
            if presented { searchExpanded = true }
        }
        .toolbar {
            if searchExpanded {
                searchBarItem
            } else {
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
                    .help(model.selectedTab == .ai ? "Show the library (⌘I)" : "Show the AI pane (⌘I)")
                }
                // Its own capsule, after Settings and AI.
                if #available(macOS 26, *) {
                    ToolbarSpacer(.fixed)
                }
                ToolbarItem {
                    Button(action: model.showSearch) {
                        Label("Search", systemImage: "magnifyingglass")
                    }
                    .help("Search conversations (⌘F)")
                }
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
    /// Stop (red, pulsing unless paused or Reduce Motion) and Pause/Resume in one group, like Settings and AI. ⌘R and
    /// ⇧⌘P come from the main menu.
    @ToolbarContentBuilder private var recordItem: some ToolbarContent {
        if model.isRecording {
            let paused = model.snapshot.state == .paused
            ToolbarItemGroup(placement: .navigation) {
                Button(action: model.toggleRecording) {
                    Label {
                        Text("Stop")
                    } icon: {
                        Image(systemName: "stop.circle.fill")
                            .foregroundStyle(.red)
                            .symbolEffect(.pulse, isActive: !paused && !reduceMotion && windowVisible)
                    }
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
        model.selectedTab = model.selectedTab == .ai ? .library : .ai
    }

    /// The pane switches: Library, transcript, AI.
    private static let paneAnimation = Animation.snappy(duration: 0.25)

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
        .animation(Self.paneAnimation, value: model.openedConversationID)
    }

    /// nil when none is opened or it was deleted.
    private var openedConversation: Conversation? {
        model.openedConversationID.flatMap { id in model.conversations.first { $0.id == id } }
    }

    @ViewBuilder private var cardsPane: some View {
        let groups = DayGroup.groups(from: searchResults)
        if model.conversations.isEmpty {
            emptyState("No conversations yet — press ⌘R to record")
        } else if groups.isEmpty {
            emptyState("No conversations match \"\(searchTerm)\"")
        } else {
            ConversationListView(model: model, groups: groups)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func emptyState(_ text: String) -> some View {
        GlassCards {
            Text(text)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 20)
                .padding(.vertical, 16)
                .modifier(GlassCard(cornerRadius: 20))
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    // MARK: - Search

    private var searchPresented: Binding<Bool> {
        Binding(get: { model.searchPresented }, set: { model.searchPresented = $0 })
    }

    /// The search bar alone in the toolbar, with its own glass: the toolbar's shared capsule would not follow its
    /// width while it grows and shrinks.
    @ToolbarContentBuilder private var searchBarItem: some ToolbarContent {
        if #available(macOS 26, *) {
            ToolbarItem { searchBar }.sharedBackgroundVisibility(.hidden)
        } else {
            ToolbarItem { searchBar }
        }
    }

    private var searchBar: some View {
        ToolbarSearchBar(text: $query, focusRequest: searchPresented, windowWidth: windowWidth) { frame in
            query = ""
            // The toolbar fades its buttons in, in an animated transaction, while the bar's ghost shrinks.
            if !reduceMotion { searchBarGhost = frame }
            withAnimation(reduceMotion ? nil : ToolbarSearchBar.animation) { searchExpanded = false }
        }
    }

    private var searchTerm: String { debouncedQuery.trimmingCharacters(in: .whitespacesAndNewlines) }

    /// Conversations whose title or transcript contains the query (case and accent insensitive); all without one.
    private var searchResults: [Conversation] {
        let term = searchTerm
        guard !term.isEmpty else { return model.conversations }
        return model.conversations.filter {
            $0.title.localizedStandardContains(term) || transcriptTexts[$0.id]?.text.localizedStandardContains(term) == true
        }
    }

    /// Reads the transcripts not read yet, or changed since; titles match meanwhile.
    private func loadTranscriptTexts() async {
        let stale = model.conversations.filter { $0.hasTranscript && transcriptTexts[$0.id]?.size != $0.sizeBytes }
        guard !debouncedQuery.isEmpty, !stale.isEmpty else { return }
        let loaded = await Task.detached(priority: .utility) {
            stale.map { conversation in
                let text = (try? Transcript.read(from: conversation.folder))?.segments.map(\.text).joined(separator: "\n")
                return (conversation.id, SearchText(size: conversation.sizeBytes, text: text ?? ""))
            }
        }.value
        guard !Task.isCancelled else { return }
        for (id, text) in loaded { transcriptTexts[id] = text }
    }

    private var aiPane: some View {
        AIPanelView(model: model)
    }
}

/// The main window's search bar, in place of the toolbar's buttons: it grows out of the Search button's place to the
/// toolbar's width, and shrinks back before the buttons return. Not `.searchable`: the system search item widened
/// when focused and pushed Record, Settings and AI into the toolbar's overflow menu in the 440 pt wide window.
private struct ToolbarSearchBar: View {
    static let height: CGFloat = 34
    /// From the window's trailing edge, like the toolbar's buttons.
    static let trailingInset: CGFloat = 8
    /// Growing and shrinking.
    static let animation = Animation.snappy(duration: 0.3)

    @Binding var text: String
    /// ⌘F sets it to focus the field; false again once focused.
    @Binding var focusRequest: Bool
    let windowWidth: CGFloat
    /// X or Escape, with the bar's frame in the window (top-left origin).
    let onClose: (CGRect) -> Void
    @State private var expanded = false
    /// The toolbar does not stretch its items: the bar reaches from its leading edge in the window, measured, to the
    /// window's trailing edge. It grows once measured, from where the Search button was.
    @State private var origin: CGPoint?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 0) {
            // The Search button's icon, at its place.
            Image(systemName: "magnifyingglass")
                .font(.body)
                .imageScale(.large)
                .frame(width: Self.height, height: Self.height)
            SearchTextField(text: $text, focusRequest: $focusRequest, onEscape: close)
                .opacity(expanded ? 1 : 0)
            // As big as the Search button: its highlight fills the bar's end.
            Button(action: close) {
                Image(systemName: "xmark")
                    .font(.body)
                    .imageScale(.large)
                    .frame(width: Self.height - 10, height: Self.height - 10)
            }
            .buttonStyle(RowIconButtonStyle())
            .frame(width: Self.height, height: Self.height)
            .help("Close search (Esc)")
            .opacity(expanded ? 1 : 0)
        }
        .frame(width: expanded ? nil : Self.height, height: Self.height, alignment: .leading)
        .clipped()
        .modifier(GlassCard(cornerRadius: Self.height / 2))
        .frame(minWidth: Self.height, idealWidth: width, maxWidth: width, alignment: .trailing)
        .opacity(origin == nil ? 0 : 1)
        .background(WindowOriginReader { point in
            guard origin != point else { return }
            let first = origin == nil
            origin = point
            if first { withAnimation(reduceMotion ? nil : Self.animation) { expanded = true } }
        })
    }

    private var width: CGFloat {
        guard let origin else { return Self.height }
        return max(windowWidth - origin.x - Self.trailingInset, Self.height)
    }

    private func close() {
        onClose(CGRect(origin: origin ?? .zero, size: CGSize(width: width, height: Self.height)))
    }
}

/// The search bar's text field. AppKit's: a SwiftUI field in a toolbar item that was just added did not take the
/// focus.
private struct SearchTextField: NSViewRepresentable {
    @Binding var text: String
    /// ⌘F sets it to focus the field; false again once asked.
    @Binding var focusRequest: Bool
    let onEscape: () -> Void

    func makeNSView(context: Context) -> Field {
        let field = Field()
        field.isBordered = false
        field.drawsBackground = false
        field.focusRingType = .none
        field.usesSingleLineMode = true
        field.cell?.isScrollable = true
        field.placeholderString = String(localized: "Search")
        field.setAccessibilityLabel("Search conversations")
        field.delegate = context.coordinator
        return field
    }

    func updateNSView(_ field: Field, context: Context) {
        context.coordinator.parent = self
        if field.stringValue != text { field.stringValue = text }
        if focusRequest {
            // A new toolbar item may not be in the window yet: it focuses itself once it is.
            field.focusInWindow = true
            DispatchQueue.main.async {
                field.focusIfInWindow()
                focusRequest = false
            }
        }
    }

    func makeCoordinator() -> Coordinator { Coordinator(parent: self) }

    final class Field: NSTextField {
        var focusInWindow = false

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            focusIfInWindow()
        }

        func focusIfInWindow() {
            guard focusInWindow, let window else { return }
            focusInWindow = false
            window.makeFirstResponder(self)
        }
    }

    @MainActor final class Coordinator: NSObject, NSTextFieldDelegate {
        var parent: SearchTextField

        init(parent: SearchTextField) {
            self.parent = parent
        }

        func controlTextDidChange(_ notification: Notification) {
            guard let field = notification.object as? NSTextField else { return }
            parent.text = field.stringValue
        }

        /// Escape closes the search bar.
        func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
            guard selector == #selector(NSResponder.cancelOperation(_:)) else { return false }
            parent.onEscape()
            return true
        }
    }
}

/// The closed search bar, under the toolbar where it was: it shrinks back into the Search button and fades out while
/// the toolbar's buttons fade in. The toolbar removes the bar's own item at once.
private struct SearchBarGhost: View {
    let frame: CGRect
    let onDone: () -> Void
    @State private var collapsed = false

    var body: some View {
        Image(systemName: "magnifyingglass")
            .font(.body)
            .imageScale(.large)
            .frame(width: frame.height, height: frame.height)
            .frame(width: collapsed ? frame.height : frame.width, alignment: .leading)
            .modifier(GlassCard(cornerRadius: frame.height / 2))
            // Seen while it shrinks, gone as it reaches the Search button.
            .animation(.easeIn(duration: 0.3)) { $0.opacity(collapsed ? 0 : 1) }
            .frame(width: frame.width, alignment: .trailing)
            .offset(x: frame.minX, y: frame.minY)
            .onAppear {
                withAnimation(ToolbarSearchBar.animation) { collapsed = true } completion: { onDone() }
            }
    }
}

/// Reports this view's top-left corner in its window (top-left origin), each time it is laid out there.
private struct WindowOriginReader: NSViewRepresentable {
    let onChange: (CGPoint) -> Void

    func makeNSView(context: Context) -> Probe { Probe() }

    func updateNSView(_ probe: Probe, context: Context) {
        probe.onChange = onChange
    }

    final class Probe: NSView {
        var onChange: ((CGPoint) -> Void)?

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            report()
        }

        override func layout() {
            super.layout()
            report()
        }

        /// On the next turn: SwiftUI may be laying out the toolbar.
        private func report() {
            guard let window else { return }
            let frame = convert(bounds, to: nil)
            let origin = CGPoint(x: frame.minX, y: window.frame.height - frame.maxY)
            DispatchQueue.main.async { self.onChange?(origin) }
        }
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
