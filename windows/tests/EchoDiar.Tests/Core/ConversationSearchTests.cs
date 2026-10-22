using EchoDiar.Core;
using EchoDiar.Core.Model;

namespace EchoDiar.Tests.Core;

// Guards the entry search: every field of a conversation is searchable, ignoring case and accents. Each term below
// appears in exactly one field, so a match proves that field is indexed. Also guards multi-select in the list.
public sealed class ConversationSearchTests
{
    private static readonly ConversationSearch.Document Document = ConversationSearch.Build(
        title: "Weekly sync",
        transcript: new Transcript
        {
            Segments =
            [
                new() { Start = 0, End = 2, Channel = TranscriptChannel.Mic, Speaker = Transcript.Speaker.Me, Text = "Hello everyone" },
                new() { Start = 2, End = 4, Channel = TranscriptChannel.System, Speaker = "s1", Text = "Xin chào từ Việt Nam" },
            ],
            SpeakerNames = new() { ["s1"] = "Lan" },
        },
        summary: "Cuộc họp: the budget was approved.",
        chat: new ChatLog
        {
            Messages =
            [
                new() { Role = ChatRole.User, Text = "Who owns the roadmap?" },
                new() { Role = ChatRole.Assistant, Text = "The design guild in Đà Nẵng owns it." },
            ],
        });

    // Title, renamed speaker, its default name, summary, chat question and answer, transcript and summary without
    // accents; "đ" is a letter of its own, not "d" with an accent (as in Swift); a blank term matches everything.
    [Theory]
    [InlineData("WEEKLY", true)]
    [InlineData("lan", true)]
    [InlineData("Speaker 1", true)]
    [InlineData("budget", true)]
    [InlineData("roadmap", true)]
    [InlineData("guild", true)]
    [InlineData("viet", true)]
    [InlineData("cuoc hop", true)]
    [InlineData("đà nẵng", true)]
    [InlineData("da nang", false)]
    [InlineData("kangaroo", false)]
    [InlineData("  ", true)]
    public void MatchesEveryFieldIgnoringCaseAndAccents(string term, bool expected) =>
        Assert.Equal(expected, Document.Matches(term));

    private static readonly Guid[] Order = [.. Enumerable.Range(0, 6).Select(_ => Guid.NewGuid())];

    [Fact]
    public void ExtendSelectsTheSpanInListOrderAndKeepsToggledIds()
    {
        var selection = new ListSelection();
        selection.Toggle(Order[0]);
        selection.Toggle(Order[4]);

        selection.Extend(Order[2], Order);
        Assert.True(selection.Ids.SetEquals([Order[0], Order[2], Order[3], Order[4]]));
        Assert.Equal(Order[4], selection.Anchor);

        selection.Extend(Order[5], Order);
        Assert.True(selection.Ids.SetEquals([Order[0], Order[2], Order[3], Order[4], Order[5]]));
    }

    [Fact]
    public void ToggleDeselectsAndPruneDropsIdsThatLeftTheList()
    {
        var selection = new ListSelection();
        selection.Toggle(Order[1]);
        selection.Toggle(Order[3]);
        selection.Toggle(Order[5]);
        selection.Toggle(Order[5]);

        selection.Prune([Order[0], Order[1], Order[2]]);

        Assert.True(selection.Ids.SetEquals([Order[1]]));
        Assert.Null(selection.Anchor);
    }
}
