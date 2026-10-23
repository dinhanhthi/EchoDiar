using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;

namespace EchoDiar.Transcribe;

public enum ModelStoreError
{
    NotDownloadable,
    UnsafePath,
    ListingFailed,
    NoFiles,
    HttpStatus,
    SizeMismatch,
    ChecksumMismatch,
    InsufficientSpace,
}

public sealed class ModelStoreException(ModelStoreError kind, string message) : Exception(message)
{
    public ModelStoreError Kind { get; } = kind;
}

/// <summary>Download progress in bytes over all files of one model.</summary>
/// <param name="File">Repo path of the file being fetched; empty once all files are done.</param>
public readonly record struct ModelDownloadProgress(long CompletedBytes, long TotalBytes, string File)
{
    public double Fraction => TotalBytes > 0 ? Math.Min(1, (double)CompletedBytes / TotalBytes) : 0;
}

/// <summary>
/// Opt-in downloads of catalog models into <c>Models/&lt;id&gt;/</c> (Swift <c>ModelStore</c>). Only the entry's
/// repos at their pinned revisions are fetched; the Hugging Face listing is untrusted: every listed path is
/// checked before anything is written, and each file must match its listed size and its SHA-256: the catalog's
/// (<see cref="ModelEntry.Sha256"/>) when it has one, else the listed LFS oid. A file with neither is refused.
/// What was checked is kept in a <see cref="MarkerName"/> file, and <see cref="VerifiedFolder"/> checks it again
/// before a model is loaded.
/// </summary>
/// <remarks>
/// What the marker guarantees: change and corruption detection, bound to the catalog's SHA-256 and to each file's size
/// and last write time as hashed. It is not a defense against a local writer who also resets a file's last write time
/// or rewrites the marker: that trust boundary is the user's own account, which can change the app's files anyway.
/// </remarks>
/// <remarks>
/// Layout: repo files with <see cref="ModelEntry.Subfolder"/> stripped, and <see cref="ModelEntry.ExtraFiles"/>
/// at their repo paths, all in <c>Models/&lt;id&gt;/</c> (the ggml file and the sherpa-onnx files sit at its root).
/// The injected <see cref="HttpClient"/> should have <c>Timeout = InfiniteTimeSpan</c>: large models take longer
/// than the 100 s default, and the cancellation token stops a download. Downloads of the same entry through one
/// store run one after the other (a second call waits for the first); separate store instances are not serialized.
/// </remarks>
public sealed class ModelStore(string root, HttpClient http, Func<string, long>? freeBytes = null)
{
    private const string HubHost = "huggingface.co";
    private const int BufferSize = 1 << 20;
    private const int MaxPages = 100;
    private const int MaxItems = 100_000;
    private const int MaxPageBytes = 16 << 20;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The file in <c>Models/&lt;id&gt;/</c> that records each file's SHA-256 as checked, with its size and last write
    /// time then; written in the staging folder after the checks, so it moves in with the files.
    /// </summary>
    public const string MarkerName = ".verified";
    private const int MaxMarkerBytes = 1 << 20;

    private readonly Func<string, long> _freeBytes = freeBytes ?? DefaultFreeBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloads = new();
    private readonly ConcurrentDictionary<string, object> _migrations = new();

    /// <summary>
    /// An install failed its check in <see cref="VerifiedFolder"/> and was deleted. Raised on the thread that called
    /// <see cref="VerifiedFolder"/> (off the UI thread, as it hashes), after its lock is released: marshal before
    /// touching the UI.
    /// </summary>
    public event Action<ModelEntry>? InstallRemoved;

    /// <summary>The <c>Models</c> folder (<c>AppPaths.Models</c>).</summary>
    public string Root { get; } = root;

    public string Folder(ModelEntry entry) => Path.Combine(Root, SafeId(entry));

    public string PartialFolder(ModelEntry entry) => Path.Combine(Root, ".partial-" + SafeId(entry));

    /// <summary>Where an install is moved before it is deleted or replaced, so <c>Models/&lt;id&gt;/</c> is never half there.</summary>
    private string OldFolder(ModelEntry entry) => Path.Combine(Root, ".old-" + SafeId(entry));

    // Installed state

