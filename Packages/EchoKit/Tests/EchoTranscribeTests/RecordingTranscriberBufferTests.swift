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
    /// whose last final decode took more than `partialMaxDecodeShare` of its chunk's duration.
    @Test func noPartialWhileAChunkIsQueuedOrTheLastDecodeWasSlow() {
        var buffer = Buffer(maxChunkSeconds: 30)
        for _ in 0..<Self.minHalves { _ = Self.appendHalfSecond(&buffer) }
        buffer.enqueue([Self.speech(at: 100)], channel: Self.system)
        #expect(buffer.takePartial() == nil)
        _ = buffer.take()
        #expect(buffer.takePartial()?.channel == Self.mic)

        let share = RecordingTranscriber.partialMaxDecodeShare
        buffer.recordDecode(channel: Self.mic, wallSeconds: (share + 0.1) * 10, chunkSeconds: 10)
        for _ in 0..<Self.stepHalves { #expect(!Self.appendHalfSecond(&buffer)) }
        #expect(buffer.takePartial() == nil)
        buffer.recordDecode(channel: Self.mic, wallSeconds: (share - 0.1) * 10, chunkSeconds: 10)
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
