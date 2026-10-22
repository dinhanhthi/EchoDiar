using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using EchoDiar.Core.Models;
using EchoDiar.Transcribe;

namespace EchoDiar.Tests.Transcribe;

// Security of the only model downloader: what the HF tree lists is untrusted, and a model is installed only
// after every file matches its listed size and SHA-256.
public sealed class ModelStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHub _hub = new();

    public ModelStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Models => Path.Combine(_dir, "Models");

    private ModelStore Store() => new(Models, new HttpClient(_hub), freeBytes: _ => long.MaxValue);

    // A traversal path or a Windows device name in the listing fails the whole download before any file is
    // fetched or written, even under an allowlisted prefix.
    [Theory]
    [InlineData("../../pwned.bin")]
    [InlineData("..\\..\\pwned.bin")]
    [InlineData("/pwned.bin")]
    [InlineData("NUL")]
    [InlineData("ggml-base.bin/CON.bin")]
    public async Task ATraversalPathInTheTreeIsRejectedAndNothingIsWritten(string hostile)
    {
        var entry = ModelCatalog.Entry("whisper-base")! with { DiskBytes = 4 };
        _hub.Tree(entry.Repo!, entry.Revision!, [Remote("ggml-base.bin", [1, 2, 3, 4]), Remote(hostile, [6, 6, 6])]);

        var error = await Assert.ThrowsAsync<ModelStoreException>(() => Store().DownloadAsync(entry, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ModelStoreError.UnsafePath, error.Kind);
        Assert.DoesNotContain(_hub.Requests, uri => uri.AbsolutePath.Contains("/resolve/"));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(Models, ".partial-" + entry.Id)));
        Assert.False(Directory.Exists(Path.Combine(Models, entry.Id)));
    }

    // A SHA-256 mismatch on any file (here the second repo's) leaves no installed model and no staging folder;
    // with the right hashes, both repos' files land in Models/<id>/ and the model is installed.
    [Fact]
    public async Task AShaMismatchLeavesNoInstalledModel()
    {
        byte[] segmentation = [1, 2, 3, 4, 5];
        byte[] embedding = [7, 8, 9];
        var entry = ModelCatalog.Entry("sherpa-pyannote-3")! with { DiskBytes = 8 };
        var extra = entry.ExtraFiles!;
        var embeddingName = extra.Files[0];
        var installed = Path.Combine(Models, entry.Id);
        var store = Store();

        // The main repo's listing is paginated; README.md is not allowlisted.
        _hub.Tree(entry.Repo!, entry.Revision!, [Remote("README.md", [0])], nextCursor: "page2");
        _hub.Tree(entry.Repo!, entry.Revision!, [Remote("model.onnx", segmentation)], cursor: "page2");
        _hub.Tree(extra.Repo, extra.Revision, [Remote(embeddingName, embedding, sha: Sha([0xBA, 0xD]))]);

        var error = await Assert.ThrowsAsync<ModelStoreException>(() => store.DownloadAsync(entry, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ModelStoreError.ChecksumMismatch, error.Kind);
        Assert.False(store.IsInstalled(entry));
        Assert.False(Directory.Exists(installed));
        Assert.False(Directory.Exists(Path.Combine(Models, ".partial-" + entry.Id)));

        _hub.Tree(extra.Repo, extra.Revision, [Remote(embeddingName, embedding)]);
        await store.DownloadAsync(entry, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(store.IsInstalled(entry));
        Assert.Equal(segmentation, System.IO.File.ReadAllBytes(Path.Combine(installed, "model.onnx")));
        Assert.Equal(embedding, System.IO.File.ReadAllBytes(Path.Combine(installed, embeddingName)));
        Assert.Equal([embeddingName, "model.onnx"], Directory.EnumerateFiles(installed).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(Models, ".partial-" + entry.Id)));

        // A reinstall whose final move fails (its staging folder vanished) keeps the previous install.
        var staging = Path.Combine(Models, ".partial-" + entry.Id);
        var vanish = new SyncProgress(p => { if (p.File == "") Directory.Delete(staging, recursive: true); });
        await Assert.ThrowsAnyAsync<IOException>(() => store.DownloadAsync(entry, vanish, TestContext.Current.CancellationToken));

        Assert.True(store.IsInstalled(entry));
        Assert.Equal(segmentation, System.IO.File.ReadAllBytes(Path.Combine(installed, "model.onnx")));
        Assert.Equal([entry.Id], Directory.EnumerateDirectories(Models).Select(Path.GetFileName));
    }

    // A file the listing does not mark as LFS needs the catalog's SHA-256, and the catalog's hash wins over a
    // listed LFS oid; otherwise nothing is installed. With the right catalog hash it installs.
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AFileIsCheckedAgainstTheCatalogHash(bool listedAsLfs, bool catalogHasHash)
    {
        byte[] tokens = [1, 2, 3];
        var entry = ModelCatalog.Entry("parakeet-v3-int8")! with
        {
            Files = ["tokens.txt"], DiskBytes = tokens.Length,
            Sha256 = catalogHasHash ? new Dictionary<string, string> { ["tokens.txt"] = Sha([9, 9, 9]) } : null,
        };
        _hub.Tree(entry.Repo!, entry.Revision!, [("tokens.txt", tokens, listedAsLfs ? Sha(tokens) : null)]);
        var store = Store();

        var error = await Assert.ThrowsAsync<ModelStoreException>(() => store.DownloadAsync(entry, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ModelStoreError.ChecksumMismatch, error.Kind);
        if (!listedAsLfs && !catalogHasHash) Assert.DoesNotContain(_hub.Requests, uri => uri.AbsolutePath.Contains("/resolve/"));
        Assert.False(store.IsInstalled(entry));
        Assert.False(Directory.Exists(Path.Combine(Models, entry.Id)));
        Assert.False(Directory.Exists(Path.Combine(Models, ".partial-" + entry.Id)));

        await store.DownloadAsync(entry with { Sha256 = new Dictionary<string, string> { ["tokens.txt"] = Sha(tokens) } },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(store.IsInstalled(entry));
    }

    // A listing whose next page links back to itself fails instead of paging forever.
    [Fact]
    public async Task ALoopingListingFails()
    {
        var entry = ModelCatalog.Entry("whisper-base")! with { DiskBytes = 4 };
        _hub.Tree(entry.Repo!, entry.Revision!, [Remote("ggml-base.bin", [1, 2, 3, 4])], nextCursor: "loop");
        _hub.Tree(entry.Repo!, entry.Revision!, [], cursor: "loop", nextCursor: "loop");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<ModelStoreException>(() => Store().DownloadAsync(entry, cancellationToken: timeout.Token));

        Assert.Equal(ModelStoreError.ListingFailed, error.Kind);
        Assert.True(_hub.Requests.Count <= 3);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static (string Path, byte[] Data, string? Sha) Remote(string path, byte[] data, string? sha = null) =>
        (path, data, sha ?? Sha(data));

    private sealed class SyncProgress(Action<ModelDownloadProgress> report) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => report(value);
    }

    /// <summary>Serves HF tree pages and resolve downloads from memory and records every request.</summary>
    private sealed class FakeHub : HttpMessageHandler
    {
        private readonly Dictionary<string, (byte[] Body, string? Link)> _responses = [];

        public List<Uri> Requests { get; } = [];

        public void Tree(
            string repo, string revision, (string Path, byte[] Data, string? Sha)[] files,
            string? cursor = null, string? nextCursor = null)
        {
            var tree = $"https://huggingface.co/api/models/{repo}/tree/{revision}?recursive=1";
            var items = files.Select(f => new
            {
                type = "file",
                oid = "0000000000000000000000000000000000000000",
                size = f.Data.Length,
                path = f.Path,
                // A null SHA lists the file as a plain git blob (no lfs block).
                lfs = f.Sha is null ? null : new { oid = f.Sha, size = f.Data.Length, pointerSize = 134 },
            });
            string? link = nextCursor is null ? null : $"<{tree}&cursor={nextCursor}>; rel=\"next\"";
            _responses[cursor is null ? tree : $"{tree}&cursor={cursor}"] = (JsonSerializer.SerializeToUtf8Bytes(items), link);
            foreach (var file in files)
            {
                _responses[$"https://huggingface.co/{repo}/resolve/{revision}/{file.Path}"] = (file.Data, null);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            if (!_responses.TryGetValue(request.RequestUri!.OriginalString, out var response))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
            }
            var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response.Body) };
            if (response.Link is not null) message.Headers.TryAddWithoutValidation("Link", response.Link);
            return Task.FromResult(message);
        }
    }
}
