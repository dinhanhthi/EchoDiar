using System.Runtime.CompilerServices;
using EchoDiar.AI;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;

namespace EchoDiar.Tests.AI;

// Guards the summary job: map-reduce sends status lines and streams only the final reduce, the result is saved as a
// version, a cancelled job saves nothing, a stopped job saves what it streamed, and an in-app model is unloaded before
// the gate is released. Also guards how many earlier chat messages a question sends, the library question's sources
// and references, and the clickable citations. AIServiceTests.swift + AIStorageTests.swift (citations).
public sealed class AIServiceTests : IDisposable
{
    /// <summary>Answers every request with <c>reply</c>, in two pieces, and reports a small context to force map-reduce.</summary>
    private sealed class FakeProvider(string reply, int context) : ILLMProvider
    {
        private readonly Lock _lock = new();
        private readonly List<IReadOnlyList<LLMMessage>> _received = [];
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<LLMMessage>? LastReceived
        {
            get { lock (_lock) return _received.Count > 0 ? _received[^1] : null; }
        }

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<LLMMessage> messages, string model, int maxTokens,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            lock (_lock) _received.Add(messages);
            await Task.Yield();
            yield return reply[..(reply.Length / 2)];
            yield return reply[(reply.Length / 2)..];
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["fake"]);

