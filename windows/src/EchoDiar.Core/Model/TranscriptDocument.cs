using System.Globalization;
using System.Text;

namespace EchoDiar.Core.Model;

/// <summary>
/// Swift <c>TranscriptDocument</c>: a transcript as one selectable text. Per segment a header "mm:ss  ● Name", a line
/// separator (U+2028, so header and body stay one paragraph), then the body (the segment text, trimmed). Segments are
/// joined by "\n"; the last one has no trailing newline. Ranges are string indices (UTF-16 code units), as on macOS.
/// </summary>
public sealed class TranscriptDocument
{
    /// <summary>A span of <see cref="Text"/>, like <c>NSRange</c>.</summary>
    public readonly record struct TextRange(int Start, int Length)
    {
        public int End => Start + Length;
    }

    /// <summary>One segment's place in <see cref="Text"/>.</summary>
    /// <param name="Index">Index in the segments the document was built from.</param>
    /// <param name="Start">Seconds from the start of the audio file.</param>
    /// <param name="Name">The display name the header shows and the copy uses.</param>
    /// <param name="Time"><see cref="Timestamp"/> of <paramref name="Start"/>, as in the header and the copy.</param>
    /// <param name="HeaderRange">"mm:ss  ● Name", without the line separator.</param>
    /// <param name="BodyRange">Empty (after the line separator) when the segment has no text.</param>
    public sealed record Entry(
        int Index, Guid Id, double Start, string Name, string Time,
        TextRange HeaderRange, TextRange TimeRange, TextRange NameRange, TextRange BodyRange);

    public string Text { get; }
    public IReadOnlyList<Entry> Entries { get; }

    /// <param name="names">Each segment's display name (the renamed speaker name); called once per segment.</param>
    public TranscriptDocument(IReadOnlyList<Transcript.Segment> segments, Func<Transcript.Segment, string> names)
    {
        var text = new StringBuilder();
        var entries = new List<Entry>(segments.Count);
        TextRange Append(string piece)
        {
            var range = new TextRange(text.Length, piece.Length);
            text.Append(piece);
            return range;
        }
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (index > 0) Append("\n");
            string name = names(segment), time = Timestamp(segment.Start);
            var timeRange = Append(time);
            Append("  ● ");
            var nameRange = Append(name);
            Append("\u2028");
            var bodyRange = Append(segment.Text.Trim());
            entries.Add(new Entry(index, segment.Id, segment.Start, name, time,
                new TextRange(timeRange.Start, nameRange.End - timeRange.Start), timeRange, nameRange, bodyRange));
        }
        Text = text.ToString();
        Entries = entries;
    }

    /// <summary>
    /// The entry whose span, from its header start to its body end (both included, so a caret right after the body
    /// still belongs to it), holds <paramref name="offset"/>; null past the end or before the start.
    /// </summary>
    public Entry? SegmentAt(int offset)
    {
        // The last entry starting at or before the offset.
        int low = 0, high = Entries.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (Entries[mid].HeaderRange.Start <= offset) low = mid + 1;
            else high = mid;
        }
        return low > 0 && offset <= Entries[low - 1].BodyRange.End ? Entries[low - 1] : null;
    }

    /// <summary>
    /// The clipboard text for a selection: one "Name [mm:ss]: part" line per segment whose body the selection
    /// touches, the part being the selected piece of the body, joined by "\n". A header selected alone adds no line.
    /// When the selection touches no body at all (only headers), the selected text as it is.
    /// </summary>
    public string CopyText(TextRange range)
    {
        var lines = new List<string>();
        foreach (var entry in Entries)
        {
            var start = Math.Max(range.Start, entry.BodyRange.Start);
            var end = Math.Min(range.End, entry.BodyRange.End);
            if (end > start) lines.Add($"{entry.Name} [{entry.Time}]: {Text[start..end]}");
        }
        if (lines.Count > 0) return string.Join("\n", lines);
        int from = Math.Clamp(range.Start, 0, Text.Length), to = Math.Clamp(range.End, from, Text.Length);
        return Text[from..to];
    }

    /// <summary>
    /// mm:ss; minutes go past 59 in recordings longer than an hour. Same format as the transcript view and the AI
    /// prompts (<c>PromptBuilder.Timestamp</c>): seconds rounded half to even, as Swift's <c>Duration</c> format does.
    /// </summary>
    public static string Timestamp(double seconds)
    {
        var whole = (long)Math.Round(Math.Max(seconds, 0), MidpointRounding.ToEven);
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 60:00}:{whole % 60:00}");
    }
}
