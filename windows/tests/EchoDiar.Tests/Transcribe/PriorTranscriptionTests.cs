using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Transcribe;

namespace EchoDiar.Tests.Transcribe;

// Guards "failures degrade, never lose text" (Swift PriorTranscriptionTests): what the recording transcriber did not
// transcribe becomes a gap the pipeline transcribes at Stop, and a prior that no longer matches the run is not used
// (full batch instead). Sessions come from a fake engine registered in the pipeline's engine factories.
public sealed class PriorTranscriptionTests : IDisposable
{
    private const string Parakeet = "parakeet-v3-int8";
    private const string Turbo = "whisper-large-v3-turbo-q5";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));

    public PriorTranscriptionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void GapsAreTheChunksNotTranscribedWithNeighborsMerged()
    {
        var gaps = PriorTranscription.Gaps(
        [
            (new Span(0, 10), PriorTranscription.ChunkOutcome.Transcribed),
            (new Span(10, 20), PriorTranscription.ChunkOutcome.Failed),
            (new Span(20, 30), PriorTranscription.ChunkOutcome.Dropped),
            (new Span(30, 40), PriorTranscription.ChunkOutcome.Transcribed),
            (new Span(42, 50), PriorTranscription.ChunkOutcome.Silence),
            (new Span(55, 60), PriorTranscription.ChunkOutcome.Pending),
        ]);

        Assert.Equal([new Span(10, 30), new Span(55, 60)], gaps);
        Assert.Empty(PriorTranscription.Gaps([]));
    }

    [Fact]
    public void IsUsableForTheSameEngineAndAudio() =>
        Assert.True(Prior.IsUsable(Prior.EngineID, null, null, 600.05));

    // Another engine, a language fallback, a re-transcribe language or audio of another length: full batch instead.
    [Theory]
    [InlineData(Parakeet, null, null, 600)]
    [InlineData(Turbo, Parakeet, null, 600)]
    [InlineData(Turbo, null, "fr", 600)]
    [InlineData(Turbo, null, null, 600.2)]
    public void IsNotUsableWhenTheRunDiffers(string engineID, string? engineFallback, string? languageOverride, double audioDuration) =>
        Assert.False(Prior.IsUsable(engineID, engineFallback, languageOverride, audioDuration));

    private static readonly PriorTranscription Prior = new() { EngineID = Turbo, Duration = 600 };

    // Pipeline

    // No gaps and speakers off: the prior's segments are the transcript, written without any model (nothing is
    // installed, so a batch run would stop at "Needs model").
    [Fact]
    public async Task UsablePriorWithoutGapsIsTheTranscriptWithoutAModel()
    {
        var (pipeline, job, engine) = PipelineJob(Recorded(2), installed: false);

        await pipeline.TranscribeAsync(job, Token);

        var transcript = Transcript.Read(job.Folder);
        Assert.Equal(Parakeet, transcript.EngineID);
        Assert.Equal(["hello there", "general kenobi"], transcript.Segments.Select(s => s.Text));
        Assert.Equal([Transcript.Speaker.Me, Transcript.Speaker.System(1)], transcript.Segments.Select(s => s.Speaker));
        var conversation = Conversation.Read(job.Folder);
        Assert.Equal(ConversationStatus.Transcribed, conversation.Status);
        Assert.True(conversation.HasTranscript);
        Assert.Equal(0, engine.Sessions);
    }

    // A gap needs the model: with none installed the job stops at "Needs model".
    [Fact]
    public async Task UsablePriorWithAGapNeedsTheModel()
    {
        var (pipeline, job, _) = PipelineJob(Recorded(2) with { SystemGaps = [new Span(1, 2)] }, installed: false);

        Assert.Equal(Parakeet, await NeededModel(pipeline, job));
    }

    // A prior that does not match the audio is ignored: the full batch runs, and needs the model even without gaps.
    [Fact]
    public async Task PriorOfAnotherLengthRunsTheBatch()
    {
        var (pipeline, job, _) = PipelineJob(Recorded(2.5), installed: false);

        Assert.Equal(Parakeet, await NeededModel(pipeline, job));
        Assert.False(File.Exists(Path.Combine(job.Folder, Transcript.FileName)));
    }

    // With the model installed, the gaps of both channels are decoded by one session from the engine factory, unloaded
    // at the end, and their segments join the prior's in time order (mic decoded first: x1).
    [Fact]
    public async Task UsablePriorFillsItsGapsWithOneSession()
    {
        var prior = Recorded(2) with
        {
            System = [new RawSegment(1, 1.4, "general kenobi")],
            MicGaps = [new Span(0.9, 1.3)],
            SystemGaps = [new Span(1.5, 2)],
        };
        var (pipeline, job, engine) = PipelineJob(prior, installed: true);

        await pipeline.TranscribeAsync(job, Token);

        Assert.Equal(1, engine.Sessions);
        Assert.True(engine.Session.Disposed);
        Assert.Equal(0, engine.BatchRuns);
        var segments = Transcript.Read(job.Folder).Segments;
        Assert.Equal(["hello there", "x1", "general kenobi", "x2"], segments.Select(s => s.Text));
        Assert.Equal([0, 0.9, 1, 1.5], segments.Select(s => Math.Round(s.Start, 2)));
    }

    // A gap is cut into speech chunks: silence is not decoded (nor the model loaded for it), each chunk gets the
    // marker language at its start, and segments come back in file time.
    [Fact]
    public async Task GapFillDecodesSpeechChunksInFileTime()
    {
        // 1 s of tone, 2 s of silence, 1 s of tone.
        var audio = Path.Combine(_root, "system.caf");
        WriteMono([(0, 16_000), (48_000, 64_000)], 64_000, audio);
        var entry = ModelCatalog.Entry(Parakeet)!;
        var ranges = LanguageRange.Ranges([new() { FileTime = 0, Language = "auto" }, new() { FileTime = 2, Language = "fr" }]);

        var none = await TranscriptionPipeline.FillGapsAsync(
            [new Span(1.5, 2.5)], audio, entry, ranges,
            _ => throw new InvalidOperationException("silence loaded the model"), _ => { }, Token);
        Assert.Empty(none);

        var session = new FakeSession();
        var segments = await TranscriptionPipeline.FillGapsAsync(
            [new Span(0, 4)], audio, entry, ranges, _ => Task.FromResult<ITranscriptionSession>(session), _ => { }, Token);
        Assert.Equal([null, "fr"], session.Languages);
        Assert.Equal(2, segments.Count);
        Assert.True(Math.Abs(segments[0].Start) < 0.05);
        Assert.InRange(segments[1].Start, 2.6, 3.0);
    }

    /// <summary>The catalog id of the job's "Needs model" error; null when it succeeded or failed otherwise.</summary>
    private static async Task<string?> NeededModel(TranscriptionPipeline pipeline, TranscriptionJob job)
    {
        try
        {
            await pipeline.TranscribeAsync(job, Token);
        }
        catch (TranscriptionException e) when (e.Kind == TranscriptionErrorKind.NeedsModel)
        {
            return e.Detail;
        }
        catch (TranscriptionException)
        {
        }
        return null;
    }

    /// <summary>Parakeet segments of a 2 s recording: one on the mic, one on the system channel, no gaps.</summary>
    private static PriorTranscription Recorded(double duration) => new()
    {
        EngineID = Parakeet,
        Mic = [new RawSegment(0, 0.8, "hello there")],
        System = [new RawSegment(1, 1.8, "general kenobi")],
        Duration = duration,
    };

    /// <summary>
    /// A 2 s stereo recording, a pipeline with the fake Parakeet engine (installed or not), and a speakers-off job
    /// carrying <paramref name="prior"/> with its engine.
    /// </summary>
    private (TranscriptionPipeline, TranscriptionJob, FakeEngine) PipelineJob(PriorTranscription prior, bool installed)
    {
        var session = new RecordingSession { State = SessionState.Stopped, AudioFileName = "audio.caf" };
        var folder = session.Folder(Path.Combine(_root, "Conversations"));
        session.Write(folder);
        WriteStereo(2, Path.Combine(folder, session.AudioFileName));
        var engine = new FakeEngine();
        var pipeline = new TranscriptionPipeline(
            new Dictionary<ModelRuntime, TranscriptionPipeline.EngineFactory> { [ModelRuntime.SherpaOnnx] = (_, _) => engine },
            null, _ => installed, e => Path.Combine(_root, "Models", e.Id), new ModelLoadGate());
        var job = new TranscriptionJob
        {
            ConversationID = session.Id, Folder = folder, EngineID = prior.EngineID, ProcessNow = true, Diarize = false,
            Prior = prior,
        };
        return (pipeline, job, engine);
    }

    /// <summary>A 16 kHz mono Float32 CAF of <paramref name="frames"/> samples: a 440 Hz tone over <paramref name="tones"/>, zeros elsewhere.</summary>
    private static void WriteMono((int Start, int End)[] tones, int frames, string path)
    {
        var samples = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            if (tones.Any(t => i >= t.Start && i < t.End)) samples[i] = (float)(Math.Sin(i * 2 * Math.PI * 440 / 16_000) * 0.5);
        }
        using var writer = new CafFile.Writer(path, 16_000, 1, CafFile.SampleFormat.Float32);
        writer.Append(samples);
    }

    /// <summary>A 16 kHz stereo Float32 CAF with a 440 Hz tone on both channels.</summary>
    private static void WriteStereo(int seconds, string path)
    {
        var frames = seconds * 16_000;
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[i * 2] = samples[i * 2 + 1] = (float)(Math.Sin(i * 2 * Math.PI * 440 / 16_000) * 0.5);
        }
        using var writer = new CafFile.Writer(path, 16_000, 2, CafFile.SampleFormat.Float32);
        writer.Append(samples);
    }

    /// <summary>A Parakeet stand-in whose sessions are <see cref="FakeSession"/>s; batch runs are only counted.</summary>
    private sealed class FakeEngine : ISessionTranscriptionEngine
    {
        public int Sessions { get; private set; }
        public int BatchRuns { get; private set; }
        public FakeSession Session { get; } = new();

        public Task<IReadOnlyList<RawSegment>> TranscribeAsync(
            string channelPath, IReadOnlyList<LanguageRange> languageRanges, Action<double> progress,
            CancellationToken cancellationToken)
        {
            BatchRuns++;
            return Task.FromResult<IReadOnlyList<RawSegment>>([]);
        }

        public Task<ITranscriptionSession> OpenSessionAsync(CancellationToken cancellationToken)
        {
            Sessions++;
            return Task.FromResult<ITranscriptionSession>(Session);
        }
    }

    /// <summary>Records each chunk's language; returns one segment spanning the chunk, numbered by call.</summary>
    private sealed class FakeSession : ITranscriptionSession
    {
        public List<string?> Languages { get; } = [];
        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<RawSegment>> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            Languages.Add(language);
            return Task.FromResult<IReadOnlyList<RawSegment>>([new RawSegment(0, samples.Length / 16_000.0, $"x{Languages.Count}")]);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
