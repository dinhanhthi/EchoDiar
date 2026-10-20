import EchoCore
import Foundation
import Testing

/// Guards multi-select in the conversation list: ⇧-click selects the inclusive span from the anchor in list order,
/// either direction, on top of ⌘-clicked ids, and ids that left the list are dropped.
struct ListSelectionTests {
    private let order = (0..<6).map { _ in UUID() }

    @Test func extendSelectsTheSpanInListOrderAndKeepsToggledIds() {
        var selection = ListSelection()
        selection.toggle(order[0])
        selection.toggle(order[4])

        selection.extend(to: order[2], in: order)
        #expect(selection.ids == [order[0], order[2], order[3], order[4]])
        #expect(selection.anchor == order[4])

        selection.extend(to: order[5], in: order)
        #expect(selection.ids == [order[0], order[2], order[3], order[4], order[5]])
    }

    @Test func pruneDropsIdsThatLeftTheList() {
        var selection = ListSelection()
        selection.toggle(order[1])
        selection.toggle(order[3])

        selection.prune(keeping: [order[0], order[1], order[2]])

        #expect(selection.ids == [order[1]])
        #expect(selection.anchor == nil)
    }
}
