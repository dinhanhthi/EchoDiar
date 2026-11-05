using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;

namespace EchoDiar.Transcribe;

/// <summary>
/// Transcribes a recording while it is made (Swift <c>RecordingTranscriber</c>): takes the writer's 16 kHz blocks,
/// cuts each channel into speech chunks (<see cref="SpeechChunker"/>) and decodes them with one Whisper or Parakeet
/// session (mic lines "Me", system lines "Others"). Live chunks are at most 12 s and close at a 0.5 s pause from 2 s,
/// and each decoded chunk becomes one line, so a line follows continuous speech within seconds. While no closed chunk
/// waits, an open chunk is decoded every <see cref="PartialStepSeconds"/> as greyed tentative text
/// (<see cref="Snapshot.Tentative"/>) that its line replaces, unless the channel's last final decode was slow
/// (<see cref="PartialMaxDecodeSeconds"/>). <see cref="FinishAsync"/> at Stop hands the segments, in file time, and
/// the spans not transcribed (gaps) to the pipeline as a <see cref="PriorTranscription"/>.
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
/// <para>
/// <c>displayOnly</c> (Swift <c>displayOnly</c>): a local live transcript while a cloud engine writes the transcript
/// after Stop. <see cref="FinishAsync"/> then cancels and returns null at once, without draining.
/// </para>
/// <para>
/// Cloud realtime (the <see cref="RealtimeLiveStream"/> constructor; Swift <c>init(realtime:engineID:displayOnly:language:)</c>)
/// streams both channels to the default cloud provider: its partials become the speaker's tentative text and its
/// finals lines, applied in order from a results queue on a background task. <see cref="TranscriberState.Loading"/>
/// until a channel's socket is live; <see cref="Snapshot.Reconnecting"/> and <see cref="Snapshot.RealtimeFailure"/>
/// tell the rest. Display only, <see cref="FinishAsync"/> cancels the stream at once and returns null; else it drains
/// the stream (at most ~5 s) and returns its finals and gaps as a prior (<c>cloud:&lt;provider id&gt;</c>).
/// </para>
/// </remarks>
public sealed class RecordingTranscriber : IAsyncDisposable
{
    public enum TranscriberState { Loading, Listening, Paused, Failed }

    /// <summary>What produces the live text (Swift <c>Source</c>; no Apple Speech on Windows).</summary>
    public enum TranscriberSource
    {
        /// <summary>Whisper or Parakeet decoding speech chunks.</summary>
        Chunks,
        /// <summary>The default cloud provider's realtime WebSocket, one per channel.</summary>
        CloudRealtime,
    }

    /// <summary>Who spoke a line: <c>ToString()</c> is the macOS label ("Me", "Others").</summary>
    public enum Speaker { Me, Others }

    /// <summary>One decoded chunk's text (<see cref="LineText"/>), not one per Whisper segment.</summary>
    /// <param name="Start">Seconds of file time (recording time, pauses excluded) where its chunk starts.</param>
    public sealed record Line(int Id, Speaker Speaker, string Text, double Start = 0);

    /// <param name="Lines">Oldest first, at most <see cref="MaxLines"/>; a new array whenever a line is added.</param>
    /// <param name="PendingSeconds">Seconds of speech waiting to be decoded, both channels summed.</param>
    /// <param name="Error">The error message when <paramref name="State"/> is <see cref="TranscriberState.Failed"/>.</param>
    public sealed record Snapshot(TranscriberState State, IReadOnlyList<Line> Lines, double PendingSeconds, string? Error = null)
    {
        /// <summary>
        /// At most one per speaker, ordered by <see cref="RecordingTranscriber.Tentative.StartSample"/>, shown after
        /// <see cref="Lines"/> (Swift <c>tentative</c>); a new array whenever it changes.
        /// </summary>
        public IReadOnlyList<Tentative> Tentative { get; init; } = [];

        public TranscriberSource Source { get; init; } = TranscriberSource.Chunks;

        /// <summary>
        /// <see cref="FinishAsync"/> returns null: the live text is not kept, a batch job writes the transcript after
        /// Stop (Swift <c>displayOnly</c>).
        /// </summary>
        public bool DisplayOnly { get; init; }

        /// <summary>Cloud realtime: a channel whose socket was live is connecting again (a drop, a rotation, a resume).</summary>
        public bool Reconnecting { get; init; }

        /// <summary>
        /// Cloud realtime: the first channel that stopped for this recording, as a short kind ("auth", "payment",
        /// "plan_limit", "config", "http_&lt;code&gt;", ...; never a message), cleared once a channel is live and none is
        /// failed (a "config" failure recovers at a language change); the state turns
        /// <see cref="TranscriberState.Failed"/> once both did, and back to running once a channel is live again.
        /// </summary>
        public string? RealtimeFailure { get; init; }
    }

    /// <summary>The text of a chunk still open, decoded so far; its line replaces it once the chunk closes (Swift <c>Tentative</c>).</summary>
    public sealed record Tentative(Speaker Speaker, string Text, long StartSample);

    /// <summary>Chunks per outcome, both channels summed.</summary>
    public sealed record ChunkStats(int Transcribed, int Silence, int Dropped, int Failed, int Pending);

