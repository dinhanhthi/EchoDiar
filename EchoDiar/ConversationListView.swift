import EchoCore
import SwiftUI

/// The library as cards under pinned day headers, the recording's card above them while recording. Shows the groups
/// it is given, so callers can filter them. Compact cards for the wide layout's sidebar, where the open entry is
/// selected and scrolled into view.
struct ConversationListView: View {
    let model: AppModel
    let groups: [DayGroup]
    var compact = false
    var selectedID: UUID?
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
                                    selected: conversation.id == selectedID)
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

    /// A small scale with a fade; only the fade with Reduce Motion.
    private var cardTransition: AnyTransition {
        reduceMotion ? .opacity : .scale(scale: 0.96).combined(with: .opacity)
    }
}

/// "Today", "Yesterday", or the date as muted text, centered on a rule that stays inside the list's padding, with a
/// gap on both sides. On the window's glass, so cards scrolling under the pinned header do not show through.
private struct DayHeader: View {
    let day: Date

    var body: some View {
        HStack(spacing: 10) {
            rule
            Text(title)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
                .fixedSize()
                .accessibilityAddTraits(.isHeader)
            rule
        }
        .padding(.vertical, 6)
        // The list's 12 pt padding, so the background reaches the column edges.
        .background(WindowGlass().padding(.horizontal, -12))
    }

    private var rule: some View {
        Rectangle()
            .fill(Color.white.opacity(0.12))
            .frame(height: 1)
            .accessibilityHidden(true)
    }

    private var title: String {
        let calendar = Calendar.current
        if calendar.isDateInToday(day) { return "Today" }
        if calendar.isDateInYesterday(day) { return "Yesterday" }
        return day.formatted(date: .long, time: .omitted)
    }
}
