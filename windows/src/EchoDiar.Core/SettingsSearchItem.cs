namespace EchoDiar.Core;

/// <summary>
/// Swift <c>SettingsSearchItem</c>: a Settings row or group the search can find: its label as shown in the form, words
/// users may type instead, and where it lives (the sidebar section and, for sections with inner tabs, the id of that
/// tab), with the titles of both as shown.
/// </summary>
/// <param name="Section">The sidebar section's id.</param>
/// <param name="SubTab">The id of the section's inner tab, or null for a section without tabs.</param>
public sealed record SettingsSearchItem(
    string Title,
    IReadOnlyList<string> Keywords,
    string Section,
    string SectionTitle,
    string? SubTab = null,
    string? SubTabTitle = null)
{
    public string Id => $"{Section}/{SubTab ?? "-"}/{Title}";

    /// <summary>
    /// True when <paramref name="term"/> (trimmed, not empty) is in the title, a keyword, the section title or the inner
    /// tab title, ignoring case and accents. A blank term matches nothing (unlike the conversation search).
    /// </summary>
    public bool Matches(string term)
    {
        term = term.Trim();
        if (term.Length == 0) return false;
        var key = SearchText.Fold(term);
        IEnumerable<string> texts = SubTabTitle is { } subTab ? [Title, SectionTitle, subTab] : [Title, SectionTitle];
        return texts.Concat(Keywords).Any(text => SearchText.ContainsFolded(text, key));
    }

    /// <summary>"Transcription › General › Default language", with the inner tab in between when it is not the title itself.</summary>
    public string Path
    {
        get
        {
            var subTab = SubTabTitle == Title ? null : SubTabTitle;
            return string.Join(" › ", new[] { SectionTitle, subTab, Title }.OfType<string>());
        }
    }
}
