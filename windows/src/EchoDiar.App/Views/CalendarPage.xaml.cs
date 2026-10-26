using System.ComponentModel;
using System.Globalization;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Calendar;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// The Calendar route (macOS CalendarView.swift): calendar access while it is not granted (Grant Access, or the way to
/// Windows' privacy settings), the launch at login hint while it is off, then the upcoming events of
/// <see cref="AppViewModel.UpcomingCalendarEvents"/> by day, or why there are none. Laid out again each minute (macOS
/// TimelineView), so Record turns on 15 min before an event and an event in progress moves under Today without a
/// calendar change. Nothing loads here: the model's calendar sync reads the store off the UI thread, so the page shows
/// at once.
/// </summary>
public sealed partial class CalendarPage : UserControl
{
    /// <summary>Record is offered this long before an event starts.</summary>
    private static readonly TimeSpan RecordLead = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly AppViewModel _model;
    private readonly DispatcherQueueTimer _timer;

    /// <summary>The events (with their day, and whether each is EchoDiar's own) the cards were built for.</summary>
    private IReadOnlyList<(DateTime Day, CalendarEvent Event, bool Own)> _layout = [];

    /// <summary>The day the cards were built: Today and Tomorrow move on at midnight.</summary>
    private DateTime _builtToday;

    /// <summary>Each card's parts that change in place: its texts (dimmed while skipped), Record and Skip.</summary>
    private readonly List<Row> _rows = [];

    /// <summary>The Skip switches are being set from the model: not a toggle.</summary>
    private bool _syncing;

    private sealed record Row(CalendarEvent Event, UIElement Texts, Button Record, ToggleSwitch Skip);

