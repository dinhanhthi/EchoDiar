using System.Buffers.Binary;
using EchoDiar.Core.Audio;

namespace EchoDiar.Tests.Core;

// CAF contract with macOS: audio.caf is 2-channel Int16 LE 16 kHz (ch0 mic, ch1 system).
public sealed class CafFileTests : IDisposable
{
    // Byte offsets fixed by the writer's layout: caff header (8), desc chunk header (12) + body (32), data chunk header (12).
    private const int DescFlagsOffset = 32;
    private const int DataSizeOffset = 56;
    private const int DescSizeOffset = 12;
    private const int SampleRateOffset = 20;
    private const int BytesPerPacketOffset = 36;
    private const int ChannelsOffset = 44;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));

    public CafFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTripKeepsFramesAndHeaderFields()
    {
        var path = Path.Combine(_dir, "audio.caf");
        short[] samples = [0, 1, -1, 32767, -32768, 1000, 16384, -16384];
        using (var writer = new CafFile.Writer(path, 16000, 2, CafFile.SampleFormat.Int16))
        {
            writer.Append(samples);
        }

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(DescFlagsOffset)));
        Assert.Equal(4 + samples.Length * 2, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(DataSizeOffset)));

        using (var reader = new CafFile.Reader(path))
        {
            Assert.Equal(16000, reader.SampleRate);
            Assert.Equal(2, reader.Channels);
            Assert.Equal(CafFile.SampleFormat.Int16, reader.Format);
            Assert.Equal(4, reader.Frames);
            Assert.Equal(samples.Select(s => s / 32768f).ToArray(), reader.Read(0, 4));
            Assert.Equal(samples[4..].Select(s => s / 32768f).ToArray(), reader.Read(2, 10));
        }

        var floatPath = Path.Combine(_dir, "float.caf");
        using (var writer = new CafFile.Writer(floatPath, 48000, 1, CafFile.SampleFormat.Float32))
        {
            writer.Append([0.5f, -0.25f]);
        }
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(File.ReadAllBytes(floatPath).AsSpan(DescFlagsOffset)));
        using (var reader = new CafFile.Reader(floatPath))
        {
            Assert.Equal(CafFile.SampleFormat.Float32, reader.Format);
            Assert.Equal([0.5f, -0.25f], reader.Read(0, 2));
        }
    }

    // Written on macOS by afconvert (-f caff -d LEI16@16000 -c 2), with the extra chunks Core Audio adds.
    [Fact]
    public void ReadsMacOSWrittenFile()
    {
        using var reader = new CafFile.Reader(Path.Combine(AppContext.BaseDirectory, "Fixtures", "macos-audio.caf"));

        Assert.Equal(16000, reader.SampleRate);
        Assert.Equal(2, reader.Channels);
        Assert.Equal(CafFile.SampleFormat.Int16, reader.Format);
        Assert.True(reader.Frames > 0);
    }

    [Fact]
    public void UnfinishedFileReportsWholeFramesOnly()
    {
        var path = Path.Combine(_dir, "audio.caf");
        var copy = Path.Combine(_dir, "crashed.caf");
        using (var writer = new CafFile.Writer(path, 16000, 2, CafFile.SampleFormat.Int16))
        {
            writer.Append(Enumerable.Range(0, 20).Select(i => (short)(i * 100)).ToArray());
            writer.Flush();

            // Copy while the data size is still -1, cut one byte into the last frame.
            byte[] bytes;
            using (var live = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bytes = new byte[live.Length];
                live.ReadExactly(bytes);
            }
            Assert.Equal(-1, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(DataSizeOffset)));
            File.WriteAllBytes(copy, bytes[..^1]);
        }

        using var reader = new CafFile.Reader(copy);
        Assert.Equal(9, reader.Frames);
        Assert.Equal(18, reader.Read(0, 100).Length);
    }

    [Theory]
    [InlineData("overflowing channels")]
    [InlineData("truncated desc")]
    [InlineData("huge chunk size")]
    [InlineData("NaN sample rate")]
    public void MalformedHeaderThrowsInvalidData(string defect)
    {
        var path = Path.Combine(_dir, "audio.caf");
        using (var writer = new CafFile.Writer(path, 16000, 2, CafFile.SampleFormat.Int16))
        {
            writer.Append(new short[8]);
        }
        var bytes = File.ReadAllBytes(path);
        switch (defect)
        {
            case "overflowing channels":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(ChannelsOffset), 0x80000000);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(BytesPerPacketOffset), 0);
                break;
            case "truncated desc":
                bytes = bytes[..(SampleRateOffset + 10)];
                break;
            case "huge chunk size":
                BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(DescSizeOffset), long.MaxValue);
                break;
            case "NaN sample rate":
                BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(SampleRateOffset), double.NaN);
                break;
        }
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => new CafFile.Reader(path));
    }
}
