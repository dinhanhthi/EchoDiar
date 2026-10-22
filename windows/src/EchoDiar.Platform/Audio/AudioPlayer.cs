using EchoDiar.Core.Audio;
using EchoDiar.Core.Settings;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EchoDiar.Platform.Audio;

/// <summary>
/// Plays a recording's CAF file on the default output device through WASAPI shared mode (100 ms latency): both voices
/// in both ears (<see cref="PlaybackChannels.Mixed"/>), or as recorded, mic left and system right
/// (<see cref="PlaybackChannels.Split"/>), switchable while it plays. Create, control and dispose it on one thread;
/// <see cref="Ended"/> may be raised on another.
/// </summary>
/// <remarks>
/// NAudio 3's <c>WasapiPlayer</c>, the replacement for the obsolete <c>WasapiOut</c>. It never changes the sample
/// rate itself, so the 16 kHz file is resampled to the device's mix rate here.
/// </remarks>
public sealed class AudioPlayer : IDisposable
{
    private const int LatencyMilliseconds = 100;

    private readonly CafSampleProvider _source;
    private readonly WasapiPlayer _output;

    /// <summary>Opens <paramref name="cafPath"/> and the output device; throws when either cannot be opened.</summary>
    public AudioPlayer(string cafPath, PlaybackChannels channels)
    {
        _source = new CafSampleProvider(cafPath, channels);
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
    /// A CAF file (Int16 or Float32, mono or stereo) as stereo Float32 at the file's rate. Read on the playback thread,
    /// seeked and switched on the caller's: one lock guards the reader, the frame position and the channel mode.
    /// </summary>
    private sealed class CafSampleProvider : ISampleProvider, IDisposable
    {
        private const int OutputChannels = 2;

        private readonly object _lock = new();
        private readonly CafFile.Reader _reader;
        private bool _mixed;
        private long _frame;
        private bool _atEnd;
        private bool _disposed;

        public CafSampleProvider(string path, PlaybackChannels channels)
        {
            _reader = new CafFile.Reader(path);
            if (_reader.SampleRate is not (>= 1 and <= 384_000) || _reader.Channels < 1)
            {
                _reader.Dispose();
                throw new InvalidDataException($"{Path.GetFileName(path)} has an unsupported format.");
            }
            _mixed = channels == PlaybackChannels.Mixed;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat((int)_reader.SampleRate, OutputChannels);
            Duration = _reader.Frames / _reader.SampleRate;
        }

        public WaveFormat WaveFormat { get; }

        public double Duration { get; }

        public double Position
        {
            get
            {
                lock (_lock) return _frame / _reader.SampleRate;
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
                var frame = (long)(Math.Clamp(seconds, 0, Duration) * _reader.SampleRate);
                _frame = Math.Clamp(frame, 0, _reader.Frames);
                _atEnd = false;
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
                var channels = _reader.Channels;
                var samples = _reader.Read(_frame, buffer.Length / OutputChannels);
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
                _reader.Dispose();
            }
        }
    }
}
