import EchoAI
import EchoCore
import Foundation

/// What a provider's realtime API allows, per `stt-protocols.md` part B. Pure, for tests.
struct RealtimePolicy: Sendable, Equatable {
    /// How a planned session switch (rotation before the session cap, resume after a pause) keeps speaker labels.
    enum Handover: Sendable, Equatable {
        /// Open the next socket `labelOverlapSeconds` before closing the old one and stream both (billed twice).
        case parallel
        /// Close the old socket, then send its last `labelOverlapSeconds` again to the next one, faster than real
        /// time (documented for the provider; Speechmatics Free allows only 2 sessions, so no third socket).
        case replay
        /// Close the old socket, then open the next one; its labels stay session-scoped (Gladia Free: 1 session,
        /// replay not documented).
        case none
    }

    /// The provider sends a ready message (`.started`) before it takes audio; else the session starts on connect.
    var announcesStart: Bool
    /// The documented maximum session length; nil when unpublished (then only `.started(expiresAt:)` rotates).
    var maxSessionSeconds: Double?
    var handover: Handover
    /// The fastest send rate (times real time) and the audio that may go out at once above it (held audio).
    var rate: Double
    var burstSeconds: Double
    /// The provider finalizes only on the app's commits (`finalize(at:)` at speech pauses): OpenAI.
    var commitsOnPause = false

    /// How much audio from before a session's start it may begin with (the connect time).
    var holdSeconds: Double { handover == .replay ? 10 : 5 }

    /// nil for a preset without a realtime adapter.
    static func of(preset: String?) -> RealtimePolicy? {
        switch preset {
        case "elevenlabs": RealtimePolicy(announcesStart: true, handover: .parallel, rate: 1, burstSeconds: 5)
        case "deepgram": RealtimePolicy(announcesStart: false, handover: .replay, rate: 1.2, burstSeconds: 2)
        case "assemblyai":
            RealtimePolicy(announcesStart: true, maxSessionSeconds: 3 * 3_600, handover: .replay, rate: 1.2,
                           burstSeconds: 2)
        case "openai":
            RealtimePolicy(announcesStart: true, handover: .parallel, rate: 1, burstSeconds: 5, commitsOnPause: true)
        case "xai": RealtimePolicy(announcesStart: true, handover: .parallel, rate: 1, burstSeconds: 5)
        case "speechmatics":
            RealtimePolicy(announcesStart: true, maxSessionSeconds: 48 * 3_600, handover: .replay, rate: 2,
                           burstSeconds: 10)
        case "soniox":
            RealtimePolicy(announcesStart: false, maxSessionSeconds: 300 * 60, handover: .parallel, rate: 1,
                           burstSeconds: 5)
        case "gladia":
            RealtimePolicy(announcesStart: false, maxSessionSeconds: 3 * 3_600, handover: .none, rate: 1,
                           burstSeconds: 5)
        default: nil
        }
    }
}

/// One channel's realtime sessions in file time: the audio each covered, its finals, and how its speaker labels join
/// the sessions before it. A session covers from its first frame to its last final, or to the end of the audio it
/// sent once the provider finished everything (a clean drain); a disconnect, the audio after the last final of an
/// unfinished drain, and audio never sent are gaps. Silent audio is covered. Pure, for tests.
struct RealtimeCoverage: Sendable, Equatable {
    struct Session: Sendable, Equatable {
        /// File seconds of the session's first frame: its stream time 0.
        var offset: Double
        /// File seconds where the audio sent ends.
        var sent: Double
        /// File seconds where the last final ends (`offset` before any).
        var lastFinal: Double
        /// nil while open; true when the provider finished everything sent.
        var drained: Bool?
        /// Opened to share audio with the session before (planned rotation, resume with replay), so its labels are
        /// reconciled with that session's; else its labels are scoped `"r<N>:<label>"`.
        var bridged: Bool
        /// Finals in file time with the provider's labels.
        var segments: [CloudSegment] = []
        /// The label mapping for the live updates, once the session before closed (`freezeLabels`).
        var labels: [String: String]?
    }

    /// Gaps shorter than this are dropped (frame remainders, rounding).
    static let minimumGapSeconds = 0.1

    private(set) var sessions: [Session] = []
    private(set) var silence: [Span] = []

    /// A new session whose first frame is at `offset` file seconds; returns its index (N in `"r<N>:"`).
    mutating func open(at offset: Double, bridged: Bool) -> Int {
        sessions.append(Session(offset: offset, sent: offset, lastFinal: offset, bridged: bridged))
        return sessions.count - 1
    }

    mutating func sent(_ index: Int, through end: Double) {
        sessions[index].sent = max(sessions[index].sent, end)
    }

    /// A final in stream seconds; returns it in file seconds. The watermark never passes the audio sent.
    @discardableResult
    mutating func final(_ index: Int, _ segment: CloudSegment) -> CloudSegment {
        let file = segment.offset(by: sessions[index].offset)
        sessions[index].segments.append(file)
        sessions[index].lastFinal = max(sessions[index].lastFinal, min(file.end, sessions[index].sent))
        return file
    }

    mutating func close(_ index: Int, drained: Bool) {
        if sessions[index].drained == nil { sessions[index].drained = drained }
    }

    /// Audio below `TranscriptionPipeline.silencePeak`: covered whether or not a session got it.
    mutating func silence(_ span: Span) {
        if let last = silence.last, last.end >= span.start {
            silence[silence.count - 1].end = max(last.end, span.end)
        } else {
            silence.append(span)
        }
    }

