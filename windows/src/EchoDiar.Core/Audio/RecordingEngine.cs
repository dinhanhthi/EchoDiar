using System.Diagnostics;
using EchoDiar.Core.Model;

namespace EchoDiar.Core.Audio;

/// <summary>
/// A capture stream downmixed to mono Float32 (the Platform's mic and loopback captures). The engine creates one per
/// open and disposes it when it closes the source.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>The device's display name, recorded in <c>session.json</c> (<c>micDeviceName</c> for the mic,
    /// <c>outputDeviceName</c> for system audio). Read after <see cref="Start"/>.</summary>
    string Name { get; }

    /// <summary>The stream rate in Hz, known once <see cref="Start"/> returns.</summary>
    int SampleRate { get; }

    /// <summary>A capture error after <see cref="Start"/> returned; capture has ended. Raised on the capture thread:
    /// handlers must not call <see cref="Stop"/> or wait for a thread that does.</summary>
    event Action<Exception>? Failed;

    /// <summary>
    /// Opens the device and starts capturing; opening errors are thrown here. <c>onSamples</c> runs on the capture
    /// thread, possibly before this returns, with mono samples (valid only during the call), the block's QPC time in
    /// 100 ns units and the sample rate.
    /// </summary>
    void Start(Action<ReadOnlySpan<float>, long, int> onSamples);

    /// <summary>Stops capturing and waits for the capture thread.</summary>
    void Stop();
}

/// <summary>What a recording captures as system audio: every process except EchoDiar, or one app.</summary>
public sealed record CaptureTarget
{
    private CaptureTarget(AudioProcessGroup? app) => App = app;

    /// <summary>Every process except EchoDiar itself.</summary>
    public static CaptureTarget Global { get; } = new((AudioProcessGroup?)null);

    public static CaptureTarget ForApp(AudioProcessGroup group) => new(group);

    /// <summary>The picked app, or null for all system audio.</summary>
    public AudioProcessGroup? App { get; }

    /// <summary>An app was picked but every process except EchoDiar is recorded (see <see cref="AudioProcessGroup.NeedsGlobalTap"/>).</summary>
    public bool GlobalTapFallback => App?.NeedsGlobalTap == true;

    /// <summary>Per-app capture of <see cref="App"/>'s process tree, else all system audio except EchoDiar.</summary>
    public TapMode TapMode => App is { NeedsGlobalTap: false } ? TapMode.PerApp : TapMode.Global;

    internal List<RecordingSession.SourceApp> SourceApps() =>
        App is null ? [] : [new() { Name = App.Name, BundleIDs = [App.Exe], Pids = [.. App.Pids] }];
}

/// <summary>Less than <see cref="RecordingEngine.MinimumFreeBytes"/> free on the recording volume.</summary>
public sealed class RecordingDiskFullException(long availableBytes)
    : IOException($"Only {availableBytes} bytes are free on the recording volume.")
{
    public long AvailableBytes { get; } = availableBytes;
}

/// <summary>
/// Orchestrates the system audio source, the mic and the writer for one recording at a time, and keeps
/// <c>session.json</c> current. The library derives <c>conversation.json</c> from it (<see cref="Conversation.FromSession"/>).
/// </summary>
/// <remarks>
/// One lock owns the state transitions; what callers poll (<see cref="CurrentSnapshot"/>, <see cref="CurrentLevels"/>,
/// <see cref="CurrentLanguage"/>) sits behind a second one, so polling never waits on a transition or a drain.
/// Sources come from the injected factories: Core never opens a device itself.
/// </remarks>
public sealed class RecordingEngine : IDisposable
{
    public enum State { Idle, Recording, Paused }

    /// <summary>Stored as <c>RecordingSession.endReason</c>: <c>user</c>, <c>diskFull</c>, <c>writeFailed</c>, <c>captureFailed</c>.</summary>
    public enum EndReason { User, DiskFull, WriteFailed, CaptureFailed }

    public sealed record Snapshot
    {
        public State State { get; init; }

