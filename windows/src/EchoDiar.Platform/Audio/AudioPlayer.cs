using EchoDiar.Core.Audio;
using EchoDiar.Core.Settings;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EchoDiar.Platform.Audio;

/// <summary>
/// Plays a recording's audio file (the recorder's CAF, or the compressor's m4a through Media Foundation) on the default
/// output device through WASAPI shared mode (100 ms latency): both voices in both ears
/// (<see cref="PlaybackChannels.Mixed"/>), or as recorded, mic left and system right (<see cref="PlaybackChannels.Split"/>),
/// switchable while it plays. A mono file plays in both ears either way. Create, control and dispose it on one thread;
/// <see cref="Ended"/> may be raised on another.
/// </summary>
/// <remarks>
/// NAudio 3's <c>WasapiPlayer</c>, the replacement for the obsolete <c>WasapiOut</c>. It never changes the sample
/// rate itself, so the file (16 kHz CAF, 48 kHz m4a) is resampled to the device's mix rate here.
/// </remarks>
public sealed class AudioPlayer : IDisposable
{
    private const int LatencyMilliseconds = 100;

    private readonly PlaybackSource _source;
    private readonly WasapiPlayer _output;

    /// <summary>Opens <paramref name="path"/> (<c>.caf</c> or <c>.m4a</c>) and the output device; throws when either cannot be opened.</summary>
    public AudioPlayer(string path, PlaybackChannels channels)
    {
        _source = Path.GetExtension(path).Equals(".caf", StringComparison.OrdinalIgnoreCase)
            ? new CafSampleProvider(path, channels)
            : new MediaFoundationSampleProvider(path, channels);
        WasapiPlayer? output = null;
        try
        {
            output = new WasapiPlayerBuilder().WithSharedMode().WithLatency(LatencyMilliseconds).Build();
            var deviceRate = output.DeviceMixFormat.SampleRate;
            ISampleProvider samples = deviceRate == _source.WaveFormat.SampleRate
                ? _source
                : new WdlResamplingSampleProvider(_source, deviceRate);
            output.Init(samples.ToWaveProvider());
        }
        catch
        {
            output?.Dispose();
            _source.Dispose();
            throw;
        }
        _output = output;
        _output.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>
    /// The end of the file was played, or the output device failed (the exception, e.g. headphones unplugged);
    /// playback has stopped. Raised on the thread NAudio picks (the creating thread's synchronization context, if any).
    /// </summary>
    public event Action<Exception?>? Ended;

    /// <summary>Seconds of audio in the file.</summary>
    public double Duration => _source.Duration;

    /// <summary>
    /// The play head in seconds: what was handed to the device, so it runs ahead of the speakers by up to the 100 ms
    /// buffer.
    /// </summary>
    public double Position => _source.Position;

    public bool IsPlaying => _output.PlaybackState == PlaybackState.Playing;

    /// <summary>Plays from the play head; from the start when the end was reached.</summary>
    public void Play()
    {
        if (_source.AtEnd) _source.Seek(0);
        _output.Play();
    }

    public void Pause()
    {
        if (_output.PlaybackState == PlaybackState.Playing) _output.Pause();
    }

    /// <summary>Moves the play head (clamped to the file); while playing, the buffered audio still plays first.</summary>
    public void Seek(double seconds) => _source.Seek(seconds);

    /// <summary>
    /// Switches between mixed and split from the next buffer read, keeping the play head and the state; while playing,
    /// the buffered audio (up to the 100 ms latency) still plays the old way.
    /// </summary>
    public void SetChannels(PlaybackChannels channels) => _source.SetChannels(channels);

    public void Dispose()
    {
        _output.PlaybackStopped -= OnPlaybackStopped;
        _output.Dispose();
        _source.Dispose();
    }

    // Also raised by Stop and Dispose, which are not an end.
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null || _source.AtEnd) Ended?.Invoke(e.Exception);
    }

    /// <summary>
    /// A file as stereo Float32 at the file's rate. Read on the playback thread, seeked and switched on the caller's: one
    /// lock guards the file, the frame position and the channel mode.
    /// </summary>
    private abstract class PlaybackSource : ISampleProvider, IDisposable
    {
        protected const int OutputChannels = 2;

        private readonly object _lock = new();
        private bool _mixed;
        private long _frame;
        private bool _atEnd;
        private bool _disposed;

        protected PlaybackSource(int sampleRate, PlaybackChannels channels)
        {
            _mixed = channels == PlaybackChannels.Mixed;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, OutputChannels);
        }

        public WaveFormat WaveFormat { get; }

        public abstract double Duration { get; }

        /// <summary>Frames in the file.</summary>
        protected abstract long Frames { get; }

        /// <summary>Channels in the file.</summary>
        protected abstract int Channels { get; }

        public double Position
        {
            get
            {
                lock (_lock) return (double)_frame / WaveFormat.SampleRate;
            }
        }

        /// <summary>The last read found no more frames.</summary>
        public bool AtEnd
        {
            get
            {
                lock (_lock) return _atEnd;
            }
        }

        public void Seek(double seconds)
        {
            lock (_lock)
            {
                var frame = (long)(Math.Clamp(seconds, 0, Duration) * WaveFormat.SampleRate);
                _frame = Math.Clamp(frame, 0, Frames);
                _atEnd = false;
                MoveTo(_frame);
            }
        }

        public void SetChannels(PlaybackChannels channels)
        {
            lock (_lock) _mixed = channels == PlaybackChannels.Mixed;
        }

        public int Read(Span<float> buffer)
        {
            lock (_lock)
            {
                if (_disposed || _atEnd) return 0;
                var channels = Channels;
                var samples = ReadFrames(_frame, buffer.Length / OutputChannels);
                var frames = samples.Length / channels;
                for (var i = 0; i < frames; i++)
                {
                    var left = samples[i * channels];
                    var right = channels > 1 ? samples[i * channels + 1] : left;
                    if (_mixed) left = right = (left + right) * 0.5f;
                    buffer[i * OutputChannels] = left;
                    buffer[i * OutputChannels + 1] = right;
                }
                _frame += frames;
                if (frames == 0) _atEnd = true;
                return frames * OutputChannels;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                Close();
            }
        }

        /// <summary>Up to <paramref name="maxFrames"/> interleaved frames from <paramref name="frame"/>; empty at the end. Under the lock.</summary>
        protected abstract ReadOnlySpan<float> ReadFrames(long frame, int maxFrames);

        /// <summary>The next <see cref="ReadFrames"/> starts at <paramref name="frame"/>. Under the lock.</summary>
        protected virtual void MoveTo(long frame)
        {
        }

        protected abstract void Close();
    }

    /// <summary>A CAF file (Int16 or Float32, mono or stereo).</summary>
    private sealed class CafSampleProvider : PlaybackSource
    {
        private readonly CafFile.Reader _reader;

        public CafSampleProvider(string path, PlaybackChannels channels)
            : this(Open(path), channels)
        {
        }

        private CafSampleProvider(CafFile.Reader reader, PlaybackChannels channels)
            : base((int)reader.SampleRate, channels)
        {
            _reader = reader;
            Duration = _reader.Frames / _reader.SampleRate;
        }

        public override double Duration { get; }

        protected override long Frames => _reader.Frames;

        protected override int Channels => _reader.Channels;

        protected override ReadOnlySpan<float> ReadFrames(long frame, int maxFrames) => _reader.Read(frame, maxFrames);

        protected override void Close() => _reader.Dispose();

        private static CafFile.Reader Open(string path)
        {
            var reader = new CafFile.Reader(path);
            if (reader.SampleRate is not (>= 1 and <= 384_000) || reader.Channels < 1)
            {
                reader.Dispose();
                throw new InvalidDataException($"{Path.GetFileName(path)} has an unsupported format.");
            }
            return reader;
        }
    }

    /// <summary>
    /// A file Media Foundation decodes (the compressor's AAC m4a, mono or stereo), as Float32. NAudio's reader opens its
    /// source reader again on the thread that reads, so it may be created on the UI thread.
    /// </summary>
    private sealed class MediaFoundationSampleProvider : PlaybackSource
    {
        private readonly MediaFoundationReader _reader;
        private readonly ISampleProvider _samples;
        private float[] _buffer = [];

        public MediaFoundationSampleProvider(string path, PlaybackChannels channels)
            : this(Open(path), channels)
        {
        }

        private MediaFoundationSampleProvider(MediaFoundationReader reader, PlaybackChannels channels)
            : base(reader.WaveFormat.SampleRate, channels)
        {
            _reader = reader;
            _samples = reader.ToSampleProvider();
            Channels = reader.WaveFormat.Channels;
            Duration = reader.TotalTime.TotalSeconds;
            Frames = (long)(Duration * reader.WaveFormat.SampleRate);
        }

        public override double Duration { get; }

        protected override long Frames { get; }

        protected override int Channels { get; }

        protected override ReadOnlySpan<float> ReadFrames(long frame, int maxFrames)
        {
            var count = maxFrames * Channels;
            if (_buffer.Length < count) _buffer = new float[count];
            var read = _samples.Read(_buffer.AsSpan(0, count));
            return _buffer.AsSpan(0, read - read % Channels);
        }

        protected override void MoveTo(long frame) => _reader.Position = frame * _reader.WaveFormat.BlockAlign;

        protected override void Close() => _reader.Dispose();

        private static MediaFoundationReader Open(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"{Path.GetFileName(path)} is missing", path);
            // Reference-counted by Media Foundation, so a second start next to MediaFoundationAudio's is harmless.
            MediaFoundationApi.Startup();
            var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
            if (reader.WaveFormat.SampleRate is not (>= 1 and <= 384_000) || reader.WaveFormat.Channels < 1)
            {
                reader.Dispose();
                throw new InvalidDataException($"{Path.GetFileName(path)} has an unsupported format.");
            }
            return reader;
        }
    }
}
