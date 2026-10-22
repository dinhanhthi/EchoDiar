using System.Runtime.InteropServices;
using System.Threading.Channels;
using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;

namespace EchoDiar.Transcribe;

/// <summary>
/// Transcribes a recording while it is made (Swift <c>RecordingTranscriber</c>): takes the writer's 16 kHz blocks,
/// cuts each channel into speech chunks (<see cref="SpeechChunker"/>) and decodes them with one Whisper or Parakeet
/// session (mic lines "Me", system lines "Others"). <see cref="FinishAsync"/> at Stop hands the segments, in file time,
/// and the spans not transcribed (gaps) to the pipeline as a <see cref="PriorTranscription"/>.
/// </summary>
/// <remarks>
/// <para>
/// Clock: the running sample count of each channel is file time. Every frame written to the CAF passes the sink,
/// silence padding included, and a pause adds no silence to the file. <see cref="Core.Audio.ChannelWriter.Pause"/> and
/// <see cref="Core.Audio.ChannelWriter.Stop"/> write the last block (and call the sink) inside their lock before they
/// return: call <see cref="Flush"/> after a pause and <see cref="FinishAsync"/> after the recording stopped.
/// </para>
/// <para>
/// Lossless while it keeps up: closed chunks wait in memory (at most <see cref="MaxQueuedSeconds"/> across both
/// channels). A chunk over the cap and a chunk the ASR failed on become gaps; a chunk whose peak is below
/// <see cref="MonoAudio.SilencePeak"/> is covered as silence without decoding.
/// </para>
/// <para>
/// Holds the <see cref="ModelLoadGate"/> as <see cref="ModelLoadGate.Owner.LiveTranscript"/> from load to unload, so
/// a batch job waits for <see cref="FinishAsync"/> or <see cref="Cancel"/>. The session lives only inside the decode
/// task. Thread-safe: <see cref="AppendInterleaved"/> runs on the writer's thread, the decode task on the thread pool,
/// the rest on any thread.
/// </para>
/// </remarks>
public sealed class RecordingTranscriber : IAsyncDisposable
{
    public enum TranscriberState { Loading, Listening, Paused, Failed }

    /// <summary>Who spoke a line: <c>ToString()</c> is the macOS label ("Me", "Others").</summary>
    public enum Speaker { Me, Others }

    public sealed record Line(int Id, Speaker Speaker, string Text);

    /// <param name="Lines">Oldest first, at most <see cref="MaxLines"/>; a new array whenever a line is added.</param>
    /// <param name="PendingSeconds">Seconds of speech waiting to be decoded, both channels summed.</param>
    /// <param name="Error">The error message when <paramref name="State"/> is <see cref="TranscriberState.Failed"/>.</param>
    public sealed record Snapshot(TranscriberState State, IReadOnlyList<Line> Lines, double PendingSeconds, string? Error = null);

    /// <summary>Chunks per outcome, both channels summed.</summary>
    public sealed record ChunkStats(int Transcribed, int Silence, int Dropped, int Failed, int Pending);

    public const int MaxLines = 200;
    public const int SampleRate = SpeechChunker.SampleRate;
    /// <summary>The most audio waiting to be decoded, both channels summed (~38 MB of Float).</summary>
    public const int MaxQueuedSeconds = 600;
    /// <summary>How often the decode task wakes without a chunk, to free the model after the idle release.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private readonly ModelEntry _entry;
    private readonly ISessionTranscriptionEngine _engine;
    private readonly ModelLoadGate _gate;
    private readonly TimeSpan? _idleRelease;
    private readonly Func<string?> _language;
    /// <summary>Filled by <see cref="AppendInterleaved"/> on the writer's thread, emptied by the decode task; locked on itself.</summary>
    private readonly Buffer _buffer;
    /// <summary>At most one pending wake-up: a chunk is waiting, the recording resumed, or the tick is due.</summary>
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _cancel = new();
    /// <summary>Guards the fields below and serializes <see cref="Updated"/>.</summary>
    private readonly Lock _lock = new();
    private TranscriberState _state = TranscriberState.Loading;
    private string? _error;
    private Line[] _lines = [];
    private int _nextLineID;
    private Snapshot? _published;
    private Task? _decoder;
    private bool _finished;
    /// <summary>No more <see cref="Updated"/>: <see cref="FinishAsync"/> drained the queue or <see cref="Cancel"/> ran.</summary>
    private bool _ended;

