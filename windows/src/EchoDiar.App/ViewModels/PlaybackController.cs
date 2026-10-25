using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using Microsoft.UI.Dispatching;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// Plays the open conversation's audio (macOS <c>PlaybackController</c>): one <see cref="AudioPlayer"/> per file,
/// the play head read at 4 Hz and only while playing, and the transcript segment under it. Lives on the UI thread.
/// </summary>
public sealed partial class PlaybackController : ObservableObject, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _ticker;
    private AudioPlayer? _player;
    private string? _path;
    private PlaybackChannels _channels = PlaybackChannels.Mixed;

    /// <summary>Sorted by start.</summary>
    private Transcript.Segment[] _segments = [];

    /// <summary>Bumped whenever the player closes, so a late end of the previous one is ignored.</summary>
    private int _generation;

    private bool _isPlaying;
    private double _currentTime;
    private double _duration;
    private Guid? _activeSegmentId;
    private bool _isReady;
    private string? _error;

    public PlaybackController(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _ticker = dispatcher.CreateTimer();
        _ticker.Interval = TickInterval;
        _ticker.Tick += (_, _) => Tick();
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    /// <summary>Seconds; updated at most 4 times a second while playing.</summary>
    public double CurrentTime
    {
        get => _currentTime;
        private set => SetProperty(ref _currentTime, value);
    }

    public double Duration
    {
        get => _duration;
        private set => SetProperty(ref _duration, value);
    }

    /// <summary>The segment under the play head; changes only when the play head enters another segment.</summary>
    public Guid? ActiveSegmentId
    {
        get => _activeSegmentId;
        private set => SetProperty(ref _activeSegmentId, value);
    }

    /// <summary>An audio file is open.</summary>
    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    /// <summary>Why the file could not be opened or played.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>
    /// Opens <paramref name="audioPath"/> (a CAF file) unless it is already open (null closes the player), and sets
    /// the segments to highlight and how the channels play.
    /// </summary>
    public void Load(string? audioPath, IReadOnlyList<Transcript.Segment> segments, PlaybackChannels channels)
    {
        _segments = [.. segments.OrderBy(s => s.Start)];
        if (audioPath != _path)
        {
            Close();
            _path = audioPath;
            _channels = channels;
            if (audioPath is not null) Open(audioPath, channels);
        }
        else
        {
            SetChannels(channels);
        }
        if (IsPlaying || CurrentTime > 0) UpdateActiveSegment();
    }

    /// <summary>
    /// Plays both voices in both ears, or as recorded (mic left, system right), switching the open player live: the
    /// play head and whether it plays are kept.
    /// </summary>
    public void SetChannels(PlaybackChannels channels)
    {
        if (channels == _channels) return;
        _channels = channels;
        _player?.SetChannels(channels);
    }

    public void TogglePlay()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    /// <summary>Plays from the play head; from the start when it is at the end.</summary>
    public void Play()
    {
        if (_player is null || Duration <= 0) return;
        try
        {
            _player.Play();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not start playback: {e}");
            Error = Loc.Format("Could not play the audio (%@).", e.Message);
            return;
        }
        Error = null;
        IsPlaying = true;
        UpdateActiveSegment();
        _ticker.Start();
    }

    public void Pause()
    {
        Tick();
        _player?.Pause();
        _ticker.Stop();
        IsPlaying = false;
    }

    /// <summary>Seeks to the segment's start and plays (a click on a transcript line).</summary>
    public void SeekTo(Transcript.Segment segment)
    {
        Seek(segment.Start);
        if (!IsPlaying) Play();
    }

    /// <summary>Moves the play head to <paramref name="time"/> seconds, still playing if it was.</summary>
    public void Seek(double time)
    {
        time = Math.Clamp(time, 0, Math.Max(0, Duration));
        _player?.Seek(time);
        CurrentTime = time;
        UpdateActiveSegment();
    }

    /// <summary>Stops and releases the player.</summary>
    public void Close()
    {
        _generation++;
        _ticker.Stop();
        _player?.Dispose();
        _player = null;
        _path = null;
        IsReady = false;
        IsPlaying = false;
        CurrentTime = 0;
        Duration = 0;
        ActiveSegmentId = null;
        Error = null;
    }

    public void Dispose() => Close();

    private void Open(string path, PlaybackChannels channels)
    {
        try
        {
            var player = new AudioPlayer(path, channels);
            var generation = _generation;
            player.Ended += error => _dispatcher.TryEnqueue(() => Finished(generation, error));
            _player = player;
            Duration = player.Duration;
            IsReady = true;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not open {path} for playback: {e}");
            Error = Loc.Format("Could not open the audio (%@).", e.Message);
        }
    }

    /// <summary>The end of the file (or a device failure): stops, clears the highlight and rewinds.</summary>
    private void Finished(int generation, Exception? error)
    {
        if (generation != _generation) return;
        _ticker.Stop();
        Tick();
        IsPlaying = false;
        if (error is not null)
        {
            // The play head stays where the device failed.
            Debug.WriteLine($"EchoDiar: playback stopped: {error}");
            Error = Loc.Format("Playback stopped (%@).", error.Message);
            return;
        }
        CurrentTime = 0;
        ActiveSegmentId = null;
    }

    /// <summary>Reads the play head while playing.</summary>
    private void Tick()
    {
        if (!IsPlaying || _player is null) return;
        CurrentTime = Math.Min(_player.Position, Duration);
        UpdateActiveSegment();
    }

    /// <summary>The last segment starting at or before the play head, while the play head is inside it.</summary>
    private void UpdateActiveSegment()
    {
        var index = Transcript.SegmentIndexAt(_segments, CurrentTime);
        ActiveSegmentId = index >= 0 ? _segments[index].Id : null;
    }
}
