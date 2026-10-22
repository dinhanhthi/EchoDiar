using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace EchoDiar.Core.Audio;

/// <summary>
/// Core Audio Format (CAF) linear PCM files, the same layout macOS writes for audio.caf.
/// Header fields are big-endian (CAF spec); samples are little-endian (desc flags say so).
/// </summary>
public static class CafFile
{
    public enum SampleFormat { Int16, Float32 }

    // CAF format flags (not ASBD flags): kCAFLinearPCMFormatFlagIsFloat, kCAFLinearPCMFormatFlagIsLittleEndian.
    private const uint FlagIsFloat = 1;
    private const uint FlagIsLittleEndian = 2;

    private const int FileHeaderSize = 8;
    private const int ChunkHeaderSize = 12;
    private const int DescSize = 32;
    private const int EditCountSize = 4;

    // Sanity caps for hostile headers: keep frame sizes and sample rates in a range the math below can't overflow.
    private const uint MaxChannels = 64;
    private const double MaxSampleRate = 768_000;

    private static int BytesPerSample(SampleFormat format) => format == SampleFormat.Int16 ? 2 : 4;

    /// <summary>
    /// Streams interleaved samples to a CAF file. The data chunk size stays -1 (to end of file)
    /// until <see cref="Finish"/>, so a file cut off by a crash is still readable.
    /// </summary>
    public sealed class Writer : IDisposable
    {
        private readonly FileStream _stream;
        private readonly int _channels;
        private readonly SampleFormat _format;
        private readonly long _dataSizeOffset;
        private long _dataBytes;
        private bool _finished;

        public Writer(string path, double sampleRate, int channels, SampleFormat format)
        {
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("CafFile needs a little-endian CPU.");
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
            _channels = channels;
            _format = format;

            // FileShare.Read: recovery and live readers may open the file while it is recorded.
            _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);

            var bytesPerSample = BytesPerSample(format);
            Span<byte> header = stackalloc byte[FileHeaderSize + ChunkHeaderSize + DescSize + ChunkHeaderSize + EditCountSize];
            var h = header;
            Encoding.ASCII.GetBytes("caff", h);
            BinaryPrimitives.WriteUInt16BigEndian(h[4..], 1); // file version
            BinaryPrimitives.WriteUInt16BigEndian(h[6..], 0); // file flags
            h = h[FileHeaderSize..];

            Encoding.ASCII.GetBytes("desc", h);
            BinaryPrimitives.WriteInt64BigEndian(h[4..], DescSize);
            h = h[ChunkHeaderSize..];
            BinaryPrimitives.WriteDoubleBigEndian(h, sampleRate);
            Encoding.ASCII.GetBytes("lpcm", h[8..]);
            var flags = FlagIsLittleEndian | (format == SampleFormat.Float32 ? FlagIsFloat : 0);
            BinaryPrimitives.WriteUInt32BigEndian(h[12..], flags);
            BinaryPrimitives.WriteUInt32BigEndian(h[16..], (uint)(channels * bytesPerSample)); // bytes per packet
            BinaryPrimitives.WriteUInt32BigEndian(h[20..], 1); // frames per packet
            BinaryPrimitives.WriteUInt32BigEndian(h[24..], (uint)channels);
            BinaryPrimitives.WriteUInt32BigEndian(h[28..], (uint)(bytesPerSample * 8));
            h = h[DescSize..];

            Encoding.ASCII.GetBytes("data", h);
            BinaryPrimitives.WriteInt64BigEndian(h[4..], -1); // size unknown while streaming
            BinaryPrimitives.WriteUInt32BigEndian(h[12..], 0); // edit count

            _dataSizeOffset = FileHeaderSize + ChunkHeaderSize + DescSize + 4;
            _stream.Write(header);
        }

        /// <summary>Appends interleaved Int16 samples (a whole number of frames).</summary>
        public void Append(ReadOnlySpan<short> samples)
        {
            Check(SampleFormat.Int16, samples.Length);
            Write(MemoryMarshal.AsBytes(samples));
        }

        /// <summary>Appends interleaved Float32 samples (a whole number of frames).</summary>
        public void Append(ReadOnlySpan<float> samples)
        {
            Check(SampleFormat.Float32, samples.Length);
            Write(MemoryMarshal.AsBytes(samples));
        }

        /// <summary>Writes buffered samples through to disk.</summary>
        public void Flush() => _stream.Flush(flushToDisk: true);

