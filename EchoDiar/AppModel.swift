import AppKit
import CoreAudio
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

/// App-wide state shared by the main window (Settings included) and the menu bar item.
/// Coordinates recording: engine → stop → compress → transcribe → library refresh.
@MainActor
@Observable
final class AppModel {
    /// What the main window shows: the cards, one conversation, the open recording (only while recording) or
    /// Settings.
    enum Route: Equatable { case list, conversation(UUID), recording, settings }

    /// The open conversation's tab.
    enum DetailTab { case transcript, summary, chat }

    /// DEBUG harness launch args handled here; they skip onboarding and auto-queued work.
    static let harnessArguments: Set<String> = [
        "--transcribe", "--download-model", "--delete-model", "--live-transcribe",
    ]

    private(set) var settings = AppSettings()
    var route: Route = .list
    var detailTab: DetailTab = .transcript
    /// Where Back leaves Settings to: the route Settings was opened from.
    private(set) var settingsReturnRoute: Route = .list
    /// The main window is on screen (not closed, minimized or covered); timers and scans run only then.
    var mainWindowVisible = false
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
    /// The inner tab (its id in `SettingsTab.subTabs`) each Settings section shows; a section missing here shows its
    /// first.
    var settingsSubTabs: [SettingsTab: String] = [:]
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
    /// The running summary or question; one at a time. Also set by the library chat (LibraryChatModel.swift).
    var aiJob: AIJob?
    /// Why the last AI job, or chat archive or resume, of a conversation (nil: the library chat) failed.
    var aiError: (conversationID: UUID?, message: String)?
    /// Bumped when an AI job ends, so the AI tab reloads `summary.md` and `chat.json`.
    var aiJobsEnded = 0
    /// Bumped when a conversation's chat or archived chats change outside a question job, so the AI tab reloads them.
    private(set) var chatHistoryChanges = 0
    /// Bumped per conversation when a speaker is renamed in `transcript.json`, so search rereads that document.
    private(set) var transcriptEdits: [UUID: Int] = [:]
    /// Recording time of the last clicked citation, for the opened transcript.
    var pendingTranscriptTime: Double?
    /// The meeting app whose mic use was detected last (menu "Detected: Zoom"); nil once its mic is off.
    private(set) var detectedMeeting: DetectedMeeting?
    /// What the floating pill asks; nil = no question.
    private(set) var pillPrompt: PillPrompt?
    /// The app being recorded (`.app` targets), for the end prompt and the window anchor.
    private(set) var sourceApp: SourceApp?
    /// The app whose detection ("Always") started the recording: the pill shows "Recording <App>" for its first
    /// 10 s even when the indicator is hidden.
    private(set) var autoStartedAppName: String?
    /// Why the global record shortcut could not be registered; nil when registered or off.
    private(set) var hotKeyError: String?
    /// ⌘F asks the main window's search field to take the focus; false again once it stops editing.
    var searchPresented = false
    /// Cards are selected in the list: Esc cancels the selection instead of going back.
    var listSelectionActive = false
    /// The library chat is shown: in place of the cards on the list, or of the sidebar beside a conversation.
    var globalChatOpen = false
    /// What the library index (RAG) is doing, for Settings › RAG.
    var ragStatus = RAGStatus()

    enum PillPrompt: Equatable {
        case meetingDetected(DetectedMeeting)
        /// The recording stops at `deadline` unless "Keep recording".
        case meetingEnded(reason: MeetingEndMonitor.Reason, deadline: Date)
    }

    struct SourceApp: Equatable {
        let name: String
        /// `MeetingApp.appBundleID` for meeting apps, else the group's id; its quit ends the meeting.
        let appBundleID: String
    }

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

    struct AIJob {
        enum Kind: Equatable { case summary, question(String) }
        /// nil for a question across the library.
        let conversationID: UUID?
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
        /// What the model menu offers: `defaultModel` then the config's `shownModels`; "" is a CLI's own default.
        var models: [String] = []
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

    /// Conversations indexed out of those with a transcript, and why indexing waits or stopped.
    struct RAGStatus: Equatable {
        var indexed = 0
        var total = 0
        var running = false
        /// Waiting for a recording, compression, transcription or AI job to end.
        var paused = false
        /// Why the last pass could not run or stopped (missing model, error).
        var message: String?
        /// A cloud embedder waits for its one-time confirmation; nothing is sent meanwhile.
        var needsConsent = false
    }

    var indicatorState: IndicatorState {
        switch snapshot.state {
        case .recording: .recording
        case .paused: .paused
        case .idle: compressionJobs > 0 || transcriptionJobs > 0 ? .processing : .idle
        }
    }

    var isRecording: Bool { snapshot.state != .idle }

    /// Library indexing waits while any of these runs, so it never competes with them for the model gate or the CPU.
    var indexingPaused: Bool { isRecording || compressionJobs > 0 || transcriptionJobs > 0 || aiJob != nil }

    /// Live captions hold the model gate for a whole recording and a transcription job until it ends: a library
    /// question then skips an in-app embedder (keyword search only) instead of waiting for the gate.
    var modelGateBusy: Bool { isRecording || transcriptionJobs > 0 }

