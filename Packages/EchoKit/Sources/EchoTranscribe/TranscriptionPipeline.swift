import Accelerate
import AudioToolbox
import AVFoundation
import EchoCore
import Foundation

/// Batch transcription of recording folders, one job at a time: extract each channel to 16 kHz mono, skip silent
/// channels, ASR mic, ASR system, diarize system, merge, write `transcript.json`. Models are loaded inside each
/// step and unloaded before the next. Never touches EchoAudio: `AppModel` runs compression first.
public actor TranscriptionPipeline {
    public typealias EngineFactory = @Sendable (_ entry: ModelEntry, _ folder: URL) -> any TranscriptionEngine

    /// Batch engines by catalog runtime.
    public static let engines: [ModelEntry.Runtime: EngineFactory] = [
        .whisperKit: { WhisperKitEngine(entry: $0, folder: $1) },
    ]

    public static var registeredRuntimes: Set<ModelEntry.Runtime> { Set(engines.keys) }

    /// Language codes an engine accepts: the catalog list, or the runtime's own list for multilingual entries;
    /// empty when unknown.
    public static func supportedLanguages(for entry: ModelEntry) -> [String] {
        switch (entry.languages, entry.runtime) {
        case (.codes(let codes), _): codes
        case (.multilingual, .whisperKit): WhisperKitEngine.supportedLanguages
        case (.multilingual, _): []
        }
    }

    /// −60 dBFS: a channel whose peak stays below is silent and skips ASR.
    public static let silencePeak: Float = 0.001

    /// Every job's progress, for a single consumer.
    public nonisolated let progress: AsyncStream<JobProgress>
    private nonisolated let continuation: AsyncStream<JobProgress>.Continuation
    private let modelStore: ModelStore
    private let deferWhenConstrained: Bool
    private let speakerCountHint: Int?
    private var tail: Task<Void, Never>?

    public init(modelStore: ModelStore, deferWhenConstrained: Bool = true, speakerCountHint: Int? = nil) {
        self.modelStore = modelStore
        self.deferWhenConstrained = deferWhenConstrained
        self.speakerCountHint = speakerCountHint
        (progress, continuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(64))
    }

    /// Runs after every job queued before it; returns the `transcript.json` URL.
    public func transcribe(_ job: TranscriptionJob) async throws -> URL {
        emit(job, .queued, 0)
        let previous = tail
        let task = Task(priority: .utility) {
            await previous?.value
            return try await self.run(job)
        }
        tail = Task { _ = await task.result }
        return try await task.value
    }

    // MARK: - Job

    private struct ChannelSource: Sendable {
        var url: URL
        var channel: Int
    }

    private func run(_ job: TranscriptionJob) async throws -> URL {
        var started = false
        do {
            if deferWhenConstrained && !job.processNow { await waitWhileConstrained(job) }
            guard var session = try? RecordingSession.read(from: job.folder) else {
                throw TranscriptionError.sessionUnreadable
            }
            // Never written back: the override only applies to this run.
            if let language = job.language { session.languageMarkers = [.init(fileTime: 0, language: language)] }
            let sources = try channelSources(session: session, job: job)
            let firstLanguage = session.languageMarkers.min { $0.fileTime < $1.fileTime }?.language ?? "auto"
            let entry = try resolveEngine(job.engineID, language: firstLanguage)
            guard let factory = Self.engines[entry.runtime], let modelFolder = modelStore.folder(for: entry.id) else {
                throw TranscriptionError.unknownEngine(entry.id)
            }
            let diarizerID = ModelCatalog.defaultDiarizer.id
            let diarizer = modelStore.isInstalled(diarizerID)
                ? modelStore.folder(for: diarizerID).map {
                    SpeakerDiarizer(entryID: diarizerID, folder: $0, speakerCountHint: speakerCountHint)
                }
                : nil

            started = true
            Self.updateConversation(in: job.folder) { $0.status = .transcribing }
            let url = try await process(
                job, session: session, sources: sources, engine: factory(entry, modelFolder), entry: entry,
                diarizer: diarizer)
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

    private func process(
        _ job: TranscriptionJob, session: RecordingSession, sources: (mic: ChannelSource?, system: ChannelSource?),
        engine: any TranscriptionEngine, entry: ModelEntry, diarizer: SpeakerDiarizer?
    ) async throws -> URL {
        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("EchoDiar-transcribe-\(UUID().uuidString)")
        try fm.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: work) }
        let ranges = LanguageRange.ranges(from: session.languageMarkers)

        var mic: [RawSegment] = []
        if let source = sources.mic {
            emit(job, .extracting, 0)
            if let url = try await extract(source, to: work.appendingPathComponent("mic.caf")) {
                mic = try await engine.transcribe(channelURL: url, languageRanges: ranges) { [self] fraction in
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
                system = try await engine.transcribe(channelURL: url, languageRanges: ranges) { [self] fraction in
                    emit(job, .transcribingSystem, 0.45 + 0.35 * fraction)
                }
                if let diarizer, !system.isEmpty {
                    emit(job, .diarizing, 0.8)
                    turns = try await diarizer.diarize(channelURL: url) { [self] fraction in
                        emit(job, .diarizing, 0.8 + 0.15 * fraction)
                    }
                }
            }
        }

        emit(job, .merging, 0.95)
        let transcript = Transcript(
            segments: TranscriptMerger.merge(
                mic: mic, system: system, turns: turns, languageMarkers: session.languageMarkers),
            engineID: entry.id,
            modelID: [entry.repo, entry.subfolder].compactMap { $0 }.joined(separator: "/"),
            notes: diarizer == nil && !system.isEmpty ? [Transcript.diarizationMissingNote] : []
        )
        try transcript.write(to: job.folder)
        return job.folder.appendingPathComponent(Transcript.fileName)
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

    /// Explicit engine → the catalog default if installed → the smallest installed WhisperKit entry usable for
    /// `language` → "Needs model" (the default).
    private func resolveEngine(_ requested: String?, language: String) throws -> ModelEntry {
        if let requested {
            guard let entry = ModelCatalog.entry(id: requested), entry.kind == .asr, !entry.liveOnly,
                  Self.engines[entry.runtime] != nil else {
                throw TranscriptionError.unknownEngine(requested)
            }
            guard modelStore.isInstalled(entry.id) else { throw TranscriptionError.needsModel(entry.id) }
            return entry
        }
        let installed = ModelCatalog.asr.filter {
            !$0.liveOnly && Self.engines[$0.runtime] != nil && modelStore.isInstalled($0.id)
        }
        if let entry = installed.first(where: \.isDefault) { return entry }
        let fallback = installed
            .filter { $0.runtime == .whisperKit && $0.isSelectable(for: language) }
            .min { $0.diskMB < $1.diskMB }
        guard let fallback else { throw TranscriptionError.needsModel(ModelCatalog.defaultASR.id) }
        return fallback
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
