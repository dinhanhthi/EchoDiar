import AppKit
import EchoCore
import EchoTranscribe
import SwiftUI

/// One conversation of the library: source app icon, title (double-click to rename), time, duration, size,
/// status, transcription progress or its action, and the AI and Summary buttons. A click elsewhere opens the
/// transcript.
struct ConversationCard: View {
    let conversation: Conversation
    let model: AppModel
    @State private var editing = false
    @State private var draft = ""
    @FocusState private var titleFocused: Bool

    var body: some View {
        HStack(spacing: 12) {
            AppIcon(bundleID: conversation.sourceBundleID)
            VStack(alignment: .leading, spacing: 4) {
                title
                details
            }
            Spacer(minLength: 8)
            TranscriptionStatus(conversation: conversation, model: model)
            Button(action: showAI) {
                Label("AI", systemImage: "sparkles")
                    .labelStyle(.iconOnly)
                    .font(.system(size: 15))
            }
            .buttonStyle(RowIconButtonStyle())
            .foregroundStyle(.secondary)
            .help("Ask AI about this conversation")
            if conversation.hasSummary {
                Button(action: showAI) {
                    Label("Summary", systemImage: "text.alignleft")
                        .labelStyle(.iconOnly)
                        .font(.system(size: 15))
                }
                .buttonStyle(RowIconButtonStyle())
                .foregroundStyle(.secondary)
                .help("Show the summary")
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(GlassCard(cornerRadius: 20))
        // Buttons take their own clicks; the title's double click is handled before this.
        .contentShape(Rectangle())
        .onTapGesture(perform: open)
        .contextMenu {
            Button("Rename", action: beginEditing)
            Button("Reveal in Finder") {
                NSWorkspace.shared.activateFileViewerSelecting([conversation.folder])
            }
            Divider()
            Button("Delete…", action: confirmDelete)
                .disabled(conversation.status == .recording || conversation.status == .compressing
                          || model.isTranscribing(conversation.id))
        }
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
    private func open() {
        guard !editing else { return }
        withAnimation(.snappy) { model.openedConversationID = conversation.id }
    }

    private func showAI() {
        model.selectedConversationID = conversation.id
        withAnimation(.snappy) { model.selectedTab = .ai }
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

/// A conversation's transcription progress, or its action: Needs model, Failed — Retry, or Transcribe. Shared by
/// the card and the transcript header.
struct TranscriptionStatus: View {
    let conversation: Conversation
    let model: AppModel

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
            .font(.caption)
            .foregroundStyle(.secondary)
            .help(progress.step == .deferred
                  ? "Waiting while the Mac is hot or in Low Power Mode"
                  : progress.fraction.formatted(.percent.precision(.fractionLength(0))))
        } else if progress != nil {
            Button("Needs model") { model.openSettings(tab: .transcription) }
                .buttonStyle(RowTextButtonStyle())
                .foregroundStyle(.orange)
                .help("Download a transcription model in Settings")
        } else if conversation.status == .transcriptionFailed {
            Button("Failed — Retry") { model.transcribe(conversation.id) }
                .buttonStyle(RowTextButtonStyle())
                .foregroundStyle(.red)
                .help("Transcribe this conversation again")
        } else if conversation.status == .ready || conversation.status == .transcribing, !conversation.hasTranscript {
            Button("Transcribe") { model.transcribe(conversation.id) }
                .buttonStyle(RowTextButtonStyle())
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

/// A short colored label for the statuses that need attention; nil for `ready`, `transcribed` and
/// `transcriptionFailed` (which shows "Failed — Retry").
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

/// The text buttons of a row: a capsule highlight on hover, darker while pressed.
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
                    Capsule().fill(configuration.isPressed ? Palette.pressedFill : hovered ? Palette.hoverFill : .clear))
                .contentShape(Capsule())
                .animation(.easeOut(duration: 0.12), value: hovered)
                .onHover { hovered = $0 }
        }
    }
}

/// The source app's icon, or a waveform for global recordings and apps that are not installed.
private struct AppIcon: View {
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
