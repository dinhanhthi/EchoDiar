import Accelerate
import AudioToolbox
import AVFoundation
import EchoAI
import EchoCore
import Foundation

/// Batch transcription of recording folders, one job at a time: extract each channel to 16 kHz mono, skip silent
/// channels, ASR mic, ASR system, diarize system, merge, write `transcript.json`. Models are loaded inside each
/// step and unloaded before the next, while the job holds the `ModelLoadGate` (it waits while live captions hold
/// it). Never touches EchoAudio: `AppModel` runs compression first.
public actor TranscriptionPipeline {
    public typealias EngineFactory = @Sendable (_ entry: ModelEntry, _ folder: URL) -> any TranscriptionEngine

    /// Batch engines by catalog runtime. Apple Speech only on macOS 26+ without `--force-fallbacks`; system
    /// engines get the Models root as `folder` and ignore it.
    public static let engines: [ModelEntry.Runtime: EngineFactory] = {
        var engines: [ModelEntry.Runtime: EngineFactory] = [
            .whisperKit: { WhisperKitEngine(entry: $0, folder: $1) },
            // Parakeet v3 / Ultra; Nemotron is live only and the diarizer is not an ASR entry, so neither resolves here.
            .fluidAudio: { ParakeetEngine(entry: $0, folder: $1) },
            .speechSwift: { Qwen3ASREngine(entry: $0, folder: $1) },
        ]
        if #available(macOS 26, *), PlatformFeatures.speechAnalyzer {
            engines[.appleSpeech] = { entry, _ in AppleSpeechEngine(entry: entry) }
        }
        return engines
    }()

    public static var registeredRuntimes: Set<ModelEntry.Runtime> { Set(engines.keys) }

    /// Language codes an engine accepts: the catalog list, or the runtime's own list for multilingual entries;
    /// empty when unknown.
    public static func supportedLanguages(for entry: ModelEntry) -> [String] {
        switch (entry.languages, entry.runtime) {
        case (.codes(let codes), _): return codes
        case (.multilingual, .whisperKit): return WhisperKitEngine.supportedLanguages
        case (.multilingual, .appleSpeech):
            guard #available(macOS 26, *), PlatformFeatures.speechAnalyzer else { return [] }
            return AppleSpeechEngine.supportedLanguages
        case (.multilingual, _): return []
        }
    }

    /// Downloaded entries: installed in `Models/`. System entries (no repo): the OS feature is available.
    public static func isUsable(_ entry: ModelEntry, in modelStore: ModelStore) -> Bool {
        guard entry.repo == nil else { return modelStore.isInstalled(entry.id) }
        guard entry.runtime == .appleSpeech, #available(macOS 26, *) else { return false }
        return AppleSpeechEngine.isAvailable
    }

    /// −60 dBFS: a channel whose peak stays below is silent and skips ASR.
    public static let silencePeak: Float = 0.001

    /// Every job's progress, for a single consumer.
    public nonisolated let progress: AsyncStream<JobProgress>
    private nonisolated let continuation: AsyncStream<JobProgress>.Continuation
    private let modelStore: ModelStore
    private let gate: ModelLoadGate
    private let deferWhenConstrained: Bool
    private let speakerCountHint: Int?
    private var tail: Task<Void, Never>?

    public init(
        modelStore: ModelStore, gate: ModelLoadGate, deferWhenConstrained: Bool = true, speakerCountHint: Int? = nil
    ) {
        self.modelStore = modelStore
        self.gate = gate
        self.deferWhenConstrained = deferWhenConstrained
        self.speakerCountHint = speakerCountHint
        (progress, continuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(64))
        // `supportedLanguages(for:)` is synchronous; Apple's locale list is async, so it is cached once here.
        if #available(macOS 26, *), PlatformFeatures.speechAnalyzer {
            Task.detached(priority: .utility) { await AppleSpeechEngine.loadSupportedLanguages() }
        }
    }

    /// Runs after every job queued before it; returns the `transcript.json` URL. `cloud`: the job's cloud engine
    /// (resolved by the app from settings and the Keychain), which replaces `job.engineID`'s resolution.
    public func transcribe(_ job: TranscriptionJob, cloud: CloudEngine? = nil) async throws -> URL {
        emit(job, .queued, 0)
        let previous = tail
        let task = Task(priority: .utility) {
            await previous?.value
            return try await self.run(job, cloud: cloud)
        }
        tail = Task { _ = await task.result }
        return try await task.value
    }

    // MARK: - Job

    private struct ChannelSource: Sendable {
        var url: URL
        var channel: Int
    }

    /// Transcribes one channel: the 16 kHz extract, its source channel, which channel it is, and progress 0...1.
    private typealias ChannelASR = @Sendable (
        _ extracted: URL, _ source: ChannelSource, _ channel: Transcript.Channel,
        _ progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment]

    private func run(_ job: TranscriptionJob, cloud: CloudEngine?) async throws -> URL {
        var started = false
        do {
            if deferWhenConstrained && !job.processNow { await waitWhileConstrained(job) }
            guard var session = try? RecordingSession.read(from: job.folder) else {
                throw TranscriptionError.sessionUnreadable
            }
            // Never written back: the override only applies to this run.
            if let language = job.language { session.languageMarkers = [.init(fileTime: 0, language: language)] }
            let sources = try channelSources(session: session, job: job)
            let ranges = LanguageRange.ranges(from: session.languageMarkers)
            let diarizerID = ModelCatalog.defaultDiarizer.id
            let diarizer = modelStore.isInstalled(diarizerID)
                ? modelStore.folder(for: diarizerID).map {
                    SpeakerDiarizer(entryID: diarizerID, folder: $0, speakerCountHint: speakerCountHint)
                }
                : nil

            let url: URL
            if let cloud {
                // No local ASR model: only a local diarization takes the gate (inside `process`).
                started = true
                Self.updateConversation(in: job.folder) { $0.status = .transcribing }
                let language = Self.cloudLanguage(ranges)
                url = try await process(
                    job, session: session, sources: sources, engineID: cloud.engineID,
                    modelID: cloud.model.isEmpty ? nil : cloud.model, engineFallback: nil, diarizer: diarizer,
                    gateDiarization: true
                ) { _, source, channel, _ in
                    try await Self.cloudSegments(cloud, source: source, language: language, diarize: channel == .system)
                }
            } else {
                let firstLanguage = session.languageMarkers.min { $0.fileTime < $1.fileTime }?.language ?? "auto"
                let selected = try resolveEngine(job.engineID, language: firstLanguage)
                let (entry, engineFallback) = await fallback(
                    for: selected, markers: session.languageMarkers, language: firstLanguage)
                guard let factory = Self.engines[entry.runtime],
                      let modelFolder = modelStore.folder(for: entry.id) ?? (entry.repo == nil ? modelStore.root : nil) else {
                    throw TranscriptionError.unknownEngine(entry.id)
                }

                started = true
                let engine = factory(entry, modelFolder)
                url = try await gate.withExclusiveModel(owner: .transcriptionJob) { [self, session] in
                    Self.updateConversation(in: job.folder) { $0.status = .transcribing }
                    return try await process(
                        job, session: session, sources: sources, engineID: entry.id,
                        modelID: [entry.repo, entry.subfolder].compactMap { $0 }.joined(separator: "/"),
                        engineFallback: engineFallback, diarizer: diarizer, gateDiarization: false
                    ) { extracted, _, _, progress in
                        try await engine.transcribe(channelURL: extracted, languageRanges: ranges, progress: progress)
                    }
                }
            }
            Self.updateConversation(in: job.folder) {
                $0.status = .transcribed
                $0.hasTranscript = true
                $0.sizeBytes = LibraryStore.sizeOnDisk(of: job.folder)
            }
            emit(job, .finished, 1)
            return url
        } catch TranscriptionError.needsModel(let id) {
            if started { Self.updateConversation(in: job.folder) { $0.status = .ready } }
            emit(job, .needsModel, 0, detail: id)
            throw TranscriptionError.needsModel(id)
        } catch {
            // Also before `started`, so an unreadable session or audio shows "Failed — Retry" on the card.
            Self.updateConversation(in: job.folder) { $0.status = .transcriptionFailed }
            emit(job, .failed, 0, detail: error.localizedDescription)
            throw error
        }
    }

    /// Extracts each channel (skipping silent ones), runs `asr` on it, diarizes the system channel unless `asr`
    /// returned provider speaker labels, merges and writes `transcript.json`. `gateDiarization`: the caller does
    /// not hold the `ModelLoadGate` (cloud ASR), so the diarizer takes it.
    private func process(
        _ job: TranscriptionJob, session: RecordingSession, sources: (mic: ChannelSource?, system: ChannelSource?),
        engineID: String, modelID: String?, engineFallback: String?, diarizer: SpeakerDiarizer?,
        gateDiarization: Bool, asr: ChannelASR
    ) async throws -> URL {
        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("EchoDiar-transcribe-\(UUID().uuidString)")
        try fm.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: work) }

        var mic: [RawSegment] = []
        if let source = sources.mic {
            emit(job, .extracting, 0)
            if let url = try await extract(source, to: work.appendingPathComponent("mic.caf")) {
                emit(job, .transcribingMic, 0.05)
                mic = try await asr(url, source, .mic) { [self] fraction in
                    emit(job, .transcribingMic, 0.05 + 0.35 * fraction)
                }
                try? fm.removeItem(at: url)
            }
        }

        var system: [RawSegment] = []
        var turns: [DiarizationTurn] = []
        if let source = sources.system {
            emit(job, .extracting, 0.4)
            if let url = try await extract(source, to: work.appendingPathComponent("system.caf")) {
                emit(job, .transcribingSystem, 0.45)
                system = try await asr(url, source, .system) { [self] fraction in
                    emit(job, .transcribingSystem, 0.45 + 0.35 * fraction)
                }
                if let diarizer, !system.isEmpty, !system.contains(where: { $0.speaker != nil }) {
                    emit(job, .diarizing, 0.8)
                    let diarize: @Sendable () async throws -> [DiarizationTurn] = { [self] in
                        try await diarizer.diarize(channelURL: url) { [self] fraction in
                            emit(job, .diarizing, 0.8 + 0.15 * fraction)
                        }
                    }
                    turns = gateDiarization
                        ? try await gate.withExclusiveModel(owner: .transcriptionJob, diarize)
                        : try await diarize()
                }
            }
        }

        emit(job, .merging, 0.95)
        let providerSpeakers = system.contains { $0.speaker != nil }
        let transcript = Transcript(
            segments: TranscriptMerger.merge(
                mic: mic, system: system, turns: turns, languageMarkers: session.languageMarkers),
            engineID: engineID,
            modelID: modelID,
            engineFallback: engineFallback,
            notes: diarizer == nil && !system.isEmpty && !providerSpeakers ? [Transcript.diarizationMissingNote] : []
        )
        try transcript.write(to: job.folder)
        return job.folder.appendingPathComponent(Transcript.fileName)
    }

    /// One channel through a cloud API, from the compressed source (not the 16 kHz extract); errors other than a
    /// cancellation become `TranscriptionError.cloud` with a short reason.
    private static func cloudSegments(
        _ cloud: CloudEngine, source: ChannelSource, language: String?, diarize: Bool
    ) async throws -> [RawSegment] {
        do {
            let segments = try await CloudTranscription.transcribe(
                source: source.url, channel: source.channel, with: cloud.transcriber, language: language, diarize: diarize)
            return segments.map {
                RawSegment(start: $0.start, end: $0.end, text: $0.text, detectedLanguage: $0.language, speaker: $0.speaker)
            }
        } catch let error as CancellationError {
            throw error
        } catch {
            throw TranscriptionError.cloud(provider: cloud.name, reason: CloudTranscription.failureReason(error))
        }
    }

    /// The language sent to a cloud API for the whole recording: the single language of `ranges`, nil (detect)
    /// when it is Auto or the recording switches languages.
    static func cloudLanguage(_ ranges: [LanguageRange]) -> String? {
        let languages = Set(ranges.map(\.language))
        return languages.count == 1 ? languages.first ?? nil : nil
    }

    /// Stereo: ch0 mic, ch1 system. Two mono files: `audioFileName` mic, `systemFileName` system. A single
    /// mono file is the system channel.
    private func channelSources(
        session: RecordingSession, job: TranscriptionJob
    ) throws -> (mic: ChannelSource?, system: ChannelSource?) {
        func existing(_ name: String) throws -> URL {
            guard let url = ConversationFiles.audioURL(in: job.folder, fileName: name),
                  FileManager.default.fileExists(atPath: url.path) else {
                throw TranscriptionError.audioUnreadable("\(name) is missing")
            }
            return url
        }
        let audio = try existing(session.audioFileName)
        if let systemName = job.systemFileName {
            return (ChannelSource(url: audio, channel: 0), ChannelSource(url: try existing(systemName), channel: 0))
        }
        guard try MonoAudio.channelCount(of: audio) >= 2 else { return (nil, ChannelSource(url: audio, channel: 0)) }
        return (
            ChannelSource(url: audio, channel: RecordingSession.Channel.mic),
            ChannelSource(url: audio, channel: RecordingSession.Channel.system)
        )
    }

    /// Explicit engine, else `defaultEngine(language:isUsable:)`, else "Needs model" (the default).
    private func resolveEngine(_ requested: String?, language: String) throws -> ModelEntry {
        if let requested {
            guard let entry = ModelCatalog.entry(id: requested), entry.kind == .asr, !entry.liveOnly,
                  Self.engines[entry.runtime] != nil else {
                throw TranscriptionError.unknownEngine(requested)
            }
            guard Self.isUsable(entry, in: modelStore) else { throw TranscriptionError.needsModel(entry.id) }
            return entry
        }
        guard let entry = Self.defaultEngine(language: language, isUsable: { Self.isUsable($0, in: modelStore) }) else {
            throw TranscriptionError.needsModel(ModelCatalog.defaultASR.id)
        }
        return entry
    }

    /// With no engine chosen: the catalog default if usable → the smallest usable WhisperKit entry selectable for
    /// `language` → the first usable batch entry that `supports` `language`, then selectable for it, in catalog order.
    static func defaultEngine(language: String, isUsable: (ModelEntry) -> Bool) -> ModelEntry? {
        let installed = ModelCatalog.asr.filter { !$0.liveOnly && engines[$0.runtime] != nil && isUsable($0) }
        if let entry = installed.first(where: \.isDefault) { return entry }
        return installed
            .filter { $0.runtime == .whisperKit && $0.isSelectable(for: language) }
            .min { $0.diskMB < $1.diskMB }
            ?? installed.first { supports($0, language: language) }
            ?? installed.first { $0.isSelectable(for: language) }
    }

    /// The Models tab's default `id` when it is a usable batch entry that `supports` `language`, else nil for
    /// `defaultEngine(language:isUsable:)`: a stored default is a preference, not an explicit choice.
    public static func preferredEngineID(_ id: String?, language: String, isUsable: (ModelEntry) -> Bool) -> String? {
        guard let id, let entry = ModelCatalog.entry(id: id), entry.kind == .asr, !entry.liveOnly,
              engines[entry.runtime] != nil, isUsable(entry), supports(entry, language: language) else { return nil }
        return id
    }

    /// Selectable for `language` and, for a code, listed in `supportedLanguages(for:)` by its primary code. An
    /// empty list is unknown and counts as supported; "auto" needs no code.
    static func supports(_ entry: ModelEntry, language: String) -> Bool {
        guard entry.isSelectable(for: language) else { return false }
        let supported = supportedLanguages(for: entry)
        return language == "auto" || supported.isEmpty || supported.contains(WhisperKitEngine.whisperLanguage(language))
    }

    /// `entry`'s supported languages (Apple's list loaded first), then `languageFallback`.
    private func fallback(
        for entry: ModelEntry, markers: [RecordingSession.LanguageMarker], language: String
    ) async -> (entry: ModelEntry, engineFallback: String?) {
        var supported = Self.supportedLanguages(for: entry)
        if entry.runtime == .appleSpeech, #available(macOS 26, *), PlatformFeatures.speechAnalyzer {
            supported = await AppleSpeechEngine.loadSupportedLanguages()
        }
        let languages = LanguageRange.ranges(from: markers).compactMap(\.language).map(WhisperKitEngine.whisperLanguage)
        return Self.languageFallback(
            for: entry, supported: supported, markerLanguages: languages, language: language,
            isInstalled: { modelStore.isInstalled($0) })
    }

    /// `entry`, or an installed WhisperKit stand-in (the default first, else the smallest) when one of
    /// `markerLanguages` (primary codes) is not in `supported`, with `entry.id` as the fallback note. An empty
    /// `supported` list is unknown and counts as supported. The stand-in transcribes the whole job, so it must
    /// support every marker language and be selectable for `language`; with none, `entry` runs anyway.
    static func languageFallback(
        for entry: ModelEntry, supported: [String], markerLanguages: [String], language: String,
        isInstalled: (String) -> Bool
    ) -> (entry: ModelEntry, engineFallback: String?) {
        guard !supported.isEmpty, markerLanguages.contains(where: { !supported.contains($0) }) else {
            return (entry, nil)
        }
        let standIns = ModelCatalog.asr.filter { candidate in
            let candidateLanguages = supportedLanguages(for: candidate)
            return candidate.runtime == .whisperKit && candidate.id != entry.id && isInstalled(candidate.id)
                && candidate.isSelectable(for: language)
                && (candidateLanguages.isEmpty || markerLanguages.allSatisfy(candidateLanguages.contains))
        }
        guard let standIn = standIns.first(where: \.isDefault) ?? standIns.min(by: { $0.diskMB < $1.diskMB }) else {
            return (entry, nil)
        }
        return (standIn, entry.id)
    }

    /// The extracted file, or nil when the channel is silent.
    private func extract(_ source: ChannelSource, to url: URL) async throws -> URL? {
        let peak = try await Task.detached(priority: .utility) {
            try MonoAudio.extract(channel: source.channel, of: source.url, to: url)
        }.value
        guard peak >= Self.silencePeak else {
            try? FileManager.default.removeItem(at: url)
            return nil
        }
        return url
    }

    // MARK: - Deferral

    private static var isConstrained: Bool {
        let info = ProcessInfo.processInfo
        return info.thermalState.rawValue >= ProcessInfo.ThermalState.serious.rawValue || info.isLowPowerModeEnabled
    }

    /// Waits for thermal or power state notifications while the Mac is hot or in Low Power Mode (no polling).
    private func waitWhileConstrained(_ job: TranscriptionJob) async {
        while Self.isConstrained && !Task.isCancelled {
            emit(job, .deferred, 0)
            await withTaskGroup(of: Void.self) { group in
                for name in [ProcessInfo.thermalStateDidChangeNotification, .NSProcessInfoPowerStateDidChange] {
                    group.addTask {
                        for await _ in NotificationCenter.default.notifications(named: name) { return }
                    }
                }
                await group.next()
                group.cancelAll()
            }
        }
    }

    // MARK: - Helpers

    private nonisolated func emit(
        _ job: TranscriptionJob, _ step: JobProgress.Step, _ fraction: Double, detail: String? = nil
    ) {
        continuation.yield(JobProgress(conversationID: job.conversationID, step: step, fraction: fraction, detail: detail))
    }

    /// Read-modify-write of `conversation.json` when the folder has one (the DEBUG harness folder does not).
    private static func updateConversation(in folder: URL, _ change: (inout Conversation) -> Void) {
        guard var conversation = try? Conversation.read(from: folder) else { return }
        change(&conversation)
        try? conversation.write()
    }
}

