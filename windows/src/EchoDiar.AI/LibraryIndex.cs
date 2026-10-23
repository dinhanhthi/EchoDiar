using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EchoDiar.Core;
using EchoDiar.Core.Json;
using EchoDiar.Core.Model;

namespace EchoDiar.AI;

public enum LibraryIndexErrorKind
{
    /// <summary>The embedder key is not a plain folder name, or <see cref="LibraryIndex.LoadAsync"/> was not called.</summary>
    InvalidKey,
    /// <summary>
    /// The embedder returned a different number of vectors than texts, vectors of different lengths, or a length other
    /// than the index's.
    /// </summary>
    VectorMismatch,
}

/// <summary>Swift <c>LibraryIndexError</c>.</summary>
public sealed class LibraryIndexException(LibraryIndexErrorKind kind)
    : Exception(kind == LibraryIndexErrorKind.InvalidKey ? "The index has no valid embedder key." : "The embedder returned unusable vectors.")
{
    public LibraryIndexErrorKind Kind { get; } = kind;
}

/// <summary>
/// The RAG index of the library (Swift <c>LibraryIndex</c>): one shard per conversation at
/// <c>root/&lt;embedder key&gt;/&lt;conversation id&gt;.json</c> (root = <see cref="AppPaths.Rag"/>), every shard of the loaded
/// key kept in memory. Retrieval fuses cosine similarity and BM25 with reciprocal rank fusion. Local embedders run
/// inside <c>ModelLoadGate</c> (owner Embedder): the caller holds it while it passes a loaded embedder to
/// <see cref="IndexAsync"/>, so the index itself takes no gate.
/// </summary>
/// <remarks>
/// As the Swift actor, every call runs one at a time (file and CPU work on the thread pool, never on the caller's
/// thread), so no two writes of a shard overlap. <see cref="IndexAsync"/> lets other calls run while it embeds, then
/// writes nothing when another key was loaded meanwhile.
/// </remarks>
public sealed partial class LibraryIndex(string root)
{
    public enum ChunkKind { Transcript, Summary }

    /// <summary>A passage that is embedded and searched: a window of transcript turns, or a part of the summary.</summary>
    /// <param name="Start">Seconds of the window's first turn start; null for summary chunks.</param>
    /// <param name="End">Seconds of the window's last turn end; null for summary chunks.</param>
    public sealed record Chunk(ChunkKind Kind, double? Start, double? End, string Text);

    /// <param name="Score">Reciprocal rank fusion score; higher is better.</param>
    public sealed record Hit(Guid ConversationID, Chunk Chunk, double Score);

    /// <summary>
    /// The shard file format, version 1. <paramref name="Vectors"/> holds <c>chunks × dimension</c> Float32 values,
    /// little-endian, chunk after chunk.
    /// </summary>
    internal sealed record Shard(int Version, string Fingerprint, int Dimension, IReadOnlyList<Chunk> Chunks, byte[] Vectors);

    private sealed record Entry(string Fingerprint, IReadOnlyList<Chunk> Chunks, float[] Vectors, IReadOnlyList<IReadOnlyList<string>> Terms);

    /// <summary>Estimated tokens per chunk: Apple's embedder reads 256 real tokens, and the estimate runs a little low.</summary>
    internal const int ChunkTokens = 220;
    /// <summary>Candidates taken from each ranking before fusion.</summary>
    internal const int FusionDepth = 50;
    internal const double RrfK = 60.0;

    private readonly SemaphoreSlim _turn = new(1, 1);
    private string? _key;
    private int? _dimension;
    private Dictionary<Guid, Entry> _entries = [];

    public string Root { get; } = root;

    /// <summary>Indexed conversations of the loaded key.</summary>
    public Task<int> CountAsync() => RunAsync(() => _entries.Count, CancellationToken.None);

    public Task<IReadOnlySet<Guid>> IndexedIdsAsync() =>
        RunAsync<IReadOnlySet<Guid>>(() => _entries.Keys.ToHashSet(), CancellationToken.None);

    // Chunking

