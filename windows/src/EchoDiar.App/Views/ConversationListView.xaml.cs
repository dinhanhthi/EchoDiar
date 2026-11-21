using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Transcribe;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;

namespace EchoDiar.App.Views;

/// <summary>
/// The library (macOS ConversationListView.swift and MainView.swift libraryPane): the search box, then the current
/// page's conversation cards under section headers (Today, Previous 3 days, Last 7 days, Last 30 days, Older), filtered
/// by the search, with the model download banner (MVP) and the page bar under the cards while there is more than one
/// page. While recording, the recording's card sits above them on every page and with a search, outside the pages and
/// their count, and the recording's own entry is left out. Ctrl-click and Shift-click select cards (from the open
/// entry in the sidebar, as in Mail); the selection bar then shows above the
/// list, which moves down, with Delete and Cancel (Esc, the window's: <see cref="MainWindow"/>). Delete deletes the selection (the
/// window's key too), else the focused card, else the open conversation (the window's key); when the open conversation goes,
/// the next card opens. The ListView itself never selects: the cards draw the open conversation and the selection. The
/// window's ↑/↓ open the previous or next card (<see cref="Move"/>), before the ListView moves the focus itself.
/// </summary>
public sealed partial class ConversationListView : UserControl
{
    /// <summary>The cards are filtered once typing pauses this long (at once when cleared).</summary>
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(200);

    private readonly AppViewModel _model;
    private readonly DispatcherQueueTimer _searchTimer;

    /// <summary>
    /// Section headers and cards, in list order, each in its own ListViewItem (so a card is never moved between item
    /// containers). Cards are kept by id and headers by section, so updates keep them. Each is kept with its content, never
    /// read back from the item: WinUI clears a list item's Content when it clears it as the container of another item
    /// (Selector.ClearContainerForItemOverride, for a recycled item whose link to itself was already undone), so it is
    /// put back when that happened.
    /// </summary>
    private readonly ObservableCollection<ListViewItem> _items = [];
    private readonly Dictionary<Guid, (ListViewItem Item, ConversationCard Card)> _cards = [];
    private readonly Dictionary<LibraryBucket, (ListViewItem Item, TextBlock Text)> _headers = [];

    private readonly ListSelection _selection = new();
    private readonly RecordingCard _card;

    /// <summary>The listed conversations, in list order.</summary>
    private IReadOnlyList<Conversation> _shown = [];

    /// <summary>The recording state the rows were last listed for.</summary>
    private bool _listedWhileRecording;

    /// <summary>The page (clamped) the rows were last listed for: another one clears the selection and scrolls to the top.</summary>
    private int _listedPage;

    /// <summary>The sidebar's selected card: the shown conversation.</summary>
    private Guid? _selectedId;

    /// <summary>The ids of the banner's models, in the order of their names in <see cref="ModelPicker"/>.</summary>
    private List<string> _pickerIds = [];

    public ConversationListView(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        ConversationList.ItemsSource = _items;
        // The rows' ListViewItem padding, so the card lines up with them.
        _card = new RecordingCard(model) { Margin = new Thickness(16, 0, 12, 0), Visibility = Visibility.Collapsed };
        ConversationList.Header = _card;
        SelectionDeleteMenu.Opening += (_, _) => UpdateSelectionDeleteMenu();
        SelectionDeleteAudioItem.Click += (_, _) => _ = DeleteSelectionAsync(audioOnly: true);
        SelectionDeleteConversationsItem.Click += (_, _) => _ = DeleteSelectionAsync(audioOnly: false);
        SelectionCancelButton.Click += (_, _) => ClearSelection();
        PreviousPageButton.Click += (_, _) => StepPage(-1);
        NextPageButton.Click += (_, _) => StepPage(1);
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = SearchDelay;
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (timer, _) =>
        {
            timer.Stop();
            _model.SearchTerm = SearchBox.Text;
        };
        SearchBox.TextChanged += (_, _) => OnSearchTextChanged();
        // The inner text field handles Esc first.
        SearchBox.AddHandler(KeyDownEvent, new KeyEventHandler(OnSearchKeyDown), handledEventsToo: true);
        // Also the keys a focused item or the list already handled (Space, Enter).
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        Loaded += (_, _) =>
        {
            StartupTrace.Write("library view Loaded");
            _model.PropertyChanged += OnModelChanged;
            // The search may have been cleared while the library chat was shown in this view's place.
            OnSearchTermChanged();
            UpdateRows();
            UpdateBanner();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            // Typing not applied yet is dropped: a later tick would bring back a search the model has cleared since.
            _searchTimer.Stop();
        };
    }

