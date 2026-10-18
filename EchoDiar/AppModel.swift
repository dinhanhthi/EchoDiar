import AppKit
import EchoAudio
import EchoCore
import Foundation
import Observation

/// App-wide state shared by the main window, the settings window and the menu bar item.
/// Coordinates recording: engine → stop → compress → library refresh.
@MainActor
@Observable
final class AppModel {
    enum Tab { case library, ai }

    /// DEBUG harness launch args handled here; they skip onboarding and auto-queued work.
    static let harnessArguments: Set<String> = ["--transcribe", "--download-model", "--delete-model"]

    private(set) var settings = AppSettings()
    var selectedTab: Tab = .library
    var selectedConversationID: UUID?
    /// The library, newest first, as last emitted by `library.changes()`.
    private(set) var conversations: [Conversation] = []
    /// The last snapshot published by the engine.
    private(set) var snapshot: RecordingEngine.Snapshot
    /// What ⌘R and the menu bar record.
    var selectedTarget: CaptureTarget = .global
    /// Apps with running audio input or output, for the Record target menu.
    private(set) var targetGroups: [AudioProcessGroup] = []
    private(set) var isStarting = false
    var recordingError: String?
    private var compressionJobs = 0

    var indicatorState: IndicatorState {
        switch snapshot.state {
        case .recording: .recording
        case .paused: .paused
        case .idle: compressionJobs > 0 ? .processing : .idle
        }
    }

    var isRecording: Bool { snapshot.state != .idle }

    /// Its `currentLevels()` has a single consumer: the level meter in the main window.
    @ObservationIgnored let engine: RecordingEngine
    @ObservationIgnored let library = LibraryStore()
    @ObservationIgnored private let compressor = AudioCompressor()
    @ObservationIgnored private let store: SettingsStore
    @ObservationIgnored private var recovery: Task<[RecoveredResult], Never>?
    /// Folder of the session being recorded; compressed when the engine reports the end.
    @ObservationIgnored private var activeFolder: URL?
    @ObservationIgnored private var terminateObserver: (any NSObjectProtocol)?

    init(store: SettingsStore = SettingsStore()) {
        self.store = store
        let engine = RecordingEngine()
        self.engine = engine
        snapshot = engine.currentSnapshot()
    }

    /// Called once by the AppDelegate after the main window is built. Later phases dispatch the DEBUG harness
    /// launch args (`--transcribe`, `--download-model`, `--delete-model`) from here.
    func start() {
        settings = store.load()
        recovery = (NSApp.delegate as? AppDelegate)?.recovery
        observeSnapshots()
        terminateObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification, object: nil, queue: nil
        ) { [engine] _ in
            engine.stop(reason: .user)
        }
        StatusItemController.controls = RecordingMenuControls(
            elapsed: { [weak self] in self?.engine.currentSnapshot().elapsed ?? 0 },
            silentTapSuspected: { [weak self] in self?.snapshot.silentTapSuspected ?? false },
            toggleRecording: { [weak self] in self?.toggleRecording() },
            togglePause: { [weak self] in self?.togglePause() },
            stop: { [weak self] in self?.stopRecording() })

