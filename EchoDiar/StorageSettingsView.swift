import AppKit
import EchoCore
import SwiftUI

/// The Storage tab, one inner tab per section: disk usage by kind (each deleted at once after a confirmation) and the
/// largest conversations (their audio or the whole) with the data folder, deleting the audio of transcribed
/// conversations and compression, and auto-delete with a preview.
struct StorageSettingsView: View {
    let model: AppModel
    @State private var usage: LibraryStore.Usage?
    @State private var preview: [RetentionPolicy.Deletion] = []
    /// Shown before auto-delete is turned on: what it moves to the Trash right away.
    @State private var confirmingAutoDelete = false
    /// A mode or age change made while auto-delete is on that deletes more, waiting for its confirmation.
    @State private var pendingChange: PendingChange?
    @State private var confirmingAudioDelete = false
    @State private var deleting = false
    /// A usage row's Delete, waiting for its confirmation.
    @State private var bulkDeletion: BulkDeletion?

    private enum BulkDeletion {
        case audio, transcriptsAndAI, models
    }

    /// Ids in `SettingsTab.storage.subTabs`; picked in the toolbar.
    private enum Tab: String {
        case usage, audio, autoDelete
    }

    private var tab: Tab { model.settingsSubTab(.storage).flatMap(Tab.init) ?? .usage }

    private static let bitrates = [32, 48, 64, 96, 128]
    private static let largestCount = 5

    var body: some View {
        Form {
            switch tab {
            case .usage:
                usageSection
                dataFolderSection
            case .audio: audioSection
            case .autoDelete: autoDeleteSection
            }
        }
        .formStyle(.grouped)
        // Measured again after a deletion or a new conversation, and only while the tab is shown (it stays alive) on
        // the Settings page of the main window on screen.
        .task(id: UsageKey(visible: shown, library: libraryKey, models: model.installedModelsBytes)) {
            guard shown else { return }
            usage = await model.storageUsage()
        }
        .task(id: PreviewKey(visible: shown, days: model.settings.autoDeleteAfterDays,
                             mode: model.settings.autoDeleteMode, library: libraryKey)) {
            guard shown else { return }
            preview = await model.retentionPreview(settings: model.settings)
        }
        .alert(
            "Delete the audio of \(transcribedWithAudio.count) transcribed conversations?",
            isPresented: $confirmingAudioDelete
        ) {
            Button("Move to Trash", role: .destructive, action: deleteTranscribedAudio)
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The audio files are moved to the Trash. Transcripts, summaries and chats stay.")
        }
        .alert(bulkTitle, isPresented: Binding(
            get: { bulkDeletion != nil },
            set: { if !$0 { bulkDeletion = nil } }
        ), presenting: bulkDeletion) { deletion in
            Button("Delete", role: .destructive) { deleteAll(deletion) }
            Button("Cancel", role: .cancel) {}
        } message: { deletion in
            Text(bulkMessage(deletion))
        }
        .alert("Turn on auto-delete?", isPresented: $confirmingAutoDelete) {
            Button(preview.isEmpty ? "Turn On" : "Turn On and Delete", role: .destructive) {
                model.updateSettings { $0.autoDeleteEnabled = true }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(autoDeleteMessage)
        }
        .alert("Delete more at the next run?", isPresented: Binding(
            get: { pendingChange != nil },
            set: { if !$0 { pendingChange = nil } }
        ), presenting: pendingChange) { change in
            Button("Change", role: .destructive) {
                model.updateSettings {
                    $0.autoDeleteAfterDays = change.days
                    $0.autoDeleteMode = change.mode
                }
            }
            Button("Cancel", role: .cancel) {}
        } message: { change in
            Text(changeMessage(change))
        }
    }

    /// The Storage tab is selected on the Settings page and the main window is on screen.
    private var shown: Bool { model.route == .settings && model.settingsTab == .storage && model.mainWindowVisible }

    // MARK: - Usage

    private var usageSection: some View {
        Section {
            if let usage {
                UsageBar(usage: usage)
                usageRow("Audio", usage.audio, color: .blue, delete: .audio, disabled: audioCandidates.isEmpty)
                usageRow("Transcripts and AI", usage.transcriptsAndAI, color: .green, delete: .transcriptsAndAI,
                         disabled: transcriptCandidates.isEmpty)
                usageRow("Models", usage.models, color: .orange, delete: .models,
                         disabled: model.deletableModelIDs.isEmpty || model.isRecording || model.aiJob != nil
                            || model.transcriptionProgress.keys.contains(where: model.isTranscribing))
                LabeledContent("Total") {
                    HStack(spacing: 10) {
                        Text(Self.bytes(usage.total)).monospacedDigit().fontWeight(.semibold)
                        Color.clear.frame(width: Self.trashWidth, height: 1)
                    }
                }
                if !usage.conversations.isEmpty {
                    DisclosureGroup("Largest conversations") {
                        let largest = usage.conversations.prefix(Self.largestCount)
                        VStack(alignment: .leading, spacing: 12) {
                            ForEach(largest) { usage in
                                LargestConversationRow(
                                    usage: usage, largest: largest.first?.total ?? 0,
                                    conversation: model.conversations.first { $0.id == usage.id }, model: model)
                            }
                        }
                        .padding(.leading, TitleChevronDisclosureStyle.indent)
                        .padding(.top, 6)
                    }
                    .disclosureGroupStyle(TitleChevronDisclosureStyle())
                }
            } else {
                ProgressView().frame(maxWidth: .infinity)
            }
        }
    }

    /// The trash button's width; the Total row leaves it empty so the sizes line up.
    private static let trashWidth: CGFloat = 16

    private func usageRow(
        _ title: LocalizedStringKey, _ bytes: Int64, color: Color, delete: BulkDeletion, disabled: Bool
    ) -> some View {
        LabeledContent {
            HStack(spacing: 10) {
                Text(Self.bytes(bytes)).monospacedDigit()
                Button { bulkDeletion = delete } label: {
                    Image(systemName: "trash")
                }
                .buttonStyle(.borderless)
                .frame(width: Self.trashWidth)
                .disabled(disabled || deleting)
                .help(Self.deleteHelp(delete))
                .accessibilityLabel(Self.deleteHelp(delete))
            }
        } label: {
            Label {
                Text(title)
            } icon: {
                Circle().fill(color).frame(width: 8, height: 8)
            }
        }
    }

    static func bytes(_ count: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: count, countStyle: .file)
    }

