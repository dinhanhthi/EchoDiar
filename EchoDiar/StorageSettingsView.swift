import AppKit
import EchoCore
import SwiftUI

/// The Storage tab: disk usage by kind and the largest conversations, deleting the audio of transcribed
/// conversations, auto-delete with a preview, compression, and the data folder.
struct StorageSettingsView: View {
    let model: AppModel
    @State private var usage: LibraryStore.Usage?
    @State private var preview: [RetentionPolicy.Deletion] = []
    /// Shown before auto-delete is turned on: what it moves to the Trash right away.
    @State private var confirmingAutoDelete = false
    /// A mode or age change made while auto-delete is on that deletes more, waiting for its confirmation.
    @State private var pendingChange: PendingChange?
    /// False while the Settings window is closed, minimized or covered: no scans then.
    @State private var windowVisible = true
    @State private var confirmingAudioDelete = false
    @State private var deleting = false

    private static let bitrates = [32, 48, 64, 96, 128]
    private static let largestCount = 5

    var body: some View {
        Form {
            usageSection
            Section("Audio") {
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
            }
            autoDeleteSection
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
        .formStyle(.grouped)
        // Measured again after a deletion or a new conversation, and only while the tab is shown (it stays alive) in
        // the Settings window on screen.
        .task(id: UsageKey(visible: shown, library: libraryKey, models: model.installedModelsBytes)) {
            guard shown else { return }
            usage = await model.storageUsage()
        }
        .task(id: PreviewKey(visible: shown, days: model.settings.autoDeleteAfterDays,
                             mode: model.settings.autoDeleteMode, library: libraryKey)) {
            guard shown else { return }
            preview = await model.retentionPreview(settings: model.settings)
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didChangeOcclusionStateNotification)) { note in
            guard let window = note.object as? NSWindow, window.frameAutosaveName == "SettingsSidebar" else { return }
            windowVisible = window.occlusionState.contains(.visible)
        }
        .alert(
            "Delete the audio of \(transcribedWithAudio.count) transcribed conversation\(transcribedWithAudio.count == 1 ? "" : "s")?",
            isPresented: $confirmingAudioDelete
        ) {
            Button("Move to Trash", role: .destructive, action: deleteTranscribedAudio)
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The audio files are moved to the Trash. Transcripts, summaries and chats stay.")
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

    /// The Storage tab is selected and the Settings window is on screen.
    private var shown: Bool { model.settingsTab == .storage && windowVisible }

    // MARK: - Usage

    private var usageSection: some View {
        Section("Usage") {
            if let usage {
                UsageBar(usage: usage)
                usageRow("Audio", usage.audio, color: .blue)
                usageRow("Transcripts and AI", usage.transcriptsAndAI, color: .green)
                usageRow("Models", usage.models, color: .orange)
                LabeledContent("Total") {
                    Text(Self.bytes(usage.total)).monospacedDigit().fontWeight(.semibold)
                }
                if !usage.conversations.isEmpty {
                    DisclosureGroup("Largest conversations") {
                        ForEach(usage.conversations.prefix(Self.largestCount)) { conversation in
                            LabeledContent(conversation.title) {
                                Text(Self.bytes(conversation.total)).monospacedDigit()
                            }
                            .lineLimit(1)
                        }
                    }
                }
            } else {
                ProgressView().frame(maxWidth: .infinity)
            }
        }
    }

    private func usageRow(_ title: String, _ bytes: Int64, color: Color) -> some View {
        LabeledContent {
            Text(Self.bytes(bytes)).monospacedDigit()
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
                title: "Older than", unit: "days", value: autoDeleteDays,
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
                                ? "Next run: \(countText(preview.count))"
                                : "Would delete now: \(countText(preview.count))") {
                    ForEach(preview, id: \.conversationID) { deletion in
                        Text(title(of: deletion))
                            .lineLimit(1)
                            .foregroundStyle(.secondary)
                    }
                }
            }
        } header: {
            Text("Auto-delete")
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
        let what = model.settings.autoDeleteMode == .audioOnly ? "The audio of" : "All files of"
        guard !preview.isEmpty else {
            return "\(what) transcribed conversations older than \(model.settings.autoDeleteAfterDays) days will be moved to the Trash once a day. Nothing matches now."
        }
        return "\(what) \(countText(preview.count)) will be moved to the Trash now, then daily for older ones:\n\n\(titleList(preview))"
    }

    private func changeMessage(_ change: PendingChange) -> String {
        let what = change.mode == .audioOnly ? "The audio of" : "All files of"
        return "\(what) \(countText(change.preview.count)) will be moved to the Trash at the next run (at launch or within a day):\n\n\(titleList(change.preview))"
    }

    private func titleList(_ deletions: [RetentionPolicy.Deletion]) -> String {
        let titles = deletions.prefix(10).map { "• \(title(of: $0))" }.joined(separator: "\n")
        let more = deletions.count > 10 ? "\n…and \(deletions.count - 10) more" : ""
        return titles + more
    }

    private func countText(_ count: Int) -> String {
        count == 1 ? "1 conversation" : "\(count) conversations"
    }

    private func title(of deletion: RetentionPolicy.Deletion) -> String {
        model.conversations.first { $0.id == deletion.conversationID }?.title ?? deletion.items.first?.lastPathComponent ?? ""
    }

    /// What the usage and the preview depend on. Not the conversations themselves: a recording's size changes at
    /// every library refresh, which the preview itself triggers. A transcribed conversation's size changes only when
    /// its files do (audio deleted, summary written).
    private var libraryKey: [LibraryEntry] {
        model.conversations.map {
            LibraryEntry(id: $0.id, status: $0.status, hasTranscript: $0.hasTranscript,
                         size: $0.status == .transcribed ? $0.sizeBytes : nil)
        }
    }

    private struct LibraryEntry: Equatable {
        let id: UUID
        let status: Conversation.Status
        let hasTranscript: Bool
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