    /// The spans of `0...duration` no session covered, merged, each at least `minimumGapSeconds` long.
    func gaps(through duration: Double) -> [Span] {
        let covered = sessions.map {
            Span(start: $0.offset, end: $0.drained == true ? $0.sent : min($0.lastFinal, $0.sent))
        } + silence
        var gaps: [Span] = []
        var cursor = 0.0
        for span in covered.sorted(by: { $0.start < $1.start }) where span.end > span.start {
            if span.start > cursor { gaps.append(Span(start: cursor, end: min(span.start, duration))) }
            cursor = max(cursor, span.end)
        }
        if cursor < duration { gaps.append(Span(start: cursor, end: duration)) }
        return gaps.filter { $0.end - $0.start >= Self.minimumGapSeconds }
    }

    /// Where the session before `index` stopped: `index`'s finals before it repeat that session's.
    func cut(_ index: Int) -> Double {
        index > 0 ? sessions[index - 1].sent : -.infinity
    }

    /// The audio a bridged session shares with the session before, as `SpeakerLabelReconciler.append` takes it; nil
    /// when it is not bridged or shares less than `minimumSharedSeconds` (then its labels are scoped).
    func overlap(_ index: Int) -> ClosedRange<Double>? {
        guard index > 0, sessions[index].bridged else { return nil }
        let previous = sessions[index - 1]
        let cut = previous.sent
        let lower = max(sessions[index].offset, previous.offset, cut - SpeakerLabelReconciler.labelOverlapSeconds)
        guard cut - lower >= SpeakerLabelReconciler.minimumSharedSeconds else { return nil }
        return lower...cut
    }

    /// The finals of every session in file order: a bridged session's labels reconciled with the sessions before
    /// (`SpeakerLabelReconciler`, which also drops the repeated finals before the cut), any other session's labels
    /// scoped `"r<N>:<label>"` so they never merge with an earlier session's identical strings.
    func segments() -> [CloudSegment] {
        segments(before: sessions.count)
    }

    private func segments(before end: Int) -> [CloudSegment] {
        var joined: [CloudSegment] = []
        for index in 0..<end {
            let next = sessions[index].segments.sorted { $0.start < $1.start }
            if index == 0 {
                joined = next
            } else if let overlap = overlap(index) {
                joined = SpeakerLabelReconciler.append(next, to: joined, overlap: overlap, partIndex: index)
            } else {
                joined = (joined + next.map { Self.scoped($0, index) }).sorted { $0.start < $1.start }
            }
        }
        return joined
    }

    /// Fixes a bridged session's label mapping from the finals so far, once the session before closed.
    mutating func freezeLabels(_ index: Int) {
        guard let overlap = overlap(index) else { return }
        sessions[index].labels = SpeakerLabelReconciler.labels(
            for: sessions[index].segments, earlier: segments(before: index), overlap: overlap, partIndex: index)
    }

    /// A final's label for the live updates: as `segments()` will join it, except that a bridged session's label
    /// first heard after `freezeLabels` is scoped `"p<N>:"` without the reconciler's renumbering.
    func liveLabel(_ index: Int, _ label: String?) -> String? {
        guard let label, !label.isEmpty, index > 0 else { return label }
        guard overlap(index) != nil else { return "r\(index):\(label)" }
        return sessions[index].labels?[label] ?? "p\(index):\(label)"
    }

    private static func scoped(_ segment: CloudSegment, _ index: Int) -> CloudSegment {
        var segment = segment
        if let label = segment.speaker, !label.isEmpty { segment.speaker = "r\(index):\(label)" }
        return segment
    }
}

