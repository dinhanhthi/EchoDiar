using EchoDiar.AI;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.AI;

// Guards the library chat's core (LibraryChatModel.swift, no Swift suite): a question's sources are numbered per
// conversation in first-hit order within the token budget and the 12-passage cap, deleted conversations are never
// cited; earlier answers lose their citations before they go back into a prompt; and the cloud embedding confirmation
// key changes with the destination origin.
public sealed class LibraryChatTests
{
    private static Conversation MakeConversation(string title) => new()
    {
        Id = Guid.NewGuid(), Title = title, CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), Folder = "/tmp",
    };

    private static LibraryIndex.Hit Hit(Conversation conversation, string text, double score = 1) =>
        new(conversation.Id, new LibraryIndex.Chunk(LibraryIndex.ChunkKind.Transcript, 0, 5, text), score);

    [Fact]
    public void SourcesAreGroupedPerConversationAndNumberedInFirstHitOrder()
    {
        var sync = MakeConversation("Weekly sync");
        var retro = MakeConversation("Retro");
        var deleted = MakeConversation("Gone");
        var hits = new[] { Hit(retro, "Me [00:07]: Friday."), Hit(deleted, "Old."), Hit(sync, "Me [01:10]: Budget."), Hit(retro, "Me [02:00]: OK.") };

        var (sources, references) = LibraryChat.Sources(hits, [sync, retro], budget: 10_000);

        Assert.Equal([1, 2], sources.Select(s => s.Number));
        Assert.Equal(["Retro", "Weekly sync"], sources.Select(s => s.Title));
        Assert.Equal(["Me [00:07]: Friday.", "Me [02:00]: OK."], sources[0].Passages);
        Assert.Equal([(1, retro.Id), (2, sync.Id)], references.Select(r => (r.Number, r.ConversationID)));
    }

    [Fact]
    public void SourcesStayWithinTheBudgetAndTheCap()
    {
        var conversation = MakeConversation("Weekly sync");
        var big = new string('a', 400);
        var many = Enumerable.Range(0, 20).Select(i => Hit(conversation, $"passage {i}")).ToList();

        var capped = LibraryChat.Sources(many, [conversation], budget: 10_000).Sources;
        // 100 + 2 tokens, plus 3 + 20 for the new source's title and line: 125, over 110, so the big hit is skipped and the small one fits.
        var fitted = LibraryChat.Sources([Hit(conversation, big), Hit(conversation, "small")], [conversation], budget: 110).Sources;

        Assert.Equal(LibraryChat.MaxPassages, capped.Single().Passages.Count);
        Assert.Equal(["small"], fitted.Single().Passages);
    }

    [Theory]
    [InlineData("It is Friday [1 00:07] and [2].", "It is Friday and.")]
    [InlineData("See [the launch](echodiar-ref:5EED0001-0000-0000-0000-000000000001:7) [1, 01:02:03].", "See the launch.")]
    [InlineData("A link [1](https://example.com) stays.", "A link [1](https://example.com) stays.")]
    [InlineData("[1000 00:07] is not a source number.", "[1000 00:07] is not a source number.")]
    public void EarlierAnswersLoseTheirCitations(string text, string expected) =>
        Assert.Equal(expected, LibraryChat.WithoutCitations(text));

    [Fact]
    public void EmbeddingDestinationIsTheOrigin()
    {
        var config = ProviderConfig.Custom(baseURL: " HTTPS://API.Example.com:443/v1 ");
        var other = ProviderConfig.Custom(baseURL: "http://api.example.com:8080/v1");

        Assert.Equal($"{config.Id}@https://api.example.com", LibraryChat.EmbeddingDestination(config));
        Assert.Equal($"{other.Id}@http://api.example.com:8080", LibraryChat.EmbeddingDestination(other));
        Assert.Null(LibraryChat.EmbeddingDestination(ProviderConfig.Custom(baseURL: "not a url")));
    }
}
