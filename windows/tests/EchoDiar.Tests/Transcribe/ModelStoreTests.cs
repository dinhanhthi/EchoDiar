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
        var catalog = ModelCatalog.Entry("sherpa-pyannote-3")!;
        // Listed hashes only (the catalog's would not match these bytes).
        var entry = catalog with { DiskBytes = 8, Sha256 = null, ExtraFiles = catalog.ExtraFiles! with { Sha256 = null } };
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
        Assert.Equal([ModelStore.MarkerName, embeddingName, "model.onnx"],
            Directory.EnumerateFiles(installed).Select(Path.GetFileName).Order(StringComparer.Ordinal));
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

    // The SHA-256s checked at download are kept in Models/<id>/.verified, and a load (VerifiedFolder) goes on only
    // while it matches the catalog and each file's size and last write time.
    [Fact]
    public async Task ADownloadWritesTheMarkerALoadChecks()
    {
        byte[] model = [1, 2, 3, 4];
        var entry = Base(model);
        _hub.Tree(entry.Repo!, entry.Revision!, [Remote("ggml-base.bin", model)]);
        var store = Store();

        await store.DownloadAsync(entry, cancellationToken: TestContext.Current.CancellationToken);

        var folder = Path.Combine(Models, entry.Id);
        using var marker = JsonDocument.Parse(System.IO.File.ReadAllBytes(Path.Combine(folder, ModelStore.MarkerName)));
        Assert.Equal(entry.Id, marker.RootElement.GetProperty("id").GetString());
        var file = Assert.Single(marker.RootElement.GetProperty("files").EnumerateArray().ToList());
        Assert.Equal("ggml-base.bin", file.GetProperty("path").GetString());
        Assert.Equal(Sha(model), file.GetProperty("sha256").GetString());
        Assert.Equal(model.Length, store.InstalledBytes(entry));
        Assert.Equal(folder, store.VerifiedFolder(entry));
    }

    // A marker that does not match the catalog is not trusted: the file is hashed, fails, and the install is removed
    // (it shows as not installed, to be downloaded again).
    [Fact]
    public async Task AMarkerThatDoesNotMatchTheCatalogIsNotVerified()
    {
        byte[] model = [1, 2, 3, 4];
        var entry = Base(model);
        _hub.Tree(entry.Repo!, entry.Revision!, [Remote("ggml-base.bin", model)]);
        var store = Store();
        await store.DownloadAsync(entry, cancellationToken: TestContext.Current.CancellationToken);

        var updated = Base([9, 9, 9, 9]);

        Assert.True(store.IsInstalled(updated));
        Assert.Null(store.VerifiedFolder(updated));
        Assert.False(store.IsInstalled(updated));
        Assert.False(Directory.Exists(Path.Combine(Models, entry.Id)));
    }

    // A same-size file planted without a marker, or put over a verified one, is never handed to a loader; an install
    // from before markers whose files match the catalog is hashed once and gets its marker.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASameSizeFileIsNotLoadedUntilItMatches(bool overDownload)
    {
        byte[] model = [1, 2, 3, 4];
        var entry = Base(model);
        var folder = Path.Combine(Models, entry.Id);
        var path = Path.Combine(folder, "ggml-base.bin");
        var store = Store();
        if (overDownload)
        {
            _hub.Tree(entry.Repo!, entry.Revision!, [Remote("ggml-base.bin", model)]);
            await store.DownloadAsync(entry, cancellationToken: TestContext.Current.CancellationToken);
        }
        else
        {
            Directory.CreateDirectory(folder);
        }

        System.IO.File.WriteAllBytes(path, [6, 6, 6, 6]);
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        Assert.True(store.IsInstalled(entry));
        Assert.Null(store.VerifiedFolder(entry));
        Assert.False(Directory.Exists(folder));

        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllBytes(path, model);

        Assert.Equal(folder, store.VerifiedFolder(entry));
        Assert.True(System.IO.File.Exists(Path.Combine(folder, ModelStore.MarkerName)));
        Assert.Equal(folder, store.VerifiedFolder(entry));
    }

    // A failed check that deletes the install tells the app (its rows show Download again); an install from before
    // markers that matches only gets its marker, and nothing is raised.
    [Fact]
    public void InstallRemovedIsRaisedOnlyWhenAFailedCheckDeletesTheInstall()
    {
        byte[] model = [1, 2, 3, 4];
        var entry = Base(model);
        var folder = Path.Combine(Models, entry.Id);
        var path = Path.Combine(folder, "ggml-base.bin");
        var store = Store();
        var removed = new List<ModelEntry>();
        store.InstallRemoved += removed.Add;
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllBytes(path, model);

        Assert.Equal(folder, store.VerifiedFolder(entry));
        Assert.Equal(folder, store.MarkedFolder(entry));
        Assert.Empty(removed);

        System.IO.File.WriteAllBytes(path, [6, 6, 6, 6]);
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        Assert.Null(store.MarkedFolder(entry));
        Assert.True(Directory.Exists(folder));
        Assert.Null(store.VerifiedFolder(entry));
        Assert.False(Directory.Exists(folder));
        Assert.Same(entry, Assert.Single(removed));
    }

    // Callers that verify the same marker-less install at once hash it under one lock: each gets the folder, and the
    // marker is written once without a sharing violation.
    [Fact]
    public async Task ConcurrentChecksOfOneInstallAllSucceed()
    {
        var model = new byte[1 << 20];
        new Random(1).NextBytes(model);
        var entry = Base(model);
        var folder = Path.Combine(Models, entry.Id);
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllBytes(Path.Combine(folder, "ggml-base.bin"), model);
        var store = Store();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.VerifiedFolder(entry))));

        Assert.All(results, r => Assert.Equal(folder, r));
        Assert.Equal(folder, store.MarkedFolder(entry));
        Assert.Empty(Directory.EnumerateFiles(Models, ".verified-*"));
    }

    // A marker that lists a path twice (as many records as files) is no match, never an exception: the files are
    // hashed again and the marker rewritten.
    [Fact]
    public void AMarkerListingAPathTwiceIsNotTrusted()
    {
        byte[] model = [1, 2, 3, 4];
        var entry = ModelCatalog.Entry("whisper-base")! with
        {
            Files = ["a.bin", "b.bin"],
            DiskBytes = 2 * model.Length,
            Sha256 = new Dictionary<string, string> { ["a.bin"] = Sha(model), ["b.bin"] = Sha(model) },
        };
        var folder = Path.Combine(Models, entry.Id);
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "a.bin", "b.bin" }) System.IO.File.WriteAllBytes(Path.Combine(folder, name), model);
        var record = new
        {
            path = "a.bin", sha256 = Sha(model), bytes = model.Length,
            modifiedTicks = System.IO.File.GetLastWriteTimeUtc(Path.Combine(folder, "a.bin")).Ticks,
        };
        System.IO.File.WriteAllBytes(Path.Combine(folder, ModelStore.MarkerName),
            JsonSerializer.SerializeToUtf8Bytes(new { id = entry.Id, files = new[] { record, record } }));
        var store = Store();

        Assert.True(store.IsInstalled(entry));
        Assert.Null(store.MarkedFolder(entry));
        Assert.Equal(folder, store.VerifiedFolder(entry));
        Assert.Equal(folder, store.MarkedFolder(entry));
    }

    /// <summary>whisper-base with one fake file whose catalog hash is that of <paramref name="model"/>.</summary>
    private static ModelEntry Base(byte[] model) => ModelCatalog.Entry("whisper-base")! with
    {
        DiskBytes = model.Length,
        Sha256 = new Dictionary<string, string> { ["ggml-base.bin"] = Sha(model) },
    };

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
