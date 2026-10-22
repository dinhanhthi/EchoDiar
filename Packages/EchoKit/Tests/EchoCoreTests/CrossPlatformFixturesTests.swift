import AVFoundation
@testable import EchoCore
import Foundation
import Testing

/// File-format contract with the Windows port (`windows/`): the macOS encoders write `macos-written.json`, which the
/// C# models must round-trip, and the EchoCore types read the Windows-written JSON and CAF fixtures.
struct CrossPlatformFixturesTests {
    private static let writing = ProcessInfo.processInfo.environment["ECHODIAR_WRITE_FIXTURES"] == "1"
    private static let vietnamese = "Xin chào, cuộc họp"
    private static let conversationID = UUID(uuidString: "6F1C2A9E-3B4D-4E5F-8A7B-1C2D3E4F5A6B")!
    private static let chatID = UUID(uuidString: "0A1B2C3D-4E5F-4061-8273-94A5B6C7D8E9")!
    /// 2026-10-21T09:30:15.25Z: fractional, and exact as a double (and in .NET ticks).
    private static let at = Date(timeIntervalSinceReferenceDate: 814_267_815.25)
    /// The same instant as ISO 8601 stores it, truncated to whole seconds.
    private static let atSeconds = Date(timeIntervalSinceReferenceDate: 814_267_815)

    /// `windows/tests/EchoDiar.Tests/Fixtures` in this checkout.
    private static func fixtures(_ file: String = #filePath) throws -> URL {
        // <root>/Packages/EchoKit/Tests/EchoCoreTests/<file>
        var root = URL(fileURLWithPath: file)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let folder = root.appendingPathComponent("windows/tests/EchoDiar.Tests/Fixtures", isDirectory: true)
        try #require(FileManager.default.fileExists(atPath: folder.appendingPathComponent("windows-written.json").path))
        return folder
    }

    private static func makeFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("CrossPlatformFixturesTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    // MARK: - macOS → Windows

    /// Writes each file kind with the app's own encoder into `macos-written.json`, keyed by file name.
    @Test(.enabled(if: writing))
    func writesMacOSJsonFixture() throws {
        let folder = try Self.makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }

        try Self.conversation(folder: folder).write()
        try Self.transcript.write(to: folder)
        try Self.session.write(to: folder)
        try Self.chat.write(to: folder)
        var files: [String: Data] = [:]
        for name in [Conversation.fileName, Transcript.fileName, RecordingSession.fileName, ChatLog.fileName] {
            files[name] = try Data(contentsOf: folder.appendingPathComponent(name))
        }
        files[ChatArchive.indexFileName] = try ChatArchive.Index(chats: [
            ChatArchive.Entry(id: Self.chatID, title: Self.vietnamese, archivedAt: Self.atSeconds, messageCount: 2),
        ]).encoded()
        files[Conversation.summaryIndexFileName] = try SummaryIndex(summaries: [
            SummaryVersion(
                number: 1, createdAt: Self.at, provider: "anthropic", model: "claude-sonnet-5-5", stopped: true, text: ""),
        ]).encoded()

        // Spliced as text, not re-serialized, so the bytes stay the encoders' own.
        let entries = try files.keys.sorted().map { name in
            let json = try #require(String(data: files[name]!, encoding: .utf8))
            return "  \"\(name)\" : " + json.replacingOccurrences(of: "\n", with: "\n  ")
        }
        let output = try Self.fixtures().appendingPathComponent("macos-written.json")
        try Data(("{\n" + entries.joined(separator: ",\n") + "\n}\n").utf8).write(to: output, options: .atomic)

        let written = try JSONSerialization.jsonObject(with: Data(contentsOf: output)) as? [String: Any]
        #expect(written?.keys.sorted() == files.keys.sorted())
    }

    private static func conversation(folder: URL) -> Conversation {
        Conversation(
            id: conversationID, title: vietnamese, customTitle: true, createdAt: at, duration: 61.5,
            sourceAppName: "Zoom", sourceBundleID: "us.zoom.xos", status: .transcribed, hasTranscript: true,
            hasSummary: true, sizeBytes: 1_234_567, folder: folder)
    }

