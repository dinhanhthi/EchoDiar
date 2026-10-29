using System.ComponentModel;
using EchoDiar.App.ViewModels;
using EchoDiar.Transcribe;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// Live captions on the recording page (macOS LiveTranscriptView.swift): <see cref="AppViewModel.RecordingCaptions"/>'s
/// state and lines. Each line ("Me" or "Others", then its text) is built in code once, in a plain panel: at most
/// <see cref="RecordingTranscriber.MaxLines"/>, so nothing is virtualized or recycled. The open chunks' greyed
/// tentative text follows in its own panel. The newest line is scrolled into view unless the user scrolled up.
/// </summary>
public sealed partial class LiveTranscriptView : UserControl
{
    /// <summary>How close to the end still counts as "at the newest line".</summary>
    private const double BottomSlack = 24;

    private readonly AppViewModel _model;

    /// <summary>The lines shown, oldest first, with their rows in <c>LinesPanel</c>.</summary>
    private readonly List<(RecordingTranscriber.Line Line, FrameworkElement Row)> _rows = [];

    /// <summary>The tentative text shown in <c>TentativePanel</c>; a snapshot has a new list only on change.</summary>
    private IReadOnlyList<RecordingTranscriber.Tentative> _tentative = [];

    /// <summary>The view is at the newest line: new lines scroll it along.</summary>
    private bool _followsNewest = true;

    public LiveTranscriptView(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        AutomationProperties.SetName(Scroller, Loc.T("Live transcript"));
        Scroller.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate) _followsNewest = Scroller.VerticalOffset >= Scroller.ScrollableHeight - BottomSlack;
        };
        // After a layout pass: the scroll range includes the new lines, or the new viewport.
        LinesPanel.SizeChanged += (_, _) => FollowNewest();
        TentativePanel.SizeChanged += (_, _) => FollowNewest();
        Scroller.SizeChanged += (_, _) => FollowNewest();
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            Show(_model.RecordingCaptions);
        };
        Unloaded += (_, _) => _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.RecordingCaptions)) Show(_model.RecordingCaptions);
    }

    private void Show(RecordingTranscriber.Snapshot? snapshot)
    {
        if (snapshot is null)
        {
            ClearRows();
            Scroller.Visibility = Visibility.Collapsed;
            return;
        }
        ShowState(snapshot);
        ShowLines(snapshot.Lines);
        ShowTentative(snapshot.Tentative);
        Scroller.Visibility = snapshot.Lines.Count > 0 || snapshot.Tentative.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>The state line; why the transcriber stopped as its tooltip.</summary>
    private void ShowState(RecordingTranscriber.Snapshot snapshot)
    {
        var failed = snapshot.State == RecordingTranscriber.TranscriberState.Failed;
        RecordingCard.SetText(StatusText, snapshot.State switch
        {
            RecordingTranscriber.TranscriberState.Loading => Loc.T("Live transcript: loading the model…"),
            RecordingTranscriber.TranscriberState.Paused => Loc.T("Live transcript: paused"),
            RecordingTranscriber.TranscriberState.Listening =>
                snapshot.Lines.Count == 0 && snapshot.Tentative.Count == 0
                    ? Loc.T("Live transcript: listening…")
                    : Loc.T("Live transcript"),
            _ => Loc.T("Live transcript stopped. The transcript is made after Stop."),
        });
        var textStyle = (Style)Resources[failed ? "LiveStatusErrorText" : "LiveStatusText"];
        if (StatusText.Style != textStyle) StatusText.Style = textStyle;
        var iconStyle = (Style)Resources[failed ? "LiveStatusErrorIcon" : "LiveStatusIcon"];
        if (StatusIcon.Style != iconStyle) StatusIcon.Style = iconStyle;
        var tip = failed ? snapshot.Error : null;
        if (!Equals(ToolTipService.GetToolTip(StatusPanel), tip)) ToolTipService.SetToolTip(StatusPanel, tip);
    }

    /// <summary>
    /// Drops the rows the snapshot no longer has (the oldest) and adds its new ones. Another recording's transcriber
    /// numbers its lines from 0 again: its line with the last shown id is another object, so every row is rebuilt.
    /// </summary>
    private void ShowLines(IReadOnlyList<RecordingTranscriber.Line> lines)
    {
        if (_rows.Count > 0 && IsAnotherTranscriber(lines)) ClearRows();
        while (_rows.Count > 0 && lines.Count > 0 && _rows[0].Line.Id < lines[0].Id)
        {
            _rows.RemoveAt(0);
            LinesPanel.Children.RemoveAt(0);
        }
        var lastShown = _rows.Count > 0 ? _rows[^1].Line.Id : -1;
        foreach (var line in lines)
        {
            if (line.Id <= lastShown) continue;
            var row = Row(line.Speaker, line.Text, "LiveLineText");
            _rows.Add((line, row));
            LinesPanel.Children.Add(row);
        }
    }

    /// <summary>Rebuilds the tentative rows when the snapshot has another list (a new one only on change).</summary>
    private void ShowTentative(IReadOnlyList<RecordingTranscriber.Tentative> tentative)
    {
        if (ReferenceEquals(tentative, _tentative)) return;
        _tentative = tentative;
        TentativePanel.Children.Clear();
        foreach (var item in tentative) TentativePanel.Children.Add(Row(item.Speaker, item.Text, "LiveTentativeText"));
    }

    private bool IsAnotherTranscriber(IReadOnlyList<RecordingTranscriber.Line> lines)
    {
        if (lines.Count == 0) return true;
        var last = _rows[^1].Line;
        if (last.Id > lines[^1].Id) return true;
        foreach (var line in lines)
        {
            if (line.Id == last.Id) return !ReferenceEquals(line, last);
        }
        // Every shown line was dropped as the oldest: the trim above removes them.
        return false;
    }

    private void ClearRows()
    {
        _rows.Clear();
        LinesPanel.Children.Clear();
        _tentative = [];
        TentativePanel.Children.Clear();
        _followsNewest = true;
    }

    /// <summary>
    /// The speaker in the transcript's color (the macOS labels "Me" and "Others"), then the selectable text in
    /// <paramref name="textStyle"/>.
    /// </summary>
    private Grid Row(RecordingTranscriber.Speaker who, string lineText, string textStyle)
    {
        var me = who == RecordingTranscriber.Speaker.Me;
        var speaker = new TextBlock
        {
            Text = Loc.T(me ? "Me" : "Others") + ":",
            Style = (Style)Resources[me ? "LiveSpeakerMe" : "LiveSpeakerOthers"],
        };
        var text = new TextBlock { Text = lineText.Trim(), Style = (Style)Resources[textStyle] };
        Grid.SetColumn(text, 1);
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(speaker);
        row.Children.Add(text);
        return row;
    }

    private void FollowNewest()
    {
        if (!_followsNewest || (_rows.Count == 0 && _tentative.Count == 0) || _model.IsClosing) return;
        Scroller.ChangeView(null, Scroller.ScrollableHeight, null, disableAnimation: true);
    }
}
