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

    /// The file is untrusted: a repeated id keeps its first entry (the cards are keyed by id), and the times are finite
    /// within 7 days (the clock formatting traps on a huge one), so every reader is safe.
    @Test func decodeDropsRepeatedIDsAndClampsTimes() throws {
        let id = "5B0E3C1A-7D2F-4E8B-9A61-2C4D6E8F0A1B"
        let catchUps = try CatchUpLog.decode(Data("""
            {"catchUps": [
              {"id": "\(id)", "elapsed": 1e300, "throughTime": 1e300, "throughLineID": -7, "text": "first"},
              {"id": "\(id)", "elapsed": 2, "text": "again"},
              {"elapsed": -5, "throughTime": -1, "throughLineID": 3}
            ]}
            """.utf8)).catchUps
        #expect(catchUps.map(\.text) == ["first", ""])
        #expect(catchUps.map(\.elapsed) == [7 * 24 * 3600, 0])
        #expect(catchUps.map(\.throughTime) == [7 * 24 * 3600, 0])
        #expect(catchUps.map(\.throughLineID) == [-1, 3])
    }
}
