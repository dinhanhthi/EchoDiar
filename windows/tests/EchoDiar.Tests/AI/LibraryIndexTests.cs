using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EchoDiar.AI;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.AI;

// Guards the RAG index (LibraryIndexTests.swift): an indexed conversation is found again (with its time) by a fresh
// index on the same folder, and the reconcile pass marks changed conversations stale and drops shards of conversations
// that are gone. Plus the shard contract with macOS (a Swift-written shard is read; ours are Swift's bytes) and the API
// embedder's key. No network.
public sealed class LibraryIndexTests : IDisposable
{
    private const string Key = "fake-embedder";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"LibraryIndexTests-{Guid.NewGuid():N}");

    private string Rag => Path.Combine(_root, "RAG");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task IndexedChunkIsFoundWithItsTimeAfterReload()
    {
        var (conversation, transcript) = MakeConversation(turns: 40, budgetTurn: 30);
        var index = new LibraryIndex(Rag);
        await index.LoadAsync(Key, Token);
        await index.IndexAsync(conversation, transcript, null, new KeywordEmbedder(), Token);

        var reloaded = new LibraryIndex(Rag);
        await reloaded.LoadAsync(Key, Token);
        const string question = "What is the budget?";
        var vector = (await new KeywordEmbedder().EmbedAsync([question], true, Token))[0];
        var hits = await reloaded.SearchAsync(question, vector, 3, Token);

        var hit = Assert.IsType<LibraryIndex.Hit>(hits.FirstOrDefault());
        Assert.Equal(conversation.Id, hit.ConversationID);
        Assert.Equal(LibraryIndex.ChunkKind.Transcript, hit.Chunk.Kind);
        Assert.Contains("forty thousand", hit.Chunk.Text);
        var start = Assert.NotNull(hit.Chunk.Start);
        var end = Assert.NotNull(hit.Chunk.End);
        Assert.True(start <= 300 && end >= 308, $"{start}-{end}");
        Assert.True(start > 0);
        Assert.Equal(1, await reloaded.CountAsync());
    }

    [Fact]
    public void LongTurnsGiveNoWindowContainedInAnother()
    {
        // About 100, 100 and 150 tokens per turn: a shared turn plus the next one would not fit a window.
        int[] lengths = [400, 400, 600, 400, 400, 600, 400];
        var transcript = new Transcript
        {
            Segments = [.. lengths.Select((length, index) => new Transcript.Segment
            {
                Start = index * 10, End = index * 10 + 8, Channel = TranscriptChannel.System,
                Speaker = Transcript.Speaker.System(1), Text = new string('a', length),
            })],
        };
        var ranges = LibraryIndex.Chunks(transcript, null).Select(c => ((int)(c.Start!.Value / 10), (int)(c.End!.Value / 10))).ToList();

        Assert.Equal(0, ranges[0].Item1);
        Assert.Equal(lengths.Length - 1, ranges[^1].Item2);
        for (var i = 0; i < ranges.Count; i++)
        {
            for (var j = 0; j < ranges.Count; j++)
            {
                if (i == j) continue;
                var (a, b) = (ranges[i], ranges[j]);
                Assert.False(b.Item1 <= a.Item1 && a.Item2 <= b.Item2, $"window {a} is inside window {b}");
            }
        }
    }

    [Fact]
    public async Task ChangedTranscriptIsStaleAndUnlistedConversationLosesItsShard()
    {
        var (kept, keptTranscript) = MakeConversation(turns: 5);
        var (gone, goneTranscript) = MakeConversation(turns: 5);
        var index = new LibraryIndex(Rag);
        await index.LoadAsync(Key, Token);
        await index.IndexAsync(kept, keptTranscript, "Travel is booked.", new KeywordEmbedder(), Token);
        await index.IndexAsync(gone, goneTranscript, null, new KeywordEmbedder(), Token);
        Assert.Empty(await index.StaleAsync([kept, gone], Token));
        // A shard of another key, not loaded: only PruneAsync reaches it, without an embedder.
        var otherKey = Path.Combine(Rag, "other");
        Directory.CreateDirectory(otherKey);
        var orphan = Path.Combine(otherKey, $"{Guid.NewGuid().ToString().ToUpperInvariant()}.json");
        File.WriteAllText(orphan, "{}");
        await index.PruneAsync([kept, gone], Token);
        Assert.False(File.Exists(orphan));

        keptTranscript.Segments[0].Text += " The hiring plan was moved to next week.";
        keptTranscript.Write(kept.Folder);
        var stale = await index.StaleAsync([kept], Token);

        Assert.Equal([kept.Id], stale.Select(c => c.Id));
        var shards = Path.Combine(Rag, Key);
        Assert.False(File.Exists(Path.Combine(shards, LibraryIndex.ShardFileName(gone.Id))));
        Assert.True(File.Exists(Path.Combine(shards, LibraryIndex.ShardFileName(kept.Id))));
        Assert.Equal(1, await index.CountAsync());

        await index.RemoveAllAsync();
        Assert.False(Directory.Exists(shards));
    }

    // A load cancelled while it reads shards leaves no key loaded (IndexAsync refuses), and the next load works again.
    [Fact]
    public async Task CancelledLoadLeavesNoKeyAndNextLoadIndexesAgain()
    {
        var (conversation, transcript) = MakeConversation(turns: 5);
        var index = new LibraryIndex(Rag);
        await index.LoadAsync(Key, Token);
        await index.IndexAsync(conversation, transcript, null, new KeywordEmbedder(), Token);
        // Copies of the shard give the load work to be cancelled in.
        const int copies = 300;
        var shard = Path.Combine(Rag, Key, LibraryIndex.ShardFileName(conversation.Id));
        for (var i = 0; i < copies; i++)
            File.Copy(shard, Path.Combine(Rag, Key, LibraryIndex.ShardFileName(Guid.NewGuid())));

        // Retries until the cancel lands inside the loop: not before the load starts (the old shards stay), not after it ends.
        var midway = false;
        for (var attempt = 0; attempt < 20 && !midway; attempt++)
        {
            using var cancel = new CancellationTokenSource();
            var load = index.LoadAsync(Key, cancel.Token);
            await Task.Yield();
            cancel.Cancel();
            try
            {
                await load;
            }
            catch (OperationCanceledException)
            {
                midway = await index.CountAsync() == 0;
            }
        }
        Assert.True(midway, "no cancel landed while the load read shards");

        var refused = await Assert.ThrowsAsync<LibraryIndexException>(
            () => index.IndexAsync(conversation, transcript, null, new KeywordEmbedder(), Token));
        Assert.Equal(LibraryIndexErrorKind.InvalidKey, refused.Kind);
        await index.LoadAsync(Key, Token);
        Assert.Equal(copies + 1, await index.CountAsync());
        var (added, addedTranscript) = MakeConversation(turns: 5);
        await index.IndexAsync(added, addedTranscript, null, new KeywordEmbedder(), Token);
        Assert.Equal(copies + 2, await index.CountAsync());
    }

    // Fixtures/macos-shard.json is Swift's JSONEncoder output for LibraryIndex.Shard (keys in Swift's arbitrary order),
    // macos-shard-sorted.json the same shard with .sortedKeys: ours must read the first and write the second's bytes.
    [Fact]
    public async Task MacShardIsReadAndWrittenBackAsSwiftWritesIt()
    {
        var id = Guid.Parse("5EED0001-0000-4000-8000-000000000001");
        Directory.CreateDirectory(Path.Combine(Rag, Key));
        File.Copy(Fixture("macos-shard.json"), Path.Combine(Rag, Key, LibraryIndex.ShardFileName(id)));
        var index = new LibraryIndex(Rag);
        await index.LoadAsync(Key, Token);

        var hits = await index.SearchAsync("budget", [1, 0, 0], 3, Token);

        Assert.Equal(1, await index.CountAsync());
        var hit = hits[0];
        Assert.Equal(id, hit.ConversationID);
        Assert.Equal(new LibraryIndex.Chunk(
            LibraryIndex.ChunkKind.Transcript, 0, 8.5,
            "Me [00:00]: \"Budget\" a/b \\ tab\there é 😀 \u0001\u001f\u007f\u2028"), hit.Chunk);
        var shard = LibraryIndex.DecodeShard(File.ReadAllBytes(Fixture("macos-shard.json")));
        Assert.Equal("1800000000.25-1234|-", shard.Fingerprint);
        Assert.Equal(3, shard.Dimension);
        Assert.Equal(new LibraryIndex.Chunk(LibraryIndex.ChunkKind.Summary, null, null, "Hiring <b>&</b> next week.\nSecond line"), shard.Chunks[2]);
        Assert.Equal(7200.123456789, shard.Chunks[1].Start);
        Assert.Equal(File.ReadAllBytes(Fixture("macos-shard-sorted.json")), LibraryIndex.EncodeShard(shard));
    }

    [Fact]
    public async Task ApiEmbedderKeyNamesProviderAndModelAndKeySitsInTheHeader()
    {
        const string apiKey = "sk-test-fake-0000";
        HttpRequestMessage? sent = null;
        string? body = null;
        var handler = new FakeHandler(async request =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"index":0,"embedding":[3,4]}]}""", Encoding.UTF8, "application/json"),
            };
        });
        var config = ProviderConfig.Presets().Single(p => p.Id == "openai");
        var embedder = new APITextEmbedder(config, apiKey, "Text Embedding/3", handler);
        var local = ProviderConfig.Custom(baseURL: " http://[::1]:11434/v1 ");

        var vectors = await embedder.WithLoadedAsync(loaded => loaded.EmbedAsync(["hello"], false, Token), Token);

        Assert.Equal("api-openai-text-embedding-3", embedder.Key);
        Assert.False(embedder.IsLocal);
        Assert.True(new APITextEmbedder(local, null, "m").IsLocal);
        Assert.Equal("https://api.openai.com/v1/embeddings", sent!.RequestUri!.AbsoluteUri);
        Assert.Equal($"Bearer {apiKey}", sent.Headers.Authorization!.ToString());
        Assert.DoesNotContain(apiKey, body);
        Assert.Equal("hello", JsonNode.Parse(body!)!["input"]![0]!.GetValue<string>());
        Assert.Equal([0.6f, 0.8f], vectors[0]);
    }

    /// <summary>
    /// A transcribed conversation in <c>root/library/&lt;id&gt;</c>, its <c>transcript.json</c> written: <paramref name="turns"/>
    /// turns about travel, turn <paramref name="budgetTurn"/> (if any) about the budget.
    /// </summary>
    private (Conversation, Transcript) MakeConversation(int turns, int? budgetTurn = null)
    {
        var id = Guid.NewGuid();
        var folder = Path.Combine(_root, "library", id.ToString().ToUpperInvariant());
        var transcript = new Transcript
        {
            Segments = [.. Enumerable.Range(0, turns).Select(index => new Transcript.Segment
            {
                Start = index * 10, End = index * 10 + 8, Channel = TranscriptChannel.System,
                Speaker = Transcript.Speaker.System(1),
                Text = index == budgetTurn
                    ? "The budget for next quarter is forty thousand euros."
                    : $"We went through the travel plans for the offsite, item {index}.",
            })],
        };
        transcript.Write(folder);
        var conversation = new Conversation
        {
            Id = id, Title = "Planning", CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), HasTranscript = true,
            Folder = folder,
        };
        conversation.Write();
        return (conversation, transcript);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>One dimension per keyword, plus one for text with none of them; L2-normalised.</summary>
    private sealed class KeywordEmbedder : ILoadedTextEmbedder
    {
        private static readonly string[] Keywords = ["budget", "travel", "hiring"];

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, bool isQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(text =>
            {
                var lower = text.ToLowerInvariant();
                var vector = Keywords.Select(k => lower.Contains(k) ? 1f : 0f).ToList();
                vector.Add(vector.Contains(1) ? 0 : 1);
                return EmbeddingMath.Normalized([.. vector]);
            })]);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await respond(request);
            response.RequestMessage = request;
            return response;
        }
    }
}
