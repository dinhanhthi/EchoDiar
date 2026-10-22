using System.Globalization;

namespace EchoDiar.Core;

/// <summary>
/// Swift <c>localizedStandardContains</c>: the text search of the conversations and of Settings, ignoring case and
/// accents.
/// </summary>
public static class SearchText
{
    /// <summary>True when <paramref name="text"/> contains <paramref name="term"/> ignoring case and accents.</summary>
    public static bool Contains(string text, string term) => ContainsFolded(text, Fold(term));

    /// <summary><see cref="Contains"/> with a term already passed through <see cref="Fold"/>.</summary>
    internal static bool ContainsFolded(string text, string foldedTerm) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(Fold(text), foldedTerm, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    /// <summary>
    /// ICU's <c>IgnoreNonSpace</c> folds "đ"/"Đ" into "d", but it is a letter of its own in Vietnamese and Swift keeps it
    /// apart ("da nang" does not find "Đà Nẵng"; "đà nẵng" does). Both cases become one private-use character, which never
    /// occurs in titles, transcripts, summaries, chats or settings, so they match each other and nothing else.
    /// </summary>
    internal static string Fold(string s) => s.Replace('đ', '').Replace('Đ', '');
}
