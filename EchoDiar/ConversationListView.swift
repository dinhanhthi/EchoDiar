import EchoCore
import SwiftUI

extension Notification.Name {
    /// ⌘⌫ or Del on the library page (posted by the app delegate's key monitor).
    static let deleteInSidebar = Notification.Name("EchoDiar.deleteInSidebar")
    /// ↑ or ↓ on the library page, `userInfo["delta"]` -1 or 1 (posted by the app delegate's key monitor).
    static let moveInSidebar = Notification.Name("EchoDiar.moveInSidebar")
}

/// The library as compact cards under pinned day headers, in the library page's sidebar, the recording's card above
/// them while recording. Shows the groups it is given, so callers can filter them. The open entry is selected and
/// scrolled into view. ⌘-click and ⇧-click select cards (not the recording's), from the open entry as in Mail, and the
/// selection bar shows at the top while any is selected. ⌘⌫ or Del deletes the selection, else the open conversation;
/// ↑/↓ open the previous or next entry.
struct ConversationListView: View {
    let model: AppModel
    let groups: [DayGroup]
    var selectedID: UUID?
    @State private var selection = ListSelection()
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 10, pinnedViews: .sectionHeaders) {
                    if model.isRecording {
                        RecordingCard(model: model, selected: model.route == .recording)
                            .id(Self.recordingCardID)
                            .transition(cardTransition)
                    }
                    ForEach(groups) { group in
                        Section {
                            ForEach(group.conversations) { conversation in
                                ConversationCard(
                                    conversation: conversation, model: model,
                                    selected: selection.isEmpty
                                        ? conversation.id == selectedID : selection.ids.contains(conversation.id),
                                    select: { extend in
                                        // The sidebar's open entry is where the selection starts.
                                        if selection.isEmpty, let selectedID { selection.toggle(selectedID) }
                                        extend
                                            ? selection.extend(to: conversation.id, in: order)
                                            : selection.toggle(conversation.id)
                                    })
                                .id(conversation.id)
                                .transition(cardTransition)
                            }
                        } header: {
                            DayHeader(day: group.day)
                        }
                    }
                }
                .padding(12)
                .background(OverlayScrollers())
                .animation(reduceMotion ? nil : Motion.standard, value: groups)
                .animation(reduceMotion ? nil : Motion.standard, value: model.isRecording)
            }
            .safeAreaInset(edge: .top, spacing: 0) {
                if !selection.isEmpty {
                    SelectionBar(conversations: selectedConversations, model: model, onDelete: { audioOnly in
                        if DeleteMenuItems.delete(audioOnly: audioOnly, selectedConversations, model) { selection.clear() }
                    }, cancel: { selection.clear() })
                        .padding(.horizontal, 12)
                        .padding(.top, 12)
                        // Cards scrolling under the bar do not show through around it.
                        .background(Palette.surface)
                        .transition(cardTransition)
                }
            }
            .animation(reduceMotion ? nil : Motion.standard, value: selection.isEmpty)
            // Cards that left the list (search filter, deletion) are not selected.
            .onChange(of: groups) { selection.prune(keeping: order) }
            // Esc cancels the selection instead of leaving the entry.
            .onChange(of: selection.isEmpty) { model.listSelectionActive = !selection.isEmpty }
            .onDisappear { model.listSelectionActive = false }
            // Centered when the sidebar appears; later only scrolled as far as needed, so a click on a visible
            // card does not move the list.
            .onAppear {
                guard let selectedID else { return }
                withAnimation(nil) { proxy.scrollTo(selectedID, anchor: .center) }
            }
            // A plain click on another card opens it and ends the selection.
            .onChange(of: selectedID) {
                selection.clear()
                guard let selectedID else { return }
                withAnimation(reduceMotion ? nil : Motion.standard) { proxy.scrollTo(selectedID) }
            }
            // The recording's card, when ↑ opens it from below.
            .onChange(of: model.route) {
                guard model.route == .recording else { return }
                withAnimation(reduceMotion ? nil : Motion.standard) { proxy.scrollTo(Self.recordingCardID) }
            }
            .onReceive(NotificationCenter.default.publisher(for: .deleteInSidebar)) { _ in deleteFromKeyboard() }
            .onReceive(NotificationCenter.default.publisher(for: .moveInSidebar)) { note in
                guard let delta = note.userInfo?["delta"] as? Int else { return }
                move(by: delta)
            }
        }
    }

    private static let recordingCardID = "recordingCard"

    /// ⌘⌫ or Del: the selected cards, else the open conversation (the recording's card never). When the open
    /// conversation goes, the next card opens, the previous one for the last, else the list.
    private func deleteFromKeyboard() {
        var openID: UUID?
        if case .conversation(let id) = model.route { openID = id }
        if !selection.isEmpty {
            let targets = selectedConversations
            let deleting = Set(targets.filter { DeleteMenuItems.canDelete($0, model) }.map(\.id))
            // Before the delete, which goes back to the list at once. The next card opens only from there: not over a
            // page the route went to while the alert was up (a recording stopped, …), as on Windows.
            let next = openID.flatMap { deleting.contains($0) ? neighbour(of: $0, skipping: deleting) : nil }
            guard DeleteMenuItems.delete(audioOnly: false, targets, model) else { return }
            selection.clear()
            if let next, model.route == .list { model.open(next, tab: model.detailTab) }
        } else if let openID, let conversation = model.conversations.first(where: { $0.id == openID }),
                  DeleteMenuItems.canDelete(conversation, model) {
            let next = neighbour(of: openID, skipping: [openID])
            guard DeleteMenuItems.delete(audioOnly: false, conversation, model) else { return }
            if let next, model.route == .list { model.open(next, tab: model.detailTab) }
        }
    }

    /// The first card after `id` in list order that is not being deleted, else the nearest one before it.
    private func neighbour(of id: UUID, skipping deleting: Set<UUID>) -> UUID? {
        guard let index = order.firstIndex(of: id) else { return nil }
        return order[(index + 1)...].first { !deleting.contains($0) }
            ?? order[..<index].last { !deleting.contains($0) }
    }

    /// ↑/↓ with no card selected: the previous or next entry (the recording's card first while recording), opened as
    /// a click on its card opens it; the first or the last when none is open. Stops at the ends.
    private func move(by delta: Int) {
        guard selection.isEmpty else { return }
        let entries: [AppModel.Route] = (model.isRecording ? [.recording] : []) + order.map { .conversation($0) }
        let target = entries.firstIndex(of: model.route).map { $0 + delta } ?? (delta > 0 ? 0 : entries.count - 1)
        guard entries.indices.contains(target) else { return }
        switch entries[target] {
        case .conversation(let id): model.open(id, tab: model.detailTab)
        default: model.route = .recording
        }
    }

    /// The cards' ids in list order, across day groups.
    private var order: [UUID] { groups.flatMap(\.conversations).map(\.id) }

    /// The selected conversations in list order.
    private var selectedConversations: [Conversation] {
        groups.flatMap(\.conversations).filter { selection.ids.contains($0.id) }
    }

    /// A small scale with a fade; only the fade with Reduce Motion.
    private var cardTransition: AnyTransition {
        reduceMotion ? .opacity : .scale(scale: 0.96).combined(with: .opacity)
    }
}