    private var dataFolderSection: some View {
        Section {
            LabeledContent("Data folder") {
                HStack {
                    Text((RecordingSession.conversationsRoot.path as NSString).abbreviatingWithTildeInPath)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .textSelection(.enabled)
                    Button("Open in Finder") {
                        let root = RecordingSession.conversationsRoot
                        // Created on the first recording, so it may not exist yet.
                        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(root)
                    }
                }
            }
        }
    }

    // MARK: - Audio

    private var audioSection: some View {
        Section {
            let candidates = transcribedWithAudio
            LabeledContent {
                Button("Delete audio of transcribed conversations…") { confirmingAudioDelete = true }
                    .disabled(candidates.isEmpty || deleting)
            } label: {
                Text("Free space")
                Text("Transcripts, summaries and chats stay; playback and re-transcribing are no longer possible.")
            }
            Toggle(isOn: model.setting(\.deleteAudioAfterTranscription)) {
                Text("Delete audio after transcription")
                Text("Moves the audio to the Trash once a transcription succeeds.")
            }
            Picker("Compression bitrate", selection: model.setting(\.compressionBitrateKbps)) {
                ForEach(Self.bitrates, id: \.self) { Text("\($0) kbps").tag($0) }
                if !Self.bitrates.contains(model.settings.compressionBitrateKbps) {
                    Text("\(model.settings.compressionBitrateKbps) kbps").tag(model.settings.compressionBitrateKbps)
                }
            }
            Toggle(isOn: model.setting(\.keepUncompressedAudio)) {
                Text("Keep uncompressed audio")
                Text("Keeps the original CAF next to the compressed file (about 10 times larger).")
            }
            Picker(selection: model.setting(\.playbackChannels)) {
                Text("Both voices in both ears").tag(PlaybackChannels.mixed)
                Text("My voice left, others right").tag(PlaybackChannels.split)
            } label: {
                Text("Playback")
                Text("How the transcript plays your microphone and the meeting's sound.")
            }
        }
    }

