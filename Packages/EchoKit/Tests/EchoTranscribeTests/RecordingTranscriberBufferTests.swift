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

    private static let rate = RecordingTranscriber.sampleRate
    private static let mic = RecordingSession.Channel.mic

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
