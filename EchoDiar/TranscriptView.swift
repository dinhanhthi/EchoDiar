import AppKit
import EchoCore
import EchoTranscribe
import SwiftUI

extension Notification.Name {
    /// Space in the main window on a conversation's Transcript tab (posted by the app delegate's key monitor).
    static let togglePlayback = Notification.Name("EchoDiar.togglePlayback")
}

/// One conversation inside the main window (its title, date, duration and size, its tabs and Reveal in Finder are in
/// the toolbar). The Transcript tab ends with a bar of speakers (click to rename), actions and playback. A click on a
/// segment plays from its start; Space plays or pauses. Stays mounted across tabs, so playback goes on in Summary and
/// Chat.
struct TranscriptView: View {
    let conversation: Conversation
    let model: AppModel
    @State private var transcript: Transcript?
    /// Speaker ids of `transcript`: me first, then s1, s2…
    @State private var speakers: [String] = []
    /// `transcript.json` exists but could not be read.
    @State private var loadFailed = false
    @State private var playback = PlaybackController()
    /// The speaker whose rename popover is open.
    @State private var renaming: String?
    @State private var nameDraft = ""
    /// The "+N" popover listing the speakers without a chip is open.
    @State private var showingMoreSpeakers = false
    /// Language for the next Re-transcribe; "" keeps the recording's language markers.
    @State private var retranscribeLanguage = ""
    /// The segment the list scrolls to, set by an AI citation; cleared once scrolled.
    @State private var scrollTarget: UUID?
    /// The folder still has audio: false once Storage or auto-delete moved it to the Trash (no Play, no Re-transcribe).
    @State private var hasAudio = true
    /// The tab whose body is shown: follows `model.detailTab` one update later, so `forward` is set before the bodies
    /// swap and both the outgoing and the incoming body slide the same way.
    @State private var shownTab: AppModel.DetailTab
    /// The last tab switch went right (Transcript → Summary → Chat).
    @State private var forward = true
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(conversation: Conversation, model: AppModel) {
        self.conversation = conversation
        self.model = model
        _shownTab = State(initialValue: model.detailTab)
    }

