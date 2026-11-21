using System.ComponentModel;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Model;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace EchoDiar.App.Views;

/// <summary>
/// The recording's Chat tab (macOS LiveChatView.swift): questions about the meeting so far
/// (<see cref="AppViewModel.AskLive"/>), answered with the catch-up provider. <see cref="AppViewModel.LiveChatMessages"/>
/// (questions in a bubble, answers rendered by <see cref="MarkdownView"/> with their model), then
/// <see cref="AppViewModel.LiveChatRun"/>: its question, the answer streamed so far (or a spinner with its status line),
/// its error. <c>[mm:ss]</c> citations keep the link style but open nothing, as macOS: the live transcript has no
/// scroll-to-time hook. Under it the question row, or <see cref="AppViewModel.LiveChatDisabledReason"/>.
/// </summary>
public sealed partial class LiveChatView : UserControl
{
    /// <summary>Citations link-styled but inert; web links plain text.</summary>
    private static readonly MarkdownLinks Links = new(Seek: _ => { });

    private readonly AppViewModel _model;
    private bool _framed = true;

    /// <summary>The messages and the run shown: the list is rebuilt when either is another one.</summary>
    private IReadOnlyList<ChatLog.Message>? _messagesShown;
    private LiveChatRun? _runShown;

    /// <summary>Where the run's answer, spinner and error go, after its question; what it shows as last rendered.</summary>
    private StackPanel? _streamSlot;
    private string? _streamRendered;

