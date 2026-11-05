using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using EchoDiar.AI;
using EchoDiar.AI.Cloud;
using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;

namespace EchoDiar.Transcribe;

/// <summary>How a planned session switch (rotation before the session cap, resume after a pause) keeps speaker labels (Swift <c>RealtimePolicy.Handover</c>).</summary>
internal enum RealtimeHandover
{
    /// <summary>Open the next socket <c>LabelOverlapSeconds</c> before closing the old one and stream both (billed twice).</summary>
    Parallel,
    /// <summary>
    /// Close the old socket, then send its last <c>LabelOverlapSeconds</c> again to the next one, faster than real time
    /// (documented for the provider; Speechmatics Free allows only 2 sessions, so no third socket).
    /// </summary>
    Replay,
    /// <summary>
    /// Close the old socket, then open the next one; its labels stay session-scoped (Gladia Free: 1 session, replay not
    /// documented).
    /// </summary>
    None,
}

/// <summary>What a provider's realtime API allows, per <c>stt-protocols.md</c> part B (Swift <c>RealtimePolicy</c>). Pure, for tests.</summary>
/// <param name="AnnouncesStart">The provider sends a ready message (<see cref="RealtimeEvent.Started"/>) before it takes audio; else the session starts on connect.</param>
/// <param name="MaxSessionSeconds">The documented maximum session length; null when unpublished (then only <c>Started.ExpiresAt</c> rotates).</param>
/// <param name="Rate">The fastest send rate (times real time) above <paramref name="BurstSeconds"/>, the audio that may go out at once.</param>
/// <param name="CommitsOnPause">The provider finalizes only on the app's commits (<c>Finalize</c> at speech pauses): OpenAI.</param>
internal sealed record RealtimePolicy(
    bool AnnouncesStart, double? MaxSessionSeconds, RealtimeHandover Handover, double Rate, double BurstSeconds,
    bool CommitsOnPause = false)
{
    /// <summary>How much audio from before a session's start it may begin with (the connect time).</summary>
    public double HoldSeconds => Handover == RealtimeHandover.Replay ? 10 : 5;

    /// <summary>null for a preset without a realtime adapter.</summary>
    public static RealtimePolicy? Of(string? preset) => preset switch
    {
        "elevenlabs" => new(true, null, RealtimeHandover.Parallel, 1, 5),
        "deepgram" => new(false, null, RealtimeHandover.Replay, 1.2, 2),
        "assemblyai" => new(true, 3 * 3_600, RealtimeHandover.Replay, 1.2, 2),
        "openai" => new(true, null, RealtimeHandover.Parallel, 1, 5, CommitsOnPause: true),
        "xai" => new(true, null, RealtimeHandover.Parallel, 1, 5),
        "speechmatics" => new(true, 48 * 3_600, RealtimeHandover.Replay, 2, 10),
        "soniox" => new(false, 300 * 60, RealtimeHandover.Parallel, 1, 5),
        "gladia" => new(false, 3 * 3_600, RealtimeHandover.None, 1, 5),
        _ => null,
    };
}

/// <summary>
/// One channel's realtime sessions in file time (Swift <c>RealtimeCoverage</c>): the audio each covered, its finals, and
/// how its speaker labels join the sessions before it. A session covers from its first frame to its last final, or to
/// the end of the audio it sent once the provider finished everything (a clean drain); a disconnect, the audio after the
/// last final of an unfinished drain, and audio never sent are gaps. Silent audio is covered. Pure, for tests (a class
/// here, not a value type: the runner owns one per channel and mutates it in place).
/// </summary>
internal sealed class RealtimeCoverage
{
    internal sealed class Session
    {
        /// <summary>File seconds of the session's first frame: its stream time 0.</summary>
        public double Offset { get; init; }
        /// <summary>File seconds where the audio sent ends.</summary>
        public double Sent { get; set; }
        /// <summary>File seconds where the last final ends (<see cref="Offset"/> before any).</summary>
        public double LastFinal { get; set; }
        /// <summary>null while open; true when the provider finished everything sent.</summary>
        public bool? Drained { get; set; }
        /// <summary>
        /// Opened to share audio with the session before (planned rotation, resume with replay), so its labels are
        /// reconciled with that session's; else its labels are scoped <c>"r&lt;N&gt;:&lt;label&gt;"</c>.
        /// </summary>
        public bool Bridged { get; init; }
        /// <summary>Finals in file time with the provider's labels.</summary>
        public List<CloudSegment> Segments { get; } = [];
        /// <summary>The label mapping for the live updates, once the session before closed (<see cref="FreezeLabels"/>).</summary>
        public Dictionary<string, string>? Labels { get; set; }
    }

    /// <summary>Gaps shorter than this are dropped (frame remainders, rounding).</summary>
    public const double MinimumGapSeconds = 0.1;

    private readonly List<Session> _sessions = [];
    private readonly List<Span> _silences = [];

    public IReadOnlyList<Session> Sessions => _sessions;
    public IReadOnlyList<Span> Silences => _silences;

    /// <summary>A new session whose first frame is at <paramref name="offset"/> file seconds; returns its index (N in <c>"r&lt;N&gt;:"</c>).</summary>
    public int Open(double offset, bool bridged)
    {
        _sessions.Add(new Session { Offset = offset, Sent = offset, LastFinal = offset, Bridged = bridged });
        return _sessions.Count - 1;
    }

    public void Sent(int index, double end) => _sessions[index].Sent = Math.Max(_sessions[index].Sent, end);

    /// <summary>A final in stream seconds; returns it in file seconds. The watermark never passes the audio sent.</summary>
    public CloudSegment Final(int index, CloudSegment segment)
    {
        var session = _sessions[index];
        var file = segment.Offset(session.Offset);
        session.Segments.Add(file);
        session.LastFinal = Math.Max(session.LastFinal, Math.Min(file.End, session.Sent));
        return file;
    }

    public void Close(int index, bool drained) => _sessions[index].Drained ??= drained;

    /// <summary>Audio below <see cref="MonoAudio.SilencePeak"/>: covered whether or not a session got it.</summary>
    public void Silence(Span span)
    {
        if (_silences.Count > 0 && _silences[^1] is var last && last.End >= span.Start)
            _silences[^1] = last with { End = Math.Max(last.End, span.End) };
        else
            _silences.Add(span);
    }

