import EchoAI
import EchoCore
import Foundation
import Testing

/// Guards the RAG index: an indexed conversation is found again (with its time) by a fresh index on the same folder,
/// and the reconcile pass marks changed conversations stale and drops shards of conversations that are gone.
struct LibraryIndexTests {
    /// One dimension per keyword, plus one for text with none of them; L2-normalised.
    private struct KeywordEmbedder: LoadedTextEmbedder {
        static let keywords = ["budget", "travel", "hiring"]

        func embed(_ texts: [String], query: Bool) async throws -> [[Float]] {
            texts.map { text in
                let lower = text.lowercased()
                var vector = Self.keywords.map { lower.contains($0) ? Float(1) : 0 }
                vector.append(vector.contains(1) ? 0 : 1)
                return EmbeddingMath.normalized(vector)
            }
        }
    }

    private static let key = "fake-embedder"

    private func makeRoot() -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("LibraryIndexTests-\(UUID().uuidString)")
    }

    /// A transcribed conversation in `root/library/<id>`, its `transcript.json` written: `turns` turns about travel,
    /// turn `budgetTurn` (if any) about the budget.
    private func makeConversation(in root: URL, turns: Int, budgetTurn: Int? = nil) throws -> (Conversation, Transcript) {
        let id = UUID()
        let folder = root.appendingPathComponent("library").appendingPathComponent(id.uuidString)
        let transcript = Transcript(segments: (0..<turns).map { index in
            Transcript.Segment(
                start: Double(index * 10), end: Double(index * 10 + 8), channel: .system,
                speaker: Transcript.Speaker.system(1),
                text: index == budgetTurn
                    ? "The budget for next quarter is forty thousand euros."
                    : "We went through the travel plans for the offsite, item \(index).")
        })
        try transcript.write(to: folder)
        let conversation = Conversation(
            id: id, title: "Planning", createdAt: Date(timeIntervalSince1970: 1_800_000_000), hasTranscript: true,
            folder: folder)
        try conversation.write()
        return (conversation, transcript)
    }

    @Test func indexedChunkIsFoundWithItsTimeAfterReload() async throws {
        let root = makeRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let (conversation, transcript) = try makeConversation(in: root, turns: 40, budgetTurn: 30)
        let index = LibraryIndex(root: root.appendingPathComponent("RAG"))
        await index.load(key: Self.key)
        try await index.index(conversation, transcript: transcript, summary: nil, embedder: KeywordEmbedder())

        let reloaded = LibraryIndex(root: root.appendingPathComponent("RAG"))
        await reloaded.load(key: Self.key)
        let question = "What is the budget?"
        let vector = try await KeywordEmbedder().embed([question], query: true)[0]
        let hits = await reloaded.search(question: question, vector: vector, limit: 3)

        let hit = try #require(hits.first)
        #expect(hit.conversationID == conversation.id)
        #expect(hit.chunk.kind == .transcript)
        #expect(hit.chunk.text.contains("forty thousand"))
        let start = try #require(hit.chunk.start)
        let end = try #require(hit.chunk.end)
        #expect(start <= 300 && end >= 308)
        #expect(start > 0)
        #expect(await reloaded.count == 1)
    }

    @Test func longTurnsGiveNoWindowContainedInAnother() {
        // About 100, 100 and 150 tokens per turn: a shared turn plus the next one would not fit a window.
        let lengths = [400, 400, 600, 400, 400, 600, 400]
        let transcript = Transcript(segments: lengths.enumerated().map { index, length in
            Transcript.Segment(
                start: Double(index * 10), end: Double(index * 10 + 8), channel: .system,
                speaker: Transcript.Speaker.system(1), text: String(repeating: "a", count: length))
        })
        let ranges = LibraryIndex.chunks(transcript: transcript, summary: nil).map { chunk in
            (Int(chunk.start! / 10), Int(chunk.end! / 10))
        }

        #expect(ranges.first?.0 == 0 && ranges.last?.1 == lengths.count - 1)
        for (i, a) in ranges.enumerated() {
            for (j, b) in ranges.enumerated() where i != j {
                #expect(!(b.0 <= a.0 && a.1 <= b.1), "window \(a) is inside window \(b)")
            }
        }
    }

    @Test func changedTranscriptIsStaleAndUnlistedConversationLosesItsShard() async throws {
        let root = makeRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let (kept, keptTranscript) = try makeConversation(in: root, turns: 5)
        let (gone, goneTranscript) = try makeConversation(in: root, turns: 5)
        let index = LibraryIndex(root: root.appendingPathComponent("RAG"))
        await index.load(key: Self.key)
        try await index.index(kept, transcript: keptTranscript, summary: "Travel is booked.", embedder: KeywordEmbedder())
        try await index.index(gone, transcript: goneTranscript, summary: nil, embedder: KeywordEmbedder())
        #expect(await index.stale([kept, gone]).isEmpty)
        // A shard of another key, not loaded: only `prune` reaches it, without an embedder.
        let otherKey = root.appendingPathComponent("RAG").appendingPathComponent("other")
        try FileManager.default.createDirectory(at: otherKey, withIntermediateDirectories: true)
        let orphan = otherKey.appendingPathComponent("\(UUID().uuidString).json")
        try Data("{}".utf8).write(to: orphan)
        await index.prune([kept, gone])
        #expect(!FileManager.default.fileExists(atPath: orphan.path))

        var edited = keptTranscript
        edited.segments[0].text += " The hiring plan was moved to next week."
        try edited.write(to: kept.folder)
        let stale = await index.stale([kept])

        #expect(stale.map(\.id) == [kept.id])
        let shards = root.appendingPathComponent("RAG").appendingPathComponent(Self.key)
        #expect(!FileManager.default.fileExists(atPath: shards.appendingPathComponent("\(gone.id.uuidString).json").path))
        #expect(FileManager.default.fileExists(atPath: shards.appendingPathComponent("\(kept.id.uuidString).json").path))
        #expect(await index.count == 1)

        await index.removeAll()
        #expect(!FileManager.default.fileExists(atPath: shards.path))
    }
}
