using EchoDiar.Core.Models;
using EchoDiar.Transcribe;
using EchoDiar.Transcribe.Engines;
using NAudio.Wave;

namespace EchoDiar.Tests.Transcribe;

// The real engines on their native libraries (CI runs them on x64 and ARM64): a pinned model downloaded through
// ModelStore (SHA-256 checked) decodes a 10 s two-voice fixture. Models are kept in ECHODIAR_TEST_MODELS
// (default ~/.cache/echodiar-test-models) and only downloaded when not installed there.
[Trait("Category", "Native")]
public sealed class NativeSmokeTests : IDisposable
{
    /// <summary>Spoken by both voices of the fixture ("Good morning..." / "Morning...").</summary>
    private const string KnownWord = "morning";

    private static readonly string ModelsRoot = Environment.GetEnvironmentVariable("ECHODIAR_TEST_MODELS") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "echodiar-test-models");

    // Test-only entry: the catalog has no tiny model. Same repo and revision as the catalog's Whisper entries.
    private static readonly ModelEntry WhisperTiny = ModelCatalog.Entry("whisper-base")! with
    {
        Id = "whisper-tiny-test",
        Files = ["ggml-tiny.bin"],
        Sha256 = new Dictionary<string, string>
        {
            ["ggml-tiny.bin"] = "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
        },
        DiskBytes = 77_691_713,
    };

    // The catalog diarizer with its two files' hashes pinned here (the catalog relies on the listed LFS oids).
    private static readonly ModelEntry Diarizer = PinnedDiarizer();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarNative-" + Guid.NewGuid().ToString("N"));
    private readonly string _audio;

    public NativeSmokeTests()
    {
        Directory.CreateDirectory(_dir);
        _audio = Path.Combine(_dir, "two-voice.caf");
        var wav = Path.Combine(AppContext.BaseDirectory, "Fixtures", "two-voice-16k.wav");
        MonoAudio.Extract(0, wav, _audio, ReadWav);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine("NativeSmoke: " + line);

    [Fact]
    public async Task WhisperTranscribesTheFixture()
    {
        var folder = await Installed(WhisperTiny);

        var segments = await Native(() => WhisperEngine.Create(WhisperTiny, folder)
            .TranscribeAsync(_audio, [new LanguageRange(0, null, "en")], _ => { }, Token));

        var text = string.Join(" ", segments.Select(s => s.Text));
        Log($"whisper ({Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}): {text}");
        Assert.Contains(KnownWord, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheDiarizerFindsTwoSpeakers()
    {
        var folder = await Installed(Diarizer);

        var turns = await Native(() => SherpaDiarizer.Create(Diarizer, folder, null).DiarizeAsync(_audio, _ => { }, Token));

        var speakers = turns.Select(t => t.SpeakerIndex).Distinct().Count();
        Log($"diarizer ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}): {speakers} speakers, turns "
            + string.Join(", ", turns.Select(t => $"{t.Start:0.00}-{t.End:0.00}#{t.SpeakerIndex}")));
        Assert.Equal(2, speakers);
    }

    // Opt-in: the model is 640 MB. Goes through ParakeetEngine (60 s slabs), never a raw sherpa stream.
    [Fact]
    public async Task ParakeetTranscribesTheFixture()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ECHODIAR_TEST_PARAKEET") == "1",
            "Set ECHODIAR_TEST_PARAKEET=1 to download and run Parakeet (640 MB).");
        var entry = ModelCatalog.Entry("parakeet-v3-int8")!;
        var folder = await Installed(entry);

        var segments = await Native(() => ParakeetEngine.Create(entry, folder)
            .TranscribeAsync(_audio, [new LanguageRange(0, null, null)], _ => { }, Token));

        var text = string.Join(" ", segments.Select(s => s.Text));
        Log($"parakeet: {text}");
        Assert.Contains(KnownWord, text, StringComparison.OrdinalIgnoreCase);
    }

    private static ModelEntry PinnedDiarizer()
    {
        var entry = ModelCatalog.Entry("sherpa-pyannote-3")!;
        var extra = entry.ExtraFiles!;
        return entry with
        {
            Sha256 = new Dictionary<string, string>
            {
                ["model.onnx"] = "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079",
            },
            ExtraFiles = extra with
            {
                Sha256 = new Dictionary<string, string>
                {
                    [extra.Files[0]] = "aa3cfc16963a10586a9393f5035d6d6b57e98d358b347f80c2a30bf4f00ceba2",
                },
            },
        };
    }

    /// <summary>The entry's folder under <see cref="ModelsRoot"/>, downloaded and verified by ModelStore unless already installed.</summary>
    private static async Task<string> Installed(ModelEntry entry)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var store = new ModelStore(ModelsRoot, http);
        if (!store.IsInstalled(entry)) await store.DownloadAsync(entry, cancellationToken: Token);
        Assert.True(store.IsInstalled(entry), $"{entry.Id} is not installed in {ModelsRoot}");
        return store.Folder(entry);
    }

    /// <summary>
    /// Runs an engine call; when its native library cannot be loaded, the test is skipped, except on Windows,
    /// where the natives must ship.
    /// </summary>
    private static async Task<T> Native<T>(Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or TypeInitializationException || error.Message.Contains("native", StringComparison.OrdinalIgnoreCase))
        {
            if (OperatingSystem.IsWindows()) throw;
            Assert.Skip($"Native library unavailable on this OS: {error.Message}");
            throw;
        }
    }

    /// <summary>The fixture's PCM16 samples as floats (the decoder hook of <see cref="MonoAudio.Extract"/>).</summary>
    private static float[] ReadWav(string path, int channel)
    {
        using var reader = new WaveFileReader(path);
        Assert.Equal(new WaveFormat((int)MonoAudio.SampleRate, 16, 1), reader.WaveFormat);
        var samples = new float[reader.SampleCount];
        var read = reader.ToSampleProvider().Read(samples);
        return samples[..read];
    }
}