    /// <summary>The spans of <c>0...duration</c> no session covered, merged, each at least <see cref="MinimumGapSeconds"/> long.</summary>
    public List<Span> Gaps(double duration)
    {
        var covered = _sessions
            .Select(session => new Span(session.Offset, session.Drained == true ? session.Sent : Math.Min(session.LastFinal, session.Sent)))
            .Concat(_silences);
        var gaps = new List<Span>();
        var cursor = 0.0;
        foreach (var span in covered.OrderBy(span => span.Start))
        {
            if (!(span.End > span.Start)) continue;
            if (span.Start > cursor) gaps.Add(new Span(cursor, Math.Min(span.Start, duration)));
            cursor = Math.Max(cursor, span.End);
        }
        if (cursor < duration) gaps.Add(new Span(cursor, duration));
        return gaps.Where(gap => gap.End - gap.Start >= MinimumGapSeconds).ToList();
    }

    /// <summary>Where the session before <paramref name="index"/> stopped: <paramref name="index"/>'s finals before it repeat that session's.</summary>
    public double Cut(int index) => index > 0 ? _sessions[index - 1].Sent : double.NegativeInfinity;

    /// <summary>
    /// The audio a bridged session shares with the session before, as <see cref="SpeakerLabelReconciler.Append"/> takes
    /// it; null when it is not bridged or shares less than <see cref="SpeakerLabelReconciler.MinimumSharedSeconds"/>
    /// (then its labels are scoped).
    /// </summary>
    public TimeRange? Overlap(int index)
    {
        if (index <= 0 || !_sessions[index].Bridged) return null;
        var previous = _sessions[index - 1];
        var cut = previous.Sent;
        var lower = Math.Max(Math.Max(_sessions[index].Offset, previous.Offset), cut - SpeakerLabelReconciler.LabelOverlapSeconds);
        if (cut - lower < SpeakerLabelReconciler.MinimumSharedSeconds) return null;
        return new TimeRange(lower, cut);
    }

    /// <summary>
    /// The finals of every session in file order: a bridged session's labels reconciled with the sessions before
    /// (<see cref="SpeakerLabelReconciler"/>, which also drops the repeated finals before the cut), any other session's
    /// labels scoped <c>"r&lt;N&gt;:&lt;label&gt;"</c> so they never merge with an earlier session's identical strings.
    /// </summary>
    public List<CloudSegment> Segments() => Segments(_sessions.Count);

    private List<CloudSegment> Segments(int end)
    {
        var joined = new List<CloudSegment>();
        for (var index = 0; index < end; index++)
        {
            var next = _sessions[index].Segments.OrderBy(segment => segment.Start).ToList();
            if (index == 0)
                joined = next;
            else if (Overlap(index) is { } overlap)
                joined = SpeakerLabelReconciler.Append(next, joined, overlap, index);
            else
                joined = joined.Concat(next.Select(segment => Scoped(segment, index))).OrderBy(segment => segment.Start).ToList();
        }
        return joined;
    }

    /// <summary>Fixes a bridged session's label mapping from the finals so far, once the session before closed.</summary>
    public void FreezeLabels(int index)
    {
        if (Overlap(index) is not { } overlap) return;
        _sessions[index].Labels = SpeakerLabelReconciler.Labels(_sessions[index].Segments, Segments(index), overlap, index);
    }

    /// <summary>
    /// A final's label for the live updates: as <see cref="Segments()"/> will join it, except that a bridged session's
    /// label first heard after <see cref="FreezeLabels"/> is scoped <c>"p&lt;N&gt;:"</c> without the reconciler's renumbering.
    /// </summary>
    public string? LiveLabel(int index, string? label)
    {
        if (string.IsNullOrEmpty(label) || index <= 0) return label;
        if (Overlap(index) is null) return $"r{Invariant(index)}:{label}";
        return _sessions[index].Labels?.GetValueOrDefault(label) ?? $"p{Invariant(index)}:{label}";
    }

    private static CloudSegment Scoped(CloudSegment segment, int index) =>
        string.IsNullOrEmpty(segment.Speaker) ? segment : segment with { Speaker = $"r{Invariant(index)}:{segment.Speaker}" };

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Streams a recording's two channels ("Me" mic, "Others" system) to the default cloud provider's realtime API while it
/// is made, one socket and one adapter per channel, and hands back the finals and the spans not covered (gaps) at Stop
/// (Swift <c>RealtimeLiveStream</c>, same behavior).
/// </summary>
/// <remarks>
/// <para>
/// Audio: the writer's 16 kHz Float blocks (<see cref="Append"/>, file time = the running sample count, contiguous) go to
/// a per-channel ring; each session's sender takes them in 100 ms frames, resampled to the adapter's rate, at most
/// <see cref="RealtimePolicy.Rate"/> times real time above <see cref="RealtimePolicy.BurstSeconds"/>. A block never waits
/// for a socket: while no session runs it is only kept in the ring (60 s), so a disconnected span becomes a gap, never a
/// queue. A session starts with the audio since its connect attempt began (at most <see cref="RealtimePolicy.HoldSeconds"/>).
/// </para>
/// <para>
/// Sessions: connect (the init call first when the adapter has one), wait for <see cref="RealtimeEvent.Started"/> where
/// the provider sends one (else it counts as started on connect), then stream. A failure before the first start, a
/// successful send or a message is a connect failure. Reconnects back off 1, 2, 4 ... 30 s; auth, payment, plan-limit
/// (concurrency refusal) and configuration failures, and the same connect failure 5 times in a row, stop the channel
/// (<see cref="State.Failed"/>). Pause closes the sockets, the next block opens new ones; a language marker change
/// switches to a session in the new language. Before the provider's session cap a planned rotation keeps speaker labels:
/// a parallel overlap of <c>LabelOverlapSeconds</c>, a replay of them where the provider takes faster audio, or no
/// overlap where the provider allows neither (scoped labels). Keep-alives (<see cref="IRealtimeAdapter.KeepAlive"/>) go
/// out while no audio flows; the socket itself pings every 30 s (<see cref="RealtimeConnection.PingInterval"/>).
/// </para>
/// <para>
/// Stop (<see cref="FinishAsync"/>): each socket gets the rest of its audio, <c>Finalize</c> and <c>CloseMessages</c>,
/// then up to 5 s to drain (<see cref="IRealtimeAdapter.IsDrained"/>, <see cref="RealtimeEvent.Ended"/> or the socket
/// closing); what did not drain is a gap.
/// </para>
/// <para>
/// Threading (the Swift actor): every state change runs on one event loop, a single-reader queue of work items started by
/// <see cref="Start"/>. The socket tasks never touch the state: they post to the loop and check their own session token,
/// so an abort they did not cause (a socket aborted by another operation or by <c>Dispose</c>) is a "network" failure
/// and one the runner caused is ignored. <see cref="Append"/> and <see cref="Pause"/> only queue and never block.
/// <see cref="Start"/>, <see cref="FinishAsync"/> and <see cref="Cancel"/> are for one owner.
/// </para>
/// <para>
/// Errors never leave as text: states carry a short kind (<see cref="CloudRealtime.ErrorKind"/>, or a fixed one), so a
/// Gladia session-token URL, a body or the key never reach a log or the UI.
/// </para>
/// </remarks>
public sealed class RealtimeLiveStream
{
    /// <summary>A channel's realtime state.</summary>
    public abstract record State
    {
        private State()
        {
        }

