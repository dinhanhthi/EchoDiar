import AppKit
import EchoAudio
import EchoCore
import EchoTranscribe
import Foundation
import Observation
import os

/// App-wide state shared by the main window, the settings window and the menu bar item.
/// Coordinates recording: engine → stop → compress → transcribe → library refresh.
@MainActor
@Observable
final class AppModel {
    enum Tab { case library, ai }

    /// DEBUG harness launch args handled here; they skip onboarding and auto-queued work.
    static let harnessArguments: Set<String> = ["--transcribe", "--download-model", "--delete-model"]

    private(set) var settings = AppSettings()
    var selectedTab: Tab = .library
    var selectedConversationID: UUID?
    /// The conversation whose transcript fills the library pane; nil shows the cards.
    var openedConversationID: UUID?
    /// The library, newest first, as last emitted by `library.changes()`.
    private(set) var conversations: [Conversation] = []
    /// The last snapshot published by the engine.
    private(set) var snapshot: RecordingEngine.Snapshot
    /// What ⌘R and the menu bar record.
    var selectedTarget: CaptureTarget = .global
    /// Apps with running audio input or output, for the Record target menu.
    private(set) var targetGroups: [AudioProcessGroup] = []
    private(set) var isStarting = false
    /// Picked in the Record menu or while recording; nil = `settings.defaultLanguage`. Cleared when a recording ends.
    private var languageOverride: String?
    var recordingError: String?
    private var compressionJobs = 0
    private var transcriptionJobs = 0
    /// Latest progress of each queued or running transcription, by conversation id. A `needsModel` entry stays
    /// (its `detail` is the missing catalog id) until the job is submitted again or a model download succeeds.
    private(set) var transcriptionProgress: [UUID: JobProgress] = [:]
    var settingsTab: SettingsTab = .general
    /// Running model downloads, by catalog id.
    private(set) var downloads: [String: ModelDownload] = [:]
    /// Last download error, by catalog id; cleared when a download of that id starts.
    private(set) var downloadErrors: [String: String] = [:]
    private(set) var installedModelIDs: Set<String> = []
    private(set) var installedModelsBytes: Int64 = 0
    /// Live captions of the current recording; nil when the setting was off at its start, or when not recording.
    private(set) var liveTranscript: LiveTranscriber.Snapshot?

    struct ModelDownload {
        var fraction: Double = 0
        let task: Task<Void, any Error>
    }

    var indicatorState: IndicatorState {
        switch snapshot.state {
        case .recording: .recording
        case .paused: .paused
        case .idle: compressionJobs > 0 || transcriptionJobs > 0 ? .processing : .idle
        }
    }

    var isRecording: Bool { snapshot.state != .idle }

    /// Its `currentLevels()` has a single consumer: the level meter in the main window.
    @ObservationIgnored let engine: RecordingEngine
    @ObservationIgnored let library = LibraryStore()
    /// Created with the model so FluidAudio's offline mode is set before any model load.
    @ObservationIgnored let modelStore: ModelStore
    @ObservationIgnored let pipeline: TranscriptionPipeline
    @ObservationIgnored private let compressor = AudioCompressor()
    @ObservationIgnored private let store: SettingsStore
    @ObservationIgnored private var recovery: Task<[RecoveredResult], Never>?
    /// Folder of the session being recorded; compressed when the engine reports the end.
    @ObservationIgnored private var activeFolder: URL?
    @ObservationIgnored private var terminateObserver: (any NSObjectProtocol)?
    /// `supportedLanguages(engineID:)` by catalog id, sorted by name once.
    @ObservationIgnored private var languageCache: [String: [String]] = [:]
    @ObservationIgnored private var liveTranscriber: LiveTranscriber?

    init(store: SettingsStore = SettingsStore()) {
        self.store = store
        let engine = RecordingEngine()
        self.engine = engine
        snapshot = engine.currentSnapshot()
        let modelStore = ModelStore()
        self.modelStore = modelStore
        pipeline = TranscriptionPipeline(modelStore: modelStore)
    }

