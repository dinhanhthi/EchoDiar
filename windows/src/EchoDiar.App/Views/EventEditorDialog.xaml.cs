using EchoDiar.App.ViewModels;
using EchoDiar.Core.Calendar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// Creates, edits or deletes an event of EchoDiar's own calendar (macOS EventEditorSheet.swift); <c>editing</c> null =
/// a new event. Opens at once: an existing event shows its card's values, disabled, until the store's draft is read off
/// the UI thread. Save and Delete stay in the dialog until the store answers; an error shows in it, success closes it.
/// A repeating event asks which occurrences in this same dialog (macOS alert): Cancel, Esc included, goes back to the form.
/// </summary>
public sealed partial class EventEditorDialog : ContentDialog
{
    private enum Pending { None, Save, Delete }

    private readonly AppViewModel _model;
    private readonly CalendarEvent? _editing;
    private CalendarEventDraft _draft;

    /// <summary>The values as loaded, to tell whether Repeat or End changed; null until read.</summary>
    private CalendarEventDraft? _loaded;

    private bool _isRecurring;
    private bool _loadFailed;

    /// <summary>A store call runs: the form, the buttons and closing wait for it.</summary>
    private bool _busy;

    /// <summary>The question shown instead of the form, and the spans its Primary and Secondary buttons apply.</summary>
    private Pending _pending;
    private CalendarSpan _primarySpan;
    private CalendarSpan _secondarySpan;

    /// <summary>Saved or deleted: the dialog may close.</summary>
    private bool _done;

    /// <summary>The controls are being set from the draft: not an edit.</summary>
    private bool _syncing;

    public EventEditorDialog(AppViewModel model, CalendarEvent? editing)
    {
        _model = model;
        _editing = editing;
        // An existing event shows its card's values until the store's are read.
        _draft = editing is null
            ? CalendarEventDraft.New(DateTimeOffset.Now, TimeZoneInfo.Local)
            : new CalendarEventDraft(editing.Title, editing.Start, editing.End);
        if (editing is null) _loaded = _draft with { };
        InitializeComponent();
        // In CalendarEventDraft.Repeat order; Custom is added only for a rule read back from another app.
        foreach (var text in new[] { Loc.T("None"), Loc.T("Daily"), Loc.T("Weekdays (Mon–Fri)"), Loc.T("Weekly"), Loc.T("Monthly") })
        {
            RepeatBox.Items.Add(text);
        }
        EndRepeatBox.Items.Add(Loc.T("Never"));
        EndRepeatBox.Items.Add(Loc.T("On date"));
        LoadFailedText.Text = Loc.T("This event can't be edited here.");

        TitleBox.TextChanged += (_, _) => Edit(() => _draft.Title = TitleBox.Text);
        LinkBox.TextChanged += (_, _) => Edit(() => _draft.UrlText = LinkBox.Text);
        DayPicker.DateChanged += (_, e) => Edit(() => MoveToDay(e.NewDate));
        StartPicker.SelectedTimeChanged += (_, e) => Edit(() => _draft.Start = AtTime(e.NewTime, _draft.Start));
        EndPicker.SelectedTimeChanged += (_, e) => Edit(() => _draft.End = AtTime(e.NewTime, _draft.End));
        RepeatBox.SelectionChanged += (_, _) => Edit(SetRepeat);
        EndRepeatBox.SelectionChanged += (_, _) => Edit(SetEndsOnDate);
        RepeatEndPicker.DateChanged += (_, e) => Edit(() =>
        {
            if (e.NewDate is { } day) _draft.RepeatEnd = new DateTimeOffset(day.ToLocalTime().Date);
            else FillRepeatEnd();
        });
        DeleteButton.Click += (_, _) => Ask(Pending.Delete);
        PrimaryButtonClick += (_, e) =>
        {
            // Closes only once the store has answered.
            e.Cancel = true;
            if (_pending == Pending.None) Save();
            else Answer(_primarySpan);
        };
        SecondaryButtonClick += (_, e) =>
        {
            e.Cancel = true;
            Answer(_secondarySpan);
        };
        Closing += (_, e) =>
        {
            if (_done) return;
            if (_busy) e.Cancel = true;
            else if (_pending != Pending.None)
            {
                // Cancel or Esc on the question: back to the form.
                e.Cancel = true;
                ShowForm();
            }
        };

        Fill();
        ShowForm();
        if (editing is not null) _ = LoadAsync(editing);
    }

