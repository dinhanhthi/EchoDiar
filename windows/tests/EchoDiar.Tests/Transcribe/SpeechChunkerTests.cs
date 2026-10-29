using EchoDiar.Transcribe;

namespace EchoDiar.Tests.Transcribe;

// Swift SpeechChunkerTests: guards the chunk boundaries of transcribe-while-recording: chunks close at pauses, silence
// is never chunked, and speech longer than the model window is cut at its quietest frame into contiguous chunks.
// Signals are 16 kHz sines: speech at −20 dBFS with a 100 ms −45 dBFS dip each second (like the gaps between words, so
// the noise floor never climbs to the speech level), a −70 dBFS floor and a −65 dBFS cut marker. Fed in 1000-sample
// blocks (not a whole number of 20 ms frames), as the recording sink's blocks vary. Tolerances allow the 300 ms
// pre-roll and 200 ms post-roll.
public sealed class SpeechChunkerTests
{
    private const int Rate = 16_000;
    private const int Frame = 320;

    [Fact]
    public void APauseClosesTheChunkAndSilenceIsNotChunked()
    {
        float[] signal = [.. NoiseFloor(1), .. Speech(3), .. NoiseFloor(2), .. Speech(3), .. NoiseFloor(1)];
        var chunker = new SpeechChunker(maxChunkSeconds: 30);

        var closed = Feed(signal, chunker, offset => offset < 5 * Rate ? "en" : "fr");
        var flushed = Flush(chunker);

        Assert.Single(closed);
        Assert.Single(flushed);
        var (first, second) = (closed[0], flushed[0]);
        Assert.InRange(first.Start, At(1) - At(0.3) - 2 * Frame, At(1));
        Assert.InRange(first.End, At(4), At(4.2) + 2 * Frame);
        Assert.InRange(second.Start, At(6) - At(0.3) - 2 * Frame, At(6));
        Assert.InRange(second.End, At(9), signal.Length);
        Assert.Equal("en", first.Language);
        Assert.Equal("fr", second.Language);
        foreach (var chunk in closed.Concat(flushed))
        {
            Assert.Equal(signal[chunk.Start..chunk.End], chunk.Samples);
        }
    }