/// The selection's count, a Delete menu (the audio only or the conversations; `onDelete` gets whether audio only;
/// each disabled when no selected conversation it applies to can be deleted now) and Cancel (also Esc), on a raised
/// card.
private struct SelectionBar: View {
    let conversations: [Conversation]
    let model: AppModel
    let onDelete: (_ audioOnly: Bool) -> Void
    let cancel: () -> Void

    /// Like the chat tab's bottom bar menus.
    private static let buttonHeight: CGFloat = 28

    var body: some View {
        let asks = !model.settings.deleteWithoutConfirmation
        let deletable = conversations.filter { DeleteMenuItems.canDelete($0, model) }
        let withAudio = deletable.contains(where: \.hasAudio)
        HStack(spacing: 12) {
            Text("\(conversations.count) selected")
                .font(.subheadline.weight(.semibold))
                .lineLimit(1)
                .fixedSize()
            Spacer()
            Menu {
                Button(asks ? "Delete audio only…" : "Delete audio only") { onDelete(true) }
                    .disabled(!withAudio)
                Button(asks ? "Delete conversations…" : "Delete conversations") { onDelete(false) }
            } label: {
                HStack(spacing: 4) {
                    Image(systemName: "trash").accessibilityLabel("Delete")
                    MenuChevron()
                }
            }
            .menuStyle(.button)
            .buttonStyle(BottomBarButtonStyle(height: Self.buttonHeight, horizontalPadding: 10))
            .menuIndicator(.hidden)
            .fixedSize()
            .disabled(deletable.isEmpty)
            .help("Delete the audio or the selected conversations")
            Button(action: cancel) {
                Label("Cancel", systemImage: "xmark").labelStyle(.iconOnly)
            }
            .buttonStyle(BottomBarButtonStyle(height: Self.buttonHeight, horizontalPadding: 0))
            .keyboardShortcut(.cancelAction)
            .help("Cancel the selection (Esc)")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(GlassCard(cornerRadius: 20, raised: true, fill: Palette.raisedSurface))
    }
}

/// "Today", "Yesterday", or the date as muted text, centered. On the sidebar's panel, so cards scrolling under the
/// pinned header do not show through.
private struct DayHeader: View {
    let day: Date

    var body: some View {
        Text(title)
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
            .accessibilityAddTraits(.isHeader)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 6)
            // The list's 12 pt padding, so the background reaches the column edges.
            .background(Palette.surface.padding(.horizontal, -12))
    }

    private var title: String {
        let calendar = Calendar.current
        if calendar.isDateInToday(day) { return String(localized: "Today") }
        if calendar.isDateInYesterday(day) { return String(localized: "Yesterday") }
        return day.formatted(date: .long, time: .omitted)
    }
}

/// Thin scrollers that show only while scrolling, as with a trackpad, even when a mouse makes macOS show the wide
/// always-visible ones: those look too heavy on the sidebar's panel. Placed in the scroll content, to reach its
/// `NSScrollView`; AppKit resets the style when the system preference changes, so it is set again then.
private struct OverlayScrollers: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { Bridge() }
    func updateNSView(_ view: NSView, context: Context) {}

    private final class Bridge: NSView {
        private var observer: NSObjectProtocol?

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            apply()
            guard observer == nil else { return }
            observer = NotificationCenter.default.addObserver(
                forName: NSScroller.preferredScrollerStyleDidChangeNotification, object: nil, queue: .main
            ) { [weak self] _ in
                // After the scroll view's own handler, which sets the preferred style.
                DispatchQueue.main.async { self?.apply() }
            }
        }

        deinit {
            if let observer { NotificationCenter.default.removeObserver(observer) }
        }

        private func apply() {
            // The scroller keeps its own style once made: set both.
            enclosingScrollView?.scrollerStyle = .overlay
            enclosingScrollView?.verticalScroller?.scrollerStyle = .overlay
        }
    }
}
