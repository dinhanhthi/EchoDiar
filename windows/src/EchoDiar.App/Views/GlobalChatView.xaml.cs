using System.Diagnostics;
using System.Globalization;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Model;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using VirtualKey = Windows.System.VirtualKey;

namespace EchoDiar.App.Views;

/// <summary>
/// The chat across the library (macOS GlobalChatView.swift): questions answered from the indexed conversations, each
/// answer with its numbered sources under it. <c>[n mm:ss]</c> / <c>[n]</c> citations and the sources open the
/// conversation at that moment or its summary, the chat staying beside it (<see cref="AppViewModel.OpenReference"/>).
/// Laid out like the AI panel's Chat tab. The state is <see cref="LibraryChatViewModel"/>; the question runs in
/// <see cref="AppViewModel"/>, so it goes on when the chat closes. Answers are Markdown rendered by
/// <see cref="MarkdownView"/> with their saved references only (never null, so a bare <c>[mm:ss]</c> stays plain text);
/// web links open once their destination is confirmed. Attached while it is in the window.
/// </summary>
public sealed partial class GlobalChatView : UserControl
{
    private readonly AppViewModel _model;
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly DispatcherQueueTimer _focusTimer;
    private LibraryChatViewModel? _chat;

    /// <summary>What was rendered last, so an update that changes nothing renders nothing.</summary>
    private (IReadOnlyList<ChatLog.Message> Chat, bool HasJob, string? Question, string Sources)? _chatRendered;
    private string? _streamRendered;
    private string? _historyKey;

    /// <summary>The streamed answer at the end of the messages: its status row, or its text once the first arrives.</summary>
    private StackPanel? _streamSlot;

    public GlobalChatView(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(2);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => Toast.Visibility = Visibility.Collapsed;
        _focusTimer = DispatcherQueue.CreateTimer();
        _focusTimer.Interval = TimeSpan.FromMilliseconds(50);
        _focusTimer.IsRepeating = false;
        _focusTimer.Tick += (_, _) =>
        {
            if (_chat is { RagEnabled: true } && QuestionBox.IsEnabled) QuestionBox.Focus(FocusState.Programmatic);
        };

        ToolTipService.SetToolTip(LanguageButton,
            Loc.T("Language of summaries and answers (Auto = the transcript's language for summaries, the question's for answers)"));
        ModelMenu.Opening += (_, _) => BuildModelMenu();
        LanguageMenu.Opening += (_, _) => BuildLanguageMenu();
        TurnOnButton.Click += (_, _) => OpenRagSettings();
        IndexingSettingsButton.Click += (_, _) => OpenRagSettings();
        BusyStopButton.Click += (_, _) => _chat?.Stop();
        AskStopButton.Click += (_, _) => _chat?.Stop();
        SendButton.Click += (_, _) => Ask();
        FreshChatButton.Click += (_, _) => _chat?.StartFreshChat();
        HistoryButton.Click += (_, _) => ShowHistory(HistoryButton.IsChecked == true);
        CloseHistoryButton.Click += (_, _) => ShowHistory(false);
        ClearHistoryButton.Click += (_, _) => _ = ClearHistoryAsync();
        QuestionBox.PreviewKeyDown += OnQuestionKeyDown;
        QuestionBox.TextChanged += (_, _) => UpdateActions();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        if (_chat is not null) return;
        _chat = new LibraryChatViewModel(_model);
        _chat.Changed += Update;
        _chat.Attach();
        Update();
        // Opening the chat puts the cursor in the question.
        _focusTimer.Start();
    }

    private void Detach()
    {
        _toastTimer.Stop();
        _focusTimer.Stop();
        if (_chat is not null)
        {
            _chat.Changed -= Update;
            _chat.Detach();
        }
        _chat = null;
        _chatRendered = null;
        _streamRendered = null;
        _historyKey = null;
        _streamSlot = null;
    }

    /// <summary>Everything from the state: RAG off or on, the messages, the history, the error, the bottom bar.</summary>
    private void Update()
    {
        if (_model.IsClosing || _chat is null) return;
        var on = _chat.RagEnabled;
        if (on && ChatContent.Visibility != Visibility.Visible) _focusTimer.Start();
        IntroPane.Visibility = Shown(!on);
        ChatContent.Visibility = Shown(on);
        if (!on) return;
        UpdateChat();
        UpdateHistory();
        var error = _chat.Error;
        ErrorText.Text = error ?? "";
        ToolTipService.SetToolTip(ErrorText, error);
        ErrorText.Visibility = Shown(error is not null);
        UpdateBottomBar();
        UpdateActions();
    }

