using System.ComponentModel;
using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Platform.SystemServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace EchoDiar.App.Views;

/// <summary>
/// The title bar's capsules (macOS MainView.swift toolbar), shown by route: Back off the list; Record with its menu of
/// targets (Stop, blinking, and Pause or Resume while recording); on a conversation its title, Reveal, Delete and the
/// tabs; Settings and Search everywhere (placeholders until Phases 13 and 11). Update and Ask across conversations
/// stay collapsed until Phases 27 and 26. It also hosts the window's dialogs (it lives as long as the window): the
/// delete confirmation, "Recording problem" and the lost-mic prompt, one at a time.
/// </summary>
public sealed partial class TitleBarToolbar : UserControl
{
    private const string PauseGlyph = "";
    private const string ResumeGlyph = "";

    /// <summary>The recording indicator dims every other second (macOS RecordingBlink).</summary>
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromSeconds(1);

    private static readonly UISettings UISettings = new();

    private AppViewModel? _model;

    /// <summary>The open dialog: WinUI shows one at a time per window.</summary>
    private ContentDialog? _dialog;

    /// <summary>The lost-mic prompt is open.</summary>
    private ContentDialog? _routePrompt;

    /// <summary>The mic was lost while another dialog was open: asked once that one closes.</summary>
    private bool _routePromptPending;

    private DispatcherQueueTimer? _blinkTimer;

    /// <summary>Record's tooltip, "Record" and the target; set again only when the target changes.</summary>
    private string? _recordTip;

