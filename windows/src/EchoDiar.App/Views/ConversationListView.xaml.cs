using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Transcribe;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;

namespace EchoDiar.App.Views;

/// <summary>
/// The library (macOS ConversationListView.swift and MainView.swift libraryPane): conversation cards under day headers
/// (Today, Yesterday, then the date), filtered by the toolbar's search, with the model download banner (MVP). While
/// recording, the recording's card sits above them and the recording's own entry is left out. Ctrl-click and
/// Shift-click select cards (from the open entry in the sidebar, as in Mail); the selection bar then shows above the
/// list with Delete and Cancel (Esc). Delete deletes the selection, else the focused card. The ListView itself never
/// selects: the cards draw the open conversation and the selection, so the arrow keys only move the focus.
/// </summary>
public sealed partial class ConversationListView : UserControl
{
    private readonly AppViewModel _model;

    /// <summary>
    /// Day headers and cards, in list order, each in its own ListViewItem (so a card is never moved between item
    /// containers). Cards are kept by id and headers by day, so updates keep them.
    /// </summary>
    private readonly ObservableCollection<ListViewItem> _items = [];
    private readonly Dictionary<Guid, ListViewItem> _cards = [];
    private readonly Dictionary<DateTime, ListViewItem> _headers = [];

    private readonly ListSelection _selection = new();
    private readonly RecordingCard _card;

    /// <summary>The listed conversations, in list order.</summary>
    private IReadOnlyList<Conversation> _shown = [];

    /// <summary>The recording state the rows were last listed for.</summary>
    private bool _listedWhileRecording;

    /// <summary>Shown as the wide layout's sidebar: only the rows.</summary>
    private bool _sidebar;

