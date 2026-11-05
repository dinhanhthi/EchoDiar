using EchoDiar.AI.Cloud;
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
    // last final decode took longer than PartialMaxDecodeSeconds.
    [Fact]
    public void NoPartialWhileAChunkIsQueuedOrTheLastDecodeWasSlow()
    {
        var buffer = new RecordingTranscriber.Buffer(maxChunkSeconds: 30);
        for (var i = 0; i < MinHalves; i++) AppendHalfSecond(buffer);
        buffer.Enqueue([Speech(at: 100)], System);
        Assert.Null(buffer.TakePartial());
        _ = buffer.Take();
        Assert.Equal(Mic, buffer.TakePartial()?.Channel);

        const double limit = RecordingTranscriber.PartialMaxDecodeSeconds;
        buffer.RecordDecode(Mic, wallSeconds: limit + 0.5);
        for (var i = 0; i < StepHalves; i++) Assert.False(AppendHalfSecond(buffer));
        Assert.Null(buffer.TakePartial());
        buffer.RecordDecode(Mic, wallSeconds: limit - 0.5);
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

// Swift RealtimeCoverageTests: guards the cloud realtime coverage (RealtimeCoverage): stream time becomes file time by
// the session's offset, a disconnect and the audio after an undrained session's last final become gaps, a planned
// rotation's parallel overlap keeps one speaker when the new session labels the same voice differently, and an unplanned
// disconnect's scoped labels join only through the gap upload's padding (TranscriptionPipeline.JoinGap).
public sealed class RealtimeCoverageTests
{
    // (c) A session from 0 that sent through 10 s with its last final at 8 s and did not drain, then a reconnect at 15 s
    // that drained through 25 s, in a 30 s recording.
    [Fact]
    public void AReconnectLeavesTheDisconnectAndTheUnfinalizedAudioAsGaps()
    {
        var coverage = new RealtimeCoverage();
        var first = coverage.Open(0, bridged: false);
        coverage.Sent(first, 10);
        coverage.Final(first, new CloudSegment(1, 3, "one"));
        coverage.Final(first, new CloudSegment(4, 8, "two"));
        coverage.Close(first, drained: false);
        var second = coverage.Open(15, bridged: false);
        coverage.Sent(second, 25);
        var final = coverage.Final(second, new CloudSegment(2, 4, "three"));
        coverage.Close(second, drained: true);

        Assert.Equal((17.0, 19.0), (final.Start, final.End));
        Assert.Equal([new Span(8, 15), new Span(25, 30)], coverage.Gaps(30));
        Assert.Equal([1.0, 4, 17], coverage.Segments().Select(s => s.Start));
    }

    // (d) A rotation opens session 1 at 30 s while session 0 streams through 60 s; session 1 calls the voice "B", session
    // 0 "A": the joined finals and the live label after FreezeLabels are all "A", none repeated.
    [Fact]
    public void AParallelRotationKeepsOneSpeaker()
    {
        var coverage = new RealtimeCoverage();
        var old = coverage.Open(0, bridged: false);
        coverage.Sent(old, 60);
        for (var start = 0.0; start < 60; start += 2) coverage.Final(old, new CloudSegment(start, start + 2, "old", "A"));
        var next = coverage.Open(30, bridged: true);
        coverage.Sent(next, 90);
        for (var start = 0.0; start < 60; start += 2) coverage.Final(next, new CloudSegment(start, start + 2, "new", "B"));
        coverage.Close(old, drained: true);
        coverage.FreezeLabels(next);
        coverage.Close(next, drained: true);

        var segments = coverage.Segments();
        Assert.Equal(["A"], segments.Select(s => s.Speaker).Distinct());
        Assert.Equal(Enumerable.Range(0, 45).Select(i => i * 2.0), segments.Select(s => s.Start));
        Assert.Equal("A", coverage.LiveLabel(next, "B"));
        Assert.Empty(coverage.Gaps(90));
    }

    // (e) Two sessions around an unplanned disconnect both call their speaker "A": the second's labels are scoped (r1:A).
    // When the gap upload hears nobody in its padding the two stay two speakers; when it hears the same voice through the
    // left padding, the gap and the right padding, the second session's "A" joins the first's.
    [Fact]
    public void ScopedSessionLabelsJoinOnlyThroughTheGapsPadding()
    {
        var coverage = new RealtimeCoverage();
        foreach (var (offset, sent) in new[] { (0.0, 40.0), (60.0, 100.0) })
        {
            var session = coverage.Open(offset, bridged: false);
            coverage.Sent(session, sent);
            foreach (var final in Pieces("A", 0, 40)) coverage.Final(session, final);
            coverage.Close(session, drained: false);
        }
        var gap = new Span(40, 60);
        Assert.Equal([gap], coverage.Gaps(100));
        var stream = coverage.Segments().Select(s => new RawSegment(s.Start, s.End, s.Text, Speaker: s.Speaker)).ToList();
        Assert.Equal(["A", "r1:A"], Labels(stream));

        Assert.Equal(["A", "r1:A"], Labels(TranscriptionPipeline.JoinGap([], gap, new TimeRange(10, 90), stream, labels: true, partIndex: 1)));
        Assert.Equal(["A"], Labels(TranscriptionPipeline.JoinGap(
            Pieces("X", 10, 90), gap, new TimeRange(10, 90), stream, labels: true, partIndex: 1)));
    }

    // A gap upload's segment that straddles an edge of the gap (word-timed providers group up to ~15 s) is cut at its
    // sentence ends, each piece timed by its share of the text, and the pieces whose midpoint is in the gap are kept: the
    // speech inside the gap is not lost with the half outside it. A segment inside the gap stays whole; one in the padding
    // adds nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GapEdgeSegmentsKeepTheirSentencesInsideTheGap(bool labels)
    {
        List<CloudSegment> response =
        [
            new(12, 20, "Pad pad.", "X"),
            new(30, 50, "One one. Two two. Tri tri. For for.", "X"),
            new(50, 60, "Mid mid. Mid two.", "X"),
            new(65, 85, "Fiv fiv. Six six. Sev sev. Eig eig.", "X"),
            new(95, 100, "Pad two.", "X"),
        ];
        var joined = TranscriptionPipeline.JoinGap(response, new Span(41, 73), new TimeRange(11, 103), [], labels, partIndex: 1);

        Assert.Equal(["Tri tri.", "For for.", "Mid mid. Mid two.", "Fiv fiv.", "Six six."], joined.Select(s => s.Text));
        Assert.Equal(30 + 20.0 * 18 / 35, joined[0].Start, 2);
        Assert.Equal(30 + 20.0 * 26 / 35, joined[0].End, 2);
        Assert.Equal((50.0, 60.0), (joined[2].Start, joined[2].End));
        Assert.All(joined, s => Assert.Equal(labels ? joined[0].Speaker : null, s.Speaker));

        // A short gap inside one segment whose midpoint is in it: the segment stays whole (none of its sentences has its
        // midpoint in the gap). CJK sentence ends cut without a space.
        var shortGap = TranscriptionPipeline.JoinGap([response[1]], new Span(39.8, 41), new TimeRange(9.8, 71), [], labels, partIndex: 1);
        Assert.Equal(["One one. Two two. Tri tri. For for."], shortGap.Select(s => s.Text));
        var cjk = TranscriptionPipeline.SentencePieces(new CloudSegment(0, 6, "你好。我们开始吧！"), new Span(5, 9));
        Assert.Equal(["你好。", "我们开始吧！"], cjk.Select(s => s.Text));
    }

    private static List<CloudSegment> Pieces(string label, double from, double to)
    {
        var pieces = new List<CloudSegment>();
        for (var start = from; start < to; start += 2) pieces.Add(new CloudSegment(start, Math.Min(start + 2, to), "stream", label));
        return pieces;
    }

    private static List<string> Labels(IEnumerable<RawSegment> segments) =>
        [.. segments.Select(s => s.Speaker).OfType<string>().Distinct().Order(StringComparer.Ordinal)];
}

// Swift RealtimeRecoveryTests: guards the cloud realtime transcriber's recovery: once both channels stopped (Failed), a
// final is dropped; a channel that is live again (a "config" failure recovers at a language change) brings the state back
// to listening and its finals show.
public sealed class RealtimeRecoveryTests
{
    [Fact]
    public void BothChannelsFailedRecoverOnceOneIsLiveAgain()
    {
        var stream = new RealtimeLiveStream(
            "rt-model", RealtimePolicy.Of("speechmatics")!, (_, _, _) => throw new RealtimeLiveStream.Failure("config"));
        var transcriber = new RecordingTranscriber(stream, "cloud:fake", displayOnly: true, () => null);
        var snapshots = new List<RecordingTranscriber.Snapshot>();
        transcriber.Updated += snapshots.Add;
        const int system = RecordingSession.Channel.System;
        static RealtimeLiveStream.Update State(int channel, RealtimeLiveStream.State state) =>
            new RealtimeLiveStream.Update.StateChanged(channel, state);

        for (var channel = 0; channel < 2; channel++) transcriber.Apply(State(channel, new RealtimeLiveStream.State.Failed("config")));
        Assert.Equal(RecordingTranscriber.TranscriberState.Failed, snapshots[^1].State);

        transcriber.Apply(new RealtimeLiveStream.Update.Final(system, new RawSegment(0, 1, "dropped")));
        transcriber.Apply(State(system, new RealtimeLiveStream.State.Connecting()));
        transcriber.Apply(State(system, new RealtimeLiveStream.State.Live()));
        transcriber.Apply(new RealtimeLiveStream.Update.Final(system, new RawSegment(2, 3, "hello again")));
        var recovered = snapshots[^1];
        Assert.Equal(RecordingTranscriber.TranscriberState.Listening, recovered.State);
        Assert.Contains(recovered.Lines, l => l.Text.Contains("hello again", StringComparison.Ordinal));
        Assert.DoesNotContain(recovered.Lines, l => l.Text.Contains("dropped", StringComparison.Ordinal));
        transcriber.Cancel();
    }
}

// The realtime runner's ends without a socket: an adapter factory that refuses ("config") stops both channels, and
// FinishAsync still returns within its drain bound with the whole recording as gaps; Cancel while a connect hangs lets
// FinishAsync return.
public sealed class RealtimeLiveStreamEndTests
{
    private static readonly TimeSpan Bound = RealtimeLiveStream.DrainTimeout + TimeSpan.FromSeconds(3);

    [Fact]
    public async Task AFactoryFailureStopsBothChannelsAndFinishReturnsTheGaps()
    {
        var token = TestContext.Current.CancellationToken;
        var stream = new RealtimeLiveStream(
            "rt-model", RealtimePolicy.Of("deepgram")!, (_, _, _) => throw new RealtimeLiveStream.Failure("config"));
        stream.Start();
        stream.Append(Tone(), Tone(), 0, null);

        var failed = new HashSet<int>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Bound);
        await foreach (var update in stream.Updates.ReadAllAsync(timeout.Token))
        {
            if (update is RealtimeLiveStream.Update.StateChanged { State: RealtimeLiveStream.State.Failed { Kind: "config" } } state)
                failed.Add(state.Channel);
            if (failed.Count == 2) break;
        }
        var result = await stream.FinishAsync().WaitAsync(Bound, token);

        Assert.Equal([new Span(0, 1)], result.MicGaps);
        Assert.Equal([new Span(0, 1)], result.SystemGaps);
        Assert.Equal("rt-model", result.ModelId);
        Assert.Equal(1, result.Duration);
    }

    [Fact]
    public async Task CancelWhileAConnectHangsLetsFinishReturn()
    {
        var token = TestContext.Current.CancellationToken;
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new RealtimeLiveStream("rt-model", RealtimePolicy.Of("deepgram")!, async (_, _, cancellationToken) =>
        {
            connecting.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        stream.Start();
        stream.Append(Tone(), Tone(), 0, null);
        await connecting.Task.WaitAsync(Bound, token);

        stream.Cancel();

        await stream.FinishAsync().WaitAsync(Bound, token);
    }

    /// <summary>One second of a 440 Hz tone at 16 kHz (not silence, so no session covers it for free).</summary>
    private static float[] Tone() =>
        [.. Enumerable.Range(0, RecordingTranscriber.SampleRate).Select(i => (float)(0.5 * Math.Sin(i * 2 * Math.PI * 440 / 16_000)))];
}
