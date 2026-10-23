using System.ComponentModel;
using System.Diagnostics;
using EchoDiar.App.ViewModels;
using EchoDiar.App.Views;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Windowing;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace EchoDiar.App.Services;

/// <summary>
/// The floating pill (macOS FloatingPillController.swift): a topmost tool window that never takes focus, kept out of
/// screen captures unless "Show it in screenshots and screen recordings" is on. Shows while recording (unless "Show
/// floating indicator" is off, or the main window is in front and <c>hidePillWhenAppActive</c> is on) and for every
/// prompt (Phase 20). Its top-right corner stays put while its content grows or shrinks. Drags are saved per monitor
/// (default: the work area's top-right corner); with "Attach to meeting window" it follows the recorded app's largest
/// window instead, at the offset it was dragged to from that window's top-right corner (default: just inside it).
/// <para>UI thread only. <see cref="Dispose"/> closes the window: call it when the main window closes.</para>
/// </summary>
internal sealed class FloatingPillController : IDisposable
{
    /// <summary>A drag is saved this long after its last move (macOS <c>panelMoved</c>).</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly AppViewModel _model;
    private readonly Window _mainWindow;
    private readonly DispatcherQueueTimer _saveTimer;
    private FloatingPillWindow? _window;
    private nint _hwnd;
    private bool _shown;
    private bool _mainActive;
    private bool _disposed;

    /// <summary>The window's frame was set once: later shows start from the monitor it is on.</summary>
    private bool _placed;

    /// <summary>The next <see cref="Place"/> starts from the saved spot; otherwise it keeps the top-right corner.</summary>
    private bool _restoreSaved = true;

    private WindowFollower? _follower;

    /// <summary>The meeting window's frame while following it and it has a visible window.</summary>
    private RectInt32? _anchorFrame;

    /// <summary>The pill's top-right corner from the meeting window's while following; set at once by a drag; null = inside it.</summary>
    private PillPosition? _windowOffset;

    public FloatingPillController(AppViewModel model, Window mainWindow, DispatcherQueue dispatcher)
    {
        _model = model;
        _mainWindow = mainWindow;
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = SaveDelay;
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SavePosition();
        _model.PropertyChanged += OnModelChanged;
        _mainWindow.Activated += OnMainActivated;
        Evaluate();
    }

