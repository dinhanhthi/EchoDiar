namespace EchoDiar.Transcribe;

/// <summary>
/// Splits a 16 kHz mono stream into speech chunks for transcribe-while-recording, with an energy VAD over 20 ms
/// frames (Swift <c>SpeechChunker</c>). A chunk opens on speech (with pre-roll), closes at a pause (with post-roll)
/// and is cut at its quietest frame when it reaches <c>maxChunkSeconds</c>, so every chunk fits the model window. The
/// short pause and the length a chunk needs before the pause closes it are per instance (<c>shortPauseFrames</c>,
/// <c>shortPauseMinSeconds</c>): the batch gap fill keeps the defaults, the live path passes shorter ones. Silence
/// between chunks is never emitted: it counts as covered. The clock is the sample index of the stream, so chunk starts
/// are file time.
/// </summary>
/// <remarks>Pure and bounded: only the open chunk and the pre-roll are retained. Not thread-safe.</remarks>
public sealed class SpeechChunker
{
    /// <summary>Contiguous samples of the stream starting at <paramref name="StartSample"/>.</summary>
    /// <param name="Language">The language of the chunk's first frame (null = detect).</param>
    public sealed record Chunk(long StartSample, float[] Samples, string? Language);

    // Tuning (initial values, see the go/no-go measurement).

    public const int SampleRate = 16_000;
    public const int FrameSamples = 320; // 20 ms
    public const int FramesPerSecond = SampleRate / FrameSamples;
    /// <summary>Where the noise floor starts; it then follows the quietest frame.</summary>
    public const float InitialFloorDb = -70;
    /// <summary>How fast the noise floor climbs back after a quiet frame (it falls instantly).</summary>
    public const float FloorRiseDbPerSecond = 2;
    /// <summary>A frame is speech when its RMS is at least this far above the floor...</summary>
    public const float SpeechAboveFloorDb = 10;
    /// <summary>...and at least this loud.</summary>
    public const float SpeechMinDb = -55;
    /// <summary>The level of digital silence, so zeros do not drag the floor to minus infinity.</summary>
    public const float SilenceDb = -100;
    /// <summary>Consecutive speech frames that open a chunk.</summary>
    public const int OpenFrames = 2;
    public const int PreRollFrames = 15; // 300 ms
    public const int PostRollFrames = 10; // 200 ms
    /// <summary>The default pause that closes a chunk of at least the short-pause minimum.</summary>
    public const int ShortPauseFrames = 30; // 0.6 s
    /// <summary>A pause this long closes a chunk of any length.</summary>
    public const int LongPauseFrames = 75; // 1.5 s
    /// <summary>The default length a chunk needs before a short pause closes it, capped at half the window.</summary>
    public const int ShortPauseMinSeconds = 8;
    /// <summary>A chunk reaching the window is cut at its quietest frame in the last third.</summary>
    private const int CutSearchNumerator = 2, CutSearchDenominator = 3;

    private const float FloorRisePerFrame = FloorRiseDbPerSecond / FramesPerSecond;

    private readonly record struct Frame(float Db, bool IsSpeech, string? Language);

    private readonly int _maxFrames;
    private readonly int _shortPauseFrames;
    private readonly int _shortPauseMinFrames;

    private float _floorDb = InitialFloorDb;
    // Whole frames: the open chunk, or the pre-roll while none is open. _samples starts at _startSample.
    private readonly List<float> _samples = [];
    private readonly List<Frame> _frames = [];
    private long _startSample;
    private bool _isOpen;
    // Non-speech frames at the end of _frames.
    private int _trailingSilence;
    // The start of a frame not yet complete, with the language of its first sample.
    private readonly float[] _partial = new float[FrameSamples];
    private int _partialCount;
    private string? _partialLanguage;

    public SpeechChunker(
        int maxChunkSeconds, int shortPauseMinSeconds = ShortPauseMinSeconds, int shortPauseFrames = ShortPauseFrames)
    {
        _maxFrames = Math.Max(1, maxChunkSeconds) * FramesPerSecond;
        _shortPauseFrames = shortPauseFrames;
        _shortPauseMinFrames = Math.Min(shortPauseMinSeconds * FramesPerSecond, _maxFrames / 2);
    }

    /// <summary>Feeds the next samples of the stream; returns the chunks they closed.</summary>
    public List<Chunk> Append(ReadOnlySpan<float> samples, string? language)
    {
        var chunks = new List<Chunk>();
        var index = 0;
        while (index < samples.Length)
        {
            if (_partialCount == 0) _partialLanguage = language;
            var take = Math.Min(FrameSamples - _partialCount, samples.Length - index);
            samples.Slice(index, take).CopyTo(_partial.AsSpan(_partialCount));
            _partialCount += take;
            index += take;
            if (_partialCount == FrameSamples)
            {
                if (Process(_partial, _partialLanguage) is { } chunk) chunks.Add(chunk);
                _partialCount = 0;
            }
        }
        return chunks;
    }

