using System.Diagnostics;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Model;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.ViewManagement;

namespace EchoDiar.App.Views;

/// <summary>
/// What a <see cref="CatchUpCard"/> shows (macOS <c>CatchUpCard</c>): the catch-up being written or a saved one. The id
/// is the run's job id, which its saved catch-up keeps, so a card keeps its open state once saved; the elapsed seconds
/// of recording when it was asked for; the provider's name (null when it is gone), with "(leaves this PC)" when its data
/// does, and the model that write or wrote it.
/// </summary>
public sealed record CatchUpCardState(
    Guid Id, double Elapsed, string Text, bool Running = false, bool Stopping = false, string? StatusLine = null,
    string? Error = null, bool Stopped = false, string? Provider = null, string? Model = null)
{
    public static CatchUpCardState From(CatchUpRun run) => new(
        run.JobId, run.Elapsed, run.Text, run.IsRunning, run.Stopping, run.StatusLine, run.Error,
        Provider: ProviderLabel(run.ProviderName, run.ProviderLeavesPC), Model: run.Model);

    /// <summary>A saved catch-up: its provider's name while that provider is still among <paramref name="providers"/>.</summary>
    public static CatchUpCardState From(CatchUp catchUp, IReadOnlyList<AIProviderInfo> providers) => new(
        catchUp.Id, catchUp.Elapsed, catchUp.Text, Stopped: catchUp.Stopped,
        Provider: providers.FirstOrDefault(p => p.Id == catchUp.Provider) is { } provider
            ? ProviderLabel(provider.Name, provider.LeavesPC)
            : null,
        Model: catchUp.Model);

    /// <summary>"&lt;name&gt; (leaves this PC)" when its data does, as the AI menus say.</summary>
    private static string ProviderLabel(string name, bool leavesPC) =>
        leavesPC ? Loc.Format("%@ (leaves this PC)", name) : name;
}

/// <summary>
/// One catch-up (macOS CatchUpPanel.swift <c>CatchUpCardView</c>): a click on its header raises
/// <see cref="ToggleRequested"/> (the owner keeps the open state and sets <see cref="IsOpen"/>); Stop while it is being
/// written (<see cref="StopRequested"/>), else Copy, which shows a checkmark and "Copied" for 2 s. Open, its text: a
/// placeholder whose ring fades in after 150 ms until the text comes, then its Markdown (rendered again only when the
/// text changed, and only while open), the error under it. Links act only as <c>links</c> allows (none by default).
/// </summary>
public sealed partial class CatchUpCard : UserControl
{
    private const string OpenGlyph = "\uE70D";
    private const string ClosedGlyph = "\uE76C";
    private const string CopyGlyph = "\uE8C8";
    private const string CopiedGlyph = "\uE73E";

    private static readonly TimeSpan SpinnerDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CopiedDuration = TimeSpan.FromSeconds(2);
    private static readonly UISettings UISettings = new();

    private readonly MarkdownLinks _links;
    private readonly DispatcherQueueTimer _spinnerTimer;
    private readonly DispatcherQueueTimer _copiedTimer;
    private CatchUpCardState? _state;

    /// <summary>The text in <see cref="MarkdownText"/>; null when none was rendered yet.</summary>
    private string? _renderedText;

    private bool _open;

    public CatchUpCard(MarkdownLinks? links = null)
    {
        _links = links ?? new MarkdownLinks();
        InitializeComponent();
        _spinnerTimer = DispatcherQueue.CreateTimer();
        _spinnerTimer.Interval = SpinnerDelay;
        _spinnerTimer.IsRepeating = false;
        _spinnerTimer.Tick += (t, _) =>
        {
            t.Stop();
            PlaceholderRing.OpacityTransition = UISettings.AnimationsEnabled ? new ScalarTransition() : null;
            PlaceholderRing.IsActive = true;
            PlaceholderRing.Opacity = 1;
        };
        _copiedTimer = DispatcherQueue.CreateTimer();
        _copiedTimer.Interval = CopiedDuration;
        _copiedTimer.IsRepeating = false;
        _copiedTimer.Tick += (t, _) =>
        {
            t.Stop();
            ShowCopied(false);
        };
        HeaderButton.Click += (_, _) => ToggleRequested?.Invoke();
        StopButton.Click += (_, _) => StopRequested?.Invoke();
        CopyButton.Click += (_, _) => Copy();
        ShowCopied(false);
        ShowOpen();
    }

    /// <summary>Its header was clicked: the owner flips <see cref="IsOpen"/>.</summary>
    public event Action? ToggleRequested;

    /// <summary>Stop was clicked, while it is being written.</summary>
    public event Action? StopRequested;

    public bool IsOpen
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            ShowOpen();
        }
    }

    /// <summary>Shows <paramref name="state"/>; cheap when only the text or status changed, as while it streams.</summary>
    public void Show(CatchUpCardState state)
    {
        _state = state;
        var title = Loc.Format("Catch-up at %@", ConversationText.Clock(state.Elapsed));
        RecordingCard.SetText(TitleText, title);
        var details = string.Join(" · ", new[] { state.Provider, state.Model, state.Stopped ? Loc.T("Stopped") : null }
            .Where(part => !string.IsNullOrEmpty(part)));
        RecordingCard.SetText(DetailsText, details);
        DetailsText.Visibility = details.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(HeaderButton, details.Length > 0 ? $"{title}, {details}" : title);

        StopButton.Visibility = state.Running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = !state.Stopping;
        CopyButton.Visibility = !state.Running && state.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowPlaceholder(state.Text.Length == 0 && state.Running);
        RecordingCard.SetText(StatusText, state.StatusLine ?? Loc.T("Writing the catch-up…"));
        MarkdownText.Visibility = state.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderText();
        RecordingCard.SetText(ErrorText, state.Error ?? "");
        ErrorText.Visibility = state.Error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowOpen()
    {
        Chevron.Glyph = _open ? OpenGlyph : ClosedGlyph;
        Body.Visibility = _open ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(HeaderButton, Loc.T(_open ? "Expanded" : "Collapsed"));
        RenderText();
    }

    /// <summary>The Markdown again when the text changed since its last render; a closed card waits until it opens.</summary>
    private void RenderText()
    {
        if (!_open || _state is not { } state || state.Text == _renderedText) return;
        _renderedText = state.Text;
        MarkdownView.Render(MarkdownText, state.Text, _links);
    }

    /// <summary>The placeholder at once, its ring only after <see cref="SpinnerDelay"/>, so a fast answer does not flash it.</summary>
    private void ShowPlaceholder(bool shown)
    {
        var visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (Placeholder.Visibility == visibility) return;
        Placeholder.Visibility = visibility;
        _spinnerTimer.Stop();
        PlaceholderRing.OpacityTransition = null;
        PlaceholderRing.IsActive = false;
        PlaceholderRing.Opacity = 0;
        if (shown) _spinnerTimer.Start();
    }

    private void Copy()
    {
        if (_state is not { Text.Length: > 0 } state) return;
        var package = new DataPackage();
        package.SetText(state.Text);
        try
        {
            Clipboard.SetContent(package);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not copy the catch-up: {e.GetType().Name}");
            return;
        }
        ShowCopied(true);
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void ShowCopied(bool copied)
    {
        CopyIcon.Glyph = copied ? CopiedGlyph : CopyGlyph;
        AutomationProperties.SetName(CopyButton, Loc.T(copied ? "Copied" : "Copy"));
        ToolTipService.SetToolTip(CopyButton, Loc.T(copied ? "Copied" : "Copy the catch-up"));
    }
}