    /// <summary>
    /// <paramref name="entry"/> is the installed Whisper or Parakeet entry <paramref name="engine"/> loads
    /// (<see cref="TranscriptionPipeline.TranscribesChunks"/>). <paramref name="idleRelease"/> frees the model once no
    /// chunk came for that long (a paused recording) and loads it again for the next one; null keeps it until
    /// <see cref="FinishAsync"/>. <paramref name="language"/> gives the current marker language ("auto", a BCP-47
    /// code, or null = detect); it is called for every block on the writer's thread, so it must be cheap and thread-safe.
    /// </summary>
    public RecordingTranscriber(
        ModelEntry entry, ISessionTranscriptionEngine engine, ModelLoadGate gate, TimeSpan? idleRelease,
        Func<string?> language)
    {
        _entry = entry;
        _engine = engine;
        _gate = gate;
        _idleRelease = idleRelease;
        _language = language;
        _buffer = new Buffer(entry.WindowSeconds ?? TranscriptionPipeline.DefaultWindowSeconds);
    }

    /// <summary>
    /// Every change, raised inside the transcriber's lock (so in order) on the thread that made it: the decode task, or
    /// the caller of <see cref="Start"/> or <see cref="Flush"/>. Marshal to the UI; never wait there for another thread
    /// that calls into the transcriber. Ends with <see cref="FinishAsync"/> or <see cref="Cancel"/>.
    /// </summary>
    public event Action<Snapshot>? Updated;

    /// <summary>What <see cref="FinishAsync"/> left; null before it, or when the model never loaded or <see cref="Cancel"/> ran.</summary>
    public ChunkStats? Stats { get; private set; }

