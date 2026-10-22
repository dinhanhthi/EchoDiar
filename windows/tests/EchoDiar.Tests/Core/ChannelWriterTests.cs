using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;

namespace EchoDiar.Tests.Core;

// The capture writer's core: ring blocks are placed in the 2-channel 16 kHz CAF by their QPC time (100 ns units).
public sealed class ChannelWriterTests : IDisposable
{
    private const long T0 = 1_000_000_000; // an arbitrary QPC time, in 100 ns units
    private const long TicksPerMs = 10_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));

    public ChannelWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void GapBetweenBlocksBecomesSilenceAtItsFrame()
    {
        var path = Path.Combine(_dir, "audio.caf");
        var writer = new ChannelWriter(path);
        var mic = new RingBuffer(16_000, 16_000);
        var system = new RingBuffer(16_000, 16_000);
        writer.Start(mic, system, T0);
        var block = Enumerable.Repeat(0.5f, 1_600).ToArray(); // 100 ms
        system.Write(block, T0);
        system.Write(block, T0 + 150 * TicksPerMs); // 50 ms after the first block ends
        writer.Stop();

        Assert.Null(writer.Stats.Failure);
        using var reader = new CafFile.Reader(path);
        Assert.Equal(4_000, reader.Frames);
        var samples = reader.Read(0, 4_000);
        float Ch(int channel, int frame) => samples[frame * 2 + channel];
        for (var frame = 0; frame < 4_000; frame++)
        {
            var expected = frame is >= 1_600 and < 2_400 ? 0f : 0.5f;
            Assert.Equal(expected, Ch(RecordingSession.Channel.System, frame), 0.001f);
            Assert.Equal(0f, Ch(RecordingSession.Channel.Mic, frame)); // the empty mic is padded
        }
    }

    [Fact]
    public void FortyEightKilohertzBlocksBecomeSixteenKilohertzFrames()
    {
        var writer = new ChannelWriter(Path.Combine(_dir, "audio.caf"));
        var mic = new RingBuffer(48_000, 48_000);
        var system = new RingBuffer(48_000, 48_000);
        writer.Start(mic, system, T0);
        const int blocks = 50;
        var block = Enumerable.Range(0, 480).Select(i => (float)Math.Sin(i * 2 * Math.PI * 440 / 48_000) * 0.5f).ToArray();
        for (var i = 0; i < blocks; i++)
        {
            mic.Write(block, T0 + i * 10 * TicksPerMs);
            system.Write(block, T0 + i * 10 * TicksPerMs);
        }
        writer.Stop();

        Assert.Null(writer.Stats.Failure);
        Assert.InRange(writer.Stats.FramesWritten, blocks * 160 - 1, blocks * 160 + 1);
    }

    // Guards the "No system audio" banner: a tap that never delivered sound is suspect, one that went quiet after
    // delivering sound (the call ended, the app still holds its output open) is not.
    [Theory]
    [InlineData(0, true)]
    [InlineData(16_000, false)]
    public void SilentTapIsSuspectedOnlyForZerosFromTheStart(int soundFrames, bool suspected)
    {
        var frames = soundFrames + 6 * 16_000;
        var writer = new ChannelWriter(Path.Combine(_dir, "audio.caf")) { IsTargetPlayingOutput = () => true };
        var mic = new RingBuffer(frames, 16_000);
        var system = new RingBuffer(frames, 16_000);
        writer.Start(mic, system, T0);
        var sound = Enumerable.Range(0, frames)
            .Select(i => i < soundFrames ? (float)Math.Sin(i * 2 * Math.PI * 440 / 16_000) * 0.5f : 0f).ToArray();
        mic.Write(sound, T0);
        system.Write(sound, T0);
        writer.Stop();

        Assert.Null(writer.Stats.Failure);
        Assert.Equal(suspected, writer.Stats.SilentTapSuspected);
    }
}