    /// Called once by the AppDelegate after the main window is built. Dispatches the DEBUG harness launch args
    /// `--transcribe`, `--download-model` and `--delete-model`.
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
            stop: { [weak self] in self?.stopRecording() },
            language: { [weak self] in self?.recordingLanguage ?? LanguageOptions.auto },
            languageGroups: { [weak self] in
                guard let self else { return [] }
                return LanguageOptions.groups(
                    selection: recordingLanguage, recent: settings.recentLanguages, supported: supportedLanguages())
            },
            setLanguage: { [weak self] in self?.setRecordingLanguage($0) })

        #if DEBUG
        if runTranscribeHarness() || runModelHarness() { return }
        #endif
        guard ProcessInfo.processInfo.arguments.allSatisfy({ !Self.harnessArguments.contains($0) }) else { return }
        if !settings.onboardingCompleted { OnboardingWindow.shared.show(model: self) }
        observeTranscriptions()
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
        let language = recordingLanguage
        Task {
            _ = await recovery?.value
            // Before the engine starts: it reads the sink at start, and the model loads meanwhile.
            if settings.liveTranscript { startLiveTranscript() }
            do {
                activeFolder = try await Task.detached(priority: .userInitiated) { [engine] in
                    try engine.start(target: Self.resolved(target), language: language)
                }.value
            } catch {
                // Not awaited: a first model load can take long, and no batch job follows a failed start.
                let live = detachLiveTranscript()
                Task { await live?.stop() }
                NSLog("EchoDiar: could not start recording: \(error)")
                recordingError = "The recording could not start (\(error))."
            }
            isStarting = false
        }
    }

    /// Connects a new transcriber to the engine's sink, publishes its updates until `detachLiveTranscript` and loads
    /// its model.
    private func startLiveTranscript() {
        let live = LiveTranscriber(modelStore: modelStore) { [engine] in engine.currentLanguage() }
        engine.liveSink = { live.append(interleaved: $0, frames: $1, channels: $2) }
        liveTranscriber = live
        liveTranscript = LiveTranscriber.Snapshot(state: .loading)
        Task { [weak self] in
            for await snapshot in live.updates {
                guard let self, liveTranscriber === live else { return }
                liveTranscript = snapshot
            }
        }
        Task { await live.start() }
    }

    /// Disconnects and hides the current transcriber; the caller awaits its `stop()`, which unloads the model.
    private func detachLiveTranscript() -> LiveTranscriber? {
        engine.liveSink = nil
        let live = liveTranscriber
        liveTranscriber = nil
        liveTranscript = nil
        return live
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

    /// The language the next recording starts with, or the current one while recording.
    var recordingLanguage: String { languageOverride ?? settings.defaultLanguage }

    /// Before recording: the next session's language. While recording: a language marker at the current time.
    func setRecordingLanguage(_ language: String) {
        guard language != recordingLanguage else { return }
        languageOverride = language
        if isRecording { engine.setLanguage(language) }
        rememberLanguage(language)
    }

    /// Codes the engine accepts (`engineID` nil = the catalog default), sorted by name. Later engines only
    /// register in `TranscriptionPipeline.supportedLanguages(for:)`.
    func supportedLanguages(engineID: String? = nil) -> [String] {
        let entry = engineID.flatMap(ModelCatalog.entry(id:)) ?? ModelCatalog.defaultASR
        if let cached = languageCache[entry.id] { return cached }
        let codes = TranscriptionPipeline.supportedLanguages(for: entry)
            .map { (code: $0, name: LanguageOptions.name($0)) }
            .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
            .map(\.code)
        languageCache[entry.id] = codes
        return codes
    }

    /// Most recent first; Auto and the pinned languages are always listed, so they are not kept.
    private func rememberLanguage(_ language: String) {
        guard language != LanguageOptions.auto, !LanguageOptions.pinned.contains(language) else { return }
        updateSettings {
            let recent = [language] + $0.recentLanguages.filter { $0 != language }
            $0.recentLanguages = Array(recent.prefix(LanguageOptions.maxRecent))
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
                    languageOverride = nil
                    if let message = Self.stopMessage(reason) { recordingError = message }
                    let folder = activeFolder
                    activeFolder = nil
                    let live = detachLiveTranscript()
                    Task {
                        // The live model is unloaded first, so the batch model is never loaded next to it.
                        await live?.stop()
                        if let folder { self.compress([folder]) }
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
                transcribe(after: result)
            }
            compressionJobs -= 1
            await library.refresh()
        }
    }

    // MARK: - Transcription

    /// Queues the folder once compression is done; a failed compression leaves the CAF, which is used instead.
    /// Skipped when the folder has no usable audio or session, already has a transcript, or its last transcription
    /// failed (a compression that keeps failing is retried at every launch; the card's "Failed — Retry" retries).
    private func transcribe(after result: CompressionResult) {
        switch result.failure {
        case .sessionUnreadable?, .stillRecording?, .unsafeAudioFileName?, .sourceUnreadable?: return
        default: break
        }
        let conversation = try? Conversation.read(from: result.folder)
        guard conversation?.hasTranscript != true, conversation?.status != .transcriptionFailed else { return }
        submitTranscription(folder: result.folder)
    }

    /// The card's "Transcribe" and "Retry", and the transcript's "Re-transcribe" (`engineID` = a catalog id, nil =
    /// the default resolution; `language` replaces the recording's language markers, nil keeps them): queues the
    /// conversation unless a job for it is already active.
    func transcribe(_ id: UUID, engineID: String? = nil, language: String? = nil) {
        guard !isTranscribing(id), let conversation = conversations.first(where: { $0.id == id }) else { return }
        if let language { rememberLanguage(language) }
        submitTranscription(folder: conversation.folder, engineID: engineID, language: language)
    }

    /// Queued, deferred or running; a `needsModel` result is not active.
    func isTranscribing(_ id: UUID) -> Bool {
        guard let step = transcriptionProgress[id]?.step else { return false }
        return step != .needsModel
    }

    /// A failed recording is never transcribed: its audio is missing or empty.
    private func submitTranscription(folder: URL, engineID: String? = nil, language: String? = nil) {
        guard let session = try? RecordingSession.read(from: folder), !session.probe, session.state != .failed else {
            return
        }
        let job = TranscriptionJob(
            conversationID: session.id, folder: folder,
            systemFileName: session.audioFileName == AudioCompressor.micFileName ? AudioCompressor.systemFileName : nil,
            engineID: engineID, language: language)
        // Set now so the card leaves its button before the pipeline's first event arrives.
        transcriptionProgress[session.id] = JobProgress(conversationID: session.id, step: .queued, fraction: 0)
        transcriptionJobs += 1
        Task {
            do {
                _ = try await pipeline.transcribe(job)
            } catch {
                NSLog("EchoDiar: transcription failed for \(folder.lastPathComponent): \(error)")
            }
            transcriptionJobs -= 1
            await library.refresh()
        }
    }

    /// The single consumer of `pipeline.progress`; refreshes the library when a job changes step, so the card
    /// follows the `conversation.json` status the pipeline writes.
    private func observeTranscriptions() {
        Task { [weak self, pipeline] in
            for await progress in pipeline.progress {
                guard let self else { return }
                let previous = transcriptionProgress[progress.conversationID]?.step
                switch progress.step {
                case .finished, .failed: transcriptionProgress[progress.conversationID] = nil
                default: transcriptionProgress[progress.conversationID] = progress
                }
                if previous != progress.step { await library.refresh() }
            }
        }
    }

    // MARK: - Models

    /// Downloads a catalog entry; progress is published at most once per 0.5%.
    func downloadModel(_ id: String) {
        guard downloads[id] == nil else { return }
        downloadErrors[id] = nil
        let task = Task.detached(priority: .utility) { [modelStore] in
            let lastStep = OSAllocatedUnfairLock(initialState: -1)
            try await modelStore.download(id) { progress in
                let step = Int(progress.fraction * 200)
                guard lastStep.withLock({ last in
                    defer { last = max(last, step) }
                    return step > last
                }) else { return }
                Task { @MainActor in self.downloads[id]?.fraction = progress.fraction }
            }
        }
        downloads[id] = ModelDownload(task: task)
        Task {
            let result = await task.result
            downloads[id] = nil
            switch result {
            case .success:
                // Any installed WhisperKit entry can stand in for the missing default, so every card offers Transcribe.
                transcriptionProgress = transcriptionProgress.filter { $0.value.step != .needsModel }
            case .failure(let error) where error is CancellationError:
                break
            case .failure(let error):
                NSLog("EchoDiar: model download failed for \(id): \(error)")
                downloadErrors[id] = error.localizedDescription
            }
            refreshInstalledModels()
        }
    }

    func cancelDownload(_ id: String) {
        downloads[id]?.task.cancel()
    }

    func deleteModel(_ id: String) {
        Task {
            do {
                try await Task.detached(priority: .utility) { [modelStore] in try modelStore.delete(id) }.value
            } catch {
                NSLog("EchoDiar: could not delete model \(id): \(error)")
                downloadErrors[id] = error.localizedDescription
            }
            refreshInstalledModels()
        }
    }

    /// Reads the installed models and their total size off the main thread.
    func refreshInstalledModels() {
        Task {
            let (ids, bytes) = await Task.detached(priority: .utility) { [modelStore] in
                (Set(ModelCatalog.all.map(\.id).filter(modelStore.isInstalled)), modelStore.totalSize())
            }.value
            installedModelIDs = ids
            installedModelsBytes = bytes
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
        if openedConversationID == id { openedConversationID = nil }
        Task {
            do {
                try await library.delete(id: id)
            } catch {
                NSLog("EchoDiar: could not delete conversation \(id): \(error)")
            }
        }
    }

    // MARK: - Settings

    func openSettings(tab: SettingsTab) {
        settingsTab = tab
        SettingsWindow.shared.show(model: self)
    }

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

#if DEBUG
// MARK: - Model download and transcription harness

extension AppModel {
    /// `--transcribe <audio file> [--engine <catalog id>]`: copies the file into a temp session folder (mono =
    /// system channel), runs the pipeline in "Process now" mode, prints the `transcript.json` path and exits 0;
    /// exit 1 with a message on error. Progress goes to stderr. Returns false when the arg is absent.
    private func runTranscribeHarness() -> Bool {
        guard ProcessInfo.processInfo.arguments.contains("--transcribe") else { return false }
        guard let path = Self.harnessValue(after: "--transcribe") else {
            Self.finish(1, "usage: --transcribe <audio file> [--engine <catalog id>]")
        }
        let source = URL(fileURLWithPath: path)
        let session = RecordingSession(state: .stopped, audioFileName: "audio." + source.pathExtension.lowercased())
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("EchoDiar-transcribe-\(session.folderName)", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try FileManager.default.copyItem(at: source, to: folder.appendingPathComponent(session.audioFileName))
            try session.write(to: folder)
        } catch {
            try? FileManager.default.removeItem(at: folder)
            Self.finish(1, "error: \(error.localizedDescription)")
        }
        let pipeline = TranscriptionPipeline(modelStore: modelStore, deferWhenConstrained: false)
        let job = TranscriptionJob(
            conversationID: session.id, folder: folder, engineID: Self.harnessValue(after: "--engine"), processNow: true)
        Task.detached(priority: .utility) {
            var step: JobProgress.Step?
            for await progress in pipeline.progress where progress.step != step {
                step = progress.step
                FileHandle.standardError.write(Data("\(progress.step.rawValue)\n".utf8))
            }
        }
        Task {
            do {
                let url = try await pipeline.transcribe(job)
                Self.finish(0, url.path)
            } catch {
                try? FileManager.default.removeItem(at: folder)
                Self.finish(1, "error: \(error.localizedDescription)")
            }
        }
        return true
    }

    private static func harnessValue(after flag: String) -> String? {
        let arguments = ProcessInfo.processInfo.arguments
        guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
        return arguments[index + 1]
    }

    /// `--download-model <id> [--cancel-after <s>]` (exit 0 installed, 3 cancelled, 1 failed) and
    /// `--delete-model <id>` (exit 0, 1 failed). Returns false when neither arg is present.
    private func runModelHarness() -> Bool {
        let arguments = ProcessInfo.processInfo.arguments
        let store = modelStore
        if arguments.contains("--delete-model") {
            guard let id = Self.harnessValue(after: "--delete-model") else { Self.finish(1, "usage: --delete-model <id>") }
            do {
                try store.delete(id)
                Self.finish(0, "deleted \(id)")
            } catch {
                Self.finish(1, "error: \(error.localizedDescription)")
            }
        }
        guard arguments.contains("--download-model") else { return false }
        guard let id = Self.harnessValue(after: "--download-model") else {
            Self.finish(1, "usage: --download-model <id> [--cancel-after <seconds>]")
        }
        let cancelAfter = Self.harnessValue(after: "--cancel-after").flatMap(Double.init)
        Task {
            let download = Task.detached(priority: .userInitiated) {
                let lastPercent = OSAllocatedUnfairLock(initialState: -1)
                try await store.download(id) { progress in
                    let percent = Int(progress.fraction * 100)
                    guard lastPercent.withLock({ last in
                        defer { last = max(last, percent) }
                        return percent > last
                    }) else { return }
                    Self.write(String(
                        format: "progress %d%% %.1f/%.1f MiB",
                        percent, Double(progress.completedBytes) / 1_048_576, Double(progress.totalBytes) / 1_048_576))
                }
            }
            if let cancelAfter {
                Task {
                    try? await Task.sleep(for: .seconds(cancelAfter))
                    download.cancel()
                }
            }
            switch await download.result {
            case .success:
                guard store.isInstalled(id) else { Self.finish(1, "error: \(id) is incomplete after download") }
                let size = store.installedSize(id)
                Self.finish(0, String(format: "installed %@ %lld bytes (%.1f MiB)", id, size, Double(size) / 1_048_576))
            case .failure(let error) where error is CancellationError:
                try? FileManager.default.removeItem(at: store.partialFolder(id))
                Self.finish(3, "cancelled \(id)")
            case .failure(let error):
                Self.finish(1, "error: \(error.localizedDescription)")
            }
        }
        return true
    }

    nonisolated private static func write(_ line: String) {
        FileHandle.standardOutput.write(Data((line + "\n").utf8))
    }

    private static func finish(_ code: Int32, _ message: String) -> Never {
        write(message)
        exit(code)
    }
}
#endif