    public const int MaxLines = 200;
    public const int SampleRate = SpeechChunker.SampleRate;
    /// <summary>The most audio waiting to be decoded, both channels summed (~38 MB of Float).</summary>
    public const int MaxQueuedSeconds = 600;
    /// <summary>How often the decode task wakes without a chunk, to free the model after the idle release.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    /// <summary>
    /// The longest live chunk (at most the model window): the hard cut searches its last third, 8–12 s. With
    /// <see cref="LiveShortPauseMinSeconds"/> it bounds the time to a live line under continuous speech (the batch
    /// tuning waits up to 30 s). Short chunks are left to the text filters
    /// (<see cref="Engines.WhisperEngine.SpeechText"/>, the lone-Merci rule).
    /// </summary>
    public const int LiveMaxChunkSeconds = 12;
    /// <summary>
    /// The pause that closes a live chunk of at least <see cref="LiveShortPauseMinSeconds"/> (Swift
    /// <c>liveShortPauseFrames</c>; the batch tuning waits 0.6 s).
    /// </summary>
    public const int LiveShortPauseFrames = 25; // 0.5 s
    /// <summary>
    /// The length a live chunk needs before a 0.5 s pause closes it: <c>min(2·50, 600/2) = 100</c> frames, so from 2 s
    /// (measured on macOS: 1 s chunks lost real speech and invented short lines).
    /// </summary>
    public const int LiveShortPauseMinSeconds = 2;
    /// <summary>The length an open chunk needs before its first partial decode (the greyed tentative text; Swift <c>partialMinSeconds</c>).</summary>
    public const int PartialMinSeconds = 1;
    /// <summary>
    /// How much an open chunk grows between two partials of it (Swift <c>partialStepSeconds</c>): the first knob to turn
    /// if the decode budget is exceeded.
    /// </summary>
    public const int PartialStepSeconds = 2;
    /// <summary>
    /// No partial on a channel while its last final decode took longer than this, so partials never starve the finals
    /// on a slow decoder (Swift <c>partialMaxDecodeSeconds</c>, 2.4 s there; lower here on purpose). A partial costs
    /// about as much as a final, and a Whisper decode costs about the same whatever the chunk length (it pads to a
    /// fixed window), so a share of the chunk's duration would turn partials off for short chunks. At one partial
    /// every <see cref="PartialStepSeconds"/> (2 s), a decode of at most 1.2 s (0.6 × the step) keeps one channel's
    /// partials at most ~60 % of the single decoder; with both channels speaking they would fill it, but finals go
    /// first and wait for at most one running partial. CPU Whisper.net on a slow PC stays above it and shows finals
    /// only (with the "N s behind" notice, <see cref="BehindSeconds"/>).
    /// </summary>
    public const double PartialMaxDecodeSeconds = 1.2;
    /// <summary>
    /// The speech waiting to be decoded from which the live transcript says how far behind it is
    /// (<see cref="BehindSeconds"/>; Swift <c>behindNoticeSeconds</c>): <see cref="Snapshot.PendingSeconds"/> sums
    /// both channels, and one waiting chunk per channel is up to 2 × <see cref="LiveMaxChunkSeconds"/> (24 s) while the
    /// decoder keeps up, so below this nothing is behind.
    /// </summary>
    public const int BehindNoticeSeconds = 25;

    /// <summary>null for cloud realtime, as are <see cref="_engine"/> and <see cref="_gate"/>.</summary>
    private readonly ModelEntry? _entry;
    private readonly ISessionTranscriptionEngine? _engine;
    private readonly ModelLoadGate? _gate;
    /// <summary>The prior's engine: the entry's catalog id, or <c>cloud:&lt;provider id&gt;</c> for cloud realtime.</summary>
    private readonly string _engineId;
    /// <summary>The cloud realtime stream; null for the chunk path.</summary>
    private readonly RealtimeLiveStream? _realtime;
    /// <summary>
    /// Cloud realtime: the stream's updates and <see cref="AppendInterleaved"/>'s resume notices (null), applied in order
    /// by the realtime task.
    /// </summary>
    private readonly Channel<RealtimeLiveStream.Update?> _results =
        Channel.CreateUnbounded<RealtimeLiveStream.Update?>(new UnboundedChannelOptions { SingleReader = true });
    /// <summary>Cloud realtime: one channel's samples of a block, reused on the writer's thread (the stream copies them).</summary>
    private float[] _micScratch = [];
    private float[] _systemScratch = [];
    private readonly TimeSpan? _idleRelease;
    private readonly Func<string?> _language;
    /// <summary><see cref="FinishAsync"/> returns null without draining.</summary>
    private readonly bool _displayOnly;
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
    /// <summary>Every line shown, oldest first, also those the snapshot dropped (<see cref="MaxLines"/>): the catch-up's input.</summary>
    private readonly List<Line> _finalized = [];
    private Tentative[] _tentative = [];
    private int _nextLineID;
    private Snapshot? _published;
    private Task? _decoder;
    private bool _finished;
    /// <summary>No more <see cref="Updated"/>: <see cref="FinishAsync"/> drained the queue or <see cref="Cancel"/> ran.</summary>
    private bool _ended;
    /// <summary>Cloud realtime: each channel's last state (index = <see cref="RecordingSession.Channel"/>), and the channels that were live.</summary>
    private readonly RealtimeLiveStream.State?[] _realtimeStates = [null, null];
    private readonly HashSet<int> _realtimeConnected = [];
    private bool _reconnecting;
    private string? _realtimeFailure;

    /// <summary>
    /// <paramref name="entry"/> is the installed Whisper or Parakeet entry <paramref name="engine"/> loads
    /// (<see cref="TranscriptionPipeline.TranscribesChunks"/>). <paramref name="idleRelease"/> frees the model once no
    /// chunk came for that long (a paused recording) and loads it again for the next one; null keeps it until
    /// <see cref="FinishAsync"/>. <paramref name="language"/> gives the current marker language ("auto", a BCP-47
    /// code, or null = detect); it is called for every block on the writer's thread, so it must be cheap and thread-safe.
    /// <paramref name="displayOnly"/> keeps nothing at Stop (<see cref="FinishAsync"/> returns null at once).
    /// </summary>
    public RecordingTranscriber(
        ModelEntry entry, ISessionTranscriptionEngine engine, ModelLoadGate gate, TimeSpan? idleRelease,
        Func<string?> language, bool displayOnly = false)
    {
        _entry = entry;
        _engine = engine;
        _gate = gate;
        _engineId = entry.Id;
        _idleRelease = idleRelease;
        _language = language;
        _displayOnly = displayOnly;
        _buffer = new Buffer(
            Math.Min(entry.WindowSeconds ?? TranscriptionPipeline.DefaultWindowSeconds, LiveMaxChunkSeconds),
            shortPauseMinSeconds: LiveShortPauseMinSeconds, shortPauseFrames: LiveShortPauseFrames);
    }