    /// <summary>
    /// Every allowlisted file is in place and the folder holds exactly the catalog's byte count (the marker aside). It
    /// says what is on disk, not that it is what was downloaded: load from <see cref="VerifiedFolder"/>.
    /// </summary>
    public bool IsInstalled(ModelEntry entry)
    {
        if (entry.Repo is null || !Directory.Exists(Folder(entry))) return false;
        var subfolder = entry.Subfolder is null ? [] : Components(entry.Subfolder);
        if (subfolder is null) return false;
        var required = new List<string[]?>();
        if (entry.Files is not null) required.AddRange(entry.Files.Select(f => Components(f) is { } p ? Destination(p, subfolder) : null));
        if (entry.ExtraFiles is not null) required.AddRange(entry.ExtraFiles.Files.Select(Components));
        foreach (var parts in required)
        {
            if (parts is null || SafePath(Folder(entry), parts) is not { } path || !Path.Exists(path)) return false;
        }
        return InstalledBytes(entry) == entry.DiskBytes;
    }

    /// <summary>Bytes of the model's files under <c>Models/&lt;id&gt;/</c> (its <see cref="MarkerName"/> file aside).</summary>
    public long InstalledBytes(ModelEntry entry)
    {
        var marker = new FileInfo(Path.Combine(Folder(entry), MarkerName));
        return SizeOf(Folder(entry)) - (marker.Exists ? marker.Length : 0);
    }

    // Verification

    private sealed record MarkerFile(string Path, string Sha256, long Bytes, long ModifiedTicks);

    private sealed record Marker(string Id, List<MarkerFile> Files);

    /// <summary>
    /// <c>Models/&lt;id&gt;/</c> when its files are those checked at download: the marker lists exactly the files there,
    /// with the catalog's SHA-256 (or, for a file the catalog has none for, the one checked at download) and each
    /// file's size and last write time. An install without a valid marker (one from before markers, or a file changed
    /// since) is hashed once now: when every file matches the catalog it gets its marker; when one does not, the
    /// install is deleted (unless a download of it runs), so it shows as not installed. Null when not installed or not
    /// verified. Hashes whole files the first time: call it off the UI thread, before loading the model. Hashing one
    /// entry runs once at a time: a second caller waits, then takes the marker the first one wrote.
    /// </summary>
    public string? VerifiedFolder(ModelEntry entry)
    {
        if (MarkedFolder(entry) is { } folder) return folder;
        if (!IsInstalled(entry)) return null;
        bool removed;
        lock (_migrations.GetOrAdd(SafeId(entry), _ => new object()))
        {
            if (MarkedFolder(entry) is { } marked) return marked;
            if (!IsInstalled(entry)) return null;
            if (Migrate(entry) is { } migrated) return migrated;
            removed = !Directory.Exists(Folder(entry));
        }
        if (removed) InstallRemoved?.Invoke(entry);
        return null;
    }

    /// <summary>
    /// <c>Models/&lt;id&gt;/</c> when it is installed and its marker matches its files as they are now; null otherwise
    /// (<see cref="VerifiedFolder"/> then hashes them). Never hashes nor deletes: cheap enough before a recording starts.
    /// </summary>
    public string? MarkedFolder(ModelEntry entry)
    {
        if (!IsInstalled(entry)) return null;
        var folder = Folder(entry);
        return ReadMarker(folder) is { } marker && Matches(marker, entry, folder, FilesIn(folder), ExpectedHashes(entry))
            ? folder
            : null;
    }

    /// <summary>
    /// Hashes every file of an install without a valid marker: all match the catalog, the folder with its new marker;
    /// one does not, null, the install deleted unless a download of it runs. Called inside the entry's migration lock.
    /// </summary>
    private string? Migrate(ModelEntry entry)
    {
        var folder = Folder(entry);
        var expected = ExpectedHashes(entry);
        var checkedFiles = new List<MarkerFile>();
        foreach (var file in FilesIn(folder))
        {
            if (!expected.TryGetValue(file, out var sha)) return null;
            var (hash, bytes, modified) = HashOf(Path.Combine(folder, file));
            if (!string.Equals(hash, sha, StringComparison.OrdinalIgnoreCase))
            {
                DeleteUnlessDownloading(entry);
                return null;
            }
            checkedFiles.Add(new(file, sha, bytes, modified));
        }
        // Written aside, then moved in, so a crash never leaves half a marker (or a stray file) in the folder.
        var temporary = Path.Combine(Root, $".verified-{SafeId(entry)}-{Guid.NewGuid():N}");
        WriteMarker(temporary, new(entry.Id, checkedFiles));
        File.Move(temporary, Path.Combine(folder, MarkerName), overwrite: true);
        return folder;
    }

