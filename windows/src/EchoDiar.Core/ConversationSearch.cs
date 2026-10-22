using EchoDiar.Core.Model;

namespace EchoDiar.Core;

/// <summary>Swift <c>ConversationSearch</c>: full-text search over a conversation: title, speakers, transcript, summary and chat.</summary>
public static class ConversationSearch
{
    /// <summary>The searchable text of one conversation, one string per field.</summary>
    /// <param name="Speakers">Display and default names of every speaker, in first-appearance order.</param>
    public sealed record Document(string Title, IReadOnlyList<string> Speakers, string Transcript, string Summary, string Chat)
    {
        /// <summary>
        /// True when <paramref name="term"/> is blank, or some field contains it ignoring case and accents (Swift
        /// <c>localizedStandardContains</c>). The whole trimmed term is one substring, as in Swift.
        /// </summary>
        public bool Matches(string term)
        {
            term = term.Trim();
            if (term.Length == 0) return true;
            var key = SearchText.Fold(term);
            return new[] { Title, Transcript, Summary, Chat }.Concat(Speakers).Any(field => SearchText.ContainsFolded(field, key));
        }
    }

    /// <summary>Builds the document of one conversation; a missing transcript, summary or chat yields empty fields.</summary>
    public static Document Build(string title, Transcript? transcript, string? summary, ChatLog? chat)
    {
        var segments = transcript?.Segments ?? [];
        var speakers = new List<string>();
        if (transcript is not null)
        {
            foreach (var segment in segments)
            {
                foreach (var name in new[] { transcript.DisplayName(segment.Speaker), Transcript.DefaultName(segment.Speaker) })
                {
                    if (!speakers.Contains(name)) speakers.Add(name);
                }
            }
        }
        return new Document(
            Title: title,
            Speakers: speakers,
            Transcript: string.Join('\n', segments.Select(s => s.Text)),
            Summary: summary ?? "",
            Chat: string.Join('\n', (chat?.Messages ?? []).Select(m => m.Text)));
    }
}