    /// <summary>
    /// Cloud realtime: <paramref name="stream"/> (<see cref="RealtimeLiveStream.Make"/>) takes both channels' blocks;
    /// <paramref name="engineId"/> is the prior's (<c>cloud:&lt;provider id&gt;</c> of the stream's provider).
    /// <paramref name="displayOnly"/> (the realtime result is not the transcript) cancels the stream at Stop and returns
    /// null; else <see cref="FinishAsync"/> drains it and returns the prior. <paramref name="language"/> as for the
    /// other constructor, read per block. No model gate, no idle release.
    /// </summary>
    public RecordingTranscriber(RealtimeLiveStream stream, string engineId, bool displayOnly, Func<string?> language)
    {
        _realtime = stream;
        _engineId = engineId;
        _language = language;
        _displayOnly = displayOnly;
        _buffer = new Buffer(LiveMaxChunkSeconds);
    }

    /// <summary>
    /// Every change, raised inside the transcriber's lock (so in order) on the thread that made it: the decode task, or
    /// the caller of <see cref="Start"/> or <see cref="Flush"/>. Marshal to the UI; never wait there for another thread
    /// that calls into the transcriber. Ends with <see cref="FinishAsync"/> or <see cref="Cancel"/>.
    /// </summary>
    public event Action<Snapshot>? Updated;

    /// <summary>What <see cref="FinishAsync"/> left; null before it, or when the model never loaded or <see cref="Cancel"/> ran.</summary>
    public ChunkStats? Stats { get; private set; }

