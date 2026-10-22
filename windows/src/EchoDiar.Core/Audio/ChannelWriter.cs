using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using EchoDiar.Core.Model;
using NAudio.Dsp;

namespace EchoDiar.Core.Audio;

/// <summary>A snapshot of <see cref="ChannelWriter"/>'s progress.</summary>
public sealed record ChannelWriterStats
{
    public long FramesWritten { get; init; }

    /// <summary>Continuous trailing duration under -60 dBFS.</summary>
    public double MicSilentSeconds { get; init; }

    public double SystemSilentSeconds { get; init; }

    /// <summary>
    /// System channel all zeros since the start, for 5 s at least, while <see cref="ChannelWriter.IsTargetPlayingOutput"/>
    /// reports output. Zeros after sound mean the target went quiet (a call ended with its output still open), not a
    /// blocked capture.
    /// </summary>
    public bool SilentTapSuspected { get; init; }

    /// <summary>First write-path error; writing stops after it and <see cref="ChannelWriter.Stop"/> rethrows it.</summary>
    public Exception? Failure { get; init; }

    public double Duration => FramesWritten / ChannelWriter.SampleRate;
}

/// <summary>
/// Drains the mic and system rings, aligns them by QPC time and writes a 2-channel Int16 16 kHz CAF
/// (ch0 = mic, ch1 = system, see <see cref="RecordingSession.Channel"/>).
/// </summary>
/// <remarks>
/// The writer has no timer: the owner calls <see cref="Drain"/> every <see cref="DrainInterval"/>. Every public
/// method takes one lock, so they may be called from any thread. Times are QPC in 100 ns units, the clock the
/// capture sources stamp their blocks with.
/// </remarks>
public sealed class ChannelWriter : IDisposable
{
    public enum Channel { Mic = RecordingSession.Channel.Mic, System = RecordingSession.Channel.System }

    /// <summary>Receives each newly written block: interleaved samples (ch0 mic, ch1 system), frame count, channel count.</summary>
    public delegate void LiveSinkHandler(ReadOnlySpan<short> interleaved, int frames, int channels);

    public const double SampleRate = 16_000;
    public const int ChannelCount = 2;
    public static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(200);

    private const double TicksPerSecond = 10_000_000;
    private const long SyncInterval = 15 * 10_000_000L;
    private const double JitterTolerance = 0.02 * SampleRate;
    private const double MaxLag = 1.0 * SampleRate;
    private const int SilenceWindow = 1_600;
    private const float SilenceThreshold = 0.001f;
    private const float ZeroThreshold = 1f / 32_768;
    private const int SilentTapFrames = 5 * 16_000;

    private enum State { Idle, Running, Paused, Stopped }

    private readonly object _gate = new();
    private CafFile.Writer? _file;
    private State _state = State.Idle;
    private Stream[] _streams = [];
    private LiveSinkHandler? _sink;
    private Func<bool>? _targetPlaying;
    private long _baseTime;
    private double _baseFrame;
    private long _framesWritten;
    private Exception? _failure;
    private long _lastSync;
    private short[] _interleaved = [];
    private volatile ChannelWriterStats _stats = new();

    /// <summary>Called with each newly written block, inside the writer's lock. Read at <see cref="Start"/>.</summary>
    public LiveSinkHandler? LiveSink { get; set; }

    /// <summary>Whether the captured app is currently producing output. Read at <see cref="Start"/>; called inside the writer's lock.</summary>
    public Func<bool>? IsTargetPlayingOutput { get; set; }

    public ChannelWriterStats Stats => _stats;

    /// <summary>Creates (or replaces) the CAF at <paramref name="path"/>.</summary>
    public ChannelWriter(string path)
    {
        _file = new CafFile.Writer(path, SampleRate, ChannelCount, CafFile.SampleFormat.Int16);
    }

    public void Dispose()
    {
        try { Stop(); }
        catch { /* Stop already recorded the failure in Stats. */ }
    }

    // Public API

    /// <summary>File time 0 is <paramref name="now"/>; blocks stamped before it are dropped as overlaps.</summary>
    public void Start(RingBuffer micRing, RingBuffer systemRing, long now)
    {
        lock (_gate)
        {
            if (_state != State.Idle) throw new InvalidOperationException("ChannelWriter was already started.");
            _streams = [new Stream(micRing), new Stream(systemRing)];
            _sink = LiveSink;
            _targetPlaying = IsTargetPlayingOutput;
            _lastSync = now;
            Rebase(now);
            _state = State.Running;
        }
    }

