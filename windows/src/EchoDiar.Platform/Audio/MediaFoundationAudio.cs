using System.Runtime.InteropServices;
using EchoDiar.Core.Audio;
using NAudio.Dsp;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace EchoDiar.Platform.Audio;

/// <summary>Why <see cref="MediaFoundationAudio.EncodeAac"/> failed (Swift <c>AudioCompressorError</c>).</summary>
public enum AudioEncodeFailure
{
    /// <summary>The CAF is missing or unreadable, or has no such channel.</summary>
    SourceUnreadable,

    /// <summary>The AAC encoder refused the format or bitrate: the compressor falls back to two mono files.</summary>
    SettingRejected,

    /// <summary>Media Foundation failed while writing the m4a.</summary>
    Encode,
}

public sealed class AudioEncodeException(AudioEncodeFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AudioEncodeFailure Failure { get; } = failure;
}

/// <summary>
/// m4a through Media Foundation (NAudio's sink writer and source reader wrappers): AAC-LC encoding of a recording's
/// CAF, and decoding to 16 kHz mono for transcription. Media Foundation starts once, on first use, and stays up for
/// the process (playback's <c>MediaFoundationReader</c> shares it). The objects are free-threaded; call from a
/// background thread, as encoding and decoding take seconds.
/// </summary>
public sealed class MediaFoundationAudio : IAudioDecoder
{
    /// <summary>The m4a rate (decision 6): the Media Foundation AAC encoder accepts only 44.1 and 48 kHz.</summary>
    public const int EncodeSampleRate = 48_000;

    /// <summary>Total bitrates the AAC encoder offers at 48 kHz (12000/16000/20000/24000 bytes per second).</summary>
    public static readonly IReadOnlyList<int> BitratesKbps = [96, 128, 160, 192];

    private const int TargetRate = 16_000;
    private const int ChunkFrames = 16_384; // per CafFile.Reader.Read call, under its 2^20 cap
    private const int AacLcProfileLevel2 = 0x29; // MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION: AAC-LC, up to 48 kHz stereo
    private const int RawAacPayload = 0; // MF_MT_AAC_PAYLOAD_TYPE: raw AAC, as an MPEG-4 file holds it

    private static readonly Lock StartupLock = new();
    private static bool _started;