    // MARK: - Delete by kind

    /// Conversations with audio that can lose it now (not recorded, compressed or transcribed).
    private var audioCandidates: [Conversation] {
        model.conversations.filter { $0.hasAudio && DeleteMenuItems.canDelete($0, model) }
    }

    /// Conversations with a transcript, summary or chat to delete, or without audio (deleted whole).
    private var transcriptCandidates: [Conversation] {
        model.conversations.filter {
            DeleteMenuItems.canDelete($0, model) && ($0.hasTranscript || $0.hasSummary || !$0.hasAudio)
        }
    }

    private static func deleteHelp(_ deletion: BulkDeletion) -> LocalizedStringKey {
        switch deletion {
        case .audio: "Delete the audio of every conversation"
        case .transcriptsAndAI: "Delete every transcript, summary and chat"
        case .models: "Delete every downloaded model"
        }
    }

    private var bulkTitle: LocalizedStringKey {
        switch bulkDeletion {
        case .audio?: "Delete the audio of \(audioCandidates.count) conversations?"
        case .transcriptsAndAI?: "Delete the transcripts, summaries and chats of \(transcriptCandidates.count) conversations?"
        case .models?: "Delete \(model.deletableModelIDs.count) downloaded models?"
        case nil: ""
        }
    }

    private func bulkMessage(_ deletion: BulkDeletion) -> String {
        switch deletion {
        case .audio:
            let untranscribed = audioCandidates.filter { !$0.hasTranscript }.count
            let note = untranscribed == 0 ? ""
                : "\n\n" + String(localized: "\(untranscribed) of them have no transcript yet: nothing of it will be left to read.")
            return String(localized: "The audio files are moved to the Trash. Transcripts, summaries and chats stay; playback and re-transcribing are no longer possible.") + note
        case .transcriptsAndAI:
            let withoutAudio = transcriptCandidates.filter { !$0.hasAudio }.count
            let note = withoutAudio == 0 ? ""
                : "\n\n" + String(localized: "\(withoutAudio) conversations without audio would be left empty: they are moved to the Trash entirely.")
            return String(localized: "They are moved to the Trash. The audio stays, so the conversations can be transcribed again.") + note
        case .models:
            return String(localized: "The transcription, speaker and AI models are removed from this Mac (not moved to the Trash). Download them again in Settings › Transcription and Settings › AI.")
        }
    }

    private func deleteAll(_ deletion: BulkDeletion) {
        switch deletion {
        case .audio:
            let ids = audioCandidates.map(\.id)
            deleting = true
            Task {
                await model.deleteAudio(of: ids)
                deleting = false
            }
        case .transcriptsAndAI:
            let ids = transcriptCandidates.map(\.id)
            deleting = true
            Task {
                await model.deleteTranscriptsAndAI(of: ids)
                deleting = false
            }
        case .models:
            model.deleteAllModels()
        }
    }

    // MARK: - Delete audio

    /// Transcribed conversations whose audio is still on disk, per the last usage measure.
    private var transcribedWithAudio: [Conversation] {
        let withAudio = Set((usage?.conversations ?? []).filter { $0.audio > 0 }.map(\.id))
        return model.conversations.filter { $0.status == .transcribed && $0.hasTranscript && withAudio.contains($0.id) }
    }

    private func deleteTranscribedAudio() {
        let ids = transcribedWithAudio.map(\.id)
        deleting = true
        Task {
            await model.deleteAudio(of: ids)
            deleting = false
        }
    }

    // MARK: - Auto-delete