    /// <summary>
    /// Windows of consecutive transcript turns (<see cref="PromptBuilder.Line"/> lines, speaker display names) of about
    /// <see cref="ChunkTokens"/> estimated tokens, each sharing its first turn with the previous window's last when that
    /// turn and the next one fit a window together; then the summary split on blank lines, paragraphs merged or split
    /// to fit <see cref="ChunkTokens"/>.
    /// </summary>
    public static IReadOnlyList<Chunk> Chunks(Transcript? transcript, string? summary)
    {
        var chunks = new List<Chunk>();
        if (transcript is { Segments.Count: > 0 })
        {
            var segments = transcript.Segments;
            var lines = segments.Select(s => PromptBuilder.Line(s, transcript)).ToList();
            var costs = lines.Select(l => TranscriptChunker.EstimateTokens(l) + 1).ToList();
            var start = 0;
            while (true)
            {
                var end = start;
                var used = 0;
                while (end < lines.Count)
                {
                    var cost = costs[end];
                    if (end > start && used + cost > ChunkTokens) break;
                    used += cost;
                    end++;
                }
                chunks.Add(new Chunk(
                    ChunkKind.Transcript, segments[start].Start, segments[end - 1].End,
                    string.Join("\n", lines.Skip(start).Take(end - start))));
                if (end >= lines.Count) break;
                start = end - 1 > start && costs[end - 1] + costs[end] <= ChunkTokens ? end - 1 : end;
            }
        }
        if (summary is not null)
        {
            var paragraphs = BlankLine().Split(summary)
                .Select(TrimmingWhitespaceAndNewlines)
                .Where(p => p.Length > 0);
            chunks.AddRange(Pack(paragraphs.SelectMany(Split).ToList(), "\n\n")
                .Select(text => new Chunk(ChunkKind.Summary, null, null, text)));
        }
        return chunks;
    }

    /// <summary><paramref name="text"/> as parts of at most <see cref="ChunkTokens"/>: by lines, then by words. A single word is never split.</summary>
    private static List<string> Split(string text)
    {
        if (TranscriptChunker.EstimateTokens(text) <= ChunkTokens) return [text];
        var lines = text.Split(Newlines).Where(l => l.Any(c => !IsWhitespace(c))).ToList();
        if (lines.Count > 1) return Pack(lines.SelectMany(Split).ToList(), "\n");
        var words = Words(text);
        return words.Count > 1 ? Pack(words, " ") : [text];
    }

    /// <summary>Consecutive <paramref name="parts"/> joined while the result fits <see cref="ChunkTokens"/>.</summary>
    private static List<string> Pack(IReadOnlyList<string> parts, string separator)
    {
        var packed = new List<string>();
        var current = "";
        foreach (var part in parts)
        {
            var joined = current.Length == 0 ? part : current + separator + part;
            if (current.Length > 0 && TranscriptChunker.EstimateTokens(joined) > ChunkTokens)
            {
                packed.Add(current);
                current = part;
            }
            else
            {
                current = joined;
            }
        }
        if (current.Length > 0) packed.Add(current);
        return packed;
    }

    /// <summary>
    /// Swift's <c>/\n[ \t]*\n/</c> on a String: a CRLF is one Character there, which <c>\n</c> does not match, so a
    /// <c>\n</c> right after <c>\r</c> starts no blank line.
    /// </summary>
    [GeneratedRegex(@"(?<!\r)\n[ \t]*\n")]
    private static partial Regex BlankLine();

    /// <summary>Foundation <c>CharacterSet.newlines</c>.</summary>
    private static readonly char[] Newlines = ['\n', '\v', '\f', '\r', '\u0085', '\u2028', '\u2029'];

    /// <summary>Foundation <c>CharacterSet.whitespaces</c>: Unicode Zs, tab and U+200B (listed from the set on macOS).</summary>
    private static bool IsWhitespace(char c) =>
        c is '\t' or '\u200B' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary><paramref name="text"/> trimmed of Foundation <c>CharacterSet.whitespacesAndNewlines</c> (whitespaces and newlines).</summary>
    private static string TrimmingWhitespaceAndNewlines(string text)
    {
        static bool Trimmed(char c) => IsWhitespace(c) || Newlines.Contains(c);
        var start = 0;
        var end = text.Length;
        while (start < end && Trimmed(text[start])) start++;
        while (end > start && Trimmed(text[end - 1])) end--;
        return text[start..end];
    }

