import Accelerate
import AudioToolbox
import AVFoundation
import EchoAI
import EchoCore
import Foundation

/// Batch transcription of recording folders: extract each channel to 16 kHz mono, skip silent channels, ASR mic,
/// ASR system, diarize system (the cloud diarizer when given, else or after its failure the local one), merge, write
/// `transcript.json`. Local jobs run one at a time; a cloud job starts at once, in parallel with any other job.
/// Models are loaded inside each step and unloaded before the next, while the job holds the `ModelLoadGate` (it waits
/// while a recording transcriber holds it); a cloud job takes it only for a local diarization. A job with a usable
/// `prior` keeps its segments and transcribes only its gaps, taking the gate only for gap fill or diarization. Never
/// touches EchoAudio: `AppModel` runs compression first.
public actor TranscriptionPipeline {
    public typealias EngineFactory = @Sendable (_ entry: ModelEntry, _ folder: URL) -> any TranscriptionEngine

    /// Batch engines by catalog runtime. Apple Speech only on macOS 26+ without `--force-fallbacks`; system
    /// engines get the Models root as `folder` and ignore it.
    public static let engines: [ModelEntry.Runtime: EngineFactory] = {
        var engines: [ModelEntry.Runtime: EngineFactory] = [
            .whisperKit: { WhisperKitEngine(entry: $0, folder: $1) },
            // Parakeet v3 / Ultra; the diarizer is not an ASR entry, so it never resolves here.
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

    /// Returns the `transcript.json` URL. A local job runs after every local job queued before it; a cloud job
    /// starts at once. `cloud`: the job's cloud engine (resolved by the app from settings and the Keychain), which
    /// replaces `job.engineID`'s resolution.
    /// `cloudDiarizer`: labels the system channel's speakers when the ASR does not (its text is dropped); unused
    /// when `job.diarize` is false.
    public func transcribe(
        _ job: TranscriptionJob, cloud: CloudEngine? = nil, cloudDiarizer: CloudEngine? = nil
    ) async throws -> URL {
        emit(job, .queued, 0)
        let previous = cloud == nil ? tail : nil
        let task = Task(priority: .utility) {
            await previous?.value
            return try await self.run(job, cloud: cloud, cloudDiarizer: cloudDiarizer)
        }
        if cloud == nil { tail = Task { _ = await task.result } }
        return try await task.value
    }

    // MARK: - Job

    private struct ChannelSource: Sendable {
        var url: URL
        var channel: Int
    }

    /// Transcribes one channel: the 16 kHz extract, its source channel, which channel it is, and progress 0...1. Not
    /// `@Sendable`: the gap fill's closure owns a non-Sendable session for the whole job.
    private typealias ChannelASR = (
        _ extracted: URL, _ source: ChannelSource, _ channel: Transcript.Channel,
        _ progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment]

    private func run(_ job: TranscriptionJob, cloud: CloudEngine?, cloudDiarizer: CloudEngine?) async throws -> URL {
        var started = false
        // Creates `conversation.json` when missing (transcribed before compression); nil while unreadable.
        var stored: RecordingSession?
        do {
            if let cloud, let prior = job.prior, prior.engineID == cloud.engineID,
               let session = try? RecordingSession.read(from: job.folder),
               let sources = try? channelSources(session: session, job: job), sources.mic != nil,
               let audio = ConversationFiles.audioURL(in: job.folder, fileName: session.audioFileName),
               let duration = try? MonoAudio.duration(of: audio),
               prior.isUsable(
                   engineID: cloud.engineID, engineFallback: nil, languageOverride: job.language,
                   audioDuration: duration) {
                // A cloud realtime stream as the transcript: only its gaps are uploaded, and a diarizer runs only when
                // the stream labelled no speakers.
                stored = session
                let streamLabels = job.diarize && prior.system.contains { $0.speaker?.isEmpty == false }
                let (diarizer, cloudDiarizer) = streamLabels ? (nil, nil) : diarizers(for: job, cloud: cloudDiarizer)
                let fillsGaps = !prior.micGaps.isEmpty || (sources.system != nil && !prior.systemGaps.isEmpty)
                    || (sources.system != nil && streamLabels && !prior.systemBridges.isEmpty)
                let diarizes = sources.system != nil && (diarizer != nil || cloudDiarizer != nil)
                if (fillsGaps || diarizes) && deferWhenConstrained && !job.processNow { await waitWhileConstrained(job) }
                started = true
                let url = try await transcribeCloudPrior(
                    job, prior: prior, session: session, sources: sources, cloud: cloud, streamLabels: streamLabels,
                    duration: duration, diarizer: diarizer, cloudDiarizer: cloudDiarizer)
                Self.updateConversation(in: job.folder, session: session) {
                    $0.status = .transcribed
                    $0.hasTranscript = true
                    $0.sizeBytes = LibraryStore.sizeOnDisk(of: job.folder)
                }
                emit(job, .finished, 1)
                return url
            }
            if cloud == nil, let prior = job.prior, let session = try? RecordingSession.read(from: job.folder),
               let sources = try? channelSources(session: session, job: job), sources.mic != nil,
               let entry = await usableEntry(for: prior, job: job, session: session) {
                // Only gap fill and diarization need a model, so only they take the gate or wait for a cool Mac.
                stored = session
                let (diarizer, cloudDiarizer) = diarizers(for: job, cloud: cloudDiarizer)
                let fillsGaps = !prior.micGaps.isEmpty || (sources.system != nil && !prior.systemGaps.isEmpty)
                let diarizes = sources.system != nil && !prior.system.isEmpty && (diarizer != nil || cloudDiarizer != nil)
                var modelFolder: URL?
                if fillsGaps {
                    guard Self.isUsable(entry, in: modelStore), let folder = modelStore.folder(for: entry.id) else {
                        throw TranscriptionError.needsModel(entry.id)
                    }
                    modelFolder = folder
                }
                if (fillsGaps || diarizes) && deferWhenConstrained && !job.processNow { await waitWhileConstrained(job) }
                started = true
                let url = try await transcribePrior(
                    job, prior: prior, session: session, sources: sources, entry: entry, modelFolder: modelFolder,
                    diarizer: diarizer, cloudDiarizer: cloudDiarizer)
                Self.updateConversation(in: job.folder, session: session) {
                    $0.status = .transcribed
                    $0.hasTranscript = true
                    $0.sizeBytes = LibraryStore.sizeOnDisk(of: job.folder)
                }
                emit(job, .finished, 1)
                return url
            }
            if deferWhenConstrained && !job.processNow { await waitWhileConstrained(job) }
            guard var session = try? RecordingSession.read(from: job.folder) else {
                throw TranscriptionError.sessionUnreadable
            }
            stored = session
            // Never written back: the override only applies to this run.
            if let language = job.language { session.languageMarkers = [.init(fileTime: 0, language: language)] }
            let sources = try channelSources(session: session, job: job)
            let ranges = LanguageRange.ranges(from: session.languageMarkers)
            let (diarizer, cloudDiarizer) = diarizers(for: job, cloud: cloudDiarizer)

            let url: URL
            if let cloud {
                // No local ASR model: only a local diarization takes the gate (inside `process`).
                started = true
                Self.updateConversation(in: job.folder, session: session) { $0.status = .transcribing }
                let language = Self.cloudLanguage(ranges)
                url = try await process(
                    job, session: session, sources: sources, engineID: cloud.engineID,
                    modelID: cloud.model.isEmpty ? nil : cloud.model, engineFallback: nil, diarizer: diarizer,
                    cloudDiarizer: cloudDiarizer, gateDiarization: true
                ) { @Sendable _, source, channel, _ in
                    try await Self.cloudSegments(
                        cloud, source: source, language: language, diarize: job.diarize && channel == .system)
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
                    Self.updateConversation(in: job.folder, session: session) { $0.status = .transcribing }
                    return try await process(
                        job, session: session, sources: sources, engineID: entry.id,
                        modelID: [entry.repo, entry.subfolder].compactMap { $0 }.joined(separator: "/"),
                        engineFallback: engineFallback, diarizer: diarizer, cloudDiarizer: cloudDiarizer,
                        gateDiarization: false
                    ) { extracted, _, _, progress in
                        try await engine.transcribe(channelURL: extracted, languageRanges: ranges, progress: progress)
                    }
                }
            }
            Self.updateConversation(in: job.folder, session: session) {
                $0.status = .transcribed
                $0.hasTranscript = true
                $0.sizeBytes = LibraryStore.sizeOnDisk(of: job.folder)
            }
            emit(job, .finished, 1)
            return url
        } catch TranscriptionError.needsModel(let id) {
            if started { Self.updateConversation(in: job.folder, session: stored) { $0.status = .ready } }
            emit(job, .needsModel, 0, detail: id)
            throw TranscriptionError.needsModel(id)
        } catch {
            // Also before `started`, so an unreadable session or audio shows "Failed — Retry" on the card.
            Self.updateConversation(in: job.folder, session: stored) { $0.status = .transcriptionFailed }
            emit(job, .failed, 0, detail: error.localizedDescription)
            throw error
        }
    }

    /// Extracts each channel (skipping silent ones), runs `asr` on it, diarizes the system channel unless `asr`
    /// returned provider speaker labels, merges and writes `transcript.json`. Diarization: `cloudDiarizer` first
    /// (never takes the gate itself); `diarizer` when there is none, or after it failed or labeled no speaker
    /// (`Transcript.cloudDiarizationFailedNote`). `gateDiarization`: the caller does not hold the `ModelLoadGate`
    /// (cloud ASR), so the local diarizer takes it. With `prior`, each channel is its segments plus `asr` of its gaps:
    /// a channel is extracted only when it has gaps, or for the system channel's diarization. Nonisolated, so `asr`
    /// may hold a non-Sendable session across both channels.
    private nonisolated func process(
        _ job: TranscriptionJob, session: RecordingSession, sources: (mic: ChannelSource?, system: ChannelSource?),
        engineID: String, modelID: String?, engineFallback: String?, diarizer: SpeakerDiarizer?,
        cloudDiarizer: CloudEngine?, gateDiarization: Bool, prior: PriorTranscription? = nil, asr: ChannelASR
    ) async throws -> URL {
        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("EchoDiar-transcribe-\(UUID().uuidString)")
        try fm.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: work) }

        var mic = prior?.mic ?? []
        if let source = sources.mic, prior.map({ !$0.micGaps.isEmpty }) ?? true {
            emit(job, .extracting, 0)
            if let url = try await extract(source, to: work.appendingPathComponent("mic.caf")) {
                emit(job, .transcribingMic, 0.05)
                mic += try await asr(url, source, .mic) { [self] fraction in
                    emit(job, .transcribingMic, 0.05 + 0.35 * fraction)
                }
                try? fm.removeItem(at: url)
            }
        }

        var system = prior?.system ?? []
        var turns: [DiarizationTurn] = []
        var notes: [String] = []
        let fillsSystem = prior.map { !$0.systemGaps.isEmpty } ?? true
        let diarizes = !system.isEmpty && (diarizer != nil || cloudDiarizer != nil)
        if let source = sources.system, fillsSystem || diarizes {
            emit(job, .extracting, 0.4)
            if let url = try await extract(source, to: work.appendingPathComponent("system.caf")) {
                if fillsSystem {
                    emit(job, .transcribingSystem, 0.45)
                    system += try await asr(url, source, .system) { [self] fraction in
                        emit(job, .transcribingSystem, 0.45 + 0.35 * fraction)
                    }
                }
                if !system.isEmpty, !system.contains(where: { $0.speaker != nil }) {
                    if let cloudDiarizer {
                        emit(job, .diarizing, 0.8)
                        do {
                            turns = try await Self.cloudTurns(
                                cloudDiarizer, source: source,
                                language: Self.cloudLanguage(LanguageRange.ranges(from: session.languageMarkers)))
                        } catch let error as CancellationError {
                            throw error
                        } catch {
                            // A cancelled upload throws URLError, not CancellationError.
                            if Task.isCancelled { throw error }
                            // Error kind only: a provider message can carry the request or response body.
                            NSLog("EchoDiar: cloud diarization (%@) failed: %@",
                                  cloudDiarizer.name, String(reflecting: type(of: error)))
                        }
                        if turns.isEmpty { notes.append(Transcript.cloudDiarizationFailedNote) }
                    }
                    if turns.isEmpty, let diarizer {
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
        }

        if prior != nil {
            mic.sort { $0.start < $1.start }
            system.sort { $0.start < $1.start }
        }
        emit(job, .merging, 0.95)
        let providerSpeakers = system.contains { $0.speaker != nil }
        if job.diarize && diarizer == nil && turns.isEmpty && !system.isEmpty && !providerSpeakers {
            notes.append(Transcript.diarizationMissingNote)
        }
        let transcript = Transcript(
            segments: TranscriptMerger.merge(
                mic: mic, system: system, turns: turns, languageMarkers: session.languageMarkers),
            engineID: engineID,
            modelID: modelID,
            engineFallback: engineFallback,
            notes: notes
        )
        // A re-transcription keeps the transcript it replaces as a numbered version.
        try transcript.write(to: job.folder, keepingPrevious: true)
        return job.folder.appendingPathComponent(Transcript.fileName)
    }

    /// `prior`'s segments plus its gaps, transcribed with one session of `entry` loaded on the first chunk of speech
    /// and unloaded before the gate is released; `modelFolder` nil = no gaps, so no ASR model and no gate (a local
    /// diarizer takes it itself).
    private func transcribePrior(
        _ job: TranscriptionJob, prior: PriorTranscription, session: RecordingSession,
        sources: (mic: ChannelSource?, system: ChannelSource?), entry: ModelEntry, modelFolder: URL?,
        diarizer: SpeakerDiarizer?, cloudDiarizer: CloudEngine?
    ) async throws -> URL {
        let modelID = [entry.repo, entry.subfolder].compactMap { $0 }.joined(separator: "/")
        Self.updateConversation(in: job.folder, session: session) { $0.status = .transcribing }
        guard let modelFolder else {
            return try await process(
                job, session: session, sources: sources, engineID: entry.id, modelID: modelID, engineFallback: nil,
                diarizer: diarizer, cloudDiarizer: cloudDiarizer, gateDiarization: true, prior: prior
            ) { _, _, _, _ in [] }
        }
        return try await gate.withExclusiveModel(owner: .transcriptionJob) { [self] in
            let ranges = LanguageRange.ranges(from: session.languageMarkers)
            // Created, used and unloaded here only.
            var model: (any TranscriptionSession)?
            do {
                let url = try await process(
                    job, session: session, sources: sources, engineID: entry.id, modelID: modelID,
                    engineFallback: nil, diarizer: diarizer, cloudDiarizer: cloudDiarizer, gateDiarization: false,
                    prior: prior
                ) { extracted, _, channel, progress in
                    try await Self.fillGaps(
                        channel == .mic ? prior.micGaps : prior.systemGaps, of: extracted, entry: entry,
                        folder: modelFolder, ranges: ranges, session: &model, progress: progress)
                }
                await model?.unload()
                return url
            } catch {
                await model?.unload()
                throw error
            }
        }
    }

    /// Covered audio a gap upload takes on each side of the gap on a channel without stream speaker labels (mic, or a
    /// system stream with none); a labelled channel takes `SpeakerLabelReconciler.labelOverlapSeconds`.
    static let gapPaddingSeconds = 0.5

    /// A cloud realtime `prior` as the transcript: each channel's gaps go to `cloud` (`fillCloudGaps`), then `process`
    /// merges, with no gaps left, so nothing else is uploaded unless a diarizer is given. `streamLabels`: `job.diarize`
    /// is on and the stream labelled the system channel's speakers, so its gaps are diarized and bridged onto those
    /// labels and the caller passes no diarizer, and the audio around each resume with scoped labels is uploaded only
    /// to map them back (`fillCloudGaps`' bridges). With `job.diarize` off the stream labels are dropped (one speaker).
    /// The transcript records the realtime model.
    private func transcribeCloudPrior(
        _ job: TranscriptionJob, prior: PriorTranscription, session: RecordingSession,
        sources: (mic: ChannelSource?, system: ChannelSource?), cloud: CloudEngine, streamLabels: Bool,
        duration: Double, diarizer: SpeakerDiarizer?, cloudDiarizer: CloudEngine?
    ) async throws -> URL {
        Self.updateConversation(in: job.folder, session: session) { $0.status = .transcribing }
        let language = Self.cloudLanguage(LanguageRange.ranges(from: session.languageMarkers))
        var filled = prior
        if !job.diarize {
            filled.system = prior.system.map { segment in
                var segment = segment
                segment.speaker = nil
                return segment
            }
        }
        if let source = sources.mic, !prior.micGaps.isEmpty {
            emit(job, .transcribingMic, 0.05)
            filled.mic = try await Self.fillCloudGaps(
                prior.micGaps, into: filled.mic, source: source, cloud: cloud, language: language, labels: false,
                duration: duration
            ) { [self] fraction in emit(job, .transcribingMic, 0.05 + 0.35 * fraction) }
        }
        if let source = sources.system, !prior.systemGaps.isEmpty || (streamLabels && !prior.systemBridges.isEmpty) {
            emit(job, .transcribingSystem, 0.45)
            filled.system = try await Self.fillCloudGaps(
                prior.systemGaps, bridges: prior.systemBridges, into: filled.system, source: source, cloud: cloud,
                language: language, labels: streamLabels, duration: duration
            ) { [self] fraction in emit(job, .transcribingSystem, 0.45 + 0.35 * fraction) }
        }
        filled.micGaps = []
        filled.systemGaps = []
        filled.systemBridges = []
        return try await process(
            job, session: session, sources: sources, engineID: cloud.engineID,
            modelID: prior.modelID ?? (cloud.model.isEmpty ? nil : cloud.model), engineFallback: nil,
            diarizer: diarizer, cloudDiarizer: cloudDiarizer, gateDiarization: true, prior: filled
        ) { _, _, _, _ in [] }
    }

    /// One channel's `segments` with each of its `gaps` transcribed by `cloud` from the compressed source: the gap
    /// padded into covered audio (`labelOverlapSeconds` each side with `labels`, else `gapPaddingSeconds`), clamped to
    /// `0...duration`, sent through the range API (split under the provider's cap) and joined by `joinGap`. `labels`:
    /// the upload asks for speaker labels and they are bridged onto the stream's. With `labels`, each of `bridges`
    /// (file seconds of a resume with session-scoped labels and no gap) is a zero-length gap: its upload adds no
    /// segment and only maps the labels first heard after it back; none is uploaded when no label is new after it (a
    /// gap's padding bridged it) or the batch model only streams. In file order, so each later span bridges onto the
    /// earlier ones.
    private static func fillCloudGaps(
        _ gaps: [Span], bridges: [Double] = [], into segments: [RawSegment], source: ChannelSource,
        cloud: CloudEngine, language: String?, labels: Bool, duration: Double, progress: @Sendable (Double) -> Void
    ) async throws -> [RawSegment] {
        guard !CloudTranscription.isStreamingOnly(cloud.model) else {
            if gaps.isEmpty { return segments }
            throw TranscriptionError.cloud(
                provider: cloud.name, reason: "\(cloud.model) only streams live audio; pick another model in Settings")
        }
        let padding = labels ? SpeakerLabelReconciler.labelOverlapSeconds : gapPaddingSeconds
        let spans = (gaps + (labels ? bridges.map { Span(start: $0, end: $0) } : [])).sorted { $0.start < $1.start }
        var segments = segments.sorted { $0.start < $1.start }
        for (index, gap) in spans.enumerated() {
            try Task.checkCancellation()
            let lower = max(0, gap.start - padding)
            let upper = min(duration, gap.end + padding)
            guard gap.end >= gap.start, upper > lower else { continue }
            if gap.end == gap.start, !hasNewLabels(segments, after: gap.start) { continue }
            let response: [CloudSegment]
            do {
                response = try await CloudTranscription.transcribe(
                    source: source.url, channel: source.channel, range: lower...upper, with: cloud.transcriber,
                    language: language, diarize: labels)
            } catch let error as CancellationError {
                throw error
            } catch {
                throw TranscriptionError.cloud(provider: cloud.name, reason: CloudTranscription.failureReason(error))
            }
            segments = joinGap(response, gap: gap, upload: lower...upper, into: segments, labels: labels, partIndex: index + 1)
            progress(Double(index + 1) / Double(spans.count))
        }
        return segments
    }

    /// Whether a speaker label of `segments` is first heard at or after `time`.
    private static func hasNewLabels(_ segments: [RawSegment], after time: Double) -> Bool {
        var before = Set<String>(), after = Set<String>()
        for segment in segments {
            guard let speaker = segment.speaker, !speaker.isEmpty else { continue }
            if segment.start < time { before.insert(speaker) } else { after.insert(speaker) }
        }
        return !after.subtracting(before).isEmpty
    }

    /// `segments` (one channel, file time) with a gap's upload `response` (file time, covering `upload` = the gap and
    /// its padding) joined, sorted by start. The response's segments whose midpoint is inside `gap` are kept; the
    /// padding's only serve the labels. Without `labels` every speaker label is dropped. With `labels`, two steps:
    /// the response's labels map onto the labels before the gap by the left padding (`SpeakerLabelReconciler`, a label
    /// with no match gets its own `"p<N>:"` scope, `partIndex` and up, so it never merges); then each label first heard
    /// after the gap (the session after an unplanned disconnect, `"r<N>:"`) takes the label of (before + response)
    /// it matches in the right padding, unless that label is already in use after the gap; a label with no match
    /// keeps its own. Pure, for tests.
    static func joinGap(
        _ response: [CloudSegment], gap: Span, upload: ClosedRange<Double>, into segments: [RawSegment], labels: Bool,
        partIndex: Int
    ) -> [RawSegment] {
        func inGap(_ segment: CloudSegment) -> Bool {
            let middle = (segment.start + segment.end) / 2
            return middle >= gap.start && middle < gap.end
        }
        func label(_ speaker: String?) -> String? { speaker.flatMap { $0.isEmpty ? nil : $0 } }
        func raw(_ segment: CloudSegment) -> RawSegment {
            RawSegment(
                start: segment.start, end: segment.end, text: segment.text, detectedLanguage: segment.language,
                speaker: labels ? label(segment.speaker) : nil)
        }
        guard labels else {
            return (segments + response.filter(inGap).map(raw)).sorted { $0.start < $1.start }
        }
        let existing = segments.map {
            CloudSegment(start: $0.start, end: $0.end, text: $0.text, speaker: $0.speaker, language: $0.detectedLanguage)
        }
        // Step 1: the gap onto the session before. Every existing label counts for the scope; only the left padding
        // counts for the matches.
        let left = upload.lowerBound...max(upload.lowerBound, gap.start)
        let gapMapping = SpeakerLabelReconciler.labels(
            for: response, earlier: existing, overlap: left, partIndex: partIndex)
        let mapped = response.map { segment in
            var segment = segment
            segment.speaker = label(segment.speaker).flatMap { gapMapping[$0] }
            return segment
        }
        // Step 2: the session after onto (before + gap).
        let before = existing.filter { $0.start < gap.start }
        let after = existing.filter { $0.start >= gap.start }
        let beforeLabels = Set(before.compactMap { label($0.speaker) })
        let afterLabels = Set(after.compactMap { label($0.speaker) })
        let newLabels = afterLabels.subtracting(beforeLabels)
        let earlier = before + mapped
        let earlierLabels = Set(earlier.compactMap { label($0.speaker) })
        let right = min(max(gap.end, upload.lowerBound), upload.upperBound)...upload.upperBound
        let afterMapping = newLabels.isEmpty ? [:] : SpeakerLabelReconciler.labels(
            for: after.filter { label($0.speaker).map(newLabels.contains) ?? false }, earlier: earlier, overlap: right,
            partIndex: partIndex
        ).filter { earlierLabels.contains($0.value) && !afterLabels.contains($0.value) }
        let bridged = segments.map { segment in
            var segment = segment
            if let speaker = label(segment.speaker), let target = afterMapping[speaker] { segment.speaker = target }
            return segment
        }
        return (bridged + mapped.filter(inGap).map(raw)).sorted { $0.start < $1.start }
    }

    /// The gaps of one channel's 16 kHz extract, in file time: each read in slabs of at most `slabSeconds` and cut
    /// into speech chunks (`SpeechChunker`), so silence is not decoded; a chunk below `silencePeak` is skipped, and
    /// each chunk gets the language of `ranges` at its start. `session` is loaded on the first chunk to decode.
    static func fillGaps(
        _ gaps: [Span], of extract: URL, entry: ModelEntry, folder: URL, ranges: [LanguageRange],
        session: inout (any TranscriptionSession)?, progress: @Sendable (Double) -> Void
    ) async throws -> [RawSegment] {
        let duration = try MonoAudio.duration(of: extract)
        let total = max(1, gaps.reduce(0) { $0 + max(0, min($1.end, duration) - $1.start) })
        var done = 0.0
        var segments: [RawSegment] = []
        for gap in gaps {
            var slabStart = max(0, gap.start)
            let gapEnd = min(gap.end, duration)
            while slabStart < gapEnd {
                try Task.checkCancellation()
                let slabEnd = min(slabStart + slabSeconds, gapEnd)
                var chunker = SpeechChunker(maxChunkSeconds: entry.windowSeconds ?? 30)
                var chunks = chunker.append(try MonoAudio.read(extract, from: slabStart, to: slabEnd), language: nil)
                chunks += chunker.flush()
                for chunk in chunks where peak(chunk.samples) >= silencePeak {
                    let start = slabStart + Double(chunk.startSample) / MonoAudio.sampleRate
                    if session == nil {
                        guard let loaded = try await TranscriptionSessions.load(entry: entry, folder: folder) else {
                            throw TranscriptionError.unknownEngine(entry.id)
                        }
                        session = loaded
                    }
                    let language = ranges.last { $0.start <= start }?.language
                    segments += try await session!.transcribe(chunk.samples, language: language).map {
                        RawSegment(start: $0.start + start, end: $0.end + start, text: $0.text,
                                   detectedLanguage: $0.detectedLanguage, speaker: $0.speaker)
                    }
                }
                done += slabEnd - slabStart
                progress(min(1, done / total))
                slabStart = slabEnd
            }
        }
        return segments
    }

    /// Audio read into memory at once by the gap fill, like the batch engines' slabs (10 min is about 38 MB).
    static let slabSeconds = 600.0

    private static func peak(_ samples: [Float]) -> Float {
        var peak: Float = 0
        vDSP_maxmgv(samples, 1, &peak, vDSP_Length(samples.count))
        return peak
    }

    /// The entry that keeps `prior`: `job`'s engine, else the prior's (the one resolved when the recording started),
    /// when `prior.isUsable` for it, the session's language fallback, `job.language` and the session audio's
    /// duration; nil runs the whole batch.
    private func usableEntry(
        for prior: PriorTranscription, job: TranscriptionJob, session: RecordingSession
    ) async -> ModelEntry? {
        guard let entry = ModelCatalog.entry(id: job.engineID ?? prior.engineID), Self.transcribesChunks(entry),
              let audio = ConversationFiles.audioURL(in: job.folder, fileName: session.audioFileName),
              let duration = try? MonoAudio.duration(of: audio) else { return nil }
        let firstLanguage = session.languageMarkers.min { $0.fileTime < $1.fileTime }?.language ?? "auto"
        let (resolved, engineFallback) = await fallback(
            for: entry, markers: session.languageMarkers, language: firstLanguage)
        let usable = prior.isUsable(
            engineID: resolved.id, engineFallback: engineFallback, languageOverride: job.language,
            audioDuration: duration)
        return usable ? resolved : nil
    }

    /// The job's local and cloud diarizers; both nil when `job.diarize` is false.
    private func diarizers(for job: TranscriptionJob, cloud: CloudEngine?) -> (SpeakerDiarizer?, CloudEngine?) {
        let hint = job.speakerCountHint ?? speakerCountHint
        let diarizerID = job.diarize ? Self.localDiarizerID(
            preferred: job.diarizerID, speakerCountHint: hint, isInstalled: { modelStore.isInstalled($0) }) : nil
        let diarizer = diarizerID.flatMap { id in
            modelStore.folder(for: id).map { SpeakerDiarizer(entryID: id, folder: $0, speakerCountHint: hint) }
        }
        return (diarizer, job.diarize ? cloud : nil)
    }

    /// One channel through a cloud API, from the compressed source (not the 16 kHz extract); errors other than a
    /// cancellation become `TranscriptionError.cloud` with a short reason. A streaming-only model fails before any
    /// upload (the file endpoints refuse it).
    private static func cloudSegments(
        _ cloud: CloudEngine, source: ChannelSource, language: String?, diarize: Bool
    ) async throws -> [RawSegment] {
        guard !CloudTranscription.isStreamingOnly(cloud.model) else {
            throw TranscriptionError.cloud(
                provider: cloud.name, reason: "\(cloud.model) only streams live audio; pick another model in Settings")
        }
        do {
            let segments = try await CloudTranscription.transcribe(
                source: source.url, channel: source.channel, with: cloud.transcriber, language: language, diarize: diarize)
            return segments.map {
                RawSegment(
                    start: $0.start, end: $0.end, text: $0.text, detectedLanguage: $0.language,
                    speaker: diarize ? $0.speaker : nil)
            }
        } catch let error as CancellationError {
            throw error
        } catch {
            throw TranscriptionError.cloud(provider: cloud.name, reason: CloudTranscription.failureReason(error))
        }
    }

    /// The system channel's speaker turns from a cloud diarizer, from the compressed source (not the 16 kHz
    /// extract); the text it returns is dropped.
    private static func cloudTurns(
        _ cloud: CloudEngine, source: ChannelSource, language: String?
    ) async throws -> [DiarizationTurn] {
        let segments = try await CloudTranscription.transcribe(
            source: source.url, channel: source.channel, with: cloud.transcriber, language: language, diarize: true)
        return CloudTranscription.turns(from: segments)
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
            guard let entry = ModelCatalog.entry(id: requested), entry.kind == .asr,
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
        let installed = ModelCatalog.asr.filter { engines[$0.runtime] != nil && isUsable($0) }
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
        guard let id, let entry = ModelCatalog.entry(id: id), entry.kind == .asr,
              engines[entry.runtime] != nil, isUsable(entry), supports(entry, language: language) else { return nil }
        return id
    }

    /// The entry a recording transcriber runs so that the pipeline keeps its segments at Stop: the batch resolution
    /// for `engineID` (explicit, else `defaultEngine(language:isUsable:)`) and the recording's `language` ("auto" or a
    /// code), when it is a usable WhisperKit, Parakeet or Apple Speech entry (`transcribesLive`) that needs no language
    /// fallback; nil otherwise. Apple Speech is display only: its transcriber returns no prior, so the batch runs.
    public static func incrementalEntry(
        engineID: String?, language: String, isUsable: (ModelEntry) -> Bool
    ) -> ModelEntry? {
        let selected: ModelEntry?
        if let engineID {
            selected = ModelCatalog.entry(id: engineID).flatMap { entry in
                entry.kind == .asr && engines[entry.runtime] != nil && isUsable(entry) ? entry : nil
            }
        } else {
            selected = defaultEngine(language: language, isUsable: isUsable)
        }
        guard let entry = selected, transcribesLive(entry) else { return nil }
        let markers = LanguageRange.ranges(from: [.init(fileTime: 0, language: language)])
            .compactMap(\.language).map(WhisperKitEngine.whisperLanguage)
        let resolved = languageFallback(
            for: entry, supported: supportedLanguages(for: entry), markerLanguages: markers, language: language,
            isInstalled: { ModelCatalog.entry(id: $0).map(isUsable) ?? false })
        return resolved.engineFallback == nil ? entry : nil
    }

    /// The local models a recording transcriber can run, in catalog order, for the cloud live-transcript picker: the
    /// usable WhisperKit and Parakeet entries, and Apple Speech on macOS 26+ (`transcribesLive`). Any language: the
    /// picked id still goes through `incrementalEntry` at Start.
    public static func liveEntries(isUsable: (ModelEntry) -> Bool) -> [ModelEntry] {
        ModelCatalog.asr.filter { engines[$0.runtime] != nil && isUsable($0) && transcribesLive($0) }
    }

    /// A batch ASR entry that also loads as a `TranscriptionSession`: WhisperKit or Parakeet.
    static func transcribesChunks(_ entry: ModelEntry) -> Bool {
        entry.kind == .asr && (entry.runtime == .whisperKit || entry.runtime == .fluidAudio)
    }

    /// A batch ASR entry that runs as a recording transcriber: one that `transcribesChunks`, or Apple Speech, which
    /// streams each channel for the live display only (no prior).
    static func transcribesLive(_ entry: ModelEntry) -> Bool {
        transcribesChunks(entry) || (entry.kind == .asr && entry.runtime == .appleSpeech)
    }

    /// Selectable for `language` and, for a code, listed in `supportedLanguages(for:)` by its primary code. An
    /// empty list is unknown and counts as supported; "auto" needs no code.
    static func supports(_ entry: ModelEntry, language: String) -> Bool {
        guard entry.isSelectable(for: language) else { return false }
        let supported = supportedLanguages(for: entry)
        return language == "auto" || supported.isEmpty || supported.contains(WhisperKitEngine.whisperLanguage(language))
    }

    /// The local diarizer: `preferred` when it is an installed catalog diarizer, except a Nemotron pick gives way to
    /// an installed pyannote when `speakerCountHint` is above `SpeakerDiarizer.nemotronMaxSpeakers`; else (cloud
    /// ids and nil too) the first installed diarizer in catalog order; nil when none is installed.
    static func localDiarizerID(preferred: String?, speakerCountHint: Int?, isInstalled: (String) -> Bool) -> String? {
        let installed = ModelCatalog.diarization.map(\.id).filter(isInstalled)
        guard let preferred, installed.contains(preferred) else { return installed.first }
        let pyannote = ModelCatalog.defaultDiarizer.id
        if SpeakerDiarizer.nemotronPresets[preferred] != nil,
           (speakerCountHint ?? 0) > SpeakerDiarizer.nemotronMaxSpeakers, installed.contains(pyannote) {
            return pyannote
        }
        return preferred
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

    /// The Mac is hot or in Low Power Mode: jobs wait unless "Process now" (or deferral is off).
    public static var isConstrained: Bool {
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

    /// Read-modify-write of `conversation.json`. A missing one (transcribed before compression, or the DEBUG harness
    /// folder) is created from `session` with the session audio's duration, as `AudioCompressor` does; nothing is
    /// written without a session, or when the existing file is unreadable.
    private static func updateConversation(
        in folder: URL, session: RecordingSession?, _ change: (inout Conversation) -> Void
    ) {
        var conversation: Conversation
        if FileManager.default.fileExists(atPath: folder.appendingPathComponent(Conversation.fileName).path) {
            guard let existing = try? Conversation.read(from: folder) else { return }
            conversation = existing
        } else {
            guard let session else { return }
            let audio = ConversationFiles.audioURL(in: folder, fileName: session.audioFileName)
            let duration = audio.flatMap { try? MonoAudio.duration(of: $0) } ?? 0
            conversation = Conversation(session: session, folder: folder, duration: duration)
        }
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

    /// Samples from `start` to `end` seconds (nil = end of file) of a mono file, read in chunks straight into the
    /// result: a buffer as long as the range plus its copy would double the peak (230 MB for a 1 h diarizer input),
    /// and one `read` can return fewer frames than asked.
    static func read(_ url: URL, from start: Double, to end: Double?) throws -> [Float] {
        let file = try AVAudioFile(forReading: url, commonFormat: .pcmFormatFloat32, interleaved: false)
        let rate = file.processingFormat.sampleRate
        let first = min(file.length, AVAudioFramePosition(max(0, start) * rate))
        let last = min(file.length, end.map { AVAudioFramePosition($0 * rate) } ?? file.length)
        guard last > first else { return [] }
        let count = Int(last - first)
        guard let chunk = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: chunkFrames) else {
            throw TranscriptionError.audioUnreadable(url.lastPathComponent)
        }
        file.framePosition = first
        return try [Float](unsafeUninitializedCapacity: count) { samples, filled in
            while filled < count {
                try file.read(into: chunk, frameCount: min(chunkFrames, AVAudioFrameCount(count - filled)))
                let frames = Int(chunk.frameLength)
                guard frames > 0 else { break }
                (samples.baseAddress! + filled).initialize(from: chunk.floatChannelData![0], count: frames)
                filled += frames
            }
        }
    }

    static func duration(of url: URL) throws -> Double {
        let file = try AVAudioFile(forReading: url)
        return Double(file.length) / file.processingFormat.sampleRate
    }
}
