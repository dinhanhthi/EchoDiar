@testable import EchoAI
import EchoCore
import Foundation
import Synchronization
import Testing

/// Guards the summary job: map-reduce sends status lines and streams only the final reduce, the result is saved as a
/// version, a cancelled job saves nothing, a stopped job saves what it streamed, and an in-app model is unloaded
/// before the gate is released. Also guards how many earlier chat messages a question sends.
struct AIServiceTests {
    /// Answers every request with `reply`, in two pieces, and reports a small context to force map-reduce.
    final class FakeProvider: LLMProvider {
        let reply: String
        let context: Int
        let calls = Mutex(0)
        let received = Mutex<[[LLMMessage]]>([])

        init(reply: String, context: Int) {
            self.reply = reply
            self.context = context
        }

        func stream(messages: [LLMMessage], model: String, maxTokens: Int) -> AsyncThrowingStream<String, Error> {
            calls.withLock { $0 += 1 }
            received.withLock { $0.append(messages) }
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

    /// Sends `piece` (when not empty), then waits until cancelled, as a slow answer does.
    final class StallingProvider: LLMProvider {
        let piece: String
        let context: Int

        init(piece: String, context: Int = 128_000) {
            self.piece = piece
            self.context = context
        }

        func stream(messages: [LLMMessage], model: String, maxTokens: Int) -> AsyncThrowingStream<String, Error> {
            let piece = piece
            return AsyncThrowingStream { continuation in
                let task = Task {
                    if !piece.isEmpty { continuation.yield(piece) }
                    while !Task.isCancelled { try? await Task.sleep(for: .milliseconds(10)) }
                    continuation.finish(throwing: CancellationError())
                }
                continuation.onTermination = { _ in task.cancel() }
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
            conversation: conversation, transcript: transcript(turns: 120), provider: provider, providerID: "fake",
            model: "fake", language: "en") {
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
        let versions = Conversation.readSummaryVersions(from: conversation.folder)
        #expect(versions.map(\.provider) == ["fake"])
        #expect(versions.map(\.stopped) == [false])
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
            conversation: conversation, transcript: transcript(turns: 3), provider: provider, providerID: "local",
            model: "local", language: "en") {
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
    func stoppedQuestionSavesTheStreamedPart() async throws {
        let conversation = try makeConversation()
        defer { try? FileManager.default.removeItem(at: conversation.folder) }
        let service = AIService(gate: ModelLoadGate())

        var streamed = ""
        for try await update in service.ask(
            question: "What was decided?", conversation: conversation, transcript: transcript(turns: 3), summary: nil,
            history: [], historyLimit: 10, provider: StallingProvider(piece: "Ship on Friday "), providerID: "fake",
            model: "fake", language: "en") {
            if case .text(let text) = update {
                streamed += text
                service.stop()
            }
        }

        #expect(streamed == "Ship on Friday ")
        let messages = ChatLog.read(from: conversation.folder).messages
        #expect(messages.map(\.role) == [.user, .assistant])
        #expect(messages.map(\.text) == ["What was decided?", "Ship on Friday"])
        #expect(messages.last?.provider == "fake")
    }

    @Test(arguments: [
        (3, ["History Q3", "History A3"]),
        (0, ["History Q1", "History A1", "History Q2", "History A2", "History Q3", "History A3"]),
    ])
    func questionSendsTheLastHistoryMessagesFromAUserTurn(limit: Int, expected: [String]) async throws {
        let conversation = try makeConversation()
        defer { try? FileManager.default.removeItem(at: conversation.folder) }
        let provider = FakeProvider(reply: "Friday.", context: 128_000)
        let history = (1...3).flatMap { index in
            [ChatLog.Message(role: .user, text: "History Q\(index)"),
             ChatLog.Message(role: .assistant, text: "History A\(index)")]
        }

        for try await _ in AIService(gate: ModelLoadGate()).ask(
            question: "What was decided?", conversation: conversation, transcript: transcript(turns: 3), summary: nil,
            history: history, historyLimit: limit, provider: provider, providerID: "fake", model: "fake",
            language: "en") {}

        let sent = try #require(provider.received.withLock { $0.last }).filter { $0.content.hasPrefix("History ") }
        #expect(sent.map(\.content) == expected)
        #expect(sent.first?.role == .user)
    }

    @Test
    func stoppedSummarySavesAStoppedVersion() async throws {
        let conversation = try makeConversation()
        defer { try? FileManager.default.removeItem(at: conversation.folder) }
        let service = AIService(gate: ModelLoadGate())

        for try await update in service.summarize(
            conversation: conversation, transcript: transcript(turns: 3), provider: StallingProvider(piece: "## Decisions"),
            providerID: "fake", model: "fake", language: "en") {
            if case .text = update { service.stop() }
        }

        let versions = Conversation.readSummaryVersions(from: conversation.folder)
        #expect(versions.map(\.text) == ["## Decisions"])
        #expect(versions.map(\.stopped) == [true])
        #expect(Conversation.readSummary(from: conversation.folder) == "## Decisions")
    }

    @Test
    func stoppedLocalSummarySavesWhatTheModelReturned() async throws {
        let conversation = try makeConversation()
        defer { try? FileManager.default.removeItem(at: conversation.folder) }
        let service = AIService(gate: ModelLoadGate())

        for try await update in service.summarize(
            conversation: conversation, transcript: transcript(turns: 3), provider: FakeLocalProvider(),
            providerID: "local", model: "local", language: "en") {
            if case .text = update { service.stop() }
        }

        let versions = Conversation.readSummaryVersions(from: conversation.folder)
        #expect(versions.map(\.text) == ["Partial answer"])
        #expect(versions.map(\.stopped) == [true])
    }

    @Test
    func summaryStoppedBeforeItsTextSavesNothing() async throws {
        let conversation = try makeConversation()
        defer { try? FileManager.default.removeItem(at: conversation.folder) }
        let service = AIService(gate: ModelLoadGate())

        var updates: [AIService.Update] = []
        for try await update in service.summarize(
            conversation: conversation, transcript: transcript(turns: 120),
            provider: StallingProvider(piece: "Notes", context: 1_500), providerID: "fake", model: "fake",
            language: "en") {
            updates.append(update)
            service.stop()
        }

        #expect(updates.count == 1)
        guard case .status(let status) = updates.first else { Issue.record("No status first"); return }
        #expect(status.hasPrefix("Reading part 1 of"))
        #expect(Conversation.readSummaryVersions(from: conversation.folder).isEmpty)
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
