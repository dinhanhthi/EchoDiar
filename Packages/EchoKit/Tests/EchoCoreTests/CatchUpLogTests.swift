import EchoCore
import Foundation
import Testing

/// Guards reading `catchups.json`: a malformed entry is skipped, not fatal; a file that is not a catch-up log throws,
/// so an append never overwrites it.
struct CatchUpLogTests {
    @Test func loadSkipsMalformedEntriesAndThrowsOnAnUnreadableFile() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("CatchUpLogTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let url = folder.appendingPathComponent(CatchUpLog.fileName)

        #expect(try CatchUpLog.load(from: folder).catchUps.isEmpty)

        try Data("""
            {"version": 1, "catchUps": [
              {"elapsed": 1, "text": 5},
              {"elapsed": 2, "createdAt": "yesterday"},
              {"elapsed": 3, "throughLineID": 7, "text": "## Now"},
              5
            ]}
            """.utf8).write(to: url)
        let catchUps = try CatchUpLog.load(from: folder).catchUps
        #expect(catchUps.map(\.elapsed) == [3])
        #expect(catchUps.first?.text == "## Now")

        try Data("not json".utf8).write(to: url)
        #expect(throws: (any Error).self) { try CatchUpLog.load(from: folder) }
        try Data(#"{"catchUps": 5}"#.utf8).write(to: url)
        #expect(throws: (any Error).self) { try CatchUpLog.load(from: folder) }
    }
}