    /// Its `currentLevels()` has a single consumer: `levels`.
    @ObservationIgnored let engine: RecordingEngine
    @ObservationIgnored let levels: LevelFeed
    @ObservationIgnored let library = LibraryStore()
    /// Created with the model so FluidAudio's offline mode is set before any model load.
    @ObservationIgnored let modelStore: ModelStore
    /// The single "one heavy model at a time" lock, shared by the pipeline and live captions.
    @ObservationIgnored let modelGate = ModelLoadGate()
    @ObservationIgnored let pipeline: TranscriptionPipeline
    @ObservationIgnored let aiService: AIService
    /// The RAG index, in a folder of its own beside the conversations: it deletes the other folders inside it.
    @ObservationIgnored let libraryIndex = LibraryIndex(
        root: RecordingSession.conversationsRoot.deletingLastPathComponent().appendingPathComponent("RAG", isDirectory: true))
    /// The running `syncLibraryIndex()` pass; another pass asked for meanwhile runs once it ends.
    @ObservationIgnored var libraryIndexTask: Task<Void, Never>?
    @ObservationIgnored var libraryIndexPending = false
    /// The embedder key last loaded into `libraryIndex`.
    @ObservationIgnored var libraryIndexKey: String?
    /// The next pass deletes the current key's shards first (Rebuild).
    @ObservationIgnored var libraryIndexRebuild = false
    /// `library.changes()` emitted once: an empty `conversations` is then an empty library, not one still loading.
    @ObservationIgnored var libraryLoaded = false
    @ObservationIgnored private let compressor = AudioCompressor()
    @ObservationIgnored private let store: SettingsStore
    @ObservationIgnored private var recovery: Task<[RecoveredResult], Never>?
    /// Folder of the session being recorded; compressed when the engine reports the end.
    @ObservationIgnored private var activeFolder: URL?
    /// Folder of the recording that ended on its page; opened once the library lists it, dropped if the list is left.
    @ObservationIgnored private var pendingOpenFolder: URL?
    @ObservationIgnored private var terminateObserver: (any NSObjectProtocol)?
    /// `supportedLanguages(engineID:)` by catalog id, sorted by name once.
    @ObservationIgnored private var languageCache: [String: [String]] = [:]
    @ObservationIgnored private var liveTranscriber: LiveTranscriber?
    @ObservationIgnored private var savedKeys: [String: Bool] = [:]
    @ObservationIgnored private let meetingDetector = MeetingDetector()
    @ObservationIgnored private let systemEvents = SystemEventsMonitor()
    /// Created on the normal launch path only (not for the DEBUG harnesses).
    @ObservationIgnored private var pill: FloatingPillController?
    @ObservationIgnored private var endWatch: EndWatch?
    @ObservationIgnored private var endWatchGeneration = 0
    /// Bundle IDs of the meeting apps whose mic is on, per the detector (every known app, ignored ones too).
    @ObservationIgnored private var meetingMics: Set<String> = []
    @ObservationIgnored private var autoStartNoticeTask: Task<Void, Never>?
    /// Created on the normal launch path only (not for the DEBUG harnesses).
    @ObservationIgnored private var hotKey: GlobalHotKey?
    /// The Shortcuts tab is recording a new shortcut: the current one is unregistered meanwhile.
    @ObservationIgnored private var hotKeyCapturing = false
    /// The Shortcuts tab is recording a new shortcut: the main window's Esc and Space go to it.
    var isCapturingHotKey: Bool { hotKeyCapturing }

    init(store: SettingsStore = SettingsStore()) {
        self.store = store
        let engine = RecordingEngine()
        self.engine = engine
        levels = LevelFeed(engine: engine)
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
            setLanguage: { [weak self] in self?.setRecordingLanguage($0) },
            detectedApp: { [weak self] in self?.detectedMeeting?.name },
            stopAskingForDetectedApp: { [weak self] in self?.stopAskingForDetectedApp() })