    /// <summary>Settings › RAG (Phase 27.3), which turns RAG on and picks the embedder.</summary>
    private void OpenRagSettings() => _model.OpenSettings("rag");

    // Chat

    /// <summary>
    /// The messages and the answer being written, rebuilt when the chat, the question or the library changes (a source's
    /// title, a deleted source); the streamed answer alone follows the job's updates. A hint while there are none.
    /// </summary>
    private void UpdateChat()
    {
        if (_chat is null) return;
        var job = _chat.ShownJob;
        var empty = _chat.Chat.Count == 0 && job is null;
        EmptyHint.Visibility = Shown(empty);
        ChatScroller.Visibility = Shown(!empty);
        var key = (_chat.Chat, job is not null, job?.Question, SourcesKey(_chat));
        if (_chatRendered != key)
        {
            _chatRendered = key;
            _streamRendered = null;
            _streamSlot = null;
            MessagesPanel.Children.Clear();
            foreach (var message in _chat.Chat)
            {
                MessagesPanel.Children.Add(MessageView(message.Role, message.Text, message.Model, message.References ?? []));
            }
            if (job is not null)
            {
                MessagesPanel.Children.Add(MessageView(ChatRole.User, job.Question ?? "", null, []));
                _streamSlot = new StackPanel();
                MessagesPanel.Children.Add(_streamSlot);
            }
            ScrollChatToEnd();
        }
        if (job is not null) UpdateStream(job);
    }

    /// <summary>What the source rows show of their conversations: each one's title and date, or that it was deleted.</summary>
    private static string SourcesKey(LibraryChatViewModel chat) => string.Join("\u0001", chat.Chat
        .SelectMany(m => m.References ?? [])
        .Select(r => chat.Conversation(r.ConversationID) is { } c ? $"{c.Id}\u0002{c.Title}\u0002{c.CreatedAt.ToUnixTimeSeconds()}" : "-"));