    /// <summary>The store's values for the event, and whether it repeats; not found = "can't be edited here".</summary>
    private async Task LoadAsync(CalendarEvent editing)
    {
        if (await _model.CalendarDraftAsync(editing) is not { } loaded)
        {
            _loadFailed = true;
            ShowForm();
            return;
        }
        (_draft, _isRecurring) = loaded;
        if (_draft.RepeatRule == CalendarEventDraft.Repeat.Custom) RepeatBox.Items.Add(Loc.T("Custom"));
        _loaded = _draft with { };
        Fill();
    }

    // Form

    /// <summary>The form's title and buttons (Save / Cancel, or Close when the event can't be edited).</summary>
    private void ShowForm()
    {
        _pending = Pending.None;
        Title = _editing is null ? Loc.T("New Event") : Loc.T("Edit Event");
        PrimaryButtonText = _loadFailed ? "" : Loc.T("Save");
        SecondaryButtonText = "";
        CloseButtonText = _loadFailed ? Loc.T("Close") : Loc.T("Cancel");
        DefaultButton = _loadFailed ? ContentDialogButton.Close : ContentDialogButton.Primary;
        Body.Visibility = Visibility.Visible;
        SetVisible(Form, !_loadFailed);
        SetVisible(LoadFailedText, _loadFailed);
        SetVisible(DeleteButton, _editing is not null && !_loadFailed);
        Update();
    }

    /// <summary>The controls from the draft.</summary>
    private void Fill()
    {
        Sync(() =>
        {
            TitleBox.Text = _draft.Title;
            DayPicker.Date = Day(_draft.Start);
            StartPicker.SelectedTime = TimeOfDay(_draft.Start);
            EndPicker.SelectedTime = TimeOfDay(_draft.End);
            LinkBox.Text = _draft.UrlText;
            RepeatBox.SelectedIndex = (int)_draft.RepeatRule;
        });
        FillRepeatEnd();
    }

    /// <summary>End Repeat and End date from the draft.</summary>
    private void FillRepeatEnd() => Sync(() =>
    {
        EndRepeatBox.SelectedIndex = _draft.RepeatEnd is null ? 0 : 1;
        RepeatEndPicker.Date = _draft.RepeatEnd is { } end ? Day(end) : null;
    });

    /// <summary>Sets controls without taking it as the user's edit, then updates the hints.</summary>
    private void Sync(Action set)
    {
        _syncing = true;
        try
        {
            set();
        }
        finally
        {
            _syncing = false;
        }
        Update();
    }

    /// <summary>A user's change to the draft, then the hints and Save.</summary>
    private void Edit(Action change)
    {
        if (_syncing || _loaded is null) return;
        change();
        Update();
    }

    /// <summary>The hints, the repeat end, and what can be clicked.</summary>
    private void Update()
    {
        var ready = _loaded is not null && !_busy;
        Form.IsEnabled = ready;
        DeleteButton.IsEnabled = ready;
        IsPrimaryButtonEnabled = _pending == Pending.None ? ready && _draft.IsValid : !_busy;
        IsSecondaryButtonEnabled = !_busy;

        SetVisible(EndHint, _draft.End <= _draft.Start);
        string? linkHint = null;
        if (!string.IsNullOrWhiteSpace(_draft.UrlText))
        {
            if (_draft.Url is null) linkHint = Loc.T("Use an https link.");
            else if (MeetingLink.Find(_draft.Url, null, null, null) is null) linkHint = Loc.T("Not a recognized meeting link: Join won't be offered.");
        }
        LinkHint.Text = linkHint ?? "";
        SetVisible(LinkHint, linkHint is not null);

        var repeats = _draft.RepeatRule is not (CalendarEventDraft.Repeat.None or CalendarEventDraft.Repeat.Custom);
        SetVisible(EndRepeatBox, repeats);
        SetVisible(RepeatEndPicker, repeats && _draft.RepeatEnd is not null);
        SetVisible(RepeatEndHint, repeats && _draft.RepeatEnd is { } repeatEnd && repeatEnd < Day(_draft.Start));
    }

    /// <summary>The event's day: start and end move to it, keeping their times.</summary>
    private void MoveToDay(DateTimeOffset? picked)
    {
        if (picked is not { } day)
        {
            // A date can't be cleared.
            Sync(() => DayPicker.Date = Day(_draft.Start));
            return;
        }
        var days = (day.ToLocalTime().Date - _draft.Start.LocalDateTime.Date).Days;
        if (days == 0) return;
        _draft.Start = new DateTimeOffset(_draft.Start.LocalDateTime.AddDays(days));
        _draft.End = new DateTimeOffset(_draft.End.LocalDateTime.AddDays(days));
    }

