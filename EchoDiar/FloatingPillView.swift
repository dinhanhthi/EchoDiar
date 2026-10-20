import EchoAudio
import EchoCore
import SwiftUI

/// Whether the pointer is over the pill, from `PillHostingView`'s tracking area.
@MainActor
@Observable
final class PillHover {
    var isHovering = false
}

/// The pill's content: the recording indicator (dot, elapsed time, level meter; Pause/Resume, Stop and Language on
/// hover) or a prompt. Created each time the pill shows, so its clock and meter run only while it is visible.
struct FloatingPillView: View {
    let model: AppModel
    let hover: PillHover
    /// The laid-out size, for the panel.
    let onSize: (CGSize) -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var expanded = false

    var body: some View {
        content
            .padding(.horizontal, 12)
            .modifier(PillGlass())
            // Clear room in the panel for the glass's edge and shadow, which the panel's bounds would cut off.
            .padding(6)
            .fixedSize()
            .gesture(WindowDragGesture())
            .onGeometryChange(for: CGSize.self) { $0.size } action: { onSize($0) }
            // Expands at once, collapses 1 s after the pointer leaves.
            .task(id: hover.isHovering) {
                let hovering = hover.isHovering
                if !hovering {
                    try? await Task.sleep(for: .seconds(1))
                    guard !Task.isCancelled else { return }
                }
                withAnimation(reduceMotion ? nil : Motion.standard) { expanded = hovering }
            }
    }

    @ViewBuilder private var content: some View {
        switch model.pillPrompt {
        case .meetingDetected(let meeting)?: detectedPrompt(meeting)
        case .meetingEnded(let reason, let deadline)?: endPrompt(reason: reason, deadline: deadline)
        case nil: indicator
        }
    }

    // MARK: - Recording

    private var indicator: some View {
        let paused = model.snapshot.state == .paused
        let tint: Color = paused ? .orange : .red
        return HStack(spacing: 8) {
            PillLogo(size: 16)
            Image(systemName: "circle.fill")
                .font(.system(size: 9))
                .foregroundStyle(tint)
                .modifier(RecordingBlink(isActive: !paused && !reduceMotion))
                .accessibilityLabel(paused ? "Paused" : "Recording")
            if let name = model.autoStartedAppName {
                Text("Recording \(name)")
                    .font(.callout.weight(.semibold))
            }
            if paused {
                elapsed.foregroundStyle(tint)
            } else {
                TimelineView(.periodic(from: .now, by: 1)) { _ in elapsed }
                PillMeter(feed: model.levels)
            }
            if expanded {
                controls(paused: paused)
                    .transition(.opacity)
            }
        }
        .frame(height: 28)
    }

    private var elapsed: some View {
        let seconds = model.engine.currentSnapshot().elapsed
        let pattern: Duration.TimeFormatStyle.Pattern = seconds >= 3_600 ? .hourMinuteSecond : .minuteSecond
        return Text(Duration.seconds(seconds).formatted(.time(pattern: pattern)))
            .font(.callout.monospacedDigit().weight(.semibold))
    }

    private func controls(paused: Bool) -> some View {
        HStack(spacing: 12) {
            Button(action: model.togglePause) {
                Label(paused ? "Resume" : "Pause", systemImage: paused ? "play.fill" : "pause.fill")
            }
            .help(paused ? "Resume recording" : "Pause recording")
            Button(action: model.stopRecording) {
                Label("Stop", systemImage: "stop.fill")
            }
            .help("Stop recording")
            Menu {
                LanguagePicker(
                    title: "Language",
                    selection: Binding(get: { model.recordingLanguage }, set: { model.setRecordingLanguage($0) }),
                    model: model)
                    .pickerStyle(.inline)
                    .labelsHidden()
            } label: {
                Label("Language", systemImage: "globe")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help("Language from now on")
        }
        .labelStyle(.iconOnly)
        .buttonStyle(RowIconButtonStyle())
    }

    // MARK: - Prompts

    private func detectedPrompt(_ meeting: DetectedMeeting) -> some View {
        HStack(spacing: 10) {
            PillLogo(size: 32)
            VStack(alignment: .leading, spacing: 8) {
                Text("\(meeting.name) call detected — Record?")
                    .font(.callout.weight(.semibold))
                HStack(spacing: 8) {
                    Button("Record") { model.recordDetectedMeeting() }
                        .buttonStyle(.borderedProminent)
                        .tint(.red)
                    Button("Not now", action: model.dismissMeetingPrompt)
                    // Not for browsers: any web page using the mic would start a recording.
                    if !meeting.app.isBrowser {
                        Button("Always for \(meeting.name)") { model.recordDetectedMeeting(always: true) }
                    }
                }
                .controlSize(.small)
            }
        }
        .padding(.vertical, 10)
    }

    /// The default action is Stop: the recording stops when the countdown ends.
    private func endPrompt(reason: MeetingEndMonitor.Reason, deadline: Date) -> some View {
        HStack(spacing: 10) {
            PillLogo(size: 32)
            VStack(alignment: .leading, spacing: 8) {
                Text(question(for: reason))
                    .font(.callout.weight(.semibold))
                TimelineView(.periodic(from: .now, by: 1)) { context in
                    let left = max(0, Int(deadline.timeIntervalSince(context.date).rounded(.up)))
                    Text("Stopping in \(left) s")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
                HStack(spacing: 8) {
                    Button("Keep recording", action: model.keepRecording)
                    Button("Stop now", action: model.stopFromEndPrompt)
                        .buttonStyle(.borderedProminent)
                        .tint(.red)
                }
                .controlSize(.small)
            }
        }
        .padding(.vertical, 10)
    }

    private func question(for reason: MeetingEndMonitor.Reason) -> String {
        let app = model.sourceApp?.name ?? "The app"
        switch reason {
        case .sourceExited: return "\(app) quit — stop recording?"
        case .sourceInactive: return "\(app) stopped using audio — stop recording?"
        case .callEnded: return "The call seems to be over — stop recording?"
        case .silence: return "No sound for \(model.settings.endMonitor.effectiveSilenceMinutes) min — stop recording?"
        case .screenLocked: return "Screen locked — stop recording?"
        case .inputRouteLost: return "Microphone disconnected — stop recording?"
        case .systemSleep, .diskFull, .maxDuration: return "Stop recording?"
        }
    }
}

/// The app logo, so the pill reads as EchoDiar's over other apps.
private struct PillLogo: View {
    let size: CGFloat

    var body: some View {
        if let logo = AboutInfo.logo {
            Image(nsImage: logo)
                .resizable()
                .interpolation(.high)
                .frame(width: size, height: size)
                .accessibilityHidden(true)
        }
    }
}

/// The louder channel's peak, from the shared `LevelFeed` (15 Hz while shown).
private struct PillMeter: View {
    let feed: LevelFeed

    var body: some View {
        let level = LevelFeed.fraction(max(feed.mic, feed.system))
        Capsule()
            .fill(Palette.hoverFill)
            .overlay(alignment: .leading) {
                Capsule().fill(Color.green).frame(width: 28 * level)
            }
            .frame(width: 28, height: 4)
            .accessibilityHidden(true)
            .task { await feed.attach() }
    }
}

/// The pill floats over other apps in a clear panel: Liquid Glass on macOS 26+ (unless `--force-fallbacks`), a
/// material otherwise. Not `GlassCard`, whose solid surface is for the main window.
private struct PillGlass: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 26, *), PlatformFeatures.liquidGlass {
            content.glassEffect(.regular, in: .rect(cornerRadius: 14))
        } else {
            content.background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        }
    }
}
