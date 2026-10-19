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
    private var retentionScheduler: NSBackgroundActivityScheduler?

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

    /// Moves the conversation's audio files to the Trash and updates its size; transcript, summary and chat stay.
    public func deleteAudio(id: UUID) throws {
        guard let index = conversations.firstIndex(where: { $0.id == id }) else { throw LibraryError.notFound(id) }
        let current = conversations[index]
        for url in Self.audioFiles(in: current.folder) {
            try FileManager.default.trashItem(at: url, resultingItemURL: nil)
        }
        var conversation = (try? Conversation.read(from: current.folder)) ?? current
        conversation.sizeBytes = Self.sizeOnDisk(of: current.folder)
        if FileManager.default.fileExists(atPath: current.folder.appendingPathComponent(Conversation.fileName).path) {
            try conversation.write()
        }
        conversations[index] = conversation
        emit()
    }

    // MARK: - Auto-delete

    /// What `RetentionPolicy` selects from a fresh scan, without the `excluding` conversations (active jobs).
    public func retentionDeletions(
        settings: AppSettings, now: Date = Date(), excluding: Set<UUID> = []
    ) -> [RetentionPolicy.Deletion] {
        RetentionPolicy.select(
            conversations: refresh().filter { !excluding.contains($0.id) }, now: now, settings: settings)
    }

    /// Moves `retentionDeletions` to the Trash; returns the ones done. `isBusy` is asked right before each one (an
    /// active job), which is then skipped. A failure is logged and the rest continue.
    @discardableResult
    public func applyRetention(
        settings: AppSettings, now: Date = Date(), isBusy: @Sendable (UUID) async -> Bool = { _ in false }
    ) async -> [RetentionPolicy.Deletion] {
        var done: [RetentionPolicy.Deletion] = []
        for deletion in retentionDeletions(settings: settings, now: now) {
            guard await !isBusy(deletion.conversationID) else { continue }
            do {
                switch deletion.mode {
                case .wholeConversation: try delete(id: deletion.conversationID)
                case .audioOnly: try deleteAudio(id: deletion.conversationID)
                }
                done.append(deletion)
            } catch {
                NSLog("EchoDiar: auto-delete failed for \(deletion.conversationID): \(error)")
            }
        }
        return done
    }

    /// Calls `run` about once a day (1 h tolerance) when the system sees fit (no timer polls); replaces a previous
    /// schedule. The caller runs it once at launch itself.
    public func startRetentionSchedule(_ run: @escaping @Sendable () async -> Void) {
        retentionScheduler?.invalidate()
        let scheduler = NSBackgroundActivityScheduler(identifier: "dinh.thi.EchoDiar.retention")
        scheduler.repeats = true
        scheduler.interval = 24 * 3_600
        scheduler.tolerance = 3_600
        scheduler.qualityOfService = .background
        scheduler.schedule { completion in
            nonisolated(unsafe) let completion = completion
            Task {
                await run()
                completion(.finished)
            }
        }
        retentionScheduler = scheduler
    }

    public func stopRetentionSchedule() {
        retentionScheduler?.invalidate()
        retentionScheduler = nil
    }

    // MARK: - Usage

    /// Bytes on disk by kind. `models` comes from the caller (`ModelStore`), which EchoCore does not import.
    public struct Usage: Equatable, Sendable {
        public var audio: Int64
        /// Transcripts, summaries, chats and metadata.
        public var transcriptsAndAI: Int64
        public var models: Int64
        /// Largest first.
        public var conversations: [ConversationUsage]

        public var total: Int64 { audio + transcriptsAndAI + models }
    }

    public struct ConversationUsage: Equatable, Identifiable, Sendable {
        public var id: UUID
        public var title: String
        public var audio: Int64
        public var total: Int64
    }

    /// Measures every conversation folder of the last `refresh` now (on the actor, off the main thread).
    public func usage(modelBytes: @Sendable () -> Int64) -> Usage {
        let sizes = conversations.map { conversation in
            let (audio, total) = Self.audioAndTotalSize(of: conversation.folder)
            return ConversationUsage(id: conversation.id, title: conversation.title, audio: audio, total: total)
        }
        let audio = sizes.reduce(0) { $0 + $1.audio }
        return Usage(
            audio: audio,
            transcriptsAndAI: sizes.reduce(0) { $0 + $1.total } - audio,
            models: modelBytes(),
            conversations: sizes.sorted { $0.total > $1.total })
    }

    /// Audio files of a conversation folder: regular files (not symlinks) with an audio extension, as written by
    /// the recorder (CAF) and the compressor (m4a).
    public static func audioFiles(in folder: URL) -> [URL] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .isSymbolicLinkKey]
        let files = (try? FileManager.default.contentsOfDirectory(
            at: folder, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles])) ?? []
        return files.filter { url in
            guard audioExtensions.contains(url.pathExtension.lowercased()),
                  let values = try? url.resourceValues(forKeys: Set(keys)) else { return false }
            return values.isRegularFile == true && values.isSymbolicLink != true
        }.sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    private static let audioExtensions: Set<String> = ["caf", "m4a"]

    /// Allocated bytes of the folder's audio files, and of every file in it.
    private static func audioAndTotalSize(of folder: URL) -> (audio: Int64, total: Int64) {
        let audio = audioFiles(in: folder).reduce(Int64(0)) { sum, url in
            let values = try? url.resourceValues(forKeys: [.totalFileAllocatedSizeKey, .fileSizeKey])
            return sum + Int64(values?.totalFileAllocatedSize ?? values?.fileSize ?? 0)
        }
        return (audio, sizeOnDisk(of: folder))
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
