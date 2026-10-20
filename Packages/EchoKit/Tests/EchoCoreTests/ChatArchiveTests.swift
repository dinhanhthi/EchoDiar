import EchoCore
import Foundation
import Testing

/// Guards the chat history: starting a fresh chat archives `chat.json`, resuming an archived chat makes it `chat.json`
/// again (archiving the open one), and a title, once set, stays.
struct ChatArchiveTests {
    private func makeFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("ChatArchiveTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    private func writeChat(_ texts: [String], in folder: URL) throws {
        try ChatLog(messages: texts.enumerated().map {
            ChatLog.Message(role: $0.offset.isMultiple(of: 2) ? .user : .assistant, text: $0.element)
        }).write(to: folder)
    }

    @Test func resumeRestoresTheArchivedChatAndArchivesTheOpenOne() throws {
        let folder = try makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let reference = ChatLog.Reference(number: 1, conversationID: UUID(), title: "Planning")
        try ChatLog(messages: [
            ChatLog.Message(role: .user, text: "What was decided?"),
            ChatLog.Message(role: .assistant, text: "Ship on Friday.", references: [reference]),
        ]).write(to: folder)
        let first = try #require(try ChatArchive.archive(in: folder))
        #expect(first.needsTitle)
        #expect(first.entry.title == "What was decided?")
        #expect(ChatLog.read(from: folder).messages.isEmpty)
        try writeChat(["Who owns the budget?"], in: folder)

        let open = try #require(try ChatArchive.resume(first.entry.id, in: folder))

        #expect(ChatLog.read(from: folder).messages.map(\.text) == ["What was decided?", "Ship on Friday."])
        #expect(ChatLog.read(from: folder).messages.map(\.references) == [nil, [reference]])
        #expect(ChatArchive.entries(in: folder) == [open.entry])
        #expect(open.entry.title == "Who owns the budget?")
        let archived = folder.appendingPathComponent(ChatArchive.folderName)
            .appendingPathComponent("\(first.entry.id.uuidString).json")
        #expect(!FileManager.default.fileExists(atPath: archived.path))
    }

    @Test func titledChatKeepsItsTitleAndEmptyChatIsNotArchived() throws {
        let folder = try makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        try writeChat(["What was decided?", "Ship on Friday."], in: folder)
        let first = try #require(try ChatArchive.archive(in: folder))
        try ChatArchive.setTitle("Release date", for: first.entry.id, in: folder)

        #expect(try ChatArchive.resume(first.entry.id, in: folder) == nil)
        let again = try #require(try ChatArchive.archive(in: folder))

        #expect(!again.needsTitle)
        #expect(again.entry.title == "Release date")
        #expect(try ChatArchive.archive(in: folder) == nil)
        #expect(ChatArchive.entries(in: folder).map(\.title) == ["Release date"])
    }
}
