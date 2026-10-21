import EchoCore
import Foundation
import Testing

/// Guards the kept transcripts: a re-transcription archives the replaced `transcript.json` as `transcripts/v<N>.json`,
/// `transcript.json` stays the newest, and a transcript saved before versions existed is version 1.
struct TranscriptVersionsTests {
    private func makeFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("TranscriptVersionsTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    private func transcript(_ text: String, engineID: String) -> Transcript {
        Transcript(segments: [.init(start: 0, end: 1, channel: .mic, speaker: Transcript.Speaker.me, text: text)], engineID: engineID)
    }

    @Test func retranscribingKeepsTheReplacedTranscript() throws {
        let folder = try makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }

        try transcript("First.", engineID: "whisper").write(to: folder)
        try transcript("Second.", engineID: "parakeet").write(to: folder, keepingPrevious: true)

        let archived = folder.appendingPathComponent(Transcript.versionsFolderName).appendingPathComponent("v1.json")
        #expect(FileManager.default.fileExists(atPath: archived.path))
        #expect(try Transcript.read(from: folder).segments.map(\.text) == ["Second."])

        let versions = Transcript.readVersions(from: folder)
        #expect(versions.map(\.number) == [1, 2])
        #expect(versions.map { $0.transcript.segments.map(\.text) } == [["First."], ["Second."]])
        #expect(versions.map(\.transcript.engineID) == ["whisper", "parakeet"])

        var renamed = try Transcript.read(from: folder)
        renamed.speakerNames[Transcript.Speaker.me] = "Thi"
        try renamed.write(to: folder)

        #expect(Transcript.readVersions(from: folder).map(\.number) == [1, 2])
    }

    @Test func transcriptFromBeforeVersionsIsVersionOne() throws {
        let folder = try makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }

        #expect(Transcript.readVersions(from: folder).isEmpty)

        try transcript("Old.", engineID: "whisper").write(to: folder)

        let versions = Transcript.readVersions(from: folder)
        #expect(versions.map(\.number) == [1])
        #expect(versions.first?.transcript.segments.map(\.text) == ["Old."])
    }
}
