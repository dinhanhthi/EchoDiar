using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using EchoDiar.AI;
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
/// The Summary or Chat tab of the open conversation (macOS AIPanelView.swift, with its AIModelMenu, AILanguageMenu,
/// AISetupHints and WebLinkConfirmation). The state is <see cref="AIPanelViewModel"/>; the jobs run in
/// <see cref="AppViewModel"/>, so one goes on when the page changes and another conversation shows it as busy, with
/// Stop. Answers are Markdown rendered by <see cref="MarkdownView"/> (at most every 120 ms while streaming, as the model
/// batches the text, and once more when saved); <c>[mm:ss]</c> citations call the seek action
/// <see cref="ConversationView"/> passes, web links open once their destination is confirmed. Attach when the page shows,
/// Detach when it goes.
/// </summary>
public sealed partial class AIPanelView : UserControl
{
    /// <summary>The summary languages offered (macOS <c>AILanguageMenu</c>), plus a hand-edited one.</summary>
    private static readonly (string Code, string Name)[] Languages =
        [("auto", "Auto"), ("en", "English"), ("fr", "French"), ("vi", "Vietnamese")];

    private readonly DispatcherQueueTimer _toastTimer;
    private readonly DispatcherQueueTimer _focusTimer;

    private AppViewModel? _model;
    private AIPanelViewModel? _panel;
    private Action<double>? _seek;
    private DetailTab _tab = DetailTab.Summary;

    /// <summary>What was rendered last, so an update that changes nothing renders nothing.</summary>
    private string? _summaryRendered;
    private (IReadOnlyList<ChatLog.Message> Chat, bool HasJob, string? Question)? _chatRendered;
    private string? _streamRendered;
    private string? _historyKey;

    /// <summary>The streamed answer at the end of the messages: its status row, or its text once the first arrives.</summary>
    private StackPanel? _streamSlot;

    public AIPanelView()
    {
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
            if (_model is not null && _tab == DetailTab.Chat && QuestionBox.IsEnabled) QuestionBox.Focus(FocusState.Programmatic);
        };