    public TitleBarToolbar()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => _model?.GoBack();
        RecordSplitButton.Click += (_, _) => _ = _model?.StartRecordingAsync(_model.SelectedTarget);
        TargetMenu.Opening += (_, _) => OnTargetMenuOpening();
        StopButton.Click += (_, _) => _ = _model?.StopRecordingAsync();
        PauseButton.Click += (_, _) => _ = _model?.TogglePauseAsync();
        RevealButton.Click += (_, _) => Reveal();
        DeleteMenu.Opening += (_, _) => UpdateDeleteMenu();
        DeleteAudioItem.Click += async (_, _) => await DeleteAsync(audioOnly: true);
        DeleteConversationItem.Click += async (_, _) => await DeleteAsync(audioOnly: false);
        // A start that failed before the window was shown still gets its dialog.
        Loaded += (_, _) => ShowNextDialog();
    }

    /// <summary>Follows <paramref name="model"/> for as long as the window lives.</summary>
    public void Attach(AppViewModel model)
    {
        _model = model;
        model.PropertyChanged += OnModelChanged;
        model.MicRouteLostAsk += OnMicRouteLostAsk;
        _blinkTimer = DispatcherQueue.CreateTimer();
        _blinkTimer.Interval = BlinkInterval;
        _blinkTimer.Tick += (_, _) => Blink();
        Update();
    }

    /// <summary>The window closed: its controls fail every call, so the model's changes no longer reach them.</summary>
    public void Detach()
    {
        if (_model is null) return;
        _model.PropertyChanged -= OnModelChanged;
        _model.MicRouteLostAsk -= OnMicRouteLostAsk;
        _model = null;
        _blinkTimer?.Stop();
        _blinkTimer = null;
    }

    /// <summary>
    /// The window-space rects (physical pixels at <paramref name="scale"/>) of every visible interactive element: the
    /// title bar passes clicks there to the controls, and drags the window everywhere else.
    /// </summary>
    public IReadOnlyList<RectInt32> PassthroughRects(double scale)
    {
        FrameworkElement[] elements = [BackButton, RecordCapsule, UpdateButton, FileCapsule, AppCapsule, SearchBox, TabsCapsule];
        var rects = new List<RectInt32>();
        foreach (var element in elements)
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
            var bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            rects.Add(new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)));
        }
        return rects;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.Conversations):
                StartupTrace.Write($"toolbar update ({_model?.Conversations.Count} conversations)");
                Update();
                break;
            case nameof(AppViewModel.Route):
            case nameof(AppViewModel.RecordingSnapshot):
            case nameof(AppViewModel.IsStarting):
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.TranscriptionErrors):
            case nameof(AppViewModel.SelectedTarget):
                Update();
                break;
            case nameof(AppViewModel.RecordingError):
                ShowNextDialog();
                break;
            case nameof(AppViewModel.RecordTargets):
                // Apps that started playing since the menu opened show up in it.
                if (TargetMenu.IsOpen) BuildTargetMenu();
                break;
        }
    }

    private void Update()
    {
        if (_model is null) return;
        BackButton.Visibility = Shown(_model.Route.Kind != RouteKind.List);

        var recording = _model.IsRecording;
        var paused = _model.RecordingSnapshot.State == RecordingEngine.State.Paused;
        RecordSplitButton.Visibility = Shown(!recording);
        RecordSplitButton.IsEnabled = !_model.IsStarting;
        var recordTip = $"Record {_model.SelectedTarget?.Name ?? Loc.T("All system audio")}";
        if (recordTip != _recordTip) ToolTipService.SetToolTip(RecordSplitButton, _recordTip = recordTip);
        StopButton.Visibility = Shown(recording);
        StopButton.IsEnabled = !_model.IsStarting;
        PauseButton.Visibility = Shown(recording);
        PauseIcon.Glyph = paused ? ResumeGlyph : PauseGlyph;
        var pauseLabel = Loc.T(paused ? "Resume recording" : "Pause recording");
        AutomationProperties.SetName(PauseButton, pauseLabel);
        ToolTipService.SetToolTip(PauseButton, pauseLabel);
        UpdateBlink(recording && !paused);
        // The recording ended (stopped elsewhere): the lost-mic question no longer applies.
        if (!recording)
        {
            _routePromptPending = false;
            _routePrompt?.Hide();
        }

        var conversation = ShownConversation();
        var onConversation = conversation is not null;
        TitlePanel.Visibility = Shown(onConversation);
        FileCapsule.Visibility = Shown(onConversation);
        TabsCapsule.Visibility = Shown(onConversation);
        if (conversation is null) return;
        TitleText.Text = conversation.Title;
        DetailText.Text = ConversationText.Detail(conversation, _model);
        DeleteButton.IsEnabled = _model.CanDelete(conversation);
    }

    /// <summary>The conversation page's conversation, while it exists; null on the other pages.</summary>
    private Conversation? ShownConversation() =>
        _model?.Route is { Kind: RouteKind.Conversation, ConversationId: { } id }
            ? _model.Conversations.FirstOrDefault(c => c.Id == id)
            : null;

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // Record menu

    /// <summary>Built when it opens, then refreshed: apps that started playing since show up.</summary>
    private void OnTargetMenuOpening()
    {
        BuildTargetMenu();
        _ = _model?.RefreshRecordTargetsAsync();
    }

    /// <summary>"All system audio" first, then the apps with audio; the picked one is checked.</summary>
    private void BuildTargetMenu()
    {
        if (_model is null) return;
        TargetMenu.Items.Clear();
        TargetMenu.Items.Add(TargetItem(Loc.T("All system audio"), null));
        if (_model.RecordTargets.Count == 0) return;
        TargetMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var group in _model.RecordTargets)
        {
            TargetMenu.Items.Add(TargetItem(group.NeedsGlobalTap ? $"{group.Name} (records all system audio)" : group.Name, group));
        }
    }

    /// <summary>Matched by app, since process lists change.</summary>
    private RadioMenuFlyoutItem TargetItem(string text, AudioProcessGroup? group)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = text,
            GroupName = "RecordTarget",
            IsChecked = _model?.SelectedTarget?.Exe == group?.Exe,
        };
        item.Click += (_, _) =>
        {
            if (_model is not null) _model.SelectedTarget = group;
        };
        return item;
    }

    // Reveal and Delete

    private void Reveal()
    {
        if (ShownConversation() is not { } conversation) return;
        try
        {
            ShellFiles.Reveal(conversation.Folder);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not reveal {conversation.Folder}: {e}");
        }
    }

    /// <summary>Delete's choices: the audio only, or the whole conversation; "…" when each is asked first.</summary>
    private void UpdateDeleteMenu()
    {
        if (_model is null || ShownConversation() is not { } conversation) return;
        var asks = !_model.Settings.DeleteWithoutConfirmation;
        DeleteAudioItem.Text = conversation.HasAudio
            ? Loc.T(asks ? "Delete audio only…" : "Delete audio only")
            : Loc.T("Audio has been removed");
        DeleteAudioItem.IsEnabled = conversation.HasAudio;
        DeleteConversationItem.Text = Loc.T(asks ? "Delete conversation…" : "Delete conversation");
    }

    /// <summary>Asks first unless "Delete conversations without confirmation" is on, then deletes.</summary>
    private async Task DeleteAsync(bool audioOnly)
    {
        if (_model is null || ShownConversation() is not { } conversation || !_model.CanDelete(conversation)) return;
        var id = conversation.Id;
        if (!_model.Settings.DeleteWithoutConfirmation && !await ConfirmDeleteAsync(conversation, audioOnly)) return;
        // It may have started a job while the dialog was open.
        if (_model.Conversations.FirstOrDefault(c => c.Id == id) is not { } current || !_model.CanDelete(current)) return;
        if (audioOnly) await _model.DeleteAudioAsync(id);
        else await _model.DeleteConversationAsync(id);
    }

    /// <summary>The macOS alert's texts (not in its string catalog), with the Recycle Bin for the Trash.</summary>
    private async Task<bool> ConfirmDeleteAsync(Conversation conversation, bool audioOnly)
    {
        if (_dialog is not null) return false;
        var dialog = NewDialog(
            audioOnly ? $"Delete the audio of \"{conversation.Title}\"?" : $"Delete \"{conversation.Title}\"?",
            audioOnly
                ? "The audio is moved to the Recycle Bin. The transcript, summary and chat stay; playback and re-transcribing are no longer possible."
                : "The recording and its files are moved to the Recycle Bin.");
        dialog.PrimaryButtonText = Loc.T("Delete");
        dialog.CloseButtonText = Loc.T("Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        var confirmed = false;
        await ShowAsync(dialog, result => confirmed = result == ContentDialogResult.Primary);
        return confirmed;
    }

    // Recording dialogs and indicator

    /// <summary>Shows what waits, unless a dialog is open: a recording problem first, then the lost-mic prompt.</summary>
    private void ShowNextDialog()
    {
        if (_model is null || _dialog is not null || XamlRoot is null) return;
        if (_model.RecordingError is { } error)
        {
            _ = ShowRecordingProblemAsync(error);
        }
        else if (_routePromptPending)
        {
            _routePromptPending = false;
            if (_model.IsRecording) _ = AskRouteLostAsync();
        }
    }

    /// <summary>
    /// "Recording problem" (macOS MainView.swift alert on recordingError): the only UI for a start or resume that
    /// failed and for a recording the engine stopped on its own.
    /// </summary>
    private Task ShowRecordingProblemAsync(string error)
    {
        var dialog = NewDialog(Loc.T("Recording problem"), error);
        dialog.CloseButtonText = Loc.T("OK");
        dialog.DefaultButton = ContentDialogButton.Close;
        return ShowAsync(dialog, _ => _model?.DismissRecordingError(error));
    }

    private void OnMicRouteLostAsk()
    {
        if (_routePrompt is not null) return;
        _routePromptPending = true;
        ShowNextDialog();
    }

    /// <summary>
    /// The lost mic with "Ask to stop" (macOS: the pill's end prompt for inputRouteLost, its countdown comes with
    /// Phase 19): Stop now stops as Stop does, Keep recording carries on with the default input.
    /// </summary>
    private async Task AskRouteLostAsync()
    {
        var dialog = NewDialog("Microphone disconnected — stop recording?", null);
        dialog.PrimaryButtonText = Loc.T("Stop now");
        dialog.CloseButtonText = Loc.T("Keep recording");
        dialog.DefaultButton = ContentDialogButton.Primary;
        _routePrompt = dialog;
        await ShowAsync(dialog, result =>
        {
            if (result == ContentDialogResult.Primary && _model is { IsRecording: true } model) _ = model.StopRecordingAsync();
        });
        _routePrompt = null;
    }

    private ContentDialog NewDialog(string title, string? content) => new()
    {
        XamlRoot = XamlRoot,
        Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
        Title = title,
        Content = content,
    };

    /// <summary>
    /// Shows <paramref name="dialog"/> as the open one, hands its result to <paramref name="answered"/> (not once the
    /// window closed), then shows what waits.
    /// </summary>
    private async Task ShowAsync(ContentDialog dialog, Action<ContentDialogResult> answered)
    {
        _dialog = dialog;
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not show a dialog: {e}");
            result = ContentDialogResult.None;
        }
        finally
        {
            _dialog = null;
        }
        if (_model is null) return;
        answered(result);
        ShowNextDialog();
    }

    /// <summary>Stop blinks while recording, unless paused, the window is hidden or Windows animations are off.</summary>
    private void UpdateBlink(bool active)
    {
        if (_blinkTimer is null) return;
        active &= UISettings.AnimationsEnabled;
        if (active && !_blinkTimer.IsRunning) _blinkTimer.Start();
        if (!active)
        {
            _blinkTimer.Stop();
            StopIcon.Opacity = 1;
        }
    }

    private void Blink()
    {
        if (_model is null) return;
        StopIcon.Opacity = StopIcon.Opacity < 1 || XamlRoot is not { IsHostVisible: true } ? 1 : 0.35;
    }
}