        /// <summary>Seconds written to the audio file.</summary>
        public double Elapsed { get; init; }

        public bool SilentTapSuspected { get; init; }

        /// <summary>The picked app could not be isolated, so every process except EchoDiar is recorded; warn the user.</summary>
        public bool GlobalTapFallback { get; init; }

        /// <summary>Less than 1 GB left on the recording volume.</summary>
        public bool LowDisk { get; init; }

        public double MicSilentSeconds { get; init; }
        public double SystemSilentSeconds { get; init; }

        /// <summary>Set on the snapshot published when a recording ends.</summary>
        public EndReason? EndReason { get; init; }

        internal Snapshot Applying(ChannelWriterStats stats) => this with
        {
            Elapsed = stats.Duration,
            SilentTapSuspected = stats.SilentTapSuspected,
            MicSilentSeconds = stats.MicSilentSeconds,
            SystemSilentSeconds = stats.SystemSilentSeconds,
        };
    }

    public const long LowDiskBytes = 1_000_000_000;
    public const long MinimumFreeBytes = 200_000_000;

    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(1);
    private const int DiskCheckTicks = 60;
    private const double RingSeconds = 4;
    private const int StandInFrames = 1_024;
    private static readonly long OutputCheckTicks = TimeSpan.FromSeconds(2).Ticks;

    private readonly object _gate = new();
    private readonly object _shared = new();
    private readonly Func<CaptureTarget, ICaptureSource> _openSystem;
    private readonly Func<string?, ICaptureSource> _openMic;
    private readonly Func<string, long?> _availableFreeSpace;
    private readonly Func<long> _clock;

    // Behind _gate.
    private State _state = State.Idle;
    private CaptureTarget _target = CaptureTarget.Global;
    private string? _micEndpointId;
    private RecordingSession? _session;
    private string? _folder;
    private ChannelWriter? _writer;
    private Capture? _system;
    private Capture? _mic;
    private Timer? _drainTimer;
    private Timer? _monitorTimer;
    private int _ticks;
    private long _micGeneration;
    private bool _lowDisk;
    private bool _silentTapSuspected;

    // Behind _shared.
    private Snapshot _snapshot = new();
    private ChannelWriter? _sharedWriter;
    private RingBuffer? _micRing;
    private RingBuffer? _systemRing;
    private string? _language;

    /// <param name="openSystem">Creates the system audio source for a target (not started).</param>
    /// <param name="openMic">Creates the mic source for an endpoint id, null for the default input (not started).</param>
    /// <param name="availableFreeSpace">Free bytes on the volume of a path, null when unknown (the check is skipped).
    /// Defaults to <see cref="DriveInfo.AvailableFreeSpace"/>.</param>
    /// <param name="clock">The capture sources' clock: QPC in 100 ns units. Defaults to <see cref="Stopwatch"/>.</param>
    public RecordingEngine(
        Func<CaptureTarget, ICaptureSource> openSystem,
        Func<string?, ICaptureSource> openMic,
        Func<string, long?>? availableFreeSpace = null,
        Func<long>? clock = null)
    {
        _openSystem = openSystem;
        _openMic = openMic;
        _availableFreeSpace = availableFreeSpace ?? DriveFreeSpace;
        _clock = clock ?? (static () => Stopwatch.GetElapsedTime(0).Ticks);
    }

    /// <summary>Published on state changes and flag flips only, inside the engine's lock: handlers must dispatch, never block.</summary>
    public event Action<Snapshot>? SnapshotChanged;

    /// <summary>Gets each written block (see <see cref="ChannelWriter.LiveSink"/>). Read at <see cref="Start"/>.</summary>
    public ChannelWriter.LiveSinkHandler? LiveSink { get; set; }

    /// <summary>
    /// Whether the target currently plays output, for the silent-tap check. Read at <see cref="Start"/>; called at most
    /// every 2 s, inside the writer's lock. Null: a silent tap is never suspected.
    /// </summary>
    public Func<CaptureTarget, bool>? IsTargetPlayingOutput { get; set; }

    public void Dispose() => Stop();

