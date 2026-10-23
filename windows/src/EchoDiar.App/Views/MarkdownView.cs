using EchoDiar.AI;
using EchoDiar.Core.Model;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace EchoDiar.App.Views;

/// <summary>
/// What an answer's links do. <see cref="References"/> are a library answer's sources (null for an answer about one
/// conversation, whose <c>[mm:ss]</c> citations call <see cref="Seek"/>); only their ids open a conversation. A link
/// whose action is null shows as plain text. <see cref="OpenUrl"/> gets a checked http(s) URL and should show its
/// destination before opening it (the link text is model output), as macOS does.
/// </summary>
public sealed record MarkdownLinks(
    IReadOnlyList<ChatLog.Reference>? References = null,
    Action<double>? Seek = null,
    Action<Guid, double?>? OpenConversation = null,
    Action<Uri>? OpenUrl = null);

/// <summary>
/// Model Markdown in a <see cref="RichTextBlock"/> (macOS <c>MarkdownText</c>): the lines and styles of
/// <see cref="MarkdownParser"/>, built from Paragraph, Run and Hyperlink objects only, so the text is never read as
/// XAML, HTML or an image source. Hyperlinks have no NavigateUri: a click calls the <see cref="MarkdownLinks"/> action,
/// so the framework never launches anything itself. Each call rebuilds the whole text (a parse is linear, but the
/// selection is lost), so a streamed answer should be rendered at most a few times a second plus once at its end.
/// </summary>
public static class MarkdownView
{
    /// <summary>The indent of a list level, as macOS (14 points).</summary>
    private const double Indent = 14;

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

    public static void Render(RichTextBlock target, string markdown, MarkdownLinks links)
    {
        target.IsTextSelectionEnabled = true;
        target.Blocks.Clear();
        foreach (var block in MarkdownParser.Parse(markdown, links.References))
            target.Blocks.Add(ToParagraph(block, links));
    }

    private static Paragraph ToParagraph(MarkdownBlock block, MarkdownLinks links)
    {
        var heading = block.Kind == MarkdownBlockKind.Heading;
        var list = block.Kind is MarkdownBlockKind.Bullet or MarkdownBlockKind.Numbered;
        var paragraph = new Paragraph { Margin = new Thickness(list ? block.Level * Indent : 0, heading ? 4 : 0, 0, 4) };
        if (heading) paragraph.FontWeight = FontWeights.SemiBold;
        if (block.Kind == MarkdownBlockKind.Bullet) paragraph.Inlines.Add(new Run { Text = "•  " });
        if (block.Kind == MarkdownBlockKind.Numbered) paragraph.Inlines.Add(new Run { Text = block.Marker + "  " });
        foreach (var inline in block.Inlines) paragraph.Inlines.Add(ToInline(inline, links));
        return paragraph;
    }

    private static Inline ToInline(MarkdownInline inline, MarkdownLinks links)
    {
        var run = new Run { Text = inline.Text };
        if (inline.Style.HasFlag(MarkdownStyle.Bold)) run.FontWeight = FontWeights.Bold;
        if (inline.Style.HasFlag(MarkdownStyle.Italic)) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
        if (inline.Style.HasFlag(MarkdownStyle.Code)) run.FontFamily = CodeFont;
        if (LinkAction(inline.Link, links) is not { } action) return run;
        var hyperlink = new Hyperlink();
        hyperlink.Inlines.Add(run);
        hyperlink.Click += (_, _) => action();
        return hyperlink;
    }

    /// <summary>
    /// The click action of an allowed link, checked again here: a web URL must still be http(s) without user info, and a
    /// reference must be one of the answer's own.
    /// </summary>
    private static Action? LinkAction(MarkdownLink? link, MarkdownLinks links) => link switch
    {
        MarkdownLink.Web web when links.OpenUrl is { } open && MarkdownParser.WebUrl(web.Url.OriginalString) is { } url =>
            () => open(url),
        MarkdownLink.Seek seek when links.Seek is { } seekTo && links.References is null
            && seek.Seconds is >= 0 and <= MarkdownParser.MaxSeconds =>
            () => seekTo(seek.Seconds),
        MarkdownLink.Reference reference when links.OpenConversation is { } openConversation
            && links.References?.Any(r => r.ConversationID == reference.Id) == true =>
            () => openConversation(reference.Id, reference.Seconds),
        _ => null,
    };
}
