using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Transcribe;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>One library entry: the title, then the date, duration and status.</summary>
public sealed partial record ConversationRow(Guid Id, string Title, string Detail);

/// <summary>The library, newest first, with the model download banner (MVP; Phase 11 completes it).</summary>
public sealed partial class ConversationListView : UserControl
{
    private readonly AppViewModel _model;
    private readonly ObservableCollection<ConversationRow> _rows = [];

    public ConversationListView(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        ConversationList.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            UpdateRows();
            UpdateBanner();
        };
        Unloaded += (_, _) => _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.Conversations):
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.TranscriptionErrors):
                UpdateRows();
                break;
            case nameof(AppViewModel.InstalledModelIds):
            case nameof(AppViewModel.Settings):
            case nameof(AppViewModel.Downloads):
            case nameof(AppViewModel.DownloadErrors):
                UpdateBanner();
                break;
        }
    }

    /// <summary>Replaces only the rows that changed, so the list keeps its scroll position.</summary>
    private void UpdateRows()
    {
        var rows = _model.Conversations
            .Select(c => new ConversationRow(c.Id, c.Title, ConversationText.Detail(c, _model)))
            .ToList();
        if (rows.Count == _rows.Count && rows.Select(r => r.Id).SequenceEqual(_rows.Select(r => r.Id)))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] != _rows[i]) _rows[i] = rows[i];
            }
        }
        else
        {
            _rows.Clear();
            foreach (var row in rows) _rows.Add(row);
        }
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The models still missing: speech models until one is installed, then speaker models while "Tell speakers
    /// apart" is on and none is installed.
    /// </summary>
    private void UpdateBanner()
    {
        var needsSpeech = !_model.HasTranscriptionModel;
        var visible = needsSpeech || _model.NeedsDiarizer;
        ModelBanner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;

        BannerTitle.Text = needsSpeech ? "Download a transcription model" : "Download a speaker model";
        BannerDetail.Text = needsSpeech
            ? "Recordings are transcribed on this PC. Pick a speech recognition model to download."
            : "\"Tell speakers apart\" is on. Download a model to tell the other speakers apart.";
        var entries = (needsSpeech ? ModelCatalog.Asr : ModelCatalog.Diarization)
            .Where(e => !_model.InstalledModelIds.Contains(e.Id))
            .ToList();
        var selected = (ModelPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        if (!entries.Select(e => e.Id).SequenceEqual(ModelPicker.Items.OfType<ComboBoxItem>().Select(i => i.Tag as string)))
        {
            ModelPicker.Items.Clear();
            foreach (var entry in entries) ModelPicker.Items.Add(new ComboBoxItem { Content = entry.DisplayName, Tag = entry.Id });
            var keep = entries.FindIndex(e => e.Id == selected);
            var fallback = entries.FindIndex(e => e.IsDefault);
            ModelPicker.SelectedIndex = keep >= 0 ? keep : Math.Max(0, fallback);
        }

        var running = _model.Downloads.FirstOrDefault(d => entries.Any(e => e.Id == d.Key));
        var downloading = running.Key is not null;
        ModelPicker.IsEnabled = !downloading;
        DownloadButton.IsEnabled = !downloading && entries.Count > 0;
        CancelButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadBar.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadBar.Value = running.Value;

        var error = entries.Select(e => _model.DownloadErrors.GetValueOrDefault(e.Id)).FirstOrDefault(m => m is not null);
        DownloadErrorText.Text = error is null ? "" : $"The download failed: {error}";
        DownloadErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRecordClick(object sender, RoutedEventArgs e) => _model.ShowRecording();

    private void OnConversationClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ConversationRow row) _model.Open(row.Id);
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if ((ModelPicker.SelectedItem as ComboBoxItem)?.Tag is string id) _model.DownloadModel(id);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        foreach (var id in _model.Downloads.Keys.ToList()) _model.CancelDownload(id);
    }
}

/// <summary>Texts shared by the library list and the conversation page.</summary>
internal static class ConversationText
{
    /// <summary>"m:ss", or "h:mm:ss" from one hour.</summary>
    public static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>The date, the duration and the status.</summary>
    public static string Detail(Conversation conversation, AppViewModel model)
    {
        var created = conversation.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return $"{created} · {Clock(conversation.Duration)} · {Status(conversation, model)}";
    }

    /// <summary>The job's step and percent while one is queued or running, else the stored status.</summary>
    public static string Status(Conversation conversation, AppViewModel model)
    {
        if (model.TranscriptionProgress.TryGetValue(conversation.Id, out var progress))
        {
            return progress.Step switch
            {
                JobStep.NeedsModel => "Needs a transcription model",
                JobStep.Queued => "Waiting to transcribe",
                _ => $"Transcribing {(int)(progress.Fraction * 100)}%",
            };
        }
        if (model.TranscriptionErrors.ContainsKey(conversation.Id)) return "Transcription failed";
        return conversation.Status switch
        {
            ConversationStatus.Recording => "Recording",
            ConversationStatus.Recovered => "Recovered",
            ConversationStatus.Compressing => "Compressing",
            ConversationStatus.CompressionFailed => "Compression failed",
            ConversationStatus.Ready => "Ready",
            ConversationStatus.Transcribing => "Transcribing",
            ConversationStatus.Transcribed => "Transcribed",
            ConversationStatus.Failed => "Recording failed",
            ConversationStatus.TranscriptionFailed => "Transcription failed",
            _ => conversation.HasTranscript ? "Transcribed" : "Not transcribed",
        };
    }
}
