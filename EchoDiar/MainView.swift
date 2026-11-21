import AppKit
import EchoAudio
import EchoCore
import SwiftUI

/// The main window, three pages: the library (the cards as a sidebar with its own search field and pages, then a
/// conversation, the open recording while recording, or the welcome pane), Settings and the Calendar. The sidebar
/// button, Back, Record (or Stop and Pause) and a conversation's title on the leading side; a conversation's Reveal in
/// Finder, Settings, Calendar and the library chat, then the conversation's tabs on the trailing side. The library
/// chat takes the sidebar's place.
struct MainView: View {
    let model: AppModel
    let updater: Updater
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// The sidebar's search query.
    @State private var query = ""
    /// `query` once typing pauses for 200 ms (at once when cleared): what the cards are filtered by.
    @State private var debouncedQuery = ""
    /// Searchable text by conversation (speakers, transcript, summary, chat), read off the main thread only once a
    /// query is typed; read again when the conversation's size changes (a new transcript) or an AI job ends (a new
    /// summary or chat). Released with the window.
    @State private var searchDocuments: [UUID: SearchDocument] = [:]
    /// The Settings page's search query: while not empty, Settings lists only the matching settings.
    @State private var settingsQuery = ""
    /// The sidebar's page (0-based) of the sorted, searched list; shown clamped to the pages that exist.
    @State private var listPage = 0
    /// The window's width, which the toolbar title's room is measured from.
    @State private var windowWidth: CGFloat = 0
    /// The route on screen; it follows `model.route`, with a slide when the page changes.
    @State private var shownRoute = AppModel.Route.list
    /// The last page change went to a page further right (library, Settings, Calendar).
    @State private var forward = true
    /// The toolbar title's rename popover is open, with `titleDraft` in its field.
    @State private var renamingTitle = false
    @State private var titleDraft = ""

    private struct SearchDocument {
        let size: Int64
        let jobsEnded: Int
        let edits: Int
        let chatChanges: Int
        let document: ConversationSearch.Document
    }

    /// Reloads the search documents while a query is typed: the conversations changed, an AI job ended, a
    /// speaker was renamed, or an earlier chat was deleted.
    private struct SearchLoadKey: Equatable {
        let conversations: [Conversation]
        let jobsEnded: Int
        let transcriptEdits: [UUID: Int]
        let chatChanges: Int
    }

    /// The pages that slide, left to right; moving between the list, conversations and the recording stays on the
    /// library page, only its detail changes.
    private enum Page: Int {
        case library, settings, calendar

