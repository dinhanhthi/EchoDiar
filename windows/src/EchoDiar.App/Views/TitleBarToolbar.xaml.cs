using System.ComponentModel;
using System.Diagnostics;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Platform.SystemServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace EchoDiar.App.Views;

/// <summary>
/// The title bar's capsules (macOS MainView.swift toolbar), shown by route: Back off the list; Record with its menu of
/// targets (Stop and Pause or Resume while recording); on a conversation its title, Reveal, Delete and the tabs;
/// Settings and Search everywhere (placeholders until Phases 13 and 11). Update and Ask across conversations stay
/// collapsed until Phases 27 and 26.
/// </summary>
public sealed partial class TitleBarToolbar : UserControl
{
    private const string PauseGlyph = "";
    private const string ResumeGlyph = "";

    private AppViewModel? _model;

    /// <summary>What Record records: null for all system audio, else an app picked in the menu.</summary>
    private AudioProcessGroup? _target;

    /// <summary>A delete confirmation is open: only one dialog at a time.</summary>
    private bool _confirming;

    public TitleBarToolbar()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => _model?.GoBack();
        RecordSplitButton.Click += (_, _) => _ = _model?.StartRecordingAsync(_target);
        TargetMenu.Opening += (_, _) => OnTargetMenuOpening();
        StopButton.Click += (_, _) => _ = _model?.StopRecordingAsync();
        PauseButton.Click += (_, _) => _ = _model?.TogglePauseAsync();
        RevealButton.Click += (_, _) => Reveal();
        DeleteMenu.Opening += (_, _) => UpdateDeleteMenu();
        DeleteAudioItem.Click += async (_, _) => await DeleteAsync(audioOnly: true);
        DeleteConversationItem.Click += async (_, _) => await DeleteAsync(audioOnly: false);
    }

    /// <summary>Follows <paramref name="model"/> for as long as the window lives.</summary>
    public void Attach(AppViewModel model)
    {
        _model = model;
        model.PropertyChanged += OnModelChanged;
        Update();
    }

    /// <summary>The window closed: its controls fail every call, so the model's changes no longer reach them.</summary>
    public void Detach()
    {
        if (_model is null) return;
        _model.PropertyChanged -= OnModelChanged;
        _model = null;
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
                Update();
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
        StopButton.Visibility = Shown(recording);
        StopButton.IsEnabled = !_model.IsStarting;
        PauseButton.Visibility = Shown(recording);
        PauseIcon.Glyph = paused ? ResumeGlyph : PauseGlyph;
        var pauseLabel = Loc.T(paused ? "Resume recording" : "Pause recording");
        AutomationProperties.SetName(PauseButton, pauseLabel);
        ToolTipService.SetToolTip(PauseButton, pauseLabel);

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
            IsChecked = _target?.Exe == group?.Exe,
        };
        item.Click += (_, _) => _target = group;
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
        if (_confirming) return false;
        _confirming = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                Title = audioOnly ? $"Delete the audio of \"{conversation.Title}\"?" : $"Delete \"{conversation.Title}\"?",
                Content = audioOnly
                    ? "The audio is moved to the Recycle Bin. The transcript, summary and chat stay; playback and re-transcribing are no longer possible."
                    : "The recording and its files are moved to the Recycle Bin.",
                PrimaryButtonText = Loc.T("Delete"),
                CloseButtonText = Loc.T("Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _confirming = false;
        }
    }
}
