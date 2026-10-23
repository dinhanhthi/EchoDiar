using EchoDiar.AI.Cloud;
using EchoDiar.Core.Audio;
using EchoDiar.Platform.Audio;

namespace EchoDiar.Platform.Tests;

// Cloud transcription's upload (MediaFoundationAudio as IAudioExporter), on the real Media Foundation encoder and
// decoder (Windows only, CI job test-platform): one channel as mono AAC at the cloud bit rate, a part of it, from the
// CAF or from the m4a the compressor wrote, and the loudness the split looks at.
public sealed class CloudExportTests : IDisposable
{
    private const double Seconds = 3;
    private const int SampleRate = 16_000;
    private const double Tolerance = 0.05;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarPlatformTests-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly MediaFoundationAudio _audio = new();

    public CloudExportTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // The whole system channel of a CAF: mono, as long as the recording, at about 96 kbps.
    [Fact]
    public void ExportsOneChannelAsMonoAacAtTheCloudBitRate()
    {
        var caf = WriteCaf();
        var m4a = Path.Combine(_dir, "channel.m4a");

        var written = _audio.ExportMonoAac(caf, 1, null, m4a, TestContext.Current.CancellationToken);

        var decoded = _audio.Duration(m4a);
        var bytesPerSecond = new FileInfo(m4a).Length / decoded;
        _output.WriteLine($"written {written:0.0000} s, decodes to {decoded:0.0000} s, {bytesPerSecond:0} bytes/s");
        Assert.Equal(Seconds, written, 3);
        Assert.Equal(1, _audio.ChannelCount(m4a));
        Assert.InRange(decoded, Seconds - Tolerance, Seconds + Tolerance);
        // 96 kbps = 12,000 bytes per second, plus the container.
        Assert.InRange(bytesPerSecond, 9_000, 16_000);
    }

    // A split part: only the range is written.
    [Fact]
    public void ExportsOnlyTheRange()
    {
        var caf = WriteCaf();
        var m4a = Path.Combine(_dir, "part-1.m4a");

        var written = _audio.ExportMonoAac(caf, 1, new TimeRange(1, 2), m4a, TestContext.Current.CancellationToken);

        Assert.Equal(1, written, 3);
        Assert.InRange(_audio.Duration(m4a), 1 - Tolerance, 1 + Tolerance);
    }

    // The real path: the recording was compressed to a stereo m4a before it is transcribed.
    [Fact]
    public void ExportsFromTheCompressedM4a()
    {
        var stereo = Path.Combine(_dir, "audio.m4a");
        MediaFoundationAudio.EncodeAac(WriteCaf(), stereo, null, 96);
        var m4a = Path.Combine(_dir, "channel.m4a");

        var written = _audio.ExportMonoAac(stereo, 1, null, m4a, TestContext.Current.CancellationToken);
        var part = Path.Combine(_dir, "part.m4a");
        var partWritten = _audio.ExportMonoAac(stereo, 0, new TimeRange(0.5, 2), part, TestContext.Current.CancellationToken);

        _output.WriteLine($"from m4a: written {written:0.0000} s, decodes to {_audio.Duration(m4a):0.0000} s; part {partWritten:0.0000} s");
        Assert.Equal(1, _audio.ChannelCount(m4a));
        Assert.InRange(written, Seconds - Tolerance, Seconds + Tolerance);
        Assert.InRange(_audio.Duration(m4a), Seconds - Tolerance, Seconds + Tolerance);
        Assert.Equal(1.5, partWritten, 3);
        Assert.InRange(_audio.Duration(part), 1.5 - Tolerance, 1.5 + Tolerance);
    }

    // Levels: one RMS per 0.1 s slot of the channel, silence near 0, the tone well above (the split's quietest cut).
    [Fact]
    public void LevelsFollowTheChannelsLoudness()
    {
        var caf = WriteCaf();

        var levels = _audio.Levels(caf, 1, CloudTranscription.LevelWindow, TestContext.Current.CancellationToken);

        Assert.Equal(30, levels.Length);
        Assert.All(levels[..14], level => Assert.InRange(level, 0, 0.001));
        // A sine of amplitude 8000/32768 has an RMS of about 0.17.
        Assert.All(levels[16..], level => Assert.InRange(level, 0.15, 0.2));

        var stereo = Path.Combine(_dir, "audio.m4a");
        MediaFoundationAudio.EncodeAac(caf, stereo, null, 96);
        var decoded = _audio.Levels(stereo, 1, CloudTranscription.LevelWindow, TestContext.Current.CancellationToken);
        _output.WriteLine($"m4a levels: {decoded.Length} slots, {string.Join(" ", decoded.Select(l => l.ToString("0.000")))}");
        Assert.InRange(decoded.Length, 30, 31);
        Assert.True(decoded[5] < 0.01 && decoded[25] > 0.1);
    }

    // A cancelled upload stops the encode.
    [Fact]
    public void ACancelledExportThrows()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => _audio.ExportMonoAac(WriteCaf(), 1, null, Path.Combine(_dir, "channel.m4a"), cancelled.Token));
    }

    /// <summary>
    /// 3 s of 2-channel Int16 at 16 kHz, as the recorder writes: a 440 Hz tone on ch0 (mic); ch1 (system) silent for
    /// the first 1.5 s, then a 660 Hz tone.
    /// </summary>
    private string WriteCaf()
    {
        var path = Path.Combine(_dir, $"audio-{Guid.NewGuid():N}.caf");
        var frames = (int)(Seconds * SampleRate);
        var samples = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[2 * i] = (short)(8_000 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
            if (i >= frames / 2) samples[2 * i + 1] = (short)(8_000 * Math.Sin(2 * Math.PI * 660 * i / SampleRate));
        }
        using var writer = new CafFile.Writer(path, SampleRate, 2, CafFile.SampleFormat.Int16);
        writer.Append(samples);
        return path;
    }
}