        init(_ route: AppModel.Route) {
            switch route {
            case .settings: self = .settings
            case .calendar: self = .calendar
            default: self = .library
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
                case .library: libraryPage.transition(pageTransition)
                // Capsule buttons on these two pages only; the toolbar and the library keep their own styles.
                case .settings:
                    SettingsView(model: model, query: $settingsQuery)
                        .buttonBorderShape(.capsule).transition(pageTransition)
                case .calendar: CalendarView(model: model).buttonBorderShape(.capsule).transition(pageTransition)
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
            // Another page clears both queries; another entry or the welcome pane keeps the sidebar's. ⌘F on the
            // Calendar page goes back to the library to focus the sidebar's field there.
            query = ""
            settingsQuery = ""
            guard !(Page(old) == .calendar && model.searchPresented) else { return }
            model.searchPresented = false
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
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { windowWidth = $0 }
        .task(id: query) {
            if !query.isEmpty { try? await Task.sleep(for: .milliseconds(200)) }
            if !Task.isCancelled { debouncedQuery = query }
        }
        // Settings search filters the settings, not the library: no documents to read there (nor on the Calendar).
        .task(id: debouncedQuery.isEmpty || Page(model.route) != .library
                ? nil : SearchLoadKey(conversations: model.conversations, jobsEnded: model.aiJobsEnded,
                                transcriptEdits: model.transcriptEdits, chatChanges: model.chatHistoryChanges)) {
            await loadSearchDocuments()
        }
        // A new search or page size starts on the first page.
        .onChange(of: debouncedQuery) { listPage = 0 }
        .onChange(of: model.settings.libraryPageSize) { listPage = 0 }
        // A conversation opened from outside the list (Calendar, a citation) shows on its page. Only here: paging
        // with a conversation open stays on the new page.
        .onChange(of: selectedID) { _, id in
            guard let id, let index = pagedList.firstIndex(where: { $0.id == id }) else { return }
            listPage = LibraryPaging.page(containing: index, size: model.settings.libraryPageSize)
        }
        .toolbar {
            leadingItems
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
                    get: { model.route == .calendar },
                    set: { if $0 { model.openCalendar() } else { model.goBack() } }
                )) {
                    Label("Calendar", systemImage: "calendar")
                }
                .help("Calendar")
                Toggle(isOn: Binding(
                    get: { model.globalChatOpen && Page(model.route) == .library },
                    set: { open in
                        // Settings opened from the Calendar page goes back twice, to the library.
                        if model.route == .settings { model.goBack() }
                        if model.route == .calendar { model.goBack() }
                        model.globalChatOpen = open
                        guard open else { return }
                        query = ""
                        model.searchPresented = false
                    }
                )) {
                    Label("Ask across conversations", systemImage: "sparkles")
                }
                .help("Ask across conversations")
            }
            tabsItem
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
            Text("Audio leaves this Mac: the recording's mic and system channels are uploaded to \(model.pendingCloudRuns.first?.providerName ?? String(localized: "the provider")) (\(model.pendingCloudRuns.first?.host ?? "")) for transcription and billed by them. The provider may keep the audio under its own retention policy. You are asked once per provider and destination.")
        }
    }

    /// On the library page the sidebar button in its own capsule, then Back in its own capsule (not on the list without
    /// the library chat), the record capsule, then a conversation's title. The record capsule: while idle Record and a
    /// chevron menu that picks the target and the language; while recording, Stop (red, pulsing unless paused or Reduce
    /// Motion) and Pause/Resume. ⌘R and ⇧⌘P come from the main menu. In a narrow window the other buttons go to the
    /// overflow menu first.
    @ToolbarContentBuilder private var leadingItems: some ToolbarContent {
        if Page(model.route) == .library {
            ToolbarItem(placement: leadingPlacement) {
                SingleToolbarButton(title: model.sidebarHidden ? "Show Sidebar" : "Hide Sidebar",
                                    systemImage: "sidebar.left", action: model.toggleSidebar)
                    .help(model.sidebarHidden ? "Show Sidebar (⌃⌘S)" : "Hide Sidebar (⌃⌘S)")
            }
            .highVisibilityPriority()
            fixedSpacer
        }
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
                        Toggle(isOn: targetBinding(.app(group))) {
                            if group.needsGlobalTap {
                                Text("\(group.name) (records all system audio)")
                            } else {
                                Text(verbatim: group.name)
                            }
                        }
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

    /// Its own capsule, green, before Settings (or a conversation's Reveal in Finder), while Sparkle has an
    /// update. Hidden while recording: installing relaunches the app.
    @ToolbarContentBuilder private var updateItem: some ToolbarContent {
        if showsUpdate {
            ToolbarItem {
                SingleToolbarButton(title: "Update", systemImage: "arrow.down.circle.fill", iconColor: .green,
                                    iconFont: .title2) {
                    updater.checkForUpdates()
                }
                .help("Install the available update")
            }
            fixedSpacer
        }
    }

    private var showsUpdate: Bool { updater.updateAvailable && !model.isRecording && !model.isStarting }

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
    /// Approximate widths of the other items: the traffic lights, the sidebar button, Back and the record capsule; the
    /// tabs; Reveal in Finder with Delete, Settings, Calendar and the library chat.
    private static let leadingWidth: CGFloat = 302
    private static let tabsWidth: CGFloat = 240
    private static let trailingButtonsWidth: CGFloat = 244

    private var updateWidth: CGFloat { showsUpdate ? 52 : 0 }

    /// What the toolbar's other items leave of the window, at most `titleMaxWidth`: the title truncates instead of
    /// pushing buttons into the overflow menu.
    private var titleWidth: CGFloat {
        min(Self.titleMaxWidth,
            windowWidth - Self.leadingWidth - Self.tabsWidth - Self.trailingButtonsWidth - updateWidth)
    }

    /// Too short a title is left out.
    private var titleFits: Bool { titleWidth >= Self.titleMinWidth }

    /// A conversation's Reveal in Finder and Delete menu (the audio or the whole conversation) in one capsule, before
    /// Settings.
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
    /// while Settings is searched: its results replace the section.
    @ToolbarContentBuilder private var settingsTabsItem: some ToolbarContent {
        let section = model.settingsTab
        if model.route == .settings, settingsQuery.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
           !section.subTabs.isEmpty {
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

    /// A conversation's tabs, in a capsule of their own like the buttons', at the trailing edge after Settings and the
    /// library chat.
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
        return String(localized: "All system audio")
    }

    /// The page switches.
    private static let paneAnimation = Motion.standard

    /// Pages slide a little in their order: a page further right comes in from the trailing side. While the route is
    /// ahead of `shownRoute` the direction comes from both: the leaving page keeps the transition it last rendered with.
    /// The leaving page fades out faster, so the two pages never show through each other.
    private var pageTransition: AnyTransition {
        if reduceMotion { return .opacity }
        let page = Page(model.route), shown = Page(shownRoute)
        let forward = page == shown ? forward : page.rawValue >= shown.rawValue
        let travel: CGFloat = forward ? 24 : -24
        return .asymmetric(
            insertion: .offset(x: travel).combined(with: .opacity),
            removal: .offset(x: -travel).combined(with: .opacity).animation(.easeOut(duration: 0.1)))
    }

    /// The library page: the cards (or the library chat) as a sidebar, then the detail. The sidebar stays while the
    /// detail changes; hidden (⌃⌘S, the toolbar's sidebar button), it slides out and the detail takes the window.
    private var libraryPage: some View {
        // Both panes pad 12 pt on every side; overlapping those paddings by 6 pt leaves an 18 pt gap between them.
        HStack(spacing: -6) {
            if !model.sidebarHidden {
                chatOr(libraryWell(libraryPane(selectedID: selectedID)))
                    .frame(width: 360)
                    .transition(reduceMotion ? .opacity : .move(edge: .leading).combined(with: .opacity))
            }
            detail
        }
        .animation(reduceMotion ? nil : Self.paneAnimation, value: model.sidebarHidden)
        // The toolbar's safe area already leaves room above the panes: 4 pt of their 12 pt top padding is enough.
        .padding(.top, -8)
    }

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

    /// The sidebar's cards (or why there are none) on a panel like the detail's, the cards a step lighter.
    private func libraryWell(_ pane: some View) -> some View {
        let shape = RoundedRectangle(cornerRadius: 20, style: .continuous)
        return pane
            .background(Palette.surface)
            .clipShape(shape)
            .overlay(shape.strokeBorder(Palette.surfaceBorder, lineWidth: 1).allowsHitTesting(false))
            .padding(12)
    }

    /// The sidebar's selected card: the shown conversation.
    private var selectedID: UUID? {
        if case .conversation(let id) = shownRoute { return id }
        return nil
    }

    /// The shown conversation (its state and playback reset per conversation), or the recording while it runs; the
    /// welcome pane on the list, for a conversation that is gone and once the recording has ended.
    @ViewBuilder private var detail: some View {
        if case .conversation(let id) = shownRoute,
           let conversation = model.conversations.first(where: { $0.id == id }) {
            TranscriptView(conversation: conversation, model: model)
                .id(id)
        } else if shownRoute == .recording && model.isRecording {
            RecordingDetailView(model: model)
        } else {
            WelcomeView(model: model)
        }
    }

    /// The sidebar's search field, then the cards of the current page matching the search (or why there are none),
    /// then the page bar while there is more than one page. The recording's card stays on top while recording, alone
    /// when the library is still empty and nothing is searched.
    private func libraryPane(selectedID: UUID?) -> some View {
        let list = pagedList
        let size = model.settings.libraryPageSize
        let page = LibraryPaging.clamp(page: listPage, total: list.count, size: size)
        let range = LibraryPaging.range(page: page, total: list.count, size: size)
        let sections = LibrarySections.sections(from: Array(list[range]), now: .now)
        return VStack(spacing: 0) {
            sidebarSearchField
                .padding([.horizontal, .top], 12)
            Group {
                if sections.isEmpty && model.isRecording && searchTerm.isEmpty {
                    ConversationListView(model: model, sections: [], selectedID: selectedID)
                } else if !model.conversations.contains(where: listed) {
                    emptyState("No conversations yet")
                } else if sections.isEmpty {
                    emptyState("No conversations match \"\(searchTerm)\"")
                } else {
                    ConversationListView(model: model, sections: sections, selectedID: selectedID)
                        // A new page starts at the top (centered on the open card when it is on that page).
                        .id(page)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            if LibraryPaging.pageCount(total: list.count, size: size) > 1 {
                pageBar(page: page, range: range, total: list.count, size: size)
            }
        }
        // Conversations added or deleted: still on a page that exists.
        .onChange(of: list.count) { _, total in
            listPage = LibraryPaging.clamp(page: listPage, total: total, size: size)
        }
    }

    /// The listed conversations matching the search, newest first: what the sidebar's pages slice.
    private var pagedList: [Conversation] {
        LibrarySections.sorted(searchResults.filter(listed))
    }

    /// "‹ 1–100 of 742 ›" under the cards; the arrows are dimmed at the first and last page.
    private func pageBar(page: Int, range: Range<Int>, total: Int, size: Int) -> some View {
        let last = LibraryPaging.pageCount(total: total, size: size) - 1
        return HStack(spacing: 12) {
            Button {
                listPage = page - 1
            } label: {
                Image(systemName: "chevron.left").frame(width: 20, height: 20)
            }
            .disabled(page == 0)
            .help("Previous page")
            .accessibilityLabel("Previous page")
            Text(String(localized: "\(range.lowerBound + 1)–\(range.upperBound) of \(total)"))
                .font(.callout)
                .monospacedDigit()
                .foregroundStyle(.secondary)
            Button {
                listPage = page + 1
            } label: {
                Image(systemName: "chevron.right").frame(width: 20, height: 20)
            }
            .disabled(page >= last)
            .help("Next page")
            .accessibilityLabel("Next page")
        }
        .buttonStyle(RowIconButtonStyle())
        .frame(maxWidth: .infinity)
        .padding(.vertical, 8)
        .overlay(alignment: .top) { Divider() }
    }

    /// The sidebar's search field, above the cards and the empty states. ⌘F focuses it (`model.searchPresented`);
    /// Esc clears the query, then gives up the focus.
    private var sidebarSearchField: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(.secondary)
            SearchTextField(text: $query, focusRequest: searchPresented,
                            accessibilityLabel: String(localized: "Search conversations")) {
                if query.isEmpty {
                    NSApp.keyWindow?.makeFirstResponder(nil)
                } else {
                    query = ""
                }
            }
            if !query.isEmpty {
                Button {
                    query = ""
                } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help("Clear search")
                .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 10)
        .frame(height: 30)
        .modifier(InputCapsule())
    }

    /// The recording's own entry is hidden while recording: its card is the recording card.
    private func listed(_ conversation: Conversation) -> Bool {
        !(model.isRecording && conversation.status == .recording)
    }

    private func emptyState(_ text: LocalizedStringKey) -> some View {
        VStack(spacing: 0) {
            if model.isRecording {
                RecordingCard(model: model, selected: model.route == .recording)
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

    /// Reads the documents not read yet, or stale (size changed, an AI job ended, a speaker was renamed or a chat
    /// history changed since);
    /// titles match meanwhile.
    /// Missing transcript, summary or chat files leave their fields empty.
    private func loadSearchDocuments() async {
        let jobsEnded = model.aiJobsEnded
        let transcriptEdits = model.transcriptEdits
        let chatChanges = model.chatHistoryChanges
        let stale = model.conversations.filter { conversation in
            guard conversation.hasTranscript else { return false }
            let cached = searchDocuments[conversation.id]
            return cached?.size != conversation.sizeBytes || cached?.jobsEnded != jobsEnded
                || cached?.edits != transcriptEdits[conversation.id, default: 0] || cached?.chatChanges != chatChanges
        }
        guard !debouncedQuery.isEmpty, Page(model.route) == .library, !stale.isEmpty else { return }
        let loaded = await Task.detached(priority: .utility) {
            stale.map { conversation in
                let document = ConversationSearch.document(
                    title: conversation.title,
                    transcript: try? Transcript.read(from: conversation.folder),
                    summary: Conversation.readSummary(from: conversation.folder),
                    // The open chat and the archived ones: archiving or resuming one changes no search result, deleting
                    // one does.
                    chat: ChatLog(messages: ChatLog.read(from: conversation.folder).messages
                                  + ChatArchive.messages(in: conversation.folder)))
                return (conversation.id, SearchDocument(size: conversation.sizeBytes, jobsEnded: jobsEnded,
                                                        edits: transcriptEdits[conversation.id, default: 0],
                                                        chatChanges: chatChanges, document: document))
            }
        }.value
        guard !Task.isCancelled else { return }
        for (id, document) in loaded { searchDocuments[id] = document }
    }
}

/// A search field's text field (the library sidebar's, Settings'), drawn by its container. AppKit's: it takes the
/// focus asked for before it is in a window (⌘F from the Calendar page, or with the sidebar hidden) once it is.
struct SearchTextField: NSViewRepresentable {
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
            // A field just shown may not be in the window yet: it focuses itself once it is.
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

        /// Escape is the container's.
        func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
            guard selector == #selector(NSResponder.cancelOperation(_:)) else { return false }
            parent.onEscape()
            return true
        }
    }
}

/// The sidebar search fields' capsule (the library's, Settings'), an input: `Palette.inputSurface` with the cards' edge.
struct InputCapsule: ViewModifier {
    func body(content: Content) -> some View {
        content
            .background(Capsule().fill(Palette.inputSurface))
            .overlay(Capsule().strokeBorder(Palette.surfaceBorder, lineWidth: 1).allowsHitTesting(false))
    }
}

/// A toolbar button alone in its capsule: on macOS 26 AppKit gives it no hover highlight (only buttons of a group get
/// one), so it draws the same small round highlight itself.
private struct SingleToolbarButton: View {
    let title: LocalizedStringKey
    let systemImage: String
    /// The icon's color, like Stop's red; the toolbar's own when nil.
    var iconColor: Color?
    /// On macOS 26: a bigger font for an icon that should stand out (Update).
    var iconFont: Font = .body
    let action: () -> Void

    var body: some View {
        if #available(macOS 26, *) {
            Button(action: action) {
                label
                    .labelStyle(.iconOnly)
                    .font(iconFont)
                    .imageScale(.large)
            }
            .buttonStyle(Highlight())
        } else {
            Button(action: action) { label }
        }
    }

    private var label: some View {
        Label {
            Text(title)
        } icon: {
            if let iconColor {
                Image(systemName: systemImage).foregroundStyle(iconColor)
            } else {
                Image(systemName: systemImage)
            }
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
