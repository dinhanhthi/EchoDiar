import EchoCore
import Foundation
import Testing

/// `reload(id:)` is how a job's step change reaches the library without rescanning it: it must pick up that
/// folder's new metadata, and drop a folder that is gone.
struct LibraryStoreReloadTests {
    @Test func reloadsOneConversationAndDropsAGoneFolder() async throws {
        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory.appendingPathComponent("reload-\(UUID().uuidString)", isDirectory: true)
        defer { try? fileManager.removeItem(at: root) }
        func make(_ name: String, minutesAgo: Double) throws -> Conversation {
            let folder = root.appendingPathComponent(name, isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            let conversation = Conversation(id: UUID(), createdAt: Date(timeIntervalSinceNow: -minutesAgo * 60), folder: folder)
            try conversation.write()
            return conversation
        }
        var older = try make("a", minutesAgo: 2)
        let newer = try make("b", minutesAgo: 1)
        let store = LibraryStore(root: root)
        await store.refresh()

        older.status = .transcribing
        try older.write()
        await store.reload(id: older.id)
        #expect(await store.conversations.map(\.status) == [.ready, .transcribing])

        try fileManager.removeItem(at: newer.folder)
        await store.reload(id: newer.id)
        #expect(await store.conversations.map(\.id) == [older.id])
    }

    /// A recording has only `session.json` until it is compressed: its title (typed or from the calendar) is what the
    /// library lists, as a custom title; a session without one (written by an older build) gets the default title.
    @Test func sessionWithoutConversationIsListedWithItsTitle() async throws {
        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory.appendingPathComponent("titled-\(UUID().uuidString)", isDirectory: true)
        defer { try? fileManager.removeItem(at: root) }
        let titled = RecordingSession(state: .recording, title: "Weekly sync")
        let untitled = RecordingSession(state: .stopped, startedAt: Date(timeIntervalSinceNow: -60))
        try titled.write(to: titled.folder(in: root))
        try untitled.write(to: untitled.folder(in: root))

        let store = LibraryStore(root: root)
        await store.refresh()
        let conversations = await store.conversations

        let listed = try #require(conversations.first { $0.id == titled.id })
        #expect(listed.title == "Weekly sync")
        #expect(listed.customTitle)
        let old = try #require(conversations.first { $0.id == untitled.id })
        #expect(old.title == Conversation.defaultTitle(sourceAppName: nil, createdAt: untitled.startedAt))
        #expect(!old.customTitle)
    }
}
