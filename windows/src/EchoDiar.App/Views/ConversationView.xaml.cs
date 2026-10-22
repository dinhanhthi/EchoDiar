using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using EchoDiar.App.ViewModels;
using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Transcribe;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>One transcript line: the speaker's name, the start as "mm:ss" and the text.</summary>
public sealed partial record TranscriptLine(Transcript.Segment Segment, string Speaker, string Time, string Text)
{
    /// <summary>"Name [mm:ss]: text", the list item's name for screen readers.</summary>
    public override string ToString() => $"{Speaker} [{Time}]: {Text}";
}

/// <summary>
/// One conversation (MVP; Phase 13 completes it): the title, the status or job progress, the transcript lines and
/// playback. Clicking a line plays from it.
/// </summary>
public sealed partial class ConversationView : UserControl
{
    private readonly AppViewModel _model;
    private readonly Guid _id;
    private readonly PlaybackController _playback;
    private readonly ObservableCollection<TranscriptLine> _lines = [];

    /// <summary>What the transcript and audio were loaded for: they are read again when it changes (a job finished).</summary>
    private (bool HasTranscript, ConversationStatus Status)? _loadedFor;

    /// <summary>Bumped at every load, so an older load that finishes later is dropped.</summary>
    private int _loadGeneration;

    public ConversationView(AppViewModel model, Guid id)
    {
        _model = model;
        _id = id;
        InitializeComponent();
        _playback = new PlaybackController(DispatcherQueue);
        LineList.ItemsSource = _lines;
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _playback.PropertyChanged += OnPlaybackChanged;
            _model.ReleasingAudio += OnReleasingAudio;
            UpdateConversation();
            UpdatePlayback();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _playback.PropertyChanged -= OnPlaybackChanged;
            _model.ReleasingAudio -= OnReleasingAudio;
            _loadGeneration++; // a load still reading must not open a player after this
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
                UpdateConversation();
                break;
        }
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e) => UpdatePlayback();

    /// <summary>Its audio is about to be deleted: closes the file now; a load still reading must not reopen it.</summary>
    private void OnReleasingAudio(Guid id)
    {
        if (id != _id) return;
        _loadGeneration++;
        _playback.Close();
    }

    private void UpdateConversation()
    {
        if (_model.Conversations.FirstOrDefault(c => c.Id == _id) is not { } conversation)
        {
            // Not listed yet (the launch refresh), or deleted.
            TitleText.Text = "";
            StatusText.Text = "This conversation is not in the library.";
            TranscribeButton.Visibility = Visibility.Collapsed;
            JobBar.Visibility = Visibility.Collapsed;
            return;
        }

        TitleText.Text = conversation.Title;
        StatusText.Text = $"{ConversationText.Clock(conversation.Duration)} · {ConversationText.Status(conversation, _model)}";

        var running = _model.TranscriptionProgress.TryGetValue(_id, out var progress) && progress.Step != JobStep.NeedsModel;
        JobBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        JobBar.Value = progress?.Fraction ?? 0;

        var error = _model.TranscriptionErrors.GetValueOrDefault(_id);
        ErrorBar.Message = error is null ? "" : $"Transcription failed: {error}";
        ErrorBar.IsOpen = error is not null;

        var canTranscribe = !running && !conversation.HasTranscript && conversation.HasAudio
            && conversation.Status is not (ConversationStatus.Recording or ConversationStatus.Failed);
        TranscribeButton.Content = error is null ? "Transcribe" : "Retry";
        TranscribeButton.Visibility = canTranscribe ? Visibility.Visible : Visibility.Collapsed;

        var key = (conversation.HasTranscript, conversation.Status);
        if (_loadedFor != key)
        {
            _loadedFor = key;
            _ = LoadAsync(conversation);
        }
    }

    /// <summary>Reads the transcript and finds the audio off the UI thread, then shows the lines and opens the player.</summary>
    private async Task LoadAsync(Conversation conversation)
    {
        var generation = ++_loadGeneration;
        var folder = conversation.Folder;
        var hasTranscript = conversation.HasTranscript;
        // The file of a recording in progress is still being written.
        var recording = conversation.Status == ConversationStatus.Recording;
        var (transcript, audio) = await Task.Run(() => (hasTranscript ? ReadTranscript(folder) : null, recording ? null : CafPath(folder)));
        if (generation != _loadGeneration) return;

        _lines.Clear();
        var segments = transcript?.Segments.OrderBy(s => s.Start).ToList() ?? [];
        foreach (var segment in segments)
        {
            var name = transcript!.DisplayName(segment.Speaker);
            _lines.Add(new TranscriptLine(segment, name, ConversationText.Timestamp(segment.Start), segment.Text.Trim()));
        }
        EmptyText.Text = hasTranscript && transcript is null
            ? "The transcript could not be read."
            : recording ? "Recording in progress." : "No transcript yet.";
        EmptyText.Visibility = _lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _playback.Load(audio, segments, _model.Settings.PlaybackChannels);
        UpdatePlayback();
    }

    private void UpdatePlayback()
    {
        PlayButton.IsEnabled = _playback.IsReady;
        PlayButton.Content = _playback.IsPlaying ? "Pause" : "Play";
        TimeText.Text = _playback.Error
            ?? (_playback.IsReady
                ? $"{ConversationText.Timestamp(_playback.CurrentTime)} / {ConversationText.Timestamp(_playback.Duration)}"
                : "");

        var active = _playback.ActiveSegmentId is { } id ? _lines.FirstOrDefault(l => l.Segment.Id == id) : null;
        if (!Equals(LineList.SelectedItem, active))
        {
            LineList.SelectedItem = active;
            if (active is not null) LineList.ScrollIntoView(active);
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => _playback.TogglePlay();

    private void OnTranscribeClick(object sender, RoutedEventArgs e) => _ = _model.TranscribeAsync(_id);

    private void OnLineClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TranscriptLine line) _playback.SeekTo(line.Segment);
    }

    private static Transcript? ReadTranscript(string folder)
    {
        try
        {
            return Transcript.Read(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Debug.WriteLine($"EchoDiar: could not read the transcript in {folder}: {e}");
            return null;
        }
    }

    /// <summary>The recording's CAF file; other formats (m4a) play from Phase 14.</summary>
    private static string? CafPath(string folder) =>
        LibraryStore.AudioFiles(folder).FirstOrDefault(p => string.Equals(Path.GetExtension(p), ".caf", StringComparison.OrdinalIgnoreCase));
}
