using System.Text.RegularExpressions;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.Core;

// Guards the catalog's shape: settings and Models/<id>/ folders key on unique ids, each kind has one default, and a
// GGUF download is pinned (revision) and checked (SHA-256, size) before it is installed. Swift has no catalog test;
// these are new.
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

    [Fact]
    public void TheDefaultEmbedderIsTheSettingsDefault() =>
        Assert.Equal(AppSettings.DefaultEmbeddingModelID, ModelCatalog.Embedding.Single(e => e.IsDefault).Id);

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha1Hex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();
}
