import AppKit
import EchoCore
import EchoTranscribe
import SwiftUI

extension Notification.Name {
    /// Space in the main window on a conversation's Transcript tab (posted by the app delegate's key monitor).
    static let togglePlayback = Notification.Name("EchoDiar.togglePlayback")
}

/// One conversation inside the main window: title (double-click to rename), details, speakers (click to rename),
/// playback and actions, then the Transcript, Summary and Chat tabs. A click on a segment plays from its start; Space
/// plays or pauses. Stays mounted across tabs, so playback goes on in Summary and Chat.
struct TranscriptView: View {
    let conversation: Conversation
    let model: AppModel
    @State private var transcript: Transcript?
    /// Speaker ids of `transcript`: me first, then s1, s2…
    @State private var speakers: [String] = []
    /// `transcript.json` exists but could not be read.
    @State private var loadFailed = false
    @State private var playback = PlaybackController()
    @State private var editingTitle = false
    @State private var draft = ""
    @FocusState private var titleFocused: Bool
    /// The speaker whose rename popover is open.
    @State private var renaming: String?
    @State private var nameDraft = ""
    /// Language for the next Re-transcribe; "" keeps the recording's language markers.
    @State private var retranscribeLanguage = ""
    /// The segment the list scrolls to, set by an AI citation; cleared once scrolled.
    @State private var scrollTarget: UUID?
    /// The folder still has audio: false once Storage or auto-delete moved it to the Trash (no Play, no Re-transcribe).
    @State private var hasAudio = true

