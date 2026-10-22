using EchoDiar.Core;

namespace EchoDiar.Tests.Core;

// Guards the Settings search (Swift SettingsSearchItem.matches): a row is found by its title, a keyword, its section's
// title or its inner tab's title, ignoring case and accents, with the conversation search's "đ" rule; a blank term finds
// nothing.
public sealed class SettingsSearchTests
{
    private static readonly SettingsSearchItem Item = new(
        Title: "Default language",
        Keywords: ["Vietnamese", "Tiếng Việt", "Đà Nẵng"],
        Section: "transcription",
        SectionTitle: "Transcription",
        SubTab: "general",
        SubTabTitle: "General");

    [Theory]
    [InlineData("default LANGUAGE", true)]
    [InlineData("vietnamese", true)]
    [InlineData("tieng viet", true)]
    [InlineData("transcription", true)]
    [InlineData("  general ", true)]
    [InlineData("đà nẵng", true)]
    [InlineData("da nang", false)]
    [InlineData("kangaroo", false)]
    [InlineData("  ", false)]
    [InlineData("", false)]
    public void MatchesTitleKeywordsSectionAndSubTab(string term, bool expected) =>
        Assert.Equal(expected, Item.Matches(term));

    [Fact]
    public void PathSkipsASubTabNamedLikeTheRow()
    {
        Assert.Equal("Transcription › General › Default language", Item.Path);
        Assert.Equal("Storage › Playback", (Item with { Title = "Playback", SectionTitle = "Storage", SubTabTitle = "Playback" }).Path);
        Assert.Equal("About › Version", new SettingsSearchItem("Version", [], "about", "About").Path);
    }
}
