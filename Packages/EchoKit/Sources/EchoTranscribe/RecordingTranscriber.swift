import Accelerate
import EchoCore
import Foundation
import Synchronization

/// Transcribes a recording while it is made: takes the writer's 16 kHz blocks, cuts each channel into speech chunks
/// (`SpeechChunker`) and decodes them with one WhisperKit or Parakeet session (mic lines "Me", system lines
/// "Others"). Live chunks are at most 12 s and close at a short pause from 4 s, and each decoded chunk becomes one
/// line, so a line follows continuous speech within seconds. `finish()` at Stop hands the segments, in file time, and
/// the spans not transcribed (gaps) to the pipeline as a `PriorTranscription`.
///
/// Clock: the running sample count of each channel is file time. Every frame written to the CAF passes the sink,
/// silence padding included, and a pause adds no silence to the file. `RecordingEngine.pause()` and `stop()` flush
/// the writer synchronously (`ChannelWriter.pause()`/`stop()` run `finishSegment` on the writer queue inside
/// `pauseLocked`/`stopLocked`), so the last sink call lands before they return and before the `.idle` snapshot is
/// published: call `flush()` after `pause()` and `finish()` after `.idle`.
///
/// Lossless while it keeps up: closed chunks wait in memory (at most `maxQueuedSeconds` across both channels). A
/// chunk over the cap, a chunk the ASR failed on, and a chunk still waiting at Stop while the Mac is constrained
/// (with `deferWhenConstrained`) become gaps; a chunk whose peak is below `TranscriptionPipeline.silencePeak` is
/// covered as silence without decoding.
///
/// Holds the model gate as `.liveTranscript` from load to unload. The session lives only inside the decode task,
/// never in actor state (`WhisperKit` is not Sendable).
public actor RecordingTranscriber {
    public enum State: Sendable, Equatable {
        case loading, listening, paused
        case failed(String)
    }

    public enum Speaker: String, Sendable {
        case me = "Me", others = "Others"
    }

    /// One decoded chunk's text (`lineText`), not one per Whisper segment.
    public struct Line: Sendable, Equatable, Identifiable {
        public let id: Int
        public let speaker: Speaker
        public let text: String
    }

    public struct Snapshot: Sendable, Equatable {
        public var state: State
        /// Oldest first, at most `maxLines`.
        public var lines: [Line]
        /// Seconds of speech waiting to be decoded, both channels summed.
        public var pendingSeconds: Double
        /// Decoding is held back while the Mac is hot or in Low Power Mode (`deferWhenConstrained`); read at each
        /// publish, so within a tick.
        public var deferred: Bool

        public init(state: State, lines: [Line] = [], pendingSeconds: Double = 0, deferred: Bool = false) {
            self.state = state
            self.lines = lines
            self.pendingSeconds = pendingSeconds
            self.deferred = deferred
        }
    }

    /// Chunks per outcome, both channels summed.
    public struct Stats: Sendable, Equatable {
        public var transcribed = 0, silence = 0, dropped = 0, failed = 0, pending = 0
    }

    static let maxLines = 200
    static let sampleRate = SpeechChunker.sampleRate
    /// The most audio waiting to be decoded, both channels summed (~38 MB of Float).
    static let maxQueuedSeconds = 600
    /// How often the decode task wakes without a chunk: to free the model after `idleRelease`, and to see whether a
    /// constrained Mac recovered.
    static let tickInterval = Duration.seconds(15)
    /// The longest live chunk (at most the model window): the hard cut searches its last third, 8–12 s. With
    /// `liveShortPauseMinSeconds` it bounds the time to a live line under continuous speech (the batch tuning waits up
    /// to 30 s), while keeping chunks long enough that Whisper does not fall back on memorised boilerplate.
    static let liveMaxChunkSeconds = 12
    /// The length a live chunk needs before a 0.6 s pause closes it: `min(4·50, 600/2) = 200` frames, so from 4 s.
    static let liveShortPauseMinSeconds = 4

    /// Every change, for a single consumer; finished by `finish()` or `cancel()`.
    public nonisolated let updates: AsyncStream<Snapshot>
    private nonisolated let updatesContinuation: AsyncStream<Snapshot>.Continuation
    /// Filled by `append` on the writer queue, emptied by the decode task.
    private nonisolated let buffer: Mutex<Buffer>
    /// At most one pending wake-up: a chunk is waiting, the recording resumed, or the tick is due.
    private nonisolated let wake: AsyncStream<Void>
    private nonisolated let wakeContinuation: AsyncStream<Void>.Continuation
    private nonisolated let entry: ModelEntry
    private nonisolated let folder: URL
    private nonisolated let gate: ModelLoadGate
    private nonisolated let idleRelease: Duration?
    private nonisolated let deferWhenConstrained: Bool
    private nonisolated let language: @Sendable () -> String?
    private var snapshot = Snapshot(state: .loading)
    private var published: Snapshot?
    private var nextLineID = 0
    private var decoder: Task<Void, Never>?
    private var finished = false
    /// What `finish()` left; nil before it, or when the model never loaded or `cancel()` ran.
    public private(set) var stats: Stats?

    /// `entry` is an installed WhisperKit or Parakeet entry in `folder`. `idleRelease` frees the model once no chunk
    /// came for that long (a paused recording) and loads it again for the next one; nil keeps it until `finish()`.
    /// `deferWhenConstrained` stops decoding while the Mac is hot or in Low Power Mode. `language` gives the current
    /// marker language ("auto", a BCP-47 code, or nil = detect), read per block.
    public init(
        entry: ModelEntry, folder: URL, gate: ModelLoadGate, idleRelease: Duration? = nil,
        deferWhenConstrained: Bool, language: @escaping @Sendable () -> String?
    ) {
        self.entry = entry
        self.folder = folder
        self.gate = gate
        self.idleRelease = idleRelease
        self.deferWhenConstrained = deferWhenConstrained
        self.language = language
        buffer = Mutex(Buffer(maxChunkSeconds: min(entry.windowSeconds ?? 30, Self.liveMaxChunkSeconds),
                              shortPauseMinSeconds: Self.liveShortPauseMinSeconds))
        (updates, updatesContinuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(1))
        (wake, wakeContinuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(1))
    }

    /// Waits for the model gate, loads the model at `.utility` QoS and starts decoding.
    public func start() {
        guard decoder == nil, !finished else { return }
        publish(.loading)
        decoder = Task.detached(priority: .utility) { [self] in await decode() }
    }

    /// `RecordingEngine.liveSink`: splits the writer's interleaved Int16 block (see `RecordingSession.Channel`) and
    /// chunks it, on the writer queue, never on an audio IO thread.
    public nonisolated func append(interleaved samples: UnsafePointer<Int16>, frames: Int, channels: Int) {
        guard frames > 0, channels > max(RecordingSession.Channel.mic, RecordingSession.Channel.system) else { return }
        let mic = Self.channel(RecordingSession.Channel.mic, of: samples, frames: frames, channels: channels)
        let system = Self.channel(RecordingSession.Channel.system, of: samples, frames: frames, channels: channels)
        let language = Self.chunkLanguage(language())
        if buffer.withLock({ $0.append(mic: mic, system: system, language: language) }) { wakeContinuation.yield() }
    }

    /// On pause: closes the open chunk of each channel so it is decoded now.
    public func flush() {
        buffer.withLock { $0.flush(paused: true) }
        wakeContinuation.yield()
        publishRunning()
    }

    /// At Stop: decodes what is left (unless the Mac is constrained and `deferWhenConstrained`: then it stays a gap),
    /// unloads the model and returns what was transcribed; nil when no chunk was, or the model never loaded (the
    /// pipeline then transcribes the whole recording).
    public func finish() async -> PriorTranscription? {
        guard !finished else { return nil }
        finished = true
        guard buffer.withLock({ $0.loaded }) else {
            cancel()
            return nil
        }
        buffer.withLock {
            $0.flush(paused: false)
            $0.finishing = true
        }
        wakeContinuation.yield()
        wakeContinuation.finish()
        await decoder?.value
        decoder = nil
        updatesContinuation.finish()
        // `cancel()` may have run while the queue drained.
        guard let (result, duration) = buffer.withLock({ buffer -> ([Channel], Double)? in
            guard !buffer.closed else { return nil }
            defer { buffer.close() }
            return (buffer.channels, Double(buffer.samplesSeen) / Double(Self.sampleRate))
        }) else { return nil }
        stats = Self.stats(result)
        guard result.contains(where: { $0.chunks.contains { $0.outcome == .transcribed } }) else { return nil }
        let mic = result[RecordingSession.Channel.mic], system = result[RecordingSession.Channel.system]
        return PriorTranscription(
            engineID: entry.id, mic: mic.segments.sorted { $0.start < $1.start },
            system: system.segments.sorted { $0.start < $1.start },
            micGaps: PriorTranscription.gaps(mic.chunks), systemGaps: PriorTranscription.gaps(system.chunks),
            duration: duration)
    }

    /// Stops at once, keeping nothing; the decode task unloads the model on its way out.
    public func cancel() {
        finished = true
        buffer.withLock { $0.close() }
        wakeContinuation.finish()
        decoder?.cancel()
        decoder = nil
        updatesContinuation.finish()
    }

    // MARK: - Decode task

    /// Holds the model gate from load to unload.
    private nonisolated func decode() async {
        do {
            try await gate.withExclusiveModel(owner: .liveTranscript) { [self] in try await run() }
        } catch {
            buffer.withLock { $0.fail() }
            if !Task.isCancelled { await publish(.failed(error.localizedDescription)) }
        }
    }

    /// Owns the session from load to unload, and loads it again after an idle release.
    private nonisolated func run() async throws {
        let load = { [entry, folder] () async throws -> any TranscriptionSession in
            guard let session = try await TranscriptionSessions.load(entry: entry, folder: folder) else {
                throw TranscriptionError.unknownEngine(entry.id)
            }
            return session
        }
        let model = IdleModel(try await load(), idle: idleRelease, load: load, unload: { await $0.unload() })
        buffer.withLock { $0.loaded = true }
        await publishRunning()
        let tick = Task { [wakeContinuation] in
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.tickInterval)
                wakeContinuation.yield()
            }
        }
        defer { tick.cancel() }
        do {
            for await _ in wake {
                try await drain(model)
                if await model.releaseIfIdle() { MemoryRelief.trim() }
                await publishRunning()
            }
            try await drain(model)
        } catch {
            await model.release()
            throw error
        }
        await model.release()
    }

    /// Decodes the waiting chunks in order, unless the Mac is constrained and decoding waits for it.
    private nonisolated func drain(_ model: IdleModel<any TranscriptionSession>) async throws {
        while !(deferWhenConstrained && TranscriptionPipeline.isConstrained), let item = buffer.withLock({ $0.take() }) {
            let session = try await model.get()
            do {
                let segments = try await session.transcribe(item.chunk.samples, language: item.chunk.language)
                let offset = Double(item.chunk.startSample) / Double(Self.sampleRate)
                let shifted = segments.map {
                    RawSegment(start: $0.start + offset, end: $0.end + offset, text: $0.text,
                               detectedLanguage: $0.detectedLanguage, speaker: $0.speaker)
                }
                buffer.withLock { $0.complete(item, segments: shifted) }
                let mic = item.channel == RecordingSession.Channel.mic
                let rate = Double(Self.sampleRate)
                let span = Span(start: Double(item.chunk.startSample) / rate,
                                end: Double(item.chunk.startSample + item.chunk.samples.count) / rate)
                if let text = Self.liveLine(shifted, mic: mic, othersSpeak: {
                    buffer.withLock { $0.othersSpeak(during: span) }
                }) { await add(mic ? .me : .others, text) }
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                buffer.withLock { $0.complete(item, segments: nil) }
            }
        }
    }

    // MARK: - State

    /// Yields the snapshot when it changed.
    private func publish(_ state: State) {
        snapshot.state = state
        snapshot.pendingSeconds = buffer.withLock { $0.pendingSeconds }
        snapshot.deferred = deferWhenConstrained && TranscriptionPipeline.isConstrained
        guard snapshot != published else { return }
        published = snapshot
        updatesContinuation.yield(snapshot)
    }

    /// Listening or paused once loaded; leaves `loading` and `failed` alone.
    private func publishRunning() {
        guard snapshot.state != .loading || buffer.withLock({ $0.loaded }) else { return }
        if case .failed = snapshot.state { return }
        publish(buffer.withLock { $0.paused } ? .paused : .listening)
    }

    private func add(_ speaker: Speaker, _ text: String) {
        snapshot.lines.append(Line(id: nextLineID, speaker: speaker, text: text))
        nextLineID += 1
        snapshot.lines.removeFirst(max(0, snapshot.lines.count - Self.maxLines))
        publishRunning()
    }

    // MARK: - Helpers

    private static func stats(_ channels: [Channel]) -> Stats {
        var stats = Stats()
        for (_, outcome) in channels.flatMap(\.chunks) {
            switch outcome {
            case .transcribed: stats.transcribed += 1
            case .silence: stats.silence += 1
            case .dropped: stats.dropped += 1
            case .failed: stats.failed += 1
            case .pending: stats.pending += 1
            }
        }
        return stats
    }

    /// nil (detect) for an empty or "auto" marker, else the trimmed code.
    private static func chunkLanguage(_ language: String?) -> String? {
        let trimmed = language?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return trimmed.isEmpty || trimmed.lowercased() == "auto" ? nil : trimmed
    }

    /// A chunk's segments as one line: trimmed, blank ones skipped, joined with a space; nil when no text is left.
    static func lineText(_ segments: [RawSegment]) -> String? {
        let texts = segments.map { $0.text.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
        return texts.isEmpty ? nil : texts.joined(separator: " ")
    }

    /// The line a decoded chunk shows (`lineText`), or nil when it shows none: a mic line that is a lone "Merci."
    /// (`TranscriptMerger.isLoneMerci`) while `othersSpeak` is the residual of others' voices on a near-silent mic.
    /// `othersSpeak` is called only for a mic lone Merci. Live uses the energy chunk's span while the final transcript
    /// (`TranscriptMerger.isBleed`) uses Whisper segment times, so the two can rarely disagree; accepted, the final
    /// transcript is the source of truth.
    static func liveLine(_ segments: [RawSegment], mic: Bool, othersSpeak: () -> Bool) -> String? {
        guard let text = lineText(segments) else { return nil }
        return mic && TranscriptMerger.isLoneMerci(text) && othersSpeak() ? nil : text
    }

    private static func channel(_ index: Int, of samples: UnsafePointer<Int16>, frames: Int, channels: Int) -> [Float] {
        [Float](unsafeUninitializedCapacity: frames) { output, count in
            var scale: Float = 1 / 32_768
            vDSP_vflt16(samples + index, vDSP_Stride(channels), output.baseAddress!, 1, vDSP_Length(frames))
            vDSP_vsmul(output.baseAddress!, 1, &scale, output.baseAddress!, 1, vDSP_Length(frames))
            count = frames
        }
    }

    fileprivate static func peak(_ samples: [Float]) -> Float {
        var peak: Float = 0
        vDSP_maxmgv(samples, 1, &peak, vDSP_Length(samples.count))
        return peak
    }

    /// A chunk waiting to be decoded, and where its outcome is recorded.
    struct Item: Sendable {
        var channel: Int
        var index: Int
        var chunk: SpeechChunker.Chunk
    }

    /// One channel's chunker, chunk outcomes (file order) and decoded segments (file time).
    struct Channel: Sendable {
        var chunker: SpeechChunker
        var chunks: [(span: Span, outcome: PriorTranscription.ChunkOutcome)] = []
        var segments: [RawSegment] = []
    }

    /// Both channels grow in step (the writer writes equal frames per channel); index = `RecordingSession.Channel`.
    struct Buffer: Sendable {
        var channels: [Channel]
        /// The most samples waiting to be decoded, both channels summed.
        let maxQueuedSamples: Int
        var queue: [Item] = []
        var queuedSamples = 0
        var samplesSeen = 0
        /// The session loaded once; until then `finish()` has nothing to wait for.
        var loaded = false
        /// Between `flush()` and the next block.
        var paused = false
        /// `finish()` flushed the chunkers: no more blocks.
        var finishing = false
        /// The decode task ended with an error: chunks are failed, not queued.
        var failed = false
        var closed = false

        init(
            maxChunkSeconds: Int, shortPauseMinSeconds: Int = SpeechChunker.shortPauseMinSeconds,
            maxQueuedSamples: Int = RecordingTranscriber.maxQueuedSeconds * RecordingTranscriber.sampleRate
        ) {
            self.maxQueuedSamples = maxQueuedSamples
            let mic = Channel(chunker: SpeechChunker(maxChunkSeconds: maxChunkSeconds,
                                                     shortPauseMinSeconds: shortPauseMinSeconds))
            channels = [mic, mic]
        }

        var pendingSeconds: Double { Double(queuedSamples) / Double(RecordingTranscriber.sampleRate) }

        /// True when a chunk was queued or the recording resumed.
        mutating func append(mic: [Float], system: [Float], language: String?) -> Bool {
            guard !closed, !finishing else { return false }
            let resumed = paused
            paused = false
            samplesSeen += mic.count
            let micQueued = enqueue(channels[RecordingSession.Channel.mic].chunker.append(mic, language: language),
                                    channel: RecordingSession.Channel.mic)
            let systemQueued = enqueue(
                channels[RecordingSession.Channel.system].chunker.append(system, language: language),
                channel: RecordingSession.Channel.system)
            return resumed || micQueued || systemQueued
        }

        mutating func flush(paused: Bool) {
            guard !closed, !finishing else { return }
            self.paused = paused
            for channel in channels.indices { enqueue(channels[channel].chunker.flush(), channel: channel) }
        }

        /// Records each chunk: silence, dropped over the cap, failed after a decoder error, else queued (pending).
        @discardableResult
        mutating func enqueue(_ chunks: [SpeechChunker.Chunk], channel: Int) -> Bool {
            var queued = false
            for chunk in chunks {
                let rate = Double(RecordingTranscriber.sampleRate)
                let span = Span(start: Double(chunk.startSample) / rate,
                                end: Double(chunk.startSample + chunk.samples.count) / rate)
                let outcome: PriorTranscription.ChunkOutcome
                if RecordingTranscriber.peak(chunk.samples) < TranscriptionPipeline.silencePeak {
                    outcome = .silence
                } else if failed {
                    outcome = .failed
                } else if queuedSamples + chunk.samples.count > maxQueuedSamples {
                    outcome = .dropped
                } else {
                    outcome = .pending
                    queue.append(Item(channel: channel, index: channels[channel].chunks.count, chunk: chunk))
                    queuedSamples += chunk.samples.count
                    queued = true
                }
                channels[channel].chunks.append((span, outcome))
            }
            return queued
        }

        /// True when the system channel has speech near `span` (widened by `TranscriptMerger.bleedTimeTolerance`): a
        /// chunk that is not silence overlapping it, or the open chunk starting by its end.
        func othersSpeak(during span: Span) -> Bool {
            guard !closed else { return false }
            let start = span.start - TranscriptMerger.bleedTimeTolerance
            let end = span.end + TranscriptMerger.bleedTimeTolerance
            let system = channels[RecordingSession.Channel.system]
            if system.chunks.contains(where: { $0.outcome != .silence && $0.span.start < end && $0.span.end > start }) {
                return true
            }
            guard let open = system.chunker.openChunk else { return false }
            return Double(open.startSample) / Double(RecordingTranscriber.sampleRate) <= end
        }

        mutating func take() -> Item? {
            guard !closed, !queue.isEmpty else { return nil }
            let item = queue.removeFirst()
            queuedSamples -= item.chunk.samples.count
            return item
        }

        /// `segments` nil: the ASR failed on the chunk.
        mutating func complete(_ item: Item, segments: [RawSegment]?) {
            guard !closed else { return }
            channels[item.channel].chunks[item.index].outcome = segments == nil ? .failed : .transcribed
            channels[item.channel].segments += segments ?? []
        }

        /// The decode task is gone: what waits, and every later chunk, is a failed chunk.
        mutating func fail() {
            failed = true
            for item in queue { channels[item.channel].chunks[item.index].outcome = .failed }
            queue = []
            queuedSamples = 0
        }

        mutating func close() {
            closed = true
            queue = []
            queuedSamples = 0
            channels = []
        }
    }
}