    /// <summary>Swaps in the ring of a rebuilt capture source; the old ring is drained first and the channel's resampler is recreated.</summary>
    public void ReplaceRing(Channel channel, RingBuffer ring)
    {
        lock (_gate)
        {
            if (_state is not (State.Running or State.Paused)) throw new InvalidOperationException("ChannelWriter is not recording.");
            var stream = _streams[(int)channel];
            if (_state == State.Running && _failure is null)
            {
                try
                {
                    Drain(stream);
                    stream.Convert(end: true);
                }
                catch (Exception e)
                {
                    Fail(e);
                }
            }
            else
            {
                stream.Discard();
            }
            stream.Configure(ring);
        }
    }

    /// <summary>Writes what was captured so far; afterwards the rings are drained and discarded until <see cref="Resume"/>.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_state != State.Running) return;
            FinishSegment();
            _state = State.Paused;
        }
    }

    /// <summary>File time continues from the paused position at <paramref name="now"/>; frames captured before it are discarded.</summary>
    public void Resume(long now)
    {
        lock (_gate)
        {
            if (_state != State.Paused) return;
            foreach (var stream in _streams) stream.Discard();
            Rebase(now);
            _state = State.Running;
        }
    }

    /// <summary>
    /// Moves the rings into the file. <paramref name="now"/> is the current QPC time: a channel more than 1 s behind it
    /// is padded with silence, and the file is flushed to disk every 15 s.
    /// </summary>
    public void Drain(long now)
    {
        lock (_gate)
        {
            if (_state != State.Running || _failure is not null)
            {
                foreach (var stream in _streams) stream.Discard();
                return;
            }
            try
            {
                foreach (var stream in _streams) Drain(stream);
                var deadline = OutputIndex(now) - MaxLag;
                foreach (var stream in _streams)
                {
                    if (stream.Position < deadline) stream.AppendSilence(deadline - stream.Position);
                }
                WriteStaged(padToLongest: false);
                if (now - _lastSync >= SyncInterval)
                {
                    _file?.Flush();
                    _lastSync = now;
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }
    }

    /// <summary>Final drain, flush and close. Safe to call repeatedly; rethrows the first write-path error.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_state == State.Running) FinishSegment();
            _state = State.Stopped;
            if (_file is not null)
            {
                try { _file.Dispose(); }
                catch (Exception e) { Fail(e); }
                _file = null;
            }
            if (_failure is not null) ExceptionDispatchInfo.Throw(_failure);
        }
    }

    // Inside the lock

    private void Rebase(long now)
    {
        _baseTime = now;
        _baseFrame = _framesWritten;
        foreach (var stream in _streams) stream.Position = _baseFrame;
    }

    /// <summary>Writes everything staged, padding the shorter channel, so the next segment starts aligned.</summary>
    private void FinishSegment()
    {
        if (_failure is not null) return;
        try
        {
            foreach (var stream in _streams)
            {
                Drain(stream);
                stream.Convert(end: true);
            }
            WriteStaged(padToLongest: true);
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private double OutputIndex(long time) => _baseFrame + (time - _baseTime) / TicksPerSecond * SampleRate;

    /// <summary>Places each ring block at the file position of its time: gaps become silence, overlaps are dropped.</summary>
    private void Drain(Stream stream)
    {
        var scratch = stream.Scratch;
        while (true)
        {
            var frames = stream.Ring.Read(scratch, out var time);
            if (frames == 0) break;
            var offset = OutputIndex(time) - stream.Position;
            var skip = 0;
            if (offset > JitterTolerance)
            {
                stream.AppendSilence(offset);
            }
            else if (offset < -JitterTolerance)
            {
                skip = Math.Min(frames, (int)Math.Round(-offset * stream.Ring.SampleRate / SampleRate));
            }
            if (skip < frames) stream.Append(scratch.AsSpan(skip, frames - skip));
        }
    }

    private void WriteStaged(bool padToLongest)
    {
        if (_file is null || _streams.Length == 0) return;
        var shortest = _streams.Min(s => s.Staged.Count);
        var longest = _streams.Max(s => s.Staged.Count);
        var frames = padToLongest ? longest : shortest;
        if (frames == 0) return;
        if (padToLongest)
        {
            foreach (var stream in _streams)
            {
                if (stream.Staged.Count < frames) stream.Staged.AddRange(Enumerable.Repeat(0f, frames - stream.Staged.Count));
            }
        }

        if (_interleaved.Length < frames * ChannelCount) _interleaved = new short[frames * ChannelCount];
        var output = _interleaved.AsSpan(0, frames * ChannelCount);
        for (var channel = 0; channel < _streams.Length; channel++)
        {
            var staged = CollectionsMarshal.AsSpan(_streams[channel].Staged);
            for (var i = 0; i < frames; i++)
            {
                output[i * ChannelCount + channel] = (short)Math.Clamp(MathF.Round(staged[i] * 32_767f), -32_768f, 32_767f);
            }
        }
        _file.Append(output);
        _sink?.Invoke(output, frames, ChannelCount);

        foreach (var stream in _streams)
        {
            stream.TrackSilence(frames);
            stream.Staged.RemoveRange(0, frames);
        }
        _framesWritten += frames;
        PublishStats();
    }

    private void PublishStats()
    {
        var mic = _streams[RecordingSession.Channel.Mic];
        var system = _streams[RecordingSession.Channel.System];
        var suspected = !system.Heard && system.ZeroFrames >= SilentTapFrames && (_targetPlaying?.Invoke() ?? false);
        _stats = _stats with
        {
            FramesWritten = _framesWritten,
            MicSilentSeconds = mic.SilentFrames / SampleRate,
            SystemSilentSeconds = system.SilentFrames / SampleRate,
            SilentTapSuspected = suspected,
        };
    }

    private void Fail(Exception error)
    {
        if (_failure is not null) return;
        _failure = error;
        _stats = _stats with { Failure = error };
    }

    /// <summary>One channel: its ring, a persistent resampler to 16 kHz, and resampled frames not yet written.</summary>
    private sealed class Stream
    {
        private const int ScratchFrames = 8_192;

        private WdlResampler _resampler = null!;
        private float[] _output = [];

        public float[] Scratch { get; } = new float[ScratchFrames];
        public RingBuffer Ring { get; private set; } = null!;

        /// <summary>File frame index of the next frame fed to the resampler.</summary>
        public double Position { get; set; }

        /// <summary>Resampled frames starting at the writer's frames written.</summary>
        public List<float> Staged { get; } = new((int)SampleRate);

        public int SilentFrames { get; private set; }
        public int ZeroFrames { get; private set; }

        /// <summary>A non-zero window was written: the source delivers audio.</summary>
        public bool Heard { get; private set; }

        public Stream(RingBuffer ring) => Configure(ring);

        public void Configure(RingBuffer ring)
        {
            Ring = ring;
            // Windowed sinc, no IIR stages: linear phase like AVAudioConverter, and a pass-through (to float rounding) at 16 kHz in.
            _resampler = new WdlResampler();
            _resampler.SetMode(false, 0, true);
            _resampler.SetFeedMode(true);
            _resampler.SetRates(ring.SampleRate, SampleRate);
            _output = new float[(int)Math.Ceiling(ScratchFrames * SampleRate / ring.SampleRate) + 64];
        }

        /// <summary>Reads and drops everything in the ring and resets the resampler.</summary>
        public void Discard()
        {
            while (Ring.Read(Scratch, out _) > 0) { }
            _resampler.Reset(0);
        }

        public void Append(ReadOnlySpan<float> samples)
        {
            for (var done = 0; done < samples.Length;)
            {
                var n = Math.Min(ScratchFrames, samples.Length - done);
                Feed(n, samples.Slice(done, n));
                done += n;
            }
            Position += samples.Length * SampleRate / Ring.SampleRate;
        }

        public void AppendSilence(double outputFrames)
        {
            var count = (int)Math.Round(outputFrames * Ring.SampleRate / SampleRate);
            for (var done = 0; done < count;)
            {
                var n = Math.Min(ScratchFrames, count - done);
                Feed(n, ReadOnlySpan<float>.Empty);
                done += n;
            }
            Position += count * SampleRate / Ring.SampleRate;
        }

        /// <summary>Feeds <paramref name="n"/> frames (zeros when <paramref name="samples"/> is empty) and stages the output.</summary>
        private void Feed(int n, ReadOnlySpan<float> samples)
        {
            _resampler.ResamplePrepare(n, 1, out var input);
            if (samples.IsEmpty) input[..n].Clear();
            else samples.CopyTo(input);
            var produced = _resampler.ResampleOut(_output, n, _output.Length, 1);
            Staged.AddRange(_output.AsSpan(0, produced));
        }

        /// <summary>Flushes the resampler's tail into <see cref="Staged"/> when <paramref name="end"/>, then resets it.</summary>
        public void Convert(bool end)
        {
            if (!end) return;
            _resampler.ResamplePrepare(1, 1, out _);
            var produced = _resampler.ResampleOut(_output, 0, _output.Length, 1);
            Staged.AddRange(_output.AsSpan(0, produced));
            _resampler.Reset(0);
        }

        /// <summary>Updates the silence runs with the first <paramref name="frames"/> staged frames, in 100 ms windows.</summary>
        public void TrackSilence(int frames)
        {
            var staged = CollectionsMarshal.AsSpan(Staged);
            for (var offset = 0; offset < frames;)
            {
                var n = Math.Min(SilenceWindow, frames - offset);
                float peak = 0;
                foreach (var sample in staged.Slice(offset, n)) peak = Math.Max(peak, Math.Abs(sample));
                SilentFrames = peak < SilenceThreshold ? SilentFrames + n : 0;
                ZeroFrames = peak < ZeroThreshold ? ZeroFrames + n : 0;
                Heard = Heard || peak >= ZeroThreshold;
                offset += n;
            }
        }
    }
}
