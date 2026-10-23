using System.Globalization;
using System.Text;
using EchoDiar.Core.Model;

namespace EchoDiar.AI;

public enum MarkdownBlockKind { Paragraph, Heading, Bullet, Numbered }

[Flags]
public enum MarkdownStyle { None = 0, Bold = 1, Italic = 2, Code = 4 }

/// <summary>
/// A line of model Markdown: <see cref="Level"/> is 1-6 for a heading and the indent (leading spaces / 2) for a list
/// item; <see cref="Marker"/> is a numbered item's own number with its <c>.</c> or <c>)</c>, empty otherwise.
/// </summary>
public sealed record MarkdownBlock(MarkdownBlockKind Kind, int Level, string Marker, IReadOnlyList<MarkdownInline> Inlines);

/// <summary>A run of text in one style; <see cref="Link"/> is set only for an allowed link.</summary>
public sealed record MarkdownInline(string Text, MarkdownStyle Style, MarkdownLink? Link = null);

/// <summary>The only links an answer may carry; anything else is shown as plain text.</summary>
public abstract record MarkdownLink
{
    private MarkdownLink() { }

    /// <summary>An absolute http(s) URL without user info.</summary>
    public sealed record Web(Uri Url) : MarkdownLink;

    /// <summary>A <c>[mm:ss]</c> citation of the open conversation: seconds within 0...86 400.</summary>
    public sealed record Seek(double Seconds) : MarkdownLink;

    /// <summary>An <c>echodiar-ref:</c> citation of one of the answer's references (Seconds null: its summary).</summary>
    public sealed record Reference(Guid Id, double? Seconds) : MarkdownLink;
}

/// <summary>
/// Model Markdown parsed into lines of styled text (macOS <c>MarkdownText</c>), for a view that builds its text from
/// objects only: <c>#</c> headings, <c>-</c>/<c>*</c>/<c>+</c>/<c>•</c> bullets and <c>1.</c> items (indented by their
/// leading spaces), paragraphs, <c>**bold**</c>, <c>*italic*</c>, <c>`code`</c>, links and citations. Model output is
/// untrusted: there is no HTML (tags stay literal text), images show their alt text and are never fetched, and a link
/// is kept only when it is an http(s) URL without user info, a <c>[mm:ss]</c> citation (<see cref="SeekScheme"/>,
/// answers about one conversation) or an <c>echodiar-ref:</c> citation of one of the answer's references (library
/// answers); every other link (javascript:, file:, data:, relative, unknown ids…) shows its text only. Linear in the
/// input, so a streamed answer can be parsed again on every update.
/// </summary>
public static class MarkdownParser
{
    /// <summary>The scheme of transcript citation links (macOS <c>MarkdownText.citationScheme</c>).</summary>
    public const string SeekScheme = "echodiar-time";

    /// <summary>The longest moment a citation may seek to, as macOS (24 h).</summary>
    public const int MaxSeconds = 86_400;

    /// <summary>Nesting deeper than this is shown as plain text.</summary>
    private const int MaxDepth = 8;

    /// <summary>The longest <c>[…]</c> tried as a citation (<c>[999 1,000:00:00]</c> fits).</summary>
    private const int MaxCitationLength = 40;

    /// <summary>
    /// The lines of <paramref name="markdown"/>; with <paramref name="references"/> (a library answer) <c>[n mm:ss]</c> /
    /// <c>[n]</c> citations and <c>echodiar-ref:</c> links of those references are links, else (an answer about one
    /// conversation) <c>[mm:ss]</c> citations are.
    /// </summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string markdown, IReadOnlyList<ChatLog.Reference>? references = null)
    {
        var known = references?.Select(r => r.ConversationID).ToHashSet();
        var blocks = new List<MarkdownBlock>();
        foreach (var raw in markdown.Split(['\n', '\r', '\u2028', '\u2029', '\u0085']))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var indent = raw.TakeWhile(c => c is ' ' or '\t').Count() / 2;
            var hashes = line.TakeWhile(c => c == '#').Count();
            MarkdownBlock block;
            if (hashes is >= 1 and <= 6 && line.Length > hashes && line[hashes] == ' ')
                block = Block(MarkdownBlockKind.Heading, hashes, "", line[(hashes + 1)..]);
            else if (line.Length > 1 && "-*+•".Contains(line[0]) && line[1] == ' ')
                block = Block(MarkdownBlockKind.Bullet, indent, "", line[2..]);
            else if (NumberMarker(line) is { } marker)
                block = Block(MarkdownBlockKind.Numbered, indent, marker, line[(marker.Length + 1)..]);
            else
                block = Block(MarkdownBlockKind.Paragraph, 0, "", line);
            blocks.Add(block);
        }
        return blocks;

