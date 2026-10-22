using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Transcribe;
using Outcome = EchoDiar.Transcribe.PriorTranscription.ChunkOutcome;

namespace EchoDiar.Tests.Transcribe;

// Swift RecordingTranscriberBufferTests: guards "failures degrade, never lose text" in the recording transcriber's
// bookkeeping: a chunk over the queue cap, a chunk the ASR failed on, a chunk still queued when the decode task ends
// and a chunk taken but never completed all become gaps (through PriorTranscription.Gaps, as FinishAsync builds
// MicGaps/SystemGaps); transcribed and silence chunks do not. Chunks are whole seconds of a constant 0.5 (speech) or
// zeros (silence).
public sealed class RecordingTranscriberBufferTests
{
    private const int Rate = RecordingTranscriber.SampleRate;
    private const int Mic = RecordingSession.Channel.Mic;

    [Fact]
    public void AChunkOverTheCapIsDroppedAndAGap()
    {
        var buffer = new RecordingTranscriber.Buffer(maxChunkSeconds: 30, maxQueuedSamples: Rate);
        buffer.Enqueue([Speech(at: 0), Speech(at: 2), Silence(at: 4)], Mic);
        var item = buffer.Take()!;
        buffer.Complete(item, [new RawSegment(0, 1, "x")]);

        Assert.Equal([Outcome.Transcribed, Outcome.Dropped, Outcome.Silence], Outcomes(buffer));
        Assert.Equal([new Span(2, 3)], Gaps(buffer));
    }

    [Fact]
    public void FailedChunksAndChunksAfterTheDecoderFailedAreGaps()
    {
        var buffer = new RecordingTranscriber.Buffer(maxChunkSeconds: 30);
        buffer.Enqueue([Speech(at: 0), Speech(at: 2), Speech(at: 4)], Mic);
        buffer.Complete(buffer.Take()!, null);
        buffer.Complete(buffer.Take()!, [new RawSegment(2, 3, "x")]);
        buffer.Fail();
        buffer.Enqueue([Speech(at: 6)], Mic);

        Assert.Equal([Outcome.Failed, Outcome.Transcribed, Outcome.Failed, Outcome.Failed], Outcomes(buffer));
        Assert.Null(buffer.Take());
        Assert.Equal([new Span(0, 1), new Span(4, 5), new Span(6, 7)], Gaps(buffer));
    }

    // The decode task ended with an error (its catch calls Fail()) while a chunk was being decoded: it stays pending.
    [Fact]
    public void AChunkTakenButNeverCompletedStaysPendingAndAGap()
    {
        var buffer = new RecordingTranscriber.Buffer(maxChunkSeconds: 30);
        buffer.Enqueue([Speech(at: 0), Speech(at: 2)], Mic);
        _ = buffer.Take();
        buffer.Fail();

        Assert.Equal([Outcome.Pending, Outcome.Failed], Outcomes(buffer));
        Assert.Equal([new Span(0, 1), new Span(2, 3)], Gaps(buffer));
    }

    private static SpeechChunker.Chunk Speech(int at) => new((long)at * Rate, Enumerable.Repeat(0.5f, Rate).ToArray(), null);

    private static SpeechChunker.Chunk Silence(int at) => new((long)at * Rate, new float[Rate], null);

    private static List<Outcome> Outcomes(RecordingTranscriber.Buffer buffer) =>
        [.. buffer.Channels[Mic].Chunks.Select(c => c.Outcome)];

    private static List<Span> Gaps(RecordingTranscriber.Buffer buffer) => PriorTranscription.Gaps(buffer.Channels[Mic].Chunks);
}