    /// <summary>Swift <c>split(whereSeparator: \.isWhitespace)</c>: runs between Unicode White_Space characters.</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (IsWhiteSpaceProperty(c))
            {
                if (word.Length > 0) words.Add(word.ToString());
                word.Clear();
            }
            else
            {
                word.Append(c);
            }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    /// <summary>The Unicode White_Space property (Swift <c>Character.isWhitespace</c>).</summary>
    private static bool IsWhiteSpaceProperty(char c) =>
        c is >= '\t' and <= '\r' or ' ' or '\u0085' or '\u00A0' or '\u1680' or >= '\u2000' and <= '\u200A'
            or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000';

    // Lifecycle

    /// <summary>
    /// Modification date (seconds since 1970, printed as Swift prints a Double) and size of <c>transcript.json</c> and of
    /// <c>summary.md</c> (the newest summary, rewritten with every version), <c>-</c> for a missing file. A changed
    /// fingerprint means the shard is out of date.
    /// </summary>
    public static string Fingerprint(string folder) =>
        string.Join("|", new[] { Transcript.FileName, Conversation.SummaryFileName }.Select(name =>
        {
            try
            {
                var info = new FileInfo(Path.Combine(folder, name));
                if (!info.Exists) return "-";
                var seconds = (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond;
                return $"{SwiftDescription(seconds)}-{info.Length}";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return "-";
            }
        }));

    /// <summary>
    /// Reads every shard of <paramref name="key"/> into memory, replacing what was loaded. Unreadable shards, and shards
    /// whose vector length differs from the first one read, are skipped (their conversations come back as stale).
    /// Cancelled midway, no key is loaded.
    /// </summary>
    public Task LoadAsync(string key, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        _entries = [];
        _dimension = null;
        _key = null;
        if (FolderFor(key) is not { } folder) return;
        var entries = new Dictionary<Guid, Entry>();
        int? dimension = null;
        foreach (var (id, path) in ShardFiles(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Shard shard;
            float[]? vectors;
            try
            {
                shard = DecodeShard(File.ReadAllBytes(path));
                vectors = shard.Version == 1 ? Floats(shard.Vectors, shard.Chunks.Count, shard.Dimension) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                continue;
            }
            if (vectors is null) continue;
            if (shard.Chunks.Count > 0)
            {
                if (shard.Dimension <= 0 || (dimension ?? shard.Dimension) != shard.Dimension) continue;
                dimension = shard.Dimension;
            }
            entries[id] = new Entry(shard.Fingerprint, shard.Chunks, vectors, TermsOf(shard.Chunks));
        }
        _entries = entries;
        _dimension = dimension;
        _key = key;
    }, cancellationToken);

    /// <summary>
    /// The conversations with a transcript whose shard is missing or out of date (fingerprint), in list order. Shards of
    /// conversations not in <paramref name="conversations"/>, or without a transcript, are deleted.
    /// </summary>
    public Task<IReadOnlyList<Conversation>> StaleAsync(
        IReadOnlyList<Conversation> conversations, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<Conversation>>(() =>
        {
            var transcribed = conversations.Where(c => c.HasTranscript).ToList();
            if (_key is null || FolderFor(_key) is not { } folder) return transcribed;
            var keep = transcribed.Select(c => c.Id).ToHashSet();
            foreach (var (id, path) in ShardFiles(folder))
            {
                if (!keep.Contains(id)) TryDelete(path);
            }
            _entries = _entries.Where(e => keep.Contains(e.Key)).ToDictionary();
            return transcribed.Where(c =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return !_entries.TryGetValue(c.Id, out var entry) || entry.Fingerprint != Fingerprint(c.Folder);
            }).ToList();
        }, cancellationToken);

    /// <summary>
    /// Deletes, in every key's folder, the shards of conversations not in <paramref name="conversations"/> or without a
    /// transcript, and drops them from memory. Needs no loaded key, so trashed conversations lose their text without an
    /// embedder.
    /// </summary>
    public Task PruneAsync(IReadOnlyList<Conversation> conversations, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var keep = conversations.Where(c => c.HasTranscript).Select(c => c.Id).ToHashSet();
            foreach (var folder in KeyFolders())
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (id, path) in ShardFiles(folder))
                {
                    if (!keep.Contains(id)) TryDelete(path);
                }
            }
            _entries = _entries.Where(e => keep.Contains(e.Key)).ToDictionary();
        }, cancellationToken);

    /// <summary>
    /// Chunks the conversation, embeds every chunk as a passage in one call, and writes its shard (atomically: temp file,
    /// then replace). When another key is loaded meanwhile, nothing is written. Cancellation stops before the write.
    /// </summary>
    public async Task IndexAsync(
        Conversation conversation, Transcript? transcript, string? summary, ILoadedTextEmbedder embedder,
        CancellationToken cancellationToken = default)
    {
        var key = await RunAsync(() => _key, cancellationToken).ConfigureAwait(false);
        if (key is null || FolderFor(key) is not { } folder) throw new LibraryIndexException(LibraryIndexErrorKind.InvalidKey);
        var (fingerprint, chunks) = await Task.Run(
            () => (Fingerprint(conversation.Folder), Chunks(transcript, summary)), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<float[]> vectors = chunks.Count == 0
            ? []
            : await embedder.EmbedAsync(chunks.Select(c => c.Text).ToList(), false, cancellationToken).ConfigureAwait(false);
        var length = vectors.Count > 0 ? vectors[0].Length : 0;
        if (vectors.Count != chunks.Count || vectors.Any(v => v.Length != length) || (chunks.Count > 0 && length == 0))
            throw new LibraryIndexException(LibraryIndexErrorKind.VectorMismatch);
        await RunAsync(() =>
        {
            if (_key != key) return;
            if (chunks.Count > 0 && (_dimension ?? length) != length)
                throw new LibraryIndexException(LibraryIndexErrorKind.VectorMismatch);
            cancellationToken.ThrowIfCancellationRequested();
            var flat = vectors.SelectMany(v => v).ToArray();
            var shard = new Shard(1, fingerprint, length, chunks, Bytes(flat));
            Directory.CreateDirectory(folder);
            AtomicFile.Write(Path.Combine(folder, ShardFileName(conversation.Id)), EncodeShard(shard));
            if (chunks.Count > 0) _dimension = length;
            _entries[conversation.Id] = new Entry(fingerprint, chunks, flat, TermsOf(chunks));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the index folders of every key but <paramref name="key"/>.</summary>
    public Task RemoveAllExceptAsync(string key) => RunAsync(() =>
    {
        if (FolderFor(key) is null || !Directory.Exists(Root)) return;
        foreach (var name in Children(Root))
        {
            if (name != key) TryDeleteEntry(Path.Combine(Root, name));
        }
        if (_key != key) Unload();
    }, CancellationToken.None);

    /// <summary>
    /// Deletes the index folders of every key (RAG turned off, or an empty library) and empties memory; no key is loaded
    /// after it.
    /// </summary>
    public Task RemoveAllAsync() => RunAsync(() =>
    {
        foreach (var name in Children(Root)) TryDeleteEntry(Path.Combine(Root, name));
        Unload();
    }, CancellationToken.None);

    /// <summary>Deletes the index folder of <paramref name="key"/> (Rebuild); when it is loaded, memory is emptied and the key stays loaded.</summary>
    public Task RemoveKeyAsync(string key) => RunAsync(() =>
    {
        if (FolderFor(key) is not { } folder) return;
        TryDeleteEntry(folder);
        if (_key != key) return;
        _entries = [];
        _dimension = null;
    }, CancellationToken.None);

    // Search

    /// <summary>
    /// The <paramref name="limit"/> best chunks for <paramref name="question"/>: the top <see cref="FusionDepth"/> by
    /// cosine similarity with <paramref name="vector"/> and the top <see cref="FusionDepth"/> by BM25 (chunks sharing a
    /// term with the question), fused by reciprocal rank (k = 60). BM25 alone when <paramref name="vector"/> is null or
    /// not of the index's length.
    /// </summary>
    public Task<IReadOnlyList<Hit>> SearchAsync(
        string question, float[]? vector, int limit, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<Hit>>(() =>
        {
            var refs = new List<(Guid Id, Entry Entry, int Chunk)>();
            var documents = new List<IReadOnlyList<string>>();
            foreach (var (id, entry) in _entries.OrderBy(e => UuidString(e.Key), StringComparer.Ordinal))
            {
                for (var index = 0; index < entry.Chunks.Count; index++)
                {
                    refs.Add((id, entry, index));
                    documents.Add(entry.Terms[index]);
                }
            }
            var fused = new double[refs.Count];
            var queryTerms = TranscriptChunker.Terms(question);
            var querySet = queryTerms.ToHashSet(StringComparer.Ordinal);
            var lexical = TranscriptChunker.Bm25Rank(queryTerms, documents)
                .Where(i => documents[i].Any(querySet.Contains)).Take(FusionDepth);
            var rank = 0;
            foreach (var index in lexical) fused[index] += 1 / (RrfK + ++rank);
            cancellationToken.ThrowIfCancellationRequested();
            if (vector is not null && _dimension is { } dimension && vector.Length == dimension)
            {
                var similarities = refs.Select(r =>
                {
                    var offset = r.Chunk * dimension;
                    var dot = 0f;
                    for (var i = 0; i < dimension; i++) dot += r.Entry.Vectors[offset + i] * vector[i];
                    return dot;
                }).ToArray();
                rank = 0;
                foreach (var index in Enumerable.Range(0, refs.Count).OrderByDescending(i => similarities[i]).ThenBy(i => i).Take(FusionDepth))
                    fused[index] += 1 / (RrfK + ++rank);
            }
            return Enumerable.Range(0, refs.Count).Where(i => fused[i] > 0)
                .OrderByDescending(i => fused[i]).ThenBy(i => i)
                .Take(Math.Max(limit, 0))
                .Select(i => new Hit(refs[i].Id, refs[i].Entry.Chunks[refs[i].Chunk], fused[i]))
                .ToList();
        }, cancellationToken);

    // Shard file

    /// <summary>
    /// Compact JSON as Swift's <c>JSONEncoder</c> writes it (<c>/</c> escaped, non-ASCII raw, doubles in their shortest
    /// form), with keys sorted: Swift's bare encoder writes them in an arbitrary order that changes per process, which
    /// its decoder ignores. The bytes equal Swift's with <c>.sortedKeys</c>.
    /// </summary>
    internal static byte[] EncodeShard(Shard shard)
    {
        var json = new StringBuilder();
        json.Append("{\"chunks\":[");
        for (var i = 0; i < shard.Chunks.Count; i++)
        {
            var chunk = shard.Chunks[i];
            if (i > 0) json.Append(',');
            json.Append('{');
            if (chunk.End is { } end) json.Append("\"end\":").Append(JsonNumber(end)).Append(',');
            json.Append("\"kind\":").Append(chunk.Kind == ChunkKind.Transcript ? "\"transcript\"" : "\"summary\"").Append(',');
            if (chunk.Start is { } start) json.Append("\"start\":").Append(JsonNumber(start)).Append(',');
            json.Append("\"text\":");
            AppendString(json, chunk.Text);
            json.Append('}');
        }
        json.Append("],\"dimension\":").Append(shard.Dimension.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"fingerprint\":");
        AppendString(json, shard.Fingerprint);
        json.Append(",\"vectors\":");
        AppendString(json, Convert.ToBase64String(shard.Vectors));
        json.Append(",\"version\":").Append(shard.Version.ToString(CultureInfo.InvariantCulture)).Append('}');
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    /// <summary>Throws <see cref="JsonException"/> or <see cref="FormatException"/> when the JSON is not a shard (any key order).</summary>
    internal static Shard DecodeShard(byte[] utf8)
    {
        var file = SwiftJson.Deserialize<ShardFile>(utf8);
        var chunks = file.Chunks.Select(c => new Chunk(
            c.Kind switch
            {
                "transcript" => ChunkKind.Transcript,
                "summary" => ChunkKind.Summary,
                _ => throw new JsonException($"Unknown chunk kind {c.Kind}."),
            }, c.Start, c.End, c.Text)).ToList();
        return new Shard(file.Version, file.Fingerprint, file.Dimension, chunks, Convert.FromBase64String(file.Vectors));
    }

    private sealed record ShardFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")] public required int Version { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("fingerprint")] public required string Fingerprint { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("dimension")] public required int Dimension { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("chunks")] public required List<ChunkFile> Chunks { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("vectors")] public required string Vectors { get; init; }
    }

    private sealed record ChunkFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("kind")] public required string Kind { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("start")] public double? Start { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("end")] public double? End { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("text")] public required string Text { get; init; }
    }

    /// <summary>Swift <c>JSONEncoder</c>'s string escapes: <c>" \ /</c>, the short forms, other C0 controls as lowercase <c>\u00xx</c>.</summary>
    private static void AppendString(StringBuilder json, string text)
    {
        json.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '/': json.Append("\\/"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                case < ' ': json.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture)); break;
                default: json.Append(c); break;
            }
        }
        json.Append('"');
    }

    /// <summary>A double as Swift's <c>JSONEncoder</c> writes it: shortest round-trip form, <c>300</c>, <c>8.5</c>, <c>1e-07</c>.</summary>
    private static string JsonNumber(double value)
    {
        if (!double.IsFinite(value)) throw new JsonException("A chunk time is not a finite number.");
        return value.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');
    }

    /// <summary>A double as Swift's <c>description</c> prints it: <c>1800000000.0</c>, <c>1729700000.1234567</c>.</summary>
    private static string SwiftDescription(double value)
    {
        var text = JsonNumber(value);
        return text.Contains('.') || text.Contains('e') ? text : text + ".0";
    }

    // Helpers

    /// <summary>Runs <paramref name="body"/> on the thread pool, after every earlier call ended.</summary>
    private async Task<T> RunAsync<T>(Func<T> body, CancellationToken cancellationToken)
    {
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(body, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    private Task RunAsync(Action body, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            body();
            return true;
        }, cancellationToken);

    private void Unload()
    {
        _key = null;
        _entries = [];
        _dimension = null;
    }

    /// <summary><c>root/key</c>, or null when <paramref name="key"/> is not a plain folder name (empty, <c>.</c>, <c>..</c>, a separator).</summary>
    private string? FolderFor(string key)
    {
        if (key.Length == 0 || key is "." or ".." || key.IndexOfAny(['/', '\\', ':']) >= 0 || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        return Path.Combine(Root, key);
    }

    private IEnumerable<string> KeyFolders()
    {
        try
        {
            return Directory.Exists(Root) ? Directory.GetDirectories(Root) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> Children(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFileSystemEntries(folder).Select(p => Path.GetFileName(p)) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary><c>&lt;uuid&gt;.json</c> files of <paramref name="folder"/>, by conversation id.</summary>
    private static List<(Guid Id, string Path)> ShardFiles(string folder)
    {
        var shards = new List<(Guid, string)>();
        foreach (var name in Children(folder))
        {
            if (name.EndsWith(".json", StringComparison.Ordinal) && Guid.TryParseExact(name[..^5], "D", out var id))
                shards.Add((id, Path.Combine(folder, name)));
        }
        return shards;
    }

    /// <summary>Upper-case, as Swift's <c>uuidString</c> and every other file of the library.</summary>
    private static string UuidString(Guid id) => id.ToString("D").ToUpperInvariant();

    internal static string ShardFileName(Guid id) => UuidString(id) + ".json";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A file, or a folder with everything in it (Swift <c>removeItem</c>); errors are ignored, as with Swift's <c>try?</c>.</summary>
    private static void TryDeleteEntry(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<IReadOnlyList<string>> TermsOf(IReadOnlyList<Chunk> chunks) =>
        chunks.Select(c => (IReadOnlyList<string>)TranscriptChunker.Terms(c.Text)).ToList();

    private static byte[] Bytes(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        for (var i = 0; i < floats.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), floats[i]);
        return bytes;
    }

    /// <summary><c>chunks × dimension</c> Float32 little-endian values, or null when <paramref name="data"/> has another size.</summary>
    private static float[]? Floats(byte[] data, int chunks, int dimension)
    {
        if (dimension < 0 || chunks < 0) return null;
        var count = (long)chunks * dimension;
        if (count > data.Length / 4 || data.Length != count * 4) return null;
        var floats = new float[count];
        for (var i = 0; i < floats.Length; i++) floats[i] = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(i * 4));
        return floats;
    }
}