        public sealed record Connecting : State;

        public sealed record Live : State;

        public sealed record Reconnecting : State;

        /// <summary>
        /// Stopped for this recording: "auth", "payment", "plan_limit" (a concurrency refusal), "config" (no key,
        /// unsupported language, e.g. Speechmatics without one), "http_&lt;code&gt;", or a provider's error kind.
        /// </summary>
        public sealed record Failed(string Kind) : State;
    }

    /// <summary>What the live view shows; <c>Channel</c> = <see cref="RecordingSession.Channel"/>.</summary>
    public abstract record Update
    {
        private Update()
        {
        }

        public sealed record StateChanged(int Channel, State State) : Update;

        /// <summary>
        /// The channel's text so far, replacing its previous partial; null clears it. <c>Start</c> in file seconds when
        /// the provider times its partials.
        /// </summary>
        public sealed record Partial(int Channel, string? Text, double? Start) : Update;

        /// <summary>
        /// A final in file seconds, its speaker label mapped or scoped as far as known (<c>RealtimeCoverage.LiveLabel</c>);
        /// <see cref="Result"/> holds the reconciled labels.
        /// </summary>
        public sealed record Final(int Channel, RawSegment Segment) : Update;
    }

    /// <param name="MicGaps">File spans no session covered, to transcribe after Stop.</param>
    /// <param name="SystemBridges">
    /// File seconds where the system channel resumed after a pause in a session whose labels are scoped (no replay shared
    /// with the session before); empty when the system finals carry no labels. The pipeline uploads the audio around each
    /// to map the scoped labels back.
    /// </param>
    /// <param name="ModelId">The realtime model used.</param>
    /// <param name="Duration">Seconds of audio appended.</param>
    public sealed record Result(
        IReadOnlyList<RawSegment> Mic, IReadOnlyList<RawSegment> System, IReadOnlyList<Span> MicGaps,
        IReadOnlyList<Span> SystemGaps, IReadOnlyList<double> SystemBridges, string ModelId, double Duration);

    /// <summary>A failure with a fixed kind that stops the channel (the adapter factory's).</summary>
    internal sealed class Failure(string kind) : Exception("The realtime session cannot start.")
    {
        public string Kind { get; } = kind;
    }

    internal const int CaptureRate = SpeechChunker.SampleRate;
    /// <summary>100 ms; a remainder at a close goes out from 20 ms.</summary>
    internal const int FrameSamples = CaptureRate / 10;
    internal const int MinimumFrameSamples = CaptureRate / 50;
    internal const int FramesPerStep = 10;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    /// <summary>How long before the session cap the rotation starts, beyond <c>LabelOverlapSeconds</c>.</summary>
    internal const double RotationMarginSeconds = 60;
    internal const double MaxBackoffSeconds = 30;
    internal const int MaxRepeatedFailures = 5;
    /// <summary>A session lived this long before failing: its reconnect starts the backoff over.</summary>
    internal const double HealthySessionSeconds = 30;
    internal const double RingSeconds = 60;
    /// <summary>Audio a session may lag behind beyond what it started with before it counts as stalled (a disconnect).</summary>
    internal const double StallSeconds = 15;
    internal const int OverlapSamples = (int)SpeakerLabelReconciler.LabelOverlapSeconds * CaptureRate;

    private const double Rate = CaptureRate;

    private readonly Channel<Action> _work = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Update> _updates = Channel.CreateUnbounded<Update>();
    private readonly TaskCompletionSource<Result> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RealtimePolicy _policy;
    private readonly HttpMessageHandler? _handler;
    private readonly HttpMessageInvoker? _invoker;
    private readonly Func<int, string?, CancellationToken, Task<IRealtimeAdapter>> _makeAdapter;
    private readonly ChannelState[] _channels = [new(), new()];
    private readonly Dictionary<int, Session> _sessions = [];
    private int _nextSessionId;
    private Task? _loop;
    private bool _finishing;
    private bool _finishRequested;
    private volatile bool _cancelled;

    /// <summary>
    /// <paramref name="makeAdapter"/> builds a fresh adapter for each socket (channel, language marker); it runs in the
    /// session's task, so the key is read there, never in the caller's update. A <see cref="Failure"/> it throws stops
    /// the channel, any other exception counts as "config". <paramref name="handler"/> (the init call) and
    /// <paramref name="invoker"/> (the handshake) are test seams; null for the shared transports.
    /// </summary>
    internal RealtimeLiveStream(
        string modelId, RealtimePolicy policy, Func<int, string?, CancellationToken, Task<IRealtimeAdapter>> makeAdapter,
        HttpMessageHandler? handler = null, HttpMessageInvoker? invoker = null)
    {
        ModelId = modelId;
        _policy = policy;
        _makeAdapter = makeAdapter;
        _handler = handler;
        _invoker = invoker;
    }

    /// <summary>The realtime model used.</summary>
    public string ModelId { get; }

    /// <summary>The live updates, in order; completed once <see cref="FinishAsync"/> has its result or after <see cref="Cancel"/>.</summary>
    public ChannelReader<Update> Updates => _updates.Reader;

    /// <summary>
    /// The runner for <paramref name="config"/>'s realtime model; null when the provider has no realtime API.
    /// <paramref name="apiKey"/> is read in each session's task (an empty key stops both channels with "config").
    /// Streaming speaker labels are asked for on the system channel only, and only with <paramref name="diarize"/>.
    /// <paramref name="handler"/> and <paramref name="invoker"/>: test seams of <see cref="RealtimeConnection"/>.
    /// </summary>
    public static RealtimeLiveStream? Make(
        ProviderConfig config, bool diarize, Func<CancellationToken, Task<string>> apiKey,
        HttpMessageHandler? handler = null, HttpMessageInvoker? invoker = null)
    {
        if (!CloudRealtime.Supports(config) || RealtimePolicy.Of(config.Preset) is not { } policy) return null;
        // A copy: the settings may change while the recording runs (Swift captures the value).
        var snapshot = config with { };
        return new RealtimeLiveStream(CloudRealtime.Model(snapshot), policy, async (channel, language, cancellationToken) =>
        {
            var key = await apiKey(cancellationToken).ConfigureAwait(false);
            if (key.Length == 0
                || CloudRealtime.Make(snapshot, key, language, diarize && channel == RecordingSession.Channel.System) is not { } adapter)
                throw new Failure("config");
            return adapter;
        }, handler, invoker);
    }

