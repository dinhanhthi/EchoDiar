import AppKit
import EchoAI
import EchoCore
import EchoTranscribe
import SwiftUI

/// One conversation of the library's sidebar: source app icon, title (double-click to rename), time, duration, size
/// and status, then a row with the transcription progress or its action. The actions are in the context menu and the
/// detail. A click elsewhere opens the conversation on the detail's tab; selected adds an accent fill and stroke.
/// ⌘-click and ⇧-click select cards instead of opening.
struct ConversationCard: View {
    let conversation: Conversation
    let model: AppModel
    var selected = false
    /// ⌘-click toggles the card, ⇧-click (`extend`) adds the span from the anchor.
    var select: ((_ extend: Bool) -> Void)? = nil
    @State private var editing = false
    @State private var draft = ""
    @FocusState private var titleFocused: Bool
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let activity = CardActivity(conversation: conversation, model: model)
        HStack(spacing: 12) {
            AppIcon(bundleID: conversation.sourceBundleID)
                // An overlay, so the badge never moves the row; display only, the card takes the click.
                .overlay(alignment: .bottomTrailing) {
                    if let activity {
                        ActivityBadge(
                            activity: activity,
                            blinks: activity != .queued && !reduceMotion && model.mainWindowVisible)
                            .offset(x: 4, y: 4)
                            .allowsHitTesting(false)
                    }
                }
            VStack(alignment: .leading, spacing: 4) {
                title
                details
                TranscriptionFailureReason(conversation: conversation, model: model)
                // The status has its own row under the title and details, from the same leading edge; often empty.
                HStack(spacing: 12) {
                    TranscriptionStatus(conversation: conversation, model: model)
                    if let job = CardActivity.aiJob(conversation.id, model) { AIJobLabel(activity: job) }
                    Spacer(minLength: 0)
                }
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        // The fill sits under the content, on the surface; the stroke on top.
        .background {
            if selected { Self.shape.fill(Color.accentColor.opacity(0.18)) }
        }
        .modifier(GlassCard(cornerRadius: 20, raised: true))
        .overlay {
            if selected { Self.shape.strokeBorder(Color.accentColor, lineWidth: 1.5).allowsHitTesting(false) }
        }
        // Buttons take their own clicks; the title's double click is handled before this.
        .contentShape(Rectangle())
        .onTapGesture(perform: open)
        // Before the title's taps: the modifiers are read when the click happens.
        .highPriorityGesture(TapGesture().modifiers(.command).onEnded { select?(false) },
                             including: select == nil ? .subviews : .all)
        .highPriorityGesture(TapGesture().modifiers(.shift).onEnded { select?(true) },
                             including: select == nil ? .subviews : .all)
        .contextMenu {
            Button("Rename", action: beginEditing)
            Button("Reveal in Finder", action: revealInFinder)
            Divider()
            DeleteMenuItems(conversation: conversation, model: model)
                .disabled(!DeleteMenuItems.canDelete(conversation, model))
        }
        .onChange(of: editing) {
            if editing { model.renamingConversation = conversation.id } else { endRenamingFlag() }
        }
        // Leaving the page or closing the window mid-rename never turns `editing` off.
        .onDisappear(perform: endRenamingFlag)
    }

    /// The `GlassCard` shape, for the selection.
    private static let shape = RoundedRectangle(cornerRadius: 20, style: .continuous)

    @ViewBuilder private var title: some View {
        if editing {
            TextField("Title", text: $draft)
                .textFieldStyle(.plain)
                .font(.headline)
                .focused($titleFocused)
                .onAppear { titleFocused = true }
                .onSubmit {
                    editing = false
                    if draft != conversation.title { model.renameConversation(conversation.id, to: draft) }
                }
                .onExitCommand { editing = false }
                // Clicking elsewhere cancels, like Esc; only Return saves.
                .onChange(of: titleFocused) { _, focused in if !focused { editing = false } }
        } else {
            Text(conversation.title)
                .font(.headline)
                .lineLimit(1)
                .truncationMode(.tail)
                // Side by side, so the first click opens at once instead of waiting out the double click, which then
                // renames the opened conversation.
                .onTapGesture(perform: open)
                .simultaneousGesture(TapGesture(count: 2).onEnded(beginEditing))
                .help(conversation.title)
        }
    }

    private var details: some View {
        HStack(spacing: 8) {
            Text(conversation.createdAt.formatted(date: .abbreviated, time: .shortened))
            if conversation.duration > 0 {
                Text(Self.durationText(conversation.duration)).monospacedDigit()
            }
            if conversation.sizeBytes > 0 {
                Text(ByteCountFormatter.string(fromByteCount: conversation.sizeBytes, countStyle: .file))
            }
            if let badge = StatusBadge(status: conversation.status) { badge }
            // A failed recording may never have had audio: its badge says so.
            if !conversation.hasAudio && conversation.status != .recording && conversation.status != .failed {
                Image(systemName: "speaker.slash")
                    .help("Audio has been removed")
                    .accessibilityLabel("Audio has been removed")
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .lineLimit(1)
    }

    /// m:ss, or h:mm:ss from one hour.
    static func durationText(_ seconds: Double) -> String {
        let pattern: Duration.TimeFormatStyle.Pattern = seconds >= 3_600 ? .hourMinuteSecond : .minuteSecond
        return Duration.seconds(seconds).formatted(.time(pattern: pattern))
    }

    private func beginEditing() {
        draft = conversation.title
        editing = true
    }

    /// Only if it is still this card's: another card's rename may have started since.
    private func endRenamingFlag() {
        if model.renamingConversation == conversation.id { model.renamingConversation = nil }
    }

    /// Not while the title is being edited: clicks in its field must not open the conversation.
    /// On the tab the detail shows.
    private func open() {
        guard !editing else { return }
        model.open(conversation.id, tab: model.detailTab)
    }

    private func revealInFinder() {
        NSWorkspace.shared.activateFileViewerSelecting([conversation.folder])
    }

}

/// Delete's choices: the audio only (most of the size, often useless once transcribed) or the whole conversation,
/// each asked first unless "Delete conversations without confirmation" is on. In the card's context menu, the
/// conversation's toolbar and Storage's largest conversations; the list page's selection bar uses the bulk
/// `delete`.
struct DeleteMenuItems: View {
    let conversation: Conversation
    let model: AppModel

    /// Not while it is recorded, compressed or transcribed.
    static func canDelete(_ conversation: Conversation, _ model: AppModel) -> Bool {
        conversation.status != .recording && conversation.status != .compressing
            && !model.isTranscribing(conversation.id)
    }

    var body: some View {
        let asks = !model.settings.deleteWithoutConfirmation
        if conversation.hasAudio {
            Button(asks ? "Delete audio only…" : "Delete audio only") { Self.delete(audioOnly: true, conversation, model) }
        } else {
            Button("Audio has been removed") {}.disabled(true)
        }
        Button(asks ? "Delete conversation…" : "Delete conversation") {
            Self.delete(audioOnly: false, conversation, model)
        }
    }

    /// Whether it was deleted (not cancelled).
    @discardableResult
    static func delete(audioOnly: Bool, _ conversation: Conversation, _ model: AppModel) -> Bool {
        if !model.settings.deleteWithoutConfirmation {
            let alert = NSAlert()
            if audioOnly {
                alert.messageText = String(localized: "Delete the audio of \"\(conversation.title)\"?")
                alert.informativeText = String(localized: "The audio is moved to the Trash. The transcript, summary and chat stay; playback and re-transcribing are no longer possible.")
            } else {
                alert.messageText = String(localized: "Delete \"\(conversation.title)\"?")
                alert.informativeText = String(localized: "The recording and its files are moved to the Trash.")
            }
            alert.addButton(withTitle: String(localized: "Delete")).hasDestructiveAction = true
            alert.addButton(withTitle: String(localized: "Cancel"))
            guard alert.runModal() == .alertFirstButtonReturn else { return false }
        }
        if audioOnly {
            Task { await model.deleteAudio(of: conversation.id) }
        } else {
            model.deleteConversation(conversation.id)
        }
        return true
    }

    /// The list page's bulk delete: only the conversations that can be deleted now (and have audio, for the audio
    /// only), asked once for all; one is asked by its title. Whether any was deleted (not cancelled, not none).
    @discardableResult
    static func delete(audioOnly: Bool, _ conversations: [Conversation], _ model: AppModel) -> Bool {
        let eligible = conversations.filter { canDelete($0, model) && (!audioOnly || $0.hasAudio) }
        if eligible.count <= 1 { return eligible.first.map { delete(audioOnly: audioOnly, $0, model) } ?? false }
        if !model.settings.deleteWithoutConfirmation {
            let alert = NSAlert()
            if audioOnly {
                alert.messageText = String(localized: "Delete the audio of \(eligible.count) conversations?")
                alert.informativeText = String(localized: "The audio is moved to the Trash. The transcript, summary and chat stay; playback and re-transcribing are no longer possible.")
            } else {
                alert.messageText = String(localized: "Delete \(eligible.count) conversations?")
                alert.informativeText = String(localized: "The recordings and their files are moved to the Trash.")
            }
            alert.addButton(withTitle: String(localized: "Delete")).hasDestructiveAction = true
            alert.addButton(withTitle: String(localized: "Cancel"))
            guard alert.runModal() == .alertFirstButtonReturn else { return false }
        }
        let ids = eligible.map(\.id)
        if audioOnly {
            // One by one, like the single delete: the list form skips conversations with an AI job.
            Task { for id in ids { await model.deleteAudio(of: id) } }
        } else {
            for id in ids { model.deleteConversation(id) }
        }
        return true
    }
}

/// A conversation's transcription progress, or its action: Needs model, Retry (with a "Retry with another engine"
/// menu; the audio is kept; the reason is `TranscriptionFailureReason`), or Transcribe. Shared by the card and the
/// transcript header.
struct TranscriptionStatus: View {
    let conversation: Conversation
    let model: AppModel

    /// The failed transcription state: Retry and its engine menu, with the reason shown apart.
    static func showsFailure(_ conversation: Conversation, _ model: AppModel) -> Bool {
        model.transcriptionProgress[conversation.id] == nil && conversation.status == .transcriptionFailed
    }

    /// Still in the Stop sequence (`finishRecording`) with no job yet, and an automatic transcription about to run:
    /// not compressing (the badge says so), nor a failed recording (never transcribed), nor transcribed already.
    static func isAboutToTranscribe(_ conversation: Conversation, _ model: AppModel) -> Bool {
        model.transcriptionProgress[conversation.id] == nil && model.isTranscribing(conversation.id)
            && model.settings.autoTranscribe && conversation.status != .compressing && conversation.status != .failed
            && !conversation.hasTranscript
    }

    /// Progress first: a queued job's conversation is still `ready`. A `transcribing` status without a job was
    /// left by a quit or crash, so it offers Transcribe again. A failed recording (`failed`) offers nothing.
    var body: some View {
        let progress = model.transcriptionProgress[conversation.id]
        if let progress, progress.step != .needsModel {
            ProgressLabel(progress: progress, conversationID: conversation.id, model: model)
        } else if progress != nil {
            Button("Needs model") { model.openSettings(tab: .providers, subTab: "localAI") }
                .buttonStyle(RowTextButtonStyle())
                .fixedSize()
                .foregroundStyle(.orange)
                .help("Download a transcription model in Settings")
        } else if Self.showsFailure(conversation, model) {
            HStack(spacing: 2) {
                Button("Retry") { model.transcribe(conversation.id) }
                    .buttonStyle(RowTextButtonStyle())
                    .foregroundStyle(.red)
                    .lineLimit(1)
                    .disabled(!conversation.hasAudio)
                    .help(conversation.hasAudio
                          ? "Transcription failed — transcribe this conversation again" : "Audio has been removed")
                Menu {
                    EngineMenuItems(title: "Retry with another engine", model: model) {
                        model.transcribe(conversation.id, engineID: $0)
                    }
                } label: {
                    Label("Retry with another engine", systemImage: "chevron.down")
                        .labelStyle(.iconOnly)
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
                .foregroundStyle(.secondary)
                .disabled(!conversation.hasAudio)
                .help(conversation.hasAudio ? "Retry with another engine" : "Audio has been removed")
            }
            .fixedSize()
        } else if Self.isAboutToTranscribe(conversation, model) {
            // Still in the Stop sequence (`finishRecording`) before its job reports progress.
            HStack(spacing: 6) {
                ProgressView().controlSize(.mini)
                Text("Transcribing…")
            }
            .lineLimit(1)
            .fixedSize()
            .font(.caption)
            .foregroundStyle(.secondary)
        } else if conversation.status == .ready || conversation.status == .transcribing, !conversation.hasTranscript {
            Button("Transcribe") { model.transcribe(conversation.id) }
                .buttonStyle(RowTextButtonStyle())
                .fixedSize()
                .foregroundStyle(.secondary)
                .disabled(!conversation.hasAudio)
                .help(conversation.hasAudio ? "Transcribe this conversation" : "Audio has been removed")
        }
    }

    /// A job's ring (or spinner), step and percent. The only reader of `transcriptionFractions`, so a new percent
    /// redraws this label alone, not every card.
    private struct ProgressLabel: View {
        let progress: JobProgress
        let conversationID: UUID
        let model: AppModel

        var body: some View {
            let fraction = model.transcriptionFractions[conversationID] ?? progress.fraction
            HStack(spacing: 6) {
                if progress.step == .queued || progress.step == .deferred {
                    ProgressView().controlSize(.mini)
                } else {
                    ProgressRing(fraction: fraction)
                }
                Text(TranscriptionStatus.stepText(progress.step))
            }
            .lineLimit(1)
            .fixedSize()
            .font(.caption)
            .foregroundStyle(.secondary)
            .help(progress.step == .deferred
                  ? String(localized: "Waiting while the Mac is hot or in Low Power Mode")
                  : fraction.formatted(.percent.precision(.fractionLength(0))))
        }
    }

    private static func stepText(_ step: JobProgress.Step) -> String {
        switch step {
        case .queued: String(localized: "Queued")
        case .deferred: String(localized: "Waiting…")
        case .extracting: String(localized: "Preparing audio…")
        case .transcribingMic, .transcribingSystem: String(localized: "Transcribing…")
        case .diarizing: String(localized: "Identifying speakers…")
        case .merging, .finished: String(localized: "Finishing…")
        case .needsModel, .failed: ""
        }
    }
}

/// Why the last transcription failed, in red under the details, up to two lines (the full text on hover); only in
/// the failed state. The reason is kept in memory, so after a relaunch it reads "Transcription failed".
struct TranscriptionFailureReason: View {
    let conversation: Conversation
    let model: AppModel

    var body: some View {
        if TranscriptionStatus.showsFailure(conversation, model) {
            let reason = model.transcriptionErrors[conversation.id] ?? String(localized: "Transcription failed")
            Text(reason)
                .font(.caption)
                .foregroundStyle(.red)
                .lineLimit(2)
                .truncationMode(.tail)
                .fixedSize(horizontal: false, vertical: true)
                .help(reason)
        }
    }
}

/// The default engine (`AppModel.defaultASRModelID`, when installed or configured) first, marked "(default)", then the
/// other installed local engines, then the other configured cloud engines under "Cloud", for the Retry and
/// Re-transcribe menus; "Download a model…" when there are none. `pick` gets a catalog or cloud engine id.
struct EngineMenuItems: View {
    let title: LocalizedStringKey
    let model: AppModel
    let pick: (String) -> Void

    var body: some View {
        let local = model.installedBatchEngines
        let cloud = model.cloudEngineConfigs
        if local.isEmpty && cloud.isEmpty {
            Button("Download a model…") { model.openSettings(tab: .providers, subTab: "localAI") }
        } else {
            let defaultID = model.defaultASRModelID
            let defaultCloud = cloud.first { Self.engineID($0) == defaultID }
            let otherCloud = cloud.filter { Self.engineID($0) != defaultID }
            let localFirst = local.filter { $0.id == defaultID } + local.filter { $0.id != defaultID }
            if defaultCloud != nil || !local.isEmpty {
                Section(title) {
                    if let defaultCloud {
                        item(model.cloudEngineName(defaultCloud), id: Self.engineID(defaultCloud), isDefault: true)
                    }
                    ForEach(localFirst) { entry in
                        item(entry.displayName, id: entry.id, isDefault: entry.id == defaultID)
                    }
                }
            }
            if !otherCloud.isEmpty {
                Section(defaultCloud == nil && local.isEmpty ? "\(Text(title)) (cloud)" : "Cloud") {
                    ForEach(otherCloud) { config in
                        item(model.cloudEngineName(config), id: Self.engineID(config), isDefault: false)
                    }
                }
            }
        }
    }

    private static func engineID(_ config: ProviderConfig) -> String {
        CloudTranscription.engineID(providerID: config.id)
    }

    private func item(_ name: String, id: String, isDefault: Bool) -> some View {
        Button(isDefault ? String(localized: "\(name) (default)") : name) { pick(id) }
    }
}

/// A short colored label for the statuses that need attention; nil for `ready`, `transcribed` and
/// `transcriptionFailed` (which shows its reason and Retry).
struct StatusBadge: View {
    let text: String
    let color: Color

    init?(status: Conversation.Status) {
        switch status {
        case .recording: (text, color) = (String(localized: "Recording"), .red)
        case .recovered: (text, color) = (String(localized: "Recovered"), .orange)
        case .compressing: (text, color) = (String(localized: "Compressing…"), .blue)
        case .compressionFailed: (text, color) = (String(localized: "Compression failed"), .red)
        case .failed: (text, color) = (String(localized: "Failed"), .red)
        default: return nil
        }
    }

    var body: some View {
        Text(text)
            .font(.caption2.weight(.semibold))
            .foregroundStyle(color)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(color.opacity(0.18), in: Capsule())
    }
}

/// What a card shows it is busy with, most important first: a transcription (queued, running or in the Stop
/// sequence), then its AI job: "Queued" behind another in-app job, else a summary or a question. Read from
/// `aiJobs` only, never `aiJobText`, so streamed text does not redraw the cards.
private enum CardActivity {
    case transcribing, summarizing, answering, queued

    @MainActor init?(conversation: Conversation, model: AppModel) {
        let id = conversation.id
        if let step = model.transcriptionProgress[id]?.step, step != .needsModel {
            self = .transcribing
        } else if TranscriptionStatus.isAboutToTranscribe(conversation, model) {
            self = .transcribing
        } else if let job = Self.aiJob(id, model) {
            self = job
        } else {
            return nil
        }
    }

    /// The conversation's summary or question job; nil without one.
    @MainActor static func aiJob(_ id: UUID, _ model: AppModel) -> CardActivity? {
        guard let entry = model.aiJobs.entry(for: id) else { return nil }
        if model.aiJobs.isQueued(id) { return .queued }
        switch entry.kind {
        case .summary: return .summarizing
        case .question: return .answering
        case .libraryQuestion, .title: return nil
        }
    }

    var text: String {
        switch self {
        case .transcribing: String(localized: "Transcribing…")
        case .summarizing: String(localized: "Summarizing…")
        case .answering: String(localized: "Answering…")
        case .queued: String(localized: "Queued")
        }
    }

    var symbol: String {
        switch self {
        case .transcribing: "waveform"
        case .summarizing: "sparkles"
        case .answering: "ellipsis.bubble"
        case .queued: "clock"
        }
    }

    var color: Color {
        switch self {
        case .transcribing: .blue
        case .summarizing: .purple
        case .answering: .teal
        case .queued: .gray
        }
    }
}

/// The card's activity on the app icon's corner: a 14 pt tinted circle like `StatusBadge`'s capsule, on the card's
/// surface so the icon does not show through. Its symbol blinks in 1 s steps (`RecordingBlink`), never a continuous
/// animation over the glass.
private struct ActivityBadge: View {
    let activity: CardActivity
    let blinks: Bool

    var body: some View {
        Image(systemName: activity.symbol)
            .font(.system(size: 8, weight: .semibold))
            .foregroundStyle(activity.color)
            .modifier(RecordingBlink(isActive: blinks))
            .frame(width: 14, height: 14)
            .background(Circle().fill(activity.color.opacity(0.18)))
            .background(Circle().fill(Palette.surface))
            .accessibilityElement()
            .accessibilityLabel(activity.text)
    }
}

/// The AI job's state in the card's status row, beside the transcription's: a mini spinner and "Summarizing…",
/// "Answering…" or "Queued".
private struct AIJobLabel: View {
    let activity: CardActivity

    var body: some View {
        HStack(spacing: 6) {
            ProgressView().controlSize(.mini)
            Text(activity.text)
        }
        .lineLimit(1)
        .fixedSize()
        .font(.caption)
        .foregroundStyle(.secondary)
    }
}

/// The text buttons of a row, secondary style: a visible capsule fill, lighter on hover, darker while pressed.
private struct RowTextButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        Highlight(configuration: configuration)
    }

    private struct Highlight: View {
        let configuration: ButtonStyleConfiguration
        @State private var hovered = false

        var body: some View {
            configuration.label
                .font(.caption.weight(.medium))
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .background(
                    Capsule()
                        .fill(Palette.inputSurface)
                        .overlay(Capsule().fill(
                            configuration.isPressed ? Color.black.opacity(0.2) : hovered ? Palette.hoverFill : .clear)))
                .contentShape(Capsule())
                .animation(Motion.hover, value: hovered)
                .onHover { hovered = $0 }
        }
    }
}

/// The source app's icon, or a waveform for global recordings and apps that are not installed.
struct AppIcon: View {
    let bundleID: String?

    var body: some View {
        Group {
            if let icon = bundleID.flatMap(AppIconCache.icon(bundleID:)) {
                Image(nsImage: icon).resizable()
            } else {
                Image(systemName: "waveform")
                    .font(.system(size: 18, weight: .medium))
                    .foregroundStyle(.secondary)
            }
        }
        .frame(width: 32, height: 32)
        .accessibilityHidden(true)
    }
}

/// App icons by bundle id, looked up once per app; a missing app is cached as nil too.
@MainActor
private enum AppIconCache {
    private static var icons: [String: NSImage?] = [:]

    static func icon(bundleID: String) -> NSImage? {
        if let cached = icons[bundleID] { return cached }
        let icon = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID)
            .map { NSWorkspace.shared.icon(forFile: $0.path) }
        icons[bundleID] = icon
        return icon
    }
}

/// A mini circular progress, drawn here: AppKit's determinate circular indicator paints a square behind itself.
private struct ProgressRing: View {
    let fraction: Double

    var body: some View {
        ZStack {
            Circle().stroke(Palette.hoverFill, lineWidth: 2)
            Circle()
                .trim(from: 0, to: fraction)
                .stroke(Color.accentColor, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                .rotationEffect(.degrees(-90))
        }
        .frame(width: 10, height: 10)
        .accessibilityElement()
        .accessibilityValue(fraction.formatted(.percent.precision(.fractionLength(0))))
    }
}