    // Public API

    /// <summary>
    /// Writes <c>session.json</c> before any IO, then starts the system source, the mic (silence if unavailable) and
    /// the writer. Returns the session folder. Throws if the disk is nearly full or the system source cannot start; the
    /// folder is then removed. <paramref name="micEndpointId"/> null records the default input.
    /// </summary>
    public string Start(CaptureTarget target, string language, string? micEndpointId, string root)
    {
        lock (_gate)
        {
            if (_state != State.Idle) throw new InvalidOperationException("A recording is already running.");
            Directory.CreateDirectory(root);
            var available = _availableFreeSpace(root);
            if (available < MinimumFreeBytes) throw new RecordingDiskFullException(available.Value);
            _lowDisk = available < LowDiskBytes;
            _silentTapSuspected = false;
            _target = target;
            _micEndpointId = micEndpointId;

            var session = new RecordingSession
            {
                State = SessionState.Recording,
                SourceApps = target.SourceApps(),
                TapMode = target.TapMode,
                GlobalTapFallback = target.GlobalTapFallback,
                LanguageMarkers = [new() { FileTime = 0, Language = language }],
            };
            var folder = session.Folder(root);
            session.Write(folder);

            Capture system;
            try
            {
                system = OpenSystem();
            }
            catch
            {
                Discard(folder);
                throw;
            }
            var mic = OpenMic();

            ChannelWriter? writer = null;
            try
            {
                writer = new ChannelWriter(Path.Combine(folder, session.AudioFileName))
                {
                    LiveSink = LiveSink,
                    IsTargetPlayingOutput = OutputCheck(target),
                };
                writer.Start(mic.Ring, system.Ring, _clock());
            }
            catch
            {
                system.Source?.Dispose();
                mic.Source?.Dispose();
                writer?.Dispose(); // closes audio.caf so the folder can be deleted
                Discard(folder);
                throw;
            }

            session.MicDeviceName = mic.Source?.Name;
            session.OutputDeviceName = system.Source?.Name;
            _state = State.Recording;
            _session = session;
            _folder = folder;
            _writer = writer;
            _system = system;
            _mic = mic;
            Save();
            _ticks = 0;
            _drainTimer = new Timer(Drain, writer, ChannelWriter.DrainInterval, ChannelWriter.DrainInterval);
            _monitorTimer = new Timer(Tick, writer, MonitorInterval, MonitorInterval);
            lock (_shared)
            {
                _sharedWriter = writer;
                _micRing = mic.Ring;
                _systemRing = system.Ring;
                _language = language;
            }
            Publish();
            return folder;
        }
    }