    /// <summary>
    /// Waits for the model gate on the thread pool, loads the model there and starts decoding (cloud realtime: starts
    /// the stream and applies its updates on the thread pool).
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_decoder is not null || _finished) return;
            PublishLocked(TranscriberState.Loading);
            _decoder = _realtime is { } realtime ? Task.Run(() => RunRealtimeAsync(realtime)) : Task.Run(DecodeAsync);
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
        if (_realtime is { } realtime)
        {
            StreamBlock(realtime, interleaved, frames, channels, language);
            return;
        }
        bool wake;
        lock (_buffer) wake = _buffer.Append(interleaved, frames, channels, language);
        if (wake) _wake.Writer.TryWrite(true);
    }

    /// <summary>
    /// Cloud realtime: hands the block, at its file position, to the stream, which copies it and never blocks; the first
    /// block after a pause queues a resume notice.
    /// </summary>
    private void StreamBlock(RealtimeLiveStream realtime, ReadOnlySpan<short> interleaved, int frames, int channels, string? language)
    {
        (long StartSample, bool Resumed)? slot;
        lock (_buffer) slot = _buffer.AppendRealtime(frames);
        if (slot is not { } block) return;
        if (_micScratch.Length < frames)
        {
            _micScratch = new float[frames];
            _systemScratch = new float[frames];
        }
        var mic = _micScratch.AsSpan(0, frames);
        var system = _systemScratch.AsSpan(0, frames);
        for (var i = 0; i < frames; i++)
        {
            mic[i] = interleaved[i * channels + RecordingSession.Channel.Mic] / 32_768f;
            system[i] = interleaved[i * channels + RecordingSession.Channel.System] / 32_768f;
        }
        realtime.Append(mic, system, block.StartSample, language);
        if (block.Resumed) _results.Writer.TryWrite(null);
    }

    /// <summary>On pause: closes the open chunk of each channel so it is decoded now (cloud realtime: closes the sockets).</summary>
    public void Flush()
    {
        lock (_buffer) _buffer.Flush(paused: true);
        _wake.Writer.TryWrite(true);
        _realtime?.Pause();
        PublishRunning();
    }

    /// <summary>
    /// At Stop: decodes what is left, unloads the model (releasing the gate) and returns what was transcribed; null
    /// when no chunk was, or the model never loaded (the pipeline then transcribes the whole recording). Display only:
    /// cancels without waiting for a decode in flight and returns null at once; <see cref="Stats"/> then counts the
    /// lines shown as transcribed, once the model loaded. Cloud realtime: see <see cref="FinishRealtimeAsync"/>.
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
        if (_displayOnly)
        {
            if (loaded) lock (_lock) Stats = new ChunkStats(_nextLineID, 0, 0, 0, 0);
            Cancel();
            return null;
        }
        if (_realtime is { } realtime) return await FinishRealtimeAsync(realtime, decoder).ConfigureAwait(false);
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
            EngineID = _engineId,
            Mic = [.. mic.Segments.OrderBy(s => s.Start)],
            System = [.. system.Segments.OrderBy(s => s.Start)],
            MicGaps = PriorTranscription.Gaps(mic.Chunks),
            SystemGaps = PriorTranscription.Gaps(system.Chunks),
            Duration = duration,
        };
    }

    /// <summary>
    /// Cloud realtime as the transcript: drains the stream (at most its ~5 s drain) and returns its finals, its gaps per
    /// channel and its model as the prior, also when no socket ever connected (the whole recording is then a gap); null
    /// when <see cref="Cancel"/> ran meanwhile.
    /// </summary>
    private async Task<PriorTranscription?> FinishRealtimeAsync(RealtimeLiveStream realtime, Task? decoder)
    {
        lock (_buffer) _buffer.Finishing = true;
        var result = await realtime.FinishAsync().ConfigureAwait(false);
        // The realtime task applies the updates left and ends once the stream's updates complete.
        if (decoder is not null) await decoder.ConfigureAwait(false);
        else _results.Writer.TryComplete();
        lock (_lock)
        {
            _decoder = null;
            _ended = true;
        }
        double duration;
        lock (_buffer)
        {
            if (_buffer.Closed) return null;
            duration = (double)_buffer.SamplesSeen / SampleRate;
            _buffer.Close();
        }
        lock (_lock) Stats = new ChunkStats(_nextLineID, 0, 0, 0, 0);
        return new PriorTranscription
        {
            EngineID = _engineId,
            Mic = [.. result.Mic.OrderBy(s => s.Start)],
            System = [.. result.System.OrderBy(s => s.Start)],
            MicGaps = result.MicGaps,
            SystemGaps = result.SystemGaps,
            Duration = duration,
            ModelId = result.ModelId,
            SystemBridges = result.SystemBridges,
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
        _realtime?.Cancel();
        _results.Writer.TryComplete();
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
        if (_gate is not { } gate) return;
        try
        {
            await gate.WithExclusiveModelAsync(ModelLoadGate.Owner.LiveTranscript, async t =>
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
        if (_engine is not { } engine) throw new TranscriptionException(TranscriptionErrorKind.UnknownEngine, _engineId);
        var session = await engine.OpenSessionAsync(token).ConfigureAwait(false);
        await using var model = new IdleModel<ITranscriptionSession>(
            session, _idleRelease, engine.OpenSessionAsync, s => s.DisposeAsync());
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

    /// <summary>
    /// Decodes the waiting chunks in order, then a partial of an open chunk when one is due; a final queued meanwhile
    /// goes first. A failed load ends the decode task; a failed chunk becomes a gap. Ends by dropping the tentative text
    /// no final will replace.
    /// </summary>
    private async Task DrainAsync(IdleModel<ITranscriptionSession> model, CancellationToken token)
    {
        while (true)
        {
            Item? item;
            Partial? partial = null;
            lock (_buffer) item = _buffer.Take();
            if (item is null) lock (_buffer) partial = _buffer.TakePartial();
            if (item is not null) await DecodeFinalAsync(item, model, token).ConfigureAwait(false);
            else if (partial is not null) await DecodePartialAsync(partial, model, token).ConfigureAwait(false);
            else break;
        }
        PruneTentative();
    }

    /// <summary>
    /// Records a closed chunk's segments and decode time (the session's transcribe only, not a model reload after the
    /// idle release, as Swift starts its clock after <c>model.get()</c>), and shows its line in place of its tentative
    /// text.
    /// </summary>
    private async Task DecodeFinalAsync(Item item, IdleModel<ITranscriptionSession> model, CancellationToken token)
    {
        var (segments, wallSeconds) = await TranscribeAsync(model, item.Chunk, token).ConfigureAwait(false);
        var offset = (double)item.Chunk.StartSample / SampleRate;
        var shifted = segments?.Select(s => s with { Start = s.Start + offset, End = s.End + offset }).ToList();
        lock (_buffer)
        {
            _buffer.Complete(item, shifted);
            _buffer.RecordDecode(item.Channel, wallSeconds);
        }
        var mic = item.Channel == RecordingSession.Channel.Mic;
        string? text = null;
        if (shifted is not null)
        {
            var span = new Span(offset, (double)(item.Chunk.StartSample + item.Chunk.Samples.Length) / SampleRate);
            text = LiveLine(shifted, mic, () =>
            {
                lock (_buffer) return _buffer.OthersSpeak(span);
            });
        }
        AddLine(mic ? Speaker.Me : Speaker.Others, text, item.Chunk.StartSample);
    }

    /// <summary>Shows an open chunk's text so far as its speaker's tentative text; a failed decode changes nothing.</summary>
    private async Task DecodePartialAsync(Partial partial, IdleModel<ITranscriptionSession> model, CancellationToken token)
    {
        var (segments, _) = await TranscribeAsync(model, partial.Chunk, token).ConfigureAwait(false);
        if (segments is null) return;
        var mic = partial.Channel == RecordingSession.Channel.Mic;
        var span = new Span((double)partial.Chunk.StartSample / SampleRate,
            (double)(partial.Chunk.StartSample + partial.Chunk.Samples.Length) / SampleRate);
        var text = LiveLine(segments, mic, () =>
        {
            lock (_buffer) return _buffer.OthersSpeak(span);
        });
        SetTentative(mic ? Speaker.Me : Speaker.Others, text, partial.Chunk.StartSample);
    }

    /// <summary>
    /// The chunk's segments (chunk time), or null when the ASR failed on it, and the wall seconds of the session's
    /// transcribe (without a model load); cancellation propagates.
    /// </summary>
    private static Task<(IReadOnlyList<RawSegment>? Segments, double WallSeconds)> TranscribeAsync(
        IdleModel<ITranscriptionSession> model, SpeechChunker.Chunk chunk, CancellationToken token) =>
        model.UseAsync<(IReadOnlyList<RawSegment>?, double)>(async (session, t) =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var segments = await session.TranscribeAsync(chunk.Samples, chunk.Language, t).ConfigureAwait(false);
                return (segments, watch.Elapsed.TotalSeconds);
            }
            catch (Exception) when (!t.IsCancellationRequested)
            {
                return (null, watch.Elapsed.TotalSeconds);
            }
        }, token);

    // Cloud realtime

    /// <summary>
    /// Starts the stream and applies its updates, with <see cref="AppendInterleaved"/>'s resume notices, in order until
    /// the stream ends (<see cref="FinishAsync"/>) or <see cref="Cancel"/> ran; never throws.
    /// </summary>
    private async Task RunRealtimeAsync(RealtimeLiveStream realtime)
    {
        var token = _cancel.Token;
        realtime.Start();
        var pump = PumpRealtimeAsync(realtime, token);
        try
        {
            await foreach (var result in _results.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (result is null) ApplyResumed();
                else Apply(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        await pump.ConfigureAwait(false);
        if (token.IsCancellationRequested) return;
        lock (_lock) _tentative = [];
        PublishRunning();
    }

    /// <summary>Moves the stream's updates into the results queue, and completes it once the stream ends.</summary>
    private async Task PumpRealtimeAsync(RealtimeLiveStream realtime, CancellationToken token)
    {
        try
        {
            await foreach (var update in realtime.Updates.ReadAllAsync(token).ConfigureAwait(false))
                _results.Writer.TryWrite(update);
        }
        catch (OperationCanceledException)
        {
        }
        _results.Writer.TryComplete();
    }

    /// <summary>The first block after a pause: listening again.</summary>
    private void ApplyResumed()
    {
        lock (_lock)
        {
            if (_state == TranscriberState.Failed) return;
        }
        PublishRunning();
    }

    /// <summary>
    /// One update of the stream, in the order they came (Swift <c>apply</c>). Once failed, only socket states still
    /// count: a stopped channel can stream again. Internal for the tests.
    /// </summary>
    internal void Apply(RealtimeLiveStream.Update update)
    {
        lock (_lock)
        {
            if (_state == TranscriberState.Failed && update is not RealtimeLiveStream.Update.StateChanged) return;
        }
        switch (update)
        {
            case RealtimeLiveStream.Update.StateChanged state:
                SetRealtimeState(state.Channel, state.State);
                break;
            case RealtimeLiveStream.Update.Partial partial:
                SetPartial(partial.Channel, partial.Text, partial.Start);
                break;
            case RealtimeLiveStream.Update.Final final:
                AddFinal(final.Channel, final.Segment);
                break;
        }
    }

    /// <summary>
    /// A channel's text so far as its speaker's tentative text (null clears it). Without a provider time it keeps the
    /// start of the speaker's tentative text, else starts at the channel's last final end, else at the samples seen.
    /// </summary>
    private void SetPartial(int channel, string? text, double? start)
    {
        var speaker = SpeakerOf(channel);
        lock (_lock)
        {
            long startSample;
            if (start is { } seconds) startSample = (long)Math.Round(seconds * SampleRate, MidpointRounding.AwayFromZero);
            else if (_tentative.FirstOrDefault(t => t.Speaker == speaker) is { } current) startSample = current.StartSample;
            else lock (_buffer) startSample = _buffer.PartialStart(channel);
            _tentative = WithTentative(_tentative, speaker, text, startSample);
        }
        PublishRunning();
    }

    /// <summary>
    /// <see cref="TranscriberState.Loading"/> until a channel is live, <see cref="Snapshot.Reconnecting"/> while a
    /// channel that was live connects again, <see cref="Snapshot.RealtimeFailure"/> from the first channel that stopped,
    /// <see cref="TranscriberState.Failed"/> once both did, until one is live again (a "config" failure recovers at a
    /// language change).
    /// </summary>
    private void SetRealtimeState(int channel, RealtimeLiveStream.State state)
    {
        if (channel < 0 || channel >= _realtimeStates.Length) return;
        lock (_lock)
        {
            _realtimeStates[channel] = state;
            switch (state)
            {
                case RealtimeLiveStream.State.Live:
                    _realtimeConnected.Add(channel);
                    lock (_buffer) _buffer.Loaded = true;
                    // Both channels had stopped: this one streams again, so the running publish shows it running.
                    if (_state == TranscriberState.Failed) _state = TranscriberState.Loading;
                    // A "config" failure recovers at a language change.
                    if (!_realtimeStates.Any(s => s is RealtimeLiveStream.State.Failed)) _realtimeFailure = null;
                    break;
                case RealtimeLiveStream.State.Failed failed:
                    _realtimeFailure ??= failed.Kind;
                    _tentative = WithTentative(_tentative, SpeakerOf(channel), null, 0);
                    break;
            }
            _reconnecting = Enumerable.Range(0, _realtimeStates.Length).Any(index => _realtimeStates[index] switch
            {
                RealtimeLiveStream.State.Reconnecting => true,
                RealtimeLiveStream.State.Connecting => _realtimeConnected.Contains(index),
                _ => false,
            });
            var failures = _realtimeStates.OfType<RealtimeLiveStream.State.Failed>().Select(f => f.Kind).ToList();
            if (failures.Count == _realtimeStates.Length)
            {
                _reconnecting = false;
                _error = $"Cloud realtime stopped ({failures[0]}).";
                PublishLocked(TranscriberState.Failed);
                return;
            }
            PublishRunningLocked();
            // The running publish leaves loading alone: the new fields still show.
            PublishLocked(_state);
        }
    }

    /// <summary>Records a final and shows its line in place of the speaker's tentative text.</summary>
    private void AddFinal(int channel, RawSegment segment)
    {
        var speaker = SpeakerOf(channel);
        var span = new Span(segment.Start, segment.End);
        lock (_lock)
        {
            lock (_buffer) _buffer.AddLive(segment, channel);
            // The system's text still tentative counts like the chunk path's open chunk: its final lags the mic's.
            var othersPending = _tentative.Any(t => t.Speaker == Speaker.Others
                && (double)t.StartSample / SampleRate <= span.End + TranscriptMerger.BleedTimeTolerance);
            var text = LiveLine([segment], speaker == Speaker.Me, () =>
            {
                if (othersPending) return true;
                lock (_buffer) return _buffer.OthersFinal(span);
            });
            _tentative = WithTentative(_tentative, speaker, null, 0);
            if (text is not null) AppendLineLocked(speaker, text, segment.Start);
        }
        PublishRunning();
    }

    private static Speaker SpeakerOf(int channel) => channel == RecordingSession.Channel.Mic ? Speaker.Me : Speaker.Others;

    // State

    /// <summary>Raises <see cref="Updated"/> when the snapshot changed; call inside <see cref="_lock"/>.</summary>
    private void PublishLocked(TranscriberState state)
    {
        if (_ended) return;
        _state = state;
        if (state == TranscriberState.Failed) _tentative = [];
        double pending;
        lock (_buffer) pending = _buffer.PendingSeconds;
        var snapshot = new Snapshot(_state, _lines, pending, _state == TranscriberState.Failed ? _error : null)
        {
            Tentative = _tentative,
            Source = _realtime is null ? TranscriberSource.Chunks : TranscriberSource.CloudRealtime,
            DisplayOnly = _displayOnly,
            Reconnecting = _reconnecting,
            RealtimeFailure = _realtimeFailure,
        };
        if (snapshot == _published) return;
        _published = snapshot;
        Updated?.Invoke(snapshot);
    }

    /// <summary>Listening or paused once loaded; leaves loading (before the load) and failed alone.</summary>
    private void PublishRunning()
    {
        lock (_lock) PublishRunningLocked();
    }

    /// <summary><see cref="PublishRunning"/>; call inside <see cref="_lock"/>.</summary>
    private void PublishRunningLocked()
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

    /// <summary>
    /// The final of the chunk starting at <paramref name="startSample"/> (Swift <c>add</c>): its line (none when
    /// <paramref name="text"/> is null) replaces the speaker's tentative text of that chunk, in one publish, and goes to
    /// the snapshot (the newest <see cref="MaxLines"/>) and the finalized log (all of them).
    /// </summary>
    private void AddLine(Speaker speaker, string? text, long startSample)
    {
        lock (_lock)
        {
            _tentative = WithoutTentative(_tentative, speaker, startSample);
            if (text is not null) AppendLineLocked(speaker, text, (double)startSample / SampleRate);
        }
        PublishRunning();
    }

    /// <summary>
    /// Adds a line with the next id starting at <paramref name="start"/> seconds to the snapshot (the newest
    /// <see cref="MaxLines"/>) and the finalized log (all of them); call inside <see cref="_lock"/>.
    /// </summary>
    private void AppendLineLocked(Speaker speaker, string text, double start)
    {
        var line = new Line(_nextLineID++, speaker, text, start);
        _finalized.Add(line);
        var lines = new List<Line>(_lines) { line };
        _lines = [.. lines.Skip(Math.Max(0, lines.Count - MaxLines))];
    }

    /// <summary>
    /// The lines shown after the line <paramref name="afterId"/> (all of them for null), oldest first, none dropped
    /// (Swift <c>finalizedLines(after:)</c>); ids increase by one and are never reused. Kept after
    /// <see cref="FinishAsync"/> and <see cref="Cancel"/>.
    /// </summary>
    public IReadOnlyList<Line> FinalizedLines(int? afterId)
    {
        lock (_lock) return afterId is { } id ? [.. _finalized.Where(l => l.Id > id)] : [.. _finalized];
    }

    private void SetTentative(Speaker speaker, string? text, long startSample)
    {
        lock (_lock) _tentative = WithTentative(_tentative, speaker, text, startSample);
        PublishRunning();
    }

    /// <summary>Drops the tentative text no final will replace (<see cref="Buffer.KeepsTentative"/>; Swift <c>pruneTentative</c>).</summary>
    private void PruneTentative()
    {
        lock (_lock)
        {
            Tentative[] kept;
            lock (_buffer)
            {
                kept = [.. _tentative.Where(t => _buffer.KeepsTentative(
                    t.Speaker == Speaker.Me ? RecordingSession.Channel.Mic : RecordingSession.Channel.System,
                    t.StartSample))];
            }
            if (kept.Length == _tentative.Length) return;
            _tentative = kept;
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

    /// <summary>
    /// How far behind decoding is, in rounded seconds of speech waiting (<see cref="Snapshot.PendingSeconds"/>), for
    /// the live transcript's status (Swift <c>behindSeconds</c>): null unless listening with at least
    /// <see cref="BehindNoticeSeconds"/> waiting. Halves round away from zero, as Swift's <c>rounded()</c>.
    /// </summary>
    public static int? BehindSeconds(Snapshot snapshot) =>
        snapshot.State == TranscriberState.Listening && snapshot.PendingSeconds >= BehindNoticeSeconds
            ? (int)Math.Round(snapshot.PendingSeconds, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>
    /// A chunk's segments as one line (Swift <c>lineText</c>): trimmed, blank ones skipped, joined with a space; null
    /// when no text is left.
    /// </summary>
    internal static string? LineText(IReadOnlyList<RawSegment> segments)
    {
        var texts = segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0).ToList();
        return texts.Count == 0 ? null : string.Join(" ", texts);
    }

    /// <summary>
    /// The line a decoded chunk shows (Swift <c>liveLine</c>, <see cref="LineText"/>), or null when it shows none: a mic
    /// line that is a lone "Merci." (<see cref="TranscriptMerger.IsLoneMerci"/>) while <paramref name="othersSpeak"/> is
    /// the residual of others' voices on a near-silent mic. <paramref name="othersSpeak"/> is called only for a mic lone
    /// Merci. Live uses the energy chunk's span while the final transcript (<c>TranscriptMerger.IsBleed</c>) uses
    /// Whisper segment times, so the two can rarely disagree; accepted, the final transcript is the source of truth.
    /// </summary>
    internal static string? LiveLine(IReadOnlyList<RawSegment> segments, bool mic, Func<bool> othersSpeak)
    {
        var text = LineText(segments);
        return text is not null && mic && TranscriptMerger.IsLoneMerci(text) && othersSpeak() ? null : text;
    }

    /// <summary>
    /// <paramref name="current"/> with <paramref name="speaker"/>'s tentative text set to <paramref name="text"/> from
    /// <paramref name="startSample"/> (removed when null; Swift <c>tentative(_:setting:text:startSample:)</c>): at most
    /// one per speaker, ordered by start (then speaker); <paramref name="current"/> itself when nothing changes.
    /// </summary>
    internal static Tentative[] WithTentative(Tentative[] current, Speaker speaker, string? text, long startSample)
    {
        var kept = current.Where(t => t.Speaker != speaker);
        if (text is not null) kept = kept.Append(new Tentative(speaker, text, startSample));
        Tentative[] next = [.. kept.OrderBy(t => t.StartSample).ThenBy(t => t.Speaker)];
        return next.SequenceEqual(current) ? current : next;
    }

    /// <summary>
    /// <paramref name="current"/> without <paramref name="speaker"/>'s tentative text when it is of the chunk starting
    /// at <paramref name="startSample"/>, its final came (Swift <c>tentative(_:clearing:startSample:)</c>);
    /// <paramref name="current"/> itself when nothing changes.
    /// </summary>
    internal static Tentative[] WithoutTentative(Tentative[] current, Speaker speaker, long startSample) =>
        current.Any(t => t.Speaker == speaker && t.StartSample == startSample)
            ? [.. current.Where(t => t.Speaker != speaker || t.StartSample != startSample)]
            : current;

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

    /// <summary>An open chunk to decode as tentative text; its outcome is not recorded (Swift <c>Partial</c>).</summary>
    /// <param name="Channel">A <see cref="RecordingSession.Channel"/> index.</param>
    public sealed record Partial(int Channel, SpeechChunker.Chunk Chunk);

    /// <summary>One channel's chunker, chunk outcomes (file order) and decoded segments (file time).</summary>
    public sealed class ChannelState(int maxChunkSeconds, int shortPauseMinSeconds, int shortPauseFrames)
    {
        public SpeechChunker Chunker { get; } = new(maxChunkSeconds, shortPauseMinSeconds, shortPauseFrames);
        public List<(Span Span, PriorTranscription.ChunkOutcome Outcome)> Chunks { get; } = [];
        public List<RawSegment> Segments { get; } = [];
        /// <summary>The open chunk's start and length at its last partial.</summary>
        internal (long StartSample, int Count)? LastPartial { get; set; }
        /// <summary>The open chunk's start and length when <see cref="Buffer.Append"/> last woke the decoder for a partial.</summary>
        internal (long StartSample, int Count)? SignalledPartial { get; set; }
        /// <summary>The last final decode's wall seconds.</summary>
        internal double DecodeSeconds { get; set; }
    }

    /// <summary>
    /// The transcriber's bookkeeping, public for its tests. Not thread-safe (the transcriber locks it). Both channels
    /// grow in step (the writer writes equal frames per channel); index = <see cref="RecordingSession.Channel"/>.
    /// </summary>
    public sealed class Buffer(
        int maxChunkSeconds, int shortPauseMinSeconds = SpeechChunker.ShortPauseMinSeconds,
        int shortPauseFrames = SpeechChunker.ShortPauseFrames, int maxQueuedSamples = MaxQueuedSeconds * SampleRate)
    {
        private readonly Queue<Item> _queue = new();
        private int _queuedSamples;
        private bool _failed;
        /// <summary>One channel at a time, reused across blocks (the chunker copies what it keeps).</summary>
        private float[] _scratch = [];

        public List<ChannelState> Channels { get; private set; } =
            [new(maxChunkSeconds, shortPauseMinSeconds, shortPauseFrames),
                new(maxChunkSeconds, shortPauseMinSeconds, shortPauseFrames)];
        public long SamplesSeen { get; private set; }
        /// <summary>The session loaded once; until then <see cref="FinishAsync"/> has nothing to wait for.</summary>
        public bool Loaded { get; set; }
        /// <summary>Between <see cref="Flush"/> and the next block.</summary>
        public bool Paused { get; private set; }
        /// <summary><see cref="FinishAsync"/> flushed the chunkers: no more blocks.</summary>
        public bool Finishing { get; set; }
        public bool Closed { get; private set; }

        public double PendingSeconds => (double)_queuedSamples / SampleRate;

        /// <summary>
        /// Splits an interleaved Int16 block per channel and chunks it; true when a chunk was queued, the recording
        /// resumed or a partial became due.
        /// </summary>
        public bool Append(ReadOnlySpan<short> interleaved, int frames, int channels, string? language)
        {
            if (Closed || Finishing) return false;
            var resumed = Paused;
            Paused = false;
            SamplesSeen += frames;
            if (_scratch.Length < frames) _scratch = new float[frames];
            var micQueued = ChunkChannel(interleaved, frames, channels, RecordingSession.Channel.Mic, language);
            var systemQueued = ChunkChannel(interleaved, frames, channels, RecordingSession.Channel.System, language);
            var partialDue = false;
            for (var channel = 0; channel < Channels.Count; channel++) partialDue |= SignalPartial(channel);
            return resumed || micQueued || systemQueued || partialDue;
        }

        /// <summary>
        /// Cloud realtime: a block of <paramref name="count"/> samples per channel goes to the stream, which queues
        /// nothing here: its file position and whether it is the first after a pause; null when closed, finishing or failed.
        /// </summary>
        public (long StartSample, bool Resumed)? AppendRealtime(int count)
        {
            if (Closed || Finishing || _failed) return null;
            var startSample = SamplesSeen;
            var resumed = Paused;
            Paused = false;
            SamplesSeen += count;
            return (startSample, resumed);
        }

        /// <summary>
        /// Cloud realtime: where a partial without a provider time starts: the channel's last final end, else the
        /// samples seen.
        /// </summary>
        public long PartialStart(int channel)
        {
            if (Closed || Channels[channel].Segments.Count == 0) return SamplesSeen;
            return (long)Math.Round(Channels[channel].Segments[^1].End * SampleRate, MidpointRounding.AwayFromZero);
        }

        /// <summary>Cloud realtime: a final of <paramref name="channel"/>, in file seconds.</summary>
        public void AddLive(RawSegment segment, int channel)
        {
            if (Closed) return;
            Channels[channel].Segments.Add(segment);
        }

        /// <summary>
        /// Cloud realtime: true when a system final overlaps <paramref name="span"/> widened by
        /// <see cref="TranscriptMerger.BleedTimeTolerance"/>.
        /// </summary>
        public bool OthersFinal(Span span)
        {
            if (Closed) return false;
            var start = span.Start - TranscriptMerger.BleedTimeTolerance;
            var end = span.End + TranscriptMerger.BleedTimeTolerance;
            return Channels[RecordingSession.Channel.System].Segments.Any(s => s.Start < end && s.End > start);
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

        /// <summary>
        /// True when the system channel has speech near <paramref name="span"/> (widened by
        /// <see cref="TranscriptMerger.BleedTimeTolerance"/>): a chunk that is not silence overlapping it, or the open
        /// chunk starting by its end (Swift <c>othersSpeak(during:)</c>).
        /// </summary>
        public bool OthersSpeak(Span span)
        {
            if (Closed) return false;
            var start = span.Start - TranscriptMerger.BleedTimeTolerance;
            var end = span.End + TranscriptMerger.BleedTimeTolerance;
            var system = Channels[RecordingSession.Channel.System];
            if (system.Chunks.Any(c => c.Outcome != PriorTranscription.ChunkOutcome.Silence && c.Span.Start < end && c.Span.End > start))
                return true;
            var open = system.Chunker.OpenChunk();
            return open is not null && (double)open.StartSample / SampleRate <= end;
        }

        public Item? Take()
        {
            if (Closed || !_queue.TryDequeue(out var item)) return null;
            _queuedSamples -= item.Chunk.Samples.Length;
            return item;
        }

        /// <summary>
        /// The open chunk to decode as tentative text (Swift <c>takePartial()</c>), only while no closed chunk waits
        /// (partials never starve the finals) and not paused, finishing, closed or failed: among the channels with a
        /// partial due (<see cref="PartialDue"/>), the one whose open chunk started first.
        /// </summary>
        public Partial? TakePartial()
        {
            if (_queue.Count > 0 || Paused || Finishing || Closed || _failed) return null;
            int? channel = null;
            (long StartSample, int Count) open = default;
            for (var c = 0; c < Channels.Count; c++)
            {
                if (PartialDue(c) is not { } due || (channel is not null && due.StartSample >= open.StartSample)) continue;
                channel = c;
                open = due;
            }
            if (channel is not { } picked || Channels[picked].Chunker.OpenChunk() is not { } chunk) return null;
            Channels[picked].LastPartial = open;
            return new Partial(picked, chunk);
        }

        /// <summary>
        /// True while a final of <paramref name="channel"/>'s chunk starting at <paramref name="startSample"/> may still
        /// come and replace its tentative text: the chunk waits in the queue or is still open (Swift
        /// <c>keepsTentative(channel:startSample:)</c>). False once finishing (what is left is decoded or a gap by
        /// then) or closed.
        /// </summary>
        public bool KeepsTentative(int channel, long startSample)
        {
            if (Closed || Finishing) return false;
            return _queue.Any(i => i.Channel == channel && i.Chunk.StartSample == startSample)
                || Channels[channel].Chunker.OpenSpan()?.StartSample == startSample;
        }

        /// <summary>
        /// Stores the wall seconds of a final decode of <paramref name="channel"/> for the partial gate (Swift
        /// <c>recordDecode(channel:wallSeconds:)</c>).
        /// </summary>
        public void RecordDecode(int channel, double wallSeconds)
        {
            if (Closed) return;
            Channels[channel].DecodeSeconds = wallSeconds;
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

        /// <summary>
        /// The open chunk's span of <paramref name="channel"/> when a partial of it is due (Swift <c>partialDue(_:)</c>):
        /// at least <see cref="PartialMinSeconds"/> long, grown <see cref="PartialStepSeconds"/> since the last partial
        /// of the same chunk (a new start is a new chunk), and the channel's last final decode took at most
        /// <see cref="PartialMaxDecodeSeconds"/>.
        /// </summary>
        private (long StartSample, int Count)? PartialDue(int channel)
        {
            var state = Channels[channel];
            if (state.Chunker.OpenSpan() is not { } open || open.Count < PartialMinSeconds * SampleRate
                || state.DecodeSeconds > PartialMaxDecodeSeconds)
                return null;
            if (state.LastPartial is { } last && last.StartSample == open.StartSample
                && open.Count - last.Count < PartialStepSeconds * SampleRate)
                return null;
            return open;
        }

        /// <summary>
        /// True once per due step (Swift <c>signalPartial(_:)</c>): a partial of <paramref name="channel"/> is due and
        /// <see cref="Append"/> has not woken the decoder for it yet.
        /// </summary>
        private bool SignalPartial(int channel)
        {
            if (_failed || PartialDue(channel) is not { } open) return false;
            if (Channels[channel].SignalledPartial is { } signalled && signalled.StartSample == open.StartSample
                && open.Count - signalled.Count < PartialStepSeconds * SampleRate)
                return false;
            Channels[channel].SignalledPartial = open;
            return true;
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
