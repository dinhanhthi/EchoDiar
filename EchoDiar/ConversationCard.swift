import AppKit
import EchoAI
import EchoCore
import EchoTranscribe
import SwiftUI

/// One conversation of the library: source app icon, title (double-click to rename), time, duration, size and
/// status, then a bottom row with the AI, Summary, Reveal in Finder and Delete buttons and the transcription progress
/// or its action. A click elsewhere opens the transcript. Compact (the wide layout's sidebar) drops the buttons, which
/// stay in the context menu and the detail, and keeps the status, and a click keeps the detail's tab; selected adds an
/// accent fill and stroke.
struct ConversationCard: View {
    let conversation: Conversation
    let model: AppModel
    var compact = false
    var selected = false
    @State private var editing = false
    @State private var draft = ""
    @FocusState private var titleFocused: Bool

    var body: some View {
        HStack(spacing: 12) {
            AppIcon(bundleID: conversation.sourceBundleID)
            VStack(alignment: .leading, spacing: 4) {
                title
                details
                TranscriptionFailureReason(conversation: conversation, model: model)
                // The buttons and status have their own row under the title and details, from the same leading edge.
                HStack(spacing: 12) {
                    if !compact {
                        rowButton("AI", systemImage: "sparkles") { model.open(conversation.id, tab: .chat) }
                            .help("Ask AI about this conversation")
                        if conversation.hasSummary {
                            rowButton("Summary", systemImage: "text.alignleft") {
                                model.open(conversation.id, tab: .summary)
                            }
                            .help("Show the summary")
                        }
                        rowButton("Reveal in Finder", systemImage: "folder", action: revealInFinder)
                            .help("Reveal in Finder")
                        rowButton("Delete", systemImage: "trash", action: delete)
                            .disabled(!canDelete)
                            .help("Move this conversation to the Trash")
                    }
                    TranscriptionStatus(conversation: conversation, model: model)
                    Spacer(minLength: 0)
                }
            }
        }
        .padding(.horizontal, compact ? 10 : 14)
        .padding(.vertical, compact ? 8 : 10)
        // The fill sits under the content, on the surface; the stroke on top.
        .background {
            if selected { Self.shape.fill(Color.accentColor.opacity(0.18)) }
        }
        .modifier(GlassCard(cornerRadius: 20))
        .overlay {
            if selected { Self.shape.strokeBorder(Color.accentColor, lineWidth: 1.5).allowsHitTesting(false) }
        }
        // Buttons take their own clicks; the title's double click is handled before this.
        .contentShape(Rectangle())
        .onTapGesture(perform: open)
        .contextMenu {
            Button("Rename", action: beginEditing)
            Button("Reveal in Finder", action: revealInFinder)
            Divider()
            Button(model.settings.deleteWithoutConfirmation ? "Delete" : "Delete…", action: delete)
                .disabled(!canDelete)
        }
    }

    /// The `GlassCard` shape, for the selection.
    private static let shape = RoundedRectangle(cornerRadius: 20, style: .continuous)

