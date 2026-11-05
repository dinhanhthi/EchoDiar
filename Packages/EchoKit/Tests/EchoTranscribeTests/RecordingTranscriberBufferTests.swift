import EchoAI
import EchoCore
@testable import EchoTranscribe
import Testing

/// Guards "failures degrade, never lose text" in the recording transcriber's bookkeeping: a chunk over the queue cap,
/// a chunk the ASR failed on, a chunk still queued when the decode task ends and a chunk taken but never completed all
/// become gaps (through `PriorTranscription.gaps`, as `finish()` builds `micGaps`/`systemGaps`); transcribed and
/// silence chunks do not. Chunks are whole seconds of a constant 0.5 (speech) or zeros (silence).
struct RecordingTranscriberBufferTests {
    typealias Buffer = RecordingTranscriber.Buffer

    @Test func aChunkOverTheCapIsDroppedAndAGap() {
        var buffer = Buffer(maxChunkSeconds: 30, maxQueuedSamples: Self.rate)
        buffer.enqueue([Self.speech(at: 0), Self.speech(at: 2), Self.silence(at: 4)], channel: Self.mic)
        let item = buffer.take()!
        buffer.complete(item, segments: [RawSegment(start: 0, end: 1, text: "x")])

        #expect(Self.outcomes(buffer) == [.transcribed, .dropped, .silence])
        #expect(Self.gaps(buffer) == [Span(start: 2, end: 3)])
    }

    @Test func failedChunksAndChunksAfterTheDecoderFailedAreGaps() {
        var buffer = Buffer(maxChunkSeconds: 30)
        buffer.enqueue([Self.speech(at: 0), Self.speech(at: 2), Self.speech(at: 4)], channel: Self.mic)
        buffer.complete(buffer.take()!, segments: nil)
        buffer.complete(buffer.take()!, segments: [RawSegment(start: 2, end: 3, text: "x")])
        buffer.fail()
        buffer.enqueue([Self.speech(at: 6)], channel: Self.mic)

        #expect(Self.outcomes(buffer) == [.failed, .transcribed, .failed, .failed])
        #expect(buffer.take() == nil)
        #expect(Self.gaps(buffer) == [Span(start: 0, end: 1), Span(start: 4, end: 5), Span(start: 6, end: 7)])
    }

    /// The decode task ended with an error (its catch calls `fail()`) while a chunk was being decoded: it stays pending.
    @Test func aChunkTakenButNeverCompletedStaysPendingAndAGap() {
        var buffer = Buffer(maxChunkSeconds: 30)
        buffer.enqueue([Self.speech(at: 0), Self.speech(at: 2)], channel: Self.mic)
        _ = buffer.take()
        buffer.fail()

        #expect(Self.outcomes(buffer) == [.pending, .failed])
        #expect(Self.gaps(buffer) == [Span(start: 0, end: 1), Span(start: 2, end: 3)])
    }

    /// Guards the live Merci regression's "others speak" check: a system chunk that is not silence within
    /// `TranscriptMerger.bleedTimeTolerance` of the span, or a system chunk still open that started by the span's end
    /// (plus the tolerance); a silence chunk, a mic chunk or system speech further away is not.
    @Test func othersSpeakDuringASpanOnlyWhenTheSystemChannelHasSpeechNearIt() {
        var closed = Buffer(maxChunkSeconds: 30)
        closed.enqueue([Self.speech(at: 2)], channel: Self.system)
        closed.enqueue([Self.speech(at: 6)], channel: Self.mic)
        #expect(closed.othersSpeak(during: Span(start: 1, end: 1.8)))
        #expect(!closed.othersSpeak(during: Span(start: 5.5, end: 7)))

        var silence = Buffer(maxChunkSeconds: 30)
        silence.enqueue([Self.silence(at: 2)], channel: Self.system)
        #expect(!silence.othersSpeak(during: Span(start: 1.8, end: 2.5)))

        var open = Buffer(maxChunkSeconds: 30)
        _ = open.append(mic: [Float](repeating: 0, count: Self.rate),
                        system: [Float](repeating: 0.5, count: Self.rate), language: nil)
        #expect(open.channels[Self.system].chunks.isEmpty)
        #expect(open.othersSpeak(during: Span(start: 0.5, end: 0.9)))
    }