    public CalendarPage(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        NewEventButton.Click += (_, _) => _ = OpenEditorAsync(null);
        CalendarSettingsButton.Click += (_, _) => _model.OpenSettings(SettingsSearch.Recording);
        GrantAccessButton.Click += (_, _) => _model.RefreshCalendar();
        PrivacySettingsButton.Click += (_, _) =>
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-calendar"));
        OpenGeneralButton.Click += (_, _) => _model.OpenSettings(SettingsSearch.General);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TickInterval;
        _timer.Tick += (_, _) => Update();
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _timer.Start();
            Update();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _timer.Stop();
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.CalendarAccess):
            case nameof(AppViewModel.UpcomingCalendarEvents):
            case nameof(AppViewModel.Settings):
                Update();
                break;
            // IsRecording is raised at 15 Hz while recording: only Record follows it.
            case nameof(AppViewModel.IsRecording):
            case nameof(AppViewModel.IsStarting):
                UpdateRecordButtons(DateTimeOffset.Now);
                break;
        }
    }

    private void Update()
    {
        var access = _model.CalendarAccess;
        var granted = access == CalendarAccess.Granted;
        SetVisible(AccessCard, !granted);
        if (!granted)
        {
            var notDetermined = access == CalendarAccess.NotDetermined;
            RecordingCard.SetText(AccessText, Loc.T(notDetermined
                ? "EchoDiar has no calendar access yet."
                : "Calendar access is off. Turn on EchoDiar in Windows Settings › Privacy & security › Calendar."));
            SetVisible(GrantAccessButton, notDetermined);
            SetVisible(PrivacySettingsButton, !notDetermined);
        }
        SetVisible(LaunchAtLoginCard, !_model.Settings.LaunchAtLogin);
        NewEventButton.IsEnabled = granted;

        var now = DateTimeOffset.Now;
        var layout = granted ? Layout(now) : [];
        SetVisible(EmptyCard, granted && layout.Count == 0);
        SetVisible(LinkOnlyText, !_model.Settings.Calendar.AllTimedEvents);
        if (DateTime.Today != _builtToday || !layout.SequenceEqual(_layout)) Build(layout);
        UpdateRows(now);
    }

    /// <summary>
    /// The events not over at <paramref name="now"/>, with the day they start (an event in progress counts as today's);
    /// the model lists them by start, so by day too.
    /// </summary>
    private List<(DateTime Day, CalendarEvent Event, bool Own)> Layout(DateTimeOffset now)
    {
        var own = _model.Settings.Calendar.OwnCalendarID;
        return _model.UpcomingCalendarEvents
            .Where(e => e.End > now)
            .Select(e => ((e.Start > now ? e.Start : now).LocalDateTime.Date, e, own is not null && e.CalendarID == own))
            .ToList();
    }

    /// <summary>A header per day, then a card per event of that day.</summary>
    private void Build(IReadOnlyList<(DateTime Day, CalendarEvent Event, bool Own)> layout)
    {
        _layout = layout;
        _builtToday = DateTime.Today;
        _rows.Clear();
        EventsPanel.Children.Clear();
        DateTime? shownDay = null;
        foreach (var (day, @event, own) in layout)
        {
            if (day != shownDay)
            {
                shownDay = day;
                var header = new TextBlock { Text = DayTitle(day), Style = (Style)Resources["DayHeaderStyle"] };
                AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
                EventsPanel.Children.Add(header);
            }
            EventsPanel.Children.Add(Card(@event, own));
        }
    }

    /// <summary>
    /// The time, the title (one line, whole in its tooltip) and the meeting service; then Edit… for EchoDiar's own events,
    /// Join with a meeting link, Record and Skip.
    /// </summary>
    private Border Card(CalendarEvent @event, bool own)
    {
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = $"{Time(@event.Start)}–{Time(@event.End)}",
            Style = (Style)Resources["SecondaryTextStyle"],
        });
        var title = new TextBlock
        {
            Text = @event.Title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        ToolTipService.SetToolTip(title, @event.Title);
        texts.Children.Add(title);
        if (@event.Link?.ServiceName is { } service)
        {
            texts.Children.Add(new TextBlock { Text = service, Style = (Style)Resources["CaptionStyle"] });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (own) actions.Children.Add(ActionButton(Loc.T("Edit…"), null, () => _ = OpenEditorAsync(@event)));
        if (@event.Link is not null)
        {
            actions.Children.Add(ActionButton(Loc.T("Join"), Loc.T("Open the meeting link"), () => _model.OpenCalendarEventLink(@event)));
        }
        // All system audio, as on macOS: the meeting app may not run yet.
        var record = ActionButton(Loc.T("Record"), Loc.T("Record all system audio, from 15 min before the start"),
            () => _ = _model.StartRecordingAsync(null, @event));
        actions.Children.Add(record);
        var skipLabel = new TextBlock
        {
            Text = Loc.T("Skip"),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Resources["SecondaryTextStyle"],
        };
        // The switch carries the name.
        AutomationProperties.SetAccessibilityView(skipLabel, AccessibilityView.Raw);
        var skip = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(skip, Loc.T("Skip"));
        skip.Toggled += (_, _) =>
        {
            if (!_syncing) _model.SkipCalendarEvent(@event, skip.IsOn);
        };
        var skipPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { skipLabel, skip } };
        ToolTipService.SetToolTip(skipPanel, Loc.T("Skip this meeting: no prompt and no recording"));
        actions.Children.Add(skipPanel);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(actions, 1);
        grid.Children.Add(texts);
        grid.Children.Add(actions);
        _rows.Add(new Row(@event, texts, record, skip));
        return new Border { Style = (Style)Resources["CardStyle"], Child = grid };
    }

    private static Button ActionButton(string text, string? tip, Action click)
    {
        var button = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
        if (tip is not null) ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Skipped events dimmed, with Skip on; then Record.</summary>
    private void UpdateRows(DateTimeOffset now)
    {
        var skipped = _model.Settings.Calendar.SkippedEvents;
        _syncing = true;
        try
        {
            foreach (var row in _rows)
            {
                var isSkipped = skipped.ContainsKey(row.Event.Id);
                if (row.Skip.IsOn != isSkipped) row.Skip.IsOn = isSkipped;
                row.Texts.Opacity = isSkipped ? 0.5 : 1;
            }
        }
        finally
        {
            _syncing = false;
        }
        UpdateRecordButtons(now);
    }

    /// <summary>Record while idle, from 15 min before an event's start until its end.</summary>
    private void UpdateRecordButtons(DateTimeOffset now)
    {
        var idle = !_model.IsRecording && !_model.IsStarting;
        foreach (var row in _rows)
        {
            var enabled = idle && row.Event.Start - now <= RecordLead && row.Event.End > now;
            if (row.Record.IsEnabled != enabled) row.Record.IsEnabled = enabled;
        }
    }

    /// <summary>The event editor: a new event (null) or one of EchoDiar's own.</summary>
    private Task OpenEditorAsync(CalendarEvent? @event) => Task.CompletedTask; // The editor arrives in task 9.2.

    private static string DayTitle(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return Loc.T("Today");
        if (day == today.AddDays(1)) return Loc.T("Tomorrow");
        return ConversationListView.LongDate(day);
    }

    private static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    private static void SetVisible(UIElement element, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != visibility) element.Visibility = visibility;
    }
}