    /// <summary>Saves a drag still waiting for its delay, lets go of the meeting window, and closes the pill's window.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.PropertyChanged -= OnModelChanged;
        try
        {
            _mainWindow.Activated -= OnMainActivated;
        }
        catch (Exception e)
        {
            // The main window is closing and may refuse calls; the handler checks _disposed anyway.
            Debug.WriteLine($"EchoDiar: could not detach from the main window: {e.Message}");
        }
        if (_saveTimer.IsRunning)
        {
            _saveTimer.Stop();
            SavePosition();
        }
        StopFollowing();
        if (_window is not { } window) return;
        window.ContentSizeChanged -= OnContentSizeChanged;
        window.Dragged -= OnDragged;
        window.AppWindow.Changed -= OnAppWindowChanged;
        window.Detach();
        _window = null;
        try
        {
            window.Close();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not close the pill: {e}");
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _model.IsClosing) return;
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.Settings):
                // Any settings change applies it again (macOS sets sharingType in every apply).
                if (_hwnd != 0) PillWindowStyle.SetShownInCaptures(_hwnd, _model.Settings.ShowPillInScreenCaptures);
                Evaluate();
                break;
            case nameof(AppViewModel.IsRecording):
            case nameof(AppViewModel.AutoStartedAppName):
            case nameof(AppViewModel.PillPrompt):
            case nameof(AppViewModel.PillAppRootPid):
                Evaluate();
                break;
        }
    }

    /// <summary>The pill never activates, so the main window stays active while the pill is clicked.</summary>
    private void OnMainActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_disposed || _model.IsClosing) return;
        _mainActive = args.WindowActivationState != WindowActivationState.Deactivated;
        Evaluate();
    }

    /// <summary>
    /// Shows or hides the pill and follows the app's window as the state asks (macOS <c>wanted</c> and <c>apply</c>):
    /// a prompt always shows it; recording shows it with "Show floating indicator" (or for an automatic start's
    /// first 10 s), unless the main window is in front and "Hide it while an EchoDiar window is in front" is on.
    /// </summary>
    private void Evaluate()
    {
        var settings = _model.Settings;
        var showPill = settings.ShowPill || _model.AutoStartedAppName is not null;
        var indicator = _model.IsRecording && showPill && !(settings.HidePillWhenAppActive && _mainActive);
        var show = _model.PillPrompt is not null || indicator;
        if (!show)
        {
            Hide();
            return;
        }
        EnsureWindow();
        SyncAnchor(settings.AttachPillToWindow ? _model.PillAppRootPid : null);
        Show();
    }

    /// <summary>
    /// The window, made once: no border or title bar, not resizable, then the pill style (tool window, no activation,
    /// topmost), before it is first shown.
    /// </summary>
    private void EnsureWindow()
    {
        if (_window is not null) return;
        var window = new FloatingPillWindow(_model);
        var appWindow = window.AppWindow;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
        }
        _hwnd = Win32Interop.GetWindowFromWindowId(appWindow.Id);
        if (!PillWindowStyle.Apply(_hwnd)) Debug.WriteLine("EchoDiar: the pill's window style was not fully applied.");
        PillWindowStyle.SetShownInCaptures(_hwnd, _model.Settings.ShowPillInScreenCaptures);
        appWindow.Changed += OnAppWindowChanged;
        window.ContentSizeChanged += OnContentSizeChanged;
        window.Dragged += OnDragged;
        _window = window;
    }

    private void Show()
    {
        if (_shown || _window is not { } window) return;
        _shown = true;
        window.Attach();
        _restoreSaved = true;
        Place();
        // Never Activate(): it would take the focus from the meeting app.
        window.AppWindow.Show(activateWindow: false);
    }

    private void Hide()
    {
        if (!_shown || _window is not { } window) return;
        _shown = false;
        StopFollowing();
        window.AppWindow.Hide();
        window.Detach();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_disposed || !args.DidSizeChange) return;
        PillWindowStyle.UpdateShape(_hwnd);
    }

    private void OnContentSizeChanged(SizeInt32 size)
    {
        if (_disposed) return;
        Place();
    }

    // Placement

    /// <summary>
    /// The top-right corner at its offset from the followed window's, else where it is, else at the saved spot of the
    /// pill's monitor (default: its work area's top-right corner); clamped into that monitor's work area (macOS
    /// <c>place</c>). The size is the content's, or a stand-in until its first layout.
    /// </summary>
    private void Place()
    {
        if (_window is not { } window) return;
        var content = window.ContentSize;
        var current = CurrentFrame(window);
        var anchor = _anchorFrame ?? current;
        if (PillWindowStyle.MonitorFor(anchor) is not { } monitor) return;
        var width = content.Width > 0 ? content.Width : (int)Math.Ceiling(100 * monitor.Scale);
        var height = content.Height > 0 ? content.Height : (int)Math.Ceiling(36 * monitor.Scale);
        RectInt32 frame;
        if (_anchorFrame is { } windowFrame)
        {
            frame = PillWindowStyle.FrameAt(windowFrame, _windowOffset, width, height, monitor.Scale, monitor.WorkArea);
        }
        else if (!_restoreSaved)
        {
            frame = PillWindowStyle.FrameAt(current, new PillPosition { X = 0, Y = 0 }, width, height, monitor.Scale, monitor.WorkArea);
        }
        else
        {
            var saved = _model.Settings.PillPositions.GetValueOrDefault(monitor.Key);
            frame = PillWindowStyle.FrameAt(monitor.WorkArea, saved, width, height, monitor.Scale, monitor.WorkArea);
        }
        _restoreSaved = false;
        _placed = true;
        window.AppWindow.MoveAndResize(frame);
    }

    /// <summary>The pill's frame once placed; before that the main window's, whose monitor it first shows on.</summary>
    private RectInt32 CurrentFrame(FloatingPillWindow window)
    {
        var appWindow = _placed ? window.AppWindow : _mainWindow.AppWindow;
        return new RectInt32(appWindow.Position.X, appWindow.Position.Y, appWindow.Size.Width, appWindow.Size.Height);
    }

    /// <summary>A drag: while following, the offset from the window changes at once; saved 0.5 s after the last move.</summary>
    private void OnDragged()
    {
        if (_disposed || _window is not { } window) return;
        if (_anchorFrame is { } anchor && PillWindowStyle.MonitorFor(anchor) is { } monitor)
        {
            _windowOffset = PillWindowStyle.OffsetOf(CurrentFrame(window), anchor, monitor.Scale);
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>While following: the offset from the window; otherwise the spot on the pill's monitor (macOS <c>savePosition</c>).</summary>
    private void SavePosition()
    {
        if (_window is not { } window) return;
        if (_anchorFrame is not null)
        {
            _model.SavePillWindowOffset(_windowOffset);
            return;
        }
        var frame = CurrentFrame(window);
        if (PillWindowStyle.MonitorFor(frame) is not { } monitor) return;
        _model.SavePillPosition(monitor.Key, PillWindowStyle.OffsetOf(frame, monitor.WorkArea, monitor.Scale));
    }

    // Following the meeting window

    /// <summary>Follows <paramref name="rootPid"/>'s largest window; when that stops, the pill goes back to the saved spot.</summary>
    private void SyncAnchor(int? rootPid)
    {
        if (rootPid == _follower?.RootPid) return;
        var wasFollowing = _follower is not null;
        StopFollowing();
        if (rootPid is { } pid)
        {
            _windowOffset = _model.Settings.PillWindowOffset;
            try
            {
                // Reports the window's frame at once, which places the pill.
                _follower = new WindowFollower(pid, OnAnchorChanged);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not follow the meeting window: {e}");
            }
        }
        if (wasFollowing && _follower is null)
        {
            _restoreSaved = true;
            Place();
        }
    }

    private void StopFollowing()
    {
        _follower?.Dispose();
        _follower = null;
        _anchorFrame = null;
    }

    /// <summary>The followed window moved or changed; null (no visible window) = the saved spot. Never throws.</summary>
    private void OnAnchorChanged(RectInt32? frame)
    {
        try
        {
            if (_disposed) return;
            _anchorFrame = frame;
            if (frame is null) _restoreSaved = true;
            Place();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not place the pill by the meeting window: {e}");
        }
    }
}
