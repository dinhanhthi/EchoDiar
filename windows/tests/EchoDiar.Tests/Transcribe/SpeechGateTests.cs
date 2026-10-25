using EchoDiar.Transcribe.Engines;

namespace EchoDiar.Tests.Transcribe;

// The Whisper speech gate's segment rule, without loading Silero: a segment is kept only where it meets speech.
public sealed class SpeechGateTests
{
    // Guards the gate's purpose: a segment Whisper makes up outside speech is dropped, one within a frame of it is kept.
    [Theory]
    [InlineData(2.5, 3.5, true)] // inside
    [InlineData(4.2, 5.0, true)] // starts within the 0.256 s padding after the speech
    [InlineData(1.0, 1.75, true)] // ends within the padding before it
    [InlineData(4.3, 5.0, false)] // just outside
    [InlineData(double.NaN, 3.0, false)]
    public void KeepsOnlySegmentsThatMeetSpeech(double start, double end, bool kept)
    {
        List<(double Start, double End)> speech = [(2.0, 4.0)];

        Assert.Equal(kept, SpeechGate.HasSpeech(speech, start, end));
    }
}