        /// <summary>Patches the data chunk size. Further appends throw.</summary>
        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            Span<byte> size = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(size, EditCountSize + _dataBytes);
            _stream.Position = _dataSizeOffset;
            _stream.Write(size);
            _stream.Seek(0, SeekOrigin.End);
            Flush();
        }

        public void Dispose()
        {
            try { Finish(); }
            finally { _stream.Dispose(); }
        }

        private void Check(SampleFormat format, int sampleCount)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            if (format != _format) throw new InvalidOperationException($"This file holds {_format} samples.");
            if (sampleCount % _channels != 0) throw new ArgumentException("Samples must be whole interleaved frames.");
        }

        private void Write(ReadOnlySpan<byte> bytes)
        {
            _stream.Write(bytes);
            _dataBytes += bytes.Length;
        }
    }

    /// <summary>
    /// Reads linear PCM CAF files (Int16 or Float32, little-endian). Unknown chunks are skipped and a
    /// data size of -1 (or past the end of an unfinished file) means "to end of file".
    /// </summary>
    public sealed class Reader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly long _dataOffset;
        private readonly int _bytesPerFrame;

        // Largest frame count one Read returns, so the buffer size can't overflow.
        private const int MaxReadFrames = 1 << 20;

        public double SampleRate { get; }
        public int Channels { get; }
        public SampleFormat Format { get; }

        /// <summary>Whole frames in the data chunk; a trailing partial frame is ignored.</summary>
        public long Frames { get; }

        public Reader(string path)
        {
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("CafFile needs a little-endian CPU.");
            // FileShare.ReadWrite: the file may still be open in a Writer. FileShare.Delete: an open player never blocks
            // the compressor's delete or a move to the Recycle Bin.
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                var length = _stream.Length;
                if (length < FileHeaderSize) throw new InvalidDataException("Not a CAF file.");
                Span<byte> buffer = stackalloc byte[DescSize];
                _stream.ReadExactly(buffer[..FileHeaderSize]);
                if (!IsTag(buffer, "caff")) throw new InvalidDataException("Not a CAF file.");

                var haveDesc = false;
                long dataBytes = -1;
                while (_stream.Position + ChunkHeaderSize <= length)
                {
                    _stream.ReadExactly(buffer[..ChunkHeaderSize]);
                    var size = BinaryPrimitives.ReadInt64BigEndian(buffer[4..]);
                    var bodyStart = _stream.Position;
                    if (IsTag(buffer, "data"))
                    {
                        _dataOffset = bodyStart + EditCountSize;
                        var available = Math.Max(0, length - _dataOffset);
                        dataBytes = size < 0 ? available : Math.Min(size - EditCountSize, available);
                        break;
                    }
                    if (size < 0) throw new InvalidDataException("Only the data chunk may have an unknown size.");
                    if (size > length - bodyStart) throw new InvalidDataException("CAF chunk runs past the end of the file.");
                    if (IsTag(buffer, "desc"))
                    {
                        if (size < DescSize) throw new InvalidDataException("CAF desc chunk is too short.");
                        _stream.ReadExactly(buffer[..DescSize]);
                        (SampleRate, Channels, Format) = ParseDesc(buffer);
                        haveDesc = true;
                    }
                    _stream.Position = bodyStart + size;
                }

                if (!haveDesc) throw new InvalidDataException("CAF file has no desc chunk.");
                if (dataBytes < 0) throw new InvalidDataException("CAF file has no data chunk.");
                _bytesPerFrame = Channels * BytesPerSample(Format);
                Frames = dataBytes / _bytesPerFrame;
            }
            catch (EndOfStreamException e)
            {
                _stream.Dispose();
                throw new InvalidDataException("CAF file is truncated.", e);
            }
            catch
            {
                _stream.Dispose();
                throw;
            }
        }

        /// <summary>Reads up to <paramref name="count"/> frames (at most 2^20 per call) from <paramref name="startFrame"/> as interleaved floats (Int16 scaled by 1/32768).</summary>
        public float[] Read(long startFrame, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(startFrame);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            var frames = (int)Math.Clamp(Frames - startFrame, 0, Math.Min(count, MaxReadFrames));
            if (frames == 0) return [];
            var bytes = new byte[frames * _bytesPerFrame];
            _stream.Position = _dataOffset + startFrame * _bytesPerFrame;
            _stream.ReadExactly(bytes);

            if (Format == SampleFormat.Float32) return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
            var shorts = MemoryMarshal.Cast<byte, short>(bytes);
            var result = new float[shorts.Length];
            for (var i = 0; i < shorts.Length; i++) result[i] = shorts[i] / 32768f;
            return result;
        }

        public void Dispose() => _stream.Dispose();

        private static bool IsTag(ReadOnlySpan<byte> buffer, string tag) =>
            buffer[..4].SequenceEqual(Encoding.ASCII.GetBytes(tag));

        private static (double SampleRate, int Channels, SampleFormat Format) ParseDesc(ReadOnlySpan<byte> desc)
        {
            var sampleRate = BinaryPrimitives.ReadDoubleBigEndian(desc);
            var flags = BinaryPrimitives.ReadUInt32BigEndian(desc[12..]);
            var bytesPerPacket = BinaryPrimitives.ReadUInt32BigEndian(desc[16..]);
            var framesPerPacket = BinaryPrimitives.ReadUInt32BigEndian(desc[20..]);
            var channels = BinaryPrimitives.ReadUInt32BigEndian(desc[24..]);
            var bits = BinaryPrimitives.ReadUInt32BigEndian(desc[28..]);

            SampleFormat? format = (flags, bits) switch
            {
                (FlagIsLittleEndian, 16) => SampleFormat.Int16,
                (FlagIsLittleEndian | FlagIsFloat, 32) => SampleFormat.Float32,
                _ => null,
            };
            if (!double.IsFinite(sampleRate) || sampleRate <= 0 || sampleRate > MaxSampleRate)
            {
                throw new InvalidDataException("CAF sample rate is out of range.");
            }
            if (channels == 0 || channels > MaxChannels) throw new InvalidDataException("CAF channel count is out of range.");
            if (!IsTag(desc[8..], "lpcm") || format is null || framesPerPacket != 1
                || bytesPerPacket != (long)channels * (bits / 8))
            {
                throw new NotSupportedException("Only interleaved little-endian Int16 or Float32 linear PCM CAF files are supported.");
            }
            return (sampleRate, (int)channels, format.Value);
        }
    }
}
