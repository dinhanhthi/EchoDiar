import EchoCore
import SwiftUI

/// The library as cards under pinned day headers, the recording's card above them while recording. Shows the groups
/// it is given, so callers can filter them. Compact cards for the wide layout's sidebar, where the open entry is
/// selected and scrolled into view. On the list page ⌘-click and ⇧-click select cards (not the recording's), and the
/// selection bar shows at the top while any is selected.
struct ConversationListView: View {
    let model: AppModel
    let groups: [DayGroup]
    var compact = false
    var selectedID: UUID?
    @State private var selection = ListSelection()
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 10, pinnedViews: .sectionHeaders) {
                    if model.isRecording {
                        RecordingCard(model: model, compact: compact, selected: model.route == .recording)
                            .transition(cardTransition)
                    }
                    ForEach(groups) { group in
                        Section {
                            ForEach(group.conversations) { conversation in
                                ConversationCard(
                                    conversation: conversation, model: model, compact: compact,
                                    selected: conversation.id == selectedID || selection.ids.contains(conversation.id),
                                    select: compact ? nil : { extend in
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
                .animation(.snappy, value: groups)
                .animation(.snappy, value: model.isRecording)
            }
            .safeAreaInset(edge: .top, spacing: 0) {
                if !compact && !selection.isEmpty {
                    SelectionBar(conversations: selectedConversations, model: model, onDelete: { audioOnly in
                        if DeleteMenuItems.delete(audioOnly: audioOnly, selectedConversations, model) { selection.clear() }
                    }, cancel: { selection.clear() })
                        .padding(.horizontal, 12)
                        .padding(.top, 12)
                        // Cards scrolling under the bar do not show through around it.
                        .background(WindowGlass())
                        .transition(cardTransition)
                }
            }
            .animation(.snappy, value: selection.isEmpty)
            // Cards that left the list (search filter, deletion) are not selected.
            .onChange(of: groups) { selection.prune(keeping: order) }
            // Centered when the sidebar appears; later only scrolled as far as needed, so a click on a visible
            // card does not move the list.
            .onAppear {
                guard let selectedID else { return }
                withAnimation(nil) { proxy.scrollTo(selectedID, anchor: .center) }
            }
            .onChange(of: selectedID) {
                guard let selectedID else { return }
                withAnimation(reduceMotion ? nil : .snappy) { proxy.scrollTo(selectedID) }
            }
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

    var body: some View {
        let asks = !model.settings.deleteWithoutConfirmation
        let deletable = conversations.filter { DeleteMenuItems.canDelete($0, model) }
        let withAudio = deletable.contains(where: \.hasAudio)
        HStack(spacing: 12) {
            Text("\(conversations.count) selected")
                .font(.subheadline.weight(.semibold))
            Spacer()
            Menu {
                Button(asks ? "Delete audio only…" : "Delete audio only") { onDelete(true) }
                    .disabled(!withAudio)
                Button(asks ? "Delete conversations…" : "Delete conversations") { onDelete(false) }
            } label: {
                Label("Delete", systemImage: "trash")
            }
            .fixedSize()
            .disabled(deletable.isEmpty)
            Button("Cancel", action: cancel)
                .keyboardShortcut(.cancelAction)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(GlassCard(cornerRadius: 20, raised: true))
    }
}

/// "Today", "Yesterday", or the date as muted text, centered. On the window's glass, so cards scrolling under the
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
            .background(WindowGlass().padding(.horizontal, -12))
    }

    private var title: String {
        let calendar = Calendar.current
        if calendar.isDateInToday(day) { return "Today" }
        if calendar.isDateInYesterday(day) { return "Yesterday" }
        return day.formatted(date: .long, time: .omitted)
    }
}