    var body: some View {
        VStack(spacing: 10) {
            header
            // Interim tabs, restyled later.
            Picker("View", selection: Binding(get: { model.detailTab }, set: { model.detailTab = $0 })) {
                Text("Transcript").tag(AppModel.DetailTab.transcript)
                Text("Summary").tag(AppModel.DetailTab.summary)
                Text("Chat").tag(AppModel.DetailTab.chat)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            if model.detailTab == .transcript {
                content
            } else {
                AIPanelView(conversation: conversation, model: model)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .task(id: conversation) { await load() }
        .task { model.refreshInstalledModels() }
        .onChange(of: model.pendingTranscriptTime) { showPendingTime() }
        // Not while a title or speaker name is edited: the text field takes Space.
        .onReceive(NotificationCenter.default.publisher(for: .togglePlayback)) { _ in
            guard playback.isReady, !editingTitle, renaming == nil else { return }
            playback.togglePlay()
        }
        .onDisappear { playback.close() }
    }

    // MARK: - Header

    private var header: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    title
                    details
                }
                .layoutPriority(1)
                Spacer(minLength: 8)
                if conversation.hasTranscript && !failed {
                    TranscriptionStatus(conversation: conversation, model: model)
                }
            }
            // A failed re-transcription gets its own row, so the title keeps its space.
            if conversation.hasTranscript && failed {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    TranscriptionFailureReason(conversation: conversation, model: model)
                    Spacer(minLength: 8)
                    TranscriptionStatus(conversation: conversation, model: model)
                }
            }
            if transcript?.notes.contains(Transcript.diarizationMissingNote) == true {
                diarizationMissingNotice
            }
            if transcript != nil && !speakers.isEmpty {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        ForEach(speakers, id: \.self, content: speakerChip)
                    }
                }
            }
            HStack(spacing: 8) {
                actions
                Spacer(minLength: 8)
                if hasAudio { transport }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(GlassCard(cornerRadius: 20))
    }

    private var failed: Bool { TranscriptionStatus.showsFailure(conversation, model) }

    /// The transcript was made without the speaker model, so every system-audio voice is Speaker 1.
    private var diarizationMissingNotice: some View {
        let installed = model.installedModelIDs.contains(ModelCatalog.defaultDiarizer.id)
        return HStack(alignment: .firstTextBaseline, spacing: 8) {
            Group {
                Image(systemName: "person.2.slash")
                if installed {
                    Text("Speakers were not told apart. The speaker model is installed now: re-transcribe.")
                } else {
                    Text("Speakers were not told apart: the speaker model is not installed (Transcription › Balanced).")
                }
            }
            .foregroundStyle(.secondary)
            if !installed {
                Button("Download…") { model.openSettings(tab: .transcription) }
                    .buttonStyle(.link)
            }
        }
        .font(.caption)
    }

    @ViewBuilder private var title: some View {
        if editingTitle {
            TextField("Title", text: $draft)
                .textFieldStyle(.plain)
                .font(.headline)
                .focused($titleFocused)
                .onAppear { titleFocused = true }
                .onSubmit {
                    editingTitle = false
                    if draft != conversation.title { model.renameConversation(conversation.id, to: draft) }
                }
                .onExitCommand { editingTitle = false }
                // Clicking elsewhere cancels, like Esc; only Return saves.
                .onChange(of: titleFocused) { _, focused in if !focused { editingTitle = false } }
        } else {
            Text(conversation.title)
                .font(.headline)
                .lineLimit(1)
                .truncationMode(.tail)
                .onTapGesture(count: 2) {
                    draft = conversation.title
                    editingTitle = true
                }
                .help("Double-click to rename")
        }
    }

    private var details: some View {
        HStack(spacing: 8) {
            Text(conversation.createdAt.formatted(date: .abbreviated, time: .shortened))
            if conversation.duration > 0 {
                Text(ConversationCard.durationText(conversation.duration)).monospacedDigit()
            }
            if conversation.sizeBytes > 0 {
                Text(ByteCountFormatter.string(fromByteCount: conversation.sizeBytes, countStyle: .file))
            }
            if let badge = StatusBadge(status: conversation.status) { badge }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .lineLimit(1)
    }

    /// Small bordered buttons, each with its own background (no glass on the glass card); Re-transcribe shows its
    /// menu arrow.
    private var actions: some View {
        HStack(spacing: 6) {
            iconButton("Copy transcript", systemImage: "doc.on.doc", action: copyTranscript)
                .disabled(transcript?.segments.isEmpty ?? true)
            retranscribeMenu
            iconButton("Reveal in Finder", systemImage: "folder") {
                NSWorkspace.shared.activateFileViewerSelecting([conversation.folder])
            }
        }
    }

    private func iconButton(_ title: String, systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .labelStyle(.iconOnly)
        }
        .buttonStyle(.bordered)
        .controlSize(.small)
        .help(title)
    }

    /// Installed batch ASR models and configured cloud engines; picking one transcribes the conversation again with
    /// it, in the language picked below them.
    private var retranscribeMenu: some View {
        Menu {
            EngineMenuItems(title: "Transcribe again with", model: model) { engineID in
                let language = retranscribeLanguage.isEmpty ? nil : retranscribeLanguage
                model.transcribe(conversation.id, engineID: engineID, language: language)
            }
            if !model.installedBatchEngines.isEmpty || !model.cloudEngineConfigs.isEmpty {
                LanguagePicker(title: "Language", selection: $retranscribeLanguage, model: model, asRecorded: true)
            }
        } label: {
            Label("Re-transcribe", systemImage: "arrow.clockwise")
                .labelStyle(.iconOnly)
        }
        .menuStyle(.button)
        .buttonStyle(.bordered)
        .controlSize(.small)
        .fixedSize()
        .disabled(!canTranscribe)
        .help("Re-transcribe with another model or language")
    }

    /// Not while a job runs, the audio is still being recorded or compressed, or it was deleted.
    private var canTranscribe: Bool {
        hasAudio && !model.isTranscribing(conversation.id) && conversation.status != .recording
            && conversation.status != .compressing && conversation.status != .failed
    }

    private var transport: some View {
        HStack(spacing: 8) {
            Button(action: playback.togglePlay) {
                Label(playback.isPlaying ? "Pause" : "Play", systemImage: playback.isPlaying ? "pause.fill" : "play.fill")
                    .labelStyle(.iconOnly)
                    .font(.system(size: 13))
            }
            .buttonStyle(RowIconButtonStyle())
            .disabled(!playback.isReady)
            .help(playback.isPlaying ? "Pause (Space)" : "Play (Space)")
            PlaybackTime(playback: playback)
        }
    }

    // MARK: - Speakers

    private func speakerChip(_ speaker: String) -> some View {
        Button {
            nameDraft = transcript?.speakerNames[speaker] ?? ""
            renaming = speaker
        } label: {
            HStack(spacing: 5) {
                Circle().fill(Self.color(for: speaker)).frame(width: 7, height: 7)
                Text(transcript?.displayName(for: speaker) ?? speaker)
            }
            .font(.caption.weight(.medium))
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(Palette.hoverFill, in: Capsule())
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .disabled(model.isTranscribing(conversation.id))
        .help("Rename this speaker")
        .popover(isPresented: Binding(
            get: { renaming == speaker },
            set: { if !$0 { renaming = nil } }
        )) {
            VStack(alignment: .leading, spacing: 8) {
                Text("Name for \(Transcript.defaultName(for: speaker))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                TextField(Transcript.defaultName(for: speaker), text: $nameDraft)
                    .onSubmit {
                        rename(speaker, to: nameDraft)
                        renaming = nil
                    }
            }
            .padding(12)
            .frame(width: 220)
        }
    }

    /// Saves the name into `transcript.json` (read fresh, only this name changes); a blank name restores the default.
    private func rename(_ speaker: String, to name: String) {
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard var updated = transcript, updated.speakerNames[speaker] != (name.isEmpty ? nil : name) else { return }
        updated.speakerNames[speaker] = name.isEmpty ? nil : name
        transcript = updated
        let folder = conversation.folder
        Task.detached(priority: .utility) {
            do {
                var stored = try Transcript.read(from: folder)
                stored.speakerNames[speaker] = name.isEmpty ? nil : name
                try stored.write(to: folder)
            } catch {
                NSLog("EchoDiar: could not save the speaker name in \(folder.lastPathComponent): \(error)")
            }
        }
    }

    /// Me in the accent color; other speakers by number from a fixed list, so a speaker keeps its color.
    static func color(for speaker: String) -> Color {
        if speaker == Transcript.Speaker.me { return .accentColor }
        let colors: [Color] = [.orange, .green, .pink, .purple, .teal, .yellow, .mint, .indigo]
        let number = speaker.hasPrefix("s") ? Int(speaker.dropFirst()) : nil
        let index = number.map { $0 - 1 } ?? speaker.unicodeScalars.reduce(0) { $0 + Int($1.value) }
        return colors[max(index, 0) % colors.count]
    }

    /// me, then s1, s2… by number, then any other id.
    private static func speakers(in transcript: Transcript) -> [String] {
        func order(_ speaker: String) -> (Int, Int, String) {
            if speaker == Transcript.Speaker.me { return (0, 0, speaker) }
            if speaker.hasPrefix("s"), let number = Int(speaker.dropFirst()) { return (1, number, speaker) }
            return (2, 0, speaker)
        }
        return Set(transcript.segments.map(\.speaker)).sorted { order($0) < order($1) }
    }

    // MARK: - Segments

    @ViewBuilder private var content: some View {
        if let transcript, !transcript.segments.isEmpty {
            SegmentList(transcript: transcript, playback: playback, scrollTarget: $scrollTarget)
        } else if loading {
            ProgressView().frame(maxHeight: .infinity)
        } else {
            VStack(spacing: 10) {
                Text(emptyText).foregroundStyle(.secondary)
                if !conversation.hasTranscript {
                    TranscriptionFailureReason(conversation: conversation, model: model)
                        .multilineTextAlignment(.center)
                    TranscriptionStatus(conversation: conversation, model: model)
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 16)
            .modifier(GlassCard(cornerRadius: 20))
            .frame(maxHeight: .infinity)
        }
    }

    private var loading: Bool { conversation.hasTranscript && transcript == nil && !loadFailed }

    private var emptyText: String {
        if loadFailed { return "The transcript could not be read." }
        if transcript != nil { return "No speech was found in this recording." }
        switch conversation.status {
        case .recording: return "Recording in progress."
        case .compressing: return "Compressing the audio…"
        case .failed: return "The recording failed."
        default: return "No transcript yet."
        }
    }

    // MARK: - Actions

    /// Reads the transcript and resolves the audio file off the main thread; runs again when the conversation
    /// changes (e.g. a re-transcription finished). No playback while the audio is being recorded or compressed.
    private func load() async {
        let folder = conversation.folder
        let wantsTranscript = conversation.hasTranscript
        let playable = conversation.status != .recording && conversation.status != .compressing
        let (loaded, audio, hasAudio) = await Task.detached(priority: .utility) {
            let transcript = wantsTranscript ? try? Transcript.read(from: folder) : nil
            let audio = playable
                ? (try? RecordingSession.read(from: folder)).flatMap {
                    ConversationFiles.audioURL(in: folder, fileName: $0.audioFileName)
                }
                : nil
            return (transcript, audio, !LibraryStore.audioFiles(in: folder).isEmpty)
        }.value
        guard !Task.isCancelled else { return }
        self.hasAudio = hasAudio
        transcript = loaded
        speakers = loaded.map(Self.speakers) ?? []
        loadFailed = wantsTranscript && loaded == nil
        playback.load(url: audio, segments: loaded?.segments ?? [])
        showPendingTime()
    }

    /// Takes the time of a clicked AI citation (`AppModel.pendingTranscriptTime`) once the transcript is loaded:
    /// scrolls to the segment containing it (or the last one starting before it) and moves the play head there,
    /// inside that segment so it is highlighted, without playing. Cleared even without segments, so a later
    /// transcript does not jump to it.
    private func showPendingTime() {
        guard let time = model.pendingTranscriptTime, !loading else { return }
        model.pendingTranscriptTime = nil
        guard let segments = transcript?.segments,
              let segment = segments.last(where: { $0.start <= time }) ?? segments.first
        else { return }
        playback.seek(to: min(max(time, segment.start), segment.end))
        scrollTarget = segment.id
    }

    /// One "Name [mm:ss]: text" line per segment, with the renamed names.
    private func copyTranscript() {
        guard let transcript else { return }
        let text = transcript.segments
            .map { "\(transcript.displayName(for: $0.speaker)) [\(Self.timestamp($0.start))]: \($0.text)" }
            .joined(separator: "\n")
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    /// mm:ss; minutes go past 59 in recordings longer than an hour.
    static func timestamp(_ seconds: Double) -> String {
        Duration.seconds(seconds).formatted(.time(pattern: .minuteSecond(padMinuteToLength: 2)))
    }
}

/// Play head and length; the only reader of `currentTime`, so the 4 Hz updates redraw nothing else.
private struct PlaybackTime: View {
    let playback: PlaybackController

    var body: some View {
        Text("\(TranscriptView.timestamp(playback.currentTime)) / \(TranscriptView.timestamp(playback.duration))")
            .font(.caption.monospacedDigit())
            .foregroundStyle(.secondary)
    }
}

/// The segments; redrawn when the play head enters another segment, not at every tick.
private struct SegmentList: View {
    let transcript: Transcript
    let playback: PlaybackController
    @Binding var scrollTarget: UUID?

    var body: some View {
        let active = playback.activeSegmentID
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 2) {
                    ForEach(transcript.segments) { segment in
                        SegmentRow(
                            segment: segment, name: transcript.displayName(for: segment.speaker),
                            active: segment.id == active
                        ) {
                            playback.play(from: segment.start)
                        }
                    }
                }
                .padding(.vertical, 4)
            }
            .onChange(of: scrollTarget, initial: true) {
                guard let scrollTarget else { return }
                proxy.scrollTo(scrollTarget, anchor: .center)
                self.scrollTarget = nil
            }
        }
    }
}

/// Timestamp, speaker dot and name, selectable text. A click outside the text plays from the segment start.
private struct SegmentRow: View {
    let segment: Transcript.Segment
    let name: String
    let active: Bool
    let play: () -> Void

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Button(action: play) {
                Text(TranscriptView.timestamp(segment.start)).monospacedDigit()
            }
            .buttonStyle(.plain)
            .font(.caption)
            .foregroundStyle(.secondary)
            .help("Play from here")
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 5) {
                    Circle().fill(TranscriptView.color(for: segment.speaker)).frame(width: 7, height: 7)
                    Text(name).font(.caption.weight(.semibold))
                }
                Text(segment.text)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous).fill(active ? Palette.pressedFill : .clear))
        .contentShape(Rectangle())
        .onTapGesture(perform: play)
        .accessibilityAddTraits(active ? .isSelected : [])
    }
}