    private func rowButton(_ title: String, systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .labelStyle(.iconOnly)
                .font(.system(size: 15))
        }
        .buttonStyle(RowIconButtonStyle())
        .foregroundStyle(.secondary)
    }

    /// Not while it is recorded, compressed or transcribed.
    private var canDelete: Bool {
        conversation.status != .recording && conversation.status != .compressing
            && !model.isTranscribing(conversation.id)
    }

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
                .onTapGesture(count: 2, perform: beginEditing)
                // After the double click, so a double click renames instead of opening.
                .onTapGesture(perform: open)
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

    /// Not while the title is being edited: clicks in its field must not open the transcript.
    /// The transcript; in the sidebar, the tab the detail shows.
    private func open() {
        guard !editing else { return }
        model.open(conversation.id, tab: compact ? model.detailTab : .transcript)
    }

    private func revealInFinder() {
        NSWorkspace.shared.activateFileViewerSelecting([conversation.folder])
    }

    /// Asks first unless "Delete conversations without confirmation" is on.
    private func delete() {
        if model.settings.deleteWithoutConfirmation {
            model.deleteConversation(conversation.id)
        } else {
            confirmDelete()
        }
    }

    private func confirmDelete() {
        let alert = NSAlert()
        alert.messageText = "Delete \"\(conversation.title)\"?"
        alert.informativeText = "The recording and its files are moved to the Trash."
        alert.addButton(withTitle: "Delete").hasDestructiveAction = true
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        model.deleteConversation(conversation.id)
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

    /// Progress first: a queued job's conversation is still `ready`. A `transcribing` status without a job was
    /// left by a quit or crash, so it offers Transcribe again. A failed recording (`failed`) offers nothing.
    var body: some View {
        let progress = model.transcriptionProgress[conversation.id]
        if let progress, progress.step != .needsModel {
            HStack(spacing: 6) {
                if progress.step == .queued || progress.step == .deferred {
                    ProgressView().controlSize(.mini)
                } else {
                    ProgressView(value: progress.fraction).progressViewStyle(.circular).controlSize(.mini)
                }
                Text(Self.stepText(progress.step))
            }
            .lineLimit(1)
            .fixedSize()
            .font(.caption)
            .foregroundStyle(.secondary)
            .help(progress.step == .deferred
                  ? "Waiting while the Mac is hot or in Low Power Mode"
                  : progress.fraction.formatted(.percent.precision(.fractionLength(0))))
        } else if progress != nil {
            Button("Needs model") { model.openSettings(tab: .transcription) }
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
                    .help("Transcription failed — transcribe this conversation again")
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
                .help("Retry with another engine")
            }
            .fixedSize()
        } else if conversation.status == .ready || conversation.status == .transcribing, !conversation.hasTranscript {
            Button("Transcribe") { model.transcribe(conversation.id) }
                .buttonStyle(RowTextButtonStyle())
                .fixedSize()
                .foregroundStyle(.secondary)
                .help("Transcribe this conversation")
        }
    }

    private static func stepText(_ step: JobProgress.Step) -> String {
        switch step {
        case .queued: "Queued"
        case .deferred: "Waiting…"
        case .extracting: "Preparing audio…"
        case .transcribingMic, .transcribingSystem: "Transcribing…"
        case .diarizing: "Identifying speakers…"
        case .merging, .finished: "Finishing…"
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
            let reason = model.transcriptionErrors[conversation.id] ?? "Transcription failed"
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

/// Installed local engines, then the configured cloud engines under "Cloud", for the Retry and Re-transcribe menus;
/// "Download a model…" when there are none. `pick` gets a catalog or cloud engine id.
struct EngineMenuItems: View {
    let title: String
    let model: AppModel
    let pick: (String) -> Void

    var body: some View {
        let local = model.installedBatchEngines
        let cloud = model.cloudEngineConfigs
        if local.isEmpty && cloud.isEmpty {
            Button("Download a model…") { model.openSettings(tab: .transcription) }
        } else {
            if !local.isEmpty {
                Section(title) {
                    ForEach(local) { entry in Button(entry.displayName) { pick(entry.id) } }
                }
            }
            if !cloud.isEmpty {
                Section(local.isEmpty ? "\(title) (cloud)" : "Cloud") {
                    ForEach(cloud) { config in
                        Button(model.cloudEngineName(config)) { pick(CloudTranscription.engineID(providerID: config.id)) }
                    }
                }
            }
        }
    }
}

/// A short colored label for the statuses that need attention; nil for `ready`, `transcribed` and
/// `transcriptionFailed` (which shows its reason and Retry).
struct StatusBadge: View {
    let text: String
    let color: Color

    init?(status: Conversation.Status) {
        switch status {
        case .recording: (text, color) = ("Recording", .red)
        case .recovered: (text, color) = ("Recovered", .orange)
        case .compressing: (text, color) = ("Compressing…", .blue)
        case .compressionFailed: (text, color) = ("Compression failed", .red)
        case .failed: (text, color) = ("Failed", .red)
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
                .animation(.easeOut(duration: 0.12), value: hovered)
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
