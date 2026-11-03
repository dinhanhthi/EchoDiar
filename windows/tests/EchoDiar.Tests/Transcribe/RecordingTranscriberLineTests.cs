using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Transcribe;

namespace EchoDiar.Tests.Transcribe;

// Swift RecordingTranscriberLineTests: guards "one live line per chunk": the segments Whisper cuts a decoded chunk into
// are joined into one line (trimmed, single spaces, blank segments skipped), so the live transcript does not show a
// burst of short lines per chunk; a chunk with no text adds no line.
public sealed class RecordingTranscriberLineTests
{
    [Fact]
    public void AChunksSegmentsMakeOneTrimmedLine()
    {
        var segments = Raw(" parce qu'il y avait les deux", " \n", "il y avait les contraintes techniques ", "donc les clients");

        Assert.Equal(
            "parce qu'il y avait les deux il y avait les contraintes techniques donc les clients",
            RecordingTranscriber.LineText(segments));
    }

    [Fact]
    public void AChunkWithoutTextMakesNoLine()
    {
        Assert.Null(RecordingTranscriber.LineText([]));
        Assert.Null(RecordingTranscriber.LineText(Raw("", " ", "\n")));
    }

    // Guards the live Merci regression: a mic line that is a lone "Merci." while others speak is the residual of their
    // voices on a near-silent mic and is not shown; alone, on the system channel, or with more words it is kept, and
    // "others speak" is only asked for a mic lone Merci.
    [Fact]
    public void ALoneMicMerciWhileOthersSpeakIsNotShown()
    {
        var merci = Raw(" Merci. ");

        Assert.Null(RecordingTranscriber.LiveLine(merci, mic: true, othersSpeak: () => true));
        Assert.Equal("Merci.", RecordingTranscriber.LiveLine(merci, mic: true, othersSpeak: () => false));
        Assert.Equal("Merci.", RecordingTranscriber.LiveLine(merci, mic: false, othersSpeak: () => true));
        Assert.Equal("Merci beaucoup.", RecordingTranscriber.LiveLine(Raw("Merci beaucoup."), mic: true, othersSpeak: () => true));
        var asked = false;
        Assert.Equal("Bonjour.", RecordingTranscriber.LiveLine(Raw("Bonjour."), mic: true, othersSpeak: () => asked = true));
        Assert.False(asked);
    }

    // Guards the tentative text of the open chunks: at most one per speaker, ordered by start; a newer partial replaces
    // the speaker's text, a partial with no text removes it, and a final clears it only when it is the same chunk. An
    // unchanged list is the same array, so the snapshot does not change (Snapshot compares it by reference).
    [Fact]
    public void ATentativePerSpeakerIsReplacedThenClearedByItsFinal()
    {
        var me = new RecordingTranscriber.Tentative(RecordingTranscriber.Speaker.Me, "On commence la réunion", 48_000);
        var others = new RecordingTranscriber.Tentative(RecordingTranscriber.Speaker.Others, "Bonjour", 32_000);
        var tentative = RecordingTranscriber.WithTentative([], others.Speaker, "Bonjour", 32_000);
        tentative = RecordingTranscriber.WithTentative(tentative, me.Speaker, "On commence", 16_000);
        Assert.Equal([new RecordingTranscriber.Tentative(me.Speaker, "On commence", 16_000), others], tentative);
        tentative = RecordingTranscriber.WithTentative(tentative, me.Speaker, me.Text, 48_000);
        Assert.Equal([others, me], tentative);
        Assert.Same(tentative, RecordingTranscriber.WithTentative(tentative, me.Speaker, me.Text, 48_000));
        Assert.Equal([me], RecordingTranscriber.WithTentative(tentative, others.Speaker, null, 64_000));

        Assert.Same(tentative, RecordingTranscriber.WithoutTentative(tentative, me.Speaker, 16_000));
        Assert.Equal([others], RecordingTranscriber.WithoutTentative(tentative, me.Speaker, 48_000));
    }

    // Guards the catch-up's input: the finalized log keeps every line while the snapshot shows only the newest
    // MaxLines, each line starting where its chunk starts (file time), and FinalizedLines(afterId) gives the lines after
    // a line id, oldest first. Lines come from mic chunks cut by Flush, decoded by a fake session (no model).
    [Fact]
    public async Task TheLogKeepsEveryLineWhileTheSnapshotShowsTheNewest()
    {
        const int count = RecordingTranscriber.MaxLines + 1;
        const int cycle = RecordingTranscriber.SampleRate / 2;
        await using var transcriber = new RecordingTranscriber(
            ModelCatalog.Entry("parakeet-v3-int8")!, new FakeEngine(), new ModelLoadGate(), null, () => null);
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingTranscriber.Snapshot? last = null;
        transcriber.Updated += snapshot =>
        {
            last = snapshot;
            if (snapshot.State != RecordingTranscriber.TranscriberState.Loading) listening.TrySetResult();
        };
        transcriber.Start();
        await listening.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // Each cycle: 0.2 s of tone on the mic, 0.3 s of silence, then a flush closes the mic chunk (system stays silent).
        var block = new short[cycle * 2];
        for (var i = 0; i < cycle * 2 / 5; i++) block[i * 2] = (short)(Math.Sin(i * 2 * Math.PI * 440 / 16_000) * 16_000);
        for (var i = 0; i < count; i++)
        {
            transcriber.AppendInterleaved(block, cycle, 2);
            transcriber.Flush();
        }
        Assert.NotNull(await transcriber.FinishAsync());

        var lines = transcriber.FinalizedLines(null);
        Assert.Equal(Enumerable.Range(0, count), lines.Select(l => l.Id));
        Assert.All(lines, l => Assert.InRange(l.Start, l.Id * 0.5, l.Id * 0.5 + 0.2));
        Assert.Equal(Enumerable.Range(1, RecordingTranscriber.MaxLines), last!.Lines.Select(l => l.Id));
        Assert.Equal([count - 2, count - 1], transcriber.FinalizedLines(count - 3).Select(l => l.Id));
        Assert.Empty(transcriber.FinalizedLines(count - 1));
    }

    private sealed class FakeEngine : ISessionTranscriptionEngine
    {
        public Task<IReadOnlyList<RawSegment>> TranscribeAsync(
            string channelPath, IReadOnlyList<LanguageRange> languageRanges, Action<double> progress,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RawSegment>>([]);

        public Task<ITranscriptionSession> OpenSessionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ITranscriptionSession>(new FakeSession());
    }

    private sealed class FakeSession : ITranscriptionSession
    {
        public Task<IReadOnlyList<RawSegment>> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RawSegment>>([new RawSegment(0, samples.Length / 16_000.0, "line")]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static List<RawSegment> Raw(params string[] texts) =>
        [.. texts.Select((text, i) => new RawSegment(i, i + 1, text))];
}
