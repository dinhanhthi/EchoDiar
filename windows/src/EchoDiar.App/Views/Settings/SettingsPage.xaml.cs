using System.ComponentModel;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EchoDiar.App.Views;

/// <summary>A search result row: the matching setting and its section's icon.</summary>
public sealed record SettingsResultRow(SettingsSearchItem Item, string Glyph)
{
    public string Path => Item.Path;
}

/// <summary>
/// The Settings route (macOS SettingsWindow.swift <c>SettingsView</c> and SettingsSearch.swift
/// <c>SettingsSearchResults</c>): the sidebar of sections, the selected one's page, and the results of the toolbar's
/// search. <see cref="AppViewModel.SettingsSection"/> is the selection, so <see cref="AppViewModel.OpenSettings"/>
/// deep-links. Picking a section clears the search.
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    private readonly AppViewModel _model;

    /// <summary>Sections shown so far: kept while the page lives, like the macOS sections (their scans run once).</summary>
    private readonly Dictionary<string, FrameworkElement> _pages = [];

    private readonly Dictionary<string, NavigationViewItem> _items = [];

    /// <summary>The selection is being set from the model: not a pick.</summary>
    private bool _syncing;

    /// <summary>A pick or a result is being applied: the page updates once, after it.</summary>
    private bool _opening;

    /// <summary>The term the results were listed for.</summary>
    private string? _listedTerm;

    public SettingsPage(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        foreach (var section in SettingsSearch.Sections)
        {
            var item = new NavigationViewItem
            {
                Content = Loc.T(section.Title),
                Icon = new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], Glyph = section.Glyph },
                Tag = section.Id,
            };
            _items[section.Id] = item;
            Nav.MenuItems.Add(item);
        }
        Nav.SelectionChanged += OnSelectionChanged;
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            Update();
            // macOS focuses the sidebar when Settings opens.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_model.IsClosing && Nav.SelectedItem is Control selected) selected.Focus(FocusState.Programmatic);
            });
        };
        Unloaded += (_, _) => _model.PropertyChanged -= OnModelChanged;
    }

    /// <summary>The page of a section 14.2 and later phases have not built yet: its title only.</summary>
    public static Func<AppViewModel, FrameworkElement> Placeholder(string title) => _ => new TextBlock
    {
        Text = Loc.T(title),
        Margin = new Thickness(24, 16, 24, 24),
        Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
    };

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_opening) return;
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.SearchTerm):
            case nameof(AppViewModel.SettingsSection):
                Update();
                break;
        }
    }

    /// <summary>The search's sections and results while a term is searched, else every section and the selected one's page.</summary>
    private void Update()
    {
        var term = _model.SearchTerm;
        var searching = term.Length > 0;
        var hits = searching ? SettingsSearch.SectionsWithHits(term).Select(s => s.Id).ToHashSet() : null;
        foreach (var (id, item) in _items)
        {
            item.Visibility = hits is null || hits.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
        }
        _syncing = true;
        try
        {
            Nav.SelectedItem = searching ? null : _items.GetValueOrDefault(_model.SettingsSection);
        }
        finally
        {
            _syncing = false;
        }
        SectionHost.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        ResultsPanel.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        if (searching) ShowResults(term);
        else ShowSection(_model.SettingsSection);
    }

    private void ShowSection(string id)
    {
        _listedTerm = null;
        if (SettingsSearch.Section(id) is not { } section) return;
        if (!_pages.TryGetValue(id, out var page))
        {
            page = section.CreatePage(_model);
            _pages[id] = page;
        }
        if (!ReferenceEquals(SectionHost.Content, page)) SectionHost.Content = page;
    }

    private void ShowResults(string term)
    {
        if (term == _listedTerm) return;
        _listedTerm = term;
        var rows = SettingsSearch.Matching(term)
            .Select(item => new SettingsResultRow(item, SettingsSearch.Section(item.Section)?.Glyph ?? ""))
            .ToList();
        ResultsList.ItemsSource = rows;
        ResultsScroller.ChangeView(null, 0, null, disableAnimation: true);
        ResultsScroller.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResultsText.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        NoResultsText.Text = Loc.Format("No settings match \"%@\"", term);
    }

    /// <summary>A picked section shows, and the search is cleared (macOS: a sidebar pick while searching ends it).</summary>
    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncing || args.SelectedItem is not NavigationViewItem { Tag: string id }) return;
        Open(id, null);
    }

    /// <summary>A result opens its section on its inner tab and clears the search.</summary>
    private void OnResultClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SettingsResultRow row) return;
        Open(row.Item.Section, row.Item.SubTab);
    }

    /// <summary>
    /// Clears the search and opens the section, then updates once: updating between the two would select the old
    /// section, or none, while the sidebar is applying the pick.
    /// </summary>
    private void Open(string section, string? subTab)
    {
        _opening = true;
        try
        {
            _model.SearchTerm = "";
            _model.OpenSettings(section, subTab);
        }
        finally
        {
            _opening = false;
        }
        Update();
    }
}
