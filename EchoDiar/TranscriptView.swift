import AppKit
import EchoCore
import EchoTranscribe
import SwiftUI

/// One conversation's transcript inside the main window: title (double-click to rename), details, speakers
/// (click to rename), playback and actions, then the segments. A click on a segment plays from its start.
/// Esc goes back to the library, Space plays or pauses.
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

    var body: some View {
        VStack(spacing: 10) {
            header
            content
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background(shortcutButtons)
        .task(id: conversation) { await load() }
        .task { model.refreshInstalledModels() }
        .onDisappear { playback.close() }
    }

    // MARK: - Header

    private var header: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                Button(action: close) {
                    Label("Back", systemImage: "chevron.left")
                        .labelStyle(.iconOnly)
                        .font(.system(size: 15, weight: .semibold))
                }
                .buttonStyle(RowIconButtonStyle())
                .foregroundStyle(.secondary)
                .help("Back to the library (Esc)")
                VStack(alignment: .leading, spacing: 4) {
                    title
                    details
                }
                .layoutPriority(1)
                Spacer(minLength: 8)
                if conversation.hasTranscript && !failed {
                    TranscriptionStatus(conversation: conversation, model: model)
                }
                actions
            }
            // A failed re-transcription gets its own row, so the title and the icons keep their space.
            if conversation.hasTranscript && failed {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    TranscriptionFailureReason(conversation: conversation, model: model)
                    Spacer(minLength: 8)
                    TranscriptionStatus(conversation: conversation, model: model)
                }
            }
            HStack(spacing: 8) {
                if transcript != nil {
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 6) {
                            ForEach(speakers, id: \.self, content: speakerChip)
                        }
                    }
                }
                Spacer(minLength: 8)
                transport
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(GlassCard(cornerRadius: 20))
    }

    private var failed: Bool { TranscriptionStatus.showsFailure(conversation, model) }

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

    private var actions: some View {
        HStack(spacing: 14) {
            iconButton("Copy transcript", systemImage: "doc.on.doc", action: copyTranscript)
                .disabled(transcript?.segments.isEmpty ?? true)
            retranscribeMenu
            iconButton("AI", systemImage: "sparkles", action: showAI)
                .help("Ask AI about this conversation")
            iconButton("Reveal in Finder", systemImage: "folder") {
                NSWorkspace.shared.activateFileViewerSelecting([conversation.folder])
            }
        }
    }

    private func iconButton(_ title: String, systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .labelStyle(.iconOnly)
                .font(.system(size: 15))
        }
        .buttonStyle(RowIconButtonStyle())
        .foregroundStyle(.secondary)
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
                .font(.system(size: 15))
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .foregroundStyle(.secondary)
        .disabled(!canTranscribe)
        .help("Re-transcribe with another model or language")
    }

    /// Not while a job runs or the audio is still being recorded or compressed.
    private var canTranscribe: Bool {
        !model.isTranscribing(conversation.id) && conversation.status != .recording
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
            SegmentList(transcript: transcript, playback: playback)
        } else if conversation.hasTranscript && transcript == nil && !loadFailed {
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
        let (loaded, audio) = await Task.detached(priority: .utility) {
            let transcript = wantsTranscript ? try? Transcript.read(from: folder) : nil
            let audio = playable
                ? (try? RecordingSession.read(from: folder)).flatMap {
                    ConversationFiles.audioURL(in: folder, fileName: $0.audioFileName)
                }
                : nil
            return (transcript, audio)
        }.value
        guard !Task.isCancelled else { return }
        transcript = loaded
        speakers = loaded.map(Self.speakers) ?? []
        loadFailed = wantsTranscript && loaded == nil
        playback.load(url: audio, segments: loaded?.segments ?? [])
    }

    private func close() {
        withAnimation(.snappy) { model.openedConversationID = nil }
    }

    private func showAI() {
        model.selectedConversationID = conversation.id
        withAnimation(.snappy) { model.selectedTab = .ai }
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

    /// Hidden buttons for Esc (back) and Space (play or pause), off while a title or speaker name is edited so
    /// the text field gets those keys.
    private var shortcutButtons: some View {
        ZStack {
            Button("Back", action: close)
                .keyboardShortcut(.escape, modifiers: [])
            Button("Play or pause", action: playback.togglePlay)
                .keyboardShortcut(.space, modifiers: [])
                .disabled(!playback.isReady)
        }
        .disabled(editingTitle || renaming != nil)
        .frame(width: 0, height: 0)
        .opacity(0)
        .accessibilityHidden(true)
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

    var body: some View {
        let active = playback.activeSegmentID
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