    /// <summary>
    /// The window's delete flow (<see cref="TitleBarToolbar.DeleteAsync"/>: eligibility, the confirmation, the delete,
    /// the conversation that opens in place of the open one when it goes); true when something was deleted.
    /// </summary>
    internal Func<IReadOnlyList<Conversation>, bool, Guid?, Task<bool>>? Delete { get; set; }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.SearchTerm):
                OnSearchTermChanged();
                break;
            case nameof(AppViewModel.Conversations):
            case nameof(AppViewModel.SearchResults):
            case nameof(AppViewModel.LibraryPage):
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.TranscriptionErrors):
                UpdateRows();
                break;
            case nameof(AppViewModel.AIJobs):
                // A job's start, first text and end: only the cards' activity changes, never the rows.
                UpdateCards();
                break;
            case nameof(AppViewModel.RecordingSnapshot):
                // At 15 Hz while recording: only the start and the end change the list.
                if (_model.IsRecording != _listedWhileRecording) UpdateRows();
                break;
            case nameof(AppViewModel.InstalledModelIds):
            case nameof(AppViewModel.Settings):
            case nameof(AppViewModel.Downloads):
            case nameof(AppViewModel.DownloadErrors):
                UpdateBanner();
                break;
        }
    }

    /// <summary>
    /// The current page of the search results by section; moves, adds and removes only the items that changed, so the
    /// list keeps its scroll position, and updates every card in place. Another page starts at the top, without a
    /// selection. Then the empty state, the page bar, and the selection without the cards that left.
    /// </summary>
    private void UpdateRows()
    {
        var recording = _model.IsRecording;
        _listedWhileRecording = recording;
        _card.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        // The recording's own entry is left out (AppViewModel.Listed): its card is the recording card.
        var page = _model.CurrentLibraryPage();
        var sections = page.Sections;
        var pageChanged = page.Page != _listedPage;
        _listedPage = page.Page;

        var wanted = new List<ListViewItem>();
        var shown = new List<Conversation>();
        foreach (var section in sections)
        {
            wanted.Add(Header(section.Bucket));
            foreach (var conversation in section.Conversations)
            {
                if (!_cards.TryGetValue(conversation.Id, out var row))
                {
                    var card = new ConversationCard(_model, this, conversation);
                    row = (new ListViewItem
                    {
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        MinHeight = 0,
                        Content = card,
                    }, card);
                    _cards[conversation.Id] = row;
                }
                wanted.Add(row.Item);
                shown.Add(conversation);
            }
        }
        _shown = shown;
        Sync(wanted);
        // At once, not enqueued: a conversation opened from outside the list changes the page first, then the window
        // scrolls its card into view (ShowAsSidebar).
        if (pageChanged) ScrollToTop();

        // Cards of deleted conversations, headers of sections no longer listed.
        var ids = _model.Conversations.Select(c => c.Id).ToHashSet();
        foreach (var id in _cards.Keys.Where(id => !ids.Contains(id)).ToList()) _cards.Remove(id);
        var buckets = sections.Select(s => s.Bucket).ToHashSet();
        foreach (var bucket in _headers.Keys.Where(bucket => !buckets.Contains(bucket)).ToList()) _headers.Remove(bucket);

        // Cards that left the list (search filter, deletion) are not selected; nor any on another page.
        if (pageChanged) _selection.Clear();
        else _selection.Prune(shown.Select(c => c.Id).ToList());
        UpdateCards();
        UpdateEmptyState(sections.Count == 0, recording, _model.Conversations.Any(_model.Listed));
        UpdatePageBar(page);
        StartupTrace.Write($"library rows updated ({shown.Count} rows)");
    }

    /// <summary>The list's own scroller at the top, with the recording's card (the list's header).</summary>
    private void ScrollToTop()
    {
        if (Scroller(ConversationList) is { } scroller) scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>The first ScrollViewer under <paramref name="parent"/>; none before the list's template is applied.</summary>
    private static ScrollViewer? Scroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroller) return scroller;
            if (Scroller(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// macOS pageBar: "1–100 of 742" with the arrows, disabled at the first and the last page; hidden with one page.
    /// </summary>
    private void UpdatePageBar(LibraryPageContent page)
    {
        var paged = page.PageCount > 1;
        PageBar.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
        if (!paged) return;
        PreviousPageButton.IsEnabled = page.Page > 0;
        NextPageButton.IsEnabled = page.Page < page.PageCount - 1;
        RecordingCard.SetText(PageText, Loc.Format("%lld–%lld of %lld", page.Start + 1, page.Start + page.Count, page.Total));
    }

    /// <summary>The page bar's arrows: from the shown page, as the stored one may be past the last.</summary>
    private void StepPage(int delta) => _model.LibraryPage = _model.CurrentLibraryPage().Page + delta;

    /// <summary>Makes <see cref="_items"/> equal to <paramref name="wanted"/> (same instances, same order) with few changes.</summary>
    private void Sync(List<ListViewItem> wanted)
    {
        var keep = new HashSet<ListViewItem>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(_items[i])) _items.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < _items.Count && ReferenceEquals(_items[i], wanted[i])) continue;
            var from = -1;
            for (var j = i + 1; j < _items.Count; j++)
            {
                if (ReferenceEquals(_items[j], wanted[i]))
                {
                    from = j;
                    break;
                }
            }
            if (from >= 0) _items.Move(from, i);
            else _items.Insert(i, wanted[i]);
        }
    }

    /// <summary>The section's title, as muted left-aligned text; not focusable.</summary>
    private ListViewItem Header(LibraryBucket bucket)
    {
        if (!_headers.TryGetValue(bucket, out var header))
        {
            var text = new TextBlock { Style = (Style)Resources["DayHeaderText"] };
            header = (new ListViewItem
            {
                IsTabStop = false,
                IsHitTestVisible = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 0,
                Content = text,
            }, text);
            AutomationProperties.SetHeadingLevel(header.Item, AutomationHeadingLevel.Level2);
            _headers[bucket] = header;
        }
        if (!ReferenceEquals(header.Item.Content, header.Text)) header.Item.Content = header.Text;
        RecordingCard.SetText(header.Text, BucketTitle(bucket));
        return header.Item;
    }

    private static string BucketTitle(LibraryBucket bucket) => bucket switch
    {
        LibraryBucket.Today => Loc.T("Today"),
        LibraryBucket.Previous3Days => Loc.T("Previous 3 days"),
        LibraryBucket.Last7Days => Loc.T("Last 7 days"),
        LibraryBucket.Last30Days => Loc.T("Last 30 days"),
        _ => Loc.T("Older"),
    };

    /// <summary>macOS .long without a time: "October 22, 2026"; Windows' long date also names the weekday.</summary>
    internal static string LongDate(DateTime day)
    {
        var pattern = CultureInfo.CurrentCulture.DateTimeFormat.LongDatePattern;
        foreach (var weekday in new[] { "dddd, ", "dddd ", ", dddd", " dddd", "dddd" }) pattern = pattern.Replace(weekday, "");
        return day.ToString(pattern.Trim(), CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// macOS libraryPane: the recording's card alone while recording with nothing listed yet and nothing searched;
    /// "No conversations yet" for an empty library; "No conversations match" for a search without results.
    /// </summary>
    private void UpdateEmptyState(bool noSections, bool recording, bool anyListed)
    {
        string? text = null;
        if (noSections && !(recording && _model.SearchTerm.Length == 0))
        {
            text = anyListed
                ? Loc.Format("No conversations match \"%@\"", _model.SearchTerm)
                : Loc.T("No conversations yet — press Ctrl+R to record");
        }
        EmptyText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is not null) RecordingCard.SetText(EmptyText, text);
    }

    /// <summary>
    /// The sidebar beside the detail, with <paramref name="selectedId"/> (the shown conversation) selected and
    /// scrolled into view. Another shown conversation ends the selection.
    /// </summary>
    public void ShowAsSidebar(Guid? selectedId)
    {
        if (selectedId != _selectedId) _selection.Clear();
        _selectedId = selectedId;
        _card.Selected = _model.Route.Kind == RouteKind.Recording;
        UpdateCards();
        if (selectedId is { } id && _cards.TryGetValue(id, out var row) && _items.Contains(row.Item)) ConversationList.ScrollIntoView(row.Item);
    }

    // Selection

    /// <summary>
    /// Each card's state: selected as the open entry, or as part of the selection. A card WinUI
    /// took out of its item is put back first (see <see cref="_items"/>).
    /// </summary>
    private void UpdateCards()
    {
        foreach (var conversation in _shown)
        {
            if (!_cards.TryGetValue(conversation.Id, out var row)) continue;
            if (!ReferenceEquals(row.Item.Content, row.Card)) row.Item.Content = row.Card;
            var selected = _selection.IsEmpty ? conversation.Id == _selectedId : _selection.Ids.Contains(conversation.Id);
            row.Card.Update(conversation, selected);
        }
        UpdateSelectionBar();
    }

    /// <summary>A plain click (or Enter), or its AI or Summary button with their <paramref name="tab"/>: opens the card and ends the selection.</summary>
    internal void Open(ConversationCard card, DetailTab? tab = null)
    {
        StartupTrace.Write($"card open {card.Conversation.Id} (click, Enter or its AI/Summary button)");
        ClearSelection();
        if (tab is { } shown) _model.Open(card.Conversation.Id, shown);
        else _model.Open(card.Conversation.Id);
    }

    /// <summary>Ctrl-click toggles the card, Shift-click (<paramref name="extend"/>) adds the span from the anchor.</summary>
    internal void Select(ConversationCard card, bool extend)
    {
        // The sidebar's open entry is where the selection starts.
        if (_selection.IsEmpty && _selectedId is { } open && _shown.Any(c => c.Id == open)) _selection.Toggle(open);
        var id = card.Conversation.Id;
        if (extend) _selection.Extend(id, _shown.Select(c => c.Id).ToList());
        else _selection.Toggle(id);
        UpdateCards();
    }

    /// <summary>Deletes the selection, asking first: the Delete key (<see cref="MainWindow"/>, wherever the focus is).</summary>
    internal void DeleteSelection() => _ = DeleteFromKeyboardAsync(SelectedConversations(), selection: true);

    /// <summary>
    /// The window's Delete with no card selected, the focus neither in the cards, nor in a text field or another list: the
    /// open conversation, asking first. False when none is open or it cannot be deleted now (the key goes on).
    /// </summary>
    internal bool DeleteOpen()
    {
        if (_model.Route is not { Kind: RouteKind.Conversation, ConversationId: { } id }
            || _model.Conversations.FirstOrDefault(c => c.Id == id) is not { } conversation
            || !_model.CanDelete(conversation))
        {
            return false;
        }
        _ = DeleteFromKeyboardAsync([conversation], selection: false);
        return true;
    }

    /// <summary>
    /// A delete from the keyboard (macOS <c>deleteFromKeyboard</c>): when the open conversation goes, the next card that
    /// is not deleted opens in its place on the same tab and takes the focus, else the nearest one before it. Picked
    /// before the delete, which opens it instead of going back to the list, so the list never shows in between.
    /// </summary>
    private async Task DeleteFromKeyboardAsync(IReadOnlyList<Conversation> conversations, bool selection)
    {
        var deleting = conversations.Where(_model.CanDelete).Select(c => c.Id).ToHashSet();
        var next = _model.Route.ConversationId is { } open && deleting.Contains(open) ? Neighbour(open, deleting) : null;
        if (!await DeleteAsync(conversations, audioOnly: false, next)) return;
        if (selection) ClearSelection();
        // The open conversation went and its neighbour opened; a job may have kept it, or the page changed.
        if (next is { } id && _model.Route.ConversationId == id) FocusCard(id);
    }

    /// <summary>The first card after <paramref name="id"/> in list order that is not being deleted, else the nearest one before it.</summary>
    private Guid? Neighbour(Guid id, HashSet<Guid> deleting)
    {
        var ids = _shown.Select(c => c.Id).ToList();
        var index = ids.IndexOf(id);
        if (index < 0) return null;
        for (var i = index + 1; i < ids.Count; i++)
        {
            if (!deleting.Contains(ids[i])) return ids[i];
        }
        for (var i = index - 1; i >= 0; i--)
        {
            if (!deleting.Contains(ids[i])) return ids[i];
        }
        return null;
    }

    /// <summary>
    /// The window's ↑/↓ (<paramref name="delta"/> -1 or 1) with no card selected: the previous or next entry of the
    /// current page (the recording's card first while recording), opened as a click on its card opens it; the first or
    /// the last when none of them is open. Stops at the page's ends: ↑/↓ never change the page. False while cards are
    /// selected (the key goes on).
    /// </summary>
    internal bool Move(int delta)
    {
        if (!_selection.IsEmpty) return false;
        // Null: the recording's card.
        var entries = new List<Guid?>();
        if (_model.IsRecording) entries.Add(null);
        foreach (var conversation in _shown) entries.Add(conversation.Id);
        var route = _model.Route;
        var current = route.Kind == RouteKind.Recording ? entries.IndexOf(null)
            : route.ConversationId is { } open ? entries.IndexOf(open)
            : -1;
        var target = current >= 0 ? current + delta : delta > 0 ? 0 : entries.Count - 1;
        if (target < 0 || target >= entries.Count) return true;
        if (entries[target] is { } id)
        {
            OpenAndFocus(id);
            return true;
        }
        // As a click on the recording's card; its card is the list's header.
        _model.ShowRecording();
        _card.StartBringIntoView();
        _card.Focus(FocusState.Programmatic);
        return true;
    }

    /// <summary>Opens the card as a click does, and gives it the focus.</summary>
    private void OpenAndFocus(Guid id)
    {
        if (!_cards.TryGetValue(id, out var row)) return;
        Open(row.Card);
        FocusCard(id);
    }

    /// <summary>
    /// Scrolls the card into view and gives its item the focus. The items are their own containers: one scrolled far away
    /// is realized by the next layout. WinUI can report a programmatic focus as the last input's (Keyboard after ↑/↓ or
    /// Delete), so Space plays the opened conversation, as on macOS, because its card is the open one
    /// (<see cref="IsOpenCard"/>), not because of its focus state; Enter and Delete still act on it.
    /// </summary>
    private void FocusCard(Guid id)
    {
        if (!_cards.TryGetValue(id, out var row)) return;
        var item = row.Item;
        ConversationList.ScrollIntoView(item);
        ConversationList.UpdateLayout();
        if (!item.Focus(FocusState.Programmatic))
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => item.Focus(FocusState.Programmatic));
        }
    }

    /// <summary>Ends the selection: the bar's Cancel, and Esc (<see cref="MainWindow"/>, wherever the focus is).</summary>
    internal void ClearSelection()
    {
        if (_selection.IsEmpty) return;
        _selection.Clear();
        UpdateCards();
    }

    /// <summary>The selected conversations, in list order.</summary>
    private List<Conversation> SelectedConversations() => _shown.Where(c => _selection.Ids.Contains(c.Id)).ToList();

    /// <summary>macOS SelectionBar: the count, Delete (disabled when none can be deleted now) and Cancel.</summary>
    private void UpdateSelectionBar()
    {
        SelectionBar.Visibility = _selection.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (_selection.IsEmpty) return;
        var selected = SelectedConversations();
        RecordingCard.SetText(SelectionCountText, Loc.Format("%lld selected", selected.Count));
        SelectionDeleteButton.IsEnabled = selected.Any(_model.CanDelete);
    }

    /// <summary>The audio only (when one that can be deleted has audio) or the conversations; "…" when asked first.</summary>
    private void UpdateSelectionDeleteMenu()
    {
        var asks = !_model.Settings.DeleteWithoutConfirmation;
        var deletable = SelectedConversations().Where(_model.CanDelete).ToList();
        SelectionDeleteAudioItem.Text = Loc.T(asks ? "Delete audio only…" : "Delete audio only");
        SelectionDeleteAudioItem.IsEnabled = deletable.Any(c => c.HasAudio);
        SelectionDeleteConversationsItem.Text = Loc.T(asks ? "Delete conversations…" : "Delete conversations");
        SelectionDeleteConversationsItem.IsEnabled = deletable.Count > 0;
    }

    private async Task DeleteSelectionAsync(bool audioOnly)
    {
        if (await DeleteAsync(SelectedConversations(), audioOnly)) ClearSelection();
    }

    /// <summary>Through the window's delete flow, which asks first unless "Delete conversations without confirmation" is on.</summary>
    internal Task<bool> DeleteAsync(IReadOnlyList<Conversation> conversations, bool audioOnly, Guid? replacement = null) =>
        Delete is { } delete && conversations.Count > 0 ? delete(conversations, audioOnly, replacement) : Task.FromResult(false);

    // Keys

    /// <summary>
    /// Enter opens the focused card, Space only one reached with the keyboard (a clicked card leaves Space to the
    /// window: <see cref="MainWindow"/> plays); Delete deletes the focused card when no card is selected (the window
    /// deletes a selection), once while held: its repeats never delete the card that opens next. Not from a text field
    /// (the title being renamed) or a button. No Delete on the recording page: the recording's card is the open entry there
    /// (macOS does nothing for Delete on it), and a card that kept the focus is not the one shown. ↑/↓ are the window's
    /// (<see cref="Move"/>).
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case VirtualKey.Enter:
            // Not the open conversation's card: Space plays it (the window's).
            case VirtualKey.Space when e.OriginalSource is ListViewItem { FocusState: FocusState.Keyboard } && !IsOpenCard(e.OriginalSource):
                if (FocusedCard(e) is not { } card) return;
                e.Handled = true;
                Open(card);
                break;
            case VirtualKey.Delete:
                if (e.KeyStatus.WasKeyDown || !_selection.IsEmpty || _model.Route.Kind == RouteKind.Recording
                    || FocusedCard(e) is not { } focused)
                {
                    return;
                }
                e.Handled = true;
                _ = DeleteFromKeyboardAsync([focused.Conversation], selection: false);
                break;
        }
    }

    /// <summary>
    /// The element is the list item of the open conversation's card (one opened with ↑/↓, a delete or a click): Space
    /// plays it instead of opening it again (<see cref="MainWindow"/>'s TakesSpace).
    /// </summary>
    internal bool IsOpenCard(object? element) =>
        element is ListViewItem { Content: ConversationCard card } && _model.Route.Kind == RouteKind.Conversation
        && card.Conversation.Id == _model.Route.ConversationId;

    /// <summary>Cards are selected: the window's Esc ends the selection instead of going back (macOS <c>listSelectionActive</c>).</summary>
    internal bool HasSelection => !_selection.IsEmpty;

    /// <summary>The card whose list item has the keyboard focus (the key came from it).</summary>
    private static ConversationCard? FocusedCard(KeyRoutedEventArgs e) =>
        e.OriginalSource is ListViewItem { Content: ConversationCard card } ? card : null;

    internal static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // Search

    /// <summary>
    /// Ctrl+F (macOS <c>showSearch</c>): the focus goes to the search box. The view put back in the sidebar in place of
    /// the library chat is not loaded yet: the box takes the focus once it is.
    /// </summary>
    public void FocusSearch()
    {
        if (IsLoaded)
        {
            FocusSearchBox();
            return;
        }
        Loaded -= OnLoadedFocusSearch;
        Loaded += OnLoadedFocusSearch;
    }

    private void OnLoadedFocusSearch(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedFocusSearch;
        FocusSearchBox();
    }

    /// <summary>A box just laid out may not take the focus at once: it tries once more after the layout.</summary>
    private void FocusSearchBox()
    {
        if (!SearchBox.Focus(FocusState.Programmatic))
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => SearchBox.Focus(FocusState.Programmatic));
        }
    }

    /// <summary>
    /// The model cleared the search (another page of the window, the window hidden, the library chat opened): the box
    /// follows, and typing not applied yet is dropped. The box's own clearing, or a blank term, leaves it as it is.
    /// </summary>
    private void OnSearchTermChanged()
    {
        if (_model.SearchTerm.Length > 0 || SearchBox.Text.Trim().Length == 0) return;
        _searchTimer.Stop();
        SearchBox.Text = "";
    }

    /// <summary>Filters once typing pauses; a cleared field shows every conversation at once.</summary>
    private void OnSearchTextChanged()
    {
        _searchTimer.Stop();
        if (SearchBox.Text.Length == 0) _model.SearchTerm = "";
        else _searchTimer.Start();
    }

    /// <summary>Esc clears the search (macOS clears the query first).</summary>
    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || SearchBox.Text.Length == 0) return;
        e.Handled = true;
        SearchBox.Text = "";
    }

    /// <summary>
    /// The models still missing: speech models until one is installed, then speaker models while "Tell speakers
    /// apart" is on and none is installed.
    /// </summary>
    private void UpdateBanner()
    {
        var needsSpeech = !_model.HasTranscriptionModel;
        var visible = needsSpeech || _model.NeedsDiarizer;
        ModelBanner.IsOpen = visible;
        ModelBanner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;

        // Windows-only texts (Strings/Windows.xcstrings).
        ModelBanner.Title = Loc.T(needsSpeech ? "Download a transcription model" : "Download a speaker model");
        ModelBanner.Message = Loc.T(needsSpeech
            ? "Recordings are transcribed on this PC. Pick a speech recognition model to download."
            : "\"Tell speakers apart\" is on. Download a model to tell the other speakers apart.");
        var entries = (needsSpeech ? ModelCatalog.Asr : ModelCatalog.Diarization)
            .Where(e => !_model.InstalledModelIds.Contains(e.Id))
            .ToList();
        var selected = PickedModelId();
        if (!entries.Select(e => e.Id).SequenceEqual(_pickerIds))
        {
            // Plain names, so the ItemTemplate (with its ellipsis) also draws the closed combo.
            _pickerIds = entries.Select(e => e.Id).ToList();
            ModelPicker.Items.Clear();
            foreach (var entry in entries) ModelPicker.Items.Add(entry.DisplayName);
            var keep = entries.FindIndex(e => e.Id == selected);
            var fallback = entries.FindIndex(e => e.IsDefault);
            ModelPicker.SelectedIndex = keep >= 0 ? keep : Math.Max(0, fallback);
        }

        var running = _model.Downloads.FirstOrDefault(d => entries.Any(e => e.Id == d.Key));
        var downloading = running.Key is not null;
        ModelPicker.IsEnabled = !downloading;
        DownloadButton.IsEnabled = !downloading && entries.Count > 0;
        CancelButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadBar.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadBar.Value = running.Value;

        var error = entries.Select(e => _model.DownloadErrors.GetValueOrDefault(e.Id)).FirstOrDefault(m => m is not null);
        DownloadErrorText.Text = error is null ? "" : Loc.Format("The download failed: %@", error);
        DownloadErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (PickedModelId() is { } id) _model.DownloadModel(id);
    }

    /// <summary>The picked model's id: the banner's combo lists the names of <see cref="_pickerIds"/>, in order.</summary>
    private string? PickedModelId() =>
        ModelPicker.SelectedIndex is var i && i >= 0 && i < _pickerIds.Count ? _pickerIds[i] : null;

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        foreach (var id in _model.Downloads.Keys.ToList()) _model.CancelDownload(id);
    }
}

