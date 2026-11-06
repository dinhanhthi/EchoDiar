using System.ComponentModel;
using CommunityToolkit.WinUI.Controls;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Calendar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// Settings › Calendar (macOS CalendarSettingsView.swift, CalendarSettingsSections): calendar access, what happens when
/// a meeting starts, the watched calendars and stopping at the event's end. Every setting shows
/// <see cref="SettingsViewModel"/> and changes it; the change is saved after its delay. Coming back from Windows' privacy
/// settings syncs the calendar (<see cref="MainWindow"/>, macOS didBecomeActive).
/// </summary>
public sealed partial class CalendarSettingsPage : UserControl
{
    /// <summary>The searchable rows (macOS SettingsSearch.swift, Calendar), each the Tag of its element.</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Calendar", ["calendar access", "iCloud", "Google", "Outlook", "Exchange", "events", "schedule", "privacy"]),
        new("When a meeting starts", ["calendar", "auto record", "event", "schedule", "wait for the call", "record at start"]),
        new("Watched calendars", ["calendar", "iCloud", "Google", "Outlook", "Exchange", "account"]),
        new("Only events with a meeting link", ["calendar", "event", "Zoom", "Google Meet", "Microsoft Teams", "Webex", "link"]),
        new("Stop when the event ends", ["calendar", "auto-stop", "event end", "meeting end"]),
    ];

    /// <summary>The calendar mode combo's items, in order (macOS: Off, Ask, Wait for the call, Record at start).</summary>
    private static readonly CalendarScheduler.Mode[] CalendarModes =
        [CalendarScheduler.Mode.Off, CalendarScheduler.Mode.Ask, CalendarScheduler.Mode.WaitForCall, CalendarScheduler.Mode.RecordAtStart];

    private readonly AppViewModel _model;
    private readonly SettingsViewModel _settings;

    /// <summary>The controls are being set from the settings: their change events are not the user's.</summary>
    private bool _syncing;

    /// <summary>One row per calendar of <see cref="AppViewModel.Calendars"/>: its calendar and its switch (on = watched).</summary>
    private readonly List<(CalendarInfo Calendar, ToggleSwitch Toggle)> _calendarRows = [];

    public CalendarSettingsPage(AppViewModel model)
    {
        _model = model;
        _settings = model.SettingsModel;
        InitializeComponent();
        AddCalendarRows();
        SetUpStopAfterEnd();

        CalendarGrantAccessButton.Click += (_, _) => _model.RefreshCalendar();
        CalendarPrivacyButton.Click += (_, _) =>
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-calendar"));
        CalendarModeCombo.SelectionChanged += (_, _) =>
        {
            var index = CalendarModeCombo.SelectedIndex;
            if (!_syncing && index >= 0) UpdateCalendar(c => c with { Mode = CalendarModes[index] });
        };
        CalendarOpenGeneralButton.Click += (_, _) => _model.OpenSettings(SettingsSearch.General);
        // The inverse of allTimedEvents (macOS meetingLinkOnly).
        LinkOnlyToggle.Toggled += (_, _) => Set(() => UpdateCalendar(c => c with { AllTimedEvents = !LinkOnlyToggle.IsOn }));
        StopAtEventEndToggle.Toggled += (_, _) => Set(() => UpdateCalendar(c => c with { StopAtEventEnd = StopAtEventEndToggle.IsOn }));

        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _settings.PropertyChanged += OnSettingsChanged;
            // The calendars may have changed while the page was not shown.
            if (!_calendarRows.Select(r => r.Calendar).SequenceEqual(_model.Calendars)) AddCalendarRows();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _settings.PropertyChanged -= OnSettingsChanged;
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_model.IsClosing) return;
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.CalendarAccess):
                Refresh();
                break;
            case nameof(AppViewModel.Calendars):
                AddCalendarRows();
                Refresh();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_model.IsClosing) Refresh();
    }

    private void Set(Action change)
    {
        if (!_syncing) change();
    }

    /// <summary>
    /// Access (nothing once granted, else what is missing and how to grant it), the mode and its Detect meetings hint,
    /// the launch at login hint, the watched calendars (with access only), and the event-end stop.
    /// </summary>
    private void Refresh()
    {
        var settings = _settings.Settings;
        _syncing = true;
        try
        {
            var access = _model.CalendarAccess;
            var granted = access == CalendarAccess.Granted;
            var notDetermined = access == CalendarAccess.NotDetermined;
            CalendarAccessCard.Visibility = granted ? Visibility.Collapsed : Visibility.Visible;
            CalendarAccessCard.Description = Loc.T(notDetermined
                ? "EchoDiar has no calendar access yet."
                : "Calendar access is off. Turn on EchoDiar in Windows Settings › Privacy & security › Calendar.");
            CalendarGrantAccessButton.Visibility = notDetermined ? Visibility.Visible : Visibility.Collapsed;
            CalendarPrivacyButton.Visibility = notDetermined ? Visibility.Collapsed : Visibility.Visible;

            var calendar = settings.Calendar;
            CalendarModeCombo.SelectedIndex = Array.IndexOf(CalendarModes, calendar.Mode);
            CalendarModeCard.Description = calendar.Mode == CalendarScheduler.Mode.WaitForCall && !settings.MeetingDetection
                ? Loc.T("Detect meetings is off, so EchoDiar asks instead.")
                : null!; // null hides it
            CalendarLaunchAtLoginCard.Visibility = settings.LaunchAtLogin ? Visibility.Collapsed : Visibility.Visible;

            WatchedCalendarsPanel.Visibility = granted ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (info, toggle) in _calendarRows) toggle.IsOn = !calendar.IgnoredCalendarIDs.Contains(info.Id);

            LinkOnlyToggle.IsOn = !calendar.AllTimedEvents;
            StopAtEventEndToggle.IsOn = calendar.StopAtEventEnd;
            StopAfterEndCard.IsEnabled = calendar.StopAtEventEnd;
            StopAfterEndBox.Value = calendar.EffectiveStopAfterEndMinutes;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>A switch per calendar, under its account's name (when it has one), in the model's order.</summary>
    private void AddCalendarRows()
    {
        _calendarRows.Clear();
        CalendarsList.Children.Clear();
        foreach (var group in _model.Calendars.GroupBy(c => c.SourceTitle))
        {
            if (group.Key.Length > 0)
            {
                CalendarsList.Children.Add(new TextBlock { Text = group.Key, Style = (Style)Resources["SourceTitleStyle"] });
            }
            foreach (var info in group)
            {
                var toggle = new ToggleSwitch();
                AutomationProperties.SetName(toggle, info.Title);
                toggle.Toggled += (_, _) => Set(() => SetWatched(info, toggle.IsOn));
                CalendarsList.Children.Add(new SettingsCard { Header = info.Title, Content = toggle });
                _calendarRows.Add((info, toggle));
            }
        }
    }

    /// <summary>Watched = not in <c>ignoredCalendarIDs</c>; a new list, so the change is seen and syncs the calendar.</summary>
    private void SetWatched(CalendarInfo calendar, bool watched) => UpdateCalendar(c =>
    {
        List<string> ignored = [.. c.IgnoredCalendarIDs.Where(id => id != calendar.Id)];
        if (!watched) ignored.Add(calendar.Id);
        return c with { IgnoredCalendarIDs = ignored };
    });

    private void UpdateCalendar(Func<CalendarScheduler.Configuration, CalendarScheduler.Configuration> change) =>
        _settings.Update(s => s with { Calendar = change(s.Calendar) });

    /// <summary>
    /// Minutes after the end, a whole number typed or stepped within its range (macOS NumberRow, as Recording's
    /// auto-stop numbers): a value outside it is clamped, a cleared box shows the saved value again.
    /// </summary>
    private void SetUpStopAfterEnd()
    {
        var range = CalendarScheduler.Configuration.StopAfterEndRange;
        StopAfterEndBox.Minimum = range.Min;
        StopAfterEndBox.Maximum = range.Max;
        ToolTipService.SetToolTip(StopAfterEndCard, $"{range.Min}–{range.Max} {Loc.T("min")}");
        StopAfterEndBox.ValueChanged += (_, args) =>
        {
            if (_syncing) return;
            if (double.IsNaN(args.NewValue))
            {
                Refresh();
                return;
            }
            var value = Math.Clamp((int)Math.Round(args.NewValue), range.Min, range.Max);
            UpdateCalendar(c => c with { StopAfterEndMinutes = value });
            // A typed value outside the range shows clamped.
            if (value != args.NewValue) Refresh();
        };
    }
}
