using System.ComponentModel;
using EchoDiar.App.ViewModels;
using EchoDiar.Transcribe;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using VirtualKey = Windows.System.VirtualKey;

namespace EchoDiar.App.Views;

/// <summary>
/// Live captions on the recording page (macOS LiveTranscriptView.swift): <see cref="AppViewModel.RecordingCaptions"/>'s
/// state and lines, as one selectable text (macOS LiveCaptionsTextView), so a drag selects across lines and Ctrl+C
/// copies them as plain text. Each line is a paragraph ("Me" or "Others" in the speaker's color, then ": " and its
/// text), built in code once: at most <see cref="RecordingTranscriber.MaxLines"/>. The open chunks' greyed tentative
/// text follows as more paragraphs. Updates edit the text in place (new lines added, dropped lines removed from the
/// front, the tentative paragraphs replaced) and keep the selection. The newest line is scrolled into view unless the
/// user scrolled up.
/// </summary>
public sealed partial class LiveTranscriptView : UserControl
{
    /// <summary>How close to the end still counts as "at the newest line".</summary>
    private const double BottomSlack = 24;

    /// <summary>Between lines, the old rows' spacing.</summary>
    private static readonly Thickness LineMargin = new(0, 0, 0, 6);

    private readonly AppViewModel _model;

    /// <summary>The lines shown, oldest first, with their paragraphs: the text's first blocks.</summary>
    private readonly List<(RecordingTranscriber.Line Line, CaptionBlock Block)> _shown = [];

    /// <summary>The paragraphs of <see cref="_tentative"/>, after the lines' ones.</summary>
    private readonly List<CaptionBlock> _tentativeBlocks = [];

    /// <summary>The tentative text shown; a snapshot has a new list only on change.</summary>
    private IReadOnlyList<RecordingTranscriber.Tentative> _tentative = [];

    /// <summary>The text's context menu: Copy, Select All.</summary>
    private readonly MenuFlyout _textMenu = new();

    /// <summary>The view is at the newest line: new lines scroll it along.</summary>
    private bool _followsNewest = true;

    /// <summary>One line's paragraph and the runs whose colors follow the theme.</summary>
    private sealed record CaptionBlock(Paragraph Paragraph, Run Name, Run Text, RecordingTranscriber.Speaker Speaker);

