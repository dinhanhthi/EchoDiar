import AVFoundation
import AppKit
import EchoAudio
import EchoCore
import SwiftUI

/// The first-run window: consent notice, microphone access, system audio test. Created on each `show` and
/// released on close, so "Show onboarding again" starts from the first step.
@MainActor
final class OnboardingWindow: NSObject, NSWindowDelegate {
    static let shared = OnboardingWindow()

    private var window: NSWindow?
    private var test: SystemAudioTest?

    func show(model: AppModel) {
        if window == nil {
            let test = SystemAudioTest()
            let view = OnboardingView(test: test) { [weak self] in
                model.updateSettings { $0.onboardingCompleted = true }
                self?.window?.close()
            }
            let window = NSWindow(contentViewController: NSHostingController(rootView: view))
            window.title = "Welcome to EchoDiar"
            window.titleVisibility = .hidden
            window.titlebarAppearsTransparent = true
            window.styleMask = [.titled, .closable, .fullSizeContentView]
            window.isReleasedWhenClosed = false
            window.center()
            window.delegate = self
            self.window = window
            self.test = test
        }
        NSApp.setActivationPolicy(.regular)
        Task { @MainActor [window] in
            NSApp.activate(ignoringOtherApps: true)
            window?.makeKeyAndOrderFront(nil)
            window?.orderFrontRegardless()
        }
    }

    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow, closing === window else { return }
        test?.cancel()
        Task { @MainActor [weak self] in
            self?.window = nil
            self?.test = nil
            let otherWindowOpen = NSApp.windows.contains {
                $0 !== closing && $0.isVisible && $0.styleMask.contains(.titled)
            }
            if !otherWindowOpen { NSApp.setActivationPolicy(.accessory) }
        }
    }
}

/// Privacy & Security panes. There is no API to read the System Audio Recording permission, so the app can only
/// point the user there.
enum PrivacySettings {
    static func openSystemAudioRecording() { open(anchor: "Privacy_AudioCapture") }
    static func openMicrophone() { open(anchor: "Privacy_Microphone") }
    static func openAccessibility() { open(anchor: "Privacy_Accessibility") }

    private static func open(anchor: String) {
        let pane = "x-apple.systempreferences:com.apple.settings.PrivacySecurity.extension"
        if let url = URL(string: "\(pane)?\(anchor)"), NSWorkspace.shared.open(url) { return }
        if let root = URL(string: pane) { _ = NSWorkspace.shared.open(root) }
    }
}

/// Shown during a recording while the tap delivers only silence (`Snapshot.silentTapSuspected`).
struct SilentTapBanner: View {
    let onFix: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "speaker.slash.fill")
                .foregroundStyle(.yellow)
            VStack(alignment: .leading, spacing: 2) {
                Text("No system audio")
                    .font(.callout.weight(.semibold))
                Text("EchoDiar may lack System Audio Recording permission. Allow it, then restart the recording.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            Button("Open Settings", action: onFix)
                .controlSize(.small)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .modifier(GlassCard(cornerRadius: 12))
    }
}

/// Runs a short global tap off the main actor while another process plays a sound. The tap excludes EchoDiar's
/// own audio, so the sound comes from `afplay`.
@MainActor
@Observable
final class SystemAudioTest {
    enum Phase: Equatable {
        case idle, running, heard, silent
        case failed(String)
    }

    private(set) var phase: Phase = .idle
    /// Live peak (0...1), only meaningful while running.
    private(set) var level: Float = 0

    @ObservationIgnored private var task: Task<Void, Never>?

    nonisolated private static let seconds = 3.0
    nonisolated private static let ticksPerSecond = 15.0
    nonisolated private static let silenceThreshold: Float = 0.001

    func run() {
        guard phase != .running else { return }
        phase = .running
        level = 0
        task = Task.detached(priority: .userInitiated) { [self] in
            let tap = ProcessTap()
            do {
                try tap.start(target: .global)
            } catch {
                await finish(.failed(String(describing: error)))
                return
            }
            var maxPeak: Float = 0
            let ticks = Int(Self.seconds * Self.ticksPerSecond)
            for tick in 0..<ticks {
                try? await Task.sleep(nanoseconds: UInt64(1e9 / Self.ticksPerSecond))
                if Task.isCancelled { break }
                // Started a moment after the tap so the whole sound lands inside the test.
                if tick == 4 { await playTestSound() }
                let peak = tap.currentRing?.readLevels().peak ?? 0
                maxPeak = max(maxPeak, peak)
                await setLevel(peak)
            }
            tap.stop()
            await finish(Task.isCancelled ? .idle : maxPeak > Self.silenceThreshold ? .heard : .silent)
        }
    }

    func cancel() {
        task?.cancel()
        task = nil
    }

    private func setLevel(_ peak: Float) {
        guard phase == .running else { return }
        level = peak
    }

    private func finish(_ result: Phase) {
        guard phase == .running else { return }
        phase = result
        level = 0
        task = nil
    }

    private func playTestSound() {
        let player = Process()
        player.executableURL = URL(fileURLWithPath: "/usr/bin/afplay")
        player.arguments = ["/System/Library/Sounds/Glass.aiff"]
        do {
            try player.run()
        } catch {
            NSLog("EchoDiar: could not play the test sound: \(error)")
        }
    }
}

// MARK: - Views

