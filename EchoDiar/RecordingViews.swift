import AppKit
import EchoAudio
import EchoCore
import SwiftUI

/// The recording in the entry list, above the first day: the recorded app's icon, "Recording…" (or "Paused"), the
/// elapsed clock and mini meters, then a bottom row with Stop, Pause or Resume and the language. Compact (the wide
/// layout's sidebar) drops the row; selected adds the accent fill and stroke of `ConversationCard`. A click opens the
/// recording page.
struct RecordingCard: View {
    let model: AppModel
    var compact = false
    var selected = false

    var body: some View {
        let paused = model.snapshot.state == .paused
        HStack(spacing: 12) {
            AppIcon(bundleID: model.sourceApp?.appBundleID)
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Circle()
                        .fill(paused ? Color.orange : Color.red)
                        .frame(width: 8, height: 8)
                    Text(paused ? "Paused" : "Recording…")
                        .font(.headline)
                        .lineLimit(1)
                    Spacer(minLength: 0)
                }
                HStack(spacing: 8) {
                    // Only while the window is visible, as in `RecordingHeader`.
                    if model.mainWindowVisible {
                        TimelineView(.periodic(from: .now, by: 1)) { _ in elapsedText }
                        if !paused { MiniMeters(feed: model.levels) }
                    } else {
                        elapsedText
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                if !compact {
                    // The buttons have their own row under the title and details, from the same leading edge.
                    HStack(spacing: 12) {
                        rowButton("Stop", systemImage: "stop.circle", action: model.stopRecording)
                            .disabled(model.isStarting)
                            .help("Stop recording (⌘R)")
                        rowButton(paused ? "Resume" : "Pause", systemImage: paused ? "play.circle" : "pause.circle",
                                  action: model.togglePause)
                            .help(paused ? "Resume recording (⇧⌘P)" : "Pause recording (⇧⌘P)")
                        languageMenu
                        Spacer(minLength: 0)
                    }
                    .padding(.top, 4)
                }
            }
        }
        .padding(.horizontal, compact ? 10 : 14)
        .padding(.vertical, compact ? 8 : 10)
        // The fill sits under the content, on the surface; the stroke on top.
        .background {
            if selected { Self.shape.fill(Color.accentColor.opacity(0.18)) }
        }
        .modifier(GlassCard(cornerRadius: 20, raised: true))
        .overlay {
            if selected { Self.shape.strokeBorder(Color.accentColor, lineWidth: 1.5).allowsHitTesting(false) }
        }
        // Buttons and the menu take their own clicks.
        .contentShape(Rectangle())
        .onTapGesture { model.route = .recording }
        // The compact card has no button row: Stop and Pause stay here.
        .contextMenu {
            Button("Stop", action: model.stopRecording)
                .disabled(model.isStarting)
            Button(paused ? "Resume" : "Pause", action: model.togglePause)
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

    /// Picking a language adds a marker at the current recording time.
    private var languageMenu: some View {
        Menu {
            LanguagePicker(
                title: "Language",
                selection: Binding(get: { model.recordingLanguage }, set: { model.setRecordingLanguage($0) }),
                model: model)
                .pickerStyle(.inline)
                .labelsHidden()
        } label: {
            Label(LanguageOptions.shortCode(model.recordingLanguage), systemImage: "globe")
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .font(.caption)
        .foregroundStyle(.secondary)
        .help("Language from now on")
    }

    private var elapsedText: some View {
        let elapsed = model.engine.currentSnapshot().elapsed
        let pattern: Duration.TimeFormatStyle.Pattern = elapsed >= 3_600 ? .hourMinuteSecond : .minuteSecond
        return Text(Duration.seconds(elapsed).formatted(.time(pattern: pattern))).monospacedDigit()
    }
}

/// The recording page (layout_1's detail): the header, then the live transcript, or the wave and how to turn the
/// live transcript on.
struct RecordingDetailView: View {
    let model: AppModel

    var body: some View {
        VStack(spacing: 12) {
            RecordingHeader(model: model)
            if let live = model.liveTranscript {
                LiveTranscriptView(snapshot: live)
            } else {
                VStack(spacing: 16) {
                    RecordingWave(model: model)
                    Text("Live transcript is off. Turn it on in Settings › Transcription.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                    Button("Open Settings") { model.openSettings(tab: .transcription) }
                        .buttonStyle(.link)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }
}

/// Vertical bars that follow the louder channel's last peaks (newest on the right), smoothed, while recording and
/// the window is visible; flat while paused, a still shape under Reduce Motion.
struct RecordingWave: View {
    let model: AppModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var history = Array(repeating: CGFloat(0), count: Self.count)

    private static let count = 24
    private static let maxHeight: CGFloat = 64

    var body: some View {
        let paused = model.snapshot.state == .paused
        HStack(spacing: 4) {
            ForEach(0..<Self.count, id: \.self) { index in
                Capsule()
                    .fill(paused ? Color.secondary : Color.red)
                    .frame(width: 4, height: max(4, Self.maxHeight * height(index, paused: paused)))
            }
        }
        .frame(height: Self.maxHeight)
        .animation(reduceMotion ? nil : .linear(duration: 0.067), value: history)
        .accessibilityHidden(true)
        // The feed reads only while a view waits in `attach()`.
        .task(id: model.mainWindowVisible) {
            guard model.mainWindowVisible else { return }
            await model.levels.attach()
        }
        .task(id: sampling) {
            while sampling && !Task.isCancelled {
                sample()
                try? await Task.sleep(for: .milliseconds(67))
            }
        }
    }

    /// The bars move only while recording, the window is visible and motion is allowed.
    private var sampling: Bool {
        model.mainWindowVisible && model.snapshot.state != .paused && !reduceMotion
    }

    private func height(_ index: Int, paused: Bool) -> CGFloat {
        if paused { return 0 }
        if reduceMotion { return 0.2 + 0.4 * abs(sin(CGFloat(index) * 0.5)) }
        return history[index]
    }

    /// Adds the louder channel's peak; a fall decays from the last bar so the wave does not flicker.
    private func sample() {
        let level = max(LevelFeed.fraction(model.levels.mic), LevelFeed.fraction(model.levels.system))
        history.removeFirst()
        history.append(max(level, (history.last ?? 0) * 0.8))
    }
}

/// Elapsed time, two-channel level meter and capture warnings while a recording is open.
struct RecordingHeader: View {
    let model: AppModel
    /// The window is visible: only then the clock and the meter update.
    private var live: Bool { model.mainWindowVisible }

    var body: some View {
        let paused = model.snapshot.state == .paused
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Image(systemName: paused ? "pause.circle.fill" : "record.circle.fill")
                    .foregroundStyle(paused ? .orange : .red)
                if live {
                    TimelineView(.periodic(from: .now, by: 1)) { _ in elapsedText }
                } else {
                    elapsedText
                }
                Text(paused ? "Paused" : "Recording")
                    .foregroundStyle(.secondary)
                Spacer(minLength: 8)
                if live && !paused {
                    ChannelMeters(feed: model.levels)
                }
                languageMenu
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .modifier(GlassCard(cornerRadius: 14))
            if model.snapshot.globalTapFallback {
                Label("This app's audio cannot be isolated, so all system audio is being recorded.",
                      systemImage: "exclamationmark.triangle.fill")
                    .font(.callout)
                    .foregroundStyle(.yellow)
            }
            if model.snapshot.silentTapSuspected {
                SilentTapBanner { PrivacySettings.openSystemAudioRecording() }
            }
        }
    }

    /// Picking a language adds a marker at the current recording time.
    private var languageMenu: some View {
        Menu {
            LanguagePicker(
                title: "Language",
                selection: Binding(get: { model.recordingLanguage }, set: { model.setRecordingLanguage($0) }),
                model: model)
                .pickerStyle(.inline)
                .labelsHidden()
        } label: {
            Label(LanguageOptions.name(model.recordingLanguage), systemImage: "globe")
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .foregroundStyle(.secondary)
        .help("Language from now on")
    }

    private var elapsedText: some View {
        let elapsed = model.engine.currentSnapshot().elapsed
        let pattern: Duration.TimeFormatStyle.Pattern = elapsed >= 3_600 ? .hourMinuteSecond : .minuteSecond
        return Text(Duration.seconds(elapsed).formatted(.time(pattern: pattern)))
            .font(.body.monospacedDigit().weight(.semibold))
    }
}

/// Mic and system peak bars, from the shared `LevelFeed` (15 Hz while shown).
struct ChannelMeters: View {
    let feed: LevelFeed

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            bar("Mic", feed.mic)
            bar("System", feed.system)
        }
        .frame(width: 110)
        .task { await feed.attach() }
    }

    private func bar(_ name: String, _ level: Float) -> some View {
        HStack(spacing: 6) {
            Text(name)
                .font(.caption2)
                .foregroundStyle(.secondary)
                .frame(width: 38, alignment: .leading)
            GeometryReader { proxy in
                ZStack(alignment: .leading) {
                    Capsule().fill(Palette.hoverFill)
                    Capsule().fill(Color.green).frame(width: proxy.size.width * LevelFeed.fraction(level))
                }
            }
            .frame(height: 5)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(name) level")
        .accessibilityValue("\(Int((LevelFeed.fraction(level) * 100).rounded())) percent")
    }
}

/// `ChannelMeters` on one text line, for the card: mic over system, from the shared `LevelFeed`.
private struct MiniMeters: View {
    let feed: LevelFeed

    var body: some View {
        VStack(spacing: 2) {
            bar(feed.mic)
            bar(feed.system)
        }
        .frame(width: 44)
        .accessibilityHidden(true)
        .task { await feed.attach() }
    }

    private func bar(_ level: Float) -> some View {
        Capsule()
            .fill(Palette.hoverFill)
            .overlay(alignment: .leading) {
                Capsule().fill(Color.green).frame(width: 44 * LevelFeed.fraction(level))
            }
            .frame(height: 3)
    }
}

/// The recording indicator's blink: dimmed every other second, without a fade, while `isActive`. A repeating
/// `.pulse` redraws its window every display frame, and over Liquid Glass makes the window server recompose the glass
/// each frame too; this changes one frame per second.
struct RecordingBlink: ViewModifier {
    let isActive: Bool

    func body(content: Content) -> some View {
        if isActive {
            TimelineView(.periodic(from: .now, by: 1)) { context in
                content.opacity(Int(context.date.timeIntervalSinceReferenceDate) % 2 == 0 ? 1 : 0.35)
            }
        } else {
            content
        }
    }
}
