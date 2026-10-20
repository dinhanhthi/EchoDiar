import Foundation

/// The app-wide "one heavy model at a time" lock, owned by `AppModel` and shared by every module that loads an ML
/// model. A holder loads, uses and unloads its model inside `withExclusiveModel`.
///
/// Policy: live captions have priority. A waiting live transcriber is served before any waiting batch job, and a
/// batch job waits for the gate while live captions hold it (the whole recording). A holder is never interrupted: a
/// batch job already running finishes first, so live captions started during it wait for its end.
public actor ModelLoadGate {
    public enum Owner: String, Sendable {
        case liveTranscript, transcriptionJob, llm
        /// A local text embedder building or querying the library index, held per batch.
        case embedder
    }

    private struct Waiter {
        let id: UUID
        let owner: Owner
        let continuation: CheckedContinuation<Void, any Error>
    }

    private var holder: Owner?
    private var waiters: [Waiter] = []

    public init() {}

    /// Runs `body` once no other owner holds the gate; throws `CancellationError` when cancelled while waiting.
    public func withExclusiveModel<T: Sendable>(
        owner: Owner, _ body: @Sendable () async throws -> T
    ) async throws -> T {
        try await acquire(owner)
        defer { release() }
        try Task.checkCancellation()
        return try await body()
    }

    private func acquire(_ owner: Owner) async throws {
        guard holder != nil else {
            holder = owner
            return
        }
        let id = UUID()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
                guard !Task.isCancelled else { return continuation.resume(throwing: CancellationError()) }
                let waiter = Waiter(id: id, owner: owner, continuation: continuation)
                if owner == .liveTranscript, let first = waiters.firstIndex(where: { $0.owner != .liveTranscript }) {
                    waiters.insert(waiter, at: first)
                } else {
                    waiters.append(waiter)
                }
            }
        } onCancel: {
            Task { await self.cancelWaiter(id) }
        }
    }

    /// Hands the gate to the next waiter, if any.
    private func release() {
        guard !waiters.isEmpty else {
            holder = nil
            return
        }
        let next = waiters.removeFirst()
        holder = next.owner
        next.continuation.resume()
    }

    private func cancelWaiter(_ id: UUID) {
        guard let index = waiters.firstIndex(where: { $0.id == id }) else { return }
        waiters.remove(at: index).continuation.resume(throwing: CancellationError())
    }
}
