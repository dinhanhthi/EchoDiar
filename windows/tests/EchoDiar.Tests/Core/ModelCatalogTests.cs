using System.Text.RegularExpressions;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.Core;

// Guards the catalog's shape: settings and Models/<id>/ folders key on unique ids, each kind has one default, and a
// download is pinned (revision) and checked (SHA-256, size) before it is installed and again before it is loaded.
// Swift has no catalog test; these are new.
public sealed partial class ModelCatalogTests
{
    [Fact]
    public void IdsAreUnique() =>
        Assert.Equal(ModelCatalog.All.Count, ModelCatalog.All.Select(e => e.Id).Distinct().Count());

    [Theory]
    [InlineData(ModelKind.Asr)]
    [InlineData(ModelKind.Diarization)]
    [InlineData(ModelKind.Llm)]
    [InlineData(ModelKind.Embedding)]
    public void EachKindHasExactlyOneDefault(ModelKind kind) =>
        Assert.Single(ModelCatalog.All, e => e.Kind == kind && e.IsDefault);

    [Fact]
    public void EveryGgufEntryIsOnePinnedFileWithItsHashAndSize()
    {
        var entries = ModelCatalog.All.Where(e => e.Runtime == ModelRuntime.Gguf).ToList();

        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrEmpty(entry.Repo));
            Assert.Matches(Sha1Hex(), entry.Revision!);
            var file = Assert.Single(entry.Files!);
            Assert.EndsWith(".gguf", file);
            Assert.Matches(Sha256Hex(), entry.Sha256![file]);
            Assert.True(entry.DiskBytes > 0);
        });
    }

    // ModelStore checks an install again against these hashes (its .verified marker, or a hash of the files once),
    // so every downloadable file needs one, the second repo's included.
    [Fact]
    public void EveryDownloadableFileHasACatalogHash()
    {
        var entries = ModelCatalog.All.Where(e => e.Repo is not null).ToList();

        Assert.All(entries, entry =>
        {
            Assert.All(entry.Files!, file => Assert.Matches(Sha256Hex(), entry.Sha256![file]));
            if (entry.ExtraFiles is { } extra) Assert.All(extra.Files, file => Assert.Matches(Sha256Hex(), extra.Sha256![file]));
        });
    }

    // LlamaProvider.ContextWindowAsync reports min(RAM cap, ContextTokens) without loading the model: every GGUF chat
    // model has its training window, and none is under the 32K cap (a shorter one would need its own prompt budget).
    [Fact]
    public void EveryGgufChatModelHasItsTrainingWindow() =>
        Assert.All(ModelCatalog.Llm.Where(e => e.Runtime == ModelRuntime.Gguf), entry => Assert.True(entry.ContextTokens >= 32_768, entry.Id));

    [Fact]
    public void TheDefaultEmbedderIsTheSettingsDefault() =>
        Assert.Equal(AppSettings.DefaultEmbeddingModelID, ModelCatalog.Embedding.Single(e => e.IsDefault).Id);

    // A settings.json from macOS keeps "mlx-qwen3-embedding-0.6b": it must pick the same model (and index key) here.
    [Theory]
    [InlineData("mlx-qwen3-embedding-0.6b", "gguf-qwen3-embedding-0.6b")]
    [InlineData("gguf-qwen3-embedding-0.6b", "gguf-qwen3-embedding-0.6b")]
    [InlineData("api:ollama", null)]
    [InlineData("apple-nl", null)]
    [InlineData("gguf-qwen3.5-4b", null)]
    public void EmbeddingIdPicksItsGgufEntry(string id, string? entry) =>
        Assert.Equal(entry, ModelCatalog.EmbeddingEntry(id)?.Id);

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha1Hex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();
}