    /// <summary>Starts taking blocks; the sockets open at the first block (its language marker).</summary>
    public void Start()
    {
        if (_loop is not null || _finishing || _cancelled) return;
        _loop = Task.Run(RunLoopAsync);
    }

    /// <summary>
    /// One block of both channels (16 kHz, <paramref name="startSample"/> its file position) with its language marker
    /// ("auto", a BCP-47 code, or null). Copies the samples and never blocks; call from the writer thread.
    /// </summary>
    public void Append(ReadOnlySpan<float> mic, ReadOnlySpan<float> system, long startSample, string? language)
    {
        var micSamples = mic.ToArray();
        var systemSamples = system.ToArray();
        Post(() =>
        {
            Feed(RecordingSession.Channel.Mic, micSamples, startSample, language);
            Feed(RecordingSession.Channel.System, systemSamples, startSample, language);
        });
    }

    /// <summary>The recording paused: closes the sockets (their finals come); the next block opens new ones.</summary>
    public void Pause() =>
        Post(() =>
        {
            for (var channel = 0; channel < _channels.Length; channel++) Pause(channel);
        });

    /// <summary>
    /// At Stop, after the last block: drains each socket (at most <see cref="DrainTimeout"/>) and returns the finals and
    /// gaps. Later calls return the same result.
    /// </summary>
    public async Task<Result> FinishAsync()
    {
        if (_loop is { } loop)
        {
            var queued = Post(() =>
            {
                _finishRequested = true;
                BeginFinish();
                ResolveIfDrained();
            });
            if (queued) return await _finished.Task.ConfigureAwait(false);
            // Finished or cancelled: the loop ends after its queued work, then the state is the caller's to read.
            await loop.ConfigureAwait(false);
        }
        else
        {
            // Never started (Swift: no feeder): no block was taken, no session opened.
            _finishing = true;
            _work.Writer.TryComplete();
        }
        _finished.TrySetResult(BuildResult());
        _updates.Writer.TryComplete();
        return await _finished.Task.ConfigureAwait(false);
    }

    /// <summary>Stops at once, keeping nothing: every socket is aborted, a waiting <see cref="FinishAsync"/> returns.</summary>
    public void Cancel()
    {
        _cancelled = true;
        if (_loop is null)
        {
            _finishing = true;
            _work.Writer.TryComplete();
            _updates.Writer.TryComplete();
            return;
        }
        Post(() =>
        {
            _finishing = true;
            foreach (var session in _sessions.Values) Abort(session);
            _sessions.Clear();
            ResolveIfDrained();
            _updates.Writer.TryComplete();
        });
        _work.Writer.TryComplete();
    }

    // Event loop.

    private async Task RunLoopAsync()
    {
        await foreach (var action in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                action();
            }
            catch (Exception)
            {
                // A bug in one work item must not stop the loop: FinishAsync would never return.
            }
        }
    }

    /// <summary>Queues <paramref name="action"/> for the loop; false once the loop takes no more work.</summary>
    private bool Post(Action action) => _work.Writer.TryWrite(action);

    /// <summary>Runs <paramref name="query"/> on the loop; null once the loop takes no more work.</summary>
    private Task<T?> Ask<T>(Func<T?> query) where T : class
    {
        var answer = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = Post(() =>
        {
            try
            {
                answer.TrySetResult(query());
            }
            catch (Exception error)
            {
                answer.TrySetException(error);
            }
        });
        if (!queued) answer.TrySetResult(null);
        return answer.Task;
    }

    // Feed.

    private void Feed(int channel, float[] samples, long startSample, string? language)
    {
        if (_finishing || _cancelled || samples.Length == 0) return;
        var state = _channels[channel];
        if (!state.Started)
        {
            state.Started = true;
            state.RingStart = startSample;
            state.Position = startSample;
        }
        var start = state.Position;
        state.Ring.AddRange(samples);
        state.Position += samples.Length;
        if (samples.All(sample => Math.Abs(sample) < MonoAudio.SilencePeak))
            state.Coverage.Silence(new Span(start / Rate, state.Position / Rate));
        state.Paused = false;
        var marker = Marker(language);
        if (state.HasMarker && !string.Equals(marker, state.Language, StringComparison.OrdinalIgnoreCase))
            SwitchLanguage(channel, start);
        state.Language = marker;
        state.HasMarker = true;
        var stalled = _sessions.Values
            .Where(session => session.Channel == channel && session.Phase == Phase.Live
                              && (state.Position - session.NextSample) / Rate > session.BacklogLimit)
            .Select(session => session.Id).ToList();
        foreach (var id in stalled) Fail(id, "stalled", permanent: false);
        TrimRing(channel);
        Ensure(channel);
    }

    /// <summary>Closes the channel's sessions at <paramref name="sample"/>; the next one starts there in the new language.</summary>
    private void SwitchLanguage(int channel, long sample)
    {
        var state = _channels[channel];
        state.Plan = new Plan { From = sample, Waits = true };
        if (state.Failure == "config")
        {
            state.Failure = null;
            state.Failures = 0;
            state.State = null;
        }
        EndSessions(channel, sample);
    }

    private void Pause(int channel)
    {
        var state = _channels[channel];
        if (!state.Started || state.Paused) return;
        var point = state.Position;
        state.Paused = true;
        state.LiveSinceResume = false;
        var replay = _policy.Handover == RealtimeHandover.Replay;
        state.Plan = new Plan
        {
            Bridged = replay, ReplayFrom = replay ? point - OverlapSamples : null, From = point, Waits = true, Resumes = true,
        };
        EndSessions(channel, point);
    }

    private void BeginFinish()
    {
        if (_finishing) return;
        _finishing = true;
        for (var channel = 0; channel < _channels.Length; channel++) EndSessions(channel, _channels[channel].Position);
    }

    /// <summary>Closes the channel's streaming sessions at <paramref name="sample"/> and drops those not streaming yet.</summary>
    private void EndSessions(int channel, long sample)
    {
        foreach (var session in _sessions.Values.Where(session => session.Channel == channel).ToList())
        {
            switch (session.Phase)
            {
                case Phase.Live:
                    Close(session.Id, sample);
                    break;
                case Phase.Connecting or Phase.WaitingStart:
                    Complete(session.Id, drained: false);
                    break;
            }
        }
    }

