using EchoDiar.Core.Audio;

namespace EchoDiar.Transcribe;

/// <summary>
/// 16 kHz mono Float32 channel files (Swift <c>MonoAudio</c>). Recordings are CAF; compressed ones (m4a) go through
/// an <see cref="IAudioDecoder"/> (Platform's Media Foundation decoder, passed in by the app).
/// </summary>
public static class MonoAudio
{
    public const double SampleRate = 16_000;
    /// <summary>Audio an engine reads into memory at once: 10 min of 16 kHz Float32 is about 38 MB.</summary>
    public const double SlabSeconds = 600;
    /// <summary>−60 dBFS: a channel whose peak stays below is silent and skips ASR.</summary>
    public const float SilencePeak = 0.001f;

    // Frames per CafFile.Reader.Read call, well under its 2^20 cap: every read below loops until done.
    private const int ChunkFrames = 16_384;

    /// <summary>Channels in the file; a non-CAF file needs <paramref name="decoder"/>.</summary>
    public static int ChannelCount(string path, IAudioDecoder? decoder = null)
    {
        if (!IsCaf(path)) return Unreadable(path, () => Decoder(path, decoder).ChannelCount(path));
        return Unreadable(path, () =>
        {
            using var reader = new CafFile.Reader(path);
            return reader.Channels;
        });
    }

    /// <summary>
    /// Writes <paramref name="channel"/> of <paramref name="source"/> to a 16 kHz mono Float32 CAF at
    /// <paramref name="destination"/>, in chunks; returns its peak. A CAF must already be 16 kHz (the recorder's
    /// rate); any other file needs <paramref name="decoder"/>.
    /// </summary>
    public static float Extract(int channel, string source, string destination, Func<string, int, float[]>? decoder = null)
    {
        if (!IsCaf(source))
        {
            if (decoder is null) throw NotSupportedYet(source);
            var samples = Unreadable(source, () => decoder(source, channel));
            using var mono = new CafFile.Writer(destination, SampleRate, 1, CafFile.SampleFormat.Float32);
            mono.Append(samples);
            return Peak(samples);
        }
        return Unreadable(source, () =>
        {
            using var reader = new CafFile.Reader(source);
            if (channel < 0 || channel >= reader.Channels)
            {
                throw new TranscriptionException(TranscriptionErrorKind.AudioUnreadable, $"{Path.GetFileName(source)} has no channel {channel}");
            }
            if (reader.SampleRate != SampleRate)
            {
                throw new TranscriptionException(
                    TranscriptionErrorKind.AudioUnreadable, $"unsupported sample rate {reader.SampleRate} Hz in {Path.GetFileName(source)}");
            }
            using var writer = new CafFile.Writer(destination, SampleRate, 1, CafFile.SampleFormat.Float32);
            var mono = new float[ChunkFrames];
            var peak = 0f;
            for (long frame = 0; frame < reader.Frames;)
            {
                var chunk = reader.Read(frame, ChunkFrames);
                var frames = chunk.Length / reader.Channels;
                if (frames == 0) break;
                for (var i = 0; i < frames; i++) mono[i] = chunk[i * reader.Channels + channel];
                peak = Math.Max(peak, Peak(mono.AsSpan(0, frames)));
                writer.Append(mono.AsSpan(0, frames));
                frame += frames;
            }
            return peak;
        });
    }

    /// <summary>Samples from <paramref name="start"/> to <paramref name="end"/> seconds (null = end of file) of a mono CAF.</summary>
    public static float[] Read(string path, double start, double? end)
    {
        RequireCaf(path);
        return Unreadable(path, () =>
        {
            using var reader = new CafFile.Reader(path);
            if (reader.Channels != 1)
            {
                throw new TranscriptionException(TranscriptionErrorKind.AudioUnreadable, $"{Path.GetFileName(path)} is not mono");
            }
            var first = Math.Min(reader.Frames, (long)(Math.Max(0, start) * reader.SampleRate));
            var last = Math.Min(reader.Frames, end is { } e ? (long)(e * reader.SampleRate) : reader.Frames);
            if (last <= first) return [];
            var samples = new float[last - first];
            long filled = 0;
            while (filled < samples.Length)
            {
                var chunk = reader.Read(first + filled, (int)Math.Min(ChunkFrames, samples.Length - filled));
                if (chunk.Length == 0) break;
                chunk.CopyTo(samples, filled);
                filled += chunk.Length;
            }
            return filled == samples.Length ? samples : samples[..(int)filled];
        });
    }

    /// <summary>Seconds of audio; a non-CAF file needs <paramref name="decoder"/>, which decodes it whole.</summary>
    public static double Duration(string path, IAudioDecoder? decoder = null)
    {
        if (!IsCaf(path)) return Unreadable(path, () => Decoder(path, decoder).Duration(path));
        return Unreadable(path, () =>
        {
            using var reader = new CafFile.Reader(path);
            return reader.Frames / reader.SampleRate;
        });
    }

    private static bool IsCaf(string path) => Path.GetExtension(path).Equals(".caf", StringComparison.OrdinalIgnoreCase);

    private static void RequireCaf(string path)
    {
        if (!IsCaf(path)) throw NotSupportedYet(path);
    }

    private static TranscriptionException NotSupportedYet(string path) =>
        new(TranscriptionErrorKind.AudioUnreadable, $"{Path.GetFileName(path)}: no decoder for this format");

    private static IAudioDecoder Decoder(string path, IAudioDecoder? decoder) => decoder ?? throw NotSupportedYet(path);

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    /// <summary>Runs <paramref name="read"/>, turning file and format errors into <see cref="TranscriptionErrorKind.AudioUnreadable"/>.</summary>
    private static T Unreadable<T>(string path, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            throw new TranscriptionException(TranscriptionErrorKind.AudioUnreadable, $"{Path.GetFileName(path)}: {e.Message}", e);
        }
    }
}