    /// <summary>A place in the text: a paragraph's index in its blocks and a <see cref="TextPointer.Offset"/> in it.</summary>
    private readonly record struct TextSpot(int Paragraph, int Offset);

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
        CaptionText.SizeChanged += (_, _) => FollowNewest();
        Scroller.SizeChanged += (_, _) => FollowNewest();
        CaptionText.PreviewKeyDown += OnTextPreviewKeyDown;
        CaptionText.ContextRequested += OnTextContextRequested;
        // After the theme walk, once the swatches hold the new theme's brushes.
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyColors);
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
            ClearText();
            Scroller.Visibility = Visibility.Collapsed;
            return;
        }
        ShowState(snapshot);
        ShowText(snapshot.Lines, snapshot.Tentative);
        Scroller.Visibility = snapshot.Lines.Count > 0 || snapshot.Tentative.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// The state line (how far behind decoding is when it falls behind); why the transcriber stopped as its tooltip.
    /// </summary>
    private void ShowState(RecordingTranscriber.Snapshot snapshot)
    {
        var failed = snapshot.State == RecordingTranscriber.TranscriberState.Failed;
        RecordingCard.SetText(StatusText, snapshot.State switch
        {
            RecordingTranscriber.TranscriberState.Loading => Loc.T("Live transcript: loading the model…"),
            RecordingTranscriber.TranscriberState.Paused => Loc.T("Live transcript: paused"),
            RecordingTranscriber.TranscriberState.Listening =>
                RecordingTranscriber.BehindSeconds(snapshot) is { } behind
                    ? Loc.Format("Live transcript: %lld s behind", behind)
                    : snapshot.Lines.Count == 0 && snapshot.Tentative.Count == 0
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
    /// Applies the snapshot to the text in place: drops the paragraphs of the lines it no longer has (the oldest), adds
    /// its new lines before the tentative paragraphs and rebuilds those when it has another tentative list (a new one
    /// only on change). The selection stays on the same text, see <see cref="RestoreSelection"/>. Another recording's
    /// transcriber numbers its lines from 0 again: its line with the last shown id is another object, so the whole text
    /// is rebuilt, without a selection.
    /// </summary>
    private void ShowText(IReadOnlyList<RecordingTranscriber.Line> lines, IReadOnlyList<RecordingTranscriber.Tentative> tentative)
    {
        if (_shown.Count > 0 && IsAnotherTranscriber(lines)) ClearText();
        var dropped = 0;
        while (dropped < _shown.Count && lines.Count > 0 && _shown[dropped].Line.Id < lines[0].Id) dropped++;
        var lastShown = _shown.Count > 0 ? _shown[^1].Line.Id : -1;
        var added = lines.Where(line => line.Id > lastShown).ToList();
        var tentativeChanged = !ReferenceEquals(tentative, _tentative);
        if (dropped == 0 && added.Count == 0 && !tentativeChanged) return;

        // Before any edit: a pointer into a removed paragraph means nothing afterwards.
        var selection = SavedSelection();
        var shownBefore = _shown.Count;
        var blocks = CaptionText.Blocks;
        for (var i = 0; i < dropped; i++) blocks.RemoveAt(0);
        _shown.RemoveRange(0, dropped);
        foreach (var line in added)
        {
            var block = NewBlock(line.Speaker, line.Text, tentative: false);
            blocks.Insert(_shown.Count, block.Paragraph);
            _shown.Add((line, block));
        }
        if (tentativeChanged)
        {
            _tentative = tentative;
            for (var i = 0; i < _tentativeBlocks.Count; i++) blocks.RemoveAt(blocks.Count - 1);
            _tentativeBlocks.Clear();
            foreach (var item in tentative)
            {
                var block = NewBlock(item.Speaker, item.Text, tentative: true);
                blocks.Add(block.Paragraph);
                _tentativeBlocks.Add(block);
            }
        }
        if (selection is { } saved)
        {
            // A line's paragraph moves up by the dropped ones; an unchanged tentative one also down by the added lines.
            // Replaced tentative text keeps its place, like macOS's plain offsets: it most likely became the new lines.
            int Moved(int index) => index < shownBefore || tentativeChanged ? index - dropped : index - dropped + added.Count;
            RestoreSelection(saved.Start with { Paragraph = Moved(saved.Start.Paragraph) },
                saved.End with { Paragraph = Moved(saved.End.Paragraph) });
        }
    }

    private bool IsAnotherTranscriber(IReadOnlyList<RecordingTranscriber.Line> lines)
    {
        if (lines.Count == 0) return true;
        var last = _shown[^1].Line;
        if (last.Id > lines[^1].Id) return true;
        foreach (var line in lines)
        {
            if (line.Id == last.Id) return !ReferenceEquals(line, last);
        }
        // Every shown line was dropped as the oldest: the trim in ShowText removes them.
        return false;
    }

    private void ClearText()
    {
        _shown.Clear();
        _tentative = [];
        _tentativeBlocks.Clear();
        CaptionText.Blocks.Clear();
        _followsNewest = true;
    }

    /// <summary>
    /// "Name: text": the speaker (the macOS labels "Me" and "Others") semibold in the transcript's color, then the text
    /// in the body color, or the secondary one when <paramref name="tentative"/>.
    /// </summary>
    private CaptionBlock NewBlock(RecordingTranscriber.Speaker who, string lineText, bool tentative)
    {
        var name = new Run
        {
            Text = Loc.T(who == RecordingTranscriber.Speaker.Me ? "Me" : "Others"),
            FontWeight = FontWeights.SemiBold,
            Foreground = SpeakerBrush(who),
        };
        var text = new Run { Text = ": " + lineText.Trim() };
        if (tentative) text.Foreground = TentativeSwatch.Foreground;
        var paragraph = new Paragraph { Margin = LineMargin };
        paragraph.Inlines.Add(name);
        paragraph.Inlines.Add(text);
        return new CaptionBlock(paragraph, name, text, who);
    }

    // Selection

    /// <summary>The selection's ends, first one first; null without a selection.</summary>
    private (TextSpot Start, TextSpot End)? SavedSelection()
    {
        if (CaptionText.SelectedText.Length == 0 || CaptionText.Blocks.Count == 0) return null;
        if (CaptionText.SelectionStart is not { } from || CaptionText.SelectionEnd is not { } to) return null;
        TextSpot start = Spot(from), end = Spot(to);
        return Before(end, start) ? (end, start) : (start, end);
    }

    private static bool Before(TextSpot a, TextSpot b) => a.Paragraph < b.Paragraph || (a.Paragraph == b.Paragraph && a.Offset < b.Offset);

    /// <summary><paramref name="pointer"/> as the last paragraph starting at or before it and the offset into that one.</summary>
    private TextSpot Spot(TextPointer pointer)
    {
        var blocks = CaptionText.Blocks;
        var offset = pointer.Offset;
        var index = 0;
        while (index + 1 < blocks.Count && blocks[index + 1].ContentStart.Offset <= offset) index++;
        return new TextSpot(index, Math.Max(0, offset - blocks[index].ContentStart.Offset));
    }

    /// <summary>
    /// Selects from <paramref name="start"/> to <paramref name="end"/> (paragraph indices in the edited text) unless the
    /// text kept that selection itself. An end before the first paragraph (dropped with its line) clamps to the text's
    /// start; a selection all in dropped lines is cleared; past the last paragraph clamps to the text's end.
    /// </summary>
    private void RestoreSelection(TextSpot start, TextSpot end)
    {
        if (CaptionText.Blocks.Count == 0 || end.Paragraph < 0)
        {
            if (CaptionText.SelectedText.Length > 0) CaptionText.Select(CaptionText.ContentStart, CaptionText.ContentStart);
            return;
        }
        if (start.Paragraph < 0) start = new TextSpot(0, 0);
        TextPointer from = Pointer(start), to = Pointer(end);
        if (CaptionText.SelectionStart is { } shownStart && CaptionText.SelectionEnd is { } shownEnd)
        {
            int a = shownStart.Offset, b = shownEnd.Offset;
            if (Math.Min(a, b) == from.Offset && Math.Max(a, b) == to.Offset) return;
        }
        CaptionText.Select(from, to);
    }

    /// <summary>The text position at <paramref name="spot"/>, clamped to its paragraph and to the text's end.</summary>
    private TextPointer Pointer(TextSpot spot)
    {
        var blocks = CaptionText.Blocks;
        if (spot.Paragraph >= blocks.Count) return blocks[^1].ContentEnd;
        var block = blocks[spot.Paragraph];
        var contentStart = block.ContentStart;
        if (spot.Offset <= 0) return contentStart;
        if (spot.Offset >= block.ContentEnd.Offset - contentStart.Offset) return block.ContentEnd;
        return contentStart.GetPositionAtOffset(spot.Offset, LogicalDirection.Forward) ?? block.ContentEnd;
    }

    /// <summary>
    /// Ctrl+C (and Ctrl+Insert) with a selection copies it as plain text, before the text's own copy. Not with Shift
    /// or Alt: Ctrl+Shift+C is the window's Copy as prompt.
    /// </summary>
    private void OnTextPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.C or VirtualKey.Insert) || !ConversationListView.IsKeyDown(VirtualKey.Control)
            || ConversationListView.IsKeyDown(VirtualKey.Shift) || ConversationListView.IsKeyDown(VirtualKey.Menu)) return;
        var text = CaptionText.SelectedText;
        if (text.Length == 0) return;
        e.Handled = true;
        // After the key's own handling, so a copy the text still makes cannot replace this one.
        DispatcherQueue.TryEnqueue(() => SetClipboard(text));
    }

    /// <summary>The text's menu (macOS: the standard one): Copy and Select All.</summary>
    private void OnTextContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        var hasPoint = args.TryGetPosition(CaptionText, out var point);
        _textMenu.Items.Clear();
        var copy = new MenuFlyoutItem { Text = Loc.T("Copy"), IsEnabled = CaptionText.SelectedText.Length > 0 };
        copy.Click += (_, _) =>
        {
            if (CaptionText.SelectedText is { Length: > 0 } text) SetClipboard(text);
        };
        _textMenu.Items.Add(copy);
        var selectAll = new MenuFlyoutItem { Text = Loc.T("Select All") };
        selectAll.Click += (_, _) => CaptionText.SelectAll();
        _textMenu.Items.Add(selectAll);
        _textMenu.ShowAt(CaptionText, hasPoint ? new FlyoutShowOptions { Position = point } : new FlyoutShowOptions());
    }

    private static void SetClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        try
        {
            Clipboard.SetContent(package);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"EchoDiar: could not copy the live transcript: {e}");
        }
    }

    // Colors

    /// <summary>The speaker's color from the LiveSpeakerMe and LiveSpeakerOthers swatches, in the current theme.</summary>
    private Brush SpeakerBrush(RecordingTranscriber.Speaker who) =>
        (who == RecordingTranscriber.Speaker.Me ? MeSwatch : OthersSwatch).Foreground;

    /// <summary>Light, Dark or contrast changed: the runs' brushes from the swatches again (the body text follows by itself).</summary>
    private void ApplyColors()
    {
        if (_model.IsClosing) return;
        foreach (var (_, block) in _shown) block.Name.Foreground = SpeakerBrush(block.Speaker);
        foreach (var block in _tentativeBlocks)
        {
            block.Name.Foreground = SpeakerBrush(block.Speaker);
            block.Text.Foreground = TentativeSwatch.Foreground;
        }
    }

    private void FollowNewest()
    {
        if (!_followsNewest || (_shown.Count == 0 && _tentativeBlocks.Count == 0) || _model.IsClosing) return;
        Scroller.ChangeView(null, Scroller.ScrollableHeight, null, disableAnimation: true);
    }
}