/// Streams a recording's two channels ("Me" mic, "Others" system) to the default cloud provider's realtime API while
/// it is made, one socket and one adapter per channel, and hands back the finals and the spans not covered (gaps) at
/// Stop.
///
/// Audio: the writer's 16 kHz Float blocks (`append`, file time = the running sample count, contiguous) go to a
/// per-channel ring; each session's sender takes them in 100 ms frames (AssemblyAI takes 20-1000 ms), resampled to
/// the adapter's rate, at most `RealtimePolicy.rate` times real time above `burstSeconds`. A block never waits for a
/// socket: while no session runs it is only kept in the ring (60 s), so a disconnected span becomes a gap, never a
/// queue. A session starts with the audio since its connect attempt began (at most `holdSeconds`).
///
/// Sessions: connect (the init call first when the adapter has one), wait for `.started` where the provider sends
/// one (else it counts as started on connect; xAI must wait), then stream. A failure before the first `.started`,
/// successful send or message is a connect failure. Reconnects back off 1, 2, 4 ... 30 s; auth, plan-limit
/// (concurrency refusal) and configuration failures, and the same connect failure 5 times in a row, stop the channel
/// (`State.failed`). Pause closes the sockets, the next block opens new ones; a language marker change switches to a
/// session in the new language. Before the provider's session cap (`.started(expiresAt:)` or
/// `RealtimePolicy.maxSessionSeconds`) a planned rotation keeps speaker labels: a parallel overlap of
/// `labelOverlapSeconds`, a replay of them where the provider takes faster audio (preferred), or no overlap where the
/// provider allows neither (scoped labels). Keep-alives (`adapter.keepAlive`) go out while no audio flows, WebSocket
/// pings every 30 s (Speechmatics closes after 3 min idle; URLSession after its request timeout).
///
/// Stop (`finish`): each socket gets the rest of its audio, `finalize` and `closeMessages`, then up to 5 s to drain
/// (`isDrained`, `.ended` or the socket closing); what did not drain is a gap.
///
/// Errors never leave as text: states carry a short kind (`CloudRealtime.errorKind`, or a fixed one), so a Gladia
/// session-token URL (`URLError.failingURL`), a body or the key never reach a log or the UI.
public actor RealtimeLiveStream {
    public enum State: Sendable, Equatable {
        case connecting, live, reconnecting
        /// Stopped for this recording: "auth", "payment", "plan_limit" (a concurrency refusal), "config" (no key,
        /// unsupported language, e.g. Speechmatics without one), "http_<code>", or a provider's error kind.
        case failed(kind: String)
    }

    /// `channel` = `RecordingSession.Channel`.
    public enum Update: Sendable, Equatable {
        case state(channel: Int, State)
        /// The channel's text so far, replacing its previous partial; nil clears it. `start` in file seconds when the
        /// provider times its partials.
        case partial(channel: Int, text: String?, start: Double?)
        /// A final in file seconds, its speaker label mapped or scoped as far as known (`RealtimeCoverage.liveLabel`);
        /// `Result` holds the reconciled labels.
        case final(channel: Int, segment: RawSegment)
    }

    public struct Result: Sendable, Equatable {
        public var mic: [RawSegment]
        public var system: [RawSegment]
        /// File spans no session covered, to transcribe after Stop.
        public var micGaps: [Span]
        public var systemGaps: [Span]
        /// File seconds where the system channel resumed after a pause in a session whose labels are scoped (no
        /// replay shared with the session before) and no gap is there; empty when the system finals carry no labels.
        /// The pipeline uploads the audio around each to map the scoped labels back.
        public var systemBridges: [Double]
        /// The realtime model used.
        public var modelID: String
        /// Seconds of audio appended.
        public var duration: Double
    }

    /// A failure with a fixed kind that stops the channel (the adapter factory's).
    struct Failure: Error {
        var kind: String
    }

    static let captureRate = SpeechChunker.sampleRate
    /// 100 ms; a remainder at a close goes out from 20 ms.
    static let frameSamples = captureRate / 10
    static let minimumFrameSamples = captureRate / 50
    static let framesPerStep = 10
    static let pollInterval = Duration.milliseconds(50)
    static let pingInterval = Duration.seconds(30)
    static let startTimeout = Duration.seconds(15)
    static let drainTimeout = Duration.seconds(5)
    /// How long before the session cap the rotation starts, beyond `labelOverlapSeconds`.
    static let rotationMarginSeconds = 60.0
    static let maxBackoffSeconds = 30.0
    static let maxRepeatedFailures = 5
    /// A session lived this long before failing: its reconnect starts the backoff over.
    static let healthySessionSeconds = 30.0
    static let ringSeconds = 60.0
    /// Audio a session may lag behind beyond what it started with before it counts as stalled (a disconnect).
    static let stallSeconds = 15.0

    public nonisolated let updates: AsyncStream<Update>
    private nonisolated let updatesContinuation: AsyncStream<Update>.Continuation
    private nonisolated let inputs: AsyncStream<Input>
    private nonisolated let inputsContinuation: AsyncStream<Input>.Continuation
    public nonisolated let modelID: String
    private let policy: RealtimePolicy
    private let urlSession: URLSession?
    private let makeAdapter: @Sendable (_ channel: Int, _ language: String?) async throws -> any RealtimeAdapter
    private var channels = [Channel(), Channel()]
    private var sessions: [Int: Session] = [:]
    private var nextSessionID = 0
    private var feeder: Task<Void, Never>?
    private var finishing = false
    private var result: Result?
    private var drainWaiter: CheckedContinuation<Void, Never>?

    /// `makeAdapter` builds a fresh adapter for each socket (channel, language marker); it runs in the session's
    /// task, so the key is read there, never in the caller's update. A `Failure` it throws stops the channel.
    init(
        modelID: String, policy: RealtimePolicy, session: URLSession? = nil,
        makeAdapter: @escaping @Sendable (_ channel: Int, _ language: String?) async throws -> any RealtimeAdapter
    ) {
        self.modelID = modelID
        self.policy = policy
        urlSession = session
        self.makeAdapter = makeAdapter
        (updates, updatesContinuation) = AsyncStream.makeStream(bufferingPolicy: .unbounded)
        (inputs, inputsContinuation) = AsyncStream.makeStream(bufferingPolicy: .unbounded)
    }

    /// The runner for `config`'s realtime model; nil when the provider has no realtime API. `apiKey` is read in
    /// each session's task (an empty key stops both channels with "config"). Streaming speaker labels are asked for
    /// on the system channel only, and only with `diarize`.
    public static func make(
        config: ProviderConfig, diarize: Bool, session: URLSession? = nil,
        apiKey: @escaping @Sendable () async throws -> String
    ) -> RealtimeLiveStream? {
        guard CloudRealtime.supports(config), let policy = RealtimePolicy.of(preset: config.preset) else { return nil }
        return RealtimeLiveStream(modelID: CloudRealtime.model(for: config), policy: policy, session: session) {
            channel, language in
            let key = try await apiKey()
            guard !key.isEmpty,
                  let adapter = CloudRealtime.make(
                      config: config, apiKey: key, language: language,
                      diarize: diarize && channel == RecordingSession.Channel.system)
            else { throw Failure(kind: "config") }
            return adapter
        }
    }

    /// Starts taking blocks; the sockets open at the first block (its language marker).
    public func start() {
        guard feeder == nil, !finishing else { return }
        feeder = Task.detached(priority: .utility) { [self, inputs] in
            for await input in inputs { await handle(input) }
        }
    }

    /// One block of both channels (16 kHz, `startSample` its file position) with its language marker ("auto", a
    /// BCP-47 code, or nil). Never blocks; call from the writer queue.
    public nonisolated func append(mic: [Float], system: [Float], startSample: Int, language: String?) {
        inputsContinuation.yield(.audio(mic: mic, system: system, startSample: startSample, language: language))
    }

    /// The recording paused: closes the sockets (their finals come); the next block opens new ones.
    public nonisolated func pause() {
        inputsContinuation.yield(.pause)
    }

    /// At Stop, after the last block: drains each socket (at most `drainTimeout`) and returns the finals and gaps.
    /// One caller: a second call while the first drains is not supported (later calls return the same result).
    public func finish() async -> Result {
        if let result { return result }
        inputsContinuation.yield(.finish)
        inputsContinuation.finish()
        if let feeder { await feeder.value } else { beginFinish() }
        if !sessions.isEmpty { await withCheckedContinuation { drainWaiter = $0 } }
        let rate = Double(Self.captureRate)
        let done = Result(
            mic: Self.raw(channels[RecordingSession.Channel.mic].coverage.segments()),
            system: Self.raw(channels[RecordingSession.Channel.system].coverage.segments()),
            micGaps: channels[RecordingSession.Channel.mic].gaps(),
            systemGaps: channels[RecordingSession.Channel.system].gaps(),
            systemBridges: Self.bridges(channels[RecordingSession.Channel.system]),
            modelID: modelID, duration: Double(channels.map(\.position).max() ?? 0) / rate)
        result = done
        updatesContinuation.finish()
        return done
    }

    /// Stops at once, keeping nothing.
    public func cancel() {
        finishing = true
        inputsContinuation.finish()
        feeder?.cancel()
        for session in sessions.values {
            session.tasks.forEach { $0.cancel() }
            session.connection?.close()
        }
        sessions = [:]
        drainWaiter?.resume()
        drainWaiter = nil
        updatesContinuation.finish()
    }

    // MARK: - Feed

    private func handle(_ input: Input) {
        switch input {
        case .audio(let mic, let system, let startSample, let language):
            feed(RecordingSession.Channel.mic, mic, startSample: startSample, language: language)
            feed(RecordingSession.Channel.system, system, startSample: startSample, language: language)
        case .pause:
            for channel in channels.indices { pause(channel) }
        case .finish:
            beginFinish()
        }
    }

    private func feed(_ channel: Int, _ samples: [Float], startSample: Int, language: String?) {
        guard !finishing, !samples.isEmpty else { return }
        let rate = Double(Self.captureRate)
        if !channels[channel].started {
            channels[channel].started = true
            channels[channel].ringStart = startSample
            channels[channel].position = startSample
        }
        let start = channels[channel].position
        channels[channel].ring += samples
        channels[channel].position += samples.count
        if samples.allSatisfy({ abs($0) < TranscriptionPipeline.silencePeak }) {
            channels[channel].coverage.silence(
                Span(start: Double(start) / rate, end: Double(channels[channel].position) / rate))
        }
        channels[channel].paused = false
        let marker = Self.marker(language)
        if channels[channel].hasMarker, marker?.lowercased() != channels[channel].language?.lowercased() {
            switchLanguage(channel, at: start)
        }
        channels[channel].language = marker
        channels[channel].hasMarker = true
        for (id, session) in sessions where session.channel == channel && session.phase == .live
            && Double(channels[channel].position - session.nextSample) / rate > session.backlogLimit
        {
            fail(id, kind: "stalled", permanent: false)
        }
        trimRing(channel)
        ensure(channel)
    }

    /// Closes the channel's sessions at `sample`; the next one starts there in the new language.
    private func switchLanguage(_ channel: Int, at sample: Int) {
        channels[channel].plan = Plan(from: sample, waits: true)
        if channels[channel].failure == "config" {
            channels[channel].failure = nil
            channels[channel].failures = 0
            channels[channel].state = nil
        }
        endSessions(channel, at: sample)
    }

    private func pause(_ channel: Int) {
        guard channels[channel].started, !channels[channel].paused else { return }
        let point = channels[channel].position
        channels[channel].paused = true
        channels[channel].liveSinceResume = false
        let replay = policy.handover == .replay
        channels[channel].plan = Plan(
            bridged: replay, replayFrom: replay ? point - Self.overlapSamples : nil, from: point, waits: true,
            resumes: true)
        endSessions(channel, at: point)
    }

    private func beginFinish() {
        guard !finishing else { return }
        finishing = true
        for channel in channels.indices { endSessions(channel, at: channels[channel].position) }
        resumeIfDrained()
    }

    /// Closes the channel's streaming sessions at `sample` and drops those not streaming yet.
    private func endSessions(_ channel: Int, at sample: Int) {
        for (id, session) in sessions where session.channel == channel {
            switch session.phase {
            case .live: close(id, at: sample)
            case .connecting, .waitingStart: complete(id, drained: false)
            case .closing: break
            }
        }
    }

    /// Keeps the last `ringSeconds`, and what a session or a planned replay still needs.
    private func trimRing(_ channel: Int) {
        let rate = Double(Self.captureRate)
        var keep = channels[channel].position - Int(Self.ringSeconds * rate)
        for session in sessions.values where session.channel == channel && session.record != nil {
            keep = min(keep, session.nextSample)
        }
        if let replay = channels[channel].plan?.replayFrom { keep = min(keep, replay) }
        let drop = keep - channels[channel].ringStart
        guard drop > Self.captureRate * 10 else { return }
        channels[channel].ring.removeFirst(drop)
        channels[channel].ringStart = keep
    }

    // MARK: - Sessions

    /// Opens the channel's next session when it should stream and none is streaming or opening (after a failure,
    /// with backoff); a planned switch that `waits` opens once the closing sessions drained.
    private func ensure(_ channel: Int) {
        let state = channels[channel]
        guard !finishing, state.hasMarker, !state.paused, state.failure == nil else { return }
        let mine = sessions.values.filter { $0.channel == channel }
        guard !mine.contains(where: { $0.phase != .closing }) else { return }
        var plan = state.plan ?? Plan()
        if plan.waits, mine.contains(where: { $0.phase == .closing }) { return }
        channels[channel].plan = nil
        let planned = plan.from != nil || plan.replayFrom != nil
        // The first session takes the recording from its first block (at most `holdSeconds` back).
        if plan.from == nil, state.coverage.sessions.isEmpty { plan.from = state.ringStart }
        let delay = planned || state.failures == 0
            ? 0 : min(Self.maxBackoffSeconds, pow(2, Double(state.failures - 1)))
        setState(channel, state.liveSinceResume ? .reconnecting : .connecting)
        open(channel, plan, delay: delay)
    }

    private func open(_ channel: Int, _ plan: Plan, delay: Double) {
        let id = nextSessionID
        nextSessionID += 1
        let language = channels[channel].language
        sessions[id] = Session(id: id, channel: channel, plan: plan)
        let task = Task.detached(priority: .utility) { [self, makeAdapter, urlSession] in
            do {
                if delay > 0 { try await Task.sleep(for: .seconds(delay)) }
                await attemptStarted(id)
                let adapter: any RealtimeAdapter
                do {
                    adapter = try await makeAdapter(channel, language)
                } catch let failure as Failure {
                    throw failure
                } catch is CancellationError {
                    throw CancellationError()
                } catch {
                    throw Failure(kind: "config")
                }
                let url = try await RealtimeConnection.socketURL(for: adapter, session: urlSession)
                try Task.checkCancellation()
                let connection = try await RealtimeConnection.connect(adapter, url: url, session: urlSession)
                await connected(id, adapter: adapter, connection: connection)
            } catch {
                await failed(id, error)
            }
        }
        sessions[id]?.tasks.append(task)
    }

    private func attemptStarted(_ id: Int) {
        guard let session = sessions[id] else { return }
        sessions[id]?.attemptStart = channels[session.channel].position
    }

    private func connected(_ id: Int, adapter: any RealtimeAdapter, connection: RealtimeConnection) {
        guard sessions[id]?.phase == .connecting else {
            connection.close()
            return
        }
        sessions[id]?.adapter = adapter
        sessions[id]?.connection = connection
        sessions[id]?.phase = .waitingStart
        let receiver = Task.detached(priority: .utility) { [self] in
            do {
                while true {
                    let message = try await connection.receive()
                    await received(id, message)
                }
            } catch {
                await receiveEnded(id, error, closeCode: connection.closeCode)
            }
        }
        let sender = Task.detached(priority: .utility) { [self] in await sendLoop(id, connection) }
        sessions[id]?.tasks += [receiver, sender]
        if policy.announcesStart {
            let timeout = Task.detached { [self] in
                try? await Task.sleep(for: Self.startTimeout)
                await startTimedOut(id)
            }
            sessions[id]?.tasks.append(timeout)
        } else {
            start(id, expiresAt: nil)
        }
    }

    /// The session takes audio: from its replay start, else from its planned start or its attempt's start, at most
    /// `holdSeconds` back.
    private func start(_ id: Int, expiresAt: Date?) {
        guard var session = sessions[id], session.phase == .waitingStart, let adapter = session.adapter else { return }
        let channel = session.channel
        let rate = Double(Self.captureRate)
        let position = channels[channel].position
        var from = session.plan.replayFrom
            ?? max(session.plan.from ?? session.attemptStart, position - Int(policy.holdSeconds * rate))
        from = min(max(from, channels[channel].ringStart), position)
        let now = ContinuousClock.now
        session.phase = .live
        session.offsetSample = from
        session.nextSample = from
        session.record = channels[channel].coverage.open(at: Double(from) / rate, bridged: session.plan.bridged)
        if session.plan.resumes, let record = session.record, record > 0,
           channels[channel].coverage.overlap(record) == nil {
            channels[channel].resumes.append(Double(session.plan.from ?? from) / rate)
        }
        session.paceStart = now
        session.lastSend = now
        session.lastPing = now
        session.liveSince = now
        session.backlogLimit = Double(position - from) / rate + Self.stallSeconds
        if adapter.sampleRate != Self.captureRate {
            session.resampler = RealtimeResampler(from: Self.captureRate, to: adapter.sampleRate)
        }
        if policy.commitsOnPause {
            session.chunker = SpeechChunker(
                maxChunkSeconds: RecordingTranscriber.liveMaxChunkSeconds,
                shortPauseMinSeconds: RecordingTranscriber.liveShortPauseMinSeconds,
                shortPauseFrames: RecordingTranscriber.liveShortPauseFrames)
        }
        let limits = [expiresAt.map { $0.timeIntervalSinceNow }, policy.maxSessionSeconds].compactMap { $0 }
            .filter { $0 > 0 }
        if let limit = limits.min() {
            let lead = SpeakerLabelReconciler.labelOverlapSeconds + Self.rotationMarginSeconds
            session.rotateAt = now + .seconds(max(limit / 2, limit - lead))
        }
        if policy.announcesStart { confirm(&session) }
        sessions[id] = session
        channels[channel].liveSinceResume = true
        promote(channel)
    }

    private func startTimedOut(_ id: Int) {
        guard sessions[id]?.phase == .waitingStart else { return }
        fail(id, kind: "start_timeout", permanent: false)
    }

    /// The connection works: connect failures start over.
    private func confirm(_ session: inout Session) {
        guard !session.confirmed else { return }
        session.confirmed = true
        channels[session.channel].failures = 0
    }

    /// Closes a streaming session once it sent its audio through `sample`; `drainTimeout` bounds the drain.
    private func close(_ id: Int, at sample: Int) {
        guard let session = sessions[id], session.phase == .live else { return }
        sessions[id]?.phase = .closing
        sessions[id]?.closeAt = max(session.nextSample, min(sample, channels[session.channel].position))
        for (other, successor) in sessions where successor.successorOf == id { sessions[other]?.successorOf = nil }
        let deadline = Task.detached { [self] in
            try? await Task.sleep(for: Self.drainTimeout)
            await complete(id, drained: false)
        }
        sessions[id]?.tasks.append(deadline)
    }

    /// Ends a session: its coverage, its tasks and socket; then the next session, the live display, Stop.
    private func complete(_ id: Int, drained: Bool) {
        guard let session = sessions.removeValue(forKey: id) else { return }
        session.tasks.forEach { $0.cancel() }
        session.connection?.close()
        let channel = session.channel
        if let record = session.record {
            channels[channel].coverage.close(record, drained: drained)
            if channels[channel].display == record { emit(.partial(channel: channel, text: nil, start: nil)) }
        }
        for (other, successor) in sessions where successor.successorOf == id { sessions[other]?.successorOf = nil }
        promote(channel)
        ensure(channel)
        resumeIfDrained()
    }

    private func resumeIfDrained() {
        guard finishing, sessions.isEmpty, let waiter = drainWaiter else { return }
        drainWaiter = nil
        waiter.resume()
    }

    /// The newest streaming session becomes the one shown live once every older one closed: its label mapping is
    /// fixed and its finals past the cut that came meanwhile are shown.
    private func promote(_ channel: Int) {
        let records = sessions.values.filter { $0.channel == channel }.compactMap(\.record)
        guard let newest = records.max(), newest != channels[channel].display,
              !records.contains(where: { $0 < newest }) else { return }
        channels[channel].display = newest
        channels[channel].coverage.freezeLabels(newest)
        let coverage = channels[channel].coverage
        for segment in coverage.sessions[newest].segments where segment.end > coverage.cut(newest) {
            emit(.final(channel: channel, segment: Self.raw(segment, speaker: coverage.liveLabel(newest, segment.speaker))))
        }
        setState(channel, .live)
    }

    // MARK: - Sending

    private nonisolated func sendLoop(_ id: Int, _ connection: RealtimeConnection) async {
        while !Task.isCancelled {
            guard let step = await step(id) else { return }
            do {
                for message in step.messages { try await connection.send(message) }
                if step.ping { connection.ping() }
            } catch {
                await failed(id, error)
                return
            }
            if step.messages.isEmpty {
                try? await Task.sleep(for: Self.pollInterval)
            } else {
                await sent(id)
            }
        }
    }

    /// What the session's sender sends next: audio frames due (paced), then at a close `finalize` and
    /// `closeMessages`; a keep-alive while no audio flows; a ping when due. nil once the session is gone.
    private func step(_ id: Int) -> Step? {
        guard let current = sessions[id] else { return nil }
        let now = ContinuousClock.now
        if current.phase == .live, !current.rotating, let rotateAt = current.rotateAt, now >= rotateAt { rotate(id) }
        if current.phase == .live, let old = current.successorOf,
           Double(current.nextSample - current.offsetSample) / Double(Self.captureRate)
           >= SpeakerLabelReconciler.labelOverlapSeconds
        {
            close(old, at: channels[current.channel].position)
        }
        guard var session = sessions[id], var adapter = session.adapter else { return sessions[id] == nil ? nil : Step() }
        if session.closeDelivered, adapter.isDrained {
            complete(id, drained: true)
            return nil
        }
        var step = Step()
        if (session.phase == .live || session.phase == .closing), !session.closeSent {
            let end = session.phase == .closing ? session.closeAt : channels[session.channel].position
            step.messages = frames(&session, &adapter, through: end, now: now)
            if session.phase == .closing, end - session.nextSample < Self.minimumFrameSamples {
                let streamed = Double(session.nextSample - session.offsetSample) / Double(Self.captureRate)
                step.messages += adapter.finalize(at: streamed) + adapter.closeMessages()
                session.closeSent = true
                session.closeDelivered = step.messages.isEmpty
            }
        }
        if step.messages.isEmpty, !session.closeSent, let keepAlive = adapter.keepAlive,
           now - session.lastSend >= keepAlive.interval
        {
            step.messages = [keepAlive.message]
        }
        if !step.messages.isEmpty { session.lastSend = now }
        if now - session.lastPing >= Self.pingInterval {
            step.ping = true
            session.lastPing = now
        }
        session.adapter = adapter
        sessions[id] = session
        return step
    }

    /// Frames of the ring from the session's next sample up to `end`, as many as the pace allows (at most
    /// `framesPerStep`); with `commitsOnPause`, a commit after each speech chunk the pause detector closes.
    private func frames(
        _ session: inout Session, _ adapter: inout any RealtimeAdapter, through end: Int, now: ContinuousClock.Instant
    ) -> [RealtimeMessage] {
        let channel = session.channel
        let rate = Double(Self.captureRate)
        let elapsed = (now - session.paceStart) / .seconds(1)
        var messages: [RealtimeMessage] = []
        for _ in 0..<Self.framesPerStep {
            let available = end - session.nextSample
            let count = available >= Self.frameSamples ? Self.frameSamples
                : session.phase == .closing && available >= Self.minimumFrameSamples ? available : 0
            let streamed = Double(session.nextSample - session.offsetSample + count) / rate
            guard count > 0, streamed <= policy.burstSeconds + policy.rate * elapsed else { break }
            let lower = session.nextSample - channels[channel].ringStart
            guard lower >= 0 else { break }
            let samples = Array(channels[channel].ring[lower..<lower + count])
            messages.append(adapter.frame(CloudRealtime.pcm16(session.resampler?.process(samples) ?? samples)))
            session.nextSample += count
            if session.chunker?.append(samples, language: nil).isEmpty == false {
                messages += adapter.finalize(at: streamed)
            }
        }
        if let record = session.record {
            channels[channel].coverage.sent(record, through: Double(session.nextSample) / rate)
        }
        return messages
    }

    /// A send went through: the connection works (and the close messages are out once sent).
    private func sent(_ id: Int) {
        guard var session = sessions[id] else { return }
        confirm(&session)
        if session.closeSent { session.closeDelivered = true }
        sessions[id] = session
    }

    /// Before the session cap: a parallel successor (the old session closes after the overlap), else the old one
    /// closes now and the next one starts where it stopped (replaying its last `labelOverlapSeconds` where allowed).
    private func rotate(_ id: Int) {
        guard let session = sessions[id] else { return }
        let channel = session.channel
        let position = channels[channel].position
        sessions[id]?.rotating = true
        switch policy.handover {
        case .parallel:
            open(channel, Plan(bridged: true, successorOf: id), delay: 0)
        case .replay:
            channels[channel].plan = Plan(
                bridged: true, replayFrom: position - Self.overlapSamples, from: position, waits: true)
            close(id, at: position)
        case .none:
            channels[channel].plan = Plan(from: position, waits: true)
            close(id, at: position)
        }
    }

    // MARK: - Receiving

    private func received(_ id: Int, _ message: RealtimeMessage) {
        guard var session = sessions[id], var adapter = session.adapter else { return }
        // An undecodable message is skipped, not fatal.
        let events = (try? adapter.parse(message)) ?? []
        session.adapter = adapter
        if session.phase != .waitingStart { confirm(&session) }
        sessions[id] = session
        for event in events {
            guard sessions[id] != nil else { return }
            switch event {
            case .started(let expiresAt):
                start(id, expiresAt: expiresAt)
            case .partial(let text, let start, let end, _):
                partial(id, text: text, start: start, end: end)
            case .final(let segment):
                final(id, segment)
            case .failed(let kind):
                let failure = Self.failure(kind: kind)
                fail(id, kind: failure.kind, permanent: failure.permanent)
            case .ended:
                if sessions[id]?.phase == .closing {
                    complete(id, drained: true)
                } else {
                    fail(id, kind: "ended", permanent: false, drained: true)
                }
            }
        }
        if let session = sessions[id], session.closeDelivered, session.adapter?.isDrained == true {
            complete(id, drained: true)
        }
    }

    private func partial(_ id: Int, text: String, start: Double?, end: Double?) {
        guard let session = sessions[id], let record = session.record,
              channels[session.channel].display == record else { return }
        let rate = Double(Self.captureRate)
        let offset = Double(session.offsetSample) / rate
        let cut = channels[session.channel].coverage.cut(record)
        // The replayed audio before the cut repeats the previous session's text.
        guard (end.map { $0 + offset } ?? Double(session.nextSample) / rate) > cut else { return }
        emit(.partial(channel: session.channel, text: text, start: start.map { $0 + offset }))
    }

    private func final(_ id: Int, _ segment: CloudSegment) {
        guard let session = sessions[id], let record = session.record else { return }
        let channel = session.channel
        let file = channels[channel].coverage.final(record, segment)
        let coverage = channels[channel].coverage
        guard channels[channel].display == record, file.end > coverage.cut(record) else { return }
        emit(.final(channel: channel, segment: Self.raw(file, speaker: coverage.liveLabel(record, file.speaker))))
    }

    private func receiveEnded(_ id: Int, _ error: any Error, closeCode: URLSessionWebSocketTask.CloseCode) {
        guard let session = sessions[id] else { return }
        if session.phase == .closing {
            complete(id, drained: session.closeDelivered && closeCode == .normalClosure)
        } else if case LLMError.httpStatus = error {
            failed(id, error)
        } else if closeCode != .invalid {
            fail(id, kind: closeCode == .normalClosure ? "closed" : "close_\(closeCode.rawValue)", permanent: false)
        } else {
            failed(id, error)
        }
    }

    // MARK: - Failures

    private func failed(_ id: Int, _ error: any Error) {
        guard sessions[id] != nil, !(error is CancellationError) else { return }
        let failure = Self.failure(error)
        fail(id, kind: failure.kind, permanent: failure.permanent)
    }

    /// Ends a failed session. A parallel successor's failure only delays the rotation; a permanent failure, or the
    /// same connect failure `maxRepeatedFailures` times, stops the channel; else the channel reconnects with backoff.
    private func fail(_ id: Int, kind: String, permanent: Bool, drained: Bool = false) {
        guard let session = sessions[id] else { return }
        let channel = session.channel
        if session.phase != .closing {
            if let old = session.successorOf, sessions[old]?.phase == .live {
                sessions[old]?.rotating = false
                sessions[old]?.rotateAt = ContinuousClock.now + .seconds(Self.maxBackoffSeconds)
            } else {
                let healthy = session.liveSince.map { (ContinuousClock.now - $0) / .seconds(1) >= Self.healthySessionSeconds }
                channels[channel].failures = healthy == true ? 1 : channels[channel].failures + 1
                let repeated = !session.confirmed && kind != "network"
                    && channels[channel].failures >= Self.maxRepeatedFailures
                if permanent || repeated {
                    channels[channel].failure = kind
                    setState(channel, .failed(kind: kind))
                }
            }
        }
        complete(id, drained: drained)
        // A stopped channel streams nothing: a parallel successor still open closes too.
        if channels[channel].failure != nil { endSessions(channel, at: channels[channel].position) }
    }

    /// An error as a short kind, and whether retrying cannot help. Never the error's text or URL. Pure, for tests.
    static func failure(_ error: any Error) -> (kind: String, permanent: Bool) {
        switch error {
        case let failure as Failure:
            return (failure.kind, true)
        case LLMError.httpStatus(let code, _):
            switch code {
            case 401, 403: return ("auth", true)
            case 402: return ("payment", true)
            case 429: return ("plan_limit", true)
            default: return ("http_\(code)", (400..<500).contains(code) && code != 408)
            }
        case LLMError.provider, LLMError.invalidBaseURL:
            return ("config", true)
        case is URLError:
            return ("network", false)
        default:
            return ("error", false)
        }
    }

    /// A provider's error kind (`RealtimeEvent.failed`): a concurrency or quota refusal is "plan_limit" and a key
    /// refusal "auth" (both stop the channel); anything else reconnects. Pure, for tests.
    static func failure(kind: String) -> (kind: String, permanent: Bool) {
        let lowered = kind.lowercased()
        if ["quota", "concurren", "too_many"].contains(where: lowered.contains) || lowered == "429" {
            return ("plan_limit", true)
        }
        if ["auth", "api_key", "forbidden"].contains(where: lowered.contains) || ["401", "403"].contains(lowered) {
            return ("auth", true)
        }
        return (CloudRealtime.errorKind(kind), false)
    }

    // MARK: - Helpers

    static let overlapSamples = Int(SpeakerLabelReconciler.labelOverlapSeconds) * captureRate

    private func setState(_ channel: Int, _ state: State) {
        guard channels[channel].state != state else { return }
        if case .failed = channels[channel].state { return }
        channels[channel].state = state
        emit(.state(channel: channel, state))
    }

    private func emit(_ update: Update) {
        updatesContinuation.yield(update)
    }

    /// The trimmed marker; nil for an empty or "auto" one.
    private static func marker(_ language: String?) -> String? {
        let trimmed = language?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return trimmed.isEmpty || trimmed.lowercased() == "auto" ? nil : trimmed
    }

    /// The channel's resumes with scoped labels, or none when its finals carry no labels.
    private static func bridges(_ channel: Channel) -> [Double] {
        channel.coverage.sessions.contains { $0.segments.contains { $0.speaker?.isEmpty == false } }
            ? channel.resumes : []
    }

    private static func raw(_ segments: [CloudSegment]) -> [RawSegment] {
        segments.map { raw($0, speaker: $0.speaker) }
    }

    private static func raw(_ segment: CloudSegment, speaker: String?) -> RawSegment {
        RawSegment(start: segment.start, end: segment.end, text: segment.text, detectedLanguage: segment.language,
                   speaker: speaker)
    }

    // MARK: - State types

    enum Input: Sendable {
        case audio(mic: [Float], system: [Float], startSample: Int, language: String?)
        case pause, finish
    }

    /// How the next session of a channel starts.
    struct Plan: Sendable {
        /// Shares audio with the session before (labels reconciled).
        var bridged = false
        /// First sample to send: the replay of the session before's last `labelOverlapSeconds`.
        var replayFrom: Int?
        /// First sample to send (at most `holdSeconds` back) when not replaying; nil = the attempt's start.
        var from: Int?
        /// Opens only once the channel's closing sessions drained (no third socket).
        var waits = false
        /// A parallel rotation's old session, closed after the overlap.
        var successorOf: Int?
        /// The first session after a pause.
        var resumes = false
    }

    struct Channel {
        var coverage = RealtimeCoverage()
        /// Audio from `ringStart` (file samples) to `position`.
        var ring: [Float] = []
        var ringStart = 0
        var position = 0
        var started = false
        /// The current marker language (nil = auto) once a block came.
        var language: String?
        var hasMarker = false
        var paused = false
        var plan: Plan?
        /// The coverage index of the session shown live.
        var display: Int?
        var state: State?
        /// Connect failures in a row (backoff); a permanent failure's kind stops the channel.
        var failures = 0
        var failure: String?
        /// A session went live since the start or the last pause: a new connect shows "reconnecting".
        var liveSinceResume = false
        /// File seconds where a session after a pause started with scoped labels (`Result.systemBridges`).
        var resumes: [Double] = []

        func gaps() -> [Span] {
            coverage.gaps(through: Double(position) / Double(RealtimeLiveStream.captureRate))
        }
    }

    enum Phase: Sendable {
        case connecting, waitingStart, live, closing
    }

    struct Session {
        let id: Int
        let channel: Int
        let plan: Plan
        var phase = Phase.connecting
        var tasks: [Task<Void, Never>] = []
        var attemptStart = 0
        var adapter: (any RealtimeAdapter)?
        var connection: RealtimeConnection?
        /// The coverage index once streaming.
        var record: Int?
        /// File samples of stream time 0, and of the next sample to send.
        var offsetSample = 0
        var nextSample = 0
        var paceStart = ContinuousClock.now
        var lastSend = ContinuousClock.now
        var lastPing = ContinuousClock.now
        var liveSince: ContinuousClock.Instant?
        var backlogLimit = 0.0
        var resampler: RealtimeResampler?
        var chunker: SpeechChunker?
        var confirmed = false
        var rotateAt: ContinuousClock.Instant?
        var rotating = false
        var successorOf: Int?
        var closeAt = 0
        /// The close messages were built, then sent.
        var closeSent = false
        var closeDelivered = false

        init(id: Int, channel: Int, plan: Plan) {
            self.id = id
            self.channel = channel
            self.plan = plan
            successorOf = plan.successorOf
        }
    }

    struct Step {
        var messages: [RealtimeMessage] = []
        var ping = false
    }
}
