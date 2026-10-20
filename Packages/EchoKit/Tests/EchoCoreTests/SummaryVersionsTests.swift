import EchoCore
import Foundation
import Testing

/// Guards the kept summaries: every summary is a numbered version in `summaries/`, `summary.md` is the newest, and
/// a summary saved before versions existed becomes version 1.
struct SummaryVersionsTests {
    private func makeConversation() throws -> Conversation {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("SummaryVersionsTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let conversation = Conversation(id: UUID(), title: "Weekly sync", createdAt: Date(), folder: folder)
        try conversation.write()
        return conversation
    }

    @Test func firstSummaryIsVersionOne() throws {
        let conversation = try makeConversation()
        defer { try? FileManager.default.removeItem(at: conversation.folder) }

        try conversation.writeSummary("First.", provider: "anthropic", model: "claude-sonnet")

        let versions = Conversation.readSummaryVersions(from: conversation.folder)
        #expect(versions.map(\.number) == [1])
        #expect(versions.first?.text == "First.")
        #expect(versions.first?.provider == "anthropic")
        #expect(versions.first?.model == "claude-sonnet")
        #expect(versions.first?.stopped == false)
        #expect(versions.first?.createdAt != nil)
        #expect(Conversation.readSummary(from: conversation.folder) == "First.")
        #expect(try Conversation.read(from: conversation.folder).hasSummary)
    }

    @Test func resummarizingKeepsEveryVersion() throws {
        let conversation = try makeConversation()
        defer { try? FileManager.default.removeItem(at: conversation.folder) }

        try conversation.writeSummary("First.", provider: "anthropic", model: "claude-sonnet")
        try conversation.writeSummary("Second, cut", provider: "mlx", model: "qwen", stopped: true)

        let versions = Conversation.readSummaryVersions(from: conversation.folder)
        #expect(versions.map(\.number) == [1, 2])
        #expect(versions.map(\.text) == ["First.", "Second, cut"])
        #expect(versions.map(\.stopped) == [false, true])
        #expect(versions.last?.model == "qwen")
        #expect(Conversation.readSummary(from: conversation.folder) == "Second, cut")
    }

    @Test func summaryFromBeforeVersionsIsVersionOne() throws {
        let conversation = try makeConversation()
        defer { try? FileManager.default.removeItem(at: conversation.folder) }
        try Data("Old.".utf8).write(to: conversation.folder.appendingPathComponent(Conversation.summaryFileName))

        let before = Conversation.readSummaryVersions(from: conversation.folder)
        #expect(before.map(\.number) == [1])
        #expect(before.first?.text == "Old.")
        #expect(before.first?.model == nil)

        try conversation.writeSummary("New.", provider: "anthropic", model: "claude-sonnet")

        let after = Conversation.readSummaryVersions(from: conversation.folder)
        #expect(after.map(\.number) == [1, 2])
        #expect(after.map(\.text) == ["Old.", "New."])
        #expect(after.first?.model == nil)
        #expect(Conversation.readSummary(from: conversation.folder) == "New.")
    }

    @Test func noSummaryHasNoVersions() throws {
        let conversation = try makeConversation()
        defer { try? FileManager.default.removeItem(at: conversation.folder) }

        #expect(Conversation.readSummaryVersions(from: conversation.folder).isEmpty)
    }
}