/// <summary>Texts shared by the library list and the conversation page.</summary>
internal static class ConversationText
{
    /// <summary>"m:ss", or "h:mm:ss" from one hour; 0 when not finite, at most 7 days.</summary>
    public static string Clock(double seconds)
    {
        // A huge value (e.g. from an edited file) would throw in TimeSpan; Math.Max lets NaN through.
        var safe = double.IsFinite(seconds) ? Math.Clamp(Math.Floor(seconds), 0, CatchUp.MaxSeconds) : 0;
        var time = TimeSpan.FromSeconds(safe);
        return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>"mm:ss" as the macOS transcript: minutes go past 59 in recordings longer than an hour.</summary>
    public static string Timestamp(double seconds)
    {
        var whole = (long)Math.Max(0, Math.Floor(seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 60:00}:{whole % 60:00}");
    }

    /// <summary>The date and the duration (the title bar's details).</summary>
    public static string When(Conversation conversation)
    {
        var created = conversation.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return $"{created} · {Clock(conversation.Duration)}";
    }

    /// <summary>The date, the duration and the status.</summary>
    public static string Detail(Conversation conversation, AppViewModel model) =>
        $"{When(conversation)} · {Status(conversation, model)}";

    /// <summary>
    /// The job's step and percent while one is queued or running, else the stored status. The macOS texts where macOS
    /// has one (ConversationCard.swift <c>stepText</c>, <c>StatusBadge</c>), else Windows-only ones.
    /// </summary>
    public static string Status(Conversation conversation, AppViewModel model)
    {
        if (model.TranscriptionProgress.TryGetValue(conversation.Id, out var progress))
        {
            if (model.IsStopping(conversation.Id)) return Loc.T("Stopping…");
            return progress.Step switch
            {
                JobStep.NeedsModel => Loc.T("Needs model"),
                JobStep.Queued => Loc.T("Queued"),
                JobStep.Deferred => Loc.T("Waiting: energy saver is on"),
                _ => Loc.Format("Transcribing %lld%%", (int)(progress.Fraction * 100)),
            };
        }
        if (model.TranscriptionErrors.ContainsKey(conversation.Id)) return Loc.T("Transcription failed");
        return Loc.T(conversation.Status switch
        {
            ConversationStatus.Recording => "Recording",
            ConversationStatus.Recovered => "Recovered",
            ConversationStatus.Compressing => "Compressing…",
            ConversationStatus.CompressionFailed => "Compression failed",
            ConversationStatus.Ready => "Ready",
            ConversationStatus.Transcribing => "Transcribing…",
            ConversationStatus.Transcribed => "Transcribed",
            ConversationStatus.Failed => "Failed",
            ConversationStatus.TranscriptionFailed => "Transcription failed",
            _ => conversation.HasTranscript ? "Transcribed" : "Not transcribed",
        });
    }
}
