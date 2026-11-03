import Foundation

/// The running AI jobs, at most one per conversation id (nil = the library chat). Cloud, local-server and CLI jobs
/// run side by side; in-app model jobs (MLX, Foundation Models) run one at a time through `ModelLoadGate`, so an in-app
/// job that is not the earliest-started one and has no text yet is shown as "Queued". Pure state: the caller starts
/// and cancels the tasks, and keeps streamed text elsewhere.
public struct AIJobTable<Payload> {
    public enum Kind: Sendable, Equatable {
        case summary, question, libraryQuestion, title
    }

    public enum Status: Sendable, Equatable {
        /// Started, no text yet (waiting for the gate, or the model before its first token).
        case running
        /// Its first text arrived.
        case streaming
    }

    public struct Entry {
        public let key: UUID?
        public let kind: Kind
        /// Runs on an in-app model, so it waits for the earlier in-app jobs.
        public let usesInAppModel: Bool
        /// When it started, from a counter that only grows.
        public let order: Int
        public var status: Status
        public var payload: Payload
    }

    private var entries: [UUID?: Entry] = [:]
    private var nextOrder = 0

    public init() {}

    /// Adds a job for `key`; false (and no change) when `key` already has one.
    @discardableResult
    public mutating func start(key: UUID?, kind: Kind, usesInAppModel: Bool, payload: Payload) -> Bool {
        guard entries[key] == nil else { return false }
        entries[key] = Entry(
            key: key, kind: kind, usesInAppModel: usesInAppModel, order: nextOrder, status: .running,
            payload: payload)
        nextOrder += 1
        return true
    }

    /// Removes the job of `key`, if any.
    public mutating func finish(key: UUID?) {
        entries[key] = nil
    }

    public func entry(for key: UUID?) -> Entry? {
        entries[key]
    }

    /// Conversations with a job; the library chat (nil) is not one of them.
    public var busyIDs: Set<UUID> {
        Set(entries.keys.compactMap { $0 })
    }

    public var isEmpty: Bool { entries.isEmpty }

    public var hasInAppJob: Bool { entries.values.contains { $0.usesInAppModel } }

    /// True for an in-app job without text yet while an earlier-started in-app job runs.
    public func isQueued(_ key: UUID?) -> Bool {
        guard let entry = entries[key], entry.usesInAppModel, entry.status == .running else { return false }
        return entries.values.contains { $0.usesInAppModel && $0.order < entry.order }
    }

    /// Records that the job of `key` got its first text.
    public mutating func markStreaming(key: UUID?) {
        entries[key]?.status = .streaming
    }
}

extension AIJobTable: Sendable where Payload: Sendable {}
extension AIJobTable.Entry: Sendable where Payload: Sendable {}
