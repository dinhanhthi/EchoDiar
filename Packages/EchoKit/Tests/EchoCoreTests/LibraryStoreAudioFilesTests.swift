import EchoCore
import Foundation
import Testing

/// `LibraryStore.audioFiles(in:)` decides what audio-only deletion moves to the Trash: it must list only the
/// conversation's own audio files, on a real folder.
struct LibraryStoreAudioFilesTests {
    @Test func listsOnlyTheRealAudioFilesOfTheFolder() throws {
        let fileManager = FileManager.default
        let base = fileManager.temporaryDirectory.appendingPathComponent("audiofiles-\(UUID().uuidString)")
        let folder = base.appendingPathComponent("conversation", isDirectory: true)
        let outside = base.appendingPathComponent("outside.m4a")
        try fileManager.createDirectory(at: folder.appendingPathComponent("sub"), withIntermediateDirectories: true)
        defer { try? fileManager.removeItem(at: base) }

        for name in ["audio.m4a", "audio.caf", "transcript.json", "summary.md", "chat.json", ".x.m4a", "sub/y.m4a"] {
            try Data("x".utf8).write(to: folder.appendingPathComponent(name))
        }
        try Data("x".utf8).write(to: outside)
        try fileManager.createSymbolicLink(at: folder.appendingPathComponent("z.m4a"), withDestinationURL: outside)

        #expect(LibraryStore.audioFiles(in: folder).map(\.lastPathComponent) == ["audio.caf", "audio.m4a"])
    }
}