        MarkdownBlock Block(MarkdownBlockKind kind, int level, string marker, string text) =>
            new(kind, level, marker, new InlineParser(text, references, known).Parse());
    }

    /// <summary><paramref name="target"/> as a web link: an absolute http or https URL with a host and no user info.</summary>
    public static Uri? WebUrl(string target)
    {
        if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var url)) return null;
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return null;
        return url.Host.Length > 0 && url.UserInfo.Length == 0 ? url : null;
    }

    /// <summary>The link a target stands for in this answer; null when it stays plain text.</summary>
    internal static MarkdownLink? Classify(string target, IReadOnlyList<ChatLog.Reference>? references, HashSet<Guid>? known)
    {
        if (WebUrl(target) is { } url) return new MarkdownLink.Web(url);
        if (references is null && target.StartsWith(SeekScheme + ":", StringComparison.Ordinal))
        {
            return long.TryParse(target.AsSpan(SeekScheme.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                   && seconds <= MaxSeconds
                ? new MarkdownLink.Seek(seconds)
                : null;
        }
        if (known is not null && target.StartsWith(LibraryCitation.Scheme + ":", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(target, UriKind.Absolute, out var reference)
            && LibraryCitation.Parse(reference) is { } cited && known.Contains(cited.Id))
            return new MarkdownLink.Reference(cited.Id, cited.Seconds);
        return null;
    }

    /// <summary>"12." or "3)" when <paramref name="line"/> starts with a list number and a space.</summary>
    private static string? NumberMarker(string line)
    {
        var digits = line.TakeWhile(char.IsAsciiDigit).Count();
        if (digits is < 1 or > 9 || line.Length < digits + 2 || line[digits] is not ('.' or ')') || line[digits + 1] != ' ')
            return null;
        return line[..(digits + 1)];
    }

    /// <summary>
    /// One line's inline Markdown. Each search for a closing delimiter only moves forward, and a failed search is
    /// remembered (no closer after that point), so a line is read a bounded number of times per nesting level.
    /// </summary>
    private sealed class InlineParser(string text, IReadOnlyList<ChatLog.Reference>? references, HashSet<Guid>? known)
    {
        private readonly List<(StringBuilder Text, MarkdownStyle Style, MarkdownLink? Link)> _runs = [];

        /// <summary>The last search for <c>]</c>: from, up to (excluded), and where it was found (-1: not found).</summary>
        private int _bracketFrom = -1, _bracketEnd = -1, _bracketAt = -1;

        /// <summary>The last link target tried: its start, its limit, and its <c>)</c> (-1: not a target).</summary>
        private int _targetFrom = -1, _targetLimit = -1, _targetEnd = -1;

        public List<MarkdownInline> Parse()
        {
            Run(0, text.Length, MarkdownStyle.None, null, allowLinks: true, depth: 0);
            return _runs.Select(r => new MarkdownInline(r.Text.ToString(), r.Style, r.Link)).ToList();
        }

        /// <summary>Parses text[start..end) in <paramref name="style"/>, inside <paramref name="link"/> when set.</summary>
        private void Run(int start, int end, MarkdownStyle style, MarkdownLink? link, bool allowLinks, int depth)
        {
            if (depth >= MaxDepth)
            {
                Add(text.AsSpan(start, end - start), style, link);
                return;
            }
            // Per emphasis delimiter (*, **, _, __; and per backtick count): no closer at or after this index.
            Span<int> noCloser = [int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue];
            Dictionary<int, int>? noCode = null;
            var i = start;
            while (i < end)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < end && char.IsAscii(text[i + 1])
                    && (char.IsPunctuation(text[i + 1]) || char.IsSymbol(text[i + 1])))
                {
                    Add(text.AsSpan(i + 1, 1), style, link);
                    i += 2;
                    continue;
                }
                if (c == '`')
                {
                    var ticks = Count(i, end, '`');
                    var close = noCode is not null && noCode.TryGetValue(ticks, out var none) && i >= none
                        ? -1
                        : text.IndexOf(new string('`', ticks), i + ticks, end - i - ticks, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        noCode ??= [];
                        noCode.TryAdd(ticks, i);
                        Add(text.AsSpan(i, ticks), style, link);
                        i += ticks;
                        continue;
                    }
                    Add(text.AsSpan(i + ticks, close - i - ticks), style | MarkdownStyle.Code, link);
                    i = close + ticks;
                    continue;
                }
                if (c is '*' or '_')
                {
                    var twice = i + 1 < end && text[i + 1] == c;
                    var size = twice ? 2 : 1;
                    var kind = (c == '*' ? 0 : 2) + (twice ? 1 : 0);
                    var close = -1;
                    if (i < noCloser[kind] && Opens(i, size, end, c))
                    {
                        close = Closer(i + size, end, c, size);
                        if (close < 0) noCloser[kind] = i;
                    }
                    if (close < 0)
                    {
                        Add(text.AsSpan(i, size), style, link);
                        i += size;
                        continue;
                    }
                    Run(i + size, close, style | (twice ? MarkdownStyle.Bold : MarkdownStyle.Italic), link, allowLinks, depth + 1);
                    i = close + size;
                    continue;
                }
                if (c == '[' || (c == '!' && i + 1 < end && text[i + 1] == '['))
                {
                    var image = c == '!';
                    var open = image ? i + 1 : i;
                    if (Bracket(open + 1, end) is var close and >= 0)
                    {
                        if (close + 1 < end && text[close + 1] == '(' && Target(close + 2, end) is var targetEnd and >= 0)
                        {
                            if (image)
                            {
                                // The alt text only, as plain text; the image is never fetched.
                                Add(text.AsSpan(open + 1, close - open - 1), style, link);
                            }
                            else
                            {
                                var target = allowLinks ? Classify(text[(close + 2)..targetEnd], references, known) : null;
                                Run(open + 1, close, style, target ?? link, allowLinks: false, depth + 1);
                            }
                            i = targetEnd + 1;
                            continue;
                        }
                        if (!image && allowLinks && close - open + 1 <= MaxCitationLength
                            && Citation(text[open..(close + 1)]) is { } cited)
                        {
                            Add(text.AsSpan(open, close - open + 1), style, cited);
                            i = close + 1;
                            continue;
                        }
                    }
                    Add(text.AsSpan(i, 1), style, link);
                    i++;
                    continue;
                }
                var next = i + 1;
                while (next < end && text[next] is not ('\\' or '`' or '*' or '_' or '[' or '!')) next++;
                Add(text.AsSpan(i, next - i), style, link);
                i = next;
            }
        }

        /// <summary>The link of a bare <c>[mm:ss]</c> (or, in a library answer, <c>[n mm:ss]</c> / <c>[n]</c>) citation.</summary>
        private MarkdownLink? Citation(string candidate)
        {
            if (references is null)
            {
                return TranscriptCitation.Seconds(candidate) is { } seconds && seconds <= MaxSeconds
                    ? new MarkdownLink.Seek(seconds)
                    : null;
            }
            var linked = LibraryCitation.Linked(candidate, references);
            if (linked.Length <= candidate.Length + 2 || !linked.StartsWith(candidate, StringComparison.Ordinal)) return null;
            return Classify(linked[(candidate.Length + 1)..^1], references, known);
        }

        /// <summary>
        /// Whether a <paramref name="size"/>-long run of <paramref name="mark"/> at <paramref name="at"/> can open
        /// emphasis: followed by a non-space, and for <c>_</c> not inside a word.
        /// </summary>
        private bool Opens(int at, int size, int end, char mark)
        {
            if (at + size >= end || char.IsWhiteSpace(text[at + size])) return false;
            return mark == '*' || at == 0 || !char.IsLetterOrDigit(text[at - 1]);
        }

        /// <summary>
        /// The next run of <paramref name="mark"/> in (from, end) that closes emphasis of <paramref name="size"/>:
        /// preceded by a non-space, for <c>_</c> not followed by a letter or digit; a single mark skips longer runs, and
        /// a longer run closes with its last <paramref name="size"/> marks (<c>***a***</c>). -1 when there is none.
        /// </summary>
        private int Closer(int from, int end, char mark, int size)
        {
            var at = from;
            while (at < end)
            {
                var found = text.IndexOf(mark, at, end - at);
                if (found < 0) return -1;
                var run = Count(found, end, mark);
                var last = found + run - size;
                if (found > from && !char.IsWhiteSpace(text[found - 1]) && (size == 2 ? run >= 2 : run == 1)
                    && (mark == '*' || last + size >= end || !char.IsLetterOrDigit(text[last + size])))
                    return last;
                at = found + run;
            }
            return -1;
        }

        /// <summary>The first <c>]</c> in [from, end), or -1; reuses the last search when it covers this one.</summary>
        private int Bracket(int from, int end)
        {
            if (_bracketFrom >= 0 && _bracketFrom <= from)
            {
                if (_bracketAt >= from) return _bracketAt < end ? _bracketAt : -1;
                if (_bracketAt < 0 && end <= _bracketEnd) return -1;
            }
            _bracketFrom = from;
            _bracketEnd = end;
            _bracketAt = text.IndexOf(']', from, end - from);
            return _bracketAt;
        }

        /// <summary>
        /// The <c>)</c> ending a link target that starts at <paramref name="from"/>, or -1: a target has no spaces,
        /// brackets or quotes, so scans from different starts never overlap.
        /// </summary>
        private int Target(int from, int end)
        {
            if (_targetFrom == from && _targetLimit == end) return _targetEnd;
            _targetFrom = from;
            _targetLimit = end;
            _targetEnd = -1;
            for (var at = from; at < end; at++)
            {
                var c = text[at];
                if (c == ')')
                {
                    _targetEnd = at;
                    break;
                }
                if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '(' or '[' or ']' or '<' or '>' or '"' or '`') break;
            }
            return _targetEnd;
        }

        private int Count(int from, int end, char mark)
        {
            var at = from;
            while (at < end && text[at] == mark) at++;
            return at - from;
        }

        /// <summary>Appends text, to the previous inline when it has the same style and link.</summary>
        private void Add(ReadOnlySpan<char> value, MarkdownStyle style, MarkdownLink? link)
        {
            if (value.IsEmpty) return;
            if (_runs.Count > 0 && _runs[^1].Style == style && ReferenceEquals(_runs[^1].Link, link))
                _runs[^1].Text.Append(value);
            else
                _runs.Add((new StringBuilder().Append(value), style, link));
        }
    }
}
