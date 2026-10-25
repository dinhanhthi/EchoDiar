using EchoDiar.Core;

namespace EchoDiar.Tests.Core;

// Swift AppLanguage.matching: the app language of a BCP-47 tag by prefix, anything else is System.
public sealed class AppLanguageTests
{
    [Theory]
    [InlineData("zh-Hans-CN", AppLanguage.ZhHans)]
    [InlineData("zh-Hans", AppLanguage.ZhHans)]
    [InlineData("zh-hans-cn", AppLanguage.ZhHans)]
    [InlineData("fr-FR", AppLanguage.Fr)]
    [InlineData("fr", AppLanguage.Fr)]
    [InlineData("en-GB", AppLanguage.En)]
    [InlineData("EN-us", AppLanguage.En)]
    [InlineData("vi-VN", AppLanguage.Vi)]
    [InlineData("es-419", AppLanguage.Es)]
    [InlineData(null, AppLanguage.System)]
    [InlineData("", AppLanguage.System)]
    [InlineData("de-DE", AppLanguage.System)]
    [InlineData("zh-Hant-TW", AppLanguage.System)]
    [InlineData("zh", AppLanguage.System)]
    [InlineData("eng", AppLanguage.System)]
    public void MatchingByPrefix(string? tag, AppLanguage expected) =>
        Assert.Equal(expected, AppLanguages.Matching(tag));

    [Fact]
    public void OverrideTagRoundTripsAndSystemClearsTheOverride()
    {
        Assert.Equal("", AppLanguage.System.OverrideTag());
        Assert.Equal(AppLanguage.System, AppLanguages.Matching(AppLanguage.System.OverrideTag()));
        foreach (var language in AppLanguages.All.Where(l => l != AppLanguage.System))
        {
            Assert.Equal(language, AppLanguages.Matching(language.OverrideTag()));
        }
        Assert.Equal(["", "en-US", "vi", "fr", "es", "zh-Hans"], AppLanguages.All.Select(l => l.OverrideTag()));
    }

    // Loc.Plural's "one" form: CLDR integer rules of the five resw languages.
    [Theory]
    [InlineData("en-US", 1, true)]
    [InlineData("en-US", 0, false)]
    [InlineData("en-US", 2, false)]
    [InlineData("es", 1, true)]
    [InlineData("fr", 0, true)]
    [InlineData("fr", 1, true)]
    [InlineData("fr", 2, false)]
    [InlineData("vi", 1, false)]
    [InlineData("zh-Hans", 1, false)]
    public void PluralOneFollowsTheLanguageRule(string tag, long count, bool expected) =>
        Assert.Equal(expected, AppLanguages.Matching(tag).IsPluralOne(count));
}