        guard ProcessInfo.processInfo.arguments.allSatisfy({ !Self.harnessArguments.contains($0) }) else { return }
        if !settings.onboardingCompleted { OnboardingWindow.shared.show(model: self) }
        Task {
            // Subscribed before the first refresh, whose emit would otherwise be missed.
            let changes = await library.changes()
            Task {
                for await conversations in changes { self.conversations = conversations }
            }
            _ = await recovery?.value
            let folders = await Task.detached(priority: .utility) { [root = library.root] in
                Self.uncompressedFolders(in: root)
            }.value
            if folders.isEmpty {
                await library.refresh()
            } else {
                compress(folders)
            }
        }
    }

    // MARK: - Recording

    func toggleRecording() {
        if isRecording { stopRecording() } else { startRecording(target: selectedTarget) }
    }

    /// Waits for launch recovery, then starts the engine off the main thread.
    func startRecording(target: CaptureTarget) {
        guard !isRecording, !isStarting else { return }
        isStarting = true
        let language = settings.defaultLanguage
        Task {
            _ = await recovery?.value
            do {
                activeFolder = try await Task.detached(priority: .userInitiated) { [engine] in
                    try engine.start(target: Self.resolved(target), language: language)
                }.value
            } catch {
                NSLog("EchoDiar: could not start recording: \(error)")
                recordingError = "The recording could not start (\(error))."
            }
            isStarting = false
        }
    }

    func stopRecording() {
        guard isRecording else { return }
        Task.detached(priority: .userInitiated) { [engine] in
            engine.stop(reason: .user)
        }
    }

    func togglePause() {
        let state = snapshot.state
        guard state != .idle else { return }
        Task {
            let failure: (any Error)? = await Task.detached(priority: .userInitiated) { [engine] in
                if state == .recording {
                    engine.pause()
                    return nil
                }
                do {
                    try engine.resume()
                    return nil
                } catch {
                    return error
                }
            }.value
            if let failure {
                NSLog("EchoDiar: could not resume recording: \(failure)")
                recordingError = "The recording could not resume (\(failure))."
            }
        }
    }

    /// Reloads the running apps for the target menu, off the main thread.
    func refreshTargets() {
        Task {
            targetGroups = await Task.detached(priority: .utility) {
                AudioProcessList.groups().filter { $0.isRunningInput || $0.isRunningOutput }
            }.value
        }
    }

    /// A picked app gets its current processes, which may have changed since the menu was built.
    nonisolated private static func resolved(_ target: CaptureTarget) -> CaptureTarget {
        guard case .app(let group) = target,
              let fresh = AudioProcessList.groups().first(where: { $0.bundleID == group.bundleID }) else { return target }
        return .app(fresh)
    }

    /// The single consumer of `engine.snapshots`. A snapshot with an end reason closes the session,
    /// whether the user or the engine (disk full, write failure) stopped it.
    private func observeSnapshots() {
        Task { [weak self, engine] in
            for await snapshot in engine.snapshots {
                guard let self else { return }
                self.snapshot = snapshot
                if snapshot.state == .idle, let reason = snapshot.endReason {
                    if let message = Self.stopMessage(reason) { recordingError = message }
                    if let folder = activeFolder {
                        activeFolder = nil
                        compress([folder])
                    }
                }
            }
        }
    }

    /// Why the engine stopped on its own; nil when the user stopped it.
    private static func stopMessage(_ reason: RecordingEngine.EndReason) -> String? {
        switch reason {
        case .user: nil
        case .diskFull: "Recording stopped: disk is full."
        case .writeFailed: "Recording stopped: could not write audio."
        case .captureFailed: "Recording stopped: audio capture failed."
        }
    }

    /// Refreshes the library so the new cards show, compresses one folder at a time, then refreshes again.
    /// Each job also refreshes once it has marked its folder `compressing`, so the card shows the badge.
    private func compress(_ folders: [URL]) {
        compressionJobs += 1
        let bitrate = settings.compressionBitrateKbps
        let keepCAF = settings.keepUncompressedAudio
        Task {
            await library.refresh()
            for folder in folders {
                let result = await compressor.compress(folder: folder, bitrateKbps: bitrate, keepCAF: keepCAF) { [library] in
                    Task { await library.refresh() }
                }
                if let failure = result.failure {
                    NSLog("EchoDiar: compression failed for \(folder.lastPathComponent): \(failure)")
                }
            }
            compressionJobs -= 1
            await library.refresh()
        }
    }

    /// Folders whose audio is still the CAF of a finished or recovered session, e.g. a recording stopped at quit,
    /// and folders left `compressing` by a job that was interrupted. Symlinked folders are skipped.
    nonisolated private static func uncompressedFolders(in root: URL) -> [URL] {
        let folders = (try? FileManager.default.contentsOfDirectory(
            at: root, includingPropertiesForKeys: [.isSymbolicLinkKey], options: [.skipsHiddenFiles])) ?? []
        return folders.filter { folder in
            guard (try? folder.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink != true,
                  let session = try? RecordingSession.read(from: folder), !session.probe,
                  session.state == .stopped || session.state == .recovered else { return false }
            if (try? Conversation.read(from: folder))?.status == .compressing { return true }
            guard session.audioFileName == AudioCompressor.cafFileName,
                  let caf = ConversationFiles.audioURL(in: folder, fileName: session.audioFileName) else { return false }
            return FileManager.default.fileExists(atPath: caf.path)
        }
    }

    // MARK: - Library

    func renameConversation(_ id: UUID, to title: String) {
        Task {
            do {
                try await library.rename(id: id, title: title)
            } catch {
                NSLog("EchoDiar: could not rename conversation \(id): \(error)")
            }
        }
    }

    /// Moves the conversation to the Trash.
    func deleteConversation(_ id: UUID) {
        if selectedConversationID == id { selectedConversationID = nil }
        Task {
            do {
                try await library.delete(id: id)
            } catch {
                NSLog("EchoDiar: could not delete conversation \(id): \(error)")
            }
        }
    }

    // MARK: - Settings

    /// Applies `change` and saves the settings file.
    func updateSettings(_ change: (inout AppSettings) -> Void) {
        var updated = settings
        change(&updated)
        guard updated != settings else { return }
        settings = updated
        do {
            try store.save(updated)
        } catch {
            NSLog("EchoDiar: could not save settings: \(error)")
        }
    }
}