    /// <summary>Waits for the model gate on the thread pool, loads the model there and starts decoding.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_decoder is not null || _finished) return;
            PublishLocked(TranscriberState.Loading);
            _decoder = Task.Run(DecodeAsync);
        }
    }

    /// <summary>
    /// <see cref="Core.Audio.ChannelWriter.LiveSinkHandler"/>: splits the writer's interleaved Int16 block (ch0 mic,
    /// ch1 system) and chunks it, on the writer's thread. Never blocks on decoding and allocates only for closed chunks.
    /// </summary>
    public void AppendInterleaved(ReadOnlySpan<short> interleaved, int frames, int channels)
    {
        if (frames <= 0 || channels <= Math.Max(RecordingSession.Channel.Mic, RecordingSession.Channel.System)) return;
        var language = ChunkLanguage(_language());
        bool wake;
        lock (_buffer) wake = _buffer.Append(interleaved, frames, channels, language);
        if (wake) _wake.Writer.TryWrite(true);
    }

    /// <summary>On pause: closes the open chunk of each channel so it is decoded now.</summary>
    public void Flush()
    {
        lock (_buffer) _buffer.Flush(paused: true);
        _wake.Writer.TryWrite(true);
        PublishRunning();
    }

    /// <summary>
    /// At Stop: decodes what is left, unloads the model (releasing the gate) and returns what was transcribed; null
    /// when no chunk was, or the model never loaded (the pipeline then transcribes the whole recording).
    /// </summary>
    public async Task<PriorTranscription?> FinishAsync()
    {
        Task? decoder;
        lock (_lock)
        {
            if (_finished) return null;
            _finished = true;
            decoder = _decoder;
        }
        bool loaded;
        lock (_buffer) loaded = _buffer.Loaded;
        if (!loaded)
        {
            Cancel();
            return null;
        }
        lock (_buffer)
        {
            _buffer.Flush(paused: false);
            _buffer.Finishing = true;
        }
        _wake.Writer.TryWrite(true);
        _wake.Writer.TryComplete();
        if (decoder is not null) await decoder.ConfigureAwait(false);
        lock (_lock)
        {
            _decoder = null;
            _ended = true;
        }
        // Cancel() may have run while the queue drained.
        List<ChannelState> result;
        double duration;
        lock (_buffer)
        {
            if (_buffer.Closed) return null;
            result = _buffer.Channels;
            duration = (double)_buffer.SamplesSeen / SampleRate;
            _buffer.Close();
        }
        Stats = CountOutcomes(result);
        if (!result.Any(c => c.Chunks.Any(chunk => chunk.Outcome == PriorTranscription.ChunkOutcome.Transcribed))) return null;
        var mic = result[RecordingSession.Channel.Mic];
        var system = result[RecordingSession.Channel.System];
        return new PriorTranscription
        {
            EngineID = _entry.Id,
            Mic = [.. mic.Segments.OrderBy(s => s.Start)],
            System = [.. system.Segments.OrderBy(s => s.Start)],
            MicGaps = PriorTranscription.Gaps(mic.Chunks),
            SystemGaps = PriorTranscription.Gaps(system.Chunks),
            Duration = duration,
        };
    }

    /// <summary>Stops at once, keeping nothing; the decode task unloads the model (releasing the gate) on its way out.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _finished = true;
            _ended = true;
            _decoder = null;
        }
        lock (_buffer) _buffer.Close();
        _wake.Writer.TryComplete();
        _cancel.Cancel();
    }

    /// <summary><see cref="Cancel"/>, then waits until the model is unloaded and the gate released.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? decoder;
        lock (_lock) decoder = _decoder;
        Cancel();
        if (decoder is not null) await decoder.ConfigureAwait(false);
    }

    // Decode task

    /// <summary>Holds the model gate from load to unload; never throws.</summary>
    private async Task DecodeAsync()
    {
        var token = _cancel.Token;
        try
        {
            await _gate.WithExclusiveModelAsync(ModelLoadGate.Owner.LiveTranscript, async t =>
            {
                await RunAsync(t).ConfigureAwait(false);
                return true;
            }, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            lock (_buffer) _buffer.Fail();
            if (!token.IsCancellationRequested)
            {
                lock (_lock)
                {
                    _error = e.Message;
                    PublishLocked(TranscriberState.Failed);
                }
            }
        }
    }

    /// <summary>Owns the session from load to unload, and loads it again after an idle release.</summary>
    private async Task RunAsync(CancellationToken token)
    {
        var session = await _engine.OpenSessionAsync(token).ConfigureAwait(false);
        await using var model = new IdleModel<ITranscriptionSession>(
            session, _idleRelease, _engine.OpenSessionAsync, s => s.DisposeAsync());
        lock (_buffer) _buffer.Loaded = true;
        PublishRunning();
        using var tickCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tick = TickAsync(tickCancel.Token);
        try
        {
            await foreach (var _ in _wake.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await DrainAsync(model, token).ConfigureAwait(false);
                await model.ReleaseIfIdleAsync().ConfigureAwait(false);
                PublishRunning();
            }
            await DrainAsync(model, token).ConfigureAwait(false);
        }
        finally
        {
            tickCancel.Cancel();
            await tick.ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) _wake.Writer.TryWrite(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Decodes the waiting chunks in order. A failed load ends the decode task; a failed chunk becomes a gap.</summary>
    private async Task DrainAsync(IdleModel<ITranscriptionSession> model, CancellationToken token)
    {
        while (true)
        {
            Item? item;
            lock (_buffer) item = _buffer.Take();
            if (item is null) return;
            var segments = await model.UseAsync<IReadOnlyList<RawSegment>?>(async (session, t) =>
            {
                try
                {
                    return await session.TranscribeAsync(item.Chunk.Samples, item.Chunk.Language, t).ConfigureAwait(false);
                }
                catch (Exception) when (!t.IsCancellationRequested)
                {
                    return null;
                }
            }, token).ConfigureAwait(false);
            var offset = (double)item.Chunk.StartSample / SampleRate;
            var shifted = segments?.Select(s => s with { Start = s.Start + offset, End = s.End + offset }).ToList();
            lock (_buffer) _buffer.Complete(item, shifted);
            if (shifted is null) continue;
            var speaker = item.Channel == RecordingSession.Channel.Mic ? Speaker.Me : Speaker.Others;
            AddLines(speaker, shifted);
        }
    }

    // State

    /// <summary>Raises <see cref="Updated"/> when the snapshot changed; call inside <see cref="_lock"/>.</summary>
    private void PublishLocked(TranscriberState state)
    {
        if (_ended) return;
        _state = state;
        double pending;
        lock (_buffer) pending = _buffer.PendingSeconds;
        var snapshot = new Snapshot(_state, _lines, pending, _state == TranscriberState.Failed ? _error : null);
        if (snapshot == _published) return;
        _published = snapshot;
        Updated?.Invoke(snapshot);
    }

    /// <summary>Listening or paused once loaded; leaves loading (before the load) and failed alone.</summary>
    private void PublishRunning()
    {
        lock (_lock)
        {
            bool loaded, paused;
            lock (_buffer)
            {
                loaded = _buffer.Loaded;
                paused = _buffer.Paused;
            }
            if ((_state == TranscriberState.Loading && !loaded) || _state == TranscriberState.Failed) return;
            PublishLocked(paused ? TranscriberState.Paused : TranscriberState.Listening);
        }
    }

    private void AddLines(Speaker speaker, List<RawSegment> segments)
    {
        lock (_lock)
        {
            var lines = new List<Line>(_lines);
            foreach (var segment in segments) lines.Add(new Line(_nextLineID++, speaker, segment.Text));
            _lines = [.. lines.Skip(Math.Max(0, lines.Count - MaxLines))];
        }
        PublishRunning();
    }

    // Helpers

    private static ChunkStats CountOutcomes(List<ChannelState> channels)
    {
        var outcomes = channels.SelectMany(c => c.Chunks).Select(c => c.Outcome).ToList();
        int Count(PriorTranscription.ChunkOutcome outcome) => outcomes.Count(o => o == outcome);
        return new ChunkStats(
            Count(PriorTranscription.ChunkOutcome.Transcribed), Count(PriorTranscription.ChunkOutcome.Silence),
            Count(PriorTranscription.ChunkOutcome.Dropped), Count(PriorTranscription.ChunkOutcome.Failed),
            Count(PriorTranscription.ChunkOutcome.Pending));
    }

    /// <summary>null (detect) for an empty or "auto" marker, else the trimmed code.</summary>
    private static string? ChunkLanguage(string? language)
    {
        var trimmed = language?.Trim() ?? "";
        return trimmed.Length == 0 || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    /// <summary>A chunk waiting to be decoded, and where its outcome is recorded.</summary>
    /// <param name="Channel">A <see cref="RecordingSession.Channel"/> index.</param>
    /// <param name="Index">The chunk's index in <see cref="ChannelState.Chunks"/>.</param>
    public sealed record Item(int Channel, int Index, SpeechChunker.Chunk Chunk);

    /// <summary>One channel's chunker, chunk outcomes (file order) and decoded segments (file time).</summary>
    public sealed class ChannelState(int maxChunkSeconds)
    {
        public SpeechChunker Chunker { get; } = new(maxChunkSeconds);
        public List<(Span Span, PriorTranscription.ChunkOutcome Outcome)> Chunks { get; } = [];
        public List<RawSegment> Segments { get; } = [];
    }

    /// <summary>
    /// The transcriber's bookkeeping, public for its tests. Not thread-safe (the transcriber locks it). Both channels
    /// grow in step (the writer writes equal frames per channel); index = <see cref="RecordingSession.Channel"/>.
    /// </summary>
    public sealed class Buffer(int maxChunkSeconds, int maxQueuedSamples = MaxQueuedSeconds * SampleRate)
    {
        private readonly Queue<Item> _queue = new();
        private int _queuedSamples;
        private bool _failed;
        /// <summary>One channel at a time, reused across blocks (the chunker copies what it keeps).</summary>
        private float[] _scratch = [];

        public List<ChannelState> Channels { get; private set; } = [new(maxChunkSeconds), new(maxChunkSeconds)];
        public long SamplesSeen { get; private set; }
        /// <summary>The session loaded once; until then <see cref="FinishAsync"/> has nothing to wait for.</summary>
        public bool Loaded { get; set; }
        /// <summary>Between <see cref="Flush"/> and the next block.</summary>
        public bool Paused { get; private set; }
        /// <summary><see cref="FinishAsync"/> flushed the chunkers: no more blocks.</summary>
        public bool Finishing { get; set; }
        public bool Closed { get; private set; }

        public double PendingSeconds => (double)_queuedSamples / SampleRate;

        /// <summary>Splits an interleaved Int16 block per channel and chunks it; true when a chunk was queued or the recording resumed.</summary>
        public bool Append(ReadOnlySpan<short> interleaved, int frames, int channels, string? language)
        {
            if (Closed || Finishing) return false;
            var resumed = Paused;
            Paused = false;
            SamplesSeen += frames;
            if (_scratch.Length < frames) _scratch = new float[frames];
            var micQueued = ChunkChannel(interleaved, frames, channels, RecordingSession.Channel.Mic, language);
            var systemQueued = ChunkChannel(interleaved, frames, channels, RecordingSession.Channel.System, language);
            return resumed || micQueued || systemQueued;
        }

        public void Flush(bool paused)
        {
            if (Closed || Finishing) return;
            Paused = paused;
            for (var channel = 0; channel < Channels.Count; channel++) Enqueue(Channels[channel].Chunker.Flush(), channel);
        }

        /// <summary>Records each chunk: silence, dropped over the cap, failed after a decoder error, else queued (pending).</summary>
        public bool Enqueue(IEnumerable<SpeechChunker.Chunk> chunks, int channel)
        {
            var queued = false;
            foreach (var chunk in chunks)
            {
                var span = new Span((double)chunk.StartSample / SampleRate, (double)(chunk.StartSample + chunk.Samples.Length) / SampleRate);
                PriorTranscription.ChunkOutcome outcome;
                if (Peak(chunk.Samples) < MonoAudio.SilencePeak)
                {
                    outcome = PriorTranscription.ChunkOutcome.Silence;
                }
                else if (_failed)
                {
                    outcome = PriorTranscription.ChunkOutcome.Failed;
                }
                else if (_queuedSamples + chunk.Samples.Length > maxQueuedSamples)
                {
                    outcome = PriorTranscription.ChunkOutcome.Dropped;
                }
                else
                {
                    outcome = PriorTranscription.ChunkOutcome.Pending;
                    _queue.Enqueue(new Item(channel, Channels[channel].Chunks.Count, chunk));
                    _queuedSamples += chunk.Samples.Length;
                    queued = true;
                }
                Channels[channel].Chunks.Add((span, outcome));
            }
            return queued;
        }

        public Item? Take()
        {
            if (Closed || !_queue.TryDequeue(out var item)) return null;
            _queuedSamples -= item.Chunk.Samples.Length;
            return item;
        }

        /// <summary><paramref name="segments"/> null: the ASR failed on the chunk.</summary>
        public void Complete(Item item, IReadOnlyList<RawSegment>? segments)
        {
            if (Closed) return;
            SetOutcome(item, segments is null ? PriorTranscription.ChunkOutcome.Failed : PriorTranscription.ChunkOutcome.Transcribed);
            Channels[item.Channel].Segments.AddRange(segments ?? []);
        }

        /// <summary>The decode task is gone: what waits, and every later chunk, is a failed chunk.</summary>
        public void Fail()
        {
            _failed = true;
            foreach (var item in _queue) SetOutcome(item, PriorTranscription.ChunkOutcome.Failed);
            _queue.Clear();
            _queuedSamples = 0;
        }

        public void Close()
        {
            Closed = true;
            _queue.Clear();
            _queuedSamples = 0;
            Channels = [];
        }

        private bool ChunkChannel(ReadOnlySpan<short> interleaved, int frames, int channels, int channel, string? language)
        {
            var samples = _scratch.AsSpan(0, frames);
            for (var i = 0; i < frames; i++) samples[i] = interleaved[i * channels + channel] / 32_768f;
            return Enqueue(Channels[channel].Chunker.Append(samples, language), channel);
        }

        private void SetOutcome(Item item, PriorTranscription.ChunkOutcome outcome) =>
            CollectionsMarshal.AsSpan(Channels[item.Channel].Chunks)[item.Index].Outcome = outcome;
    }
}