    /// <summary>Keeps the last <see cref="RingSeconds"/>, and what a session or a planned replay still needs.</summary>
    private void TrimRing(int channel)
    {
        var state = _channels[channel];
        var keep = state.Position - (long)(RingSeconds * Rate);
        foreach (var session in _sessions.Values)
            if (session.Channel == channel && session.Record is not null) keep = Math.Min(keep, session.NextSample);
        if (state.Plan?.ReplayFrom is { } replay) keep = Math.Min(keep, replay);
        var drop = keep - state.RingStart;
        if (drop <= CaptureRate * 10) return;
        state.Ring.RemoveRange(0, (int)drop);
        state.RingStart = keep;
    }

    // Sessions.

    /// <summary>
    /// Opens the channel's next session when it should stream and none is streaming or opening (after a failure, with
    /// backoff); a planned switch that <see cref="Plan.Waits"/> opens once the closing sessions drained.
    /// </summary>
    private void Ensure(int channel)
    {
        var state = _channels[channel];
        if (_finishing || !state.HasMarker || state.Paused || state.Failure is not null) return;
        var mine = _sessions.Values.Where(session => session.Channel == channel).ToList();
        if (mine.Any(session => session.Phase != Phase.Closing)) return;
        var plan = state.Plan ?? new Plan();
        if (plan.Waits && mine.Any(session => session.Phase == Phase.Closing)) return;
        state.Plan = null;
        var planned = plan.From is not null || plan.ReplayFrom is not null;
        // The first session takes the recording from its first block (at most HoldSeconds back).
        if (plan.From is null && state.Coverage.Sessions.Count == 0) plan = plan with { From = state.RingStart };
        var delay = planned || state.Failures == 0 ? 0 : Math.Min(MaxBackoffSeconds, Math.Pow(2, state.Failures - 1));
        SetState(channel, state.LiveSinceResume ? new State.Reconnecting() : new State.Connecting());
        Open(channel, plan, delay);
    }

    private void Open(int channel, Plan plan, double delay)
    {
        var id = _nextSessionId++;
        var session = new Session(id, channel, plan);
        _sessions[id] = session;
        var language = _channels[channel].Language;
        var token = session.Cancel.Token;
        _ = Task.Run(() => ConnectAsync(id, channel, language, delay, token));
    }