    private static let transcript = Transcript(
        segments: [
            Transcript.Segment(
                id: UUID(uuidString: "11111111-2222-4333-8444-555555555555")!, start: 0.5, end: 2.75, channel: .mic,
                speaker: Transcript.Speaker.me, text: vietnamese, language: "vi"),
            Transcript.Segment(
                id: UUID(uuidString: "66666666-7777-4888-9999-AAAAAAAAAAAA")!, start: 3, end: 4.25, channel: .system,
                speaker: Transcript.Speaker.system(2), text: "Hello", language: "en"),
        ],
        speakerNames: ["s2": "Lan"], engineID: "whisper", modelID: "openai_whisper-small", engineFallback: "parakeet",
        notes: [Transcript.diarizationMissingNote], createdAt: at)

    private static var session: RecordingSession {
        var session = RecordingSession(
            id: conversationID, pid: 4242, state: .stopped, startedAt: at,
            sourceApps: [.init(name: "Zoom", bundleIDs: ["us.zoom.xos"], pids: [1234, 5678])], tapMode: .perApp,
            globalTapFallback: true, micDeviceName: "MacBook Pro Microphone", outputDeviceName: "MacBook Pro Speakers",
            languageMarkers: [.init(fileTime: 0, language: "vi")], probe: true)
        session.pauses = [.init(fileTime: 12.5, start: at.addingTimeInterval(-30), end: at)]
        session.endReason = "user"
        return session
    }

    private static let chat = ChatLog(title: vietnamese, messages: [
        ChatLog.Message(role: .user, text: vietnamese, timestamp: at),
        // The "/" checks Swift's `\/` escape on the Windows side.
        ChatLog.Message(
            role: .assistant, text: "Ship on Friday [00:05] / Thứ Sáu.", provider: "anthropic",
            model: "claude-sonnet-5-5", timestamp: at.addingTimeInterval(5),
            references: [ChatLog.Reference(number: 1, conversationID: conversationID, title: vietnamese)]),
    ])

    // MARK: - Windows → macOS

    /// The EchoCore types read every file kind the Windows port writes.
    @Test(.disabled(if: writing))
    func readsWindowsJsonFixture() throws {
        let data = try Data(contentsOf: Self.fixtures().appendingPathComponent("windows-written.json"))
        let root = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        func entry(_ name: String) throws -> Data {
            try JSONSerialization.data(withJSONObject: #require(root[name]))
        }

        let conversation = try JSONDecoder().decode(Conversation.self, from: entry(Conversation.fileName))
        #expect(conversation.id == Self.conversationID)
        #expect(conversation.title == Self.vietnamese)
        #expect(conversation.status == .transcribed)
        Self.expectDate(conversation.createdAt, Self.at)

        let transcript = try JSONDecoder().decode(Transcript.self, from: entry(Transcript.fileName))
        #expect(transcript.segments.map(\.id.uuidString) == [
            "11111111-2222-4333-8444-555555555555", "66666666-7777-4888-9999-AAAAAAAAAAAA",
        ])
        #expect(transcript.segments.map(\.channel) == [.mic, .system])
        #expect(transcript.segments.map(\.speaker) == ["me", "s2"])
        #expect(transcript.segments.first?.text == Self.vietnamese)
        #expect(transcript.speakerNames == ["s2": "Lan"])
        Self.expectDate(transcript.createdAt, Self.at)

