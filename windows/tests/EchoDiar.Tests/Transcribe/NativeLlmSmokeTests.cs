using System.Diagnostics;
using EchoDiar.AI;
using EchoDiar.Core.Models;
using EchoDiar.LocalLLM;
using EchoDiar.Transcribe;
using LLama;
using LLama.Common;

namespace EchoDiar.Tests.Transcribe;

// The GGUF adapters on the llama.cpp natives (CI runs them on x64 and ARM64; Windows only, where the natives ship):
// pinned models downloaded through ModelStore (SHA-256 checked) into ECHODIAR_TEST_MODELS, as NativeSmokeTests does.
[Trait("Category", "NativeLLM")]
public sealed class NativeLlmSmokeTests
{
    // Test-only entry: the smallest Qwen3 chat model (the catalog's start at 2B).
    private static readonly ModelEntry Qwen3Small = ModelCatalog.Entry("gguf-qwen3.5-2b")! with
    {
        Id = "gguf-qwen3-0.6b-test",
        DisplayName = "Qwen3 0.6B",
        Repo = "Qwen/Qwen3-0.6B-GGUF",
        Revision = "23749fefcc72300e3a2ad315e1317431b06b590a",
        Files = ["Qwen3-0.6B-Q8_0.gguf"],
        Sha256 = new Dictionary<string, string>
        {
            ["Qwen3-0.6B-Q8_0.gguf"] = "9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031",
        },
        DiskBytes = 639_446_688,
        IsDefault = false,
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void SkipUnlessWindows() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The llama.cpp natives are tested on Windows only.");

    private static string ModelFile(string folder, ModelEntry entry) => Path.Combine(folder, entry.Files![0]);

    [Fact]
    public async Task LlamaProviderGeneratesText()
    {
        SkipUnlessWindows();
        var folder = await NativeSmokeTests.Installed(Qwen3Small);
        var provider = new LlamaProvider(Qwen3Small.Id, ModelFile(folder, Qwen3Small));

        var pieces = 0;
        var text = await provider.WithLoadedModelAsync(Qwen3Small.Id, generator => generator.GenerateAsync(
            [new LLMMessage(LLMRole.User, "Reply with the word hello.")], maxTokens: 16, _ => pieces++, Token), Token);

        NativeSmokeTests.Log($"qwen3-0.6b ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}): "
            + $"{pieces} pieces: {text}");
        Assert.True(pieces >= 1, "No token was generated.");
        Assert.NotEmpty(text);
    }

    [Fact]
    public async Task LlamaEmbedderSeparatesTexts()
    {
        SkipUnlessWindows();
        var entry = ModelCatalog.Entry("gguf-qwen3-embedding-0.6b")!;
        var folder = await NativeSmokeTests.Installed(entry);
        var embedder = new LlamaEmbedder(entry.Id, ModelFile(folder, entry));
        const string meeting = "The quarterly budget review moved to Thursday afternoon.";
        const string other = "Bananas are a good source of potassium.";

        var vectors = await embedder.WithLoadedAsync(loaded => loaded.EmbedAsync([meeting, meeting, other], isQuery: false, Token), Token);

        // L2-normalised vectors: the dot product is the cosine.
        var same = Dot(vectors[0], vectors[1]);
        var different = Dot(vectors[0], vectors[2]);
        NativeSmokeTests.Log($"qwen3-embedding: {vectors[0].Length} dims, cosine same {same:0.0000}, different {different:0.0000}");
        Assert.True(same > 0.99, $"Identical texts: cosine {same}");
        Assert.True(different < same, $"Different texts: cosine {different}, identical {same}");
    }

    // Opt-in: about 18 GB of downloads. Each catalog GGUF in turn is downloaded into a temp folder (never the cached
    // models), opened header and vocabulary only (llama.cpp knows the architecture), then deleted before the next,
    // so at most one model is on disk.
    [Fact]
    public async Task EveryCatalogGgufLoads()
    {
        SkipUnlessWindows();
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ECHODIAR_TEST_GGUF_ALL") == "1",
            "Set ECHODIAR_TEST_GGUF_ALL=1 to download and open every catalog GGUF (about 18 GB, one at a time).");
        var root = Path.Combine(Path.GetTempPath(), "EchoDiarGguf-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var store = new ModelStore(root, http);
        var failures = new List<string>();
        try
        {
            foreach (var entry in ModelCatalog.All.Where(e => e.Runtime == ModelRuntime.Gguf))
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    await store.DownloadAsync(entry, cancellationToken: Token);
                    var download = clock.Elapsed;
                    using (var weights = LLamaWeights.LoadFromFile(new ModelParams(ModelFile(store.Folder(entry), entry)) { VocabOnly = true }))
                    {
                        var arch = weights.Metadata.GetValueOrDefault("general.architecture") ?? "?";
                        NativeSmokeTests.Log($"gguf {entry.Id}: download {download.TotalSeconds:0} s, load OK ({arch}, "
                            + $"{weights.Vocab.Count} tokens)");
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    NativeSmokeTests.Log($"gguf {entry.Id}: FAILED after {clock.Elapsed.TotalSeconds:0} s: "
                        + $"{error.GetType().Name}: {error.Message}");
                    failures.Add($"{entry.Id}: {error.Message}");
                }
                finally
                {
                    // Throws (ending the loop) rather than leave a model behind for the next download.
                    store.Delete(entry);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        Assert.Empty(failures);
    }

    private static double Dot(float[] a, float[] b)
    {
        Assert.Equal(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
