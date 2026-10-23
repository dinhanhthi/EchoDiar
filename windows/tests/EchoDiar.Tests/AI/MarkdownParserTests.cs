using System.Diagnostics;
using EchoDiar.AI;
using EchoDiar.Core.Model;

namespace EchoDiar.Tests.AI;

// Guards the content isolation of model answers (security): only http(s) links without user info, [mm:ss] citations
// and echodiar-ref citations of the answer's own references become links; images, HTML and every other scheme stay
// plain text. Also the line/inline structure MarkdownView builds from, and linear time on hostile input.
public sealed class MarkdownParserTests
{
    private static readonly Guid Known = Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF");

    private static readonly ChatLog.Reference[] References =
        [new ChatLog.Reference { Number = 1, ConversationID = Known, Title = "Weekly sync" }];

    private static MarkdownInline[] Inlines(string markdown, IReadOnlyList<ChatLog.Reference>? references = null) =>
        MarkdownParser.Parse(markdown, references).Single().Inlines.ToArray();

    private static string Text(IEnumerable<MarkdownInline> inlines) => string.Concat(inlines.Select(i => i.Text));

    [Fact]
    public void LinesBecomeHeadingsListsAndParagraphs()
    {
        var blocks = MarkdownParser.Parse("## Plan\n\n- one\n    * nested\n2. second\nplain #tag\n");

        Assert.Equal(
            [
                (MarkdownBlockKind.Heading, 2, "", "Plan"),
                (MarkdownBlockKind.Bullet, 0, "", "one"),
                (MarkdownBlockKind.Bullet, 2, "", "nested"),
                (MarkdownBlockKind.Numbered, 0, "2.", "second"),
                (MarkdownBlockKind.Paragraph, 0, "", "plain #tag"),
            ],
            blocks.Select(b => (b.Kind, b.Level, b.Marker, Text(b.Inlines))));
    }

    [Fact]
    public void EmphasisAndCodeAreStyled()
    {
        var inlines = Inlines("a **bold** *it* `x*y*` ***both*** snake_case_name");

        Assert.Contains(new MarkdownInline("bold", MarkdownStyle.Bold), inlines);
        Assert.Contains(new MarkdownInline("it", MarkdownStyle.Italic), inlines);
        Assert.Contains(new MarkdownInline("x*y*", MarkdownStyle.Code), inlines);
        Assert.Contains(new MarkdownInline("both", MarkdownStyle.Bold | MarkdownStyle.Italic), inlines);
        Assert.Equal("a bold it x*y* both snake_case_name", Text(inlines));
        Assert.EndsWith("snake_case_name", inlines[^1].Text);
    }