    var body: some View {
        ZStack(alignment: .top) {
            if shownTab == .transcript {
                // The bar slides with the transcript: it has nothing for Summary and Chat.
                VStack(spacing: 10) {
                    content
                    bottomBar
                }
                .transition(tabTransition)
            } else {
                AIPanelView(conversation: conversation, model: model, tab: shownTab, transition: tabTransition)
                    .transition(tabTransition)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .clipped()
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .task(id: conversation) { await load() }
        .task { model.refreshInstalledModels() }
        .onChange(of: model.detailTab) { old, new in
            forward = (Self.tabs.firstIndex(of: new) ?? 0) > (Self.tabs.firstIndex(of: old) ?? 0)
            // Also under Reduce Motion: `tabTransition` is then a fade.
            withAnimation(.snappy(duration: 0.25)) { shownTab = new }
        }
        .onChange(of: model.pendingTranscriptTime) { showPendingTime() }
        // Not while a speaker name is edited: the text field takes Space.
        .onReceive(NotificationCenter.default.publisher(for: .togglePlayback)) { _ in
            guard playback.isReady, renaming == nil else { return }
            playback.togglePlay()
        }
        .onDisappear { playback.close() }
    }

    static let tabs: [AppModel.DetailTab] = [.transcript, .summary, .chat]

    /// Moving right, the new body comes in from the trailing edge and the old one leaves by the leading edge; the
    /// other way round moving left. Only a fade under Reduce Motion.
    private var tabTransition: AnyTransition {
        if reduceMotion { return .opacity }
        return .asymmetric(
            insertion: .move(edge: slidesForward ? .trailing : .leading),
            removal: .move(edge: slidesForward ? .leading : .trailing)
        )
        .combined(with: .opacity)
    }

    /// The outgoing body keeps the transition of its last update, the one where `model.detailTab` has changed but
    /// `shownTab` not yet: take the direction from that pending change, else from `forward`.
    private var slidesForward: Bool {
        guard model.detailTab != shownTab else { return forward }
        return (Self.tabs.firstIndex(of: model.detailTab) ?? 0) > (Self.tabs.firstIndex(of: shownTab) ?? 0)
    }

    static func tabTitle(_ tab: AppModel.DetailTab) -> LocalizedStringKey {
        switch tab {
        case .transcript: "Transcript"
        case .summary: "Summary"
        case .chat: "Chat"
        }
    }

    // MARK: - Bottom bar

    /// Like the Summary and Chat tabs' bars: the transcription's state, then the speakers, actions and playback.
    private var bottomBar: some View {
        VStack(alignment: .leading, spacing: 10) {
            // Not wrapped in a row: its body is often empty, which then takes no space. A running job shows on
            // Re-transcribe instead.
            if conversation.hasTranscript && !failed && !model.isTranscribing(conversation.id) {
                TranscriptionStatus(conversation: conversation, model: model)
            }
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
            // Up to three speaker chips, fewer when the row is short; "+N" lists the others.
            ViewThatFits(in: .horizontal) {
                ForEach(chipCounts, id: \.self) { controlRow(chips: $0) }
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

    /// Speaker chips, then the actions and the transport at the trailing edge.
    private func controlRow(chips: Int) -> some View {
        HStack(spacing: 8) {
            if transcript != nil && !speakers.isEmpty {
                HStack(spacing: 6) {
                    ForEach(speakers.prefix(chips), id: \.self, content: speakerChip)
                    if chips < speakers.count { moreSpeakersChip(Array(speakers.dropFirst(chips))) }
                }
            }
            Spacer(minLength: 8)
            actions
            if hasAudio { transport }
        }
    }

    /// The chip counts `controlRow` tries, most first: 3, 2, 1, 0 (fewer when there are fewer speakers).
    private var chipCounts: [Int] {
        Array((0...min(Self.maxSpeakerChips, speakers.count)).reversed())
    }

    private static let maxSpeakerChips = 3

    /// Copy (round) and Re-transcribe, in the accent color like Send; a spinner and "Transcribing…" in its place while a
    /// job is queued or runs.
    private var actions: some View {
        HStack(spacing: 6) {
            Button(action: copyTranscript) {
                Label("Copy transcript", systemImage: "square.on.square")
                    .labelStyle(.iconOnly)
            }
            .buttonStyle(BottomBarButtonStyle(horizontalPadding: 0))
            .help("Copy transcript")
            .disabled(transcript?.segments.isEmpty ?? true)
            if model.isTranscribing(conversation.id) {
                Button {} label: {
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.small)
                        Text("Transcribing…")
                    }
                }
                .buttonStyle(BottomBarButtonStyle(prominent: true))
                .allowsHitTesting(false)
                .fixedSize()
            } else {
                retranscribeMenu
            }
        }
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
            HStack(spacing: 6) {
                Text("Re-transcribe")
                MenuChevron()
            }
        }
        .menuStyle(.button)
        .buttonStyle(BottomBarButtonStyle(prominent: true))
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
            }
            .buttonStyle(BottomBarButtonStyle(horizontalPadding: 0))
            .disabled(!playback.isReady)
            .help(playback.isPlaying ? "Pause (Space)" : "Play (Space)")
            PlaybackTime(playback: playback)
        }
        .fixedSize()
    }

    // MARK: - Speakers

    private func speakerChip(_ speaker: String) -> some View {
        Button {
            nameDraft = transcript?.speakerNames[speaker] ?? ""
            renaming = speaker
        } label: {
            speakerLabel(speaker)
                .modifier(SpeakerChipShape())
        }
        .buttonStyle(.plain)
        // Its own width: the row tries fewer chips instead of truncating names.
        .fixedSize()
        .disabled(model.isTranscribing(conversation.id))
        .help("Rename this speaker")
        .popover(isPresented: Binding(
            get: { renaming == speaker && !showingMoreSpeakers },
            set: { if !$0 { renaming = nil } }
        )) {
            renameForm(speaker)
        }
    }