    [Fact]
    public void SpeechLongerThanTheWindowIsCutAtTheQuietestFrame()
    {
        float[] signal = [.. NoiseFloor(1), .. Speech(11), .. Dip(0.1), .. Speech(18.9)];
        var chunker = new SpeechChunker(maxChunkSeconds: 15);

        var closed = Feed(signal, chunker);
        var flushed = Flush(chunker);

        Assert.Equal(2, closed.Count);
        Assert.Single(flushed);
        var chunks = closed.Concat(flushed).ToList();
        Assert.InRange(chunks[0].End, At(12) - Frame, At(12.1) + Frame);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Samples.Length <= At(15));
            Assert.Equal(signal[chunk.Start..chunk.End], chunk.Samples);
        }
        foreach (var chunk in closed)
        {
            Assert.True(chunk.Samples.Length >= At(10) - Frame);
        }
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].End, chunks[i].Start);
        }
        Assert.Equal(signal.Length, chunks[^1].End);
    }

    // The live tuning bounds the time to a live line: a 0.6 s pause closes a chunk from 4 s (the batch tuning waits
    // for 8 s), and continuous speech is cut into contiguous chunks of at most 12 s, in the last third (8–12 s).
    [Fact]
    public void LiveChunksCloseAtAShortPauseFrom4SecondsAndNeverExceed12()
    {
        float[] paused = [.. NoiseFloor(1), .. Speech(5), .. NoiseFloor(0.7)];
        var live = new SpeechChunker(maxChunkSeconds: 12, shortPauseMinSeconds: 4);
        var batch = new SpeechChunker(maxChunkSeconds: 30);

        var closed = Feed(paused, live);

        var chunk = Assert.Single(closed);
        Assert.Empty(Feed(paused, batch));
        Assert.InRange(chunk.End, At(6), At(6.2) + 2 * Frame);
        Assert.Equal(paused[chunk.Start..chunk.End], chunk.Samples);

        float[] continuous = [.. NoiseFloor(1), .. Speech(20)];
        live = new SpeechChunker(maxChunkSeconds: 12, shortPauseMinSeconds: 4);
        var cut = Feed(continuous, live);
        var chunks = cut.Concat(Flush(live)).ToList();

        Assert.NotEmpty(cut);
        foreach (var piece in chunks)
        {
            Assert.True(piece.Samples.Length <= At(12));
            Assert.Equal(continuous[piece.Start..piece.End], piece.Samples);
        }
        foreach (var piece in cut)
        {
            Assert.True(piece.Samples.Length >= At(8) - Frame);
        }
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].End, chunks[i].Start);
        }
        Assert.Equal(continuous.Length, chunks[^1].End);
    }

    [Fact]
    public void FlushEmitsTheOpenChunk()
    {
        float[] signal = [.. NoiseFloor(0.5), .. Speech(2)];
        var chunker = new SpeechChunker(maxChunkSeconds: 30);

        Assert.Empty(Feed(signal, chunker));
        var flushed = Flush(chunker);

        Assert.Single(flushed);
        Assert.InRange(flushed[0].Start, At(0.5) - At(0.3) - 2 * Frame, At(0.5));
        Assert.Equal(signal.Length, flushed[0].End);
        Assert.Equal("en", flushed[0].Language);
    }

    // Guards the open-chunk peek the live transcriber reads ("others speaking now", tentative text): null while no
    // chunk is open, every sample fed since the chunk's start while one is, the start and a prefix of the chunk Flush
    // later emits, and pure, so a twin chunker that is never peeked emits the same chunks.
    [Fact]
    public void OpenChunkIsAPurePeekAtTheChunkFlushWillEmit()
    {
        float[][] parts = [NoiseFloor(1), [.. Speech(3), .. NoiseFloor(2), .. Speech(2)], NoiseFloor(0.3)];
        var chunker = new SpeechChunker(maxChunkSeconds: 30);
        var twin = new SpeechChunker(maxChunkSeconds: 30);

        var closed = Feed(parts[0], chunker);
        Assert.Null(chunker.OpenChunk());
        closed.AddRange(Feed(parts[1], chunker));
        var open = ToPiece(Assert.IsType<SpeechChunker.Chunk>(chunker.OpenChunk()));
        AssertSame([open], [ToPiece(chunker.OpenChunk()!)]);
        closed.AddRange(Feed(parts[2], chunker));
        var flushed = Flush(chunker);
        Assert.Null(chunker.OpenChunk());

        Assert.Single(closed);
        var last = Assert.Single(flushed);
        Assert.Equal(last.Start, open.Start);
        Assert.Equal(At(8), open.End);
        Assert.True(open.Samples.Length < last.Samples.Length);
        Assert.Equal(last.Samples[..open.Samples.Length], open.Samples);
        Assert.Equal("en", open.Language);
        AssertSame(closed, parts.SelectMany(part => Feed(part, twin)).ToList());
        AssertSame(flushed, Flush(twin));
    }

    // Helpers

    /// A chunk as sample range + content, so the tests do not depend on the chunk type's shape.
    private sealed record Piece(int Start, float[] Samples, string? Language)
    {
        public int End => Start + Samples.Length;
    }

    private static List<Piece> Feed(float[] signal, SpeechChunker chunker, Func<int, string?>? language = null)
    {
        language ??= _ => "en";
        var pieces = new List<Piece>();
        for (var offset = 0; offset < signal.Length; offset += 1_000)
        {
            var block = signal.AsSpan(offset, Math.Min(1_000, signal.Length - offset));
            pieces.AddRange(chunker.Append(block, language(offset)).Select(ToPiece));
        }
        return pieces;
    }

    private static List<Piece> Flush(SpeechChunker chunker) => chunker.Flush().Select(ToPiece).ToList();

    private static Piece ToPiece(SpeechChunker.Chunk chunk) => new((int)chunk.StartSample, chunk.Samples, chunk.Language);

    private static void AssertSame(List<Piece> expected, List<Piece> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal((expected[i].Start, expected[i].Language), (actual[i].Start, actual[i].Language));
            Assert.Equal(expected[i].Samples, actual[i].Samples);
        }
    }

    private static int At(double seconds) => (int)Math.Round(seconds * Rate, MidpointRounding.AwayFromZero);

    /// RMS −20 dBFS, starting each second with 100 ms at −45 dBFS.
    private static float[] Speech(double seconds) => Tone(seconds, i => i % Rate < At(0.1) ? 0.007_95f : 0.141f);

    /// RMS −70 dBFS.
    private static float[] NoiseFloor(double seconds) => Tone(seconds, _ => 0.000_447f);

    /// RMS −65 dBFS: the quietest stretch of a monologue, too short to close a chunk.
    private static float[] Dip(double seconds) => Tone(seconds, _ => 0.000_795f);

    private static float[] Tone(double seconds, Func<int, float> amplitude) =>
        Enumerable.Range(0, At(seconds)).Select(i => amplitude(i) * MathF.Sin(2 * MathF.PI * 220 * i / Rate)).ToArray();
}
