using System.ComponentModel;
using CommunityToolkit.WinUI.Controls;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EchoDiar.App.Views;

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

    /// <summary>How long a row a search result opened stays outlined.</summary>
    private static readonly TimeSpan HighlightDuration = TimeSpan.FromMilliseconds(1500);

    private DispatcherQueueTimer? _highlightTimer;

    /// <summary>The row outlined now.</summary>
    private Control? _highlighted;

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
        var items = SettingsSearch.Matching(term).ToList();
        ResultsList.Children.Clear();
        foreach (var item in items) ResultsList.Children.Add(ResultRow(item));
        ResultsScroller.ChangeView(null, 0, null, disableAnimation: true);
        ResultsScroller.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResultsText.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        NoResultsText.Text = Loc.Format("No settings match \"%@\"", term);
    }

    /// <summary>A picked section shows, and the search is cleared (macOS: a sidebar pick while searching ends it).</summary>
    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncing || args.SelectedItem is not NavigationViewItem { Tag: string id }) return;
        Open(id, null);
    }

    /// <summary>
    /// A result's row, "Section › Tab › Row" with its section's icon. Made in code with its item: an ItemsRepeater does
    /// not set the DataContext of an x:Bind template's element, so a click could not tell which item it was.
    /// </summary>
    private SettingsCard ResultRow(SettingsSearchItem item)
    {
        var card = new SettingsCard
        {
            Header = item.Path,
            HeaderIcon = new FontIcon
            {
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                Glyph = SettingsSearch.Section(item.Section)?.Glyph ?? "",
            },
            IsClickEnabled = true,
        };
        card.Click += (_, _) => OpenResult(item);
        return card;
    }

    /// <summary>A result opens its section on its inner tab, clears the search and points at its row.</summary>
    private void OpenResult(SettingsSearchItem item)
    {
        StartupTrace.Write($"settings result opened {item.Section}/{item.Title}");
        Open(item.Section, item.SubTab);
        if (SectionHost.Content is FrameworkElement page) ShowRow(page, item.Title);
    }

    /// <summary>
    /// Scrolls <paramref name="page"/> to the row titled <paramref name="title"/> (an element whose Tag is its entry's
    /// title) and outlines it in the accent colour for a moment. A page made just now is laid out first.
    /// </summary>
    private void ShowRow(FrameworkElement page, string title)
    {
        if (page.IsLoaded)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => HighlightRow(page, title));
            return;
        }
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            page.Loaded -= OnLoaded;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => HighlightRow(page, title));
        }
        page.Loaded += OnLoaded;
    }

    private void HighlightRow(FrameworkElement page, string title)
    {
        if (_model.IsClosing || FindRow(page, title) is not { } row) return;
        row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.3 });
        if (row is not Control control || !Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var accent)) return;
        _highlightTimer?.Stop();
        ClearHighlight();
        control.BorderBrush = (Brush)accent;
        control.BorderThickness = new Thickness(2);
        _highlighted = control;
        _highlightTimer ??= CreateHighlightTimer();
        _highlightTimer.Start();
    }

    private DispatcherQueueTimer CreateHighlightTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = HighlightDuration;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => ClearHighlight();
        return timer;
    }

    /// <summary>The highlighted row takes its style's border again.</summary>
    private void ClearHighlight()
    {
        _highlighted?.ClearValue(Control.BorderBrushProperty);
        _highlighted?.ClearValue(Control.BorderThicknessProperty);
        _highlighted = null;
    }

    /// <summary>The element under <paramref name="root"/> whose Tag is the source text of <paramref name="title"/>.</summary>
    private static FrameworkElement? FindRow(DependencyObject root, string title)
    {
        if (root is FrameworkElement { Tag: string tag } element && Loc.T(tag) == title) return element;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindRow(VisualTreeHelper.GetChild(root, i), title) is { } found) return found;
        }
        return null;
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