    /// <summary>The sidebar's selected card: the shown conversation.</summary>
    private Guid? _selectedId;

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
        // Also the keys a focused item or the list already handled (Space, Enter).
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        Loaded += (_, _) =>
        {
            StartupTrace.Write("library view Loaded");
            _model.PropertyChanged += OnModelChanged;
            UpdateRows();
            UpdateBanner();
        };
        Unloaded += (_, _) => _model.PropertyChanged -= OnModelChanged;
    }

    /// <summary>
    /// The window's delete flow (<see cref="TitleBarToolbar.DeleteAsync"/>: eligibility, the confirmation, the delete);
    /// true when something was deleted.
    /// </summary>
    public Func<IReadOnlyList<Conversation>, bool, Task<bool>>? Delete { get; set; }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.Conversations):
            case nameof(AppViewModel.SearchResults):
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.TranscriptionErrors):
                UpdateRows();
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
    /// The search results by day; moves, adds and removes only the items that changed, so the list keeps its scroll
    /// position, and updates every card in place. Then the empty state, and the selection without the cards that left.
    /// </summary>
    private void UpdateRows()
    {
        var recording = _model.IsRecording;
        _listedWhileRecording = recording;
        _card.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        // The recording's own entry is hidden while recording: its card is the recording card.
        bool Listed(Conversation c) => !(recording && c.Status == ConversationStatus.Recording);
        var groups = DayGroup.Groups(_model.SearchResults.Where(Listed));

        var wanted = new List<ListViewItem>();
        var shown = new List<Conversation>();
        foreach (var group in groups)
        {
            wanted.Add(Header(group.Day));
            foreach (var conversation in group.Conversations)
            {
                if (!_cards.TryGetValue(conversation.Id, out var row))
                {
                    row = new ListViewItem
                    {
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        MinHeight = 0,
                        Content = new ConversationCard(_model, this, conversation),
                    };
                    _cards[conversation.Id] = row;
                }
                wanted.Add(row);
                shown.Add(conversation);
            }
        }
        _shown = shown;
        Sync(wanted);

        // Cards of deleted conversations, headers of days no longer listed.
        var ids = _model.Conversations.Select(c => c.Id).ToHashSet();
        foreach (var id in _cards.Keys.Where(id => !ids.Contains(id)).ToList()) _cards.Remove(id);
        var days = groups.Select(g => g.Day).ToHashSet();
        foreach (var day in _headers.Keys.Where(day => !days.Contains(day)).ToList()) _headers.Remove(day);

        // Cards that left the list (search filter, deletion) are not selected.
        _selection.Prune(shown.Select(c => c.Id).ToList());
        UpdateCards();
        UpdateEmptyState(groups.Count == 0, recording, _model.Conversations.Any(Listed));
        StartupTrace.Write($"library rows updated ({shown.Count} rows)");
    }

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

    /// <summary>"Today", "Yesterday", or the long date without the weekday, as muted centered text; not focusable.</summary>
    private ListViewItem Header(DateTime day)
    {
        if (!_headers.TryGetValue(day, out var header))
        {
            header = new ListViewItem
            {
                IsTabStop = false,
                IsHitTestVisible = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 0,
                Content = new TextBlock { Style = (Style)Resources["DayHeaderText"] },
            };
            AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
            _headers[day] = header;
        }
        // Today and Yesterday move on at midnight.
        RecordingCard.SetText((TextBlock)header.Content, DayTitle(day));
        return header;
    }

    private static string DayTitle(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return Loc.T("Today");
        if (day == today.AddDays(-1)) return Loc.T("Yesterday");
        // macOS .long without a time: "October 22, 2026"; Windows' long date also names the weekday.
        var pattern = CultureInfo.CurrentCulture.DateTimeFormat.LongDatePattern;
        foreach (var weekday in new[] { "dddd, ", "dddd ", ", dddd", " dddd", "dddd" }) pattern = pattern.Replace(weekday, "");
        return day.ToString(pattern.Trim(), CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// macOS libraryPane: the recording's card alone while recording with nothing listed yet and nothing searched;
    /// "No conversations yet" for an empty library; "No conversations match" for a search without results.
    /// </summary>
    private void UpdateEmptyState(bool noGroups, bool recording, bool anyListed)
    {
        string? text = null;
        if (noGroups && !(recording && _model.SearchTerm.Length == 0))
        {
            text = anyListed
                ? Loc.Format("No conversations match \"%@\"", _model.SearchTerm)
                // Ctrl+R for ⌘R: the main window's shortcuts come with Phase 20.
                : Loc.T("No conversations yet — press ⌘R to record").Replace("⌘R", "Ctrl+R", StringComparison.Ordinal);
        }
        EmptyText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is not null) RecordingCard.SetText(EmptyText, text);
    }

    /// <summary>
    /// The whole page on the compact layout; on wide, the sidebar beside the page (macOS compact cards) with
    /// <paramref name="selectedId"/> (the shown conversation) selected and scrolled into view. Another shown
    /// conversation ends the selection.
    /// </summary>
    public void ShowAsSidebar(bool sidebar, Guid? selectedId)
    {
        if (selectedId != _selectedId) _selection.Clear();
        _sidebar = sidebar;
        _selectedId = selectedId;
        HeaderGrid.Visibility = sidebar ? Visibility.Collapsed : Visibility.Visible;
        _card.Compact = sidebar;
        _card.Selected = _model.Route.Kind == RouteKind.Recording;
        UpdateBanner();
        UpdateCards();
        if (selectedId is { } id && _cards.TryGetValue(id, out var row) && _items.Contains(row)) ConversationList.ScrollIntoView(row);
    }

    // Selection

    /// <summary>Each card's state: compact in the sidebar; selected as the open entry, or as part of the selection.</summary>
    private void UpdateCards()
    {
        foreach (var conversation in _shown)
        {
            var selected = _selection.IsEmpty ? conversation.Id == _selectedId : _selection.Ids.Contains(conversation.Id);
            ((ConversationCard)_cards[conversation.Id].Content).Update(conversation, _sidebar, selected);
        }
        UpdateSelectionBar();
    }

    /// <summary>A plain click (or Enter): opens the card and ends the selection.</summary>
    internal void Open(ConversationCard card)
    {
        ClearSelection();
        _model.Open(card.Conversation.Id);
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

    private void ClearSelection()
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
    internal Task<bool> DeleteAsync(IReadOnlyList<Conversation> conversations, bool audioOnly) =>
        Delete is { } delete && conversations.Count > 0 ? delete(conversations, audioOnly) : Task.FromResult(false);

    // Keys

    /// <summary>
    /// Enter or Space opens the focused card; Delete deletes the selection, else the focused card; Esc ends the
    /// selection. Not from a text field (the title being renamed) or a button.
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case VirtualKey.Enter or VirtualKey.Space:
                if (FocusedCard(e) is not { } card) return;
                e.Handled = true;
                Open(card);
                break;
            case VirtualKey.Delete:
                List<Conversation> targets = !_selection.IsEmpty ? SelectedConversations()
                    : FocusedCard(e) is { } focused ? [focused.Conversation] : [];
                if (targets.Count == 0) return;
                e.Handled = true;
                if (_selection.IsEmpty) _ = DeleteAsync(targets, audioOnly: false);
                else _ = DeleteSelectionAsync(audioOnly: false);
                break;
            case VirtualKey.Escape:
                if (_selection.IsEmpty) return;
                e.Handled = true;
                ClearSelection();
                break;
        }
    }

    /// <summary>The card whose list item has the keyboard focus (the key came from it).</summary>
    private static ConversationCard? FocusedCard(KeyRoutedEventArgs e) =>
        e.OriginalSource is ListViewItem { Content: ConversationCard card } ? card : null;

    internal static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// The models still missing: speech models until one is installed, then speaker models while "Tell speakers
    /// apart" is on and none is installed.
    /// </summary>
    private void UpdateBanner()
    {
        var needsSpeech = !_model.HasTranscriptionModel;
        var visible = !_sidebar && (needsSpeech || _model.NeedsDiarizer);
        ModelBanner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;

        BannerTitle.Text = needsSpeech ? "Download a transcription model" : "Download a speaker model";
        BannerDetail.Text = needsSpeech
            ? "Recordings are transcribed on this PC. Pick a speech recognition model to download."
            : "\"Tell speakers apart\" is on. Download a model to tell the other speakers apart.";
        var entries = (needsSpeech ? ModelCatalog.Asr : ModelCatalog.Diarization)
            .Where(e => !_model.InstalledModelIds.Contains(e.Id))
            .ToList();
        var selected = (ModelPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        if (!entries.Select(e => e.Id).SequenceEqual(ModelPicker.Items.OfType<ComboBoxItem>().Select(i => i.Tag as string)))
        {
            ModelPicker.Items.Clear();
            foreach (var entry in entries) ModelPicker.Items.Add(new ComboBoxItem { Content = entry.DisplayName, Tag = entry.Id });
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
        DownloadErrorText.Text = error is null ? "" : $"The download failed: {error}";
        DownloadErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if ((ModelPicker.SelectedItem as ComboBoxItem)?.Tag is string id) _model.DownloadModel(id);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        foreach (var id in _model.Downloads.Keys.ToList()) _model.CancelDownload(id);
    }
}

/// <summary>Texts shared by the library list and the conversation page.</summary>
internal static class ConversationText
{
    /// <summary>"m:ss", or "h:mm:ss" from one hour.</summary>
    public static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>"mm:ss" as the macOS transcript: minutes go past 59 in recordings longer than an hour.</summary>
    public static string Timestamp(double seconds)
    {
        var whole = (long)Math.Max(0, Math.Floor(seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 60:00}:{whole % 60:00}");
    }

    /// <summary>The date, the duration and the status.</summary>
    public static string Detail(Conversation conversation, AppViewModel model)
    {
        var created = conversation.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return $"{created} · {Clock(conversation.Duration)} · {Status(conversation, model)}";
    }

    /// <summary>The job's step and percent while one is queued or running, else the stored status.</summary>
    public static string Status(Conversation conversation, AppViewModel model)
    {
        if (model.TranscriptionProgress.TryGetValue(conversation.Id, out var progress))
        {
            return progress.Step switch
            {
                JobStep.NeedsModel => "Needs a transcription model",
                JobStep.Queued => "Waiting to transcribe",
                _ => $"Transcribing {(int)(progress.Fraction * 100)}%",
            };
        }
        if (model.TranscriptionErrors.ContainsKey(conversation.Id)) return "Transcription failed";
        return conversation.Status switch
        {
            ConversationStatus.Recording => "Recording",
            ConversationStatus.Recovered => "Recovered",
            ConversationStatus.Compressing => "Compressing",
            ConversationStatus.CompressionFailed => "Compression failed",
            ConversationStatus.Ready => "Ready",
            ConversationStatus.Transcribing => "Transcribing",
            ConversationStatus.Transcribed => "Transcribed",
            ConversationStatus.Failed => "Recording failed",
            ConversationStatus.TranscriptionFailed => "Transcription failed",
            _ => conversation.HasTranscript ? "Transcribed" : "Not transcribed",
        };
    }
}