    /// <summary>
    /// A copy of the open chunk so far (null when none is open): every retained sample, with its trailing silence and
    /// the incomplete frame, for the live transcriber's tentative text and its "others speaking now" check (Swift
    /// <c>openChunk</c>). Changes nothing, so the chunks emitted later are the same.
    /// </summary>
    public Chunk? OpenChunk()
    {
        if (!_isOpen) return null;
        var all = new float[_samples.Count + _partialCount];
        _samples.CopyTo(all);
        _partial.AsSpan(0, _partialCount).CopyTo(all.AsSpan(_samples.Count));
        return new Chunk(_startSample, all, _frames.Count > 0 ? _frames[0].Language : null);
    }

    /// <summary>
    /// The open chunk's start and length so far (<see cref="OpenChunk"/> without copying its samples; Swift
    /// <c>openSpan</c>), null when none is open.
    /// </summary>
    public (long StartSample, int Count)? OpenSpan() => _isOpen ? (_startSample, _samples.Count + _partialCount) : null;

    /// <summary>
    /// Emits the open chunk, trimmed to its post-roll (which may take in the incomplete frame), and drops the rest;
    /// the clock and the floor carry on, so the stream can continue (as after a pause).
    /// </summary>
    public List<Chunk> Flush()
    {
        var speechEnd = _frames.Count - _trailingSilence;
        Chunk? chunk = null;
        if (_isOpen && speechEnd > 0)
        {
            var end = Math.Min((speechEnd + PostRollFrames) * FrameSamples, _samples.Count + _partialCount);
            var all = new float[end];
            var fromSamples = Math.Min(end, _samples.Count);
            _samples.CopyTo(0, all, 0, fromSamples);
            _partial.AsSpan(0, end - fromSamples).CopyTo(all.AsSpan(fromSamples));
            chunk = new Chunk(_startSample, all, _frames[0].Language);
        }
        _startSample += _samples.Count + _partialCount;
        _samples.Clear();
        _frames.Clear();
        _partialCount = 0;
        _isOpen = false;
        _trailingSilence = 0;
        return chunk is null ? [] : [chunk];
    }

    // Frames

    private Chunk? Process(float[] frame, string? language)
    {
        var db = RmsDb(frame);
        var isSpeech = db >= Math.Max(_floorDb + SpeechAboveFloorDb, SpeechMinDb);
        _floorDb = Math.Min(db, _floorDb + FloorRisePerFrame);
        _samples.AddRange(frame);
        _frames.Add(new Frame(db, isSpeech, language));
        _trailingSilence = isSpeech ? 0 : _trailingSilence + 1;

        if (!_isOpen)
        {
            var openRun = 0;
            for (var i = Math.Max(0, _frames.Count - OpenFrames); i < _frames.Count; i++)
            {
                if (_frames[i].IsSpeech) openRun++;
            }
            if (openRun == OpenFrames)
            {
                _isOpen = true;
                DropFrames(_frames.Count - PreRollFrames - OpenFrames);
            }
            else
            {
                DropFrames(_frames.Count - PreRollFrames - OpenFrames + 1);
            }
            return null;
        }
        var speechEnd = _frames.Count - _trailingSilence;
        var end = Math.Min(speechEnd + PostRollFrames, _frames.Count);
        if (_trailingSilence >= LongPauseFrames
            || (_trailingSilence >= _shortPauseFrames && end >= _shortPauseMinFrames))
        {
            return Close(end, hasSpeech: speechEnd > 0);
        }
        if (_frames.Count >= _maxFrames) return Cut();
        return null;
    }

    /// <summary>Emits the first <paramref name="end"/> frames; the rest becomes the pre-roll of the next chunk.</summary>
    private Chunk? Close(int end, bool hasSpeech)
    {
        var chunk = hasSpeech ? TakeChunk(end) : null;
        if (!hasSpeech) DropFrames(end);
        _isOpen = false;
        DropFrames(_frames.Count - PreRollFrames - OpenFrames + 1);
        return chunk;
    }

    /// <summary>Emits the chunk up to its quietest frame in the last third of the window; the rest stays open.</summary>
    private Chunk Cut()
    {
        var lower = _maxFrames * CutSearchNumerator / CutSearchDenominator;
        var quietest = lower;
        for (var i = lower; i < _frames.Count; i++)
        {
            if (_frames[i].Db < _frames[quietest].Db) quietest = i;
        }
        var chunk = TakeChunk(quietest);
        _trailingSilence = 0;
        for (var i = _frames.Count - 1; i >= 0 && !_frames[i].IsSpeech; i--) _trailingSilence++;
        return chunk;
    }

    /// <summary>Removes and returns the first <paramref name="count"/> frames as a chunk.</summary>
    private Chunk TakeChunk(int count)
    {
        var chunk = new Chunk(_startSample, _samples.GetRange(0, count * FrameSamples).ToArray(), _frames[0].Language);
        DropFrames(count);
        return chunk;
    }

    private void DropFrames(int count)
    {
        if (count <= 0) return;
        _samples.RemoveRange(0, count * FrameSamples);
        _frames.RemoveRange(0, count);
        _startSample += count * FrameSamples;
    }

    private static float RmsDb(ReadOnlySpan<float> frame)
    {
        var sum = 0f;
        foreach (var sample in frame) sum += sample * sample;
        var rms = MathF.Sqrt(sum / frame.Length);
        return rms > 0 ? Math.Max(SilenceDb, 20 * MathF.Log10(rms)) : SilenceDb;
    }
}
