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
    /// The newest transcript (`transcript.json`): rename writes it and AI citations point into it; the list, Copy,
    /// notices and chips show `shownTranscript`.
    @State private var transcript: Transcript?
    /// Kept transcripts, oldest first, the newest last; the version menu shows when there are two or more.
    @State private var versions: [Transcript.Version] = []
    /// The version picked in the version menu; nil is the newest. Older ones are view-only.
    @State private var shownVersion: Int?
    /// The audio file `playback` opened, to reload its segments when another version is shown.
    @State private var audioURL: URL?
    /// Speaker ids of `shownTranscript`: me first, then s1, s2…
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
            withAnimation(Motion.standard) { shownTab = new }
        }
        .onChange(of: model.pendingTranscriptTime) { showPendingTime() }
        .onChange(of: shownVersion) { applyShownVersion() }
        .onChange(of: model.settings.playbackChannels) { playback.setChannels(model.settings.playbackChannels) }
        // Not while a speaker name is edited: the text field takes Space.
        .onReceive(NotificationCenter.default.publisher(for: .togglePlayback)) { _ in
            guard playback.isReady, renaming == nil else { return }
            playback.togglePlay()
        }
        .onDisappear { playback.close() }
    }

    static let tabs: [AppModel.DetailTab] = [.transcript, .summary, .chat]

    /// Moving right, the new body comes in a little from the trailing side and the old one leaves toward the leading
    /// side; the other way round moving left. The old body fades out faster, so the two never show through each other.
    /// Only a fade under Reduce Motion.
    private var tabTransition: AnyTransition {
        if reduceMotion { return .opacity }
        let travel: CGFloat = slidesForward ? 24 : -24
        return .asymmetric(
            insertion: .offset(x: travel).combined(with: .opacity),
            removal: .offset(x: -travel).combined(with: .opacity).animation(.easeOut(duration: 0.1))
        )
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
            if shownTranscript?.notes.contains(Transcript.cloudDiarizationFailedNote) == true {
                cloudDiarizationFailedNotice
            } else if shownTranscript?.notes.contains(Transcript.diarizationMissingNote) == true {
                diarizationMissingNotice
            }
            if conversation.hasAudio { PlaybackScrubber(playback: playback) }
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
        let installed = model.diarizerAvailable
        return HStack(alignment: .firstTextBaseline, spacing: 8) {
            Group {
                Image(systemName: "person.2.slash")
                if installed {
                    Text("Speakers were not told apart. The speaker model is installed now: re-transcribe.")
                } else {
                    Text("Speakers were not told apart: the speaker model is not installed (Transcription › Diarization model).")
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

    /// The cloud diarizer failed, so a model on this Mac told the speakers apart, when one was installed.
    private var cloudDiarizationFailedNotice: some View {
        let localMissing = shownTranscript?.notes.contains(Transcript.diarizationMissingNote) == true
        return HStack(alignment: .firstTextBaseline, spacing: 8) {
            Group {
                Image(systemName: "exclamationmark.icloud")
                Text(localMissing
                     ? "Cloud diarization failed and no diarization model is installed on this Mac."
                     : "Cloud diarization failed; speakers were told apart on this Mac.")
            }
            .foregroundStyle(.secondary)
            if localMissing {
                Button("Download…") { model.openSettings(tab: .transcription) }
                    .buttonStyle(.link)
            }
        }
        .font(.caption)
    }

    /// Speaker chips, then the actions and the transport at the trailing edge.
    private func controlRow(chips: Int) -> some View {
        HStack(spacing: 8) {
            if shownTranscript != nil && !speakers.isEmpty {
                HStack(spacing: 6) {
                    ForEach(speakers.prefix(chips), id: \.self, content: speakerChip)
                    if chips < speakers.count { moreSpeakersChip(Array(speakers.dropFirst(chips))) }
                }
            }
            Spacer(minLength: 8)
            actions
            if conversation.hasAudio { transport } else { audioRemovedNote }
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
            .disabled(shownTranscript?.segments.isEmpty ?? true)
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
        .help(conversation.hasAudio ? "Re-transcribe with another model or language" : "Audio has been removed")
    }

    /// Not while a job runs, the audio is still being recorded or compressed, or it was deleted.
    private var canTranscribe: Bool {
        conversation.hasAudio && !model.isTranscribing(conversation.id) && conversation.status != .recording
            && conversation.status != .compressing && conversation.status != .failed
    }

    /// In place of the transport once the audio was moved to the Trash (from a Delete menu, Storage or auto-delete).
    private var audioRemovedNote: some View {
        Label("Audio has been removed", systemImage: "speaker.slash")
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize()
            .help("Playback and re-transcribing are no longer possible. The transcript, summary and chat stay.")
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
        // Older versions are view-only: a rename edits the newest.
        .disabled(model.isTranscribing(conversation.id) || shownVersion != nil)
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
            Text(shownTranscript?.displayName(for: speaker) ?? speaker)
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
        .disabled(model.isTranscribing(conversation.id) || shownVersion != nil)
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

    /// The segments in a card like Summary's and Chat's, or a note in its middle while there are none; the version
    /// menu is pinned above them, as in Summary.
    private var content: some View {
        VStack(alignment: .leading, spacing: 0) {
            if showsVersionMenu {
                versionMenu
                    .padding(.horizontal, 14)
                    .padding(.top, 16)
            }
            Group {
                if let shown = shownTranscript, !shown.segments.isEmpty {
                    SegmentList(transcript: shown, playback: playback, scrollTarget: $scrollTarget)
                } else if loading {
                    ProgressView()
                } else {
                    VStack(spacing: 10) {
                        Text(emptyText).foregroundStyle(.secondary)
                        if !conversation.hasTranscript {
                            TranscriptionFailureReason(conversation: conversation, model: model)
                                .multilineTextAlignment(.center)
                            TranscriptionStatus(conversation: conversation, model: model)
                        }
                    }
                    .padding(20)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .modifier(GlassCard(cornerRadius: 20))
    }

    /// The picked older version's transcript, else the newest.
    private var shownTranscript: Transcript? {
        versions.first { $0.number == shownVersion }?.transcript ?? transcript
    }

    private var showsVersionMenu: Bool { versions.count >= 2 && !model.isTranscribing(conversation.id) }

    /// The kept transcripts, newest first, each with its engine and date. Picking the newest sets `shownVersion` to
    /// nil, so it shows `transcript` with its renamed speakers.
    private var versionMenu: some View {
        let shown = shownVersion ?? versions.last?.number ?? 0
        return Menu {
            ForEach(versions.reversed(), id: \.number) { version in
                Toggle(isOn: Binding(
                    get: { version.number == shown },
                    set: { if $0 { shownVersion = version.number == versions.last?.number ? nil : version.number } }
                )) {
                    Text("Version \(version.number)")
                    Text(versionDetails(version.transcript))
                }
            }
        } label: {
            let engine = shownTranscript.flatMap { model.transcriptEngineName($0) }
            AIMenuLabel(title: ([String(localized: "Version \(shown)")] + [engine].compactMap { $0 })
                .joined(separator: " · "), systemImage: "clock.arrow.circlepath")
        }
        .modifier(CompactMenu(maxWidth: 260, height: Self.versionMenuHeight))
        .help("Earlier transcripts of this conversation")
    }

    /// As tall as the Summary tab's version menu.
    private static let versionMenuHeight: CGFloat = 28

    private func versionDetails(_ transcript: Transcript) -> String {
        [model.transcriptEngineName(transcript), transcript.createdAt.formatted(date: .abbreviated, time: .shortened)]
            .compactMap { $0 }
            .joined(separator: " · ")
    }

    private var loading: Bool { conversation.hasTranscript && transcript == nil && !loadFailed }

    private var emptyText: String {
        if loadFailed { return "The transcript could not be read." }
        if shownTranscript != nil { return "No speech was found in this recording." }
        switch conversation.status {
        case .recording: return "Recording in progress."
        case .compressing: return "Compressing the audio…"
        case .failed: return "The recording failed."
        default: return "No transcript yet."
        }
    }

    // MARK: - Actions

    /// Reads the transcript and resolves the audio file off the main thread; runs again when the conversation
    /// changes (e.g. a re-transcription finished). No playback while the audio is being recorded or compressed, or
    /// once it was deleted (which stops it).
    private func load() async {
        let folder = conversation.folder
        let wantsTranscript = conversation.hasTranscript
        let playable = conversation.hasAudio && conversation.status != .recording && conversation.status != .compressing
        let (loaded, audio) = await Task.detached(priority: .utility) {
            // The newest version is transcript.json itself.
            let versions = wantsTranscript ? Transcript.readVersions(from: folder) : []
            let audio = playable
                ? (try? RecordingSession.read(from: folder)).flatMap {
                    ConversationFiles.audioURL(in: folder, fileName: $0.audioFileName)
                }
                : nil
            return (versions, audio)
        }.value
        guard !Task.isCancelled else { return }
        versions = loaded
        transcript = loaded.last?.transcript
        shownVersion = nil
        audioURL = audio
        loadFailed = wantsTranscript && loaded.isEmpty
        applyShownVersion()
        showPendingTime()
    }

    /// The speakers and the playback highlight of `shownTranscript` (same audio, so only the segments change); closes
    /// a rename popover when an older, view-only version shows.
    private func applyShownVersion() {
        speakers = shownTranscript.map(Self.speakers) ?? []
        playback.load(url: audioURL, segments: shownTranscript?.segments ?? [],
                      channels: model.settings.playbackChannels)
        if shownVersion != nil {
            renaming = nil
            showingMoreSpeakers = false
        }
    }

    /// Takes the time of a clicked AI citation (`AppModel.pendingTranscriptTime`) once the transcript is loaded:
    /// scrolls to the segment containing it (or the last one starting before it) and moves the play head there,
    /// inside that segment so it is highlighted, without playing. Cleared even without segments, so a later
    /// transcript does not jump to it.
    private func showPendingTime() {
        guard let time = model.pendingTranscriptTime, !loading else { return }
        model.pendingTranscriptTime = nil
        // Citations point into the newest transcript; applied now, so the seek highlights its segment.
        if shownVersion != nil {
            shownVersion = nil
            applyShownVersion()
        }
        guard let segments = transcript?.segments,
              let segment = segments.last(where: { $0.start <= time }) ?? segments.first
        else { return }
        playback.seek(to: min(max(time, segment.start), segment.end))
        scrollTarget = segment.id
    }

    /// One "Name [mm:ss]: text" line per segment, with the renamed names.
    private func copyTranscript() {
        guard let transcript = shownTranscript else { return }
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

/// The play head on a track: a click or a drag moves it, and playback pauses during a drag and goes on after it.
/// Reads `currentTime` itself, like `PlaybackTime`, so the 4 Hz updates redraw nothing else.
private struct PlaybackScrubber: View {
    let playback: PlaybackController
    @State private var hovered = false
    @State private var dragging = false
    @State private var resumeAfterDrag = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private static let knobSize: CGFloat = 12

    var body: some View {
        let active = playback.isReady && (hovered || dragging)
        GeometryReader { geometry in
            let width = geometry.size.width
            let fraction = playback.duration > 0 ? min(max(playback.currentTime / playback.duration, 0), 1) : 0
            ZStack(alignment: .leading) {
                Capsule().fill(Palette.switchOffFill)
                Capsule().fill(Color.accentColor).frame(width: width * fraction)
            }
            .frame(height: active ? 6 : 4)
            .overlay(alignment: .leading) {
                Circle()
                    .fill(.white)
                    .frame(width: Self.knobSize, height: Self.knobSize)
                    .offset(x: width * fraction - Self.knobSize / 2)
                    .opacity(active ? 1 : 0)
            }
            .frame(maxHeight: .infinity)
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        guard playback.isReady, width > 0 else { return }
                        if !dragging {
                            dragging = true
                            resumeAfterDrag = playback.isPlaying
                            if resumeAfterDrag { playback.pause() }
                        }
                        playback.seek(to: min(max(value.location.x / width, 0), 1) * playback.duration)
                    }
                    .onEnded { _ in
                        guard dragging else { return }
                        dragging = false
                        if resumeAfterDrag { playback.play() }
                    }
            )
        }
        .frame(height: 14)
        .onHover { hovered = $0 }
        .pointerStyle(playback.isReady ? .link : nil)
        .animation(reduceMotion ? nil : .easeOut(duration: 0.15), value: active)
        .accessibilityRepresentation {
            Slider(
                value: Binding(get: { playback.currentTime }, set: { playback.seek(to: $0) }),
                in: 0...max(playback.duration, 0.01)
            ) {
                Text("Playback position")
            }
            .disabled(!playback.isReady)
        }
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
                            active: segment.id == active, speaking: segment.id == active && playback.isPlaying
                        ) {
                            following = true
                            playback.play(from: segment.start)
                        }
                    }
                }
                // With the rows' own padding, the text sits as far from the card's edges as the summary's.
                .padding(.horizontal, 4)
                .padding(.vertical, 10)
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

/// Timestamp, speaker dot and name, text. A click anywhere on the row, the text included, plays from the segment
/// start. The text is not selectable: selectable text shows an I-beam over the row's pointer.
private struct SegmentRow: View {
    let segment: Transcript.Segment
    let name: String
    let active: Bool
    let speaking: Bool
    let play: () -> Void
    @State private var hovered = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            VStack(spacing: 4) {
                Button(action: play) {
                    Text(TranscriptView.timestamp(segment.start)).monospacedDigit()
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .help("Play from here")
                // The segment being played back.
                if speaking {
                    Image(systemName: "waveform")
                        .foregroundStyle(TranscriptView.color(for: segment.speaker))
                        .symbolEffect(.variableColor.iterative, options: .repeating, isActive: !reduceMotion)
                        .accessibilityHidden(true)
                }
            }
            .font(.caption)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 5) {
                    Circle().fill(TranscriptView.color(for: segment.speaker)).frame(width: 7, height: 7)
                    Text(name).font(.caption.weight(.semibold))
                }
                Text(segment.text)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(active ? Palette.pressedFill : hovered ? Palette.hoverFill : .clear))
        .contentShape(Rectangle())
        .onHover { hovered = $0 }
        .pointerStyle(.link)
        .onTapGesture(perform: play)
        .accessibilityAddTraits(active ? .isSelected : [])
    }
}