    /// Guards the tentative text's cadence: a partial of the open chunk is due from `partialMinSeconds`, then each time
    /// it grew `partialStepSeconds`; `append` wakes the decoder once per due step, and a new chunk starts over (no
    /// carry-over from the closed one). Speech on the mic from sample 0 opens at once, so its start is 0. Counted in
    /// half seconds from the constants, so it holds for any step.
    @Test func aPartialOfTheOpenChunkIsDueFromTheMinimumThenEachStep() {
        var buffer = Buffer(maxChunkSeconds: 30)
        for _ in 0..<(Self.minHalves - 1) { #expect(!Self.appendHalfSecond(&buffer)) }
        #expect(buffer.takePartial() == nil)

        #expect(Self.appendHalfSecond(&buffer))
        #expect(!Self.appendHalfSecond(&buffer))
        let first = buffer.takePartial()
        #expect(first?.channel == Self.mic)
        #expect(first?.chunk.startSample == 0)
        #expect(first?.chunk.samples.count == (Self.minHalves + 1) * Self.half)
        #expect(buffer.takePartial() == nil)

        for _ in 0..<(Self.stepHalves - 1) { #expect(!Self.appendHalfSecond(&buffer)) }
        #expect(buffer.takePartial() == nil)
        #expect(Self.appendHalfSecond(&buffer))
        let grown = Self.minHalves + 1 + Self.stepHalves
        #expect(buffer.takePartial()?.chunk.samples.count == grown * Self.half)

        buffer.flush(paused: false)
        buffer.complete(buffer.take()!, segments: [])
        for _ in 0..<(Self.minHalves - 1) { #expect(!Self.appendHalfSecond(&buffer)) }
        #expect(buffer.takePartial() == nil)
        #expect(Self.appendHalfSecond(&buffer))
        #expect(buffer.takePartial()?.chunk.startSample == grown * Self.half)
    }

    /// Guards "partials never starve finals": no partial while a closed chunk waits to be decoded, nor on a channel
    /// whose last final decode took longer than `partialMaxDecodeSeconds`.
    @Test func noPartialWhileAChunkIsQueuedOrTheLastDecodeWasSlow() {
        var buffer = Buffer(maxChunkSeconds: 30)
        for _ in 0..<Self.minHalves { _ = Self.appendHalfSecond(&buffer) }
        buffer.enqueue([Self.speech(at: 100)], channel: Self.system)
        #expect(buffer.takePartial() == nil)
        _ = buffer.take()
        #expect(buffer.takePartial()?.channel == Self.mic)

        let limit = RecordingTranscriber.partialMaxDecodeSeconds
        buffer.recordDecode(channel: Self.mic, wallSeconds: limit + 0.5)
        for _ in 0..<Self.stepHalves { #expect(!Self.appendHalfSecond(&buffer)) }
        #expect(buffer.takePartial() == nil)
        buffer.recordDecode(channel: Self.mic, wallSeconds: limit - 0.5)
        #expect(buffer.takePartial()?.chunk.samples.count == (Self.minHalves + Self.stepHalves) * Self.half)
    }

    /// Guards the clearing of the tentative text: it stays while its chunk is open or waits in the queue (its final
    /// replaces it), and goes once the chunk is neither (closed as silence or dropped, or completed), at Stop
    /// (`finishing`: no final may come, e.g. on a constrained Mac) and after `close()`.
    @Test func aTentativeIsKeptWhileItsChunkIsOpenOrQueued() {
        var buffer = Buffer(maxChunkSeconds: 30)
        for _ in 0..<2 { _ = Self.appendHalfSecond(&buffer) }
        #expect(buffer.keepsTentative(channel: Self.mic, startSample: 0))
        #expect(!buffer.keepsTentative(channel: Self.mic, startSample: Self.half))
        #expect(!buffer.keepsTentative(channel: Self.system, startSample: 0))

        buffer.flush(paused: true)
        #expect(buffer.keepsTentative(channel: Self.mic, startSample: 0))
        buffer.complete(buffer.take()!, segments: [])
        #expect(!buffer.keepsTentative(channel: Self.mic, startSample: 0))

        _ = Self.appendHalfSecond(&buffer)
        let start = buffer.channels[Self.mic].chunker.openSpan!.startSample
        #expect(buffer.keepsTentative(channel: Self.mic, startSample: start))
        buffer.finishing = true
        #expect(!buffer.keepsTentative(channel: Self.mic, startSample: start))
        buffer.close()
        #expect(!buffer.keepsTentative(channel: Self.mic, startSample: start))
    }

    private static let rate = RecordingTranscriber.sampleRate
    private static let mic = RecordingSession.Channel.mic
    private static let system = RecordingSession.Channel.system

    private static let half = rate / 2
    /// `partialMinSeconds` and `partialStepSeconds` in half seconds (`appendHalfSecond` calls).
    private static let minHalves = 2 * RecordingTranscriber.partialMinSeconds
    private static let stepHalves = 2 * RecordingTranscriber.partialStepSeconds

    /// Half a second of speech on the mic and silence on the system channel; what `append` returned.
    private static func appendHalfSecond(_ buffer: inout Buffer) -> Bool {
        buffer.append(mic: [Float](repeating: 0.5, count: half), system: [Float](repeating: 0, count: half),
                      language: nil)
    }

    private static func speech(at second: Int) -> SpeechChunker.Chunk {
        SpeechChunker.Chunk(startSample: second * rate, samples: [Float](repeating: 0.5, count: rate), language: nil)
    }

    private static func silence(at second: Int) -> SpeechChunker.Chunk {
        SpeechChunker.Chunk(startSample: second * rate, samples: [Float](repeating: 0, count: rate), language: nil)
    }

    private static func outcomes(_ buffer: Buffer) -> [PriorTranscription.ChunkOutcome] {
        buffer.channels[mic].chunks.map { $0.outcome }
    }

    private static func gaps(_ buffer: Buffer) -> [Span] {
        PriorTranscription.gaps(buffer.channels[mic].chunks)
    }
}

/// Guards the cloud realtime coverage (`RealtimeCoverage`): stream time becomes file time by the session's offset, a
/// disconnect and the audio after an undrained session's last final become gaps, and a planned rotation's parallel
/// overlap keeps one speaker when the new session labels the same voice differently.
struct RealtimeCoverageTests {
    /// (c) A session from 0 that sent through 10 s with its last final at 8 s and did not drain, then a reconnect at
    /// 15 s that drained through 25 s, in a 30 s recording.
    @Test func aReconnectLeavesTheDisconnectAndTheUnfinalizedAudioAsGaps() {
        var coverage = RealtimeCoverage()
        let first = coverage.open(at: 0, bridged: false)
        coverage.sent(first, through: 10)
        coverage.final(first, CloudSegment(start: 1, end: 3, text: "one"))
        coverage.final(first, CloudSegment(start: 4, end: 8, text: "two"))
        coverage.close(first, drained: false)
        let second = coverage.open(at: 15, bridged: false)
        coverage.sent(second, through: 25)
        let final = coverage.final(second, CloudSegment(start: 2, end: 4, text: "three"))
        coverage.close(second, drained: true)

        #expect(final.start == 17 && final.end == 19)
        #expect(coverage.gaps(through: 30) == [Span(start: 8, end: 15), Span(start: 25, end: 30)])
        #expect(coverage.segments().map(\.start) == [1, 4, 17])
    }

    /// (d) A rotation opens session 1 at 30 s while session 0 streams through 60 s; session 1 calls the voice "B",
    /// session 0 "A": the joined finals and the live label after `freezeLabels` are all "A", none repeated.
    @Test func aParallelRotationKeepsOneSpeaker() {
        var coverage = RealtimeCoverage()
        let old = coverage.open(at: 0, bridged: false)
        coverage.sent(old, through: 60)
        for start in stride(from: 0.0, to: 60, by: 2) {
            coverage.final(old, CloudSegment(start: start, end: start + 2, text: "old", speaker: "A"))
        }
        let new = coverage.open(at: 30, bridged: true)
        coverage.sent(new, through: 90)
        for start in stride(from: 0.0, to: 60, by: 2) {
            coverage.final(new, CloudSegment(start: start, end: start + 2, text: "new", speaker: "B"))
        }
        coverage.close(old, drained: true)
        coverage.freezeLabels(new)
        coverage.close(new, drained: true)

        let segments = coverage.segments()
        #expect(Set(segments.compactMap(\.speaker)) == ["A"])
        #expect(segments.map(\.start) == Array(stride(from: 0.0, to: 90, by: 2)))
        #expect(coverage.liveLabel(new, "B") == "A")
        #expect(coverage.gaps(through: 90).isEmpty)
    }
}

/// Guards the cloud realtime transcriber's recovery: once both channels stopped (`.failed`), a channel that is live
/// again (a "config" failure recovers at a language change) brings the state back to listening and its finals show.
struct RealtimeRecoveryTests {
    @Test func bothChannelsFailedRecoverOnceOneIsLiveAgain() async throws {
        let policy = try #require(RealtimePolicy.of(preset: "speechmatics"))
        let stream = RealtimeLiveStream(modelID: "rt-model", policy: policy) { _, _ in
            throw RealtimeLiveStream.Failure(kind: "config")
        }
        let transcriber = RecordingTranscriber(
            realtime: stream, engineID: "cloud:fake", displayOnly: true, language: { nil })
        var updates = transcriber.updates.makeAsyncIterator()
        let system = RecordingSession.Channel.system

        for channel in 0..<2 { await transcriber.apply(.realtimeState(channel: channel, .failed(kind: "config"))) }
        let failed = try #require(await updates.next())
        #expect({ if case .failed = failed.state { true } else { false } }())

        await transcriber.apply(.final(channel: system, segment: RawSegment(start: 0, end: 1, text: "dropped")))
        await transcriber.apply(.realtimeState(channel: system, .connecting))
        await transcriber.apply(.realtimeState(channel: system, .live))
        await transcriber.apply(.final(channel: system, segment: RawSegment(start: 2, end: 3, text: "hello again")))
        let recovered = try #require(await updates.next())
        #expect(recovered.state == .listening)
        #expect(recovered.lines.map(\.text).contains { $0.contains("hello again") })
        #expect(!recovered.lines.contains { $0.text.contains("dropped") })
        await transcriber.cancel()
    }
}
