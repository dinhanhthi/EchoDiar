@testable import EchoAI
import EchoCore
import Foundation
import Synchronization
import Testing

/// Guards the summary job: map-reduce sends status lines and streams only the final reduce, the result is saved,
/// a cancelled job saves nothing, and an in-app model is unloaded before the gate is released.
struct AIServiceTests {
    /// Answers every request with `reply`, in two pieces, and reports a small context to force map-reduce.
    final class FakeProvider: LLMProvider {
        let reply: String
        let context: Int
        let calls = Mutex(0)

        init(reply: String, context: Int) {
            self.reply = reply
            self.context = context
        }

        func stream(messages: [LLMMessage], model: String, maxTokens: Int) -> AsyncThrowingStream<String, Error> {
            calls.withLock { $0 += 1 }
            let middle = reply.index(reply.startIndex, offsetBy: reply.count / 2)
            return AsyncThrowingStream { continuation in
                continuation.yield(String(reply[..<middle]))
                continuation.yield(String(reply[middle...]))
                continuation.finish()
            }
        }

        func listModels() async throws -> [String] { ["fake"] }
        func contextWindow(model: String) async -> Int { context }
    }

    /// An in-app model whose generation sends one piece, then waits until cancelled and returns normally (as MLX
    /// does); its unload takes a moment.
    final class FakeLocalProvider: LocalLLMProvider {
        let unloaded = Mutex(false)

        struct Generator: LocalLLMGenerator {
            func generate(
                messages: [LLMMessage], maxTokens: Int, onText: @escaping @Sendable (String) -> Void
            ) async throws -> String {
                onText("Partial")
                while !Task.isCancelled { try? await Task.sleep(for: .milliseconds(10)) }
                return "Partial answer"
            }
        }

        func withLoadedModel<T: Sendable>(
            model: String, _ body: @Sendable (any LocalLLMGenerator) async throws -> T
        ) async throws -> T {
            do {
                let result = try await body(Generator())
                await unload()
                return result
            } catch {
                await unload()
                throw error
            }
        }

        /// Not cut short by the job's cancellation.
        private func unload() async {
            await Task.detached { try? await Task.sleep(for: .milliseconds(50)) }.value
            unloaded.withLock { $0 = true }
        }

        func listModels() async throws -> [String] { ["local"] }
        func contextWindow(model: String) async -> Int { 128_000 }
    }

    private func transcript(turns: Int) -> Transcript {
        Transcript(segments: (0..<turns).map { index in
            Transcript.Segment(
                start: Double(index * 7), end: Double(index * 7 + 6), channel: index.isMultiple(of: 2) ? .mic : .system,
                speaker: index.isMultiple(of: 2) ? Transcript.Speaker.me : Transcript.Speaker.system(1),
                text: "Turn \(index): we went through item \(index) of the roadmap in detail.", language: "en")
        })
    }

    private func makeConversation() throws -> Conversation {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("AIServiceTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let conversation = Conversation(
            id: UUID(), title: "Weekly sync", createdAt: Date(timeIntervalSince1970: 1_800_000_000), folder: folder)
        try conversation.write()
        return conversation
    }

    @Test
    func mapReduceStreamsOnlyTheFinalReduceAndSavesIt() async throws {
        let conversation = try makeConversation()
        defer { try? FileManager.default.removeItem(at: conversation.folder) }
        let reply = "Summary: the roadmap was reviewed item by item [00:07]."
        let provider = FakeProvider(reply: reply, context: 1_500)
        let service = AIService(gate: ModelLoadGate())

        var updates: [AIService.Update] = []
        for try await update in service.summarize(
            conversation: conversation, transcript: transcript(turns: 120), provider: provider, model: "fake",
            language: "en") {
            updates.append(update)
        }

        let firstText = try #require(updates.firstIndex { if case .text = $0 { true } else { false } })
        let statuses = updates[..<firstText]
        #expect(provider.calls.withLock { $0 } > 2)
        #expect(statuses.count == provider.calls.withLock { $0 })
        #expect(statuses.allSatisfy { if case .status = $0 { true } else { false } })
        #expect(statuses.last == .status("Writing the summary…"))
        #expect(updates[firstText...].allSatisfy { if case .text = $0 { true } else { false } })
        let streamed = updates[firstText...].map { update -> String in
            if case .text(let text) = update { text } else { "" }
        }.joined()
        #expect(streamed == reply)
        #expect(Conversation.readSummary(from: conversation.folder) == reply)
        #expect(try Conversation.read(from: conversation.folder).hasSummary)
    }

    @Test
    func cancelledLocalJobSavesNothingAndUnloadsBeforeReleasingTheGate() async throws {
        let conversation = try makeConversation()
        defer { try? FileManager.default.removeItem(at: conversation.folder) }
        let provider = FakeLocalProvider()
        let gate = ModelLoadGate()
        let service = AIService(gate: gate)

        var updates: [AIService.Update] = []
        for try await update in service.summarize(
            conversation: conversation, transcript: transcript(turns: 3), provider: provider, model: "local",
            language: "en") {
            updates.append(update)
            if case .text = update { break }
        }
        #expect(updates == [.status(AIService.waitingStatus), .status(AIService.loadingStatus), .text("Partial")])

        // Served once the cancelled job has left the gate.
        try await gate.withExclusiveModel(owner: .transcriptionJob) {
            #expect(provider.unloaded.withLock { $0 })
        }
        #expect(Conversation.readSummary(from: conversation.folder) == nil)
        #expect(try !Conversation.read(from: conversation.folder).hasSummary)
    }

    @Test
    func summaryLineIsCutAtTheChunkerAllowance() {
        #expect(AIService.summaryLine("Notes\n**Summary:** We agreed on Friday.\n- detail") == "We agreed on Friday.")
        #expect(AIService.summaryLine("**Summary:**\nWe agreed on Friday.\n- detail") == "We agreed on Friday.\n- detail")
        let long = AIService.summaryLine("Summary: " + String(repeating: "é", count: 600))
        #expect(long.utf8.count <= 800 && long.utf8.count >= 798)
    }

    @Test(arguments: [
        ("e", "e\u{301}", "\u{301}"),
        ("Dev 👩", "Dev 👩\u{200D}💻", "\u{200D}💻"),
        ("Hel", "Hello", "lo"),
    ])
    func snapshotDeltaKeepsSplitGraphemeClusters(previous: String, snapshot: String, delta: String) {
        #expect(SnapshotDelta.delta(from: previous, to: snapshot) == delta)
        #expect(previous + delta == snapshot)
    }

    @Test
    func snapshotDeltaIgnoresRewrittenSnapshots() {
        #expect(SnapshotDelta.delta(from: "Hello", to: "Help") == nil)
        #expect(SnapshotDelta.delta(from: "Hello", to: "Hello") == nil)
    }
}