    /// <summary>Repeat; None and Custom clear the series end so a hidden date can't block Save.</summary>
    private void SetRepeat()
    {
        if (RepeatBox.SelectedIndex < 0) return;
        _draft.RepeatRule = (CalendarEventDraft.Repeat)RepeatBox.SelectedIndex;
        if (_draft.RepeatRule is CalendarEventDraft.Repeat.None or CalendarEventDraft.Repeat.Custom) _draft.RepeatEnd = null;
        FillRepeatEnd();
    }

    /// <summary>End Repeat: On date starts one month after the event.</summary>
    private void SetEndsOnDate()
    {
        var onDate = EndRepeatBox.SelectedIndex == 1;
        if (EndRepeatBox.SelectedIndex < 0 || onDate == (_draft.RepeatEnd is not null)) return;
        _draft.RepeatEnd = onDate ? new DateTimeOffset(_draft.Start.LocalDateTime.AddMonths(1)) : null;
        FillRepeatEnd();
    }

    // Actions

    private void Save()
    {
        if (_editing is not null && _isRecurring) Ask(Pending.Save);
        else _ = RunAsync(() => _model.SaveCalendarEventAsync(_draft with { }, _editing, CalendarSpan.ThisEvent));
    }

    /// <summary>
    /// The macOS alert in place of the form. Save: This Event Only only while Repeat and End are as loaded (one occurrence
    /// can't change the rule), then This and Future Events. Delete: both for a repeating event, else Delete.
    /// </summary>
    private void Ask(Pending action)
    {
        if (_loaded is null || _busy) return;
        _pending = action;
        var both = action == Pending.Save
            ? _draft.RepeatRule == _loaded.RepeatRule && _draft.RepeatEnd == _loaded.RepeatEnd
            : _isRecurring;
        Title = action == Pending.Save ? Loc.T("Save changes to a repeating event?")
            : _isRecurring ? Loc.T("Delete a repeating event?") : Loc.T("Delete this event?");
        if (both)
        {
            PrimaryButtonText = Loc.T("This Event Only");
            _primarySpan = CalendarSpan.ThisEvent;
            SecondaryButtonText = Loc.T("This and Future Events");
            _secondarySpan = CalendarSpan.ThisAndFuture;
        }
        else
        {
            PrimaryButtonText = action == Pending.Save ? Loc.T("This and Future Events") : Loc.T("Delete");
            _primarySpan = action == Pending.Save ? CalendarSpan.ThisAndFuture : CalendarSpan.ThisEvent;
            SecondaryButtonText = "";
        }
        CloseButtonText = Loc.T("Cancel");
        DefaultButton = ContentDialogButton.Close;
        Body.Visibility = Visibility.Collapsed;
        Update();
    }

    private void Answer(CalendarSpan span)
    {
        var action = _pending;
        ShowForm();
        if (action == Pending.Save) _ = RunAsync(() => _model.SaveCalendarEventAsync(_draft with { }, _editing, span));
        else if (action == Pending.Delete && _editing is { } editing) _ = RunAsync(() => _model.DeleteCalendarEventAsync(editing, span));
    }

    /// <summary>A store call (the model's never throws): closes on success, else shows its error.</summary>
    private async Task RunAsync(Func<Task<string?>> call)
    {
        _busy = true;
        SetVisible(ErrorText, false);
        Update();
        var error = await call();
        _busy = false;
        if (error is null)
        {
            _done = true;
            Hide();
            return;
        }
        ErrorText.Text = error;
        SetVisible(ErrorText, true);
        Update();
    }

    private static DateTimeOffset Day(DateTimeOffset date) => new(date.LocalDateTime.Date);

    private static TimeSpan TimeOfDay(DateTimeOffset date) => new(date.LocalDateTime.Hour, date.LocalDateTime.Minute, 0);

    /// <summary>A time of day on the start's day, so the event never runs overnight.</summary>
    private DateTimeOffset AtTime(TimeSpan? time, DateTimeOffset current) =>
        time is { } t ? new DateTimeOffset(_draft.Start.LocalDateTime.Date + new TimeSpan(t.Hours, t.Minutes, 0)) : current;

    private static void SetVisible(UIElement element, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != visibility) element.Visibility = visibility;
    }
}
