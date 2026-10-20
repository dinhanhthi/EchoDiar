import AppKit
import EchoAudio
import EchoCore
import SwiftUI

/// The main window, one page per `AppModel.Route`: the cards, a conversation, the open recording (while recording)
/// or Settings. Back, Record (or Stop and Pause) and a conversation's title on the leading side; a conversation's
/// Reveal in Finder, Settings, the library chat and Search, then the conversation's tabs on the trailing side. The
/// library chat takes the cards' place on the list, or the sidebar's beside a conversation.
struct MainView: View {
    let model: AppModel
    let updater: Updater
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var query = ""
    /// `query` once typing pauses for 200 ms (at once when cleared): what the cards are filtered by.
    @State private var debouncedQuery = ""
    /// Searchable text by conversation (speakers, transcript, summary, chat), read off the main thread only once a
    /// query is typed; read again when the conversation's size changes (a new transcript) or an AI job ends (a new
    /// summary or chat). Released with the window.
    @State private var searchDocuments: [UUID: SearchDocument] = [:]
    /// The search bar fills the toolbar in place of its buttons, from the Search button or ⌘F until it is closed.
    @State private var searchExpanded = false
    /// The window's width, which the search bar fills from its leading edge.
    @State private var windowWidth: CGFloat = 0
    /// Where the closed search bar was, in the window, while it shrinks back into the Search button.
    @State private var searchBarGhost: CGRect?
    /// The route on screen; it follows `model.route`, with a slide when the page changes.
    @State private var shownRoute = AppModel.Route.list
    /// The last page change went to a page further right (list, entry, Settings).
    @State private var forward = true
    /// The toolbar title's rename popover is open, with `titleDraft` in its field.
    @State private var renamingTitle = false
    @State private var titleDraft = ""

    private struct SearchDocument {
        let size: Int64
        let jobsEnded: Int
        let edits: Int
        let document: ConversationSearch.Document
    }

    /// Reloads the search documents while a query is typed: the conversations changed, an AI job ended, or a
    /// speaker was renamed.
    private struct SearchLoadKey: Equatable {
        let conversations: [Conversation]
        let jobsEnded: Int
        let transcriptEdits: [UUID: Int]
    }

    /// The pages that slide, left to right; moving between two conversations stays on the same page.
    private enum Page: Int {
        case list, entry, settings

        init(_ route: AppModel.Route) {
            switch route {
            case .list: self = .list
            case .conversation, .recording: self = .entry
            case .settings: self = .settings
            }
        }
    }

    init(model: AppModel, updater: Updater) {
        self.model = model
        self.updater = updater
    }

