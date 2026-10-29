using EchoDiar.Core;
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

    private static List<RawSegment> Raw(params string[] texts) =>
        [.. texts.Select((text, i) => new RawSegment(i, i + 1, text))];
}
