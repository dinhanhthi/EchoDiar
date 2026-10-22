using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.Core;

// The recording state machine with fake sources and a test clock (QPC, 100 ns units). Pause and Stop drain the rings
// synchronously, so the engine's background timers cannot change these outcomes.
public sealed class RecordingEngineTests : IDisposable
{
    private const long TicksPerSecond = 10_000_000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));
    private readonly List<FakeSource> _systems = [];
    private readonly List<FakeSource> _mics = [];
    private readonly HashSet<string> _goneMics = [];
    private readonly FakeRoutes _routes = new();
    private long _now = 1_000_000_000;
    private RecordingEngine? _engine;

    public RecordingEngineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _engine?.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private RecordingEngine Engine(Func<FakeSource>? system = null)
    {
        _engine = new RecordingEngine(
            _ => Add(_systems, system?.Invoke() ?? new FakeSource()),
            id => Add(_mics, new FakeSource
            {
                EndpointId = id,
                StartError = id is not null && _goneMics.Contains(id) ? new InvalidOperationException("gone") : null,
            }),
            availableFreeSpace: _ => 10_000_000_000,
            clock: () => _now,
            routes: _routes);
        return _engine;
    }

    private static FakeSource Add(List<FakeSource> list, FakeSource source)
    {
        list.Add(source);
        return source;
    }

    /// <summary>One 100 ms block from the current system and mic sources, stamped now.</summary>
    private void EmitBlock()
    {
        var block = Enumerable.Repeat(0.25f, 1_600).ToArray();
        _systems[^1].Emit(block, _now);
        _mics[^1].Emit(block, _now);
    }

    [Fact]
    public void PauseThenResumeClosesThePauseAndTheFileKeepsCounting()
    {
        var engine = Engine();
        var folder = engine.Start(CaptureTarget.Global, "auto", micEndpointId: null, _root);
        EmitBlock();
        engine.Pause();

        var paused = RecordingSession.Read(folder);
        Assert.Equal(SessionState.Paused, paused.State);
        var pause = Assert.Single(paused.Pauses);
        Assert.Equal(0.1, pause.FileTime, 0.005);
        Assert.Null(pause.End);

        _now += 5 * TicksPerSecond; // paused wall-clock time adds no silence
        engine.Resume();
        EmitBlock();
        var session = engine.Stop(RecordingEngine.EndReason.User);

        Assert.NotNull(session);
        var stored = RecordingSession.Read(folder);
        Assert.Equal(SessionState.Stopped, stored.State);
        Assert.Equal("user", stored.EndReason);
        pause = Assert.Single(stored.Pauses);
        Assert.Equal(0.1, pause.FileTime, 0.005);
        Assert.NotNull(pause.End);
        using var reader = new CafFile.Reader(Path.Combine(folder, stored.AudioFileName));
        Assert.Equal(0.2, reader.Frames / 16_000.0, 0.005);
    }

    [Fact]
    public void FailingSystemSourceRemovesTheFolder()
    {
        var engine = Engine(() => new FakeSource { StartError = new InvalidOperationException("no loopback") });

        var error = Assert.Throws<InvalidOperationException>(() => engine.Start(CaptureTarget.Global, "auto", null, _root));

        Assert.Equal("no loopback", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.Empty(_mics); // the mic is never opened for a recording that cannot start
        Assert.Equal(RecordingEngine.State.Idle, engine.CurrentSnapshot().State);
    }

    [Fact]
    public void InputLostMovesTheMicToTheDefaultInput()
    {
        var engine = Engine();
        var behaviors = new List<RouteChangeBehavior>();
        engine.MicRouteLost += behaviors.Add;
        engine.RouteBehavior = () => RouteChangeBehavior.Stop;
        engine.Start(CaptureTarget.Global, "auto", micEndpointId: "mic-1", _root);
        Assert.Equal(["mic-1"], _routes.Starts);

        _goneMics.Add("mic-1");
        _routes.InputLost?.Invoke();

        Assert.Equal(2, _mics.Count);
        Assert.True(_mics[0].Disposed);
        Assert.Null(_mics[1].EndpointId); // the gone id cannot reopen
        Assert.False(_mics[1].Disposed);
        Assert.Equal(1, engine.CurrentSnapshot().MicRoutesLost);
        Assert.Equal([RouteChangeBehavior.Stop], behaviors);
        Assert.Equal(RecordingEngine.State.Recording, engine.CurrentSnapshot().State); // the App applies the behavior
    }

    [Fact]
    public void InputLostWhilePausedOpensTheDefaultInputOnResume()
    {
        var engine = Engine();
        engine.Start(CaptureTarget.Global, "auto", micEndpointId: "mic-1", _root);
        engine.Pause();

        _goneMics.Add("mic-1");
        _routes.InputLost?.Invoke();
        Assert.Single(_mics);
        Assert.Equal(1, engine.CurrentSnapshot().MicRoutesLost);

        engine.Resume();
        Assert.Equal(["mic-1", "mic-1", null], _mics.Select(mic => mic.EndpointId));
        Assert.False(_mics[^1].Disposed);
    }

    [Fact]
    public void OutputChangedRebuildsTheGlobalSystemSource()
    {
        var engine = Engine();
        engine.Start(CaptureTarget.Global, "auto", null, _root);

        _routes.OutputChanged?.Invoke();

        Assert.Equal(2, _systems.Count);
        Assert.True(_systems[0].Disposed);
        Assert.False(_systems[1].Disposed);
        Assert.Single(_mics);
    }

    [Fact]
    public void RouteEventAfterStopIsIgnored()
    {
        var engine = Engine();
        engine.Start(CaptureTarget.Global, "auto", null, _root);
        // Handlers already read by a route source that raises just after the engine unsubscribes.
        var (output, lost) = (_routes.OutputChanged, _routes.InputLost);
        engine.Stop();
        Assert.Equal(1, _routes.Stops);
        Assert.Null(_routes.InputLost);

        output?.Invoke();
        lost?.Invoke();

        Assert.Single(_systems);
        Assert.Single(_mics);
        Assert.Equal(0, engine.CurrentSnapshot().MicRoutesLost);
    }

    private sealed class FakeRoutes : IAudioRouteSource
    {
        public Action? OutputChanged;
        public Action? InputChanged;
        public Action? InputLost;

        public List<string?> Starts { get; } = [];
        public int Stops { get; private set; }

        event Action? IAudioRouteSource.OutputChanged { add => OutputChanged += value; remove => OutputChanged -= value; }
        event Action? IAudioRouteSource.InputChanged { add => InputChanged += value; remove => InputChanged -= value; }
        event Action? IAudioRouteSource.InputLost { add => InputLost += value; remove => InputLost -= value; }

        public bool Start(string? inputEndpointId)
        {
            Starts.Add(inputEndpointId);
            return true;
        }

        public void Stop() => Stops++;
    }

    private sealed class FakeSource : ICaptureSource
    {
        private Action<ReadOnlySpan<float>, long, int>? _onSamples;

        public string? EndpointId { get; init; }
        public bool Disposed { get; private set; }
        public Exception? StartError { get; init; }
        public string Name => "Fake";
        public int SampleRate => 16_000;

        public event Action<Exception>? Failed { add { } remove { } }

        public void Start(Action<ReadOnlySpan<float>, long, int> onSamples)
        {
            if (StartError is not null) throw StartError;
            _onSamples = onSamples;
        }

        public void Emit(float[] samples, long time) => _onSamples?.Invoke(samples, time, SampleRate);

        public void Stop() => _onSamples = null;

        public void Dispose()
        {
            Stop();
            Disposed = true;
        }
    }
}
