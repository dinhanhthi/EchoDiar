using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Transcribe;

namespace EchoDiar.Tests.Transcribe;

// The batch pipeline without models (Swift EngineRoutingTests + SpeakersOffPipelineTests): which installed engine
// transcribes a job, and a whole job through fake engines on a CAF fixture.
public sealed class PipelineTests : IDisposable
{
    private const string Turbo = "whisper-large-v3-turbo-q5";
    private const string Parakeet = "parakeet-v3-int8";
    private const string Diarizer = "sherpa-pyannote-3";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));

    public PipelineTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string[] List(string ids) => ids.Split(',', StringSplitOptions.RemoveEmptyEntries);

    // No engine chosen: the catalog default, else the smallest Whisper selectable for the language, else the first
    // installed batch engine that supports it, else the first selectable one; a diarizer is never an engine.
    [Theory]
    [InlineData(Parakeet, "auto", Parakeet)]
    [InlineData(Parakeet + ",whisper-small", "auto", "whisper-small")]
    [InlineData("whisper-small," + Turbo, "auto", Turbo)]
    [InlineData("whisper-small,whisper-base", "auto", "whisper-base")]
    [InlineData(Parakeet + ",phowhisper-medium", "auto", Parakeet)]
    [InlineData(Parakeet + ",phowhisper-medium", "vi", "phowhisper-medium")]
    [InlineData(Parakeet, "vi", Parakeet)]
    [InlineData("phowhisper-medium", "en", null)]
    [InlineData(Diarizer, "auto", null)]
    [InlineData("", "auto", null)]
    public void TheDefaultFallsBackToAnyInstalledBatchEngine(string installed, string language, string? expected)
    {
        var ids = List(installed);

        Assert.Equal(expected, TranscriptionPipeline.DefaultEngine(language, e => ids.Contains(e.Id))?.Id);
    }

    // A marker language the selected engine lacks: an installed Whisper stand-in (the default first, else the
    // smallest) runs the job and engineFallback names the selected one; supported, unknown or detected languages,
    // or no Whisper installed, keep the selected engine.
    [Theory]
    [InlineData("vi", Turbo, false, Turbo, Parakeet)]
    [InlineData("en,ja", "whisper-large-v3-q5,whisper-small", false, "whisper-small", Parakeet)]
    [InlineData("en,fr", Turbo, false, Parakeet, null)]
    [InlineData("vi", Turbo, true, Parakeet, null)]
    [InlineData("", Turbo, false, Parakeet, null)]
    [InlineData("vi", "", false, Parakeet, null)]
    public void AnUnsupportedMarkerLanguageFallsBackToWhisper(
        string markers, string installed, bool unknownLanguages, string expected, string? engineFallback)
    {
        var (entry, fallback) = Fallback(markers, installed, unknownLanguages);

        Assert.Equal(expected, entry.Id);
        Assert.Equal(engineFallback, fallback);
    }

    // One engine transcribes the whole job: a stand-in lacking one of the marker languages is never chosen.
    [Theory]
    [InlineData("vi,ja", "phowhisper-medium", Parakeet, null)]
    [InlineData("vi,ja", "phowhisper-medium," + Turbo, Turbo, Parakeet)]
    [InlineData("vi", "phowhisper-medium", "phowhisper-medium", Parakeet)]
    public void AStandInMustSupportEveryMarker(string markers, string installed, string expected, string? engineFallback)
    {
        var (entry, fallback) = Fallback(markers, installed, unknownLanguages: false);

        Assert.Equal(expected, entry.Id);
        Assert.Equal(engineFallback, fallback);
    }

    private static (ModelEntry Entry, string? EngineFallback) Fallback(string markers, string installed, bool unknownLanguages)
    {
        var entry = ModelCatalog.Entry(Parakeet)!;
        var languages = List(markers);
        var ids = List(installed);
        return TranscriptionPipeline.LanguageFallback(
            entry, unknownLanguages ? [] : TranscriptionPipeline.SupportedLanguages(entry), languages,
            languages.FirstOrDefault() ?? "auto", ids.Contains);
    }

    // "Tell speakers apart" off runs no diarizer and adds no note; on with no diarizer installed it adds the
    // missing-model note. Either way the whole system channel is s1, the silent mic is skipped, the extract holds
    // every frame of a recording longer than one CAF read (2^20 frames), and the missing conversation.json is
    // created with the audio length and status transcribed.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SpeakersOffRunsNoDiarizerAndStillWritesTheTranscriptAndAMissingDiarizerAddsTheNote(bool diarize, bool diarizerInstalled)
    {
        const int seconds = 66;
        var session = new RecordingSession { State = SessionState.Stopped, AudioFileName = "audio.caf" };
        var folder = session.Folder(Path.Combine(_root, "Conversations"));
        session.Write(folder);
        WriteStereo(seconds, Path.Combine(folder, session.AudioFileName));

        var engine = new FakeEngine();
        var diarizerCalls = 0;
        var pipeline = new TranscriptionPipeline(
            new Dictionary<ModelRuntime, TranscriptionPipeline.EngineFactory> { [ModelRuntime.WhisperNet] = (_, _) => engine },
            (_, _, _) =>
            {
                diarizerCalls++;
                return new FakeDiarizer();
            },
            e => e.Id == "whisper-base" || (diarizerInstalled && e.Id == Diarizer),
            e => Path.Combine(_root, "Models", e.Id),
            new ModelLoadGate());
        var job = new TranscriptionJob { ConversationID = session.Id, Folder = folder, ProcessNow = true, Diarize = diarize };

        await pipeline.TranscribeAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(0, diarizerCalls);
        Assert.Equal([seconds * 16_000], engine.SampleCounts);
        var transcript = Transcript.Read(folder);
        Assert.Equal(diarize ? [Transcript.DiarizationMissingNote] : [], transcript.Notes);
        Assert.Equal("whisper-base", transcript.EngineID);
        Assert.Equal(2, transcript.Segments.Count);
        Assert.All(transcript.Segments, s => Assert.Equal(Transcript.Speaker.System(1), s.Speaker));
        var conversation = Conversation.Read(folder);
        Assert.Equal(ConversationStatus.Transcribed, conversation.Status);
        Assert.True(conversation.HasTranscript);
        Assert.InRange(conversation.Duration, seconds - 0.01, seconds + 0.01);
    }

    // Energy saver on: the job reports Deferred and decodes nothing until the signal clears (its Changed event, no
    // polling) or "Transcribe now" releases it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeferredJobWaitsUntilTheSignalClearsOrTranscribeNow(bool transcribeNow)
    {
        var session = new RecordingSession { State = SessionState.Stopped, AudioFileName = "audio.caf" };
        var folder = session.Folder(Path.Combine(_root, "Conversations"));
        session.Write(folder);
        WriteStereo(1, Path.Combine(folder, session.AudioFileName));
        var engine = new FakeEngine();
        var signal = new FakeDeferral { ShouldDefer = true };
        var pipeline = new TranscriptionPipeline(
            new Dictionary<ModelRuntime, TranscriptionPipeline.EngineFactory> { [ModelRuntime.WhisperNet] = (_, _) => engine },
            null, e => e.Id == "whisper-base", e => Path.Combine(_root, "Models", e.Id), new ModelLoadGate(),
            deferral: signal);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.Progress += p =>
        {
            if (p.Step == JobStep.Deferred) deferred.TrySetResult();
        };
        var job = new TranscriptionJob { ConversationID = session.Id, Folder = folder, Diarize = false };

        var run = pipeline.TranscribeAsync(job, TestContext.Current.CancellationToken);
        await deferred.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Empty(engine.SampleCounts);
        if (transcribeNow)
        {
            Assert.True(pipeline.ProcessNow(session.Id));
        }
        else
        {
            signal.ShouldDefer = false;
            signal.Raise();
        }
        await run.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal([16_000], engine.SampleCounts);
    }

    private sealed class FakeDeferral : IDeferralSignal
    {
        public bool ShouldDefer { get; set; }

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();
    }

    /// <summary>A 16 kHz stereo Int16 CAF, as the recorder writes: silent mic (ch0), a 440 Hz tone on the system channel (ch1).</summary>
    private static void WriteStereo(int seconds, string path)
    {
        var frames = seconds * 16_000;
        var samples = new short[frames * 2];
        for (var i = 0; i < frames; i++) samples[i * 2 + 1] = (short)(Math.Sin(i * 2 * Math.PI * 440 / 16_000) * 16_000);
        using var writer = new CafFile.Writer(path, 16_000, 2, CafFile.SampleFormat.Int16);
        writer.Append(samples);
    }

    /// <summary>Reads its channel file as a real engine would and returns two segments without speaker labels.</summary>
    private sealed class FakeEngine : ITranscriptionEngine
    {
        public List<int> SampleCounts { get; } = [];

        public Task<IReadOnlyList<RawSegment>> TranscribeAsync(
            string channelPath, IReadOnlyList<LanguageRange> languageRanges, Action<double> progress,
            CancellationToken cancellationToken)
        {
            SampleCounts.Add(MonoAudio.Read(channelPath, 0, null).Length);
            progress(1);
            return Task.FromResult<IReadOnlyList<RawSegment>>(
                [new RawSegment(0, 0.8, "hello there"), new RawSegment(1, 1.8, "general kenobi")]);
        }
    }

    private sealed class FakeDiarizer : ISpeakerDiarizer
    {
        public Task<IReadOnlyList<DiarizationTurn>> DiarizeAsync(
            string channelPath, Action<double> progress, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiarizationTurn>>([new DiarizationTurn(0, 1, 0), new DiarizationTurn(1, 2, 1)]);
    }
}
