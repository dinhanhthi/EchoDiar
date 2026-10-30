import Accelerate
import EchoCore
import Foundation
import Synchronization

/// Transcribes a recording while it is made: takes the writer's 16 kHz blocks, cuts each channel into speech chunks
/// (`SpeechChunker`) and decodes them with one WhisperKit or Parakeet session (mic lines "Me", system lines
/// "Others"). Live chunks are at most 12 s and close at a 0.5 s pause from 2 s, and each decoded chunk becomes one
/// line, so a line follows continuous speech within seconds. While no closed chunk waits, an open chunk is decoded
/// every `partialStepSeconds` as greyed tentative text (`Snapshot.tentative`) that its line replaces, unless the
/// channel's last final decode was slow (`partialMaxDecodeSeconds`). `finish()` at Stop hands the segments, in file
/// time, and the spans not transcribed (gaps) to the pipeline as a `PriorTranscription`.
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
///
/// Apple Speech (macOS 26+) streams instead of chunking: each channel's blocks go to its own `AppleSpeechLiveStream`
/// runner, its volatile results become the speaker's tentative text and each final result a line. A missing language
/// is installed first (`.preparing`) while its audio waits in the runner's stream (at most `maxQueuedSeconds` across
/// both channels, else the transcriber fails). No model gate, no idle release, no constrained deferral: the model
/// lives in a system process. Apple live is display only: `finish()` returns nil and batch Apple Speech writes the
/// transcript after Stop, because the forced finals that bring a line ~0.6 s after a phrase lose words (measured WER
/// 11.6 % live against 0.8 % batch on the same recording).
public actor RecordingTranscriber {
    public enum State: Sendable, Equatable {
        /// `preparing`: Apple Speech is installing a language; its audio waits.
        case loading, preparing, listening, paused
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

    /// The text of a chunk still open, decoded so far; its line replaces it once the chunk closes.
    public struct Tentative: Sendable, Equatable {
        public let speaker: Speaker
        public let text: String
        public let startSample: Int
    }

    public struct Snapshot: Sendable, Equatable {
        public var state: State
        /// Oldest first, at most `maxLines`.
        public var lines: [Line]
        /// At most one per speaker, ordered by `startSample`; shown after `lines`.
        public var tentative: [Tentative]
        /// Seconds of speech waiting to be decoded, both channels summed.
        public var pendingSeconds: Double
        /// Decoding is held back while the Mac is hot or in Low Power Mode (`deferWhenConstrained`); read at each
        /// publish, so within a tick.
        public var deferred: Bool

        public init(
            state: State, lines: [Line] = [], tentative: [Tentative] = [], pendingSeconds: Double = 0,
            deferred: Bool = false
        ) {
            self.state = state
            self.lines = lines
            self.tentative = tentative
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
    /// to 30 s). Short chunks are left to the text filters (`speechText`, the lone-Merci rule) and the WER gate.
    static let liveMaxChunkSeconds = 12
    /// The pause that closes a live chunk of at least `liveShortPauseMinSeconds` (the batch tuning waits 0.6 s).
    static let liveShortPauseFrames = 25  // 0.5 s
    /// The length a live chunk needs before a 0.5 s pause closes it: `min(2·50, 600/2) = 100` frames, so from 2 s
    /// (measured: 1 s chunks lost real speech and invented short lines).
    static let liveShortPauseMinSeconds = 2
    /// The length an open chunk needs before its first partial decode (the greyed tentative text).
    static let partialMinSeconds = 1
    /// How much an open chunk grows between two partials of it: the first knob to turn if the decode budget is exceeded.
    static let partialStepSeconds = 2
    /// No partial on a channel while its last final decode took longer than this, so partials never starve the finals
    /// on a slow decoder: 0.2 × `liveMaxChunkSeconds`, a full 12 s chunk decoding within 20 %. A partial costs about
    /// as much as a final, and a Whisper decode costs about the same whatever the chunk length (it pads to a fixed
    /// window), so a share of the chunk's duration would turn partials off for short chunks. A ~10 s chunk at
    /// `partialStepSeconds` 2 costs ~5 partials + 1 final, twice that while both channels speak.
    static let partialMaxDecodeSeconds = 2.4
    /// The speech waiting to be decoded from which the live transcript says how far behind it is (`behindSeconds`):
    /// `pendingSeconds` sums both channels, and one waiting chunk per channel is up to 2 × `liveMaxChunkSeconds`
    /// (24 s) while the decoder keeps up, so below this nothing is behind.
    static let behindNoticeSeconds = 25

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
    /// The Apple Speech streams; nil for WhisperKit and Parakeet.
    private nonisolated let live: Live?
    private var snapshot = Snapshot(state: .loading)
    private var published: Snapshot?
    private var nextLineID = 0
    private var decoder: Task<Void, Never>?
    private var finished = false
    /// What `finish()` left; nil before it, or when the model never loaded or `cancel()` ran.
    public private(set) var stats: Stats?

    /// `entry` is an installed WhisperKit or Parakeet entry in `folder`, or Apple Speech (`folder` and `gate` unused).
    /// `idleRelease` frees the model once no chunk came for that long (a paused recording) and loads it again for the
    /// next one; nil keeps it until `finish()`. `deferWhenConstrained` stops decoding while the Mac is hot or in Low
    /// Power Mode. Apple Speech ignores both. `language` gives the current marker language ("auto", a BCP-47 code, or
    /// nil = detect; Apple Speech: the default language), read per block.
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
                              shortPauseMinSeconds: Self.liveShortPauseMinSeconds,
                              shortPauseFrames: Self.liveShortPauseFrames))
        (updates, updatesContinuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(1))
        (wake, wakeContinuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(1))
        live = entry.runtime == .appleSpeech ? Live() : nil
    }

    /// Waits for the model gate, loads the model at `.utility` QoS and starts decoding (Apple Speech: no gate, starts
    /// the channel runners).
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
        if let live {
            stream(live, mic: mic, system: system, language: language)
            return
        }
        if buffer.withLock({ $0.append(mic: mic, system: system, language: language) }) { wakeContinuation.yield() }
    }

    /// Apple Speech: yields the block to both runners, counted as waiting until each runner takes it (`counted`).
    /// Never blocks: the streams are unbounded, and over `maxQueuedSeconds` the transcriber fails instead.
    private nonisolated func stream(_ live: Live, mic: [Float], system: [Float], language: String?) {
        switch buffer.withLock({ $0.appendLive(mic.count) }) {
        case nil:
            return
        case .overflow:
            live.resultsContinuation.yield(.overflow)
        case .queued(let startSample, let resumed):
            live.feeds[RecordingSession.Channel.mic].yield(
                .audio(samples: mic, startSample: startSample, language: language))
            live.feeds[RecordingSession.Channel.system].yield(
                .audio(samples: system, startSample: startSample, language: language))
            if resumed { live.resultsContinuation.yield(.resumed) }
        }
    }

    /// On pause: closes the open chunk of each channel so it is decoded now.
    public func flush() {
        buffer.withLock { $0.flush(paused: true) }
        wakeContinuation.yield()
        // Apple Speech: each runner ends its analysis (its finals come) and starts a new one at the next block; its
        // chunkers stay empty.
        live?.feeds.forEach { $0.yield(.finalize) }
        publishRunning()
    }

    /// At Stop: decodes what is left (unless the Mac is constrained and `deferWhenConstrained`: then it stays a gap),
    /// unloads the model and returns what was transcribed; nil when no chunk was, or the model never loaded (the
    /// pipeline then transcribes the whole recording). Apple Speech: see `finishLive`.
    public func finish() async -> PriorTranscription? {
        guard !finished else { return nil }
        finished = true
        if live != nil { return finishLive() }
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

    /// Apple Speech at Stop: always nil (display only; batch Apple Speech writes the transcript). Cancels the runners
    /// without waiting for them, so it returns at once even with a `finalize(through:)` in flight. `stats.transcribed`
    /// counts the lines shown, once an analyzer started.
    private func finishLive() -> PriorTranscription? {
        if buffer.withLock({ $0.loaded }) { stats = Stats(transcribed: nextLineID) }
        cancel()
        return nil
    }

    /// Stops at once, keeping nothing; the decode task unloads the model on its way out.
    public func cancel() {
        finished = true
        snapshot.tentative = []
        buffer.withLock { $0.close() }
        wakeContinuation.finish()
        live?.feeds.forEach { $0.finish() }
        decoder?.cancel()
        decoder = nil
        updatesContinuation.finish()
    }

    // MARK: - Decode task

    /// Holds the model gate from load to unload (Apple Speech: no gate).
    private nonisolated func decode() async {
        do {
            if let live {
                try await runLive(live)
            } else {
                try await gate.withExclusiveModel(owner: .liveTranscript) { [self] in try await run() }
            }
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

    /// Apple Speech: runs both channels' runners and applies their results in order, until both streams end or the
    /// task is cancelled (`finish()`, `cancel()`), or something throws (the other runner is then cancelled).
    private nonisolated func runLive(_ live: Live) async throws {
        guard #available(macOS 26, *) else { throw TranscriptionError.unknownEngine(entry.id) }
        try await withThrowingTaskGroup(of: Void.self) { group in
            for channel in [RecordingSession.Channel.mic, RecordingSession.Channel.system] {
                let results = live.resultsContinuation
                group.addTask { [self] in
                    try await AppleSpeechLiveStream.run(
                        events: counted(live.events[channel], channel: channel),
                        onPreparing: { [self] in
                            buffer.withLock { _ = $0.preparing.insert(channel) }
                            results.yield(.preparing)
                        },
                        onStarted: { [self] in
                            buffer.withLock {
                                $0.loaded = true
                                $0.preparing.remove(channel)
                            }
                            results.yield(.started)
                        },
                        onVolatile: { results.yield(.volatile(channel: channel, text: $0, startSample: $1)) },
                        onFinal: { results.yield(.final(channel: channel, segment: $0)) })
                }
            }
            group.addTask { [self] in
                for await result in live.results {
                    if case .overflow = result {
                        throw TranscriptionError.engineFailed(
                            "over \(Self.maxQueuedSeconds) s of audio waited for the live transcript")
                    }
                    await apply(result)
                }
            }
            // Both runners first (the consumer only ends once `results` is finished), then the last results.
            _ = try await group.next()
            _ = try await group.next()
            live.resultsContinuation.finish()
            _ = try await group.next()
        }
        await apply(.ended)
    }

    /// `events` as the runner takes them: each block leaves `Buffer.queuedSamples` when pulled, so what waits during
    /// an install is counted, and a cancelled runner ends it.
    private nonisolated func counted(_ events: AsyncStream<AppleSpeechLiveEvent>, channel: Int)
        -> AsyncStream<AppleSpeechLiveEvent>
    {
        // Safe: the runner is the only consumer and pulls one event at a time.
        nonisolated(unsafe) var iterator = events.makeAsyncIterator()
        return AsyncStream { [self] in
            let event = await iterator.next()
            if case .audio(let samples, _, _) = event { buffer.withLock { $0.fedLive(samples.count) } }
            return event
        }
    }

    /// Decodes the waiting chunks in order, then a partial of an open chunk when one is due, unless the Mac is
    /// constrained and decoding waits for it; a final queued meanwhile goes first. Ends by dropping the tentative text
    /// no final will replace.
    private nonisolated func drain(_ model: IdleModel<any TranscriptionSession>) async throws {
        while !(deferWhenConstrained && TranscriptionPipeline.isConstrained) {
            if let item = buffer.withLock({ $0.take() }) {
                try await decodeFinal(item, model)
            } else if let partial = buffer.withLock({ $0.takePartial() }) {
                try await decodePartial(partial, model)
            } else {
                break
            }
        }
        await pruneTentative()
    }

    /// Records a closed chunk's segments and decode time, and shows its line in place of its tentative text.
    private nonisolated func decodeFinal(_ item: Item, _ model: IdleModel<any TranscriptionSession>) async throws {
        let session = try await model.get()
        let mic = item.channel == RecordingSession.Channel.mic
        let started = ContinuousClock.now
        var text: String?
        do {
            let segments = try await session.transcribe(item.chunk.samples, language: item.chunk.language)
            let offset = Double(item.chunk.startSample) / Double(Self.sampleRate)
            let shifted = segments.map {
                RawSegment(start: $0.start + offset, end: $0.end + offset, text: $0.text,
                           detectedLanguage: $0.detectedLanguage, speaker: $0.speaker)
            }
            buffer.withLock { $0.complete(item, segments: shifted) }
            let span = Span(start: offset, end: offset + Double(item.chunk.samples.count) / Double(Self.sampleRate))
            text = Self.liveLine(shifted, mic: mic, othersSpeak: {
                buffer.withLock { $0.othersSpeak(during: span) }
            })
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            buffer.withLock { $0.complete(item, segments: nil) }
        }
        let wallSeconds = (ContinuousClock.now - started) / .seconds(1)
        buffer.withLock { $0.recordDecode(channel: item.channel, wallSeconds: wallSeconds) }
        await add(mic ? .me : .others, text, startSample: item.chunk.startSample)
    }

    /// Shows an open chunk's text so far as its speaker's tentative text; a failed decode changes nothing.
    private nonisolated func decodePartial(_ partial: Partial, _ model: IdleModel<any TranscriptionSession>) async throws {
        let session = try await model.get()
        let segments: [RawSegment]
        do {
            segments = try await session.transcribe(partial.chunk.samples, language: partial.chunk.language)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            return
        }
        let mic = partial.channel == RecordingSession.Channel.mic
        let rate = Double(Self.sampleRate)
        let span = Span(start: Double(partial.chunk.startSample) / rate,
                        end: Double(partial.chunk.startSample + partial.chunk.samples.count) / rate)
        let text = Self.liveLine(segments, mic: mic, othersSpeak: {
            buffer.withLock { $0.othersSpeak(during: span) }
        })
        await setTentative(mic ? .me : .others, text, startSample: partial.chunk.startSample)
    }

    // MARK: - State

    /// Yields the snapshot when it changed.
    private func publish(_ state: State) {
        snapshot.state = state
        if case .failed = state { snapshot.tentative = [] }
        snapshot.pendingSeconds = buffer.withLock { $0.pendingSeconds }
        snapshot.deferred = live == nil && deferWhenConstrained && TranscriptionPipeline.isConstrained
        guard snapshot != published else { return }
        published = snapshot
        updatesContinuation.yield(snapshot)
    }

    /// Listening or paused once loaded and no language is installing; leaves `loading`, `preparing` and `failed` alone
    /// until then (a pause while installing shows once it is done).
    private func publishRunning() {
        let (loaded, preparing) = buffer.withLock { ($0.loaded, !$0.preparing.isEmpty) }
        guard snapshot.state != .loading || loaded, snapshot.state != .preparing || !preparing else { return }
        if case .failed = snapshot.state { return }
        publish(buffer.withLock { $0.paused } ? .paused : .listening)
    }

    /// Apple Speech: one runner result or `append` notice, in the order they came.
    private func apply(_ result: LiveResult) {
        if case .failed = snapshot.state { return }
        switch result {
        case .preparing:
            publish(.preparing)
        case .started, .resumed, .overflow:
            publishRunning()
        case .volatile(let channel, let text, let startSample):
            setTentative(Self.speaker(channel), text, startSample: startSample)
        case .final(let channel, let segment):
            addFinal(channel, segment)
        case .ended:
            snapshot.tentative = []
            publishRunning()
        }
    }

    /// Apple Speech: records a final and shows its line in place of the speaker's tentative text.
    private func addFinal(_ channel: Int, _ segment: RawSegment) {
        buffer.withLock { $0.addLive(segment, channel: channel) }
        let speaker = Self.speaker(channel)
        let span = Span(start: segment.start, end: segment.end)
        // The system's text still volatile counts like the chunk path's open chunk: its final lags the mic's.
        let othersPending = snapshot.tentative.contains {
            $0.speaker == .others
                && Double($0.startSample) / Double(Self.sampleRate) <= span.end + TranscriptMerger.bleedTimeTolerance
        }
        let text = Self.liveLine([segment], mic: speaker == .me, othersSpeak: {
            othersPending || buffer.withLock { $0.othersFinal(during: span) }
        })
        snapshot.tentative = Self.tentative(snapshot.tentative, setting: speaker, text: nil, startSample: 0)
        if let text {
            snapshot.lines.append(Line(id: nextLineID, speaker: speaker, text: text))
            nextLineID += 1
            snapshot.lines.removeFirst(max(0, snapshot.lines.count - Self.maxLines))
        }
        publishRunning()
    }

    /// The final of the chunk starting at `startSample`: its line (none when `text` is nil) replaces the speaker's
    /// tentative text of that chunk, in one publish.
    private func add(_ speaker: Speaker, _ text: String?, startSample: Int) {
        snapshot.tentative = Self.tentative(snapshot.tentative, clearing: speaker, startSample: startSample)
        if let text {
            snapshot.lines.append(Line(id: nextLineID, speaker: speaker, text: text))
            nextLineID += 1
            snapshot.lines.removeFirst(max(0, snapshot.lines.count - Self.maxLines))
        }
        publishRunning()
    }

    private func setTentative(_ speaker: Speaker, _ text: String?, startSample: Int) {
        snapshot.tentative = Self.tentative(snapshot.tentative, setting: speaker, text: text, startSample: startSample)
        publishRunning()
    }

    /// Drops the tentative text no final will replace (`Buffer.keepsTentative`).
    private func pruneTentative() {
        let tentative = snapshot.tentative
        let kept = buffer.withLock { buffer in
            tentative.filter {
                buffer.keepsTentative(channel: $0.speaker == .me ? RecordingSession.Channel.mic
                                          : RecordingSession.Channel.system,
                                      startSample: $0.startSample)
            }
        }
        guard kept != tentative else { return }
        snapshot.tentative = kept
        publishRunning()
    }

    // MARK: - Helpers

    private static func speaker(_ channel: Int) -> Speaker {
        channel == RecordingSession.Channel.mic ? .me : .others
    }

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

    /// How far behind decoding is, in rounded seconds of speech waiting (`Snapshot.pendingSeconds`), for the live
    /// transcript's status: nil unless listening with at least `behindNoticeSeconds` waiting.
    public static func behindSeconds(_ snapshot: Snapshot) -> Int? {
        guard snapshot.state == .listening, snapshot.pendingSeconds >= Double(behindNoticeSeconds) else { return nil }
        return Int(snapshot.pendingSeconds.rounded())
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

    /// `current` with `speaker`'s tentative text set to `text` from `startSample` (removed when `text` is nil): at most
    /// one per speaker, ordered by start (then speaker).
    static func tentative(_ current: [Tentative], setting speaker: Speaker, text: String?, startSample: Int)
        -> [Tentative]
    {
        var next = current.filter { $0.speaker != speaker }
        if let text { next.append(Tentative(speaker: speaker, text: text, startSample: startSample)) }
        return next.sorted { ($0.startSample, $0.speaker.rawValue) < ($1.startSample, $1.speaker.rawValue) }
    }

    /// `current` without `speaker`'s tentative text when it is of the chunk starting at `startSample` (its final came).
    static func tentative(_ current: [Tentative], clearing speaker: Speaker, startSample: Int) -> [Tentative] {
        current.filter { $0.speaker != speaker || $0.startSample != startSample }
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

    /// Apple Speech: the runners' event streams (unbounded: audio waits there while a language installs) and their
    /// results, applied in order by one consumer.
    struct Live: Sendable {
        /// Index = `RecordingSession.Channel`.
        let events: [AsyncStream<AppleSpeechLiveEvent>]
        let feeds: [AsyncStream<AppleSpeechLiveEvent>.Continuation]
        let results: AsyncStream<LiveResult>
        let resultsContinuation: AsyncStream<LiveResult>.Continuation

        init() {
            let mic = AsyncStream<AppleSpeechLiveEvent>.makeStream(bufferingPolicy: .unbounded)
            let system = AsyncStream<AppleSpeechLiveEvent>.makeStream(bufferingPolicy: .unbounded)
            events = [mic.stream, system.stream]
            feeds = [mic.continuation, system.continuation]
            (results, resultsContinuation) = AsyncStream.makeStream(bufferingPolicy: .unbounded)
        }
    }

    /// What `RecordingTranscriber.apply` handles; channel = `RecordingSession.Channel`.
    enum LiveResult: Sendable {
        case preparing, started
        case volatile(channel: Int, text: String, startSample: Int)
        case final(channel: Int, segment: RawSegment)
        /// The first block after a pause.
        case resumed
        /// More than `maxQueuedSeconds` waits: the transcriber fails.
        case overflow
        /// Both runners ended.
        case ended
    }

    /// What `Buffer.appendLive` did with a block.
    enum LiveAppend {
        case queued(startSample: Int, resumed: Bool)
        case overflow
    }

    /// A chunk waiting to be decoded, and where its outcome is recorded.
    struct Item: Sendable {
        var channel: Int
        var index: Int
        var chunk: SpeechChunker.Chunk
    }

    /// An open chunk to decode as tentative text; its outcome is not recorded.
    struct Partial: Sendable {
        var channel: Int
        var chunk: SpeechChunker.Chunk
    }

    /// One channel's chunker, chunk outcomes (file order) and decoded segments (file time).
    struct Channel: Sendable {
        var chunker: SpeechChunker
        var chunks: [(span: Span, outcome: PriorTranscription.ChunkOutcome)] = []
        var segments: [RawSegment] = []
        /// The open chunk's start and length at its last partial.
        var lastPartial: (startSample: Int, count: Int)?
        /// The open chunk's start and length when `append` last woke the decoder for a partial.
        var signalledPartial: (startSample: Int, count: Int)?
        /// The last final decode's wall seconds.
        var decodeSeconds = 0.0
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
        /// Apple Speech: the channels whose runner is installing a language.
        var preparing: Set<Int> = []

        init(
            maxChunkSeconds: Int, shortPauseMinSeconds: Int = SpeechChunker.shortPauseMinSeconds,
            shortPauseFrames: Int = SpeechChunker.shortPauseFrames,
            maxQueuedSamples: Int = RecordingTranscriber.maxQueuedSeconds * RecordingTranscriber.sampleRate
        ) {
            self.maxQueuedSamples = maxQueuedSamples
            let mic = Channel(chunker: SpeechChunker(maxChunkSeconds: maxChunkSeconds,
                                                     shortPauseMinSeconds: shortPauseMinSeconds,
                                                     shortPauseFrames: shortPauseFrames))
            channels = [mic, mic]
        }

        var pendingSeconds: Double { Double(queuedSamples) / Double(RecordingTranscriber.sampleRate) }

        /// True when a chunk was queued, the recording resumed or a partial became due.
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
            var partialDue = false
            for channel in channels.indices where signalPartial(channel) { partialDue = true }
            return resumed || micQueued || systemQueued || partialDue
        }

        /// Apple Speech: counts a block of `count` samples per channel as waiting for both runners; nil when it is not
        /// yielded (closed, finishing or failed), `.overflow` (and failed) when over `maxQueuedSamples`.
        mutating func appendLive(_ count: Int) -> LiveAppend? {
            guard !closed, !finishing, !failed else { return nil }
            guard queuedSamples + 2 * count <= maxQueuedSamples else {
                fail()
                return .overflow
            }
            let startSample = samplesSeen
            let resumed = paused
            paused = false
            samplesSeen += count
            queuedSamples += 2 * count
            return .queued(startSample: startSample, resumed: resumed)
        }

        /// Apple Speech: a runner took a block of `count` samples.
        mutating func fedLive(_ count: Int) {
            guard !closed, !failed else { return }
            queuedSamples = max(0, queuedSamples - count)
        }

        /// Apple Speech: a final result of `channel`, in file seconds.
        mutating func addLive(_ segment: RawSegment, channel: Int) {
            guard !closed else { return }
            channels[channel].segments.append(segment)
        }

        /// Apple Speech: true when a system final overlaps `span` widened by `TranscriptMerger.bleedTimeTolerance`.
        func othersFinal(during span: Span) -> Bool {
            guard !closed else { return false }
            let start = span.start - TranscriptMerger.bleedTimeTolerance
            let end = span.end + TranscriptMerger.bleedTimeTolerance
            return channels[RecordingSession.Channel.system].segments.contains { $0.start < end && $0.end > start }
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

        /// The open chunk to decode as tentative text, only while no closed chunk waits (partials never starve the
        /// finals) and not paused, finishing, closed or failed: among the channels with a partial due
        /// (`partialDue(_:)`), the one whose open chunk started first.
        mutating func takePartial() -> Partial? {
            guard queue.isEmpty, !paused, !finishing, !closed, !failed else { return nil }
            let due = channels.indices.compactMap { channel in partialDue(channel).map { (channel, $0) } }
            guard let (channel, open) = due.min(by: { $0.1.startSample < $1.1.startSample }),
                  let chunk = channels[channel].chunker.openChunk
            else { return nil }
            channels[channel].lastPartial = open
            return Partial(channel: channel, chunk: chunk)
        }

        /// True while a final of `channel`'s chunk starting at `startSample` may still come and replace its tentative
        /// text: the chunk waits in the queue or is still open. False once finishing (what is left is decoded or a gap
        /// by then) or closed.
        func keepsTentative(channel: Int, startSample: Int) -> Bool {
            guard !closed, !finishing else { return false }
            return queue.contains { $0.channel == channel && $0.chunk.startSample == startSample }
                || channels[channel].chunker.openSpan?.startSample == startSample
        }

        /// Stores the wall seconds of a final decode of `channel` for the partial gate.
        mutating func recordDecode(channel: Int, wallSeconds: Double) {
            guard !closed else { return }
            channels[channel].decodeSeconds = wallSeconds
        }

        /// The open chunk's span of `channel` when a partial of it is due: at least `partialMinSeconds` long, grown
        /// `partialStepSeconds` since the last partial of the same chunk (a new start is a new chunk), and the
        /// channel's last final decode took at most `partialMaxDecodeSeconds`.
        private func partialDue(_ channel: Int) -> (startSample: Int, count: Int)? {
            let rate = RecordingTranscriber.sampleRate
            let state = channels[channel]
            guard let open = state.chunker.openSpan, open.count >= RecordingTranscriber.partialMinSeconds * rate,
                  state.decodeSeconds <= RecordingTranscriber.partialMaxDecodeSeconds
            else { return nil }
            if let last = state.lastPartial, last.startSample == open.startSample,
               open.count - last.count < RecordingTranscriber.partialStepSeconds * rate
            {
                return nil
            }
            return open
        }

        /// True once per due step: a partial of `channel` is due and `append` has not woken the decoder for it yet.
        private mutating func signalPartial(_ channel: Int) -> Bool {
            guard !failed, let open = partialDue(channel) else { return false }
            if let signalled = channels[channel].signalledPartial, signalled.startSample == open.startSample,
               open.count - signalled.count < RecordingTranscriber.partialStepSeconds * RecordingTranscriber.sampleRate
            {
                return false
            }
            channels[channel].signalledPartial = open
            return true
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