    private var autoDeleteSection: some View {
        Section {
            Toggle(isOn: autoDeleteEnabled) {
                Text("Auto-delete old conversations")
                Text("Once a day, moves transcribed conversations older than the age below to the Trash.")
            }
            NumberRow(
                title: "Older than", unit: String(localized: "days"), value: autoDeleteDays,
                range: AppSettings.autoDeleteDaysRange)
            Picker("Delete", selection: autoDeleteMode) {
                Text("Audio only (keep transcript, summary and chat)").tag(RetentionMode.audioOnly)
                Text("Whole conversation").tag(RetentionMode.wholeConversation)
            }
            if preview.isEmpty {
                Text("Nothing matches now.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                DisclosureGroup(model.settings.autoDeleteEnabled
                                ? "Next run: \(preview.count) conversations"
                                : "Would delete now: \(preview.count) conversations") {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(preview, id: \.conversationID) { deletion in
                            Text(title(of: deletion))
                                .lineLimit(1)
                                .foregroundStyle(.secondary)
                        }
                    }
                    .padding(.leading, TitleChevronDisclosureStyle.indent)
                }
                .disclosureGroupStyle(TitleChevronDisclosureStyle())
            }
        }
    }

    /// Turning on asks first, with the preview; turning off is immediate.
    private var autoDeleteEnabled: Binding<Bool> {
        Binding(
            get: { model.settings.autoDeleteEnabled },
            set: { enabled in
                if enabled {
                    confirmingAutoDelete = true
                } else {
                    model.updateSettings { $0.autoDeleteEnabled = false }
                }
            })
    }

    private var autoDeleteDays: Binding<Int> {
        Binding(
            get: { pendingChange?.days ?? model.settings.autoDeleteAfterDays },
            set: { days in changeAutoDelete { $0.autoDeleteAfterDays = days } })
    }

    private var autoDeleteMode: Binding<RetentionMode> {
        Binding(
            get: { pendingChange?.mode ?? model.settings.autoDeleteMode },
            set: { mode in changeAutoDelete { $0.autoDeleteMode = mode } })
    }

    /// Off: saved at once. On: saved at once unless the next run would then delete more than now (another
    /// conversation, or whole folders instead of audio), which asks first with the new list; Cancel keeps the value.
    private func changeAutoDelete(_ change: @escaping (inout AppSettings) -> Void) {
        guard model.settings.autoDeleteEnabled else {
            model.updateSettings(change)
            return
        }
        let current = model.settings
        var updated = current
        change(&updated)
        guard updated != current else { return }
        Task {
            let currentPreview = await model.retentionPreview(settings: current)
            let newPreview = await model.retentionPreview(settings: updated)
            if Self.deletesMore(newPreview, than: currentPreview) {
                pendingChange = PendingChange(
                    days: updated.autoDeleteAfterDays, mode: updated.autoDeleteMode, preview: newPreview)
            } else {
                model.updateSettings(change)
            }
        }
    }

    /// `new` has a conversation `current` has not, or deletes the whole folder of one whose audio only it deletes.
    private static func deletesMore(_ new: [RetentionPolicy.Deletion], than current: [RetentionPolicy.Deletion]) -> Bool {
        let currentModes = Dictionary(current.map { ($0.conversationID, $0.mode) }, uniquingKeysWith: { first, _ in first })
        return new.contains { deletion in
            guard let mode = currentModes[deletion.conversationID] else { return true }
            return mode == .audioOnly && deletion.mode == .wholeConversation
        }
    }

    private struct PendingChange {
        let days: Int
        let mode: RetentionMode
        let preview: [RetentionPolicy.Deletion]
    }

    private var autoDeleteMessage: String {
        let audioOnly = model.settings.autoDeleteMode == .audioOnly
        let days = model.settings.autoDeleteAfterDays
        guard !preview.isEmpty else {
            return audioOnly
                ? String(localized: "The audio of transcribed conversations older than \(days) days will be moved to the Trash once a day. Nothing matches now.")
                : String(localized: "All files of transcribed conversations older than \(days) days will be moved to the Trash once a day. Nothing matches now.")
        }
        let count = preview.count
        let sentence = audioOnly
            ? String(localized: "The audio of \(count) conversations will be moved to the Trash now, then daily for older ones:")
            : String(localized: "All files of \(count) conversations will be moved to the Trash now, then daily for older ones:")
        return "\(sentence)\n\n\(titleList(preview))"
    }

    private func changeMessage(_ change: PendingChange) -> String {
        let count = change.preview.count
        let sentence = change.mode == .audioOnly
            ? String(localized: "The audio of \(count) conversations will be moved to the Trash at the next run (at launch or within a day):")
            : String(localized: "All files of \(count) conversations will be moved to the Trash at the next run (at launch or within a day):")
        return "\(sentence)\n\n\(titleList(change.preview))"
    }

    private func titleList(_ deletions: [RetentionPolicy.Deletion]) -> String {
        let titles = deletions.prefix(10).map { "• \(title(of: $0))" }.joined(separator: "\n")
        let more = deletions.count > 10 ? "\n" + String(localized: "…and \(deletions.count - 10) more") : ""
        return titles + more
    }

    private func title(of deletion: RetentionPolicy.Deletion) -> String {
        model.conversations.first { $0.id == deletion.conversationID }?.title ?? deletion.items.first?.lastPathComponent ?? ""
    }

    /// What the usage and the preview depend on. Not the conversations themselves: a recording's size changes at
    /// every library refresh, which the preview itself triggers. A transcribed conversation's size changes only when
    /// its files do (audio deleted, summary written).
    private var libraryKey: [LibraryEntry] {
        model.conversations.map {
            LibraryEntry(id: $0.id, status: $0.status, hasTranscript: $0.hasTranscript, hasAudio: $0.hasAudio,
                         size: $0.status == .transcribed ? $0.sizeBytes : nil)
        }
    }

    private struct LibraryEntry: Equatable {
        let id: UUID
        let status: Conversation.Status
        let hasTranscript: Bool
        let hasAudio: Bool
        let size: Int64?
    }

    private struct UsageKey: Equatable {
        let visible: Bool
        let library: [LibraryEntry]
        let models: Int64
    }

    private struct PreviewKey: Equatable {
        let visible: Bool
        let days: Int
        let mode: RetentionMode
        let library: [LibraryEntry]
    }
}