    var body: some View {
        VStack(spacing: 0) {
            if !model.isRecording && model.selectedTarget.globalTapFallback {
                GlobalTapNote(appName: targetName)
                    .padding([.horizontal, .top], 12)
            }
            // Rendered from `shownRoute`, so the leaving page keeps its content while it slides out.
            ZStack {
                switch Page(shownRoute) {
                case .list: listPane.transition(pageTransition)
                case .entry: entryPane.transition(pageTransition)
                case .settings: SettingsView(model: model, query: $query).transition(pageTransition)
                }
            }
        }
        // Whoever switches the page (toolbar, menu, card, citation, a recording starting), it slides the same way.
        .onChange(of: model.route, initial: true) { old, new in
            renamingTitle = false
            guard Page(old) != Page(new) else {
                shownRoute = new
                return
            }
            forward = Page(new).rawValue >= Page(old).rawValue
            withAnimation(Self.paneAnimation) { shownRoute = new }
            // Another page closes the search bar back into the Search button and clears the query; another
            // conversation keeps it, for the sidebar it filters.
            query = ""
            model.searchPresented = false
            searchExpanded = false
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(WindowGlass().ignoresSafeArea())
        // A band behind the toolbar row (drawn above it by AppKit), so scrolled content never shows through the
        // transparent title bar; the same glass as the window, so toolbar and content still read as one.
        .overlay {
            GeometryReader { proxy in
                WindowGlass()
                    .frame(height: proxy.safeAreaInsets.top)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                    .ignoresSafeArea(.container, edges: .top)
            }
            .allowsHitTesting(false)
        }
        .modifier(HardTopScrollEdge())
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
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { windowWidth = $0 }
        .task(id: query) {
            if !query.isEmpty { try? await Task.sleep(for: .milliseconds(200)) }
            if !Task.isCancelled { debouncedQuery = query }
        }
        // Settings search filters the settings, not the library: no documents to read there.
        .task(id: debouncedQuery.isEmpty || model.route == .settings
                ? nil : SearchLoadKey(conversations: model.conversations, jobsEnded: model.aiJobsEnded,
                                transcriptEdits: model.transcriptEdits)) {
            await loadSearchDocuments()
        }
        .onChange(of: model.searchPresented, initial: true) { _, presented in
            guard presented else { return }
            searchExpanded = true
            // Search filters the cards: they come back in place of the library chat. Settings search keeps it, for
            // the page Settings returns to.
            if model.route != .settings { model.globalChatOpen = false }
        }
        .toolbar {
            // The compact window's search bar replaces every button; a wide one keeps Back, the title, Record,
            // Update and the tabs.
            if !searchExpanded || wide {
                leadingItems
            }
            if searchExpanded {
                if wide {
                    flexibleSpacer
                    updateItem
                }
                searchBarItem
                if wide { tabsItem }
            } else {
                // Pushes the other capsules to the trailing edge.
                flexibleSpacer
                updateItem
                revealItem
                ToolbarItemGroup {
                    Toggle(isOn: Binding(
                        get: { model.route == .settings },
                        set: { if $0 { model.openSettings() } else { model.goBack() } }
                    )) {
                        Label("Settings", systemImage: "slider.horizontal.3")
                    }
                    .help("Settings")
                    Toggle(isOn: Binding(
                        get: { model.globalChatOpen && model.route != .settings },
                        set: { open in
                            if model.route == .settings { model.goBack() }
                            model.globalChatOpen = open
                            guard open else { return }
                            query = ""
                            searchExpanded = false
                            model.searchPresented = false
                        }
                    )) {
                        Label("Ask across conversations", systemImage: "sparkles")
                    }
                    .help("Ask across conversations")
                    Button(action: model.showSearch) {
                        Label("Search", systemImage: "magnifyingglass")
                    }
                    .help(model.route == .settings ? "Search settings (⌘F)" : "Search conversations (⌘F)")
                }
                tabsItem
            }
        }
        .task { model.refreshTargets() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            if !model.isRecording { model.refreshTargets() }
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

    /// Back in its own capsule (not on the list without the library chat), the record capsule, then a conversation's
    /// title. The record capsule: while idle Record and a chevron menu that picks the target and the language; while
    /// recording, Stop (red, pulsing unless paused or Reduce Motion) and Pause/Resume. ⌘R and ⇧⌘P come from the main
    /// menu. In a narrow window the other buttons go to the overflow menu first.
    @ToolbarContentBuilder private var leadingItems: some ToolbarContent {
        if model.route != .list || model.globalChatOpen {
            ToolbarItem(placement: leadingPlacement) {
                SingleToolbarButton(title: "Back", systemImage: "chevron.left", action: model.goBack)
                    .help("Back (Esc)")
            }
            .highVisibilityPriority()
            fixedSpacer
        }
        ToolbarItemGroup(placement: leadingPlacement) { recordButtons }
            .highVisibilityPriority()
        settingsTabsItem
        if let conversation = toolbarConversation, titleFits {
            // As far from the record capsule as Back is.
            fixedSpacer
            titleItem(conversation)
        }
    }

    /// On macOS 26, a regular item declared first, before the flexible spacer, still at the leading edge (no
    /// `.principal` item, see `titleItem`): `.navigation` items got a lighter hover than the trailing capsule's
    /// buttons, or none.
    private var leadingPlacement: ToolbarItemPlacement {
        if #available(macOS 26, *) { .automatic } else { .navigation }
    }

    @ViewBuilder private var recordButtons: some View {
        if model.isRecording {
            let paused = model.snapshot.state == .paused
            Button(action: model.toggleRecording) {
                Label {
                    Text("Stop")
                } icon: {
                    Image(systemName: "stop.circle.fill")
                        .foregroundStyle(.red)
                        .modifier(RecordingBlink(isActive: !paused && !reduceMotion && model.mainWindowVisible))
                }
            }
            .disabled(model.isStarting)
            .help("Stop recording (⌘R)")
            Button(action: model.togglePause) {
                Label(paused ? "Resume" : "Pause", systemImage: paused ? "play.circle" : "pause.circle")
            }
            .help(paused ? "Resume recording (⇧⌘P)" : "Pause recording (⇧⌘P)")
        } else {
            // A plain Record button and a chevron-only menu, not a `Menu(primaryAction:)` split button: the split
            // control had its own, lighter hover than the toolbar's other buttons.
            Button(action: model.toggleRecording) {
                Label("Record", systemImage: "record.circle")
            }
            .disabled(model.isStarting)
            .help("Record \(targetName) (⌘R)")
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
                Label("Recording options", systemImage: "chevron.down")
            }
            .menuIndicator(.hidden)
            .disabled(model.isStarting)
            .help("Choose what to record and the language")
        }
    }

    /// Its own capsule, before Settings and Search, while Sparkle found an update.
    @ToolbarContentBuilder private var updateItem: some ToolbarContent {
        if updater.updateAvailable {
            ToolbarItem {
                SingleToolbarButton(title: "Update", systemImage: "arrow.down.circle") { updater.checkForUpdates() }
                    .help("Install the available update")
            }
            fixedSpacer
        }
    }

    /// The shown conversation's title, left-aligned after the record capsule, with its date, duration and size under
    /// it; double-click to rename it in a popover (Return saves, Esc or a click elsewhere cancels), drag to move the
    /// window. Plain text, without a capsule. The first item to drop into the overflow menu.
    @ToolbarContentBuilder private func titleItem(_ conversation: Conversation) -> some ToolbarContent {
        if #available(macOS 26.1, *) {
            ToolbarItem { toolbarTitle(conversation) }.sharedBackgroundVisibility(.hidden).visibilityPriority(.low)
        } else if #available(macOS 26, *) {
            ToolbarItem { toolbarTitle(conversation) }.sharedBackgroundVisibility(.hidden)
        } else {
            ToolbarItem(placement: .navigation) { toolbarTitle(conversation) }
        }
    }

