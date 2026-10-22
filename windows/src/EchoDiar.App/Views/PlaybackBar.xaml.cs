using System.ComponentModel;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace EchoDiar.App.Views;

/// <summary>
/// Play or Pause, the scrubber, the play head and length, and the mixed or split channels of the conversation page's
/// <see cref="PlaybackController"/>. A press on the track pauses until it is released, then seeks there and plays on
/// (macOS PlaybackScrubber); the arrow keys seek at once. Reads the controller only while attached.
/// </summary>
public sealed partial class PlaybackBar : UserControl
{
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private AppViewModel? _model;
    private PlaybackController? _playback;

    /// <summary>The pointer holds the scrubber: its value previews the play head, which moves when it is released.</summary>
    private bool _dragging;

    /// <summary>Playback paused for the drag and goes on after it.</summary>
    private bool _resumeAfterDrag;

    /// <summary>The scrubber is being set from the play head, not by the user.</summary>
    private bool _settingValue;

    /// <summary>The state Play or Pause was last labeled for: a tooltip set again would close under the pointer.</summary>
    private bool? _shownPlaying;

    public PlaybackBar()
    {
        InitializeComponent();
        PlayButton.Click += (_, _) => _playback?.TogglePlay();
        Scrubber.ValueChanged += OnScrubberValueChanged;
        // The slider handles the pointer itself.
        Scrubber.AddHandler(PointerPressedEvent, new PointerEventHandler(OnScrubberPressed), handledEventsToo: true);
        Scrubber.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnScrubberReleased), handledEventsToo: true);
        Scrubber.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnScrubberReleased), handledEventsToo: true);
        Scrubber.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnScrubberReleased), handledEventsToo: true);
        ChannelsToggle.Click += (_, _) => OnChannelsClick();
    }

    /// <summary>Follows <paramref name="playback"/> and the channels setting; call when the page shows.</summary>
    public void Attach(AppViewModel model, PlaybackController playback)
    {
        Detach();
        _model = model;
        _playback = playback;
        model.PropertyChanged += OnModelChanged;
        playback.PropertyChanged += OnPlaybackChanged;
        UpdateChannels();
        Update();
    }

    /// <summary>Stops following; call when the page goes.</summary>
    public void Detach()
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        if (_playback is not null) _playback.PropertyChanged -= OnPlaybackChanged;
        _model = null;
        _playback = null;
        _dragging = false;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.Settings)) UpdateChannels();
    }

    /// <summary>Not once the window closed: its controls fail every call.</summary>
    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_model is { IsClosing: false }) Update();
    }

    private void Update()
    {
        if (_playback is not { } playback) return;
        PlayButton.IsEnabled = playback.IsReady;
        Scrubber.IsEnabled = playback.IsReady;
        if (playback.IsPlaying != _shownPlaying)
        {
            _shownPlaying = playback.IsPlaying;
            PlayIcon.Glyph = playback.IsPlaying ? PauseGlyph : PlayGlyph;
            var label = Loc.T(playback.IsPlaying ? "Pause" : "Play");
            AutomationProperties.SetName(PlayButton, label);
            ToolTipService.SetToolTip(PlayButton, label);
        }
        if (!_dragging)
        {
            _settingValue = true;
            Scrubber.Maximum = Math.Max(playback.Duration, 0.01);
            Scrubber.Value = Math.Clamp(playback.CurrentTime, 0, Scrubber.Maximum);
            _settingValue = false;
        }
        UpdateTime();
    }

    /// <summary>"mm:ss / mm:ss" (the dragged position while dragging), or why the audio does not play.</summary>
    private void UpdateTime()
    {
        if (_playback is not { } playback) return;
        var time = _dragging ? Scrubber.Value : playback.CurrentTime;
        TimeText.Text = playback.Error
            ?? (playback.IsReady ? $"{ConversationText.Timestamp(time)} / {ConversationText.Timestamp(playback.Duration)}" : "");
    }

    private void UpdateChannels()
    {
        if (_model is null) return;
        ChannelsToggle.IsChecked = _model.Settings.PlaybackChannels == PlaybackChannels.Split;
    }

    /// <summary>Saves the setting; the player follows it (ConversationView passes it on).</summary>
    private void OnChannelsClick()
    {
        _model?.SetPlaybackChannels(ChannelsToggle.IsChecked == true ? PlaybackChannels.Split : PlaybackChannels.Mixed);
    }

    // Scrubber

    private void OnScrubberPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_playback is not { IsReady: true } playback || _dragging) return;
        _dragging = true;
        _resumeAfterDrag = playback.IsPlaying;
        if (_resumeAfterDrag) playback.Pause();
    }

    private void OnScrubberReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (_playback is not { } playback) return;
        playback.Seek(Scrubber.Value);
        if (_resumeAfterDrag) playback.Play();
        Update();
    }

    /// <summary>While dragging, only the time follows; from the keyboard, the play head moves at once.</summary>
    private void OnScrubberValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_settingValue || _playback is not { IsReady: true } playback) return;
        if (_dragging)
        {
            UpdateTime();
            return;
        }
        playback.Seek(e.NewValue);
    }
}
