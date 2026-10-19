import EchoCore
import Foundation
import Testing

/// Guards auto-delete against data loss. Pure: no file is read or deleted.
struct RetentionPolicyTests {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)
    private let day: TimeInterval = 86_400

    @Test func selectsOnlyConversationsOlderThanTheAge() {
        let old = conversation(ageDays: 31)
        let recent = conversation(ageDays: 29)
        let deletions = RetentionPolicy.select(
            conversations: [old, recent], now: now, settings: settings(.wholeConversation, days: 30), audioFiles: audio)
        #expect(deletions.map(\.conversationID) == [old.id])
        #expect(deletions.first?.items == [old.folder])
    }

    /// Audio only trashes just what `audioFiles` lists (tested on a real folder in `LibraryStoreAudioFilesTests`),
    /// never the folder; a conversation with no audio left is skipped.
    @Test func audioOnlyTrashesOnlyTheListedAudioAndSkipsConversationsWithout() throws {
        let withAudio = conversation(ageDays: 120)
        let withoutAudio = conversation(ageDays: 120)
        let deletions = RetentionPolicy.select(
            conversations: [withAudio, withoutAudio], now: now, settings: settings(.audioOnly, days: 90),
            audioFiles: { $0.id == withAudio.id ? audio($0) : [] })
        let deletion = try #require(deletions.first)
        #expect(deletions.count == 1)
        #expect(deletion.conversationID == withAudio.id)
        #expect(deletion.mode == .audioOnly)
        #expect(deletion.items == audio(withAudio))
    }

    @Test(arguments: [
        Conversation.Status.recording, .compressing, .transcribing, .ready, .recovered, .compressionFailed,
        .transcriptionFailed, .failed, .unknown,
    ])
    func busyOrUntranscribedConversationsAreNeverSelected(status: Conversation.Status) {
        let old = conversation(ageDays: 400, status: status)
        for mode in [RetentionMode.audioOnly, .wholeConversation] {
            #expect(RetentionPolicy.select(
                conversations: [old], now: now, settings: settings(mode, days: 1), audioFiles: audio).isEmpty)
        }
    }

    @Test func transcribedStatusWithoutTranscriptIsNeverSelected() {
        let old = conversation(ageDays: 400, hasTranscript: false)
        #expect(RetentionPolicy.select(
            conversations: [old], now: now, settings: settings(.wholeConversation, days: 1), audioFiles: audio).isEmpty)
    }

    private func settings(_ mode: RetentionMode, days: Int) -> AppSettings {
        AppSettings(autoDeleteEnabled: true, autoDeleteAfterDays: days, autoDeleteMode: mode)
    }

    private func conversation(
        ageDays: Double, status: Conversation.Status = .transcribed, hasTranscript: Bool = true
    ) -> Conversation {
        let id = UUID()
        return Conversation(
            id: id, createdAt: now.addingTimeInterval(-ageDays * day), status: status, hasTranscript: hasTranscript,
            hasSummary: true, folder: URL(fileURLWithPath: "/tmp/retention-\(id.uuidString)", isDirectory: true))
    }

    /// The audio files the policy is told the folder holds.
    private func audio(_ conversation: Conversation) -> [URL] {
        [conversation.folder.appendingPathComponent("audio.m4a"), conversation.folder.appendingPathComponent("audio.caf")]
    }
}
