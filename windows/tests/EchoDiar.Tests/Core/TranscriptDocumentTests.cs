using EchoDiar.Core.Model;

namespace EchoDiar.Tests.Core;

// Guards the copy of a selection across several messages in the transcript text view (macOS TranscriptDocumentTests).
public sealed class TranscriptDocumentTests
{
    [Fact]
    public void SelectionAcrossTwoBodiesCopiesOneLinePerMessageWithThePartsAndNames()
    {
        Transcript.Segment[] segments =
        [
            new() { Start = 5, End = 9, Channel = TranscriptChannel.Mic, Speaker = "me", Text = "Hello there everyone" },
            new() { Start = 72, End = 80, Channel = TranscriptChannel.System, Speaker = "s1", Text = "Good morning to you" },
        ];
        var names = new Dictionary<string, string> { ["me"] = "Thi", ["s1"] = "Alice" };
        var document = new TranscriptDocument(segments, s => names[s.Speaker]);

        // From "there…" in the first body to "…morning" in the second.
        var start = document.Entries[0].BodyRange.Start + 6;
        var range = new TranscriptDocument.TextRange(start, document.Entries[1].BodyRange.Start + 12 - start);

        Assert.Equal("Thi [00:05]: there everyone\nAlice [01:12]: Good morning", document.CopyText(range));
    }
}
