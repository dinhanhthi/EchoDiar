using System.Globalization;
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
            var key = KeepStrokedD(term);
            return new[] { Title, Transcript, Summary, Chat }.Concat(Speakers).Any(field => Contains(field, key));
        }

        private static bool Contains(string text, string key) =>
            CultureInfo.InvariantCulture.CompareInfo.IndexOf(KeepStrokedD(text), key, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

        /// <summary>
        /// ICU's <c>IgnoreNonSpace</c> folds "đ"/"Đ" into "d", but it is a letter of its own in Vietnamese and Swift keeps it
        /// apart ("da nang" does not find "Đà Nẵng"; "đà nẵng" does). Both cases become one private-use character, which never
        /// occurs in titles, transcripts, summaries or chats, so they match each other and nothing else.
        /// </summary>
        private static string KeepStrokedD(string s) => s.Replace('đ', '').Replace('Đ', '');
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
