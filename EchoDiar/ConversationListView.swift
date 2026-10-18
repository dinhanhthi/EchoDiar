import EchoCore
import SwiftUI

/// The library as cards under pinned day headers. Shows the groups it is given, so callers can filter them.
struct ConversationListView: View {
    let model: AppModel
    let groups: [DayGroup]
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ScrollView {
            GlassCards {
                LazyVStack(alignment: .leading, spacing: 10, pinnedViews: .sectionHeaders) {
                    ForEach(groups) { group in
                        Section {
                            ForEach(group.conversations) { conversation in
                                ConversationCard(conversation: conversation, model: model)
                                    .transition(cardTransition)
                            }
                        } header: {
                            DayHeader(day: group.day)
                        }
                    }
                }
                .padding(12)
                .animation(.snappy, value: groups)
            }
        }
    }

    /// A small scale with a fade; only the fade with Reduce Motion.
    private var cardTransition: AnyTransition {
        reduceMotion ? .opacity : .scale(scale: 0.96).combined(with: .opacity)
    }
}

/// "Today", "Yesterday", or the date.
private struct DayHeader: View {
    let day: Date

    var body: some View {
        Text(title)
            .font(.subheadline.weight(.semibold))
            .padding(.horizontal, 10)
            .padding(.vertical, 4)
            .modifier(GlassCard(cornerRadius: 10))
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.top, 4)
            .accessibilityAddTraits(.isHeader)
    }

    private var title: String {
        let calendar = Calendar.current
        if calendar.isDateInToday(day) { return "Today" }
        if calendar.isDateInYesterday(day) { return "Yesterday" }
        return day.formatted(date: .long, time: .omitted)
    }
}