        ToolTipService.SetToolTip(LanguageButton,
            Loc.T("Language of summaries and answers (Auto = the transcript's language for summaries, the question's for answers)"));
        VersionMenu.Opening += (_, _) => BuildVersionMenu();
        ModelMenu.Opening += (_, _) => BuildModelMenu();
        LanguageMenu.Opening += (_, _) => BuildLanguageMenu();
        DataNotesFlyout.Opening += (_, _) => BuildDataNotes();
        BusyStopButton.Click += (_, _) => _panel?.Stop();
        SummaryStopButton.Click += (_, _) => _panel?.Stop();
        AskStopButton.Click += (_, _) => _panel?.Stop();
        SummarizeButton.Click += (_, _) => _panel?.Summarize();
        SendButton.Click += (_, _) => Ask();
        SummaryCopyButton.Click += (_, _) => CopyPrompt();
        ChatCopyButton.Click += (_, _) => CopyPrompt();
        FreshChatButton.Click += (_, _) => _panel?.StartFreshChat();
        HistoryButton.Click += (_, _) => ShowHistory(HistoryButton.IsChecked == true);
        CloseHistoryButton.Click += (_, _) => ShowHistory(false);
        ClearHistoryButton.Click += (_, _) => _ = ClearHistoryAsync();
        QuestionBox.PreviewKeyDown += OnQuestionKeyDown;
        QuestionBox.TextChanged += (_, _) => UpdateActions();
    }

    /// <summary>Shows conversation <paramref name="id"/>; <paramref name="seek"/> opens the transcript at a citation's time.</summary>
    public void Attach(AppViewModel model, Guid id, Action<double> seek)
    {
        Detach();
        _model = model;
        _seek = seek;
        _panel = new AIPanelViewModel(model, id);
        _panel.Changed += Update;
        _model.PropertyChanged += OnModelChanged;
        _panel.Attach();
        _tab = model.DetailTab == DetailTab.Chat ? DetailTab.Chat : DetailTab.Summary;
        UpdateTab();
        Update();
    }

    public void Detach()
    {
        _toastTimer.Stop();
        _focusTimer.Stop();
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        if (_panel is not null)
        {
            _panel.Changed -= Update;
            _panel.Detach();
        }
        _model = null;
        _panel = null;
        _seek = null;
        _summaryRendered = null;
        _chatRendered = null;
        _streamRendered = null;
        _historyKey = null;
        _streamSlot = null;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_model is not { IsClosing: false } model || e.PropertyName != nameof(AppViewModel.DetailTab)) return;
        if (model.DetailTab == DetailTab.Transcript) return;
        _tab = model.DetailTab;
        UpdateTab();
        Update();
    }

    /// <summary>
    /// The tab's pane and bottom bar: Summary's menus are model, "i", language; Chat's are model, language, "i", a fresh
    /// chat and History, with the question row under them. Opening Chat puts the cursor in the question.
    /// </summary>
    private void UpdateTab()
    {
        var chat = _tab == DetailTab.Chat;
        SummaryPane.Visibility = Shown(!chat);
        ChatPane.Visibility = Shown(chat);
        SummaryActions.Visibility = Shown(!chat);
        QuestionRow.Visibility = Shown(chat);
        FreshChatButton.Visibility = Shown(chat);
        HistoryButton.Visibility = Shown(chat);
        var order = chat
            ? new FrameworkElement[] { ModelButton, LanguageButton, DataNotesButton, FreshChatButton, HistoryButton }
            : [ModelButton, DataNotesButton, LanguageButton, FreshChatButton, HistoryButton];
        if (!order.SequenceEqual(MenusPanel.Children.OfType<FrameworkElement>()))
        {
            MenusPanel.Children.Clear();
            foreach (var element in order) MenusPanel.Children.Add(element);
        }
        if (chat) _focusTimer.Start();
        else ShowHistory(false);
    }

    /// <summary>Everything from the state: the panes, the error, the bottom bar.</summary>
    private void Update()
    {
        if (_model is not { IsClosing: false } || _panel is null) return;
        UpdateSummary();
        UpdateChat();
        UpdateHistory();
        var error = _panel.Error;
        ErrorText.Text = error ?? "";
        ToolTipService.SetToolTip(ErrorText, error);
        ErrorText.Visibility = Shown(error is not null);
        UpdateMenus();
        UpdateActions();
    }

    // Summary

    /// <summary>The summary being written, else the shown version with its menu; a note while there is none.</summary>
    private void UpdateSummary()
    {
        if (_panel is null) return;
        var job = _panel.SummaryJob;
        var shown = _panel.ShownSummary;
        var empty = job is null && shown is null;
        SummaryEmptyText.Text = _panel.EmptyNote ?? Loc.T("No summary yet.");
        SummaryEmptyText.Visibility = Shown(empty);
        SummaryContent.Visibility = Shown(!empty);
        VersionButton.Visibility = Shown(job is null && shown is not null);
        if (shown is not null)
        {
            var label = _panel.VersionLabel(shown);
            VersionText.Text = label;
            AutomationProperties.SetName(VersionButton, label);
        }
        // Until the first text: a remote provider sends no status before it.
        var waiting = job is { Text.Length: 0 };
        SummaryStatusPanel.Visibility = Shown(waiting);
        SummaryStatusText.Text = job?.Status ?? Loc.T("Writing the summary…");
        var text = job is not null ? job.Text : shown?.Text ?? "";
        SummaryText.Visibility = Shown(!waiting);
        if (text != _summaryRendered)
        {
            _summaryRendered = text;
            MarkdownView.Render(SummaryText, text, Links());
        }
    }

    /// <summary>The kept summaries, newest first, each with its model, date and whether it was stopped.</summary>
    private void BuildVersionMenu()
    {
        VersionMenu.Items.Clear();
        if (_panel is null) return;
        var shown = _panel.ShownSummary;
        foreach (var version in _panel.Summaries.Reverse())
        {
            var details = _panel.VersionDetails(version);
            var title = Loc.Format("Version %lld", version.Number);
            var item = new RadioMenuFlyoutItem
            {
                Text = details.Length == 0 ? title : $"{title} · {details}",
                GroupName = "SummaryVersion",
                IsChecked = version.Number == shown?.Number,
            };
            var number = version.Number;
            item.Click += (_, _) => _panel?.ShowVersion(number);
            VersionMenu.Items.Add(item);
        }
    }

    // Chat

    /// <summary>
    /// The messages and the answer being written, rebuilt when the chat or the question changes; the streamed answer
    /// alone follows the job's updates. A note while there are none.
    /// </summary>
    private void UpdateChat()
    {
        if (_panel is null) return;
        var job = _panel.QuestionJob;
        var empty = _panel.Chat.Count == 0 && job is null;
        ChatEmptyText.Text = _panel.EmptyNote ?? Loc.T("Ask a question about this conversation below.");
        ChatEmptyText.Visibility = Shown(empty);
        ChatScroller.Visibility = Shown(!empty);
        var key = (_panel.Chat, job is not null, job?.Question);
        if (_chatRendered != key)
        {
            _chatRendered = key;
            _streamRendered = null;
            _streamSlot = null;
            MessagesPanel.Children.Clear();
            foreach (var message in _panel.Chat) MessagesPanel.Children.Add(MessageView(message.Role, message.Text, message.Model));
            if (job is not null)
            {
                MessagesPanel.Children.Add(MessageView(ChatRole.User, job.Question ?? "", null));
                _streamSlot = new StackPanel();
                MessagesPanel.Children.Add(_streamSlot);
            }
            ScrollChatToEnd();
        }
        if (job is not null) UpdateStream(job);
    }

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
            _streamSlot.Children.Add(MessageView(ChatRole.Assistant, job.Text, _panel?.CurrentModel, streaming: true));
        }
        ScrollChatToEnd();
    }

    /// <summary>A question in a bubble at the trailing edge; an answer's Markdown with its model's badge and Copy.</summary>
    private UIElement MessageView(ChatRole role, string text, string? model, bool streaming = false)
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
        MarkdownView.Render(markdown, text, Links());
        answer.Children.Add(markdown);
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
                Content = new FontIcon { FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], FontSize = 12, Glyph = "" },
            };
            AutomationProperties.SetName(copy, Loc.T("Copy"));
            ToolTipService.SetToolTip(copy, Loc.T("Copy the answer"));
            copy.Click += (_, _) => CopyText(text, Loc.T("Copied"));
            footer.Children.Add(copy);
        }
        if (footer.Children.Count > 0) answer.Children.Add(footer);
        return answer;
    }

    /// <summary>Once laid out, so the new height is known.</summary>
    private void ScrollChatToEnd() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (_model is { IsClosing: false }) ChatScroller.ChangeView(null, ChatScroller.ScrollableHeight, null, disableAnimation: true);
    });

    /// <summary>Enter sends; Shift+Enter starts a new line (the box accepts returns).</summary>
    private void OnQuestionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || ConversationListView.IsKeyDown(VirtualKey.Shift)) return;
        e.Handled = true;
        Ask();
    }

    private void Ask()
    {
        if (_panel is not null && _panel.Ask(QuestionBox.Text)) QuestionBox.Text = "";
    }

    // History

    /// <summary>Opens or closes the History panel (not while an answer streams: its rows are off then).</summary>
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
        if (_panel is null) return;
        var busy = _panel.RunningQuestion is not null || _model?.ChatChanging == true;
        ClearHistoryButton.IsEnabled = _panel.Archives.Count > 0 && !busy;
        var title = _panel.ChatTitle ?? _panel.Chat.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? Loc.T("New chat");
        var key = string.Join("\u0001", new[] { title, _panel.Chat.Count.ToString(CultureInfo.InvariantCulture), busy.ToString() }
            .Concat(_panel.Archives.Select(a => $"{a.Id}\u0002{a.Title}\u0002{a.MessageCount}\u0002{a.ArchivedAt.ToUnixTimeSeconds()}")));
        if (key == _historyKey) return;
        _historyKey = key;
        HistoryList.Children.Clear();

        var current = new StackPanel { Spacing = 2 };
        // The title may come from a model: plain text, never Markdown.
        current.Children.Add(new TextBlock { Text = title, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var count = _panel.Chat.Count;
        current.Children.Add(new TextBlock
        {
            Text = count == 0 ? Loc.T("Current") : $"{Loc.T("Current")} · {Loc.Format("%lld messages", count)}",
            Style = (Style)Resources["CaptionStyle"],
        });
        HistoryList.Children.Add(new Border { Style = (Style)Resources["CurrentChatStyle"], Child = current });

        foreach (var entry in _panel.Archives) HistoryList.Children.Add(HistoryRow(entry, busy));
        if (_panel.Archives.Count == 0)
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
            _panel?.Resume(id);
            ShowHistory(false);
        };
        var remove = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(6, 4, 6, 4),
            Content = new FontIcon { FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], FontSize = 12, Glyph = "" },
            Opacity = 0,
            IsEnabled = !busy,
        };
        AutomationProperties.SetName(remove, Loc.T("Remove"));
        ToolTipService.SetToolTip(remove, Loc.T("Remove this chat"));
        remove.Click += (_, _) => _panel?.DeleteChat(id);
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
        if (_model?.Confirm is not { } confirm) return;
        var panel = _panel;
        if (await confirm(Loc.T("Delete all earlier chats?"), Loc.T("The open chat stays. This can't be undone."), Loc.T("Delete")))
        {
            if (ReferenceEquals(panel, _panel)) panel?.ClearChats();
        }
    }

    // Bottom bar

    /// <summary>The model and language labels, the "i", the setup hints and the busy line.</summary>
    private void UpdateMenus()
    {
        if (_panel is null || _model is null) return;
        var providers = _panel.Providers;
        var current = providers.CurrentAIProvider;
        var title = current is null ? Loc.T("No AI model")
            : current.Kind == AIProviderKind.InApp ? current.Name
            : _panel.CurrentModel is { Length: > 0 } model ? model : current.Name;
        ModelText.Text = title;
        AutomationProperties.SetName(ModelButton, title);
        var language = _model.Settings.SummaryLanguage;
        LanguageText.Text = LanguageOptions.ShortCode(language);
        DataNotesButton.Visibility = Shown(DataNotes().Count > 0);

        SetupHints.Visibility = Shown(current is null);
        var windowsAI = providers.WindowsAI is { Hidden: false, Reason: { } reason } ? Loc.T(reason) : null;
        WindowsAIHint.Text = windowsAI ?? "";
        WindowsAIHint.Visibility = Shown(current is null && windowsAI is not null);

        if (_panel.BusyElsewhere(_tab) is { } job)
        {
            var conversationTitle = _model.Conversations.FirstOrDefault(c => c.Id == job.ConversationId)?.Title ?? Loc.T("another conversation");
            BusyText.Text = job.ConversationId is null ? Loc.T("Answering a question about your conversations…")
                : job.Kind == AIJobKind.Summary ? Loc.Format("Summarizing %@…", conversationTitle)
                : Loc.Format("Answering about %@…", conversationTitle);
            ToolTipService.SetToolTip(BusyText, BusyText.Text);
            BusyRow.Visibility = Visibility.Visible;
        }
        else
        {
            BusyRow.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Summarize, Re-summarize or Stop; Send or Stop; Copy as prompt, a fresh chat; each enabled when it can run.</summary>
    private void UpdateActions()
    {
        if (_panel is null || _model is null) return;
        var canRun = _panel.CanRun;
        var summaryRunning = _panel.RunningSummary is not null;
        SummaryStopButton.Visibility = Shown(summaryRunning);
        SummarizeButton.Visibility = Shown(!summaryRunning);
        SummarizeButton.Content = Loc.T(_panel.Summaries.Count == 0 ? "Summarize" : "Re-summarize");
        ToolTipService.SetToolTip(SummarizeButton,
            _panel.Summaries.Count == 0 ? null : Loc.T("Make a new summary; the earlier ones stay in the version menu"));
        SummarizeButton.IsEnabled = canRun;

        var questionRunning = _panel.RunningQuestion is not null;
        AskStopButton.Visibility = Shown(questionRunning);
        SendButton.Visibility = Shown(!questionRunning);
        SendButton.IsEnabled = canRun && QuestionBox.Text.Trim().Length > 0;
        FreshChatButton.IsEnabled = _panel.Chat.Count > 0 && !questionRunning && !_model.ChatChanging;

        var hasTranscript = _panel.Transcript is not null;
        SummaryCopyButton.IsEnabled = hasTranscript;
        ChatCopyButton.IsEnabled = hasTranscript;
    }

    /// <summary>
    /// Every provider with the models it offers (macOS <c>AIModelMenu</c>): on this PC first, then the hosts, then the
    /// ones whose data leaves this PC; a provider without a model says to choose one in Settings, one that cannot run now
    /// is off with why; then "Choose Models…" (Settings › AI).
    /// </summary>
    private void BuildModelMenu()
    {
        ModelMenu.Items.Clear();
        if (_panel is null || _model is null) return;
        var providers = _panel.Providers;
        var all = providers.AIProviders;
        var current = providers.CurrentAIProvider;
        var currentModel = _panel.CurrentModel;
        var inApp = all.Where(p => p.Kind == AIProviderKind.InApp).ToList();
        var hosts = all.Where(p => p.Kind != AIProviderKind.InApp && !p.LeavesPC).Concat(all.Where(p => p.LeavesPC)).ToList();
        if (inApp.Count > 0)
        {
            ModelMenu.Items.Add(Header(Loc.T("On this PC")));
            foreach (var provider in inApp) ModelMenu.Items.Add(ModelItem(provider, provider.DefaultModel, provider.Name, current, currentModel));
        }
        foreach (var provider in hosts)
        {
            if (ModelMenu.Items.Count > 0) ModelMenu.Items.Add(new MenuFlyoutSeparator());
            ModelMenu.Items.Add(Header(provider.LeavesPC ? Loc.Format("%@ (leaves this PC)", provider.Name) : provider.Name));
            if (provider.Models.Count == 0) ModelMenu.Items.Add(new MenuFlyoutItem { Text = Loc.T("No model: choose one in Settings"), IsEnabled = false });
            foreach (var id in provider.Models)
            {
                ModelMenu.Items.Add(ModelItem(provider, id, id.Length == 0 ? Loc.T("Default") : id, current, currentModel));
            }
        }
        if (ModelMenu.Items.Count > 0) ModelMenu.Items.Add(new MenuFlyoutSeparator());
        var choose = new MenuFlyoutItem { Text = Loc.T("Choose Models…") };
        choose.Click += (_, _) => _model?.OpenSettings(Services.SettingsSearch.Providers);
        ModelMenu.Items.Add(choose);
    }

    private static MenuFlyoutItem Header(string text) => new() { Text = text, IsEnabled = false, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    private MenuFlyoutItemBase ModelItem(AIProviderInfo provider, string id, string title, AIProviderInfo? current, string? currentModel)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = title,
            GroupName = "AIModel",
            IsChecked = current?.Id == provider.Id && currentModel == id,
            IsEnabled = provider.UnavailableReason is null,
        };
        if (provider.UnavailableReason is { } reason) ToolTipService.SetToolTip(item, reason);
        item.Click += (_, _) => _panel?.Providers.SetAIModel(id, provider);
        return item;
    }

    /// <summary>Auto, English, French, Vietnamese, and a hand-edited code (macOS <c>AILanguageMenu</c>).</summary>
    private void BuildLanguageMenu()
    {
        LanguageMenu.Items.Clear();
        if (_panel is null || _model is null) return;
        var selection = _model.Settings.SummaryLanguage;
        var choices = Languages.Select(l => (l.Code, Name: Loc.T(l.Name))).ToList();
        if (choices.All(c => c.Code != selection)) choices.Add((selection, selection));
        foreach (var (code, name) in choices)
        {
            var item = new RadioMenuFlyoutItem { Text = name, GroupName = "SummaryLanguage", IsChecked = code == selection };
            item.Click += (_, _) =>
            {
                if (_panel is { } panel && panel.Providers.SummaryLanguage != code) panel.Providers.SummaryLanguage = code;
            };
            LanguageMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// Where the data goes with the current provider (macOS <c>dataNotes</c>): off this PC, and how far a CLI agent is
    /// contained. Empty when there is nothing to say (the "i" is then hidden).
    /// </summary>
    private List<(string Glyph, string Text)> DataNotes()
    {
        List<(string, string)> notes = [];
        if (_panel?.Providers.CurrentAIProvider is not { } provider) return notes;
        if (provider.LeavesPC) notes.Add(("", Loc.Format("Data sent to %@ leaves this PC.", provider.Name)));
        if (provider.CliPreset is { } preset && CLIRunner.ContainmentNote(preset) is { } note) notes.Add(("", Loc.T(note)));
        return notes;
    }

    private void BuildDataNotes()
    {
        DataNotesPanel.Children.Clear();
        foreach (var (glyph, text) in DataNotes())
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new FontIcon { Glyph = glyph, Style = (Style)Resources["NoteIconStyle"] });
            var block = new TextBlock { Text = text, Style = (Style)Resources["NoteTextStyle"] };
            Grid.SetColumn(block, 1);
            row.Children.Add(block);
            DataNotesPanel.Children.Add(row);
        }
    }

    // Copy

    /// <summary>
    /// Copy as prompt (macOS <c>copyPrompt</c>, Ctrl+Shift+C): the transcript as a summary prompt, or in Chat with the
    /// typed question; kept out of the clipboard history and the cloud clipboard (macOS: transient, this Mac only). False
    /// without a transcript.
    /// </summary>
    internal bool CopyPrompt()
    {
        if (_panel?.CopyPrompt(_tab == DetailTab.Chat ? QuestionBox.Text : null) is not { } prompt) return false;
        CopyText(prompt, Loc.T("Copied — paste into any chat"), transient: true);
        return true;
    }

    private void CopyText(string text, string toast, bool transient = false)
    {
        var package = new DataPackage();
        package.SetText(text);
        try
        {
            if (transient) Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
            else Clipboard.SetContent(package);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not copy: {e.GetType().Name}");
            return;
        }
        ShowToast(toast);
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

    private MarkdownLinks Links() => new(Seek: seconds => _seek?.Invoke(seconds), OpenUrl: url => _ = OpenUrlAsync(url));

    /// <summary>
    /// A web link from model output opens in the browser once the dialog showing its destination is confirmed (macOS
    /// <c>WebLinkConfirmation</c>); only http(s), checked again here.
    /// </summary>
    private async Task OpenUrlAsync(Uri url)
    {
        if (_model?.Confirm is not { } confirm || url.Scheme is not ("http" or "https")) return;
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

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