    /// <summary>A spinner with the status until the first text, then the answer so far with the model's badge.</summary>
    private void UpdateStream(AIJob job)
    {
        if (_streamSlot is null) return;
        var key = job.Text.Length == 0 ? "\u0001" + job.Status : job.Text;
        if (key == _streamRendered) return;
        _streamRendered = key;
        _streamSlot.Children.Clear();
        if (job.Text.Length == 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new ProgressRing { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, IsActive = true });
            if (job.Status is { } status)
            {
                row.Children.Add(new TextBlock
                {
                    Text = status, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Resources["CaptionStyle"],
                });
            }
            _streamSlot.Children.Add(row);
        }
        else
        {
            // The sources are saved with the answer: its citations link once it is saved.
            _streamSlot.Children.Add(MessageView(ChatRole.Assistant, job.Text, _chat?.CurrentModel, [], streaming: true));
        }
        ScrollChatToEnd();
    }

    /// <summary>
    /// A question in a bubble at the trailing edge; an answer's Markdown, its sources, then its model's badge and Copy.
    /// <paramref name="references"/> is never null here: an answer without sources keeps its <c>[n]</c> and
    /// <c>[mm:ss]</c> as plain text.
    /// </summary>
    private UIElement MessageView(
        ChatRole role, string text, string? model, IReadOnlyList<ChatLog.Reference> references, bool streaming = false)
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
        MarkdownView.Render(markdown, text, Links(references));
        answer.Children.Add(markdown);
        if (references.Count > 0) answer.Children.Add(SourcesView(references));
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (model is { Length: > 0 })
        {
            footer.Children.Add(new Border
            {
                Style = (Style)Resources["BadgeStyle"],
                Child = new TextBlock
                {
                    Text = model, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Resources["CaptionStyle"],
                },
            });
        }
        if (!streaming)
        {
            var copy = new Button
            {
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Padding = new Thickness(6, 2, 6, 2),
                Content = Icon(""),
            };
            AutomationProperties.SetName(copy, Loc.T("Copy"));
            ToolTipService.SetToolTip(copy, Loc.T("Copy the answer"));
            copy.Click += (_, _) => CopyAnswer(text);
            footer.Children.Add(copy);
        }
        if (footer.Children.Count > 0) answer.Children.Add(footer);
        return answer;
    }

    /// <summary>The answer's sources, "n · title · date": a click opens the conversation's summary; a deleted one is off.</summary>
    private UIElement SourcesView(IReadOnlyList<ChatLog.Reference> references)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Sources"), Style = (Style)Resources["CaptionStyle"], Margin = new Thickness(8, 0, 8, 2),
        });
        foreach (var reference in references) panel.Children.Add(SourceRow(reference));
        return panel;
    }

    private UIElement SourceRow(ChatLog.Reference reference)
    {
        var conversation = _chat?.Conversation(reference.ConversationID);
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        // Titles may come from a model: plain text, never Markdown.
        line.Children.Add(new TextBlock
        {
            Text = $"{reference.Number} · {conversation?.Title ?? reference.Title}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            MaxLines = 1, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (conversation is not null)
        {
            line.Children.Add(new TextBlock
            {
                Text = " · " + conversation.CreatedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
                Style = (Style)Resources["CaptionStyle"],
            });
        }
        var row = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4),
            Content = line,
            IsEnabled = conversation is not null,
        };
        AutomationProperties.SetName(row, $"{reference.Number} · {conversation?.Title ?? reference.Title}");
        ToolTipService.SetToolTip(row, Loc.T(conversation is null ? "This conversation was deleted" : "Open this conversation"));
        var id = reference.ConversationID;
        row.Click += (_, _) => _chat?.OpenReference(id, null);
        return row;
    }

    /// <summary>Once laid out, so the new height is known.</summary>
    private void ScrollChatToEnd() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (_chat is not null && !_model.IsClosing) ChatScroller.ChangeView(null, ChatScroller.ScrollableHeight, null, disableAnimation: true);
    });

    /// <summary>
    /// Enter sends; Shift+Enter starts a new line (the box accepts returns). Esc closes the History panel, or with no
    /// question typed goes back (the box has the focus whenever the chat shows, so the window's Esc never gets it).
    /// </summary>
    private void OnQuestionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when !ConversationListView.IsKeyDown(VirtualKey.Shift):
                e.Handled = true;
                Ask();
                break;
            case VirtualKey.Escape when HistoryPanel.Visibility == Visibility.Visible:
                e.Handled = true;
                ShowHistory(false);
                break;
            case VirtualKey.Escape when QuestionBox.Text.Length == 0:
                e.Handled = true;
                _model.GoBack();
                break;
        }
    }

    private void Ask()
    {
        if (_chat is not null && _chat.Ask(QuestionBox.Text)) QuestionBox.Text = "";
    }

    // History

    /// <summary>Opens or closes the History panel.</summary>
    private void ShowHistory(bool show)
    {
        HistoryPanel.Visibility = Shown(show);
        HistoryButton.IsChecked = show;
    }

    /// <summary>Esc: closes the History panel when it is open; false when it was not.</summary>
    internal bool CloseHistory()
    {
        if (HistoryPanel.Visibility != Visibility.Visible) return false;
        ShowHistory(false);
        return true;
    }

    /// <summary>
    /// The open chat first, highlighted (its title, else its first question, "New chat" while empty), then the earlier
    /// chats, newest first: a click resumes one, Remove on hover deletes it; all off while an answer streams.
    /// </summary>
    private void UpdateHistory()
    {
        if (_chat is null) return;
        var busy = _chat.RunningJob is not null || _model.ChatChanging;
        ClearHistoryButton.IsEnabled = _chat.Archives.Count > 0 && !busy;
        var title = _chat.ChatTitle ?? _chat.Chat.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? Loc.T("New chat");
        var key = string.Join("\u0001", new[] { title, _chat.Chat.Count.ToString(CultureInfo.InvariantCulture), busy.ToString() }
            .Concat(_chat.Archives.Select(a => $"{a.Id}\u0002{a.Title}\u0002{a.MessageCount}\u0002{a.ArchivedAt.ToUnixTimeSeconds()}")));
        if (key == _historyKey) return;
        _historyKey = key;
        HistoryList.Children.Clear();

        var current = new StackPanel { Spacing = 2 };
        // The title may come from a model: plain text, never Markdown.
        current.Children.Add(new TextBlock { Text = title, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var count = _chat.Chat.Count;
        current.Children.Add(new TextBlock
        {
            Text = count == 0 ? Loc.T("Current") : $"{Loc.T("Current")} · {Loc.Format("%lld messages", count)}",
            Style = (Style)Resources["CaptionStyle"],
        });
        HistoryList.Children.Add(new Border { Style = (Style)Resources["CurrentChatStyle"], Child = current });

        foreach (var entry in _chat.Archives) HistoryList.Children.Add(HistoryRow(entry, busy));
        if (_chat.Archives.Count == 0)
        {
            HistoryList.Children.Add(new TextBlock
            {
                Text = Loc.T("No earlier chats"), Style = (Style)Resources["CaptionStyle"],
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    /// <summary>An earlier chat: its title and when it was archived; Remove shows while the pointer or the focus is on it.</summary>
    private UIElement HistoryRow(ChatArchive.Entry entry, bool busy)
    {
        var text = new StackPanel { Spacing = 2 };
        // The title may come from a model: plain text, never Markdown.
        text.Children.Add(new TextBlock { Text = entry.Title, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = Loc.Format("%@ · %lld messages",
                entry.ArchivedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), entry.MessageCount),
            Style = (Style)Resources["CaptionStyle"],
        });
        var row = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 32, 6),
            Content = text,
            IsEnabled = !busy,
        };
        AutomationProperties.SetName(row, entry.Title);
        var id = entry.Id;
        row.Click += (_, _) =>
        {
            _chat?.Resume(id);
            ShowHistory(false);
        };
        var remove = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(6, 4, 6, 4),
            Content = Icon(""),
            Opacity = 0,
            IsEnabled = !busy,
        };
        AutomationProperties.SetName(remove, Loc.T("Remove"));
        ToolTipService.SetToolTip(remove, Loc.T("Remove this chat"));
        remove.Click += (_, _) => _chat?.DeleteChat(id);
        var grid = new Grid();
        grid.Children.Add(row);
        grid.Children.Add(remove);
        var pointerOver = false;
        void Reveal() => remove.Opacity = pointerOver || row.FocusState != FocusState.Unfocused || remove.FocusState != FocusState.Unfocused ? 1 : 0;
        grid.PointerEntered += (_, _) =>
        {
            pointerOver = true;
            Reveal();
        };
        grid.PointerExited += (_, _) =>
        {
            pointerOver = false;
            Reveal();
        };
        row.GotFocus += (_, _) => Reveal();
        row.LostFocus += (_, _) => Reveal();
        remove.GotFocus += (_, _) => Reveal();
        remove.LostFocus += (_, _) => Reveal();
        return grid;
    }

    /// <summary>"Delete all earlier chats?": the open chat stays.</summary>
    private async Task ClearHistoryAsync()
    {
        if (_model.Confirm is not { } confirm) return;
        var chat = _chat;
        if (await confirm(Loc.T("Delete all earlier chats?"), Loc.T("The open chat stays. This can't be undone."), Loc.T("Delete")))
        {
            if (ReferenceEquals(chat, _chat)) chat?.ClearChats();
        }
    }

    // Bottom bar

    /// <summary>A job of a conversation, the index's progress, the model and language labels and the setup hints.</summary>
    private void UpdateBottomBar()
    {
        if (_chat is null) return;
        var providers = _chat.Providers;
        var title = AIMenus.ModelTitle(providers);
        ModelText.Text = title;
        AutomationProperties.SetName(ModelButton, title);
        LanguageText.Text = LanguageOptions.ShortCode(_model.Settings.SummaryLanguage);
        var current = providers.CurrentAIProvider;
        SetupHints.Visibility = Shown(current is null);
        var windowsAI = AIMenus.WindowsAIHint(providers);
        WindowsAIHint.Text = windowsAI ?? "";
        WindowsAIHint.Visibility = Shown(current is null && windowsAI is not null);

        // It blocks asking until it ends, so it says what runs and can be stopped from here.
        if (_chat.BusyElsewhere is { } job)
        {
            var conversationTitle = _model.Conversations.FirstOrDefault(c => c.Id == job.ConversationId)?.Title ?? Loc.T("another conversation");
            BusyText.Text = job.Kind == AIJobKind.Summary
                ? Loc.Format("Summarizing %@…", conversationTitle)
                : Loc.Format("Answering about %@…", conversationTitle);
            ToolTipService.SetToolTip(BusyText, BusyText.Text);
            BusyRow.Visibility = Visibility.Visible;
        }
        else
        {
            BusyRow.Visibility = Visibility.Collapsed;
        }
        UpdateIndexingLine(_chat.Status);
    }

    /// <summary>
    /// While the index is built or still empty: how far it is, or why it waits (macOS <c>indexingLine</c>). A missing
    /// embedder or confirmation stops the pass before it counts the conversations (Total stays 0).
    /// </summary>
    private void UpdateIndexingLine(RAGStatus status)
    {
        var waiting = status.Indexed == 0 && (status.Message is not null || status.NeedsConsent || status.Total > 0);
        var show = (status.Running && status.Indexed < status.Total) || (!status.Running && waiting);
        IndexingRow.Visibility = Shown(show);
        if (!show) return;
        IndexingRing.Visibility = Shown(status.Running);
        IndexingWarningIcon.Visibility = Shown(!status.Running);
        IndexingSettingsButton.Visibility = Shown(!status.Running);
        IndexingText.Text = status.Running
            ? status.Paused
                ? Loc.Format("Indexing paused at %lld of %lld", status.Indexed, status.Total)
                : Loc.Format("Indexing %lld of %lld…", status.Indexed, status.Total)
            : status.NeedsConsent
                ? Loc.T("Confirm the embedding provider in Settings › RAG.")
                : status.Message ?? Loc.T("Your conversations are not indexed yet.");
        IndexingText.MaxLines = status.Running ? 1 : 2;
        ToolTipService.SetToolTip(IndexingText, IndexingText.Text);
    }

    /// <summary>Send or Stop, enabled when a question can run; a fresh chat, not while empty or answering.</summary>
    private void UpdateActions()
    {
        if (_chat is null) return;
        var running = _chat.RunningJob is not null;
        AskStopButton.Visibility = Shown(running);
        SendButton.Visibility = Shown(!running);
        SendButton.IsEnabled = _chat.CanAsk && QuestionBox.Text.Trim().Length > 0;
        FreshChatButton.IsEnabled = _chat.Chat.Count > 0 && !running && !_model.ChatChanging;
    }

    private void BuildModelMenu()
    {
        if (_chat is null)
        {
            ModelMenu.Items.Clear();
            return;
        }
        AIMenus.FillModelMenu(ModelMenu, _chat.Providers, () => _model.OpenSettings(Services.SettingsSearch.Providers));
    }

    private void BuildLanguageMenu()
    {
        if (_chat is null)
        {
            LanguageMenu.Items.Clear();
            return;
        }
        AIMenus.FillLanguageMenu(LanguageMenu, _chat.Providers, _model.Settings.SummaryLanguage);
    }

    // Copy

    private void CopyAnswer(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        try
        {
            Clipboard.SetContent(package);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not copy: {e.GetType().Name}");
            return;
        }
        ShowToast(Loc.T("Copied"));
    }

    /// <summary>Shows <paramref name="text"/> for 2 s; a newer one replaces it and gets its own 2 s.</summary>
    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // Links

    /// <summary>
    /// Source citations of a saved answer open their conversation (at the cited moment, or its summary) while it is in the
    /// library; web links open once their destination is confirmed. No seek: a bare <c>[mm:ss]</c> names no conversation.
    /// </summary>
    private MarkdownLinks Links(IReadOnlyList<ChatLog.Reference> references) => new(
        References: references,
        OpenConversation: (id, seconds) => _chat?.OpenReference(id, seconds),
        OpenUrl: url => _ = OpenUrlAsync(url));

    /// <summary>
    /// A web link from model output opens in the browser once the dialog showing its destination is confirmed (macOS
    /// <c>WebLinkConfirmation</c>); only http(s), checked again here.
    /// </summary>
    private async Task OpenUrlAsync(Uri url)
    {
        if (_model.Confirm is not { } confirm || url.Scheme is not ("http" or "https")) return;
        var host = url.IdnHost is { Length: > 0 } name ? name : Loc.T("this link");
        if (!await confirm(Loc.Format("Open %@?", host), url.AbsoluteUri, Loc.T("Open"))) return;
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(url);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not open a link: {e.GetType().Name}");
        }
    }

    private static FontIcon Icon(string glyph) => new()
    {
        FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
        FontSize = 12,
        Glyph = glyph,
    };

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