        public ValueTask<int> ContextWindowAsync(string model, CancellationToken cancellationToken = default) => new(context);
    }

    /// <summary>Sends <c>piece</c> (when not empty), then waits until cancelled, as a slow answer does.</summary>
    private sealed class StallingProvider(string piece, int context = 128_000) : ILLMProvider
    {
        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<LLMMessage> messages, string model, int maxTokens,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (piece.Length > 0) yield return piece;
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["fake"]);

        public ValueTask<int> ContextWindowAsync(string model, CancellationToken cancellationToken = default) => new(context);
    }

    /// <summary>
    /// An in-app model whose generation sends one piece, then waits until cancelled and returns normally; its unload
    /// takes a moment.
    /// </summary>
    private sealed class FakeLocalProvider : ILocalLLMProvider
    {
        private volatile bool _unloaded;

        public bool Unloaded => _unloaded;

        private sealed class Generator : ILocalLLMGenerator
        {
            public async Task<string> GenerateAsync(
                IReadOnlyList<LLMMessage> messages, int maxTokens, Action<string> onText,
                CancellationToken cancellationToken = default)
            {
                onText("Partial");
                while (!cancellationToken.IsCancellationRequested) await Task.Delay(10, CancellationToken.None);
                return "Partial answer";
            }
        }

        public async Task<T> WithLoadedModelAsync<T>(
            string model, Func<ILocalLLMGenerator, Task<T>> body, CancellationToken cancellationToken = default)
        {
            try
            {
                return await body(new Generator());
            }
            finally
            {
                await Unload();
            }
        }

        /// <summary>Not cut short by the job's cancellation.</summary>
        private async Task Unload()
        {
            await Task.Delay(50, CancellationToken.None);
            _unloaded = true;
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["local"]);

        public ValueTask<int> ContextWindowAsync(string model, CancellationToken cancellationToken = default) => new(128_000);
    }

    private readonly List<string> _folders = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var folder in _folders)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private string NewFolderPath()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"AIServiceTests-{Guid.NewGuid()}");
        _folders.Add(folder);
        return folder;
    }

    private static Transcript MakeTranscript(int turns) => new()
    {
        Segments = Enumerable.Range(0, turns).Select(index => new Transcript.Segment
        {
            Start = index * 7, End = index * 7 + 6,
            Channel = index % 2 == 0 ? TranscriptChannel.Mic : TranscriptChannel.System,
            Speaker = index % 2 == 0 ? Transcript.Speaker.Me : Transcript.Speaker.System(1),
            Text = $"Turn {index}: we went through item {index} of the roadmap in detail.", Language = "en",
        }).ToList(),
    };

    private Conversation MakeConversation()
    {
        var folder = NewFolderPath();
        Directory.CreateDirectory(folder);
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), Title = "Weekly sync", CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            Folder = folder,
        };
        conversation.Write();
        return conversation;
    }

    private static async Task<List<AIUpdate>> Collect(IAsyncEnumerable<AIUpdate> updates)
    {
        var all = new List<AIUpdate>();
        await foreach (var update in updates) all.Add(update);
        return all;
    }

    [Fact]
    public async Task MapReduceStreamsOnlyTheFinalReduceAndSavesIt()
    {
        var conversation = MakeConversation();
        const string reply = "Summary: the roadmap was reviewed item by item [00:07].";
        var provider = new FakeProvider(reply, context: 1_500);
        var service = new AIService(new ModelLoadGate());

        var updates = await Collect(service.Summarize(
            conversation, MakeTranscript(120), provider, providerID: "fake", model: "fake", language: "en", cancellationToken: Ct));

        var firstText = updates.FindIndex(u => u.Kind == AIUpdateKind.Text);
        Assert.True(firstText >= 0);
        var statuses = updates[..firstText];
        Assert.True(provider.Calls > 2);
        Assert.Equal(provider.Calls, statuses.Count);
        Assert.All(statuses, u => Assert.Equal(AIUpdateKind.Status, u.Kind));
        Assert.Equal(AIUpdate.Status("Writing the summary…"), statuses[^1]);
        Assert.All(updates[firstText..], u => Assert.Equal(AIUpdateKind.Text, u.Kind));
        Assert.Equal(reply, string.Concat(updates[firstText..].Select(u => u.Value)));
        Assert.Equal(reply, Conversation.ReadSummary(conversation.Folder));
        Assert.True(Conversation.Read(conversation.Folder).HasSummary);
        var versions = Conversation.ReadSummaryVersions(conversation.Folder);
        Assert.Equal(["fake"], versions.Select(v => v.Provider));
        Assert.Equal([false], versions.Select(v => v.Stopped));
    }

    [Fact]
    public async Task CancelledLocalJobSavesNothingAndUnloadsBeforeReleasingTheGate()
    {
        var conversation = MakeConversation();
        var provider = new FakeLocalProvider();
        var gate = new ModelLoadGate();
        var service = new AIService(gate);

        var updates = new List<AIUpdate>();
        await foreach (var update in service.Summarize(
            conversation, MakeTranscript(3), provider, providerID: "local", model: "local", language: "en", cancellationToken: Ct))
        {
            updates.Add(update);
            if (update.Kind == AIUpdateKind.Text) break;
        }
        Assert.Equal(
            [AIUpdate.Status(AIService.WaitingStatus), AIUpdate.Status(AIService.LoadingStatus), AIUpdate.Text("Partial")],
            updates);

        // Served once the cancelled job has left the gate.
        await gate.WithExclusiveModelAsync(ModelLoadGate.Owner.TranscriptionJob, _ =>
        {
            Assert.True(provider.Unloaded);
            return Task.FromResult(true);
        }, Ct);
        Assert.Null(Conversation.ReadSummary(conversation.Folder));
        Assert.False(Conversation.Read(conversation.Folder).HasSummary);
    }

    [Fact]
    public async Task StoppedQuestionSavesTheStreamedPart()
    {
        var conversation = MakeConversation();
        var service = new AIService(new ModelLoadGate());

        var streamed = "";
        await foreach (var update in service.Ask(
            "What was decided?", conversation, MakeTranscript(3), summary: null, history: [], historyLimit: 10,
            new StallingProvider("Ship on Friday "), providerID: "fake", model: "fake", language: "en", cancellationToken: Ct))
        {
            if (update.Kind != AIUpdateKind.Text) continue;
            streamed += update.Value;
            service.Stop();
        }

        Assert.Equal("Ship on Friday ", streamed);
        var messages = ChatLog.Read(conversation.Folder).Messages;
        Assert.Equal([ChatRole.User, ChatRole.Assistant], messages.Select(m => m.Role));
        Assert.Equal(["What was decided?", "Ship on Friday"], messages.Select(m => m.Text));
        Assert.Equal("fake", messages[^1].Provider);
    }

    [Theory]
    [InlineData(3, new[] { "History Q3", "History A3" })]
    [InlineData(0, new[] { "History Q1", "History A1", "History Q2", "History A2", "History Q3", "History A3" })]
    public async Task QuestionSendsTheLastHistoryMessagesFromAUserTurn(int limit, string[] expected)
    {
        var conversation = MakeConversation();
        var provider = new FakeProvider("Friday.", context: 128_000);
        var history = Enumerable.Range(1, 3).SelectMany(index => new[]
        {
            new ChatLog.Message { Role = ChatRole.User, Text = $"History Q{index}" },
            new ChatLog.Message { Role = ChatRole.Assistant, Text = $"History A{index}" },
        }).ToList();

        await Collect(new AIService(new ModelLoadGate()).Ask(
            "What was decided?", conversation, MakeTranscript(3), summary: null, history, historyLimit: limit, provider,
            providerID: "fake", model: "fake", language: "en", cancellationToken: Ct));

        var sent = Assert.IsAssignableFrom<IReadOnlyList<LLMMessage>>(provider.LastReceived)
            .Where(m => m.Content.StartsWith("History ", StringComparison.Ordinal)).ToList();
        Assert.Equal(expected, sent.Select(m => m.Content));
        Assert.Equal(LLMRole.User, sent[0].Role);
    }

    [Fact]
    public async Task LibraryQuestionSendsTheNumberedSourcesAndSavesTheReferences()
    {
        // Not created yet, as the global chat's folder on the first question.
        var folder = NewFolderPath();
        var provider = new FakeProvider("Friday [1 00:07].", context: 128_000);
        var date = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        PromptBuilder.LibrarySource[] sources =
        [
            new(1, "Weekly sync", date, ["Me [00:07]: Friday."]),
            new(2, "Retro", date, ["The launch slipped."]),
        ];
        ChatLog.Reference[] references =
        [
            new() { Number = 1, ConversationID = Guid.NewGuid(), Title = "Weekly sync" },
            new() { Number = 2, ConversationID = Guid.NewGuid(), Title = "Retro" },
        ];

        await Collect(new AIService(new ModelLoadGate()).AskLibrary(
            "When is the launch?", sources, references,
            [new ChatLog.Message { Role = ChatRole.User, Text = "Hi" }, new ChatLog.Message { Role = ChatRole.Assistant, Text = "Hello" }],
            historyLimit: 10, provider, providerID: "fake", model: "fake", language: "en", folder, Ct));

        var sent = Assert.IsAssignableFrom<IReadOnlyList<LLMMessage>>(provider.LastReceived);
        Assert.Equal([LLMRole.System, LLMRole.User, LLMRole.Assistant, LLMRole.User], sent.Select(m => m.Role));
        var user = sent[^1].Content;
        Assert.StartsWith("<sources>\n<source n=\"1\">\nConversation 1: Weekly sync, ", user);
        Assert.Contains("<source n=\"2\">\nConversation 2: Retro, ", user);
        Assert.EndsWith("Question: When is the launch?", user);
        var messages = ChatLog.Read(folder).Messages;
        Assert.Equal([ChatRole.User, ChatRole.Assistant], messages.Select(m => m.Role));
        Assert.Equal(["When is the launch?", "Friday [1 00:07]."], messages.Select(m => m.Text));
        Assert.Null(messages[0].References);
        Assert.Equal(references, messages[1].References!);
        Assert.Equal("fake", messages[^1].Provider);
        // As macOS writes them: both messages have a timestamp.
        Assert.All(messages, m => Assert.NotNull(m.Timestamp));
    }

    [Fact]
    public void LibraryCitationsLinkKnownSourcesOnly()
    {
        var id = Guid.NewGuid();
        var uuid = id.ToString("D").ToUpperInvariant();
        ChatLog.Reference[] references = [new() { Number = 1, ConversationID = id, Title = "Weekly sync" }];
        const string text =
            "Friday [1 01:05], see [1] and [1, 1:02:03]; not [2 00:07] nor [1 00:07](https://x.org) or [ok [1]](https://y.org).";

        var linked = LibraryCitation.Linked(text, references);

        Assert.Equal(
            $"Friday [1 01:05](echodiar-ref:{uuid}:65), see [1](echodiar-ref:{uuid})"
            + $" and [1, 1:02:03](echodiar-ref:{uuid}:3723); not [2 00:07] nor [1 00:07](https://x.org)"
            + " or [ok [1]](https://y.org).",
            linked);
        Assert.Equal(linked, LibraryCitation.Linked(linked, references));
        var parsed = LibraryCitation.Parse(new Uri($"echodiar-ref:{uuid}:65"));
        Assert.Equal(id, parsed?.Id);
        Assert.Equal(65, parsed?.Seconds);
        Assert.Null(LibraryCitation.Parse(new Uri($"echodiar-ref:{uuid}"))?.Seconds);
        Assert.NotNull(LibraryCitation.Parse(new Uri($"echodiar-ref:{uuid}")));
        Assert.Null(LibraryCitation.Parse(new Uri($"echodiar-ref:{uuid}:90000")));
        Assert.Null(LibraryCitation.Parse(new Uri("echodiar-ref:not-a-uuid")));
    }

    [Fact]
    public async Task StoppedSummarySavesAStoppedVersion()
    {
        var conversation = MakeConversation();
        var service = new AIService(new ModelLoadGate());

        await foreach (var update in service.Summarize(
            conversation, MakeTranscript(3), new StallingProvider("## Decisions"), providerID: "fake", model: "fake",
            language: "en", cancellationToken: Ct))
        {
            if (update.Kind == AIUpdateKind.Text) service.Stop();
        }

        var versions = Conversation.ReadSummaryVersions(conversation.Folder);
        Assert.Equal(["## Decisions"], versions.Select(v => v.Text));
        Assert.Equal([true], versions.Select(v => v.Stopped));
        Assert.Equal("## Decisions", Conversation.ReadSummary(conversation.Folder));
    }

    [Fact]
    public async Task StoppedLocalSummarySavesWhatTheModelReturned()
    {
        var conversation = MakeConversation();
        var service = new AIService(new ModelLoadGate());

        await foreach (var update in service.Summarize(
            conversation, MakeTranscript(3), new FakeLocalProvider(), providerID: "local", model: "local", language: "en", cancellationToken: Ct))
        {
            if (update.Kind == AIUpdateKind.Text) service.Stop();
        }

        var versions = Conversation.ReadSummaryVersions(conversation.Folder);
        Assert.Equal(["Partial answer"], versions.Select(v => v.Text));
        Assert.Equal([true], versions.Select(v => v.Stopped));
    }

    [Fact]
    public async Task SummaryStoppedBeforeItsTextSavesNothing()
    {
        var conversation = MakeConversation();
        var service = new AIService(new ModelLoadGate());

        var updates = new List<AIUpdate>();
        await foreach (var update in service.Summarize(
            conversation, MakeTranscript(120), new StallingProvider("Notes", context: 1_500), providerID: "fake",
            model: "fake", language: "en", cancellationToken: Ct))
        {
            updates.Add(update);
            service.Stop();
        }

        var only = Assert.Single(updates);
        Assert.Equal(AIUpdateKind.Status, only.Kind);
        Assert.StartsWith("Reading part 1 of", only.Value);
        Assert.Empty(Conversation.ReadSummaryVersions(conversation.Folder));
        Assert.False(Conversation.Read(conversation.Folder).HasSummary);
    }

    [Fact]
    public void SummaryLineIsCutAtTheChunkerAllowance()
    {
        Assert.Equal("We agreed on Friday.", AIService.SummaryLine("Notes\n**Summary:** We agreed on Friday.\n- detail"));
        Assert.Equal("We agreed on Friday.\n- detail", AIService.SummaryLine("**Summary:**\nWe agreed on Friday.\n- detail"));
        var bytes = System.Text.Encoding.UTF8.GetByteCount(AIService.SummaryLine("Summary: " + new string('é', 600)));
        Assert.InRange(bytes, 798, 800);
    }

    [Fact]
    public void CitationsParseToSeconds()
    {
        Assert.Equal(5, TranscriptCitation.Seconds("[00:05]"));
        Assert.Equal(4_530, TranscriptCitation.Seconds("[75:30]"));
        Assert.Equal(3_723, TranscriptCitation.Seconds("[1:02:03]"));
        Assert.Null(TranscriptCitation.Seconds("[00:75]"));
        Assert.Null(TranscriptCitation.Seconds("00:05"));

        var linked = TranscriptCitation.Linked("At [00:05] and [1:02:03]; see [10:00](https://x.y).", "cite");
        Assert.Equal("At [00:05](cite:5) and [1:02:03](cite:3723); see [10:00](https://x.y).", linked);
    }

    // From 1000 minutes on, PromptBuilder.Timestamp writes "1000:05" while macOS may write "1,000:05" (locale grouping);
    // both are cited back.
    [Theory]
    [InlineData("[1000:05]", 60_005)]
    [InlineData("[1,000:05]", 60_005)]
    [InlineData("[1 000:05]", 60_005)]
    public void LongCitationsAcceptBothSeparatorForms(string citation, double seconds)
    {
        Assert.Equal(seconds, TranscriptCitation.Seconds(citation));
        var id = Guid.NewGuid();
        ChatLog.Reference[] references = [new() { Number = 2, ConversationID = id, Title = "Offsite" }];
        Assert.EndsWith($":{seconds})", LibraryCitation.Linked($"[2 {citation[1..]}", references));
    }

    [Theory]
    [InlineData("**\"Launch date\"**\nMore text", "Launch date")]
    [InlineData("\n  # “Budget review”  \n", "Budget review")]
    [InlineData("Release plan for the new onboarding flow and the pricing page redesign", "Release plan for the new onboarding flow and the pricing pag")]
    [InlineData("\"\"\n", "")]
    public void TitleLineTakesTheFirstCleanLineCutAt60Characters(string answer, string title) =>
        Assert.Equal(title, AIService.TitleLine(answer));

    [Theory]
    [InlineData(1_500, 512)]
    [InlineData(8_192, 1_638)]
    [InlineData(128_000, 8_192)]
    public void MaxTokensIsTwentyPercentOfTheContextFrom512To8192(int context, int maxTokens) =>
        Assert.Equal(maxTokens, AIService.MaxTokens(context));
}
