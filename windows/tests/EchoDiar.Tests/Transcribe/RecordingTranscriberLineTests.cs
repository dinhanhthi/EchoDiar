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

    private static List<RawSegment> Raw(params string[] texts) =>
        [.. texts.Select((text, i) => new RawSegment(i, i + 1, text))];
}
