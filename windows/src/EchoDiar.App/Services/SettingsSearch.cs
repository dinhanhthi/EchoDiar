using EchoDiar.App.ViewModels;
using EchoDiar.App.Views;
using EchoDiar.Core;
using Microsoft.UI.Xaml;

namespace EchoDiar.App.Services;

/// <summary>An inner tab of a Settings section (macOS <c>SettingsSubTab</c>): the id deep links use, and its macOS title.</summary>
public sealed record SettingsSubTab(string Id, string Title);

/// <summary>
/// A Settings row or group the search can find (macOS <c>SettingsSearchItem</c>): its label as the page shows it (macOS
/// text), the words users may type instead, and the inner tab it is on (null for a section without tabs).
/// </summary>
public sealed record SettingsEntry(string Title, IReadOnlyList<string> Keywords, string? SubTab = null);

/// <summary>
/// A sidebar section of Settings (macOS <c>SettingsTab</c>): its id, its macOS title, its Segoe Fluent Icons glyph, its
/// inner tabs (shown in the title bar after Record; none for most), its searchable rows, and its page, made the first
/// time the section is shown on the Settings page.
/// </summary>
public sealed record SettingsSection(
    string Id,
    string Title,
    string Glyph,
    IReadOnlyList<SettingsSubTab> SubTabs,
    IReadOnlyList<SettingsEntry> Entries,
    Func<AppViewModel, FrameworkElement> CreatePage)
{
    /// <summary>The shown title of the inner tab <paramref name="id"/>, or null.</summary>
    public string? SubTabTitle(string? id) => SubTabs.FirstOrDefault(t => t.Id == id) is { } tab ? Loc.T(tab.Title) : null;
}

/// <summary>
/// The Settings sections in sidebar order and their searchable rows (macOS SettingsWindow.swift <c>SettingsTab</c> and
/// SettingsSearch.swift <c>SettingsSearchItem.all</c>). Adding a section: a <see cref="SettingsSection"/> in
/// <see cref="Sections"/> where macOS lists it, with its page factory and its entries (a page keeps its entries next to
/// it, e.g. a static <c>SearchEntries</c>, and the section points at them). The list is static so the search finds rows
/// of sections not opened yet.
/// </summary>
public static class SettingsSearch
{
    public const string General = "general";
    public const string About = "about";
    public const string Acknowledgements = "acknowledgements";

    /// <summary>Every section, in sidebar order (macOS: General, Recording, Transcription, AI, RAG, Storage, Shortcuts, About, Acknowledgements).</summary>
    public static IReadOnlyList<SettingsSection> Sections { get; } =
    [
        // Default language and Playback are in General on Windows until the Transcription and Storage sections exist.
        new(General, "General", "\uE713", [], GeneralSettingsPage.SearchEntries, model => new GeneralSettingsPage(model)),
        new(About, "About", "\uE946", [], AboutSettingsPage.SearchEntries, model => new AboutSettingsPage(model)),
        new(Acknowledgements, "Acknowledgements", "\uE8A5", [], AcknowledgementsSettingsPage.SearchEntries, _ => new AcknowledgementsSettingsPage()),
    ];

    private static readonly Lazy<IReadOnlyList<SettingsSearchItem>> AllItems = new(() =>
    [
        .. Sections.SelectMany(section => section.Entries.Select(entry => new SettingsSearchItem(
            Loc.T(entry.Title), entry.Keywords, section.Id, Loc.T(section.Title), entry.SubTab, section.SubTabTitle(entry.SubTab)))),
    ]);

    /// <summary>Every searchable row, in sidebar order and each section's order.</summary>
    public static IReadOnlyList<SettingsSearchItem> Items => AllItems.Value;

    public static SettingsSection? Section(string id) => Sections.FirstOrDefault(s => s.Id == id);

    /// <summary>The rows matching <paramref name="term"/>; none for a blank term.</summary>
    public static IReadOnlyList<SettingsSearchItem> Matching(string term) => [.. Items.Where(i => i.Matches(term))];

    /// <summary>The sections with a row matching <paramref name="term"/>, in sidebar order.</summary>
    public static IReadOnlyList<SettingsSection> SectionsWithHits(string term)
    {
        var hits = Matching(term).Select(i => i.Section).ToHashSet();
        return [.. Sections.Where(s => hits.Contains(s.Id))];
    }
}
