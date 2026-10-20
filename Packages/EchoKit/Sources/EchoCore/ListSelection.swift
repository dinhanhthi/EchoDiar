import Foundation

/// The selected rows of a list: ⌘-click toggles one row, ⇧-click adds the span from the anchor (the last ⌘-clicked
/// row) to the clicked one, in list order.
public struct ListSelection: Equatable, Sendable {
    public var ids: Set<UUID> = []
    /// Where a ⇧-click span starts.
    public var anchor: UUID?

    public init() {}

    public var isEmpty: Bool { ids.isEmpty }

    /// Selects `id`, or deselects it when selected, and makes it the anchor.
    public mutating func toggle(_ id: UUID) {
        if ids.remove(id) == nil { ids.insert(id) }
        anchor = id
    }

    /// Adds the inclusive span between the anchor and `id` in `order`, either direction; the anchor stays. Without an
    /// anchor in `order`, selects `id` and makes it the anchor. Does nothing when `id` is not in `order`.
    public mutating func extend(to id: UUID, in order: [UUID]) {
        guard let end = order.firstIndex(of: id) else { return }
        guard let anchor, let start = order.firstIndex(of: anchor) else {
            ids.insert(id)
            self.anchor = id
            return
        }
        ids.formUnion(order[min(start, end)...max(start, end)])
    }

    /// Drops the ids, and the anchor, not in `order`.
    public mutating func prune(keeping order: [UUID]) {
        let kept = Set(order)
        ids.formIntersection(kept)
        if let anchor, !kept.contains(anchor) { self.anchor = nil }
    }

    public mutating func clear() {
        ids = []
        anchor = nil
    }
}
