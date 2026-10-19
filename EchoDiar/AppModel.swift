import AppKit
#if DEBUG
import AVFoundation
#endif
import EchoAI
import EchoAudio
import EchoCore
import EchoLocalLLM
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
    static let harnessArguments: Set<String> = [
        "--transcribe", "--download-model", "--delete-model", "--live-transcribe",
    ]

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
    /// Short reason of each conversation's last failed transcription ("Deepgram: invalid API key"), in memory only;
    /// cleared when the conversation is submitted again.
    private(set) var transcriptionErrors: [UUID: String] = [:]
    /// Cloud runs waiting for their destination's one-time "audio leaves this Mac" confirmation, oldest first.
    private(set) var pendingCloudRuns: [PendingCloudRun] = []
    /// Bumped when Settings saves or removes an API key, so views listing cloud engines (which read the Keychain)
    /// update.
    private(set) var keychainVersion = 0
    var settingsTab: SettingsTab = .general
    /// Running model downloads, by catalog id.
    private(set) var downloads: [String: ModelDownload] = [:]
    /// Last download error, by catalog id; cleared when a download of that id starts.
    private(set) var downloadErrors: [String: String] = [:]
    /// Downloaded models, plus Apple Speech when the system provides it.
    private(set) var installedModelIDs: Set<String> = []
    private(set) var installedModelsBytes: Int64 = 0
    /// Live captions of the current recording; nil when the setting was off at its start, or when not recording.
    private(set) var liveTranscript: LiveTranscriber.Snapshot?
    /// Apple Speech locales reserved by EchoDiar (BCP-47), read with the installed models.
    private(set) var appleSpeechReservedLocales: [String] = []
    /// The running summary or question; one at a time.
    private(set) var aiJob: AIJob?
    /// Why the last AI job of a conversation failed.
    private(set) var aiError: (conversationID: UUID, message: String)?
    /// Bumped when an AI job ends, so the AI tab reloads `summary.md` and `chat.json`.
    private(set) var aiJobsEnded = 0
    /// Where the AI tab scrolls to when a card opens it.
    var aiFocus: AIFocus = .chat
    /// Recording time of the last clicked citation, for the opened transcript.
    var pendingTranscriptTime: Double?

    struct PendingCloudRun {
        let providerID: String
        let providerName: String
        /// `CloudTranscription.destination(for:)` when the run was queued; the confirmation is stored under it.
        let consentKey: String
        let host: String
        let conversationID: UUID
        let folder: URL
        let language: String?
    }

    enum AIFocus { case summary, chat }

    struct AIJob {
        enum Kind: Equatable { case summary, question(String) }
        let conversationID: UUID
        let kind: Kind
        /// What a long summary is doing before its text starts.
        var status: String?
        /// The answer so far.
        var text = ""
        var task: Task<Void, Never>?
    }

    /// A provider the AI tab offers: an in-app model (LLM catalog entry id) or a configured `ProviderConfig`.
    struct AIProvider: Identifiable {
        enum Kind { case inApp, localHost, cloud, cli }
        let id: String
        let name: String
        let kind: Kind
        /// The config's model; the entry id for in-app models.
        let defaultModel: String
        /// Why it cannot run now (Foundation Models); nil when it can.
        var unavailableReason: String?
        var cliPreset: CLIPreset?
        /// A local host whose base URL is not a loopback address (another machine on the network).
        var remoteHost = false

        var leavesMac: Bool { kind == .cloud || kind == .cli || remoteHost }

        /// Whether `baseURL`'s host is this Mac: localhost, 127.0.0.0/8 or ::1.
        static func isLoopback(baseURL: String) -> Bool {
            guard let host = URL(string: baseURL.trimmingCharacters(in: .whitespaces))?.host()?.lowercased() else {
                return false
            }
            let bare = host.trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
            if bare == "localhost" || bare == "::1" { return true }
            let octets = bare.split(separator: ".", omittingEmptySubsequences: false)
            return octets.count == 4 && octets[0] == "127" && octets.allSatisfy { UInt8($0) != nil }
        }
    }

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
    /// The single "one heavy model at a time" lock, shared by the pipeline and live captions.
    @ObservationIgnored let modelGate = ModelLoadGate()
    @ObservationIgnored let pipeline: TranscriptionPipeline
    @ObservationIgnored let aiService: AIService
    @ObservationIgnored private let compressor = AudioCompressor()
    @ObservationIgnored private let store: SettingsStore
    @ObservationIgnored private var recovery: Task<[RecoveredResult], Never>?
    /// Folder of the session being recorded; compressed when the engine reports the end.
    @ObservationIgnored private var activeFolder: URL?
    @ObservationIgnored private var terminateObserver: (any NSObjectProtocol)?
    /// `supportedLanguages(engineID:)` by catalog id, sorted by name once.
    @ObservationIgnored private var languageCache: [String: [String]] = [:]
    @ObservationIgnored private var liveTranscriber: LiveTranscriber?
    @ObservationIgnored private var savedKeys: [String: Bool] = [:]

    init(store: SettingsStore = SettingsStore()) {
        self.store = store
        let engine = RecordingEngine()
        self.engine = engine
        snapshot = engine.currentSnapshot()
        let modelStore = ModelStore()
        self.modelStore = modelStore
        pipeline = TranscriptionPipeline(modelStore: modelStore, gate: modelGate)
        aiService = AIService(gate: modelGate)
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
        if runTranscribeHarness() || runLiveTranscribeHarness() || runModelHarness() { return }
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
        let live = LiveTranscriber(modelStore: modelStore, gate: modelGate, engineID: settings.liveTranscriptEngine) {
            [engine] in engine.currentLanguage()
        }
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
        // Apple Speech's list loads asynchronously: an early empty answer is not kept.
        if !codes.isEmpty { languageCache[entry.id] = codes }
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

    /// The card's "Transcribe" and "Retry", and the transcript's "Re-transcribe" (`engineID` = a catalog id or a cloud
    /// engine id, nil = the default resolution; `language` replaces the recording's language markers, nil keeps them): queues the
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

    /// A failed recording is never transcribed: its audio is missing or empty. `engineID` nil = the default set in
    /// the Models tab when usable for the recording's first language, else the pipeline's default resolution. A cloud
    /// engine (picked or default) runs only while its provider is configured, else the local resolution applies; the
    /// first run per provider and destination waits for the confirmation in the main window, which opens if closed.
    private func submitTranscription(folder: URL, engineID: String? = nil, language: String? = nil) {
        guard let session = try? RecordingSession.read(from: folder), !session.probe, session.state != .failed else {
            return
        }
        var engineID = engineID
        var cloud: CloudEngine?
        if let providerID = CloudTranscription.providerID(engineID: engineID ?? settings.defaultASRModelID) {
            if let config = cloudEngineConfigs.first(where: { $0.id == providerID }) {
                // Bound to the destination, so an edited base URL asks again; an invalid one never counts as confirmed.
                let destination = CloudTranscription.destination(for: config)
                guard let destination, settings.cloudSTTConfirmed.contains(destination.consentKey) else {
                    if let destination, !pendingCloudRuns.contains(where: { $0.folder == folder }) {
                        if pendingCloudRuns.isEmpty { showMainWindowIfHidden() }
                        pendingCloudRuns.append(PendingCloudRun(
                            providerID: providerID, providerName: config.displayName,
                            consentKey: destination.consentKey, host: destination.host, conversationID: session.id,
                            folder: folder, language: language))
                    }
                    return
                }
                cloud = makeCloudEngine(config)
                engineID = cloud?.engineID
            } else if CloudTranscription.providerID(engineID: engineID) != nil {
                engineID = nil
            }
        }
        // The Models tab's default only when usable for the first language, else the pipeline's default resolution.
        let firstLanguage = language ?? session.languageMarkers.min { $0.fileTime < $1.fileTime }?.language ?? "auto"
        let preferred = TranscriptionPipeline.preferredEngineID(
            settings.defaultASRModelID, language: firstLanguage,
            isUsable: { TranscriptionPipeline.isUsable($0, in: modelStore) })
        let job = TranscriptionJob(
            conversationID: session.id, folder: folder,
            systemFileName: session.audioFileName == AudioCompressor.micFileName ? AudioCompressor.systemFileName : nil,
            engineID: engineID ?? preferred, language: language)
        transcriptionErrors[session.id] = nil
        // Set now so the card leaves its button before the pipeline's first event arrives.
        transcriptionProgress[session.id] = JobProgress(conversationID: session.id, step: .queued, fraction: 0)
        transcriptionJobs += 1
        Task {
            do {
                _ = try await pipeline.transcribe(job, cloud: cloud)
            } catch TranscriptionError.cloud(let provider, let reason) {
                // The kind only ("HTTP 400", "invalid API key"): the reason can quote the provider's error body.
                let kind = reason.prefix { $0 != ":" }
                NSLog("EchoDiar: \(provider) transcription failed for \(folder.lastPathComponent): \(kind)")
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
                case .finished: transcriptionProgress[progress.conversationID] = nil
                case .failed:
                    transcriptionProgress[progress.conversationID] = nil
                    transcriptionErrors[progress.conversationID] = progress.detail
                default: transcriptionProgress[progress.conversationID] = progress
                }
                if previous != progress.step { await library.refresh() }
            }
        }
    }

    /// The main window's answer to the first pending cloud run's confirmation: every pending run for that
    /// destination is submitted (and the destination remembered) or dropped (the conversation keeps its Transcribe
    /// button). Runs whose conversation got a job meanwhile are skipped, so no second paid job starts. Submitting
    /// re-reads the config, so a base URL changed since the alert asks again.
    func resolvePendingCloudRuns(allowed: Bool) {
        guard let first = pendingCloudRuns.first else { return }
        let runs = pendingCloudRuns.filter { $0.consentKey == first.consentKey }
        pendingCloudRuns.removeAll { $0.consentKey == first.consentKey }
        guard allowed else { return }
        if !settings.cloudSTTConfirmed.contains(first.consentKey) {
            updateSettings { $0.cloudSTTConfirmed.append(first.consentKey) }
        }
        for run in runs where !isTranscribing(run.conversationID) {
            submitTranscription(
                folder: run.folder, engineID: CloudTranscription.engineID(providerID: run.providerID), language: run.language)
        }
    }

    /// Opens (or deminiaturizes) the main window so the cloud confirmation is seen, e.g. after a recording made
    /// from the menu bar.
    private func showMainWindowIfHidden() {
        guard let delegate = NSApp.delegate as? AppDelegate else { return }
        // The main window is the only one whose delegate is the AppDelegate.
        if !NSApp.windows.contains(where: { $0.delegate === delegate && $0.isVisible }) { delegate.showMainWindow() }
    }

    /// Installed batch ASR models, in catalog order.
    var installedBatchEngines: [ModelEntry] {
        ModelCatalog.asr.filter {
            !$0.liveOnly && TranscriptionPipeline.registeredRuntimes.contains($0.runtime) && installedModelIDs.contains($0.id)
        }
    }

    /// Enabled cloud transcription providers with a model, a base URL (custom endpoints) and a saved key.
    var cloudEngineConfigs: [ProviderConfig] {
        _ = keychainVersion
        return providerConfigs.filter { config in
            config.enabled && CloudTranscription.isComplete(config)
                && (!CloudTranscription.needsKey(config) || (try? KeychainStore.hasAPIKey(forProvider: config.id)) == true)
        }
    }

    func keychainChanged() {
        savedKeys = [:]
        keychainVersion += 1
    }

    /// Whether provider `id` has a key in the Keychain; cached until `keychainChanged()`, since the AI tab asks on
    /// every update of a streamed answer.
    func hasSavedKey(_ id: String) -> Bool {
        if let cached = savedKeys[id] { return cached }
        let saved = (try? KeychainStore.hasAPIKey(forProvider: id)) == true
        savedKeys[id] = saved
        return saved
    }

    /// "Deepgram (nova-3)"; the provider name alone when it picks the model.
    func cloudEngineName(_ config: ProviderConfig) -> String {
        let model = CloudTranscription.model(for: config)
        return model.isEmpty ? config.displayName : "\(config.displayName) (\(model))"
    }

    private func makeCloudEngine(_ config: ProviderConfig) -> CloudEngine {
        let apiKey = CloudTranscription.needsKey(config) ? try? KeychainStore.apiKey(forProvider: config.id) : nil
        return CloudEngine(
            engineID: CloudTranscription.engineID(providerID: config.id), name: config.displayName,
            model: CloudTranscription.model(for: config),
            transcriber: CloudTranscription.makeTranscriber(config: config, apiKey: apiKey))
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
            // Otherwise every new recording would stop at "Needs model".
            if settings.defaultASRModelID == id { updateSettings { $0.defaultASRModelID = nil } }
            refreshInstalledModels()
        }
    }

    /// Reads the installed models, their total size and Apple Speech's reserved locales off the main thread.
    func refreshInstalledModels() {
        Task {
            let (ids, bytes) = await Task.detached(priority: .utility) { [modelStore] in
                let usable = ModelCatalog.all.filter { TranscriptionPipeline.isUsable($0, in: modelStore) }
                return (Set(usable.map(\.id)), modelStore.totalSize())
            }.value
            installedModelIDs = ids
            installedModelsBytes = bytes
            if #available(macOS 26, *), PlatformFeatures.speechAnalyzer {
                appleSpeechReservedLocales = await AppleSpeechEngine.reservedLocaleIdentifiers()
            }
        }
    }

    /// The ASR model new recordings are transcribed with: the one set in the Models tab (a catalog or cloud engine id),
    /// else the catalog default.
    var defaultASRModelID: String { settings.defaultASRModelID ?? ModelCatalog.defaultASR.id }

    /// Picking the catalog default stores nil, so the pipeline's default resolution applies again.
    func setDefaultASRModel(_ id: String) {
        updateSettings { $0.defaultASRModelID = id == ModelCatalog.defaultASR.id ? nil : id }
    }

    /// Releases EchoDiar's Apple Speech locale reservations; macOS removes the shared assets later.
    func releaseAppleSpeechAssets() {
        guard #available(macOS 26, *), PlatformFeatures.speechAnalyzer else { return }
        Task {
            await AppleSpeechEngine.releaseAssets()
            refreshInstalledModels()
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

    // MARK: - AI providers

    /// Every preset (with its saved changes), then the custom endpoints, in that order.
    var providerConfigs: [ProviderConfig] {
        let saved = Dictionary(settings.providers.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        return ProviderConfig.presets.map { saved[$0.id] ?? $0 } + settings.providers.filter { $0.preset == nil }
    }

    /// Applies `change` to the saved config `id` (or its preset, which is then saved whole) and saves the settings.
    func updateProvider(id: String, _ change: (inout ProviderConfig) -> Void) {
        guard var config = settings.providers.first(where: { $0.id == id })
            ?? ProviderConfig.presets.first(where: { $0.id == id }) else { return }
        change(&config)
        updateSettings { settings in
            if let index = settings.providers.firstIndex(where: { $0.id == id }) {
                settings.providers[index] = config
            } else {
                settings.providers.append(config)
            }
        }
    }

    /// Removes the saved config (a preset goes back to its defaults) and its Keychain key.
    func removeProvider(id: String) {
        updateSettings { $0.providers.removeAll { $0.id == id } }
        try? KeychainStore.deleteAPIKey(forProvider: id)
        savedKeys[id] = nil
    }

    /// The client for `config`, with its Keychain key; nil for an unknown CLI preset.
    nonisolated func makeProvider(for config: ProviderConfig) -> (any LLMProvider)? {
        let apiKey = config.authStyle == .none ? nil : try? KeychainStore.apiKey(forProvider: config.id)
        switch config.kind {
        case .cli:
            guard let preset = CLIPreset(rawValue: config.preset ?? "") else { return nil }
            return CLIRunner(preset: preset, executableOverride: config.baseURL)
        case .anthropic:
            return AnthropicClient(config: config, apiKey: apiKey)
        case .openAICompatible, .local:
            return OpenAICompatibleClient(config: config, apiKey: apiKey)
        case .speechToText:
            return nil
        }
    }

    /// The in-app model of LLM catalog entry `entryID`; nil when it is not installed or needs macOS 26. Callers run
    /// it inside `modelGate` (owner `.llm`); Foundation Models reports why it is unavailable when streaming.
    nonisolated func makeLocalProvider(entryID: String) -> (any LLMProvider)? {
        guard let entry = ModelCatalog.entry(id: entryID), entry.kind == .llm else { return nil }
        switch entry.runtime {
        case .foundationModels:
            guard #available(macOS 26, *), PlatformFeatures.foundationModels else { return nil }
            return AppleFoundationModelProvider()
        case .mlx:
            guard modelStore.isInstalled(entryID), let folder = modelStore.folder(for: entryID) else { return nil }
            return MLXProvider(entryID: entryID, folder: folder)
        default:
            return nil
        }
    }
}

// MARK: - AI tab

extension AppModel {
    /// Apple Foundation Models (with its unavailable reason), installed MLX models, then the enabled providers with
    /// what they need (base URL, saved key).
    var aiProviders: [AIProvider] {
        _ = keychainVersion
        var providers: [AIProvider] = []
        for entry in ModelCatalog.llm {
            if entry.runtime == .foundationModels {
                providers.append(AIProvider(
                    id: entry.id, name: entry.displayName, kind: .inApp, defaultModel: entry.id,
                    unavailableReason: Self.foundationModelsUnavailableReason))
            } else if entry.runtime == .mlx, installedModelIDs.contains(entry.id) {
                providers.append(AIProvider(id: entry.id, name: entry.displayName, kind: .inApp, defaultModel: entry.id))
            }
        }
        for config in providerConfigs where config.enabled && config.kind != .speechToText {
            let kind: AIProvider.Kind
            switch config.kind {
            case .cli: kind = .cli
            case .local: kind = .localHost
            default: kind = .cloud
            }
            if kind != .cli {
                guard !config.baseURL.trimmingCharacters(in: .whitespaces).isEmpty,
                      config.authStyle == .none || hasSavedKey(config.id) else { continue }
            }
            providers.append(AIProvider(
                id: config.id, name: config.displayName, kind: kind, defaultModel: config.defaultModel,
                cliPreset: kind == .cli ? CLIPreset(rawValue: config.preset ?? "") : nil,
                remoteHost: kind == .localHost && !AIProvider.isLoopback(baseURL: config.baseURL)))
        }
        return providers
    }

    static var foundationModelsUnavailableReason: String? {
        if #available(macOS 26, *) { return AppleFoundationModelProvider.unavailableReason }
        return "Apple Foundation Models need macOS 26."
    }

    /// The default provider when it can run, else the first one that can.
    var currentAIProvider: AIProvider? {
        let usable = aiProviders.filter { $0.unavailableReason == nil }
        return usable.first { $0.id == settings.defaultAIProviderID } ?? usable.first
    }

    /// The saved model for the default provider, else the provider's own.
    func aiModel(for provider: AIProvider) -> String {
        if provider.id == settings.defaultAIProviderID, let model = settings.defaultAIModel, !model.isEmpty { return model }
        return provider.defaultModel
    }

    func setAIProvider(_ id: String) {
        updateSettings {
            $0.defaultAIProviderID = id
            $0.defaultAIModel = nil
        }
    }

    func showAI(conversationID: UUID, focus: AIFocus) {
        selectedConversationID = conversationID
        aiFocus = focus
        selectedTab = .ai
    }

    /// Shows the conversation's transcript and keeps `time` for it to scroll and seek to.
    func openTranscript(conversationID: UUID, at time: Double) {
        pendingTranscriptTime = time
        openedConversationID = conversationID
        selectedTab = .library
    }

    /// Starts a summary or a question with `provider` and `model` (saved as the AI tab default). `summary` and
    /// `history` are the conversation's saved summary and chat, sent with a question.
    func startAIJob(
        _ kind: AIJob.Kind, conversation: Conversation, transcript: Transcript, summary: String?,
        history: [ChatLog.Message], provider: AIProvider, model: String
    ) {
        guard aiJob == nil else { return }
        let model = model.trimmingCharacters(in: .whitespacesAndNewlines)
        updateSettings {
            $0.defaultAIProviderID = provider.id
            $0.defaultAIModel = model == provider.defaultModel ? nil : model
        }
        aiError = nil
        let client = provider.kind == .inApp
            ? makeLocalProvider(entryID: provider.id)
            : providerConfigs.first { $0.id == provider.id }.flatMap(makeProvider(for:))
        guard let client else {
            aiError = (conversation.id, "\(provider.name) is not available.")
            return
        }
        let stream = switch kind {
        case .summary:
            aiService.summarize(
                conversation: conversation, transcript: transcript, provider: client, model: model,
                language: settings.summaryLanguage)
        case .question(let question):
            aiService.ask(
                question: question, conversation: conversation, transcript: transcript, summary: summary,
                history: history, provider: client, providerID: provider.id, model: model)
        }
        let task = Task { [weak self] in
            do {
                for try await update in stream {
                    switch update {
                    case .status(let status): self?.aiJob?.status = status
                    case .text(let text):
                        self?.aiJob?.status = nil
                        self?.aiJob?.text += text
                    }
                }
            } catch where !Task.isCancelled {
                self?.aiError = (conversation.id, error.localizedDescription)
            } catch {}
            guard let self else { return }
            aiJob = nil
            aiJobsEnded += 1
            await library.refresh()
        }
        aiJob = AIJob(conversationID: conversation.id, kind: kind, task: task)
    }

    func cancelAIJob() {
        aiJob?.task?.cancel()
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
        let pipeline = TranscriptionPipeline(modelStore: modelStore, gate: modelGate, deferWhenConstrained: false)
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

    /// `--live-transcribe <audio file> [--engine <catalog id>] [--language <code>]`: once the live model listens,
    /// streams the file (system channel, mic silent) through a `LiveTranscriber` at real-time pace, then 12 s of
    /// silence so its last window is decoded, prints every line and exits 0; exit 1 when no live model is installed
    /// or loading fails. `--engine` defaults to the settings' live engine, `--language` to Auto.
    private func runLiveTranscribeHarness() -> Bool {
        guard ProcessInfo.processInfo.arguments.contains("--live-transcribe") else { return false }
        guard let path = Self.harnessValue(after: "--live-transcribe") else {
            Self.finish(1, "usage: --live-transcribe <audio file> [--engine <catalog id>] [--language <code>]")
        }
        let samples: [Float]
        do {
            samples = try Self.monoSamples(of: URL(fileURLWithPath: path))
        } catch {
            Self.finish(1, "error: \(error.localizedDescription)")
        }
        let engineID = Self.harnessValue(after: "--engine") ?? settings.liveTranscriptEngine
        let language = Self.harnessValue(after: "--language")
        let live = LiveTranscriber(modelStore: modelStore, gate: modelGate, engineID: engineID) { language }
        Task {
            await live.start()
            var printed = 0
            var feeder: Task<Void, Never>?
            let started = Date()
            for await snapshot in live.updates {
                for line in snapshot.lines where line.id >= printed {
                    Self.write(String(format: "%5.1fs %@: %@", -started.timeIntervalSinceNow, line.speaker.rawValue, line.text))
                    printed = line.id + 1
                }
                switch snapshot.state {
                case .noModel: Self.finish(1, "error: no live model installed")
                case .failed(let message): Self.finish(1, "error: \(message)")
                case .listening where feeder == nil:
                    Self.write(String(format: "listening after %.1fs", -started.timeIntervalSinceNow))
                    feeder = Task.detached(priority: .utility) { await Self.feed(samples + [Float](repeating: 0, count: 12 * 16_000), to: live) }
                default: break
                }
            }
            Self.finish(0, String(format: "done after %.1fs: %d lines", -started.timeIntervalSinceNow, printed))
        }
        return true
    }

    /// 100 ms interleaved Int16 stereo blocks (mic silent) every 100 ms, then a short wait for the last decode.
    nonisolated private static func feed(_ samples: [Float], to live: LiveTranscriber) async {
        let block = 1_600
        for start in stride(from: 0, to: samples.count, by: block) {
            let frames = min(block, samples.count - start)
            var interleaved = [Int16](repeating: 0, count: frames * 2)
            for frame in 0..<frames {
                let value = max(-1, min(1, samples[start + frame])) * Float(Int16.max)
                interleaved[frame * 2 + RecordingSession.Channel.system] = Int16(value)
            }
            interleaved.withUnsafeBufferPointer { live.append(interleaved: $0.baseAddress!, frames: frames, channels: 2) }
            try? await Task.sleep(for: .milliseconds(100))
        }
        try? await Task.sleep(for: .seconds(5))
        await live.stop()
    }

    /// The file as 16 kHz mono Float32.
    nonisolated private static func monoSamples(of url: URL) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        guard let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: file.processingFormat, to: format),
              let input = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)),
              let output = AVAudioPCMBuffer(
                  pcmFormat: format,
                  frameCapacity: AVAudioFrameCount(Double(file.length) * 16_000 / file.processingFormat.sampleRate) + 4_096)
        else { throw CocoaError(.fileReadCorruptFile) }
        try file.read(into: input)
        converter.downmix = true
        var consumed = false
        var conversionError: NSError?
        converter.convert(to: output, error: &conversionError) { _, status in
            guard !consumed else {
                status.pointee = .endOfStream
                return nil
            }
            consumed = true
            status.pointee = .haveData
            return input
        }
        if let conversionError { throw conversionError }
        return Array(UnsafeBufferPointer(start: output.floatChannelData![0], count: Int(output.frameLength)))
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