/// One of the largest conversations: its title and size over a bar as long as its share of the largest one, audio
/// and the rest in the usage bar's colors, with a Delete menu (its audio or the whole conversation).
private struct LargestConversationRow: View {
    let usage: LibraryStore.ConversationUsage
    let largest: Int64
    /// Nil once it left the library, until the usage is measured again.
    let conversation: Conversation?
    let model: AppModel

    var body: some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 5) {
                HStack(alignment: .firstTextBaseline) {
                    Text(usage.title).lineLimit(1).truncationMode(.middle)
                    Spacer(minLength: 8)
                    Text(StorageSettingsView.bytes(usage.total))
                        .monospacedDigit()
                        .foregroundStyle(.secondary)
                }
                GeometryReader { proxy in
                    let width = largest > 0 ? proxy.size.width * CGFloat(usage.total) / CGFloat(largest) : 0
                    let audio = usage.total > 0 ? width * CGFloat(usage.audio) / CGFloat(usage.total) : 0
                    HStack(spacing: 0) {
                        Rectangle().fill(.blue).frame(width: audio)
                        Rectangle().fill(.green).frame(width: max(0, width - audio))
                    }
                    .clipShape(Capsule())
                }
                .frame(height: 4)
                .background(Palette.hoverFill, in: Capsule())
                .accessibilityHidden(true)
            }
            Menu {
                if let conversation { DeleteMenuItems(conversation: conversation, model: model) }
            } label: {
                Image(systemName: "trash")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .disabled(conversation.map { !DeleteMenuItems.canDelete($0, model) } ?? true)
            .help("Delete the audio or the whole conversation")
            .accessibilityLabel("Delete \(usage.title)")
        }
    }
}

/// Audio, transcripts and models as parts of one bar.
private struct UsageBar: View {
    let usage: LibraryStore.Usage

    var body: some View {
        GeometryReader { proxy in
            HStack(spacing: 0) {
                part(usage.audio, .blue, proxy.size.width)
                part(usage.transcriptsAndAI, .green, proxy.size.width)
                part(usage.models, .orange, proxy.size.width)
                Spacer(minLength: 0)
            }
        }
        .frame(height: 10)
        .background(Palette.hoverFill, in: Capsule())
        .clipShape(Capsule())
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Storage used: \(StorageSettingsView.bytes(usage.total))")
    }

    @ViewBuilder private func part(_ bytes: Int64, _ color: Color, _ width: CGFloat) -> some View {
        if bytes > 0, usage.total > 0 {
            Rectangle()
                .fill(color)
                .frame(width: width * CGFloat(bytes) / CGFloat(usage.total))
        }
    }
}
