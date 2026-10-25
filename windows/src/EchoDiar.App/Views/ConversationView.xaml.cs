using System.ComponentModel;
using EchoDiar.AI;
using EchoDiar.App.Services;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Model;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.ViewManagement;

namespace EchoDiar.App.Views;

/// <summary>
/// One conversation (macOS MainView.swift detail and toolbar title): the title, renamed in place, and the details; then
/// the toolbar's tab. It owns the transcript's state and the player, which stay while Summary or Chat shows, so
/// playback goes on there (macOS TranscriptView stays mounted across tabs).
/// </summary>
public sealed partial class ConversationView : UserControl
{
    /// <summary>How long the loading pane shows without its ring: a ring shown for a frame or two reads as a flicker.</summary>
    private static readonly TimeSpan SpinnerDelay = TimeSpan.FromMilliseconds(150);

    private static readonly UISettings UISettings = new();

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

    /// <summary>A clicked AI citation's time, shown once the transcript is read (macOS <c>pendingTranscriptTime</c>).</summary>
    private double? _pendingTime;

    /// <summary>The citation's play head, set once the player has opened the audio.</summary>
    private double? _pendingSeek;

    /// <summary>Fades the loading pane's ring in after <see cref="SpinnerDelay"/>.</summary>
    private readonly DispatcherQueueTimer _spinnerTimer;

    /// <summary>The loading pane shows; null before the first <see cref="UpdateTab"/>.</summary>
    private bool? _loadingShown;

    public ConversationView(AppViewModel model, Guid id)
    {
        _model = model;
        _id = id;
        InitializeComponent();
        _playback = new PlaybackController(DispatcherQueue);
        _transcript = new TranscriptViewModel(model, id);
        _spinnerTimer = DispatcherQueue.CreateTimer();
        _spinnerTimer.Interval = SpinnerDelay;
        _spinnerTimer.IsRepeating = false;
        _spinnerTimer.Tick += (t, _) =>
        {
            t.Stop();
            LoadingRing.OpacityTransition = UISettings.AnimationsEnabled ? new ScalarTransition() : null;
            LoadingRing.IsActive = true;
            LoadingRing.Opacity = 1;
        };
        AIPanel.HasLoadedChanged += UpdateTab;
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
            AIPanel.Attach(_model, _id, OpenCitation);
            Update();
            UpdateTab();
            OpenLibraryCitation();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.ReleasingAudio -= OnReleasingAudio;
            _transcript.PropertyChanged -= OnTranscriptChanged;
            TranscriptPane.Detach();
            AIPanel.Detach();
            _transcript.Detach();
            _spinnerTimer.Stop();
            _loadingShown = null; // shown again from the start if the page comes back
            _loadGeneration++; // a lookup still running must not open a player after this
            _audioFor = null;
            _audioPath = null;
            _playback.Dispose();
        };
    }

    /// <summary>Space on the Transcript tab (macOS <c>togglePlayback</c>): what the playback bar's play button does.</summary>
    internal void TogglePlayback() => _playback.TogglePlay();

    /// <summary>Ctrl+Shift+C on Summary or Chat: the AI panel's Copy as prompt; false when it copied nothing.</summary>
    internal bool CopyPrompt() => _model.DetailTab != DetailTab.Transcript && AIPanel.CopyPrompt();

    /// <summary>Esc on Chat closes the History panel first; false when it was not open.</summary>
    internal bool CloseHistory() => _model.DetailTab == DetailTab.Chat && AIPanel.CloseHistory();

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
            case nameof(AppViewModel.PendingTranscriptTime):
                OpenLibraryCitation();
                break;
        }
    }

    /// <summary>A library chat citation of this conversation (<see cref="AppViewModel.OpenReference"/>), taken once.</summary>
    private void OpenLibraryCitation()
    {
        if (_model.TakePendingTranscriptTime(_id) is { } seconds) OpenCitation(seconds);
    }

    /// <summary>
    /// Another version shows: the same audio highlights its segments. The first read shows the tab. A citation waits for
    /// the transcript.
    /// </summary>
    private void OnTranscriptChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranscriptViewModel.Lines)) LoadPlayback();
        if (e.PropertyName == nameof(TranscriptViewModel.HasLoaded)) UpdateTab();
        ShowPendingTime();
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

    /// <summary>
    /// Transcript shows the transcript and playback; Summary and Chat the AI panel (playback goes on). Each once its
    /// files are read, the loading pane until then (macOS c45b593).
    /// </summary>
    private void UpdateTab()
    {
        var transcript = _model.DetailTab == DetailTab.Transcript;
        var loaded = transcript ? _transcript.HasLoaded : AIPanel.HasLoaded;
        TranscriptPane.Visibility = transcript && loaded ? Visibility.Visible : Visibility.Collapsed;
        AIPanel.Visibility = !transcript && loaded ? Visibility.Visible : Visibility.Collapsed;
        ShowLoadingPane(!loaded);
    }

    /// <summary>The loading pane at once, its ring only after <see cref="SpinnerDelay"/> (macOS DetailLoadingPane).</summary>
    private void ShowLoadingPane(bool loading)
    {
        if (loading == _loadingShown) return;
        _loadingShown = loading;
        _spinnerTimer.Stop();
        LoadingRing.IsActive = false;
        LoadingRing.Opacity = 0;
        LoadingPane.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading) _spinnerTimer.Start();
        StartupTrace.Write($"conversation {_id} {_model.DetailTab}: {(loading ? "loading pane" : "content")} shown");
    }

    /// <summary>
    /// A <c>[mm:ss]</c> citation of an answer (macOS <c>openTranscript(conversationID:at:)</c>): the Transcript tab, at
    /// that time once the transcript is read.
    /// </summary>
    private void OpenCitation(double seconds)
    {
        if (_model.IsClosing) return;
        _pendingTime = seconds;
        _model.DetailTab = DetailTab.Transcript;
        ShowPendingTime();
    }

    /// <summary>
    /// The citation's time once the transcript is read (macOS TranscriptView <c>showPendingTime</c>): the newest version
    /// (citations point into it), scrolled to the segment it cites (<see cref="TranscriptCitation.SegmentAt"/>), with
    /// the play head there, inside that segment so it is highlighted, without playing. Cleared even without segments.
    /// </summary>
    private void ShowPendingTime()
    {
        if (_pendingTime is not { } time || _transcript.IsLoading) return;
        _pendingTime = null;
        if (_transcript.IsViewingOlderVersion && _transcript.Versions.Count > 0)
        {
            _transcript.ShowVersion(_transcript.Versions.Max(v => v.Number));
        }
        if (_transcript.Transcript?.Segments is not { } segments
            || TranscriptCitation.SegmentAt(segments, time) is not ({ } segment, var at)) return;
        if (_playback.IsReady) _playback.Seek(at);
        else _pendingSeek = at;
        TranscriptPane.ShowSegment(segment.Id);
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
        StartupTrace.Write($"conversation {_id}: begin audio open");
        LoadPlayback();
        StartupTrace.Write($"conversation {_id}: end audio open");
    }

    /// <summary>The player for the audio, with the shown version's segments and the channels setting; then a citation's seek.</summary>
    private void LoadPlayback()
    {
        _playback.Load(_audioPath, _transcript.ShownTranscript?.Segments ?? [], _model.Settings.PlaybackChannels);
        if (_pendingSeek is not { } at || !_playback.IsReady) return;
        _pendingSeek = null;
        _playback.Seek(at);
    }

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