    private func toolbarTitle(_ conversation: Conversation) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(conversation.title)
                .font(.headline)
                .lineLimit(1)
                .truncationMode(.tail)
            toolbarDetails(conversation)
        }
        .frame(maxWidth: titleWidth, alignment: .leading)
        .fixedSize(horizontal: false, vertical: true)
        .contentShape(Rectangle())
        .help(conversation.title)
        .onTapGesture(count: 2) {
            titleDraft = conversation.title
            renamingTitle = true
        }
        // Moves the window like the toolbar's empty space, which the item would otherwise take the click from.
        .gesture(WindowDragGesture())
        .popover(isPresented: $renamingTitle, arrowEdge: .bottom) {
            TextField("Title", text: $titleDraft)
                .onSubmit {
                    renamingTitle = false
                    if titleDraft != conversation.title { model.renameConversation(conversation.id, to: titleDraft) }
                }
                .onExitCommand { renamingTitle = false }
                .padding(12)
                .frame(width: 320)
        }
    }

    /// Only the date truncates when the line is too long.
    private func toolbarDetails(_ conversation: Conversation) -> some View {
        HStack(spacing: 8) {
            Text(conversation.createdAt.formatted(date: .abbreviated, time: .shortened))
            if conversation.duration > 0 {
                Text(ConversationCard.durationText(conversation.duration)).monospacedDigit().fixedSize()
            }
            if conversation.sizeBytes > 0 {
                Text(ByteCountFormatter.string(fromByteCount: conversation.sizeBytes, countStyle: .file)).fixedSize()
            }
            if let badge = StatusBadge(status: conversation.status) { badge.fixedSize() }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .lineLimit(1)
        .truncationMode(.tail)
    }

    /// The conversation page's conversation, while it exists; no title on the other pages.
    private var toolbarConversation: Conversation? {
        guard case .conversation(let id) = model.route else { return nil }
        return model.conversations.first { $0.id == id }
    }

    private static let titleMaxWidth: CGFloat = 360
    private static let titleMinWidth: CGFloat = 120
    /// Approximate widths of the other items: the traffic lights, Back and the record capsule; the tabs; Reveal in
    /// Finder with Delete, Settings, the library chat and Search.
    private static let leadingWidth: CGFloat = 250
    private static let tabsWidth: CGFloat = 240
    private static let trailingButtonsWidth: CGFloat = 240

    private var updateWidth: CGFloat { updater.updateAvailable ? 52 : 0 }

    /// What the toolbar's other items leave of the window (the search bar in place of the trailing buttons), at most
    /// `titleMaxWidth`: the title truncates instead of pushing buttons into the overflow menu.
    private var titleWidth: CGFloat {
        let trailing = searchExpanded ? searchBarWidth + ToolbarSearchBar.trailingInset : Self.trailingButtonsWidth
        return min(Self.titleMaxWidth, windowWidth - Self.leadingWidth - Self.tabsWidth - trailing - updateWidth)
    }

    /// The wide window's search bar: shorter when the tabs beside it leave less room, so it stays out of the
    /// overflow menu.
    private var searchBarWidth: CGFloat {
        let tabs = toolbarConversation == nil ? 0 : Self.tabsWidth
        let free = windowWidth - Self.leadingWidth - tabs - ToolbarSearchBar.trailingInset - updateWidth
        return max(min(ToolbarSearchBar.wideWidth, free), ToolbarSearchBar.height)
    }

    /// Too short a title is left out, also while the window grows into the wide layout.
    private var titleFits: Bool { titleWidth >= Self.titleMinWidth }

    /// A conversation's Reveal in Finder and Delete menu (the audio or the whole conversation) in one capsule, before
    /// Settings and Search; hidden with them while the search bar is open.
    @ToolbarContentBuilder private var revealItem: some ToolbarContent {
        if let conversation = toolbarConversation {
            ToolbarItemGroup {
                Button {
                    NSWorkspace.shared.activateFileViewerSelecting([conversation.folder])
                } label: {
                    Label("Reveal in Finder", systemImage: "folder")
                }
                .help("Reveal in Finder")
                Menu {
                    DeleteMenuItems(conversation: conversation, model: model)
                } label: {
                    Label("Delete", systemImage: "trash")
                }
                .disabled(!DeleteMenuItems.canDelete(conversation, model))
                .help("Delete the audio or the whole conversation")
            }
            fixedSpacer
        }
    }

    /// The Settings section's inner tabs, in a capsule like a conversation's tabs, after the record capsule. Hidden
    /// while the search bar is open: its results replace the section, and the bar takes the room.
    @ToolbarContentBuilder private var settingsTabsItem: some ToolbarContent {
        let section = model.settingsTab
        if model.route == .settings, !searchExpanded, !section.subTabs.isEmpty {
            fixedSpacer
            ToolbarItem(placement: leadingPlacement) {
                CapsuleTabs(
                    selection: Binding(get: { model.settingsSubTab(section) ?? "" },
                                       set: { model.settingsSubTabs[section] = $0 }),
                    tabs: section.subTabs.map(\.id),
                    title: { id in Text(section.subTabs.first { $0.id == id }?.title ?? "") }
                )
                .padding(.horizontal, 4)
                .fixedSize()
                // Each section's own tabs: another section replaces them, instead of the selection sliding and
                // resizing from the old section's tab (its title truncated meanwhile).
                .id(section)
            }
        }
    }

    /// A conversation's tabs, in a capsule of their own like the buttons', at the trailing edge after Settings and
    /// Search (or the search bar).
    @ToolbarContentBuilder private var tabsItem: some ToolbarContent {
        if toolbarConversation != nil {
            fixedSpacer
            ToolbarItem { tabs }
        }
    }

    /// AppKit pads the sides less than the top and bottom: 4 pt more puts the selected tab as far from the capsule's
    /// sides as from its top and bottom.
    private var tabs: some View {
        CapsuleTabs(
            selection: Binding(get: { model.detailTab }, set: { model.detailTab = $0 }),
            tabs: TranscriptView.tabs, title: { Text(TranscriptView.tabTitle($0)) }
        )
        .padding(.horizontal, 4)
        .fixedSize()
    }

    @ToolbarContentBuilder private var flexibleSpacer: some ToolbarContent {
        if #available(macOS 26, *) {
            ToolbarSpacer(.flexible)
        } else {
            ToolbarItem { Spacer() }
        }
    }

    /// Separates two capsules on macOS 26.
    @ToolbarContentBuilder private var fixedSpacer: some ToolbarContent {
        if #available(macOS 26, *) {
            ToolbarSpacer(.fixed)
        }
    }

    /// A conversation, the recording or Settings: the window has its wide size.
    private var wide: Bool { MainWindowLayout(model.route) == .wide }

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

    /// The page switches.
    private static let paneAnimation = Motion.standard

    /// Pages slide a little in their order: a page further right comes in from the trailing side. While the route is
    /// ahead of `shownRoute` the direction comes from both: the leaving page keeps the transition it last rendered with.
    private var pageTransition: AnyTransition {
        if reduceMotion { return .opacity }
        let page = Page(model.route), shown = Page(shownRoute)
        let forward = page == shown ? forward : page.rawValue >= shown.rawValue
        let travel: CGFloat = forward ? 24 : -24
        return .asymmetric(
            insertion: .offset(x: travel).combined(with: .opacity),
            removal: .offset(x: -travel).combined(with: .opacity))
    }

    /// The wide layout: the compact cards (or the library chat) as a sidebar, then the shown conversation (its state
    /// and playback reset per conversation) or the recording. The sidebar stays while the entry changes. The list page
    /// when the conversation is gone or the recording has ended.
    @ViewBuilder private var entryPane: some View {
        if entryShown {
            // Both panes pad 12 pt on every side; overlapping those paddings by 6 pt leaves an 18 pt gap between them.
            HStack(spacing: -6) {
                chatOr(libraryPane(compact: true, selectedID: selectedID))
                    .frame(width: 360)
                entryDetail
            }
        } else {
            listPane
        }
    }

    /// The list page: the cards, or the library chat in their place.
    private var listPane: some View { chatOr(cardsPane) }

    /// The library chat in place of `list` while it is open; the two cross-fade, whoever toggles it (toolbar, Back,
    /// Esc, Search).
    private func chatOr(_ list: some View) -> some View {
        ZStack {
            if model.globalChatOpen {
                GlobalChatView(model: model).transition(.opacity)
            } else {
                list.transition(.opacity)
            }
        }
        .animation(reduceMotion ? nil : Self.paneAnimation, value: model.globalChatOpen)
    }

    /// The conversation still exists, or the recording still runs.
    private var entryShown: Bool {
        switch shownRoute {
        case .conversation(let id): model.conversations.contains { $0.id == id }
        case .recording: model.isRecording
        default: false
        }
    }

    /// The sidebar's selected card: the shown conversation.
    private var selectedID: UUID? {
        if case .conversation(let id) = shownRoute { return id }
        return nil
    }

    @ViewBuilder private var entryDetail: some View {
        if case .conversation(let id) = shownRoute,
           let conversation = model.conversations.first(where: { $0.id == id }) {
            TranscriptView(conversation: conversation, model: model)
                .id(id)
        } else {
            RecordingDetailView(model: model)
        }
    }

    private var cardsPane: some View {
        libraryPane(compact: false, selectedID: nil)
    }

    /// The cards matching the search, or why there are none; the recording's card stays on top while recording, alone
    /// when the library is still empty and nothing is searched.
    @ViewBuilder private func libraryPane(compact: Bool, selectedID: UUID?) -> some View {
        let groups = DayGroup.groups(from: searchResults.filter(listed))
        if groups.isEmpty && model.isRecording && searchTerm.isEmpty {
            ConversationListView(model: model, groups: [], compact: compact, selectedID: selectedID)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if !model.conversations.contains(where: listed) {
            emptyState("No conversations yet — press ⌘R to record", compact: compact)
        } else if groups.isEmpty {
            emptyState("No conversations match \"\(searchTerm)\"", compact: compact)
        } else {
            ConversationListView(model: model, groups: groups, compact: compact, selectedID: selectedID)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    /// The recording's own entry is hidden while recording: its card is the recording card.
    private func listed(_ conversation: Conversation) -> Bool {
        !(model.isRecording && conversation.status == .recording)
    }

    private func emptyState(_ text: String, compact: Bool) -> some View {
        VStack(spacing: 0) {
            if model.isRecording {
                RecordingCard(model: model, compact: compact, selected: model.route == .recording)
                    .padding([.horizontal, .top], 12)
            }
            Text(text)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 20)
                .padding(12)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    // MARK: - Search

    private var searchPresented: Binding<Bool> {
        Binding(get: { model.searchPresented }, set: { model.searchPresented = $0 })
    }

    /// The search bar in its own toolbar item, with its own capsule: the toolbar's shared capsule would not follow its
    /// width while it grows and shrinks.
    @ToolbarContentBuilder private var searchBarItem: some ToolbarContent {
        if #available(macOS 26, *) {
            ToolbarItem { searchBar }.sharedBackgroundVisibility(.hidden)
        } else {
            ToolbarItem { searchBar }
        }
    }

    private var searchBar: some View {
        ToolbarSearchBar(
            text: $query, focusRequest: searchPresented, windowWidth: windowWidth,
            maxWidth: wide ? searchBarWidth : nil,
            accessibilityLabel: model.route == .settings
                ? String(localized: "Search settings") : String(localized: "Search conversations")
        ) { frame in
            query = ""
            // The toolbar fades its buttons in, in an animated transaction, while the bar's ghost shrinks.
            if !reduceMotion { searchBarGhost = frame }
            withAnimation(reduceMotion ? nil : ToolbarSearchBar.animation) { searchExpanded = false }
        }
    }

    private var searchTerm: String { debouncedQuery.trimmingCharacters(in: .whitespacesAndNewlines) }

    /// Conversations whose title, speakers, transcript, summary or chat contain the query (case and accent
    /// insensitive), on the list and in the sidebar alike; all without one. Title only until a document is read.
    private var searchResults: [Conversation] {
        let term = searchTerm
        guard !term.isEmpty else { return model.conversations }
        return model.conversations.filter { conversation in
            guard var document = searchDocuments[conversation.id]?.document else {
                return conversation.title.localizedStandardContains(term)
            }
            // The current title: a rename does not change the size.
            document.title = conversation.title
            return document.matches(term)
        }
    }

    /// Reads the documents not read yet, or stale (size changed, an AI job ended or a speaker was renamed since);
    /// titles match meanwhile.
    /// Missing transcript, summary or chat files leave their fields empty.
    private func loadSearchDocuments() async {
        let jobsEnded = model.aiJobsEnded
        let transcriptEdits = model.transcriptEdits
        let stale = model.conversations.filter { conversation in
            guard conversation.hasTranscript else { return false }
            let cached = searchDocuments[conversation.id]
            return cached?.size != conversation.sizeBytes || cached?.jobsEnded != jobsEnded
                || cached?.edits != transcriptEdits[conversation.id, default: 0]
        }
        guard !debouncedQuery.isEmpty, model.route != .settings, !stale.isEmpty else { return }
        let loaded = await Task.detached(priority: .utility) {
            stale.map { conversation in
                let document = ConversationSearch.document(
                    title: conversation.title,
                    transcript: try? Transcript.read(from: conversation.folder),
                    summary: Conversation.readSummary(from: conversation.folder),
                    // The open chat and the archived ones: archiving or resuming one changes no search result.
                    chat: ChatLog(messages: ChatLog.read(from: conversation.folder).messages
                                  + ChatArchive.messages(in: conversation.folder)))
                return (conversation.id, SearchDocument(size: conversation.sizeBytes, jobsEnded: jobsEnded,
                                                        edits: transcriptEdits[conversation.id, default: 0],
                                                        document: document))
            }
        }.value
        guard !Task.isCancelled else { return }
        for (id, document) in loaded { searchDocuments[id] = document }
    }
}

/// The main window's search bar, in place of the toolbar's buttons: it grows out of the Search button's place to the
/// toolbar's width (or `maxWidth` at the trailing edge in a wide window), and shrinks back before the buttons return.
/// Not `.searchable`: the system search item widened when focused and pushed Record, Settings and AI into the
/// toolbar's overflow menu in the 440 pt wide window.
private struct ToolbarSearchBar: View {
    static let height: CGFloat = 34
    /// From the window's trailing edge, like the toolbar's buttons.
    static let trailingInset: CGFloat = 8
    /// At most, in a wide window, which keeps Back, Record, Update and the tabs beside the bar.
    static let wideWidth: CGFloat = 360
    /// Growing and shrinking.
    static let animation = Animation.snappy(duration: 0.3)

    @Binding var text: String
    /// ⌘F sets it to focus the field; false again once focused.
    @Binding var focusRequest: Bool
    let windowWidth: CGFloat
    /// A fixed width after a flexible spacer (the bar then sits at the trailing edge); nil fills the toolbar from the
    /// bar's leading edge.
    let maxWidth: CGFloat?
    /// The field's label and placeholder, which name what it searches.
    let accessibilityLabel: String
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
            SearchTextField(
                text: $text, focusRequest: $focusRequest, accessibilityLabel: accessibilityLabel, onEscape: close
            )
                .opacity(expanded ? 1 : 0)
            // As big as the Search button; its highlight, inset from the bar's top and bottom like a group button's.
            Button(action: close) {
                Image(systemName: "xmark")
                    .font(.body)
                    .imageScale(.large)
                    .frame(width: Self.height - 16, height: Self.height - 16)
            }
            .buttonStyle(RowIconButtonStyle())
            .frame(width: Self.height, height: Self.height)
            .help("Close search (Esc)")
            .opacity(expanded ? 1 : 0)
        }
        .frame(width: expanded ? nil : Self.height, height: Self.height, alignment: .leading)
        .clipped()
        .modifier(InputCapsule())
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
        if let maxWidth { return maxWidth }
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
    let accessibilityLabel: String
    let onEscape: () -> Void

    func makeNSView(context: Context) -> Field {
        let field = Field()
        field.isBordered = false
        field.drawsBackground = false
        field.focusRingType = .none
        field.usesSingleLineMode = true
        field.cell?.isScrollable = true
        field.delegate = context.coordinator
        return field
    }

    func updateNSView(_ field: Field, context: Context) {
        context.coordinator.parent = self
        field.setAccessibilityLabel(accessibilityLabel)
        field.placeholderString = accessibilityLabel
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
            .modifier(InputCapsule())
            // Seen while it shrinks, gone as it reaches the Search button.
            .animation(.easeIn(duration: 0.3)) { $0.opacity(collapsed ? 0 : 1) }
            .frame(width: frame.width, alignment: .trailing)
            .offset(x: frame.minX, y: frame.minY)
            .onAppear {
                withAnimation(ToolbarSearchBar.animation) { collapsed = true } completion: { onDone() }
            }
    }
}

/// The search bar's capsule, an input: `Palette.inputSurface` with the cards' edge. The bar and its ghost share it so
/// the close animation keeps the same color.
private struct InputCapsule: ViewModifier {
    func body(content: Content) -> some View {
        content
            .background(Capsule().fill(Palette.inputSurface))
            .overlay(Capsule().strokeBorder(Palette.surfaceBorder, lineWidth: 1).allowsHitTesting(false))
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

/// A toolbar button alone in its capsule: on macOS 26 AppKit gives it no hover highlight (only buttons of a group get
/// one), so it draws the same small round highlight itself.
private struct SingleToolbarButton: View {
    let title: LocalizedStringKey
    let systemImage: String
    let action: () -> Void

    var body: some View {
        if #available(macOS 26, *) {
            Button(action: action) {
                Label(title, systemImage: systemImage)
                    .labelStyle(.iconOnly)
                    .font(.body)
                    .imageScale(.large)
            }
            .buttonStyle(Highlight())
        } else {
            Button(action: action) { Label(title, systemImage: systemImage) }
        }
    }

    /// AppKit pads the item less on the sides than above and below: 36 by 24 makes the capsule round. The highlight
    /// is as big as a group button's.
    private struct Highlight: ButtonStyle {
        func makeBody(configuration: Configuration) -> some View {
            HoverCircle(configuration: configuration)
        }

        private struct HoverCircle: View {
            let configuration: ButtonStyleConfiguration
            @State private var hovered = false

            var body: some View {
                configuration.label
                    .frame(width: 36, height: 24)
                    .background {
                        Circle()
                            .fill(configuration.isPressed ? Palette.pressedFill : hovered ? Palette.hoverFill : .clear)
                            .frame(width: 28, height: 28)
                    }
                    .contentShape(Rectangle())
                    .animation(Motion.hover, value: hovered)
                    .onHover { hovered = $0 }
            }
        }
    }
}

private extension ToolbarContent {
    /// On macOS 26.1, kept out of the overflow menu longer than the other items.
    @ToolbarContentBuilder func highVisibilityPriority() -> some ToolbarContent {
        if #available(macOS 26.1, *) {
            visibilityPriority(.high)
        } else {
            self
        }
    }
}

/// On macOS 26, a hard top edge for the scroll views inside: the soft edge effect would fade content into the toolbar.
private struct HardTopScrollEdge: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 26, *) {
            content.scrollEdgeEffectStyle(.hard, for: .top)
        } else {
            content
        }
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
