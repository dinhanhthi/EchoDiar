namespace EchoDiar.Core.Audio;

/// <summary>
/// Single-producer single-consumer mono Float32 ring for audio capture threads.
/// Overflow drops the newest frames (the producer never moves the consumer's read index); <see cref="Overruns"/>
/// counts the dropped frames. Every written block is stamped with its QPC time in 100 ns units, and
/// <see cref="Read"/> never crosses a block boundary, so the consumer sees each block's timing and any gap.
/// </summary>
/// <remarks>SPSC contract: one thread calls <see cref="Write"/>, one thread calls <see cref="Read"/> and
/// <see cref="AvailableFrames"/>; shared state is published with volatile reads and writes only.</remarks>
public sealed class RingBuffer
{
    private struct Stamp
    {
        public long FrameIndex;
        public long Time;
    }

    private readonly int _frameMask;
    private readonly float[] _frames;
    private readonly int _stampCount;
    private readonly int _stampMask;
    private readonly Stamp[] _stamps;
    private readonly double _ticksPerFrame;

    private long _writeIndex;
    private long _readIndex;
    private long _stampHead;
    private long _stampTail;
    private long _droppedFrames;
    private int _peakBits;
    private int _rmsBits;

    public int Capacity { get; }

    /// <summary>The producer's frame rate.</summary>
    public double SampleRate { get; }

    /// <summary>
    /// <paramref name="capacityFrames"/> is rounded up to a power of two. <paramref name="sampleRate"/> is the
    /// producer's frame rate, used to give the time of a read that starts inside a block.
    /// </summary>
    public RingBuffer(int capacityFrames, double sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        Capacity = PowerOfTwo(capacityFrames);
        _frameMask = Capacity - 1;
        _frames = new float[Capacity];
        _stampCount = PowerOfTwo(Math.Max(256, Capacity / 64));
        _stampMask = _stampCount - 1;
        _stamps = new Stamp[_stampCount];
        SampleRate = sampleRate;
        _ticksPerFrame = 10_000_000 / sampleRate;
    }

    // Producer (capture thread)

    /// <summary>Writes one block captured at <paramref name="time"/> (QPC, 100 ns units).</summary>
    public void Write(ReadOnlySpan<float> samples, long time)
    {
        if (samples.IsEmpty) return;
        UpdateLevels(samples);

        var head = _writeIndex;
        var tail = Volatile.Read(ref _readIndex);
        var n = (int)Math.Min(samples.Length, Capacity - (head - tail));
        var stampSlot = _stampHead;
        var stampFull = stampSlot - Volatile.Read(ref _stampTail) >= _stampCount;
        if (n <= 0 || stampFull)
        {
            Interlocked.Add(ref _droppedFrames, samples.Length);
            return;
        }

        var start = (int)(head & _frameMask);
        var first = Math.Min(n, Capacity - start);
        samples[..first].CopyTo(_frames.AsSpan(start));
        if (first < n) samples[first..n].CopyTo(_frames);

        _stamps[stampSlot & _stampMask] = new Stamp { FrameIndex = head, Time = time };
        Volatile.Write(ref _stampHead, stampSlot + 1);
        Volatile.Write(ref _writeIndex, head + n);
        if (n < samples.Length) Interlocked.Add(ref _droppedFrames, samples.Length - n);
    }

    private void UpdateLevels(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        double sumSquares = 0;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
            sumSquares += (double)sample * sample;
        }
        var rms = (float)Math.Sqrt(sumSquares / samples.Length);
        // Non-negative float bit patterns order like their values, so an integer max works.
        if (float.IsFinite(peak)) Max(ref _peakBits, BitConverter.SingleToInt32Bits(peak));
        if (float.IsFinite(rms)) Max(ref _rmsBits, BitConverter.SingleToInt32Bits(rms));
    }

    private static void Max(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref location, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    // Consumer

    /// <summary>
    /// Copies up to <c>destination.Length</c> frames, stopping at the next block boundary. <paramref name="time"/> is
    /// the QPC time (100 ns units) of the first copied frame, 0 when nothing was read.
    /// </summary>
    public int Read(Span<float> destination, out long time)
    {
        time = 0;
        var tail = _readIndex;
        var available = Volatile.Read(ref _writeIndex) - tail;
        if (available <= 0 || destination.IsEmpty) return 0;

        var head = Volatile.Read(ref _stampHead);
        var current = _stampTail;
        while (current + 1 < head && _stamps[(current + 1) & _stampMask].FrameIndex <= tail) current++;
        Volatile.Write(ref _stampTail, current);

        var stamp = _stamps[current & _stampMask];
        var n = (int)Math.Min(destination.Length, available);
        if (current + 1 < head) n = (int)Math.Min(n, _stamps[(current + 1) & _stampMask].FrameIndex - tail);

        var start = (int)(tail & _frameMask);
        var first = Math.Min(n, Capacity - start);
        _frames.AsSpan(start, first).CopyTo(destination);
        if (first < n) _frames.AsSpan(0, n - first).CopyTo(destination[first..]);
        Volatile.Write(ref _readIndex, tail + n);

        time = stamp.Time + (long)Math.Round((tail - stamp.FrameIndex) * _ticksPerFrame);
        return n;
    }

    public int AvailableFrames => (int)(Volatile.Read(ref _writeIndex) - _readIndex);

    /// <summary>Total frames dropped because the ring was full.</summary>
    public long Overruns => Interlocked.Read(ref _droppedFrames);

    /// <summary>Highest block peak and block RMS since the previous call; safe from any thread.</summary>
    public (float Peak, float Rms) ReadLevels()
    {
        var peak = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));
        var rms = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _rmsBits, 0));
        return (peak, rms);
    }

    private static int PowerOfTwo(int value)
    {
        var result = 1;
        while (result < value) result <<= 1;
        return result;
    }
}
