#if DEBUG
import EchoCore
import Foundation

/// DEBUG launch arg `--seed-library`: adds three transcribed conversations to the real library (VM layout, tab and
/// search checks); ones already present, matched by title, are skipped. Not a harness arg: the app keeps running.
enum DebugLibrarySeed {
    static let argument = "--seed-library"

    private struct Entry {
        var title: String
        var hoursAgo: Double
        var duration: Double
        var sourceApp: RecordingSession.SourceApp?
        var segments: [Transcript.Segment]
        var speakerNames: [String: String] = [:]
        var summary: String?
        var chat: (question: String, answer: String)?
    }

    /// Writes the missing entries synchronously; call before the library loads.
    static func run() {
        guard ProcessInfo.processInfo.arguments.contains(argument) else { return }
        let root = RecordingSession.conversationsRoot
        let existing = Set(((try? FileManager.default.contentsOfDirectory(
            at: root, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? [])
            .compactMap { try? Conversation.read(from: $0).title })
        for entry in entries where !existing.contains(entry.title) {
            do {
                try write(entry, root: root)
            } catch {
                NSLog("EchoDiar: could not seed \"\(entry.title)\": \(error)")
            }
        }
    }

    private static func write(_ entry: Entry, root: URL) throws {
        let session = RecordingSession(
            state: .stopped,
            startedAt: Date().addingTimeInterval(-entry.hoursAgo * 3_600),
            sourceApps: entry.sourceApp.map { [$0] } ?? [],
            tapMode: entry.sourceApp == nil ? .global : .perApp,
            audioFileName: "audio.m4a")
        let folder = session.folder(in: root)
        try session.write(to: folder)
        let fixture = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("EchoDiarQA/two-voice.m4a")
        if FileManager.default.fileExists(atPath: fixture.path) {
            try FileManager.default.copyItem(at: fixture, to: folder.appendingPathComponent(session.audioFileName))
        }
        try Transcript(segments: entry.segments, speakerNames: entry.speakerNames).write(to: folder)
        var conversation = Conversation(
            id: session.id,
            title: entry.title,
            customTitle: true,
            createdAt: session.startedAt,
            duration: entry.duration,
            sourceAppName: entry.sourceApp?.name,
            sourceBundleID: entry.sourceApp?.bundleIDs.first,
            status: .transcribed,
            hasTranscript: true,
            hasSummary: entry.summary != nil,
            folder: folder)
        try conversation.write()
        if let summary = entry.summary { try conversation.writeSummary(summary) }
        if let chat = entry.chat {
            try ChatLog(messages: [
                .init(role: .user, text: chat.question),
                .init(role: .assistant, text: chat.answer),
            ]).write(to: folder)
        }
        conversation = (try? Conversation.read(from: folder)) ?? conversation
        conversation.sizeBytes = LibraryStore.sizeOnDisk(of: folder)
        try conversation.write()
    }

    private static func me(_ start: Double, _ end: Double, _ text: String, _ language: String = "en") -> Transcript.Segment {
        Transcript.Segment(
            start: start, end: end, channel: .mic, speaker: Transcript.Speaker.me, text: text, language: language)
    }

    private static func other(_ start: Double, _ end: Double, _ text: String, _ language: String = "en") -> Transcript.Segment {
        Transcript.Segment(
            start: start, end: end, channel: .system, speaker: Transcript.Speaker.system(1), text: text, language: language)
    }

    private static let entries: [Entry] = [
        Entry(
            title: "Weekly sync with Lan",
            hoursAgo: 3,
            duration: 1_712,
            sourceApp: .init(name: "Zoom", bundleIDs: ["us.zoom.xos"]),
            segments: [
                other(0, 6.5, "Hi, can you hear me? I just joined from the other room."),
                me(7, 14, "Yes, loud and clear. Let's go through the release checklist first."),
                other(15, 31, "Sure. The build is green, the notarization passed and the updater feed is ready."),
                me(32, 45, "Great. What about the translations, are they all in?"),
                other(46, 62, "Mình nghĩ bản tiếng Việt nên ra trước cuối tháng, phần còn lại đã xong.", "vi"),
                me(63, 80, "Okay, then we ship English first and add Vietnamese in the next update."),
                other(81, 95, "Agreed. I'll send the notes to the team after the call."),
            ],
            speakerNames: ["s1": "Lan"],
            summary: """
            ## Summary
            Release checklist review: build, notarization and update feed are ready.

            ## Decisions
            - Ship English first; the Vietnamese translation follows in the next update.

            ## Action items
            - Lan sends the meeting notes to the team.
            """,
            chat: (
                question: "When does the Vietnamese translation ship?",
                answer: "In the next update, before the end of the month. English ships first."
            )),
        Entry(
            title: "Quarterly planning review with the product, design and engineering leads about the roadmap, "
                + "hiring plan, budget for the next two quarters and the migration of the legacy sync service",
            hoursAgo: 27,
            duration: 3_245,
            sourceApp: .init(name: "Microsoft Teams", bundleIDs: ["com.microsoft.teams2"]),
            segments: [
                me(0, 12, "Thanks for joining. We have a lot to cover, so let's start with the roadmap."),
                other(13, 40, "The sync service migration is the biggest item. We need two more engineers for it."),
                me(41, 58, "Can we split it into phases so the first part lands this quarter?"),
                other(59, 85, "Yes, phase one moves the read path, phase two the writes. Each takes about six weeks."),
                me(86, 101, "Then let's plan phase one now and revisit the budget for phase two in a month."),
            ],
            summary: """
            ## Summary
            Roadmap and hiring review. The legacy sync service migration is split in two phases.

            ## Decisions
            - Phase one (read path) starts this quarter.
            - The phase two budget is reviewed in a month.
            """,
            chat: (
                question: "How long does each migration phase take?",
                answer: "About six weeks each: phase one moves the read path, phase two the writes."
            )),
        Entry(
            title: "Quick call about the invoice",
            hoursAgo: 75,
            duration: 214,
            sourceApp: nil,
            segments: [
                other(0, 9, "Hello, I'm calling about the invoice from last week."),
                me(10, 21, "Yes, I saw it. The amount looks right, I'll pay it tomorrow."),
                other(22, 30, "Perfect, thank you. Have a good day."),
            ]),
    ]
}
#endif