    private func speakerLabel(_ speaker: String) -> some View {
        HStack(spacing: 5) {
            Circle().fill(Self.color(for: speaker)).frame(width: 7, height: 7)
            Text(transcript?.displayName(for: speaker) ?? speaker)
        }
    }

    /// The speakers without a chip; its popover lists them, and a click on one renames it in the same popover.
    private func moreSpeakersChip(_ hidden: [String]) -> some View {
        Button {
            renaming = nil
            showingMoreSpeakers = true
        } label: {
            Text(verbatim: "+\(hidden.count)")
                .modifier(SpeakerChipShape())
        }
        .buttonStyle(.plain)
        .fixedSize()
        .disabled(model.isTranscribing(conversation.id))
        .help("Show the other speakers")
        .popover(isPresented: Binding(
            get: { showingMoreSpeakers },
            set: {
                if !$0 {
                    showingMoreSpeakers = false
                    renaming = nil
                }
            }
        )) {
            if let renaming, hidden.contains(renaming) {
                renameForm(renaming)
            } else {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(hidden, id: \.self) { speaker in
                        Button {
                            nameDraft = transcript?.speakerNames[speaker] ?? ""
                            renaming = speaker
                        } label: {
                            speakerLabel(speaker)
                                .font(.callout)
                                .padding(.horizontal, 8)
                                .padding(.vertical, 4)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .help("Rename this speaker")
                    }
                }
                .padding(6)
                .frame(minWidth: 180)
            }
        }
    }

    /// The name field of a speaker: Return saves, Esc or a click elsewhere cancels.
    private func renameForm(_ speaker: String) -> some View {
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

    /// Saves the name into `transcript.json` (read fresh, only this name changes); a blank name restores the default.
    private func rename(_ speaker: String, to name: String) {
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard var updated = transcript, updated.speakerNames[speaker] != (name.isEmpty ? nil : name) else { return }
        updated.speakerNames[speaker] = name.isEmpty ? nil : name
        transcript = updated
        let folder = conversation.folder
        let id = conversation.id
        let model = model
        Task.detached(priority: .utility) {
            do {
                var stored = try Transcript.read(from: folder)
                stored.speakerNames[speaker] = name.isEmpty ? nil : name
                try stored.write(to: folder)
                await model.transcriptEdited(id)
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

/// A speaker chip's capsule, as tall as the bar's buttons.
private struct SpeakerChipShape: ViewModifier {
    func body(content: Content) -> some View {
        content
            .font(.callout.weight(.medium))
            .padding(.horizontal, 12)
            .frame(height: BottomBarButtonStyle.height)
            .background(Palette.hoverFill, in: Capsule())
            .contentShape(Capsule())
    }
}

/// The segments; redrawn when the play head enters another segment, not at every tick. While playing, the list
/// follows the play head: Play scrolls to its segment, and so does each next segment, until the user scrolls away
/// (or jumps to a search result); the next Play, or a click on a segment, follows again.
private struct SegmentList: View {
    let transcript: Transcript
    let playback: PlaybackController
    @Binding var scrollTarget: UUID?
    @State private var following = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

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
                            following = true
                            playback.play(from: segment.start)
                        }
                    }
                }
                .padding(.vertical, 4)
            }
            // The user's own scrolling (wheel, trackpad, scroller); `.animating` is `follow`'s scrollTo.
            .onScrollPhaseChange { _, phase in
                if phase != .idle && phase != .animating { following = false }
            }
            .onChange(of: scrollTarget, initial: true) {
                guard let scrollTarget else { return }
                following = false
                proxy.scrollTo(scrollTarget, anchor: .center)
                self.scrollTarget = nil
            }
            .onChange(of: active) { follow(active, proxy) }
            .onChange(of: playback.isPlaying) {
                following = playback.isPlaying
                follow(active, proxy)
            }
        }
    }

    private func follow(_ segment: UUID?, _ proxy: ScrollViewProxy) {
        guard playback.isPlaying, following, let segment else { return }
        withAnimation(reduceMotion ? nil : .easeInOut(duration: 0.25)) {
            proxy.scrollTo(segment, anchor: .center)
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
