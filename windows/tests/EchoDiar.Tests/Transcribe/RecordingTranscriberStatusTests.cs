using EchoDiar.Transcribe;
using State = EchoDiar.Transcribe.RecordingTranscriber.TranscriberState;

namespace EchoDiar.Tests.Transcribe;

// Swift RecordingTranscriberStatusTests: guards the "N s behind" notice of the live transcript: shown only while
// listening with at least BehindNoticeSeconds of speech waiting to be decoded, as rounded seconds (half away from
// zero, as Swift's rounded()).
public sealed class RecordingTranscriberStatusTests
{
    [Fact]
    public void BehindOnlyWhileListeningFromTheNoticeThreshold()
    {
        const double notice = RecordingTranscriber.BehindNoticeSeconds;

        Assert.Null(RecordingTranscriber.BehindSeconds(Snapshot(State.Listening, notice - 0.5)));
        Assert.Equal(
            RecordingTranscriber.BehindNoticeSeconds, RecordingTranscriber.BehindSeconds(Snapshot(State.Listening, notice)));
        Assert.Equal(43, RecordingTranscriber.BehindSeconds(Snapshot(State.Listening, 42.5)));
        Assert.Null(RecordingTranscriber.BehindSeconds(Snapshot(State.Paused, 60)));
        Assert.Null(RecordingTranscriber.BehindSeconds(Snapshot(State.Loading, 60)));
    }

    private static RecordingTranscriber.Snapshot Snapshot(State state, double pendingSeconds) =>
        new(state, [], pendingSeconds);
}