    /// <summary>Closes the system source and the mic (privacy indicator off) and pauses the writer.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_state != State.Recording || _writer is null || _session is null) return;
            CloseSources();
            _writer.Pause();
            _session.Pauses.Add(new() { FileTime = _writer.Stats.Duration, Start = DateTimeOffset.UtcNow });
            _session.State = SessionState.Paused;
            _state = State.Paused;
            Save();
            Publish();
        }
    }

    /// <summary>Reopens the system source and the mic. Throws, staying paused, if the system source cannot start.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_state != State.Paused || _writer is null || _session is null) return;
            var system = OpenSystem();
            var mic = OpenMic();
            try
            {
                _writer.ReplaceRing(ChannelWriter.Channel.System, system.Ring);
                _writer.ReplaceRing(ChannelWriter.Channel.Mic, mic.Ring);
            }
            catch
            {
                system.Source?.Dispose();
                mic.Source?.Dispose();
                throw;
            }
            _writer.Resume(_clock());
            _system = system;
            _mic = mic;
            CloseOpenPause(_session);
            // The input may have changed while paused.
            _session.MicDeviceName = mic.Source?.Name;
            _session.State = SessionState.Recording;
            _state = State.Recording;
            Save();
            lock (_shared)
            {
                _micRing = mic.Ring;
                _systemRing = system.Ring;
            }
            Publish();
        }
    }

    /// <summary>Returns the final session, or null when idle.</summary>
    public RecordingSession? Stop(EndReason reason = EndReason.User)
    {
        lock (_gate) return StopLocked(reason);
    }

    /// <summary>Appends a language marker ("auto" or BCP-47) at the current file time.</summary>
    public void SetLanguage(string language)
    {
        lock (_gate)
        {
            if (_state == State.Idle || _writer is null || _session is null) return;
            _session.LanguageMarkers.Add(new() { FileTime = _writer.Stats.Duration, Language = language });
            Save();
            lock (_shared) _language = language;
        }
    }

    /// <summary>The language of the last marker ("auto" or BCP-47), or null when idle.</summary>
    public string? CurrentLanguage()
    {
        lock (_shared) return _language;
    }

    /// <summary>Peak levels (0...1) since the previous call. One caller, at 15 Hz at most.</summary>
    public (float Mic, float System) CurrentLevels()
    {
        RingBuffer? mic, system;
        lock (_shared) (mic, system) = (_micRing, _systemRing);
        return (mic?.ReadLevels().Peak ?? 0, system?.ReadLevels().Peak ?? 0);
    }

    /// <summary>The last published snapshot with live writer stats (elapsed, silence).</summary>
    public Snapshot CurrentSnapshot()
    {
        Snapshot snapshot;
        ChannelWriter? writer;
        lock (_shared) (snapshot, writer) = (_snapshot, _sharedWriter);
        return writer is null ? snapshot : snapshot.Applying(writer.Stats);
    }

    // Inside _gate

    private RecordingSession? StopLocked(EndReason reason)
    {
        if (_state == State.Idle || _session is null || _folder is null) return null;
        _drainTimer?.Dispose();
        _monitorTimer?.Dispose();
        _drainTimer = _monitorTimer = null;
        CloseSources();
        try
        {
            _writer?.Stop();
        }
        catch
        {
            reason = EndReason.WriteFailed;
        }

        var session = _session;
        CloseOpenPause(session);
        session.State = SessionState.Stopped;
        session.EndReason = Raw(reason);
        Save();

        var stats = _writer?.Stats;
        _state = State.Idle;
        _session = null;
        _folder = null;
        _writer = null;
        lock (_shared)
        {
            _sharedWriter = null;
            _micRing = null;
            _systemRing = null;
            _language = null;
        }
        Publish(stats, reason);
        return session;
    }

    /// <summary>Throws if the source cannot be created or started. A failure after start leaves the system channel
    /// padding silence while the mic keeps recording (rebuilding it on route changes is Phase 10).</summary>
    private Capture OpenSystem() => Open(_openSystem(_target), static _ => { });

    /// <summary>Falls back to a stand-in ring nothing writes to, which the writer fills with silence; never throws.</summary>
    private Capture OpenMic()
    {
        var generation = ++_micGeneration;
        try
        {
            return Open(_openMic(_micEndpointId), _ => ThreadPool.QueueUserWorkItem(_ => MicFailed(generation)));
        }
        catch
        {
            return new Capture(null, new RingBuffer(StandInFrames, ChannelWriter.SampleRate));
        }
    }

    /// <summary>The mic stopped mid-recording: its channel continues as silence. Runs on the thread pool, since
    /// <see cref="ICaptureSource.Failed"/> is raised on the capture thread that closing the source joins.</summary>
    private void MicFailed(long generation)
    {
        lock (_gate)
        {
            if (generation != _micGeneration || _state != State.Recording || _writer is null || _session is null) return;
            _mic?.Source?.Dispose();
            _micGeneration++;
            var standIn = new Capture(null, new RingBuffer(StandInFrames, ChannelWriter.SampleRate));
            _writer.ReplaceRing(ChannelWriter.Channel.Mic, standIn.Ring);
            _mic = standIn;
            _session.MicDeviceName = null;
            Save();
            lock (_shared) _micRing = standIn.Ring;
        }
    }

    private void CloseSources()
    {
        _system?.Source?.Dispose();
        _mic?.Source?.Dispose();
        _system = _mic = null;
    }

    private void Drain(object? writer) => ((ChannelWriter)writer!).Drain(_clock());

    private void Tick(object? owner)
    {
        lock (_gate)
        {
            if (owner != _writer || _state == State.Idle || _writer is null || _folder is null) return;
            _ticks++;
            if (_ticks % DiskCheckTicks == 0 && _availableFreeSpace(_folder) is { } available)
            {
                if (available < MinimumFreeBytes)
                {
                    StopLocked(EndReason.DiskFull);
                    return;
                }
                if (available < LowDiskBytes != _lowDisk)
                {
                    _lowDisk = !_lowDisk;
                    Publish();
                }
            }
            if (_state != State.Recording) return;
            var stats = _writer.Stats;
            if (stats.Failure is not null)
            {
                StopLocked(EndReason.WriteFailed);
            }
            else if (stats.SilentTapSuspected != _silentTapSuspected)
            {
                _silentTapSuspected = stats.SilentTapSuspected;
                Publish(stats);
            }
        }
    }

    private void Publish(ChannelWriterStats? stats = null, EndReason? endReason = null)
    {
        var snapshot = new Snapshot
        {
            State = _state,
            GlobalTapFallback = _target.GlobalTapFallback,
            LowDisk = _lowDisk,
            EndReason = endReason,
        };
        if ((stats ?? _writer?.Stats) is { } current) snapshot = snapshot.Applying(current);
        lock (_shared) _snapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
    }

    private void Save()
    {
        if (_session is null || _folder is null) return;
        try
        {
            _session.Write(_folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Like Swift's `try?`: the next save, or launch recovery, catches up.
        }
    }

    /// <summary>
    /// <c>isTargetPlayingOutput</c> for the writer, which asks every drain once the system channel has been zero for
    /// 5 s: one query per 2 s at most. Only called inside the writer's lock, so the cache needs none.
    /// </summary>
    private Func<bool>? OutputCheck(CaptureTarget target)
    {
        if (IsTargetPlayingOutput is not { } query) return null;
        long? checkedAt = null;
        var playing = false;
        return () =>
        {
            var now = _clock();
            if (checkedAt is null || now - checkedAt >= OutputCheckTicks)
            {
                playing = query(target);
                checkedAt = now;
            }
            return playing;
        };
    }

    /// <summary>Starts <paramref name="source"/>, disposing it if that throws.</summary>
    private static Capture Open(ICaptureSource source, Action<Exception> onFailed)
    {
        try
        {
            var capture = new Capture(source);
            source.Failed += onFailed;
            source.Start(capture.Write);
            capture.Ring = new RingBuffer((int)Math.Ceiling(source.SampleRate * RingSeconds), source.SampleRate);
            return capture;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static void CloseOpenPause(RecordingSession session)
    {
        if (session.Pauses is [.., { End: null } last]) last.End = DateTimeOffset.UtcNow;
    }

    /// <summary>A start that failed before the writer ran has no audio: no conversation is left behind. A crash
    /// before this point leaves <c>session.json</c> in <c>recording</c>, which launch recovery marks failed.</summary>
    private static void Discard(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Raw(EndReason reason) => reason switch
    {
        EndReason.User => "user",
        EndReason.DiskFull => "diskFull",
        EndReason.WriteFailed => "writeFailed",
        EndReason.CaptureFailed => "captureFailed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static long? DriveFreeSpace(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>An open source and the ring its capture thread writes to; a null source is the silent mic stand-in.</summary>
    private sealed class Capture(ICaptureSource? source, RingBuffer? ring = null)
    {
        private RingBuffer? _ring = ring;

        public ICaptureSource? Source { get; } = source;

        /// <summary>Created once the source's rate is known; blocks that arrive before it are dropped.</summary>
        public RingBuffer Ring
        {
            get => Volatile.Read(ref _ring)!;
            set => Volatile.Write(ref _ring, value);
        }

        public void Write(ReadOnlySpan<float> samples, long time, int sampleRate) => Volatile.Read(ref _ring)?.Write(samples, time);
    }
}