    [Theory]
    [InlineData("http://example.com/a?b=1")]
    [InlineData("https://example.com/")]
    [InlineData("HTTPS://Example.com/path")]
    public void WebLinksAreKept(string target)
    {
        var link = Assert.IsType<MarkdownLink.Web>(Inlines($"see [docs]({target})").Single(i => i.Link is not null).Link);

        Assert.Equal(new Uri(target), link.Url);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("javascript:alert")]
    [InlineData("JavaScript://example.com/%0aalert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"\\server\share\x.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox")]
    [InlineData("search-ms:query=x")]
    [InlineData("/relative/path")]
    [InlineData("example.com")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("https://user@example.com/")]
    [InlineData("http:example.com")]
    [InlineData("echodiar-time:7")]
    public void OtherTargetsStayPlainText(string target)
    {
        var inlines = Inlines($"[click]({target})", References);

        Assert.All(inlines, i => Assert.Null(i.Link));
        Assert.Contains("click", Text(inlines));
    }

    [Fact]
    public void ImagesShowTheirAltTextOnly()
    {
        var inlines = Inlines("before ![a cat](https://example.com/cat.png) after");

        Assert.Equal([new MarkdownInline("before a cat after", MarkdownStyle.None)], inlines);
    }

    [Fact]
    public void HtmlStaysLiteral()
    {
        const string html = "<img src=x onerror=alert(1)> <b>bold</b> <script>x</script>";

        Assert.Equal([new MarkdownInline(html, MarkdownStyle.None)], Inlines(html));
    }

    [Theory]
    [InlineData("At [00:07] we agreed.", "[00:07]", 7)]
    [InlineData("At [1:02:03].", "[1:02:03]", 3_723)]
    [InlineData("At [1,000:05].", "[1,000:05]", 60_005)]
    [InlineData("See [the start](echodiar-time:12).", "the start", 12)]
    public void TranscriptCitationsSeek(string markdown, string label, double seconds)
    {
        var inline = Inlines(markdown).Single(i => i.Link is not null);

        Assert.Equal(label, inline.Text);
        Assert.Equal(new MarkdownLink.Seek(seconds), inline.Link);
    }

    [Theory]
    [InlineData("[99:99]")]
    [InlineData("[2000:00]")]
    [InlineData("[x](echodiar-time:86401)")]
    [InlineData("[x](echodiar-time:-1)")]
    [InlineData("[x](echodiar-time:1e3)")]
    public void OutOfRangeCitationsStayPlainText(string markdown) =>
        Assert.All(Inlines(markdown), i => Assert.Null(i.Link));

    [Fact]
    public void KnownReferencesOpenTheirConversation()
    {
        var inlines = Inlines($"As said [1 00:05], see [the summary](echodiar-ref:{Known}) and [1].", References);

        Assert.Equal(
            [new MarkdownLink.Reference(Known, 5), new MarkdownLink.Reference(Known, null), new MarkdownLink.Reference(Known, null)],
            inlines.Where(i => i.Link is not null).Select(i => i.Link));
        Assert.Equal("As said [1 00:05], see the summary and [1].", Text(inlines));
    }

    [Theory]
    [InlineData("[x](echodiar-ref:11111111-2222-3333-4444-555555555555:5)")]
    [InlineData("[2 00:05]")]
    [InlineData("[00:05]")]
    public void UnknownReferencesStayPlainText(string markdown) =>
        Assert.All(Inlines(markdown, References), i => Assert.Null(i.Link));

    [Fact]
    public void ReferencesOutsideALibraryAnswerStayPlainText() =>
        Assert.All(Inlines($"[x](echodiar-ref:{Known}:5) [1 00:05]"), i => Assert.Null(i.Link));

    [Theory]
    [InlineData("**unclosed *mixed _under `tick [link](http://x")]
    [InlineData("[[[[]]]]((((()))))")]
    [InlineData("[a](b)(c)[d][e](f g)")]
    [InlineData("****** ______ ``` `` ` \\")]
    [InlineData("![[x](http://a)](http://b)")]
    [InlineData("**a *b **c *d **e *f **g *h **i *j **k *l**")]
    [InlineData("#\n# \n-\n- \n1.\n1. \n#######  x")]
    public void MalformedMarkdownDoesNotThrow(string markdown)
    {
        var blocks = MarkdownParser.Parse(markdown);

        Assert.All(blocks, b => Assert.NotNull(b.Inlines));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("**a ")]
    [InlineData("[")]
    [InlineData("[a](")]
    [InlineData("[a](b ")]
    [InlineData("`")]
    [InlineData("_a ")]
    [InlineData("[[a]")]
    [InlineData("[1 00:0")]
    [InlineData("[a](https://x.com/")]
    public void HostileLongLinesParseInLinearTime(string unit)
    {
        var markdown = string.Concat(Enumerable.Repeat(unit, 1_000_000 / unit.Length + 1));
        var stopwatch = Stopwatch.StartNew();

        var blocks = MarkdownParser.Parse(markdown, unit.StartsWith("[1") ? References : null);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"{stopwatch.Elapsed} for {unit}");
        Assert.True(Text(blocks.Single().Inlines).Length >= 900_000);
    }

    [Fact]
    public void ALongAnswerParsesInBoundedTime()
    {
        const string sample = "## Notes\n- **Decision** at [00:07]: ship *it* with `code` and [docs](https://example.com/x)\n1. next\n";
        var markdown = string.Concat(Enumerable.Repeat(sample, 1_000_000 / sample.Length + 1));
        var stopwatch = Stopwatch.StartNew();

        var blocks = MarkdownParser.Parse(markdown);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
        Assert.Equal(3 * (1_000_000 / sample.Length + 1), blocks.Count);
    }
}