        let session = try JSONDecoder().decode(RecordingSession.self, from: entry(RecordingSession.fileName))
        #expect(session.id == Self.conversationID)
        #expect(session.state == .stopped)
        #expect(session.tapMode == .perApp)
        Self.expectDate(session.startedAt, Self.at)
        let pause = try #require(session.pauses.first)
        Self.expectDate(pause.start, Self.at.addingTimeInterval(-30))
        Self.expectDate(try #require(pause.end), Self.at)

        let chat = try ChatLog.decode(entry(ChatLog.fileName))
        #expect(chat.title == Self.vietnamese)
        #expect(chat.messages.map(\.role) == [.user, .assistant])
        #expect(chat.messages.first?.text == Self.vietnamese)
        #expect(chat.messages.last?.references?.first?.conversationID == Self.conversationID)
        Self.expectDate(try #require(chat.messages.first?.timestamp), Self.atSeconds)

        let chats = try ChatArchive.Index.decode(entry(ChatArchive.indexFileName)).chats
        #expect(chats.map(\.id) == [Self.chatID])
        #expect(chats.first?.title == Self.vietnamese)
        Self.expectDate(try #require(chats.first?.archivedAt), Self.atSeconds)

        let summaries = try SummaryIndex.decode(entry(Conversation.summaryIndexFileName)).summaries
        #expect(summaries.map(\.number) == [1])
        #expect(summaries.first?.stopped == true)
        Self.expectDate(try #require(summaries.first?.createdAt), Self.atSeconds)
    }

    private static func expectDate(
        _ date: Date, _ expected: Date, sourceLocation: SourceLocation = #_sourceLocation
    ) {
        #expect(abs(date.timeIntervalSince(expected)) < 1e-3, sourceLocation: sourceLocation)
    }

    /// AVAudioFile reads the Windows CAF writer's file as 16 kHz 2-channel PCM: a wrong format flag (endianness,
    /// float/int) would turn the 440 Hz sine into noise or swap it with the silent channel.
    @Test(.disabled(if: writing))
    func readsWindowsSineCaf() throws {
        let file = try AVAudioFile(forReading: Self.fixtures().appendingPathComponent("windows-sine.caf"))
        #expect(file.fileFormat.sampleRate == 16_000)
        #expect(file.fileFormat.channelCount == 2)
        #expect(file.length == 16_000)

        let buffer = try #require(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: 16_000))
        try file.read(into: buffer)
        let channels = try #require(buffer.floatChannelData)
        let mic = UnsafeBufferPointer(start: channels[0], count: Int(buffer.frameLength))
        let system = UnsafeBufferPointer(start: channels[1], count: Int(buffer.frameLength))

        let micPeak = mic.map(abs).max() ?? 0
        #expect(micPeak >= 0.45 && micPeak <= 0.55)
        let crossings = zip(mic, mic.dropFirst()).filter { ($0 < 0) != ($1 < 0) }.count
        #expect(abs(crossings - 880) <= 20)
        #expect((system.map(abs).max() ?? 0) < 0.001)
    }

    /// `ECHODIAR_DECODE_FOLDER`: a real conversation folder recorded on Windows. Only read, never written.
    private static let decodeFolder = ProcessInfo.processInfo.environment["ECHODIAR_DECODE_FOLDER"]
        .map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath, isDirectory: true) }

    /// The app's readers decode a real Windows folder, and its audio has speech on the system channel: a Float32/Int16
    /// or endianness mistake reads as clipped noise (RMS above 0.5).
    @Test(.enabled(if: decodeFolder != nil))
    func decodesRealWindowsFolder() throws {
        let folder = try #require(Self.decodeFolder)
        _ = try Conversation.read(from: folder)
        _ = try Transcript.read(from: folder)
        let session = try RecordingSession.read(from: folder)

        let url = try #require(ConversationFiles.audioURL(in: folder, fileName: session.audioFileName))
        let file = try AVAudioFile(forReading: url)
        let duration = Double(file.length) / file.fileFormat.sampleRate
        #expect(duration > 0)
        try #require(file.processingFormat.channelCount >= 2)

        let frames = AVAudioFrameCount(min(file.length, AVAudioFramePosition(file.processingFormat.sampleRate * 10)))
        let buffer = try #require(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frames))
        try file.read(into: buffer, frameCount: frames)
        let channels = try #require(buffer.floatChannelData)
        let mic = UnsafeBufferPointer(start: channels[0], count: Int(buffer.frameLength))
        let system = UnsafeBufferPointer(start: channels[1], count: Int(buffer.frameLength))
        func rms(_ samples: UnsafeBufferPointer<Float>) -> Float {
            (samples.reduce(0) { $0 + $1 * $1 } / Float(max(samples.count, 1))).squareRoot()
        }
        let micRMS = rms(mic), systemRMS = rms(system)
        print("decodesRealWindowsFolder: \(session.audioFileName) duration \(duration) s, "
            + "\(file.fileFormat.channelCount) ch, mic RMS \(micRMS), system RMS \(systemRMS)")

        #expect(systemRMS > 0.01 && systemRMS < 0.5)
        #expect(mic.allSatisfy { $0.isFinite })
        #expect(micRMS < 0.5)
    }
}
