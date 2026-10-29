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
    private const int System = RecordingSession.Channel.System;

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

    // Guards the live Merci regression's "others speak" check: a system chunk that is not silence within
    // TranscriptMerger.BleedTimeTolerance of the span, or a system chunk still open that started by the span's end (plus
    // the tolerance); a silence chunk, a mic chunk or system speech further away is not.
    [Fact]
    public void OthersSpeakDuringASpanOnlyWhenTheSystemChannelHasSpeechNearIt()
    {
        var closed = new RecordingTranscriber.Buffer(maxChunkSeconds: 30);
        closed.Enqueue([Speech(at: 2)], System);
        closed.Enqueue([Speech(at: 6)], Mic);
        Assert.True(closed.OthersSpeak(new Span(1, 1.8)));
        Assert.False(closed.OthersSpeak(new Span(5.5, 7)));

        var silence = new RecordingTranscriber.Buffer(maxChunkSeconds: 30);
        silence.Enqueue([Silence(at: 2)], System);
        Assert.False(silence.OthersSpeak(new Span(1.8, 2.5)));

        var open = new RecordingTranscriber.Buffer(maxChunkSeconds: 30);
        var interleaved = new short[2 * Rate];
        for (var i = 0; i < Rate; i++) interleaved[2 * i + System] = 16_384;
        open.Append(interleaved, Rate, 2, null);
        Assert.Empty(open.Channels[System].Chunks);
        Assert.True(open.OthersSpeak(new Span(0.5, 0.9)));
    }

    // Guards the tentative text's cadence: a partial of the open chunk is due from PartialMinSeconds, then each time it
    // grew PartialStepSeconds; Append wakes the decoder once per due step, and a new chunk starts over (no carry-over
    // from the closed one). Speech on the mic from sample 0 opens at once, so its start is 0. Counted in half seconds
    // from the constants, so it holds for any step.
    [Fact]
    public void APartialOfTheOpenChunkIsDueFromTheMinimumThenEachStep()
    {
        var buffer = new RecordingTranscriber.Buffer(maxChunkSeconds: 30);
        for (var i = 0; i < MinHalves - 1; i++) Assert.False(AppendHalfSecond(buffer));
        Assert.Null(buffer.TakePartial());

        Assert.True(AppendHalfSecond(buffer));
        Assert.False(AppendHalfSecond(buffer));
        var first = buffer.TakePartial();
        Assert.NotNull(first);
        Assert.Equal(Mic, first.Channel);
        Assert.Equal(0, first.Chunk.StartSample);
        Assert.Equal((MinHalves + 1) * Half, first.Chunk.Samples.Length);
        Assert.Null(buffer.TakePartial());

        for (var i = 0; i < StepHalves - 1; i++) Assert.False(AppendHalfSecond(buffer));
        Assert.Null(buffer.TakePartial());
        Assert.True(AppendHalfSecond(buffer));
        const int grown = MinHalves + 1 + StepHalves;
        Assert.Equal(grown * Half, buffer.TakePartial()?.Chunk.Samples.Length);

        buffer.Flush(paused: false);
        buffer.Complete(buffer.Take()!, []);
        for (var i = 0; i < MinHalves - 1; i++) Assert.False(AppendHalfSecond(buffer));
        Assert.Null(buffer.TakePartial());
        Assert.True(AppendHalfSecond(buffer));
        Assert.Equal(grown * Half, buffer.TakePartial()?.Chunk.StartSample);
    }

    // Guards "partials never starve finals": no partial while a closed chunk waits to be decoded, nor on a channel whose
    // last final decode took more than PartialMaxDecodeShare of its chunk's duration.
    [Fact]
    public void NoPartialWhileAChunkIsQueuedOrTheLastDecodeWasSlow()
    {
        var buffer = new RecordingTranscriber.Buffer(maxChunkSeconds: 30);
        for (var i = 0; i < MinHalves; i++) AppendHalfSecond(buffer);
        buffer.Enqueue([Speech(at: 100)], System);
        Assert.Null(buffer.TakePartial());
        _ = buffer.Take();
        Assert.Equal(Mic, buffer.TakePartial()?.Channel);

        buffer.RecordDecode(Mic, wallSeconds: 3, chunkSeconds: 4);
        for (var i = 0; i < StepHalves; i++) Assert.False(AppendHalfSecond(buffer));
        Assert.Null(buffer.TakePartial());
        buffer.RecordDecode(Mic, wallSeconds: 2, chunkSeconds: 4);
        Assert.Equal((MinHalves + StepHalves) * Half, buffer.TakePartial()?.Chunk.Samples.Length);
    }

    // Guards the clearing of the tentative text: it stays while its chunk is open or waits in the queue (its final
    // replaces it), and goes once the chunk is neither (closed as silence or dropped, or completed), at Stop (Finishing:
    // no final may come) and after Close().
    [Fact]
    public void ATentativeIsKeptWhileItsChunkIsOpenOrQueued()
    {
        var buffer = new RecordingTranscriber.Buffer(maxChunkSeconds: 30);
        for (var i = 0; i < 2; i++) AppendHalfSecond(buffer);
        Assert.True(buffer.KeepsTentative(Mic, 0));
        Assert.False(buffer.KeepsTentative(Mic, Half));
        Assert.False(buffer.KeepsTentative(System, 0));

        buffer.Flush(paused: true);
        Assert.True(buffer.KeepsTentative(Mic, 0));
        buffer.Complete(buffer.Take()!, []);
        Assert.False(buffer.KeepsTentative(Mic, 0));

        AppendHalfSecond(buffer);
        var start = buffer.Channels[Mic].Chunker.OpenSpan()!.Value.StartSample;
        Assert.True(buffer.KeepsTentative(Mic, start));
        buffer.Finishing = true;
        Assert.False(buffer.KeepsTentative(Mic, start));
        buffer.Close();
        Assert.False(buffer.KeepsTentative(Mic, start));
    }

    private const int Half = Rate / 2;
    // PartialMinSeconds and PartialStepSeconds in half seconds (AppendHalfSecond calls).
    private const int MinHalves = 2 * RecordingTranscriber.PartialMinSeconds;
    private const int StepHalves = 2 * RecordingTranscriber.PartialStepSeconds;

    // Half a second of speech on the mic and silence on the system channel; what Append returned.
    private static bool AppendHalfSecond(RecordingTranscriber.Buffer buffer)
    {
        var interleaved = new short[2 * Half];
        for (var i = 0; i < Half; i++) interleaved[2 * i + Mic] = 16_384;
        return buffer.Append(interleaved, Half, 2, null);
    }

    private static SpeechChunker.Chunk Speech(int at) => new((long)at * Rate, Enumerable.Repeat(0.5f, Rate).ToArray(), null);

    private static SpeechChunker.Chunk Silence(int at) => new((long)at * Rate, new float[Rate], null);

    private static List<Outcome> Outcomes(RecordingTranscriber.Buffer buffer) =>
        [.. buffer.Channels[Mic].Chunks.Select(c => c.Outcome)];

    private static List<Span> Gaps(RecordingTranscriber.Buffer buffer) => PriorTranscription.Gaps(buffer.Channels[Mic].Chunks);
}