    /// <summary>The files under <paramref name="folder"/> as <c>/</c>-separated relative paths, the marker aside.</summary>
    private static List<string> FilesIn(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(f => f != MarkerName)
            .Order(StringComparer.Ordinal)];

    /// <summary>The catalog's SHA-256 per installed path (the subfolder stripped), both repos' files.</summary>
    private static Dictionary<string, string> ExpectedHashes(ModelEntry entry)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var subfolder = entry.Subfolder is null ? [] : Components(entry.Subfolder) ?? [];
        foreach (var (path, sha) in entry.Sha256 ?? new Dictionary<string, string>())
        {
            if (Components(path) is { } parts && Destination(parts, subfolder) is { } destination) hashes[string.Join('/', destination)] = sha;
        }
        foreach (var (path, sha) in entry.ExtraFiles?.Sha256 ?? new Dictionary<string, string>())
        {
            if (Components(path) is { } parts) hashes[string.Join('/', parts)] = sha;
        }
        return hashes;
    }

    /// <summary>
    /// The marker lists exactly <paramref name="files"/>, each once, with the catalog's hash and its size and last write
    /// time now. A path listed twice, or a file that cannot be read (gone or changing meanwhile), is no match.
    /// </summary>
    private static bool Matches(Marker marker, ModelEntry entry, string folder, List<string> files, Dictionary<string, string> expected)
    {
        if (marker.Id != entry.Id || marker.Files is null || marker.Files.Count != files.Count) return false;
        var listed = new Dictionary<string, MarkerFile>(StringComparer.Ordinal);
        foreach (var record in marker.Files)
        {
            if (record?.Path is null || record.Sha256 is null || !listed.TryAdd(record.Path, record)) return false;
        }
        try
        {
            foreach (var file in files)
            {
                if (!listed.TryGetValue(file, out var record)) return false;
                if (expected.TryGetValue(file, out var sha) && !string.Equals(sha, record.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                var info = new FileInfo(Path.Combine(folder, file));
                if (info.Length != record.Bytes || info.LastWriteTimeUtc.Ticks != record.ModifiedTicks) return false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        return true;
    }

    private static Marker? ReadMarker(string folder)
    {
        var path = Path.Combine(folder, MarkerName);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxMarkerBytes) return null;
            return JsonSerializer.Deserialize<Marker>(File.ReadAllBytes(path), Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteMarker(string path, Marker marker) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(marker, Json));

    /// <summary>
    /// The file's SHA-256, with its size and last write time read from the same handle before hashing (no writer can
    /// open it meanwhile), so a file swapped in after the hash is never recorded as the one hashed.
    /// </summary>
    private static (string Sha256, long Bytes, long ModifiedTicks) HashOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        var bytes = stream.Length;
        var modified = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
        return (Convert.ToHexString(SHA256.HashData(stream)), bytes, modified);
    }

    /// <summary>Removes an install that failed its check; left alone while a download of it runs (it replaces it).</summary>
    private void DeleteUnlessDownloading(ModelEntry entry)
    {
        var gate = _downloads.GetOrAdd(SafeId(entry), _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) return;
        try
        {
            Delete(entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Bytes of all installed models (partial downloads excluded).</summary>
    public long TotalInstalledBytes() =>
        Directory.Exists(Root)
            ? Directory.EnumerateDirectories(Root).Where(d => !Path.GetFileName(d).StartsWith('.')).Sum(SizeOf)
            : 0;

    private static long SizeOf(string folder) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;

    /// <summary>
    /// Removes <c>Models/&lt;id&gt;/</c> and any partial download. The install is first renamed to <c>.old-&lt;id&gt;</c>,
    /// so a delete that fails midway leaves no half-installed model. Waits for a <see cref="VerifiedFolder"/> hashing the
    /// entry (its lock), whose open file Windows would refuse to move.
    /// </summary>
    public void Delete(ModelEntry entry)
    {
        lock (_migrations.GetOrAdd(SafeId(entry), _ => new object()))
        {
            var old = OldFolder(entry);
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
            if (Directory.Exists(Folder(entry))) Directory.Move(Folder(entry), old);
            foreach (var folder in new[] { old, PartialFolder(entry) })
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }
    }

    // Download

    private sealed record Lfs(string Oid);

    private sealed record TreeItem(string Type, string Path, long? Size, Lfs? Lfs);

    private sealed record RemoteFile(string Repo, string Revision, string Path, string[] Destination, long Size, string Sha256);

    /// <summary>
    /// Downloads the entry into <c>.partial-&lt;id&gt;</c>, verifies every file, then moves it to
    /// <c>Models/&lt;id&gt;/</c> (replacing an older copy). On any failure or cancellation the partial folder is
    /// removed and the error rethrown; an existing install is left as it was (it is set aside in
    /// <c>.old-&lt;id&gt;</c> during the final move and put back if that move fails).
    /// A concurrent download of the same entry through this store waits until this one ends.
    /// </summary>
    public async Task DownloadAsync(
        ModelEntry entry, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (entry.Repo is null || entry.Revision is null)
        {
            throw new ModelStoreException(ModelStoreError.NotDownloadable, $"{entry.Id} is not downloadable.");
        }
        var gate = _downloads.GetOrAdd(SafeId(entry), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await Install(entry, progress, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task Install(ModelEntry entry, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var staging = PartialFolder(entry);
        Directory.CreateDirectory(Root);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        try
        {
            var files = await RemoteFiles(entry, cancellationToken);
            var total = files.Sum(f => f.Size);
            var needed = total + total / 10;
            var available = _freeBytes(Root);
            if (available < needed)
            {
                throw new ModelStoreException(ModelStoreError.InsufficientSpace,
                    $"Not enough disk space: {needed / 1_048_576} MiB needed, {available / 1_048_576} MiB available.");
            }
            var targets = files.Select(f => SafePath(staging, f.Destination)
                ?? throw Unsafe(f.Path)).ToArray();
            if (files.Any(f => string.Equals(string.Join('/', f.Destination), MarkerName, StringComparison.OrdinalIgnoreCase))) throw Unsafe(MarkerName);

            Directory.CreateDirectory(staging);
            long done = 0;
            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (file, before) = (files[i], done);
                progress?.Report(new(done, total, file.Path));
                await Fetch(file, targets[i], bytes => progress?.Report(new(before + bytes, total, file.Path)), cancellationToken);
                done += file.Size;
            }
            progress?.Report(new(done, total, ""));

            cancellationToken.ThrowIfCancellationRequested();
            // Only now that every file matched: the record VerifiedFolder checks before each load.
            WriteMarker(Path.Combine(staging, MarkerName), new(entry.Id, [.. files.Select((f, i) =>
            {
                var info = new FileInfo(targets[i]);
                return new MarkerFile(string.Join('/', f.Destination), f.Sha256, info.Length, info.LastWriteTimeUtc.Ticks);
            })]));
            Replace(entry, staging);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>Moves <paramref name="staging"/> to <c>Models/&lt;id&gt;/</c>, restoring the previous install if the move fails.</summary>
    private void Replace(ModelEntry entry, string staging)
    {
        var final = Folder(entry);
        var old = OldFolder(entry);
        if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
        var hadInstall = Directory.Exists(final);
        if (hadInstall) Directory.Move(final, old);
        try
        {
            Directory.Move(staging, final);
        }
        catch
        {
            if (hadInstall) Directory.Move(old, final);
            throw;
        }
        TryDelete(old);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The allowlisted files of both repos. Any unsafe path in a listing fails the whole download.</summary>
    private async Task<List<RemoteFile>> RemoteFiles(ModelEntry entry, CancellationToken cancellationToken)
    {
        var subfolder = entry.Subfolder is null ? [] : Components(entry.Subfolder) ?? throw Unsafe(entry.Subfolder);
        var files = await Pick(entry.Repo!, entry.Revision!, subfolder, entry.Files, entry.Sha256, cancellationToken);
        if (entry.ExtraFiles is { } extra)
        {
            files.AddRange(await Pick(extra.Repo, extra.Revision, [], extra.Files, extra.Sha256, cancellationToken));
        }
        if (files.Count == 0) throw new ModelStoreException(ModelStoreError.NoFiles, $"No files to download for {entry.Id}.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!seen.Add(string.Join('/', file.Destination))) throw Unsafe(file.Path);
        }
        return files;
    }

    /// <summary>
    /// Listed files under <paramref name="subfolder"/> that match <paramref name="allowlist"/> on whole path
    /// components (null = all), with the subfolder stripped from their destination. Each gets its catalog SHA-256
    /// from <paramref name="hashes"/> (keyed by repo path), else its listed LFS oid; a file with neither fails.
    /// </summary>
    private async Task<List<RemoteFile>> Pick(
        string repo, string revision, string[] subfolder, IReadOnlyList<string>? allowlist,
        IReadOnlyDictionary<string, string>? hashes, CancellationToken cancellationToken)
    {
        var allowed = allowlist?.Select(a => Components(a) ?? throw Unsafe(a)).ToArray();
        var picked = new List<RemoteFile>();
        var matched = new HashSet<int>();
        foreach (var item in await List(repo, revision, subfolder, cancellationToken))
        {
            if (item.Type != "file") continue;
            var parts = Components(item.Path) ?? throw Unsafe(item.Path);
            var hit = allowed is null ? -1 : Array.FindIndex(allowed, a => StartsWith(parts, a));
            if (allowed is not null && hit < 0) continue;
            matched.Add(hit);
            var destination = Destination(parts, subfolder) ?? throw Unsafe(item.Path);
            var sha = hashes?.GetValueOrDefault(item.Path) ?? item.Lfs?.Oid
                ?? throw new ModelStoreException(ModelStoreError.ChecksumMismatch, $"No SHA-256 known for {item.Path}.");
            picked.Add(new(repo, revision, item.Path, destination, item.Size ?? 0, sha));
        }
        if (allowed is not null && matched.Count < allowed.Length)
        {
            throw ListingFailed($"{repo} at {revision} misses an allowlisted file.");
        }
        return picked;
    }

    /// <summary>
    /// The recursive HF tree of a repo folder, following <c>Link: &lt;…&gt;; rel="next"</c> pages. A page seen twice,
    /// more than <see cref="MaxPages"/> pages, <see cref="MaxItems"/> items or a page over <see cref="MaxPageBytes"/> fails.
    /// </summary>
    private async Task<List<TreeItem>> List(string repo, string revision, string[] subfolder, CancellationToken cancellationToken)
    {
        var path = subfolder.Length == 0 ? "" : "/" + Encode(subfolder);
        Uri? next = new($"https://{HubHost}/api/models/{repo}/tree/{revision}{path}?recursive=1");
        var items = new List<TreeItem>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null)
        {
            if (!visited.Add(next.AbsoluteUri)) throw ListingFailed($"The listing of {repo} loops back to {next}.");
            if (visited.Count > MaxPages) throw ListingFailed($"The listing of {repo} has more than {MaxPages} pages.");
            using var response = await http.GetAsync(next, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) throw ListingFailed($"HTTP {(int)response.StatusCode} listing {repo}.");
            try
            {
                await using var body = await ReadPage(response, repo, cancellationToken);
                items.AddRange(await JsonSerializer.DeserializeAsync<List<TreeItem>>(body, Json, cancellationToken) ?? []);
            }
            catch (JsonException error)
            {
                throw ListingFailed($"Unreadable listing of {repo}: {error.Message}");
            }
            if (items.Count > MaxItems) throw ListingFailed($"The listing of {repo} has more than {MaxItems} items.");
            next = NextPage(response.Headers.TryGetValues("Link", out var links) ? string.Join(",", links) : null);
        }
        return items;
    }

    /// <summary>One listing page in memory, failing once it exceeds <see cref="MaxPageBytes"/>.</summary>
    private static async Task<MemoryStream> ReadPage(HttpResponseMessage response, string repo, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var page = new MemoryStream();
        var buffer = new byte[81_920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (page.Length + read > MaxPageBytes) throw ListingFailed($"A listing page of {repo} exceeds {MaxPageBytes} bytes.");
            page.Write(buffer, 0, read);
        }
        page.Position = 0;
        return page;
    }

    /// <summary>The <c>rel="next"</c> URL of a Link header; only an https URL on the hub host is followed.</summary>
    private static Uri? NextPage(string? link)
    {
        if (link is null) return null;
        foreach (var part in link.Split(','))
        {
            if (!part.Contains("rel=\"next\"")) continue;
            int start = part.IndexOf('<'), end = part.IndexOf('>');
            if (start < 0 || end <= start) continue;
            var text = part[(start + 1)..end];
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, HubHost, StringComparison.OrdinalIgnoreCase))
            {
                return uri;
            }
            throw ListingFailed($"Refusing next page {text}.");
        }
        return null;
    }

    /// <summary>One file, streamed to disk while hashing; stops as soon as it exceeds the listed size.</summary>
    private async Task Fetch(RemoteFile file, string target, Action<long> onBytes, CancellationToken cancellationToken)
    {
        var url = new Uri($"https://{HubHost}/{file.Repo}/resolve/{file.Revision}/{Encode(file.Path.Split('/'))}");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ModelStoreException(ModelStoreError.HttpStatus, $"HTTP {(int)response.StatusCode} while downloading {file.Path}.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                written += read;
                if (written > file.Size) throw Mismatch(ModelStoreError.SizeMismatch, "Size", file.Path);
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                onBytes(written);
            }
        }
        if (written != file.Size) throw Mismatch(ModelStoreError.SizeMismatch, "Size", file.Path);
        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Mismatch(ModelStoreError.ChecksumMismatch, "SHA-256", file.Path);
        }
    }

    // Paths

    /// <summary>
    /// The <c>/</c>-separated components of a repo path, or null when it is rooted or has a backslash, colon, NUL,
    /// or an empty, <c>.</c>, <c>..</c> or trailing-dot/space component (Windows would rewrite those), or a Windows
    /// device name such as <c>NUL</c> or <c>CON.bin</c> (Windows would open a device).
    /// </summary>
    private static string[]? Components(string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.IndexOfAny(['\\', ':', '\0']) >= 0) return null;
        var parts = path.Split('/');
        return parts.All(p => p.Length > 0 && p != "." && p != ".." && !p.EndsWith('.') && !p.EndsWith(' ')
            && !ConversationFiles.IsDeviceName(p)) ? parts : null;
    }

    /// <summary>Components relative to <c>Models/&lt;id&gt;/</c>, or null when outside the subfolder (or the subfolder itself).</summary>
    private static string[]? Destination(string[] parts, string[] subfolder) =>
        parts.Length > subfolder.Length && StartsWith(parts, subfolder) ? parts[subfolder.Length..] : null;

    private static bool StartsWith(string[] parts, string[] prefix) =>
        parts.Length >= prefix.Length && parts.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    /// <summary>The full path of <paramref name="parts"/> in <paramref name="folder"/>, or null when it resolves outside it.</summary>
    private static string? SafePath(string folder, string[] parts)
    {
        var baseFull = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine([folder, .. parts]));
        return full.StartsWith(baseFull, StringComparison.Ordinal) ? full : null;
    }

    private static string SafeId(ModelEntry entry) =>
        Components(entry.Id) is [_] && !entry.Id.StartsWith('.') ? entry.Id : throw Unsafe(entry.Id);

    private static string Encode(IEnumerable<string> parts) => string.Join('/', parts.Select(Uri.EscapeDataString));

    private static ModelStoreException Unsafe(string path) =>
        new(ModelStoreError.UnsafePath, $"Refusing unsafe model path {path}.");

    private static ModelStoreException ListingFailed(string message) => new(ModelStoreError.ListingFailed, message);

    private static ModelStoreException Mismatch(ModelStoreError kind, string what, string path) =>
        new(kind, $"{what} mismatch for {path}.");

    private static long DefaultFreeBytes(string folder) =>
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder).AvailableFreeSpace;
}