    public LiveChatView(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        SendButton.Click += (_, _) => Ask();
        StopButton.Click += (_, _) => _model.StopLiveChat();
        QuestionBox.PreviewKeyDown += OnQuestionKeyDown;
        QuestionBox.TextChanged += (_, _) => UpdateSendButton();
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _model.LiveChatTextChanged += UpdateStream;
            _model.ProvidersModel.Changed += UpdateInput;
            UpdateChat();
            UpdateInput();
            // The cursor in the question when the tab opens, as macOS (not while a sidebar card is being renamed).
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (IsLoaded && _model.RenamingConversation is null && QuestionRow.Visibility == Visibility.Visible)
                {
                    QuestionBox.Focus(FocusState.Programmatic);
                }
            });
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.LiveChatTextChanged -= UpdateStream;
            _model.ProvidersModel.Changed -= UpdateInput;
        };
    }

    /// <summary>The messages and the question row in cards of their own; false inside a panel that draws one (side by side).</summary>
    public bool Framed
    {
        get => _framed;
        set
        {
            _framed = value;
            foreach (var pane in new[] { ChatPane, InputPane })
            {
                if (value) pane.Style = (Style)Resources["PaneStyle"];
                else pane.ClearValue(FrameworkElement.StyleProperty);
            }
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.LiveChatMessages):
            case nameof(AppViewModel.LiveChatRun):
                UpdateChat();
                UpdateInput();
                break;
            case nameof(AppViewModel.RecordingCaptions):
            case nameof(AppViewModel.Settings):
                UpdateInput();
                break;
        }
    }

    /// <summary>
    /// The messages and the run, rebuilt when the chat or the run is another one; the run's answer alone follows its
    /// updates. A note while there are none.
    /// </summary>
    private void UpdateChat()
    {
        var messages = _model.LiveChatMessages;
        var run = _model.LiveChatRun;
        var empty = messages.Count == 0 && run is null;
        EmptyText.Visibility = Shown(empty);
        ChatScroller.Visibility = Shown(!empty);
        if (!ReferenceEquals(messages, _messagesShown) || !ReferenceEquals(run, _runShown))
        {
            _messagesShown = messages;
            _runShown = run;
            _streamRendered = null;
            _streamSlot = null;
            MessagesPanel.Children.Clear();
            foreach (var message in messages) MessagesPanel.Children.Add(MessageView(message.Role, message.Text, message.Model));
            if (run is not null)
            {
                MessagesPanel.Children.Add(MessageView(ChatRole.User, run.Question, null));
                _streamSlot = new StackPanel { Spacing = 6 };
                MessagesPanel.Children.Add(_streamSlot);
            }
            ScrollChatToEnd();
        }
        UpdateStream();
    }

    /// <summary>
    /// The answer streamed so far, or a spinner with what the job is doing while it runs without text; its error under it.
    /// </summary>
    private void UpdateStream()
    {
        if (_streamSlot is null || _model.LiveChatRun is not { } run) return;
        var status = run.Text.Length > 0 ? "" : run.IsRunning ? "\u0001" + run.StatusLine : "\u0002";
        var key = status + "\u0000" + run.Text + "\u0000" + run.Error;
        if (key == _streamRendered) return;
        _streamRendered = key;
        _streamSlot.Children.Clear();
        if (run.Text.Length > 0)
        {
            _streamSlot.Children.Add(MessageView(ChatRole.Assistant, run.Text, null));
        }
        else if (run.IsRunning)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new ProgressRing { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, IsActive = true });
            if (run.StatusLine is { } line)
            {
                row.Children.Add(new TextBlock
                {
                    Text = line, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Resources["CaptionStyle"],
                });
            }
            _streamSlot.Children.Add(row);
        }
        if (run.Error is { } error)
        {
            _streamSlot.Children.Add(new TextBlock { Text = error, Style = (Style)Resources["ErrorTextStyle"] });
        }
        ScrollChatToEnd();
    }

    /// <summary>As the conversation chat's messages: a question in a bubble at the trailing edge; an answer's Markdown with its model's badge.</summary>
    private UIElement MessageView(ChatRole role, string text, string? model)
    {
        if (role == ChatRole.User)
        {
            return new Border
            {
                Style = (Style)Resources["UserBubbleStyle"],
                Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            };
        }
        var answer = new StackPanel { Spacing = 6 };
        var markdown = new RichTextBlock();
        MarkdownView.Render(markdown, text, Links);
        answer.Children.Add(markdown);
        if (model is { Length: > 0 })
        {
            answer.Children.Add(new Border
            {
                Style = (Style)Resources["BadgeStyle"],
                Child = new TextBlock
                {
                    Text = model, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Resources["CaptionStyle"],
                },
            });
        }
        return answer;
    }

    /// <summary>
    /// The question row (Stop while an answer is written, disabled once asked), or why no question can be asked now. While
    /// an answer is written the reason is that answer: the row stays, with Stop.
    /// </summary>
    private void UpdateInput()
    {
        if (_model.IsClosing) return;
        var run = _model.LiveChatRun;
        var running = run is { IsRunning: true };
        var reason = running ? null : _model.LiveChatDisabledReason;
        var text = reason ?? "";
        if (ReasonText.Text != text) ReasonText.Text = text;
        ReasonText.Visibility = Shown(reason is not null);
        QuestionRow.Visibility = Shown(reason is null);
        SendButton.Visibility = Shown(!running);
        StopButton.Visibility = Shown(running);
        StopButton.IsEnabled = run is not { Stopping: true };
        UpdateSendButton();
    }

    private void UpdateSendButton() => SendButton.IsEnabled = QuestionBox.Text.Trim().Length > 0;

    /// <summary>Once laid out, so the new height is known.</summary>
    private void ScrollChatToEnd() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (!_model.IsClosing) ChatScroller.ChangeView(null, ChatScroller.ScrollableHeight, null, disableAnimation: true);
    });

    /// <summary>Enter sends; Shift+Enter starts a new line (the box accepts returns).</summary>
    private void OnQuestionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || ConversationListView.IsKeyDown(VirtualKey.Shift)) return;
        e.Handled = true;
        Ask();
    }

    /// <summary>Asks only when a question can be asked now, so an Enter while an answer is written keeps the draft.</summary>
    private void Ask()
    {
        var question = QuestionBox.Text.Trim();
        if (question.Length == 0 || _model.LiveChatDisabledReason is not null) return;
        var before = _model.LiveChatRun;
        _model.AskLive(question);
        // Asked: a new run started.
        if (!ReferenceEquals(_model.LiveChatRun, before)) QuestionBox.Text = "";
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