    /// <summary>
    /// Encodes <paramref name="cafPath"/> to an AAC-LC m4a at <paramref name="outPath"/>, 48 kHz. <paramref name="channel"/>
    /// null keeps every CAF channel (stereo); otherwise only that channel is written, as mono (0 mic, 1 system).
    /// <paramref name="kbps"/> (total) snaps to <see cref="BitratesKbps"/>. Throws <see cref="AudioEncodeException"/>;
    /// a partial output may be left behind on failure.
    /// </summary>
    public static void EncodeAac(string cafPath, string outPath, int? channel, int kbps)
    {
        CafFile.Reader caf;
        try
        {
            caf = new CafFile.Reader(cafPath);
        }
        catch (Exception e) when (IsSourceFailure(e))
        {
            throw new AudioEncodeException(AudioEncodeFailure.SourceUnreadable, $"{Path.GetFileName(cafPath)}: {e.Message}", e);
        }

        using (caf)
        {
            if (channel is { } c && (c < 0 || c >= caf.Channels))
            {
                throw new AudioEncodeException(AudioEncodeFailure.SourceUnreadable, $"{Path.GetFileName(cafPath)} has no channel {c}");
            }
            var channels = channel is null ? caf.Channels : 1;
            var bitrate = SnapKbps(kbps);
            try
            {
                EnsureStarted();
                using var outputType = AacType(channels, bitrate)
                    ?? throw new AudioEncodeException(
                        AudioEncodeFailure.SettingRejected, $"No AAC encoder output for {EncodeSampleRate} Hz, {channels} ch, {bitrate} kbps");
                using var encoder = new MediaFoundationEncoder(outputType);
                encoder.Encode(outPath, new CafPcm16Source(caf, channel));
            }
            catch (COMException e) when (e.HResult is MediaFoundationErrors.MF_E_INVALIDMEDIATYPE
                or MediaFoundationErrors.MF_E_TOPO_CODEC_NOT_FOUND or MediaFoundationErrors.MF_E_TRANSFORM_TYPE_NOT_SET)
            {
                throw new AudioEncodeException(AudioEncodeFailure.SettingRejected, e.Message, e);
            }
            catch (Exception e) when (IsSourceFailure(e))
            {
                throw new AudioEncodeException(AudioEncodeFailure.SourceUnreadable, $"{Path.GetFileName(cafPath)}: {e.Message}", e);
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
            {
                throw new AudioEncodeException(AudioEncodeFailure.Encode, e.Message, e);
            }
        }
    }

    /// <summary>
    /// Average bytes per second the AAC encoder offers for PCM at <paramref name="sampleRate"/> and
    /// <paramref name="channels"/>; empty when it rejects that input (16 kHz, decision 6).
    /// </summary>
    public static IReadOnlyList<int> AacBytesPerSecond(int sampleRate, int channels)
    {
        EnsureStarted();
        return MediaFoundationEncoder.GetEncodeBitrates(AudioSubtypes.MFAudioFormat_AAC, sampleRate, channels)
            .Select(bits => bits / 8).ToArray();
    }

    /// <summary>The nearest of <see cref="BitratesKbps"/>; a tie takes the higher one.</summary>
    public static int SnapKbps(int kbps) =>
        BitratesKbps.OrderBy(rate => Math.Abs((long)rate - kbps)).ThenByDescending(rate => rate).First();

    public int ChannelCount(string path) => Decoding(path, () =>
    {
        using var reader = Open(path);
        return reader.WaveFormat.Channels;
    });

    public float[] DecodeToMono16k(string path, int channel) => Decoding(path, () =>
    {
        using var reader = Open(path);
        var format = reader.WaveFormat;
        if (channel < 0 || channel >= format.Channels) throw new InvalidDataException($"{Path.GetFileName(path)} has no channel {channel}");
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        if (!isFloat && !(format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} decodes to {format.Encoding} {format.BitsPerSample}-bit");
        }

        var resampler = new Resampler(format.SampleRate, TargetRate, 1, ChunkFrames);
        var result = new List<float>((int)Math.Min(int.MaxValue / 2, reader.TotalTime.TotalSeconds * TargetRate) + TargetRate);
        var bytes = new byte[ChunkFrames * format.BlockAlign];
        var mono = new float[ChunkFrames];
        int read;
        while ((read = reader.Read(bytes, 0, bytes.Length)) > 0)
        {
            if (!reader.WaveFormat.Equals(format)) throw new InvalidDataException($"{Path.GetFileName(path)} changes format while decoding");
            var frames = read / format.BlockAlign;
            if (isFloat)
            {
                var samples = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, frames * format.BlockAlign));
                for (var i = 0; i < frames; i++) mono[i] = samples[i * format.Channels + channel];
            }
            else
            {
                var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, frames * format.BlockAlign));
                for (var i = 0; i < frames; i++) mono[i] = samples[i * format.Channels + channel] / 32768f;
            }
            result.AddRange(resampler.Process(mono.AsSpan(0, frames)));
        }
        result.AddRange(resampler.Flush());
        return result.ToArray();
    });

    private static void EnsureStarted()
    {
        lock (StartupLock)
        {
            if (_started) return;
            MediaFoundationApi.Startup();
            _started = true;
        }
    }

    /// <summary>The AAC-LC 48 kHz output type at <paramref name="kbps"/>, or null when the encoder does not offer it.</summary>
    private static MediaType? AacType(int channels, int kbps)
    {
        var types = MediaFoundationEncoder.GetOutputMediaTypes(AudioSubtypes.MFAudioFormat_AAC);
        var selected = types.FirstOrDefault(t =>
            t.SampleRate == EncodeSampleRate && t.ChannelCount == channels && t.AverageBytesPerSecond == kbps * 1000 / 8);
        foreach (var type in types)
        {
            if (!ReferenceEquals(type, selected)) type.Dispose();
        }
        if (selected is null) return null;
        try
        {
            selected.SetUInt32(MediaFoundationAttributes.MF_MT_AAC_PAYLOAD_TYPE, RawAacPayload);
            selected.SetUInt32(MediaFoundationAttributes.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, AacLcProfileLevel2);
            return selected;
        }
        catch
        {
            selected.Dispose();
            throw;
        }
    }

    /// <summary>A source reader with float output (PCM16 when the decoder refuses float).</summary>
    private static MediaFoundationReader Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{Path.GetFileName(path)} is missing", path);
        EnsureStarted();
        return new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
    }

    /// <summary>Runs <paramref name="decode"/>, turning Media Foundation errors into <see cref="InvalidDataException"/>.</summary>
    private static T Decoding<T>(string path, Func<T> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: {e.Message}", e);
        }
    }

    private static bool IsSourceFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException;

    /// <summary>
    /// Windowed-sinc resampling in <see cref="ChannelWriter"/>'s mode (linear phase, no IIR stages), fed in chunks of
    /// at most <c>maxInputFrames</c> interleaved frames.
    /// </summary>
    private sealed class Resampler
    {
        private readonly WdlResampler _wdl = new();
        private readonly int _channels;
        private readonly float[] _output;

        public Resampler(double from, double to, int channels, int maxInputFrames)
        {
            _channels = channels;
            _wdl.SetMode(false, 0, true);
            _wdl.SetFeedMode(true);
            _wdl.SetRates(from, to);
            _output = new float[((int)Math.Ceiling(maxInputFrames * to / from) + 256) * channels];
        }

        /// <summary>Resampled frames for <paramref name="input"/>; valid until the next call.</summary>
        public ReadOnlySpan<float> Process(ReadOnlySpan<float> input)
        {
            var frames = input.Length / _channels;
            _wdl.ResamplePrepare(frames, _channels, out var buffer);
            input[..(frames * _channels)].CopyTo(buffer);
            var produced = _wdl.ResampleOut(_output, frames, _output.Length / _channels, _channels);
            return _output.AsSpan(0, produced * _channels);
        }

        /// <summary>The filter's tail, once the input has ended.</summary>
        public ReadOnlySpan<float> Flush()
        {
            _wdl.ResamplePrepare(1, _channels, out _);
            var produced = _wdl.ResampleOut(_output, 0, _output.Length / _channels, _channels);
            return _output.AsSpan(0, produced * _channels);
        }
    }

    /// <summary>
    /// A CAF's channels (or one of them) as 48 kHz interleaved PCM16, read and resampled a chunk at a time, so a long
    /// recording never sits in memory. <see cref="Read"/> returns 0 only at the end.
    /// </summary>
    private sealed class CafPcm16Source : IWaveProvider
    {
        private readonly CafFile.Reader _caf;
        private readonly int? _channel;
        private readonly Resampler _resampler;
        private readonly float[] _picked = new float[ChunkFrames];
        private byte[] _pending = [];
        private int _offset;
        private int _count;
        private long _frame;
        private bool _ended;

        public CafPcm16Source(CafFile.Reader caf, int? channel)
        {
            _caf = caf;
            _channel = channel;
            var channels = channel is null ? caf.Channels : 1;
            _resampler = new Resampler(caf.SampleRate, EncodeSampleRate, channels, ChunkFrames);
            WaveFormat = new WaveFormat(EncodeSampleRate, 16, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<byte> buffer)
        {
            var written = 0;
            while (written < buffer.Length)
            {
                if (_count == 0 && !Fill()) break;
                var n = Math.Min(_count, buffer.Length - written);
                _pending.AsSpan(_offset, n).CopyTo(buffer[written..]);
                _offset += n;
                _count -= n;
                written += n;
            }
            return written;
        }

        /// <summary>Stages the next chunk's PCM16 bytes (possibly none); false once everything was staged.</summary>
        private bool Fill()
        {
            if (_ended) return false;
            var chunk = _frame < _caf.Frames ? _caf.Read(_frame, ChunkFrames) : [];
            if (chunk.Length == 0)
            {
                _ended = true;
                Stage(_resampler.Flush());
                return true;
            }

            var frames = chunk.Length / _caf.Channels;
            _frame += frames;
            ReadOnlySpan<float> input = chunk;
            if (_channel is { } c)
            {
                for (var i = 0; i < frames; i++) _picked[i] = chunk[i * _caf.Channels + c];
                input = _picked.AsSpan(0, frames);
            }
            Stage(_resampler.Process(input));
            return true;
        }

        private void Stage(ReadOnlySpan<float> samples)
        {
            if (_pending.Length < samples.Length * 2) _pending = new byte[samples.Length * 2];
            var pcm = MemoryMarshal.Cast<byte, short>(_pending.AsSpan(0, samples.Length * 2));
            for (var i = 0; i < samples.Length; i++) pcm[i] = (short)Math.Clamp(MathF.Round(samples[i] * 32768f), short.MinValue, short.MaxValue);
            _offset = 0;
            _count = samples.Length * 2;
        }
    }
}