        #if DEBUG
        if runTranscribeHarness() || runLiveTranscribeHarness() || runModelHarness() { return }
        DebugLibrarySeed.run()
        #endif
        guard ProcessInfo.processInfo.arguments.allSatisfy({ !Self.harnessArguments.contains($0) }) else { return }
        if !settings.onboardingCompleted { OnboardingWindow.shared.show(model: self) }
        hotKey = GlobalHotKey { [weak self] in self?.toggleRecording() }
        syncHotKey()
        observeTranscriptions()
        startMeetingServices()
        Task {
            // Subscribed before the first refresh, whose emit would otherwise be missed.
            let changes = await library.changes()
            Task {
                for await conversations in changes {
                    self.conversations = conversations
                    libraryLoaded = true
                    syncLibraryIndex()
                    guard let pending = pendingOpenFolder else { continue }
                    if route != .list {
                        pendingOpenFolder = nil
                    } else if let match = conversations.first(where: {
                        $0.folder.standardizedFileURL == pending.standardizedFileURL
                    }) {
                        pendingOpenFolder = nil
                        open(match.id)
                    }
                }
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
            syncRetention()
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
                let (folder, resolved) = try await Task.detached(priority: .userInitiated) {
                    [engine, uid = settings.micDeviceUID] in
                    let resolved = Self.resolved(target)
                    return (try engine.start(target: resolved, language: language, micDeviceUID: uid), resolved)
                }.value
                activeFolder = folder
                beginEndWatch(target: resolved)
            } catch {
                // Not awaited: a first model load can take long, and no batch job follows a failed start.
                let live = detachLiveTranscript()
                Task { await live?.stop() }
                NSLog("EchoDiar: could not start recording: \(error)")
                autoStartedAppName = nil
                recordingError = Self.captureFailureMessage(error, action: "start")
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
                recordingError = Self.captureFailureMessage(failure, action: "resume")
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

    /// A picked app gets its current processes, which may have changed since the menu was built. On a miss the menu's
    /// group is kept: `ProcessTap` skips its exited processes and throws `noProcesses` when none is left.
    nonisolated private static func resolved(_ target: CaptureTarget) -> CaptureTarget {
        guard case .app(let group) = target else { return target }
        let groups = AudioProcessList.groups()
        guard let fresh = groups.first(where: { $0.bundleID == group.bundleID }) else {
            NSLog("EchoDiar: \(group.bundleID) has no current audio process")
            return target
        }
        return .app(fresh)
    }

    /// The single consumer of `engine.snapshots`. A snapshot with an end reason closes the session,
    /// whether the user or the engine (disk full, write failure) stopped it.
    private func observeSnapshots() {
        Task { [weak self, engine] in
            for await snapshot in engine.snapshots {
                guard let self else { return }
                let wasRecording = isRecording
                self.snapshot = snapshot
                // A start does not navigate. An end on the recording page shows the list, then the finished entry
                // once the library lists it; Settings opened from that page returns to the list.
                if wasRecording && !isRecording {
                    if route == .recording {
                        pendingOpenFolder = activeFolder
                        route = .list
                    } else if route == .settings && settingsReturnRoute == .recording {
                        settingsReturnRoute = .list
                    }
                }
                micRoutesLost(snapshot.micRoutesLost)
                if snapshot.state == .idle, let reason = snapshot.endReason {
                    endEndWatch()
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

    /// A readable sentence for a failed start or resume (`action`), with the error itself in parentheses.
    private static func captureFailureMessage(_ error: any Error, action: String) -> String {
        let sentence: String
        switch error {
        case ProcessTapError.noProcesses:
            sentence = "The selected app stopped playing audio before recording could \(action)."
        case RecordingEngineError.diskFull:
            sentence = "There is not enough free disk space to record."
        case ProcessTapError.unsupportedFormat:
            sentence = "The system audio format is not supported."
        case let error as ProcessTapError:
            sentence = coreAudioSentence(error, action: action)
        default:
            sentence = "The recording could not \(action)."
        }
        return "\(sentence) (\(error))"
    }

    private static func coreAudioSentence(_ error: ProcessTapError, action: String) -> String {
        let status: OSStatus
        switch error {
        case .createTap(let code), .createAggregateDevice(let code), .addFormatListener(let code),
             .readFormat(let code), .createIOProc(let code), .startDevice(let code):
            status = code
        case .noProcesses, .unsupportedFormat:
            return "The recording could not \(action)."
        }
        switch status {
        case kAudioHardwareBadObjectError:
            return "The selected app stopped playing audio before recording could \(action)."
        case kAudioHardwareNotRunningError:
            return "The audio system is not running. Try again in a moment."
        case kAudioHardwareBadDeviceError:
            return "The audio device changed while recording was starting. Try again."
        case kAudioDevicePermissionsError:
            return "Another app is using the audio device exclusively."
        case kAudioHardwareIllegalOperationError:
            return "macOS refused to capture system audio."
        default:
            return "The recording could not \(action)."
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

    /// Queues the folder once compression is done, when "auto-transcribe after stop" is on; a failed compression
    /// leaves the CAF, which is used instead.
    /// Skipped when the folder has no usable audio or session, already has a transcript, or its last transcription
    /// failed (a compression that keeps failing is retried at every launch; the card's "Failed — Retry" retries).
    private func transcribe(after result: CompressionResult) {
        guard settings.autoTranscribe else { return }
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
        guard !isTranscribing(id), let conversation = conversations.first(where: { $0.id == id }),
              conversation.hasAudio else { return }
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
            engineID: engineID ?? preferred, language: language,
            // Per job rather than a re-created pipeline: the pipeline and its progress stream live as long as the app.
            processNow: !settings.deferTranscriptionWhenConstrained, speakerCountHint: settings.speakerCountHint)
        transcriptionErrors[session.id] = nil
        // Set now so the card leaves its button before the pipeline's first event arrives.
        transcriptionProgress[session.id] = JobProgress(conversationID: session.id, step: .queued, fraction: 0)
        transcriptionJobs += 1
        Task {
            do {
                _ = try await pipeline.transcribe(job, cloud: cloud)
                if settings.deleteAudioAfterTranscription { await deleteAudio(of: session.id) }
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
        // A running pass would keep sending with the old key.
        if settings.embeddingModelID.hasPrefix(Self.apiEmbedderPrefix) { syncLibraryIndex(restart: true) }
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
                // The embedding model may be the one indexing waited for.
                if ModelCatalog.entry(id: id)?.kind == .embedding { syncLibraryIndex() }
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
            // Indexing then says the embedding model is missing.
            if ModelCatalog.entry(id: id)?.kind == .embedding { syncLibraryIndex() }
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

    func transcriptEdited(_ id: UUID) {
        transcriptEdits[id, default: 0] += 1
        syncLibraryIndex()
    }

    func renameConversation(_ id: UUID, to title: String) {
        Task {
            do {
                try await library.rename(id: id, title: title)
            } catch {
                NSLog("EchoDiar: could not rename conversation \(id): \(error)")
            }
        }
    }

    /// Moves the conversation to the Trash, cancelling its AI job first so it does not write into the trashed folder.
    func deleteConversation(_ id: UUID) {
        if route == .conversation(id) { route = .list }
        if aiJob?.conversationID == id { aiJob?.task?.cancel() }
        Task {
            do {
                try await library.delete(id: id)
            } catch {
                NSLog("EchoDiar: could not delete conversation \(id): \(error)")
            }
        }
    }

    /// Moves the conversation's audio to the Trash; its transcript, summary and chat stay.
    func deleteAudio(of id: UUID) async {
        do {
            try await library.deleteAudio(id: id)
        } catch {
            NSLog("EchoDiar: could not delete the audio of conversation \(id): \(error)")
        }
    }

    // MARK: - Auto-delete

    /// Conversations auto-delete would move to the Trash now with `settings` (Storage tab preview), from the cached
    /// library (no rescan); off the main thread, since audio only lists each candidate's folder.
    func retentionPreview(settings: AppSettings) async -> [RetentionPolicy.Deletion] {
        let busy = busyConversationIDs
        let candidates = conversations.filter { !busy.contains($0.id) }
        return await Task.detached(priority: .utility) {
            RetentionPolicy.select(conversations: candidates, now: Date(), settings: settings)
        }.value
    }

    /// Bytes on disk by kind and the largest conversations (Storage tab).
    func storageUsage() async -> LibraryStore.Usage {
        await library.usage { [modelStore] in modelStore.totalSize() }
    }

    /// Conversations with a queued or running transcription, a pending cloud confirmation, or an AI job.
    private var busyConversationIDs: Set<UUID> {
        var ids = Set(transcriptionProgress.keys).union(pendingCloudRuns.map(\.conversationID))
        if let id = aiJob?.conversationID { ids.insert(id) }
        return ids
    }

    /// Auto-delete on: schedules the daily run and runs it once now (launch, or when turned on). Off: stops it.
    /// Reads the setting when it runs, so quick toggles end in the last state.
    private func syncRetention() {
        Task { [self] in
            guard settings.autoDeleteEnabled else {
                await library.stopRetentionSchedule()
                return
            }
            await library.startRetentionSchedule { [weak self] in await self?.runRetention() }
            await runRetention()
        }
    }

    private func runRetention() async {
        guard settings.autoDeleteEnabled else { return }
        let done = await library.applyRetention(settings: settings) { [weak self] id in
            await self?.isBusy(id) ?? true
        }
        for deletion in done where deletion.mode == .wholeConversation {
            if route == .conversation(deletion.conversationID) { route = .list }
        }
    }

    /// Asked by auto-delete right before each deletion.
    private func isBusy(_ id: UUID) -> Bool {
        busyConversationIDs.contains(id)
    }

    /// Storage tab: moves the audio of these conversations to the Trash, skipping any that got a job meanwhile.
    func deleteAudio(of ids: [UUID]) async {
        for id in ids where !isBusy(id) { await deleteAudio(of: id) }
    }

    /// Storage tab: moves the transcripts, summaries and chats of these conversations to the Trash (the whole
    /// conversation when its audio is gone), skipping any that got a job meanwhile.
    func deleteTranscriptsAndAI(of ids: [UUID]) async {
        for id in ids where !isBusy(id) {
            let whole = conversations.first { $0.id == id }?.hasAudio == false
            if whole, route == .conversation(id) { route = .list }
            do {
                try await library.deleteTranscriptsAndAI(id: id)
            } catch {
                NSLog("EchoDiar: could not delete the transcript of conversation \(id): \(error)")
            }
        }
    }

    /// Storage tab: the downloaded models (not the built-in ones, nor one still downloading), which "Delete all"
    /// removes.
    var deletableModelIDs: [String] {
        ModelCatalog.all.filter { $0.repo != nil && installedModelIDs.contains($0.id) && downloads[$0.id] == nil }
            .map(\.id)
    }

    /// Storage tab: removes every downloaded model.
    func deleteAllModels() {
        for id in deletableModelIDs { deleteModel(id) }
    }

    // MARK: - Settings

    /// The Settings page of the main window, on `tab` when given; Back returns to the page it was opened from.
    func openSettings(tab: SettingsTab? = nil) {
        if let tab { settingsTab = tab }
        if route != .settings { settingsReturnRoute = route }
        route = .settings
        (NSApp.delegate as? AppDelegate)?.showMainWindow()
    }

    /// `openSettings(tab:)` on the inner tab `subTab` of that section (a search result).
    func openSettings(tab: SettingsTab?, subTab: String?) {
        openSettings(tab: tab)
        if let subTab { settingsSubTabs[settingsTab] = subTab }
    }

    /// The inner tab `tab` shows: the one picked last, else its first; nil for a section without tabs.
    func settingsSubTab(_ tab: SettingsTab) -> String? {
        settingsSubTabs[tab] ?? tab.subTabs.first?.id
    }

    /// Applies `change` and saves the settings file.
    func updateSettings(_ change: (inout AppSettings) -> Void) {
        var updated = settings
        change(&updated)
        guard updated != settings else { return }
        let detectionChanged = updated.meetingDetection != settings.meetingDetection
        let retentionChanged = updated.autoDeleteEnabled != settings.autoDeleteEnabled
        let hotKeyChanged = updated.globalHotKey != settings.globalHotKey
            || updated.globalHotKeyEnabled != settings.globalHotKeyEnabled
        let ragChanged = updated.ragEnabled != settings.ragEnabled
            || updated.embeddingModelID != settings.embeddingModelID
            || updated.embeddingAPIModel != settings.embeddingAPIModel
            || (updated.embeddingModelID.hasPrefix(Self.apiEmbedderPrefix) && updated.providers != settings.providers)
        settings = updated
        do {
            try store.save(updated)
        } catch {
            NSLog("EchoDiar: could not save settings: \(error)")
        }
        if detectionChanged { syncMeetingDetection() }
        if retentionChanged { syncRetention() }
        if hotKeyChanged { syncHotKey() }
        // A pass still running would keep embedding with the old choice.
        if ragChanged { syncLibraryIndex(restart: true) }
    }

    /// "Show onboarding again" in General: the first-run window from its first step.
    func showOnboarding() {
        updateSettings { $0.onboardingCompleted = false }
        OnboardingWindow.shared.show(model: self)
    }

    /// ⌘F: the search field focused, on the current page: the query filters the cards or the sidebar, or on Settings
    /// the settings.
    func showSearch() {
        searchPresented = true
    }

    // MARK: - Navigation

    /// Shows the conversation on `tab`.
    func open(_ id: UUID, tab: DetailTab = .transcript) {
        detailTab = tab
        route = .conversation(id)
    }

    /// Esc, ⌘[ and the toolbar's Back: a conversation or the recording goes to the list (the library chat stays
    /// open), the list closes the library chat; Settings goes to the page it was opened from, or the list when that
    /// conversation is gone or that recording has ended.
    func goBack() {
        switch route {
        case .list: globalChatOpen = false
        case .conversation, .recording: route = .list
        case .settings:
            switch settingsReturnRoute {
            case .conversation(let id) where !conversations.contains(where: { $0.id == id }): route = .list
            case .recording where !isRecording: route = .list
            default: route = settingsReturnRoute
            }
        }
    }

    // MARK: - Global shortcut

    /// While the Shortcuts tab records a new shortcut, the current one is unregistered so it reaches the recorder.
    func setHotKeyCapture(_ capturing: Bool) {
        hotKeyCapturing = capturing
        syncHotKey()
    }

    /// Registers `globalHotKey` when it is on (and not being recorded), else unregisters it.
    private func syncHotKey() {
        guard let hotKey else { return }
        let key = settings.globalHotKeyEnabled && !hotKeyCapturing ? settings.globalHotKey : nil
        hotKeyError = hotKey.register(key)
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
        // An untouched preset leaves `providers` as is, so the key's removal alone must stop a running pass.
        if settings.embeddingModelID.hasPrefix(Self.apiEmbedderPrefix) { syncLibraryIndex(restart: true) }
    }

    /// The client for `config`, with its Keychain key; nil for an unknown CLI preset. Throws when the Keychain
    /// refuses the key, or a preset that needs one has none: sent without it, the provider only answers HTTP 401.
    nonisolated func makeProvider(for config: ProviderConfig) throws -> (any LLMProvider)? {
        let apiKey = config.authStyle == .none ? nil : try KeychainStore.apiKey(forProvider: config.id)
        if config.preset != nil, config.authStyle != .none, apiKey?.isEmpty ?? true {
            throw LLMError.provider("Save a key first.")
        }
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
                    id: entry.id, name: entry.displayName, kind: .inApp, defaultModel: entry.id, models: [entry.id],
                    unavailableReason: Self.foundationModelsUnavailableReason))
            } else if entry.runtime == .mlx, installedModelIDs.contains(entry.id) {
                providers.append(AIProvider(
                    id: entry.id, name: entry.displayName, kind: .inApp, defaultModel: entry.id, models: [entry.id]))
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
            var models: [String] = []
            for id in [config.defaultModel] + config.shownModels {
                let id = id.trimmingCharacters(in: .whitespaces)
                if !id.isEmpty && !models.contains(id) { models.append(id) }
            }
            if kind == .cli && config.defaultModel.trimmingCharacters(in: .whitespaces).isEmpty { models.insert("", at: 0) }
            // The picked model stays listed, also once it is unchecked in Settings or was typed before the menu existed.
            if config.id == settings.defaultAIProviderID, let saved = settings.defaultAIModel, !saved.isEmpty,
               !models.contains(saved) {
                models.append(saved)
            }
            providers.append(AIProvider(
                id: config.id, name: config.displayName, kind: kind, defaultModel: config.defaultModel, models: models,
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

    /// The model menu's pick: `model` of `provider` for the next summaries and questions.
    func setAIModel(_ model: String, of provider: AIProvider) {
        updateSettings {
            $0.defaultAIProviderID = provider.id
            $0.defaultAIModel = model == provider.defaultModel ? nil : model
        }
    }

    /// Shows the conversation's transcript and keeps `time` for it to scroll and seek to.
    func openTranscript(conversationID: UUID, at time: Double) {
        pendingTranscriptTime = time
        detailTab = .transcript
        route = .conversation(conversationID)
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
        let client: (any LLMProvider)?
        do {
            client = try llmClient(for: provider)
        } catch {
            aiError = (conversation.id, "\(provider.name): \(error.localizedDescription)")
            return
        }
        guard let client else {
            aiError = (conversation.id, "\(provider.name) is not available.")
            return
        }
        let stream = switch kind {
        case .summary:
            aiService.summarize(
                conversation: conversation, transcript: transcript, provider: client, providerID: provider.id,
                model: model, language: settings.summaryLanguage)
        case .question(let question):
            aiService.ask(
                question: question, conversation: conversation, transcript: transcript, summary: summary,
                history: history, historyLimit: settings.chatContextMessages, provider: client,
                providerID: provider.id, model: model, language: settings.summaryLanguage)
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
            syncLibraryIndex()
            await library.refresh()
        }
        aiJob = AIJob(conversationID: conversation.id, kind: kind, task: task)
    }

    /// Ends the job early; the answer or summary streamed until then is saved, then the job ends as usual. A library
    /// question still looking for its sources (nothing streamed yet) is cancelled.
    func stopAIJob() {
        if let job = aiJob, job.conversationID == nil, job.text.isEmpty { job.task?.cancel() }
        aiService.stop()
    }

    /// Archives the conversation's chat so the next question starts without history; then asks the AI provider for
    /// its title when it has none. Does nothing while a question runs for the conversation.
    func startFreshChat(_ conversation: Conversation) {
        changeChat(of: conversation.id, in: conversation.folder) { try ChatArchive.archive(in: conversation.folder) }
    }

    /// Makes the archived chat `id` the conversation's chat, archiving the current one (titled like
    /// `startFreshChat`). Does nothing while a question runs for the conversation.
    func resumeChat(_ id: UUID, of conversation: Conversation) {
        changeChat(of: conversation.id, in: conversation.folder) { try ChatArchive.resume(id, in: conversation.folder) }
    }

    /// Runs `change` on the chat in `folder` of conversation `id` (nil: the library chat) unless a question runs for
    /// it (its answer is appended to `chat.json`); a failure shows as its AI error.
    func changeChat(of id: UUID?, in folder: URL, _ change: () throws -> ChatArchive.Archived?) {
        if let job = aiJob, job.conversationID == id, job.kind != .summary { return }
        let archived: ChatArchive.Archived?
        do {
            archived = try change()
        } catch {
            aiError = (id, error.localizedDescription)
            archived = nil
        }
        // Also after a failure: a resume may have archived the open chat first, so the pane reloads what is on disk.
        chatHistoryChanges += 1
        if let archived, archived.needsTitle { requestTitle(for: archived.entry, in: folder) }
    }

    /// Names the archived chat `entry` with the current AI provider and model, outside `aiJob`. Without a provider,
    /// when another one gave any of the chat's answers, with an in-app model, or on error its fallback title (the first
    /// question) stays.
    private func requestTitle(for entry: ChatArchive.Entry, in folder: URL) {
        Task { [weak self] in
            // Only with the provider that gave every answer of the chat, so its questions reach no provider that never
            // saw them, and never an in-app model, whose gate a hidden job would hold while the next question waits.
            guard let self, let provider = currentAIProvider, provider.kind != .inApp,
                  let chat = ChatArchive.log(entry.id, in: folder),
                  chat.messages.allSatisfy({ $0.role != .assistant || $0.provider == provider.id }),
                  let client = try? llmClient(for: provider) else { return }
            var title = ""
            do {
                for try await update in aiService.title(
                    chat: chat.messages, provider: client, model: aiModel(for: provider)) {
                    if case .text(let text) = update { title += text }
                }
            } catch { return }
            guard !title.isEmpty else { return }
            try? ChatArchive.setTitle(title, for: entry.id, in: folder)
            chatHistoryChanges += 1
        }
    }

    /// The client for `provider`; nil when its in-app model or its config is gone.
    func llmClient(for provider: AIProvider) throws -> (any LLMProvider)? {
        provider.kind == .inApp
            ? makeLocalProvider(entryID: provider.id)
            : try providerConfigs.first { $0.id == provider.id }.flatMap(makeProvider(for:))
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

// MARK: - Meetings

extension AppModel {
    /// The end monitor and its feeds for the recording in progress.
    struct EndWatch {
        var monitor: MeetingEndMonitor
        /// Target pids still running; the last exit is `.sourceExited`.
        var livePIDs: Set<pid_t> = []
        var exitSources: [any DispatchSourceProcess] = []
        var activity: ProcessActivityWatch?
        var timer: Timer?
        /// The last `.audible` fed (the monitor starts silent).
        var audible = false
        /// The last `.systemAudible` fed.
        var systemAudible = false
        /// The last `.paused` fed.
        var paused = false
        /// The meeting app whose mic feeds `.sourceInputActive`: the recorded app, nil (any known one) for global.
        var meetingBundleID: String?
        /// The last `.sourceInputActive` fed; off is fed only after on.
        var meetingMic = false
        /// `Snapshot.micRoutesLost` already handled.
        var routesLost = 0
    }

    /// The app the pill follows with "Attach to meeting window": the detected app while asking, else the recorded one.
    var pillAppBundleID: String? {
        if case .meetingDetected(let meeting)? = pillPrompt { return meeting.app.appBundleID }
        return sourceApp?.appBundleID
    }

    /// "Record" and "Always for <App>" on the detection prompt: records the detected app's processes.
    func recordDetectedMeeting(always: Bool = false) {
        guard case .meetingDetected(let meeting)? = pillPrompt else { return }
        pillPrompt = nil
        // Never for browsers: any web page using the mic would start a recording.
        if always, !meeting.app.isBrowser { updateSettings { $0.perAppBehavior[meeting.app.appBundleID] = .always } }
        startRecording(target: .app(meeting.group))
    }

    /// "Not now" on the detection prompt.
    func dismissMeetingPrompt() {
        if case .meetingDetected? = pillPrompt { pillPrompt = nil }
    }

    /// The menu's "Stop asking for this app".
    func stopAskingForDetectedApp() {
        guard let meeting = detectedMeeting else { return }
        updateSettings { $0.perAppBehavior[meeting.app.appBundleID] = .ignore }
        detectedMeeting = nil
        dismissMeetingPrompt()
    }

    /// "Keep recording" on the end prompt: that trigger stays quiet for 10 min.
    func keepRecording() {
        guard case .meetingEnded(let reason, _)? = pillPrompt else { return }
        pillPrompt = nil
        endWatch?.monitor.keepRecording(reason, now: Date())
        evaluateEnd()
    }

    /// "Stop now" on the end prompt, the end of its countdown, and a lost mic with the "stop" route setting.
    func stopFromEndPrompt() {
        endEndWatch()
        stopRecording()
    }

    /// The pill, sleep/lock/quit events and meeting detection; on the normal launch path only.
    private func startMeetingServices() {
        pill = FloatingPillController(model: self)
        systemEvents.start { [weak self] in self?.systemEvent($0) }
        Task { [weak self, meetingDetector] in
            for await event in meetingDetector.events {
                guard let self else { return }
                switch event {
                case .detected(let meeting): meetingDetected(meeting)
                case .ended(let meeting): meetingEnded(meeting)
                }
            }
        }
        syncMeetingDetection()
    }

    /// Runs the detector while `meetingDetection` is on; off forgets the detected app and its prompt.
    private func syncMeetingDetection() {
        guard pill != nil else { return }
        if settings.meetingDetection {
            meetingDetector.start()
        } else {
            meetingDetector.stop()
            // The stopped detector emits no `ended`: the monitor keeps the last mic state.
            meetingMics.removeAll()
            detectedMeeting = nil
            dismissMeetingPrompt()
        }
    }

    /// "Ignore" apps are skipped; "Always" starts recording them (browsers still ask), else the pill asks. Nothing
    /// starts while recording.
    private func meetingDetected(_ meeting: DetectedMeeting) {
        meetingMics.insert(meeting.bundleID)
        syncMeetingMic()
        var behavior = settings.perAppBehavior[meeting.app.appBundleID] ?? .ask
        guard behavior != .ignore else { return }
        if behavior == .always, meeting.app.isBrowser { behavior = .ask }
        detectedMeeting = meeting
        guard !isRecording, !isStarting else { return }
        if behavior == .always {
            autoStartedAppName = meeting.name
            startRecording(target: .app(meeting.group))
        } else {
            pillPrompt = .meetingDetected(meeting)
        }
    }

    /// The app's mic is off (call over, or a muted web client): forget it and drop its prompt. Never an end signal
    /// alone: the call-ended trigger also needs the system channel silent.
    private func meetingEnded(_ meeting: DetectedMeeting) {
        meetingMics.remove(meeting.bundleID)
        syncMeetingMic()
        guard detectedMeeting?.bundleID == meeting.bundleID else { return }
        detectedMeeting = nil
        if case .meetingDetected(let shown)? = pillPrompt, shown.bundleID == meeting.bundleID { pillPrompt = nil }
    }

    /// Feeds the end monitor's call-ended trigger whether the recorded meeting app's mic is on (any known app's for a
    /// global recording). Off is fed only after on, so a recording that never saw a meeting mic never gets the prompt.
    private func syncMeetingMic() {
        guard let watch = endWatch else { return }
        let active = watch.meetingBundleID.map(meetingMics.contains) ?? !meetingMics.isEmpty
        guard active != watch.meetingMic else { return }
        endWatch?.meetingMic = active
        recordEnd(.sourceInputActive(active))
    }

    /// Starts the end monitor of the recording that just started: a ≤ 1 Hz evaluation timer and, for an app target,
    /// its process exits and audio activity.
    private func beginEndWatch(target: CaptureTarget) {
        // The engine may already have stopped (disk full), its end snapshot handled.
        let autoStarted = autoStartedAppName
        guard engine.currentSnapshot().state != .idle else {
            autoStartedAppName = nil
            return
        }
        endEndWatch()
        dismissMeetingPrompt()
        endWatchGeneration += 1
        let generation = endWatchGeneration
        var watch = EndWatch(monitor: MeetingEndMonitor(configuration: settings.endMonitor, startedAt: Date()))
        if case .app(let group) = target {
            let app = MeetingApps.app(forBundleID: group.bundleID)
            sourceApp = SourceApp(name: app?.name ?? group.name, appBundleID: app?.appBundleID ?? group.bundleID)
            watch.meetingBundleID = group.bundleID
            // Covers plain processes too (`afplay`), which `SystemEventsMonitor` does not see quit.
            for pid in Set(group.processes.map(\.pid)) {
                let source = DispatchSource.makeProcessSource(identifier: pid, eventMask: .exit, queue: .main)
                source.setEventHandler { [weak self] in
                    MainActor.assumeIsolated { self?.sourceProcessExited(pid, generation: generation) }
                }
                source.resume()
                watch.exitSources.append(source)
                // Checked once armed: a source on a pid that already died never fires.
                if Self.isAlive(pid) { watch.livePIDs.insert(pid) }
            }
            watch.activity = ProcessActivityWatch(group: group) { [weak self] active in
                Task { @MainActor in self?.recordEnd(.sourceActive(active), generation: generation) }
            }
        } else {
            sourceApp = nil
        }
        let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.endTick() }
        }
        timer.tolerance = 0.25
        RunLoop.main.add(timer, forMode: .common)
        watch.timer = timer
        let sourceGone = sourceApp != nil && watch.livePIDs.isEmpty
        endWatch = watch
        syncMeetingMic()
        if let autoStarted { showAutoStartNotice(autoStarted) }
        if sourceGone { recordEnd(.sourceExited) }
    }

    /// Stops the timer and the feeds, and drops the end prompt. Safe to call repeatedly.
    private func endEndWatch() {
        if let watch = endWatch {
            watch.timer?.invalidate()
            watch.exitSources.forEach { $0.cancel() }
            watch.activity?.stop()
        }
        endWatch = nil
        sourceApp = nil
        autoStartNoticeTask?.cancel()
        autoStartNoticeTask = nil
        autoStartedAppName = nil
        if case .meetingEnded? = pillPrompt { pillPrompt = nil }
    }

    /// Keeps "Recording <App>" on the pill for 10 s after an automatic start.
    private func showAutoStartNotice(_ name: String) {
        autoStartedAppName = name
        autoStartNoticeTask?.cancel()
        autoStartNoticeTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(10))
            guard !Task.isCancelled else { return }
            self?.autoStartedAppName = nil
        }
    }

    /// `generation` (from a feed armed by `beginEndWatch`) drops inputs meant for an earlier recording.
    private func recordEnd(_ input: MeetingEndMonitor.Input, generation: Int? = nil) {
        guard endWatch != nil, generation.map({ $0 == endWatchGeneration }) ?? true else { return }
        endWatch?.monitor.record(input, at: Date())
        evaluateEnd()
    }

    /// Shows or dismisses the end prompt, or stops now. A shown prompt keeps its deadline while its reason holds.
    private func evaluateEnd() {
        guard let watch = endWatch else { return }
        let now = Date()
        switch watch.monitor.evaluate(now: now) {
        case .none:
            if case .meetingEnded? = pillPrompt { pillPrompt = nil }
        case .promptStop(let reason, let countdown):
            if case .meetingEnded(reason, _)? = pillPrompt { return }
            pillPrompt = .meetingEnded(reason: reason, deadline: now.addingTimeInterval(TimeInterval(countdown)))
        case .stopNow(let reason):
            NSLog("EchoDiar: recording stopped: \(reason.rawValue)")
            endEndWatch()
            // Synchronous: a `willSleep` handler has little time before the system sleeps.
            engine.stop(reason: .user)
        }
    }

    /// Every second while recording: feeds `.audible` (paused counts as silent: wall clock), `.paused` and
    /// `.systemAudible` from the writer's silence runs, evaluates, then stops once a still-shown end prompt's
    /// countdown is over.
    private func endTick() {
        guard let watch = endWatch else { return }
        let now = Date()
        let snapshot = engine.currentSnapshot()
        let quiet = min(snapshot.micSilentSeconds, snapshot.systemSilentSeconds)
        // A silence run as long as the recording means nothing rose above the floor yet.
        let audible = snapshot.state == .recording && quiet < 2 && quiet + 0.5 < snapshot.elapsed
        if audible != watch.audible {
            endWatch?.audible = audible
            let since = audible || snapshot.state != .recording ? now : now.addingTimeInterval(-quiet)
            endWatch?.monitor.record(.audible(audible), at: since)
        }
        let paused = snapshot.state != .recording
        if paused != watch.paused {
            endWatch?.paused = paused
            endWatch?.monitor.record(.paused(paused), at: now)
        }
        // Not fed while paused: the call-ended trigger does not count a pause as silence.
        let systemQuiet = snapshot.systemSilentSeconds
        let systemAudible = systemQuiet < 2 && systemQuiet + 0.5 < snapshot.elapsed
        if !paused, systemAudible != watch.systemAudible {
            endWatch?.systemAudible = systemAudible
            let since = systemAudible ? now : now.addingTimeInterval(-systemQuiet)
            endWatch?.monitor.record(.systemAudible(systemAudible), at: since)
        }
        evaluateEnd()
        // Only a prompt that still holds after the fresh inputs stops the recording.
        if case .meetingEnded(_, let deadline)? = pillPrompt, now >= deadline {
            stopFromEndPrompt()
        }
    }

    private func sourceProcessExited(_ pid: pid_t, generation: Int) {
        guard generation == endWatchGeneration, endWatch?.livePIDs.remove(pid) != nil,
              endWatch?.livePIDs.isEmpty == true else { return }
        recordEnd(.sourceExited)
    }

    /// A lost microphone, per `routeChangeBehavior`: ask through the end monitor, stop, or carry on.
    private func micRoutesLost(_ count: Int) {
        guard let watch = endWatch, count > watch.routesLost else { return }
        endWatch?.routesLost = count
        switch settings.routeChangeBehavior {
        case .ask: recordEnd(.inputRouteLost)
        case .stop: stopFromEndPrompt()
        case .continue: break
        }
    }

    private func systemEvent(_ event: SystemEventsMonitor.Event) {
        switch event {
        case .willSleep:
            if endWatch != nil {
                recordEnd(.systemSleep)
            } else if engine.currentSnapshot().state != .idle {
                // Started but its end watch not armed yet. Synchronous: little time before the system sleeps.
                NSLog("EchoDiar: recording stopped: \(MeetingEndMonitor.Reason.systemSleep.rawValue)")
                engine.stop(reason: .user)
            }
        case .screenLocked: recordEnd(.screenLocked(true))
        case .screenUnlocked: recordEnd(.screenLocked(false))
        case .didWake: break
        case .appTerminated(_, let bundleID):
            // FaceTime: the call runs in avconferenced, which never exits.
            if let bundleID, bundleID == sourceApp?.appBundleID { recordEnd(.sourceExited) }
        }
    }

    /// Alive, or alive but owned by another user.
    nonisolated private static func isAlive(_ pid: pid_t) -> Bool {
        kill(pid, 0) == 0 || errno == EPERM
    }
}

/// The single reader of `RecordingEngine.currentLevels()`, whose peaks reset on each read, shared by the main
/// window's meters and the pill's: it reads at 15 Hz only while a meter is shown.
@MainActor
@Observable
final class LevelFeed {
    private(set) var mic: Float = 0
    private(set) var system: Float = 0
    @ObservationIgnored private let engine: RecordingEngine
    @ObservationIgnored private var readers = 0
    @ObservationIgnored private var reader: Task<Void, Never>?

    init(engine: RecordingEngine) {
        self.engine = engine
    }

    /// Returns when the calling task is cancelled (a meter's `.task`); the feed reads while any caller waits here.
    func attach() async {
        readers += 1
        if reader == nil {
            reader = Task { [weak self, engine] in
                while !Task.isCancelled {
                    let levels = engine.currentLevels()
                    self?.mic = levels.mic
                    self?.system = levels.system
                    try? await Task.sleep(for: .milliseconds(67))
                }
            }
        }
        while !Task.isCancelled {
            try? await Task.sleep(for: .seconds(3_600))
        }
        readers -= 1
        guard readers == 0 else { return }
        reader?.cancel()
        reader = nil
        mic = 0
        system = 0
    }

    /// A peak level (0...1) on a -60...0 dBFS scale.
    static func fraction(_ level: Float) -> CGFloat {
        guard level > 0 else { return 0 }
        return CGFloat(min(max((20 * log10(Double(level)) + 60) / 60, 0), 1))
    }
}
