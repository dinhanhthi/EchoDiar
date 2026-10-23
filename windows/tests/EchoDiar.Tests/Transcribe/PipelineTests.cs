using EchoDiar.AI;
using EchoDiar.AI.Cloud;
using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Transcribe;

namespace EchoDiar.Tests.Transcribe;

// The batch pipeline without models (Swift EngineRoutingTests + SpeakersOffPipelineTests): which installed engine
// transcribes a job, and a whole job through fake engines on a CAF fixture; cloud engines and diarizers through fake
// transcribers and a fake exporter (no network).
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

    // A cloud engine (macOS cloud branch): each channel goes to the API from its source file (not the 16 kHz extract),
    // only the system channel asks for speakers, no local engine or diarizer runs when the provider labels speakers,
    // and the transcript names the cloud engine and its model.
    [Fact]
    public async Task ACloudEngineTranscribesEachChannelFromItsSourceAndKeepsTheProviderSpeakers()
    {
        var (session, folder) = MakeRecording(2, toneOnMic: true);
        var engine = new FakeEngine();
        var diarizer = new FakeDiarizer();
        var exporter = new FakeExporter();
        var cloud = new FakeCloud();
        var pipeline = new TranscriptionPipeline(
            new Dictionary<ModelRuntime, TranscriptionPipeline.EngineFactory> { [ModelRuntime.WhisperNet] = (_, _) => engine },
            (_, _, _) => diarizer,
            e => e.Id is "whisper-base" or Diarizer, e => Path.Combine(_root, "Models", e.Id), new ModelLoadGate(),
            exporter: exporter);
        var job = new TranscriptionJob { ConversationID = session.Id, Folder = folder, ProcessNow = true, Diarize = true };

        await pipeline.TranscribeAsync(
            job, new CloudEngine("cloud:deepgram", "Deepgram", "nova-3", cloud), null, TestContext.Current.CancellationToken);

        Assert.Empty(engine.SampleCounts);
        Assert.Equal(0, diarizer.Runs);
        var audio = Path.Combine(folder, session.AudioFileName);
        Assert.Equal([(audio, 0), (audio, 1)], exporter.Exports);
        Assert.Equal([false, true], cloud.Diarize);
        var transcript = Transcript.Read(folder);
        Assert.Equal("cloud:deepgram", transcript.EngineID);
        Assert.Equal("nova-3", transcript.ModelID);
        Assert.Empty(transcript.Notes);
        Assert.Equal(
            [Transcript.Speaker.Me, Transcript.Speaker.System(1), Transcript.Speaker.System(2)],
            transcript.Segments.Select(s => s.Speaker).Distinct().Order(StringComparer.Ordinal).ToList());
        Assert.Equal(ConversationStatus.Transcribed, Conversation.Read(folder).Status);
    }

    // A failed cloud request fails the job with the provider and a short reason (never the response body), and the
    // card shows it: conversation.json says transcription failed.
    [Fact]
    public async Task AFailedCloudRequestFailsTheJobWithAShortReason()
    {
        var (session, folder) = MakeRecording(1, toneOnMic: false);
        var pipeline = new TranscriptionPipeline(
            new Dictionary<ModelRuntime, TranscriptionPipeline.EngineFactory>(), null, _ => false,
            e => Path.Combine(_root, "Models", e.Id), new ModelLoadGate(), exporter: new FakeExporter());
        string? failure = null;
        pipeline.Progress += p =>
        {
            if (p.Step == JobStep.Failed) failure = p.Detail;
        };
        var cloud = new FakeCloud { Error = LLMException.HttpStatus(401, "{\"error\":\"secret-body\"}") };
        var job = new TranscriptionJob { ConversationID = session.Id, Folder = folder, ProcessNow = true, Diarize = false };

        var error = await Assert.ThrowsAsync<TranscriptionException>(() => pipeline.TranscribeAsync(
            job, new CloudEngine("cloud:deepgram", "Deepgram", "nova-3", cloud), null, TestContext.Current.CancellationToken));

        Assert.Equal(TranscriptionErrorKind.Cloud, error.Kind);
        Assert.Equal("Deepgram: invalid API key", failure);
        Assert.Equal(ConversationStatus.TranscriptionFailed, Conversation.Read(folder).Status);
        Assert.False(File.Exists(Path.Combine(folder, Transcript.FileName)));
    }

    // The cloud diarizer runs first and its turns label the system channel; the local diarizer then never runs.
    // When it fails or labels no speaker, the local diarizer runs and the transcript notes the cloud failure.
    [Theory]
    [InlineData("labels")]
    [InlineData("throws")]
    [InlineData("no speakers")]
    public async Task TheCloudDiarizerRunsFirstAndTheLocalOneAfterItsFailure(string outcome)
    {
        var (session, folder) = MakeRecording(2, toneOnMic: false);
        var diarizer = new FakeDiarizer();
        var pipeline = new TranscriptionPipeline(
            new Dictionary<ModelRuntime, TranscriptionPipeline.EngineFactory> { [ModelRuntime.WhisperNet] = (_, _) => new FakeEngine() },
            (_, _, _) => diarizer,
            e => e.Id is "whisper-base" or Diarizer, e => Path.Combine(_root, "Models", e.Id), new ModelLoadGate(),
            exporter: new FakeExporter());
        var cloud = outcome switch
        {
            "throws" => new FakeCloud { Error = new HttpRequestException("network") },
            "no speakers" => new FakeCloud { Labels = false },
            _ => new FakeCloud(),
        };
        var job = new TranscriptionJob { ConversationID = session.Id, Folder = folder, ProcessNow = true, Diarize = true };

        await pipeline.TranscribeAsync(
            job, null, new CloudEngine("cloud:assemblyai", "AssemblyAI", "", cloud), TestContext.Current.CancellationToken);

        Assert.Equal([true], cloud.Diarize);
        var transcript = Transcript.Read(folder);
        Assert.Equal("whisper-base", transcript.EngineID);
        if (outcome == "labels")
        {
            Assert.Equal(0, diarizer.Runs);
            Assert.Empty(transcript.Notes);
        }
        else
        {
            Assert.Equal(1, diarizer.Runs);
            Assert.Equal([Transcript.CloudDiarizationFailedNote], transcript.Notes);
        }
        // Either way the two FakeEngine segments get two speakers (cloud turns or the FakeDiarizer's).
        Assert.Equal([Transcript.Speaker.System(1), Transcript.Speaker.System(2)], transcript.Segments.Select(s => s.Speaker).ToList());
    }

    // "Tell speakers apart" off: the cloud diarizer is never asked (no upload, nothing billed).
    [Fact]
    public async Task SpeakersOffNeverUploadsToTheCloudDiarizer()
    {
        var (session, folder) = MakeRecording(1, toneOnMic: false);
        var exporter = new FakeExporter();
        var pipeline = new TranscriptionPipeline(
            new Dictionary<ModelRuntime, TranscriptionPipeline.EngineFactory> { [ModelRuntime.WhisperNet] = (_, _) => new FakeEngine() },
            null, e => e.Id == "whisper-base", e => Path.Combine(_root, "Models", e.Id), new ModelLoadGate(), exporter: exporter);
        var cloud = new FakeCloud();
        var job = new TranscriptionJob { ConversationID = session.Id, Folder = folder, ProcessNow = true, Diarize = false };

        await pipeline.TranscribeAsync(
            job, null, new CloudEngine("cloud:assemblyai", "AssemblyAI", "", cloud), TestContext.Current.CancellationToken);

        Assert.Empty(exporter.Exports);
        Assert.Empty(cloud.Diarize);
    }

    private (RecordingSession Session, string Folder) MakeRecording(int seconds, bool toneOnMic)
    {
        var session = new RecordingSession { State = SessionState.Stopped, AudioFileName = "audio.caf" };
        var folder = session.Folder(Path.Combine(_root, "Conversations"));
        session.Write(folder);
        WriteStereo(seconds, Path.Combine(folder, session.AudioFileName), toneOnMic);
        return (session, folder);
    }

    /// <summary>Records each export and writes a small file in its place, as long as the source channel would be.</summary>
    private sealed class FakeExporter : IAudioExporter
    {
        public List<(string Source, int Channel)> Exports { get; } = [];

        public double ExportMonoAac(string source, int channel, TimeRange? range, string destination, CancellationToken cancellationToken)
        {
            lock (Exports) Exports.Add((source, channel));
            File.WriteAllBytes(destination, new byte[1000]);
            return MonoAudio.Duration(source);
        }

        public float[] Levels(string source, int channel, double window, CancellationToken cancellationToken) => [];
    }

    /// <summary>
    /// Answers every upload with two segments, labeled A and B when speakers are asked for (unless
    /// <see cref="Labels"/> is false), or throws <see cref="Error"/>; records each request's diarize flag.
    /// </summary>
    private sealed class FakeCloud : ICloudTranscriber
    {
        private int _calls;

        public Exception? Error { get; init; }

        public bool Labels { get; init; } = true;

        public List<bool> Diarize { get; } = [];

        public Task<IReadOnlyList<CloudSegment>> TranscribeAsync(
            string file, double duration, string? language, bool diarize, CancellationToken cancellationToken = default)
        {
            lock (Diarize) Diarize.Add(diarize);
            if (Error is { } error) throw error;
            // Words differ per upload, so the merger never takes the mic's for bleed of the system's.
            var words = Interlocked.Increment(ref _calls) % 2 == 1 ? ("alpha bravo", "charlie delta") : ("echo foxtrot", "golf hotel");
            var labels = diarize && Labels;
            return Task.FromResult<IReadOnlyList<CloudSegment>>(
            [
                new CloudSegment(0, 0.8, words.Item1, labels ? "A" : null),
                new CloudSegment(1, 1.8, words.Item2, labels ? "B" : null),
            ]);
        }
    }

    private sealed class FakeDeferral : IDeferralSignal
    {
        public bool ShouldDefer { get; set; }

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();
    }

    /// <summary>
    /// A 16 kHz stereo Int16 CAF, as the recorder writes: a silent mic (ch0; a 330 Hz tone with <paramref name="toneOnMic"/>),
    /// a 440 Hz tone on the system channel (ch1).
    /// </summary>
    private static void WriteStereo(int seconds, string path, bool toneOnMic = false)
    {
        var frames = seconds * 16_000;
        var samples = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[i * 2 + 1] = (short)(Math.Sin(i * 2 * Math.PI * 440 / 16_000) * 16_000);
            if (toneOnMic) samples[i * 2] = (short)(Math.Sin(i * 2 * Math.PI * 330 / 16_000) * 16_000);
        }
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
        private int _runs;

        /// <summary>How many times it diarized (the pipeline makes it per job, whether it runs or not).</summary>
        public int Runs => _runs;

        public Task<IReadOnlyList<DiarizationTurn>> DiarizeAsync(
            string channelPath, Action<double> progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult<IReadOnlyList<DiarizationTurn>>([new DiarizationTurn(0, 1, 0), new DiarizationTurn(1, 2, 1)]);
        }
    }
}