    private async Task ConnectAsync(int id, int channel, string? language, double delay, CancellationToken token)
    {
        try
        {
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
            Post(() => AttemptStarted(id));
            IRealtimeAdapter adapter;
            try
            {
                adapter = await _makeAdapter(channel, language, token).ConfigureAwait(false);
            }
            catch (Failure)
            {
                throw;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                throw new Failure("config");
            }
            var url = await RealtimeConnection.SocketUrlAsync(adapter, _handler, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var connection = await RealtimeConnection.ConnectAsync(adapter, url, _invoker, token).ConfigureAwait(false);
            if (!Post(() => Connected(id, adapter, connection))) connection.Dispose();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The runner ended the session.
        }
        catch (Exception error)
        {
            Post(() => Failed(id, error));
        }
    }

    private void AttemptStarted(int id)
    {
        if (_sessions.TryGetValue(id, out var session)) session.AttemptStart = _channels[session.Channel].Position;
    }

    private void Connected(int id, IRealtimeAdapter adapter, RealtimeConnection connection)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Phase != Phase.Connecting)
        {
            connection.Dispose();
            return;
        }
        session.Adapter = adapter;
        session.Connection = connection;
        session.Phase = Phase.WaitingStart;
        var token = session.Cancel.Token;
        _ = Task.Run(() => ReceiveLoopAsync(id, connection, token));
        _ = Task.Run(() => SendLoopAsync(id, connection, token));
        if (_policy.AnnouncesStart)
            After(StartTimeout, token, () => StartTimedOut(id));
        else
            Start(id, expiresAt: null);
    }

    /// <summary>
    /// The session takes audio: from its replay start, else from its planned start or its attempt's start, at most
    /// <see cref="RealtimePolicy.HoldSeconds"/> back.
    /// </summary>
    private void Start(int id, DateTimeOffset? expiresAt)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Phase != Phase.WaitingStart
            || session.Adapter is not { } adapter) return;
        var channel = session.Channel;
        var state = _channels[channel];
        var position = state.Position;
        var from = session.Plan.ReplayFrom
                   ?? Math.Max(session.Plan.From ?? session.AttemptStart, position - (long)(_policy.HoldSeconds * Rate));
        from = Math.Min(Math.Max(from, state.RingStart), position);
        var now = Now();
        session.Phase = Phase.Live;
        session.OffsetSample = from;
        session.NextSample = from;
        var record = state.Coverage.Open(from / Rate, session.Plan.Bridged);
        session.Record = record;
        if (session.Plan.Resumes && record > 0 && state.Coverage.Overlap(record) is null)
            state.Resumes.Add((session.Plan.From ?? from) / Rate);
        session.PaceStart = now;
        session.LastSend = now;
        session.LiveSince = now;
        session.BacklogLimit = (position - from) / Rate + StallSeconds;
        if (adapter.SampleRate != CaptureRate) session.Resampler = new RealtimeResampler(CaptureRate, adapter.SampleRate);
        if (_policy.CommitsOnPause)
            session.Chunker = new SpeechChunker(
                RecordingTranscriber.LiveMaxChunkSeconds, RecordingTranscriber.LiveShortPauseMinSeconds,
                RecordingTranscriber.LiveShortPauseFrames);
        var limits = new[] { expiresAt is { } end ? (end - DateTimeOffset.UtcNow).TotalSeconds : (double?)null, _policy.MaxSessionSeconds }
            .OfType<double>().Where(limit => limit > 0).ToList();
        if (limits.Count > 0)
        {
            var limit = limits.Min();
            var lead = SpeakerLabelReconciler.LabelOverlapSeconds + RotationMarginSeconds;
            session.RotateAt = now + Math.Max(limit / 2, limit - lead);
        }
        if (_policy.AnnouncesStart) Confirm(session);
        state.LiveSinceResume = true;
        Promote(channel);
    }

    private void StartTimedOut(int id)
    {
        if (_sessions.TryGetValue(id, out var session) && session.Phase == Phase.WaitingStart)
            Fail(id, "start_timeout", permanent: false);
    }

    /// <summary>The connection works: connect failures start over.</summary>
    private void Confirm(Session session)
    {
        if (session.Confirmed) return;
        session.Confirmed = true;
        _channels[session.Channel].Failures = 0;
    }

    /// <summary>Closes a streaming session once it sent its audio through <paramref name="sample"/>; <see cref="DrainTimeout"/> bounds the drain.</summary>
    private void Close(int id, long sample)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Phase != Phase.Live) return;
        session.Phase = Phase.Closing;
        session.CloseAt = Math.Max(session.NextSample, Math.Min(sample, _channels[session.Channel].Position));
        foreach (var successor in _sessions.Values)
            if (successor.SuccessorOf == id) successor.SuccessorOf = null;
        After(DrainTimeout, session.Cancel.Token, () => Complete(id, drained: false));
    }

    /// <summary>Ends a session: its coverage, its tasks and socket; then the next session, the live display, Stop.</summary>
    private void Complete(int id, bool drained)
    {
        if (!_sessions.Remove(id, out var session)) return;
        Abort(session);
        var channel = session.Channel;
        if (session.Record is { } record)
        {
            _channels[channel].Coverage.Close(record, drained);
            if (_channels[channel].Display == record) Emit(new Update.Partial(channel, null, null));
        }
        foreach (var successor in _sessions.Values)
            if (successor.SuccessorOf == id) successor.SuccessorOf = null;
        Promote(channel);
        Ensure(channel);
        ResolveIfDrained();
    }

    /// <summary>Cancels the session's tasks (their own token first, so they know the abort is the runner's), then aborts its socket.</summary>
    private static void Abort(Session session)
    {
        session.Cancel.Cancel();
        session.Connection?.Dispose();
    }

    private void ResolveIfDrained()
    {
        if (!_finishRequested || _sessions.Count > 0) return;
        _finished.TrySetResult(BuildResult());
        _updates.Writer.TryComplete();
        _work.Writer.TryComplete();
    }

    /// <summary>
    /// The newest streaming session becomes the one shown live once every older one closed: its label mapping is fixed
    /// and its finals past the cut that came meanwhile are shown.
    /// </summary>
    private void Promote(int channel)
    {
        var state = _channels[channel];
        var records = _sessions.Values
            .Where(session => session.Channel == channel && session.Record is not null)
            .Select(session => session.Record!.Value).ToList();
        if (records.Count == 0) return;
        var newest = records.Max();
        if (newest == state.Display || records.Any(record => record < newest)) return;
        state.Display = newest;
        state.Coverage.FreezeLabels(newest);
        var coverage = state.Coverage;
        foreach (var segment in coverage.Sessions[newest].Segments.Where(segment => segment.End > coverage.Cut(newest)).ToList())
            Emit(new Update.Final(channel, Raw(segment, coverage.LiveLabel(newest, segment.Speaker))));
        SetState(channel, new State.Live());
    }

    // Sending.

    private async Task SendLoopAsync(int id, RealtimeConnection connection, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            List<RealtimeMessage>? messages;
            try
            {
                messages = await Ask(() => StepFor(id)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Post(() => Failed(id, error));
                return;
            }
            if (messages is null) return;
            try
            {
                foreach (var message in messages) await connection.SendAsync(message, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                Post(() => Failed(id, error));
                return;
            }
            if (messages.Count == 0)
            {
                try
                {
                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            else
            {
                Post(() => Sent(id));
            }
        }
    }

    /// <summary>
    /// What the session's sender sends next: audio frames due (paced), then at a close <c>Finalize</c> and
    /// <c>CloseMessages</c>; a keep-alive while no audio flows. null once the session is gone.
    /// </summary>
    private List<RealtimeMessage>? StepFor(int id)
    {
        if (!_sessions.TryGetValue(id, out var current)) return null;
        var now = Now();
        var wasLive = current.Phase == Phase.Live;
        var old = current.SuccessorOf;
        if (wasLive && !current.Rotating && current.RotateAt is { } rotateAt && now >= rotateAt) Rotate(id);
        if (wasLive && old is { } previous
            && (current.NextSample - current.OffsetSample) / Rate >= SpeakerLabelReconciler.LabelOverlapSeconds)
            Close(previous, _channels[current.Channel].Position);
        if (!_sessions.TryGetValue(id, out var session)) return null;
        if (session.Adapter is not { } adapter) return [];
        if (session.CloseDelivered && adapter.IsDrained)
        {
            Complete(id, drained: true);
            return null;
        }
        var messages = new List<RealtimeMessage>();
        if (session.Phase is Phase.Live or Phase.Closing && !session.CloseSent)
        {
            var end = session.Phase == Phase.Closing ? session.CloseAt : _channels[session.Channel].Position;
            messages = Frames(session, adapter, end, now);
            if (session.Phase == Phase.Closing && end - session.NextSample < MinimumFrameSamples)
            {
                var streamed = (session.NextSample - session.OffsetSample) / Rate;
                messages.AddRange(adapter.Finalize(streamed));
                messages.AddRange(adapter.CloseMessages());
                session.CloseSent = true;
                session.CloseDelivered = messages.Count == 0;
            }
        }
        if (messages.Count == 0 && !session.CloseSent && adapter.KeepAlive is { } keepAlive
            && now - session.LastSend >= keepAlive.Interval.TotalSeconds)
            messages = [keepAlive.Message];
        if (messages.Count > 0) session.LastSend = now;
        return messages;
    }

    /// <summary>
    /// Frames of the ring from the session's next sample up to <paramref name="end"/>, as many as the pace allows (at most
    /// <see cref="FramesPerStep"/>); with <see cref="RealtimePolicy.CommitsOnPause"/>, a commit after each speech chunk the
    /// pause detector closes.
    /// </summary>
    private List<RealtimeMessage> Frames(Session session, IRealtimeAdapter adapter, long end, double now)
    {
        var state = _channels[session.Channel];
        var elapsed = now - session.PaceStart;
        var messages = new List<RealtimeMessage>();
        for (var step = 0; step < FramesPerStep; step++)
        {
            var available = end - session.NextSample;
            var count = available >= FrameSamples ? FrameSamples
                : session.Phase == Phase.Closing && available >= MinimumFrameSamples ? (int)available : 0;
            var streamed = (session.NextSample - session.OffsetSample + count) / Rate;
            if (count <= 0 || streamed > _policy.BurstSeconds + _policy.Rate * elapsed) break;
            var lower = session.NextSample - state.RingStart;
            if (lower < 0) break;
            var samples = CollectionsMarshal.AsSpan(state.Ring).Slice((int)lower, count);
            var pcm = session.Resampler is { } resampler ? resampler.Process(samples).AsSpan() : samples;
            messages.Add(adapter.Frame(CloudRealtime.Pcm16(pcm)));
            session.NextSample += count;
            if (session.Chunker is { } chunker && chunker.Append(samples, null).Count > 0)
                messages.AddRange(adapter.Finalize(streamed));
        }
        if (session.Record is { } record) state.Coverage.Sent(record, session.NextSample / Rate);
        return messages;
    }

    /// <summary>A send went through: the connection works (and the close messages are out once sent).</summary>
    private void Sent(int id)
    {
        if (!_sessions.TryGetValue(id, out var session)) return;
        Confirm(session);
        if (session.CloseSent) session.CloseDelivered = true;
    }

    /// <summary>
    /// Before the session cap: a parallel successor (the old session closes after the overlap), else the old one closes
    /// now and the next one starts where it stopped (replaying its last <c>LabelOverlapSeconds</c> where allowed).
    /// </summary>
    private void Rotate(int id)
    {
        if (!_sessions.TryGetValue(id, out var session)) return;
        var channel = session.Channel;
        var position = _channels[channel].Position;
        session.Rotating = true;
        switch (_policy.Handover)
        {
            case RealtimeHandover.Parallel:
                Open(channel, new Plan { Bridged = true, SuccessorOf = id }, delay: 0);
                break;
            case RealtimeHandover.Replay:
                _channels[channel].Plan = new Plan
                {
                    Bridged = true, ReplayFrom = position - OverlapSamples, From = position, Waits = true,
                };
                Close(id, position);
                break;
            case RealtimeHandover.None:
                _channels[channel].Plan = new Plan { From = position, Waits = true };
                Close(id, position);
                break;
        }
    }

    // Receiving.

    private async Task ReceiveLoopAsync(int id, RealtimeConnection connection, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var message = await connection.ReceiveAsync(token).ConfigureAwait(false);
                if (!Post(() => Received(id, message))) return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The runner ended the session.
        }
        catch (Exception error)
        {
            var status = connection.CloseStatus;
            Post(() => ReceiveEnded(id, error, status));
        }
    }

    private void Received(int id, RealtimeMessage message)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Adapter is not { } adapter) return;
        IReadOnlyList<RealtimeEvent> events;
        try
        {
            events = adapter.Parse(message);
        }
        catch (Exception)
        {
            // An undecodable message is skipped, not fatal.
            events = [];
        }
        if (session.Phase != Phase.WaitingStart) Confirm(session);
        foreach (var realtimeEvent in events)
        {
            if (!_sessions.ContainsKey(id)) return;
            switch (realtimeEvent)
            {
                case RealtimeEvent.Started started:
                    Start(id, started.ExpiresAt);
                    break;
                case RealtimeEvent.Partial partial:
                    Partial(id, partial.Text, partial.Start, partial.End);
                    break;
                case RealtimeEvent.Final final:
                    Final(id, final.Segment);
                    break;
                case RealtimeEvent.Failed failed:
                    var (kind, permanent) = FailureOf(failed.Kind);
                    Fail(id, kind, permanent);
                    break;
                case RealtimeEvent.Ended:
                    if (session.Phase == Phase.Closing)
                        Complete(id, drained: true);
                    else
                        Fail(id, "ended", permanent: false, drained: true);
                    break;
            }
        }
        if (_sessions.TryGetValue(id, out var after) && after.CloseDelivered && after.Adapter?.IsDrained == true)
            Complete(id, drained: true);
    }

    private void Partial(int id, string text, double? start, double? end)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Record is not { } record
            || _channels[session.Channel].Display != record) return;
        var offset = session.OffsetSample / Rate;
        var cut = _channels[session.Channel].Coverage.Cut(record);
        // The replayed audio before the cut repeats the previous session's text.
        if (!((end is { } streamEnd ? streamEnd + offset : session.NextSample / Rate) > cut)) return;
        Emit(new Update.Partial(session.Channel, text, start + offset));
    }

    private void Final(int id, CloudSegment segment)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Record is not { } record) return;
        var channel = session.Channel;
        var coverage = _channels[channel].Coverage;
        var file = coverage.Final(record, segment);
        if (_channels[channel].Display != record || !(file.End > coverage.Cut(record))) return;
        Emit(new Update.Final(channel, Raw(file, coverage.LiveLabel(record, file.Speaker))));
    }

    private void ReceiveEnded(int id, Exception error, WebSocketCloseStatus? closeStatus)
    {
        if (!_sessions.TryGetValue(id, out var session)) return;
        if (session.Phase == Phase.Closing)
            Complete(id, drained: session.CloseDelivered && closeStatus == WebSocketCloseStatus.NormalClosure);
        else if (error is LLMException { Kind: LLMErrorKind.HttpStatus })
            Failed(id, error);
        else if (closeStatus is { } status)
            Fail(id, status == WebSocketCloseStatus.NormalClosure
                ? "closed" : "close_" + ((int)status).ToString(CultureInfo.InvariantCulture), permanent: false);
        else
            Failed(id, error);
    }

    // Failures.

    private void Failed(int id, Exception error)
    {
        if (!_sessions.TryGetValue(id, out var session)) return;
        // An abort the runner caused (Swift CancellationError); any other cancellation is a failed connection.
        if (error is OperationCanceledException && session.Cancel.IsCancellationRequested) return;
        var (kind, permanent) = FailureOf(error);
        Fail(id, kind, permanent);
    }

    /// <summary>
    /// Ends a failed session. A parallel successor's failure only delays the rotation; a permanent failure, or the same
    /// connect failure <see cref="MaxRepeatedFailures"/> times, stops the channel; else the channel reconnects with backoff.
    /// </summary>
    private void Fail(int id, string kind, bool permanent, bool drained = false)
    {
        if (!_sessions.TryGetValue(id, out var session)) return;
        var channel = session.Channel;
        var state = _channels[channel];
        if (session.Phase != Phase.Closing)
        {
            if (session.SuccessorOf is { } old && _sessions.TryGetValue(old, out var predecessor) && predecessor.Phase == Phase.Live)
            {
                predecessor.Rotating = false;
                predecessor.RotateAt = Now() + MaxBackoffSeconds;
            }
            else
            {
                var healthy = session.LiveSince is { } since && Now() - since >= HealthySessionSeconds;
                state.Failures = healthy ? 1 : state.Failures + 1;
                var repeated = !session.Confirmed && kind != "network" && state.Failures >= MaxRepeatedFailures;
                if (permanent || repeated)
                {
                    state.Failure = kind;
                    SetState(channel, new State.Failed(kind));
                }
            }
        }
        Complete(id, drained);
        // A stopped channel streams nothing: a parallel successor still open closes too.
        if (state.Failure is not null) EndSessions(channel, state.Position);
    }

    /// <summary>An error as a short kind, and whether retrying cannot help. Never the error's text or URL. Pure, for tests.</summary>
    internal static (string Kind, bool Permanent) FailureOf(Exception error) => error switch
    {
        Failure failure => (failure.Kind, true),
        LLMException { Kind: LLMErrorKind.HttpStatus } http => http.StatusCode switch
        {
            401 or 403 => ("auth", true),
            402 => ("payment", true),
            429 => ("plan_limit", true),
            var code => ("http_" + code.ToString(CultureInfo.InvariantCulture), code is >= 400 and < 500 && code != 408),
        },
        LLMException => ("config", true),
        // Transport failures, and aborts the runner did not cause (another operation's cancellation, the oversize
        // abort, a disposed socket): Swift URLError.
        WebSocketException or HttpRequestException or IOException or TimeoutException or OperationCanceledException
            or ObjectDisposedException => ("network", false),
        _ => ("error", false),
    };

    /// <summary>
    /// A provider's error kind (<see cref="RealtimeEvent.Failed"/>): a concurrency or quota refusal is "plan_limit" and a
    /// key refusal "auth" (both stop the channel); anything else reconnects. Pure, for tests.
    /// </summary>
    internal static (string Kind, bool Permanent) FailureOf(string kind)
    {
        var lowered = kind.ToLowerInvariant();
        if (new[] { "quota", "concurren", "too_many" }.Any(lowered.Contains) || lowered == "429") return ("plan_limit", true);
        if (new[] { "auth", "api_key", "forbidden" }.Any(lowered.Contains) || lowered is "401" or "403") return ("auth", true);
        return (CloudRealtime.ErrorKind(kind), false);
    }

    // Helpers.

    /// <summary>Seconds on a monotonic clock (Swift <c>ContinuousClock</c>).</summary>
    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Runs <paramref name="action"/> on the loop after <paramref name="delay"/> unless <paramref name="token"/> is cancelled first.</summary>
    private void After(TimeSpan delay, CancellationToken token, Action action) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Post(action);
        });

    private void SetState(int channel, State state)
    {
        var current = _channels[channel].State;
        if (current == state || current is State.Failed) return;
        _channels[channel].State = state;
        Emit(new Update.StateChanged(channel, state));
    }

    private void Emit(Update update) => _updates.Writer.TryWrite(update);

    /// <summary>The trimmed marker; null for an empty or "auto" one.</summary>
    private static string? Marker(string? language)
    {
        var trimmed = language?.Trim() ?? "";
        return trimmed.Length == 0 || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private Result BuildResult()
    {
        var mic = _channels[RecordingSession.Channel.Mic];
        var system = _channels[RecordingSession.Channel.System];
        return new Result(
            Raw(mic.Coverage.Segments()), Raw(system.Coverage.Segments()), mic.Gaps(), system.Gaps(), Bridges(system),
            ModelId, _channels.Max(channel => channel.Position) / Rate);
    }

    /// <summary>The channel's resumes with scoped labels, or none when its finals carry no labels.</summary>
    private static List<double> Bridges(ChannelState channel) =>
        channel.Coverage.Sessions.Any(session => session.Segments.Any(segment => !string.IsNullOrEmpty(segment.Speaker)))
            ? [.. channel.Resumes]
            : [];

    private static List<RawSegment> Raw(IEnumerable<CloudSegment> segments) =>
        segments.Select(segment => Raw(segment, segment.Speaker)).ToList();

    private static RawSegment Raw(CloudSegment segment, string? speaker) =>
        new(segment.Start, segment.End, segment.Text, segment.Language, speaker);

    // State types.

    /// <summary>How the next session of a channel starts.</summary>
    private sealed record Plan
    {
        /// <summary>Shares audio with the session before (labels reconciled).</summary>
        public bool Bridged { get; init; }
        /// <summary>First sample to send: the replay of the session before's last <c>LabelOverlapSeconds</c>.</summary>
        public long? ReplayFrom { get; init; }
        /// <summary>First sample to send (at most <c>HoldSeconds</c> back) when not replaying; null = the attempt's start.</summary>
        public long? From { get; init; }
        /// <summary>Opens only once the channel's closing sessions drained (no third socket).</summary>
        public bool Waits { get; init; }
        /// <summary>A parallel rotation's old session, closed after the overlap.</summary>
        public int? SuccessorOf { get; init; }
        /// <summary>The first session after a pause.</summary>
        public bool Resumes { get; init; }
    }

    private sealed class ChannelState
    {
        public RealtimeCoverage Coverage { get; } = new();
        /// <summary>Audio from <see cref="RingStart"/> (file samples) to <see cref="Position"/>.</summary>
        public List<float> Ring { get; } = [];
        public long RingStart { get; set; }
        public long Position { get; set; }
        public bool Started { get; set; }
        /// <summary>The current marker language (null = auto) once a block came.</summary>
        public string? Language { get; set; }
        public bool HasMarker { get; set; }
        public bool Paused { get; set; }
        public Plan? Plan { get; set; }
        /// <summary>The coverage index of the session shown live.</summary>
        public int? Display { get; set; }
        public State? State { get; set; }
        /// <summary>Connect failures in a row (backoff); a permanent failure's kind stops the channel.</summary>
        public int Failures { get; set; }
        public string? Failure { get; set; }
        /// <summary>A session went live since the start or the last pause: a new connect shows "reconnecting".</summary>
        public bool LiveSinceResume { get; set; }
        /// <summary>File seconds where a session after a pause started with scoped labels (<see cref="Result.SystemBridges"/>).</summary>
        public List<double> Resumes { get; } = [];

        public List<Span> Gaps() => Coverage.Gaps(Position / Rate);
    }

    private enum Phase
    {
        Connecting,
        WaitingStart,
        Live,
        Closing,
    }

    private sealed class Session(int id, int channel, Plan plan)
    {
        public int Id { get; } = id;
        public int Channel { get; } = channel;
        public Plan Plan { get; } = plan;
        public Phase Phase { get; set; } = Phase.Connecting;
        /// <summary>Cancels every task of the session (Swift <c>tasks</c>); never disposed, so its token stays readable.</summary>
        public CancellationTokenSource Cancel { get; } = new();
        public long AttemptStart { get; set; }
        public IRealtimeAdapter? Adapter { get; set; }
        public RealtimeConnection? Connection { get; set; }
        /// <summary>The coverage index once streaming.</summary>
        public int? Record { get; set; }
        /// <summary>File samples of stream time 0, and of the next sample to send.</summary>
        public long OffsetSample { get; set; }
        public long NextSample { get; set; }
        public double PaceStart { get; set; } = Now();
        public double LastSend { get; set; } = Now();
        public double? LiveSince { get; set; }
        public double BacklogLimit { get; set; }
        public RealtimeResampler? Resampler { get; set; }
        public SpeechChunker? Chunker { get; set; }
        public bool Confirmed { get; set; }
        public double? RotateAt { get; set; }
        public bool Rotating { get; set; }
        public int? SuccessorOf { get; set; } = plan.SuccessorOf;
        public long CloseAt { get; set; }
        /// <summary>The close messages were built, then sent.</summary>
        public bool CloseSent { get; set; }
        public bool CloseDelivered { get; set; }
    }
}
