using EchoDiar.Core.Model;

namespace EchoDiar.Tests.Core;

// Guards the play head → active transcript line lookup (macOS PlaybackController.updateActiveSegment).
public sealed class TranscriptSegmentAtTests
{
    [Fact]
    public void FindsTheLastSegmentStartedWhileInsideIt()
    {
        Transcript.Segment[] segments =
        [
            new() { Start = 1, End = 2 },
            new() { Start = 3, End = 4 }, // equal starts: the last one wins
            new() { Start = 3, End = 5 },
            new() { Start = 6, End = 7 },
        ];

        Assert.Equal(-1, Transcript.SegmentIndexAt(segments, 0.5)); // before the first
        Assert.Equal(0, Transcript.SegmentIndexAt(segments, 1)); // exactly at a start
        Assert.Equal(0, Transcript.SegmentIndexAt(segments, 2)); // the end is inclusive
        Assert.Equal(-1, Transcript.SegmentIndexAt(segments, 2.5)); // a gap
        Assert.Equal(2, Transcript.SegmentIndexAt(segments, 3.5));
        Assert.Equal(-1, Transcript.SegmentIndexAt(segments, 8)); // after the last
        Assert.Equal(-1, Transcript.SegmentIndexAt([], 1));
    }
}