/// A cloud transcription engine for one job, resolved by the app from its settings and the Keychain.
public struct CloudEngine: Sendable {
    /// `cloud:<provider id>`, written as the transcript's `engineID`.
    public var engineID: String
    /// Provider name for error reasons ("Deepgram: invalid API key").
    public var name: String
    /// Written as the transcript's `modelID`; empty when the provider picks it.
    public var model: String
    public var transcriber: any CloudTranscriber

    public init(engineID: String, name: String, model: String, transcriber: any CloudTranscriber) {
        self.engineID = engineID
        self.name = name
        self.model = model
        self.transcriber = transcriber
    }
}

/// 16 kHz mono Float32 channel files.
enum MonoAudio {
    static let sampleRate = 16_000.0
    private static let chunkFrames: AVAudioFrameCount = 16_384

    static func channelCount(of url: URL) throws -> Int {
        do {
            return Int(try AVAudioFile(forReading: url).fileFormat.channelCount)
        } catch {
            throw TranscriptionError.audioUnreadable(error.localizedDescription)
        }
    }

    /// Writes one channel of `source`, resampled to 16 kHz, to a mono Float32 CAF in chunks; returns its peak.
    static func extract(channel: Int, of source: URL, to destination: URL) throws -> Float {
        var opened: ExtAudioFileRef?
        guard ExtAudioFileOpenURL(source as CFURL, &opened) == noErr, let file = opened else {
            throw TranscriptionError.audioUnreadable(source.lastPathComponent)
        }
        defer { ExtAudioFileDispose(file) }
        var fileFormat = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        guard ExtAudioFileGetProperty(file, kExtAudioFileProperty_FileDataFormat, &size, &fileFormat) == noErr,
              channel < Int(fileFormat.mChannelsPerFrame),
              let client = AVAudioFormat(
                  commonFormat: .pcmFormatFloat32, sampleRate: sampleRate,
                  channels: fileFormat.mChannelsPerFrame, interleaved: false),
              let mono = AVAudioFormat(
                  commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: 1, interleaved: false),
              let input = AVAudioPCMBuffer(pcmFormat: client, frameCapacity: chunkFrames),
              let output = AVAudioPCMBuffer(pcmFormat: mono, frameCapacity: chunkFrames) else {
            throw TranscriptionError.audioUnreadable("unsupported format in \(source.lastPathComponent)")
        }
        var clientDescription = client.streamDescription.pointee
        guard ExtAudioFileSetProperty(
            file, kExtAudioFileProperty_ClientDataFormat, UInt32(MemoryLayout<AudioStreamBasicDescription>.size),
            &clientDescription) == noErr else {
            throw TranscriptionError.audioUnreadable("cannot convert \(source.lastPathComponent)")
        }
        let writer = try AVAudioFile(
            forWriting: destination, settings: mono.settings, commonFormat: .pcmFormatFloat32, interleaved: false)

        var peak: Float = 0
        while true {
            input.frameLength = input.frameCapacity
            var frames = input.frameCapacity
            guard ExtAudioFileRead(file, &frames, input.mutableAudioBufferList) == noErr else {
                throw TranscriptionError.audioUnreadable("decode error in \(source.lastPathComponent)")
            }
            if frames == 0 { break }
            input.frameLength = frames
            output.frameLength = frames
            let samples = input.floatChannelData![channel]
            output.floatChannelData![0].update(from: samples, count: Int(frames))
            var chunkPeak: Float = 0
            vDSP_maxmgv(samples, 1, &chunkPeak, vDSP_Length(frames))
            peak = max(peak, chunkPeak)
            try writer.write(from: output)
        }
        return peak
    }

    /// Samples from `start` to `end` seconds (nil = end of file) of a mono file.
    static func read(_ url: URL, from start: Double, to end: Double?) throws -> [Float] {
        let file = try AVAudioFile(forReading: url, commonFormat: .pcmFormatFloat32, interleaved: false)
        let rate = file.processingFormat.sampleRate
        let first = min(file.length, AVAudioFramePosition(max(0, start) * rate))
        let last = min(file.length, end.map { AVAudioFramePosition($0 * rate) } ?? file.length)
        guard last > first else { return [] }
        let count = AVAudioFrameCount(last - first)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: count) else {
            throw TranscriptionError.audioUnreadable(url.lastPathComponent)
        }
        file.framePosition = first
        try file.read(into: buffer, frameCount: count)
        return Array(UnsafeBufferPointer(start: buffer.floatChannelData![0], count: Int(buffer.frameLength)))
    }

    static func duration(of url: URL) throws -> Double {
        let file = try AVAudioFile(forReading: url)
        return Double(file.length) / file.processingFormat.sampleRate
    }
}