private struct OnboardingView: View {
    let test: SystemAudioTest
    let onDone: () -> Void

    @State private var step = 0
    private static let titles = ["Before you record", "Microphone", "System audio"]

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text(Self.titles[step])
                    .font(.title2.weight(.semibold))
                Spacer()
                Text("Step \(step + 1) of \(Self.titles.count)")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Group {
                switch step {
                case 0: ConsentStep()
                case 1: MicrophoneStep()
                default: SystemAudioStep(test: test)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .padding(20)
            .modifier(GlassCard(cornerRadius: 20))
            HStack {
                if step > 0 {
                    Button("Back") { step -= 1 }
                }
                Spacer()
                if step < Self.titles.count - 1 {
                    Button("Continue") { step += 1 }
                        .keyboardShortcut(.defaultAction)
                } else {
                    Button("Done", action: onDone)
                        .keyboardShortcut(.defaultAction)
                        .disabled(test.phase == .running)
                }
            }
        }
        .padding(.horizontal, 24)
        .padding(.top, 36)
        .padding(.bottom, 20)
        .frame(width: 520, height: 400)
        .background(WindowGlass().ignoresSafeArea())
    }
}

private struct ConsentStep: View {
    private static let consentMessage = "Heads up: I'm recording this call to take notes. The recording stays on my Mac."
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("Recording laws differ from place to place. Many require everyone on the call to agree.",
                  systemImage: "person.2.wave.2")
            Label("Tell the people you record. You are responsible for their consent.", systemImage: "hand.raised")
            Label("Audio and transcripts stay on this Mac.", systemImage: "lock.laptopcomputer")
            Text("\"\(Self.consentMessage)\"")
                .font(.callout)
                .foregroundStyle(.secondary)
            Button(copied ? "Copied" : "Copy consent message", systemImage: copied ? "checkmark" : "square.on.square") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(Self.consentMessage, forType: .string)
                copied = true
            }
        }
    }
}

private struct MicrophoneStep: View {
    @State private var status = AVCaptureDevice.authorizationStatus(for: .audio)

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("EchoDiar records your voice on its own channel, so the transcript knows what you said.")
            HStack(spacing: 6) {
                Image(systemName: statusIcon)
                    .foregroundStyle(statusColor)
                Text(statusText)
            }
            switch status {
            case .notDetermined:
                Button("Allow Microphone") {
                    Task {
                        _ = await AVCaptureDevice.requestAccess(for: .audio)
                        status = AVCaptureDevice.authorizationStatus(for: .audio)
                    }
                }
            case .denied, .restricted:
                Text("Turn on EchoDiar in Privacy & Security › Microphone.")
                    .foregroundStyle(.secondary)
                Button("Open Microphone Settings") { PrivacySettings.openMicrophone() }
            default:
                EmptyView()
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            status = AVCaptureDevice.authorizationStatus(for: .audio)
        }
    }

    private var statusText: String {
        switch status {
        case .authorized: "Microphone access is on."
        case .denied: "Microphone access is off."
        case .restricted: "Microphone access is blocked on this Mac."
        default: "Microphone access has not been requested yet."
        }
    }

    private var statusIcon: String {
        switch status {
        case .authorized: "checkmark.circle.fill"
        case .denied, .restricted: "xmark.circle.fill"
        default: "questionmark.circle"
        }
    }

    private var statusColor: Color {
        switch status {
        case .authorized: .green
        case .denied, .restricted: .red
        default: .secondary
        }
    }
}

private struct SystemAudioStep: View {
    let test: SystemAudioTest

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("EchoDiar records the other people on the call from your Mac's audio. The test plays a short sound "
                + "and listens for it. macOS asks for System Audio Recording permission the first time.")
                .fixedSize(horizontal: false, vertical: true)
            switch test.phase {
            case .idle:
                Button("Test System Audio", systemImage: "speaker.wave.2") { test.run() }
            case .running:
                LevelMeter(level: test.level)
                Text("Listening…")
                    .foregroundStyle(.secondary)
            case .heard:
                Label("System audio works.", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(.green)
                Button("Test Again") { test.run() }
            case .silent:
                Label("EchoDiar heard only silence.", systemImage: "speaker.slash.fill")
                    .foregroundStyle(.yellow)
                Text("macOS gives silence, not an error, when System Audio Recording is off. If you just allowed it, "
                    + "test again. Otherwise turn on EchoDiar in Privacy & Security › Screen & System Audio Recording.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack {
                    Button("Test Again") { test.run() }
                    Button("Open Settings") { PrivacySettings.openSystemAudioRecording() }
                }
            case .failed(let message):
                Label("The test could not start.", systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.yellow)
                Text(message)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                Button("Test Again") { test.run() }
            }
        }
    }
}

/// Peak meter on a -60...0 dBFS scale.
private struct LevelMeter: View {
    let level: Float

    private var fraction: CGFloat {
        guard level > 0 else { return 0 }
        let db = 20 * log10(Double(level))
        return CGFloat(min(max((db + 60) / 60, 0), 1))
    }

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(Palette.hoverFill)
                Capsule().fill(Color.green).frame(width: proxy.size.width * fraction)
            }
        }
        .frame(height: 8)
        .accessibilityLabel("System audio level")
        .accessibilityValue("\(Int((fraction * 100).rounded())) percent")
    }
}
