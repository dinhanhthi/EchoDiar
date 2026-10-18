import AVFoundation
import Foundation

/// The conversation library under the conversations root. File access runs on the actor, off the main thread;
/// nothing watches the disk, so callers `refresh()` after writing a folder themselves.
public actor LibraryStore {
    public enum LibraryError: Error {
        case notFound(UUID)
    }

    public let root: URL
    /// Newest first.
    public private(set) var conversations: [Conversation] = []
    private var continuations: [UUID: AsyncStream<[Conversation]>.Continuation] = [:]

    public init(root: URL = RecordingSession.conversationsRoot) {
        self.root = root
    }

    /// A stream of the full list (newest first), emitted after every `refresh`, `rename` and `delete`.
    /// Each call returns its own stream.
    public func changes() -> AsyncStream<[Conversation]> {
        let (stream, continuation) = AsyncStream.makeStream(of: [Conversation].self, bufferingPolicy: .bufferingNewest(1))
        let key = UUID()
        continuations[key] = continuation
        continuation.onTermination = { [weak self] _ in
            Task { await self?.removeContinuation(key) }
        }
        return stream
    }

    /// Rescans the root. Reads only metadata: `conversation.json` when present, else derived from `session.json`.
    /// Never writes. Symlinks, folders of probe sessions, and folders where neither file decodes, are skipped.
    @discardableResult
    public func refresh() -> [Conversation] {
        let folders = (try? FileManager.default.contentsOfDirectory(
            at: root, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey], options: [.skipsHiddenFiles])) ?? []
        conversations = folders.compactMap(Self.load).sorted { $0.createdAt > $1.createdAt }
        emit()
        return conversations
    }

    /// Sets a custom title and writes `conversation.json`. A blank title is ignored. An existing file is read
    /// fresh and only its title changes, so a status written meanwhile (e.g. by the compressor) survives.
    public func rename(id: UUID, title: String) throws {
        let title = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !title.isEmpty else { return }
        guard let index = conversations.firstIndex(where: { $0.id == id }) else { throw LibraryError.notFound(id) }
        let current = conversations[index]
        let exists = FileManager.default.fileExists(
            atPath: current.folder.appendingPathComponent(Conversation.fileName).path)
        var conversation = exists ? try Conversation.read(from: current.folder) : current
        conversation.title = title
        conversation.customTitle = true
        try conversation.write()
        conversation.sizeBytes = current.sizeBytes
        conversations[index] = conversation
        emit()
    }

    /// Moves the conversation folder to the Trash.
    public func delete(id: UUID) throws {
        guard let index = conversations.firstIndex(where: { $0.id == id }) else { throw LibraryError.notFound(id) }
        try FileManager.default.trashItem(at: conversations[index].folder, resultingItemURL: nil)
        conversations.remove(at: index)
        emit()
    }

    /// Allocated bytes of every file in `folder`.
    public static func sizeOnDisk(of folder: URL) -> Int64 {
        let keys: [URLResourceKey] = [.totalFileAllocatedSizeKey, .fileSizeKey, .isRegularFileKey]
        guard let files = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: keys) else { return 0 }
        var total: Int64 = 0
        for case let url as URL in files {
            guard let values = try? url.resourceValues(forKeys: Set(keys)), values.isRegularFile == true else { continue }
            total += Int64(values.totalFileAllocatedSize ?? values.fileSize ?? 0)
        }
        return total
    }

    private static func load(_ folder: URL) -> Conversation? {
        let values = try? folder.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard values?.isDirectory == true, values?.isSymbolicLink != true else { return nil }
        let session = try? RecordingSession.read(from: folder)
        if session?.probe == true { return nil }
        var conversation: Conversation
        if let stored = try? Conversation.read(from: folder) {
            conversation = stored
        } else if let session {
            let isRecording = session.state == .recording || session.state == .paused
            let audio = ConversationFiles.audioURL(in: folder, fileName: session.audioFileName)
            let duration = isRecording ? 0 : (audio.map { audioDuration(of: $0) } ?? 0)
            conversation = Conversation(session: session, folder: folder, duration: duration)
        } else {
            return nil
        }
        conversation.sizeBytes = sizeOnDisk(of: folder)
        return conversation
    }

    /// Seconds of audio in the file; 0 when it is missing, empty or unreadable.
    private static func audioDuration(of url: URL) -> Double {
        guard let file = try? AVAudioFile(forReading: url), file.length > 0, file.fileFormat.sampleRate > 0 else { return 0 }
        return Double(file.length) / file.fileFormat.sampleRate
    }

    private func emit() {
        for continuation in continuations.values { continuation.yield(conversations) }
    }

    private func removeContinuation(_ key: UUID) {
        continuations[key] = nil
    }
}
