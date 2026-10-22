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
/// One conversation (macOS MainView.swift detail and toolbar title): the title, renamed in place, and the details; then
/// the toolbar's tab. It owns the transcript's state and the player, which stay while Summary or Chat shows, so
/// playback goes on there (macOS TranscriptView stays mounted across tabs).
/// </summary>
public sealed partial class ConversationView : UserControl
{
    private readonly AppViewModel _model;
    private readonly Guid _id;
    private readonly PlaybackController _playback;
    private readonly TranscriptViewModel _transcript;

    /// <summary>What the audio file was looked up for: it is looked up again when it changes (compressed, deleted).</summary>
    private (bool HasAudio, ConversationStatus Status)? _audioFor;

    /// <summary>The CAF file the player opens; null while recording or compressing, without audio, or once released.</summary>
    private string? _audioPath;

    /// <summary>Bumped at every lookup and when the audio is released, so an older lookup that finishes later is dropped.</summary>
    private int _loadGeneration;

    /// <summary>The title is being renamed in its field.</summary>
    private bool _editing;

    public ConversationView(AppViewModel model, Guid id)
    {
        _model = model;
        _id = id;
        InitializeComponent();
        _playback = new PlaybackController(DispatcherQueue);
        _transcript = new TranscriptViewModel(model, id);
        TitleText.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            BeginEditing();
        };
        TitleBox.KeyDown += OnTitleBoxKeyDown;
        TitleBox.LostFocus += (_, _) => EndEditing();
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _model.ReleasingAudio += OnReleasingAudio;
            _transcript.PropertyChanged += OnTranscriptChanged;
            _transcript.Attach();
            TranscriptPane.Attach(_model, _transcript, _playback);
            Update();
            UpdateTab();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.ReleasingAudio -= OnReleasingAudio;
            _transcript.PropertyChanged -= OnTranscriptChanged;
            TranscriptPane.Detach();
            _transcript.Detach();
            _loadGeneration++; // a lookup still running must not open a player after this
            _audioFor = null;
            _audioPath = null;
            _playback.Dispose();
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.Conversations):
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.TranscriptionErrors):
                Update();
                break;
            case nameof(AppViewModel.Settings):
                _playback.SetChannels(_model.Settings.PlaybackChannels);
                break;
            case nameof(AppViewModel.DetailTab):
                UpdateTab();
                break;
        }
    }

    /// <summary>Another version shows: the same audio highlights its segments.</summary>
    private void OnTranscriptChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranscriptViewModel.Lines)) LoadPlayback();
    }

    /// <summary>Its audio is about to be deleted: closes the file now; a lookup still running must not reopen it.</summary>
    private void OnReleasingAudio(Guid id)
    {
        if (id != _id) return;
        _loadGeneration++;
        _audioPath = null;
        _playback.Close();
    }

    private void Update()
    {
        if (_model.Conversations.FirstOrDefault(c => c.Id == _id) is not { } conversation)
        {
            // Not listed yet (the launch refresh), or deleted.
            TitleText.Text = "";
            DetailText.Text = "This conversation is not in the library.";
            return;
        }
        if (!_editing) TitleText.Text = conversation.Title;
        DetailText.Text = ConversationText.Detail(conversation, _model);

        var key = (conversation.HasAudio, conversation.Status);
        if (_audioFor != key)
        {
            _audioFor = key;
            _ = FindAudioAsync(conversation);
        }
    }

    /// <summary>Transcript shows the transcript and playback; Summary and Chat their placeholder (playback goes on).</summary>
    private void UpdateTab()
    {
        var transcript = _model.DetailTab == DetailTab.Transcript;
        TranscriptPane.Visibility = transcript ? Visibility.Visible : Visibility.Collapsed;
        AIPane.Visibility = transcript ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Finds the audio off the UI thread and opens it: not while it is being recorded or compressed (macOS
    /// TranscriptView <c>load</c>), and not once it was deleted.
    /// </summary>
    private async Task FindAudioAsync(Conversation conversation)
    {
        var generation = ++_loadGeneration;
        var folder = conversation.Folder;
        var playable = conversation.HasAudio
            && conversation.Status is not (ConversationStatus.Recording or ConversationStatus.Compressing);
        var audio = playable ? await Task.Run(() => AudioPath(folder)) : null;
        if (generation != _loadGeneration || _model.IsClosing) return;
        _audioPath = audio;
        LoadPlayback();
    }

    /// <summary>The player for the audio, with the shown version's segments and the channels setting.</summary>
    private void LoadPlayback() =>
        _playback.Load(_audioPath, _transcript.ShownTranscript?.Segments ?? [], _model.Settings.PlaybackChannels);

    // Title

    private void BeginEditing()
    {
        if (_editing || _model.Conversations.FirstOrDefault(c => c.Id == _id) is not { } conversation) return;
        _editing = true;
        TitleBox.Text = conversation.Title;
        TitleText.Visibility = Visibility.Collapsed;
        TitleBox.Visibility = Visibility.Visible;
        // Once the field is laid out, so it can take the focus.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_editing) return;
            TitleBox.Focus(FocusState.Programmatic);
            TitleBox.SelectAll();
        });
    }

    private void EndEditing()
    {
        if (!_editing) return;
        _editing = false;
        TitleBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        Update();
    }

    /// <summary>Enter saves a changed title, Esc cancels (macOS toolbar title popover).</summary>
    private void OnTitleBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                var draft = TitleBox.Text;
                var changed = draft != TitleText.Text;
                EndEditing();
                if (changed) _ = _model.RenameConversationAsync(_id, draft);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                EndEditing();
                break;
        }
    }

    /// <summary>
    /// The session's audio file (macOS TranscriptView <c>load</c>): the CAF, or the m4a once compressed (the mic file of
    /// the mono fallback). Null when session.json is unreadable or the file is missing.
    /// </summary>
    private static string? AudioPath(string folder)
    {
        try
        {
            var name = RecordingSession.Read(folder).AudioFileName;
            return ConversationFiles.AudioPath(folder, name) is { } path && File.Exists(path) ? path : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
