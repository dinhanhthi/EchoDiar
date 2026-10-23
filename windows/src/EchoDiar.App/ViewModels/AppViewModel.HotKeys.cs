using System.Diagnostics;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.SystemServices;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// The global shortcuts (macOS AppModel, "Global shortcut"): <c>globalHotKeyWindows</c> starts or stops a recording and
/// <c>globalPauseHotKeyWindows</c> pauses or resumes it, from any app and while the window is hidden. Registered at
/// launch once the settings are loaded, again when Settings › Shortcuts changes them, off while a recorder there takes a
/// new one, and unregistered at <see cref="Shutdown"/>. UI thread only (<see cref="GlobalHotKeys"/> lives on it).
/// </summary>
public sealed partial class AppViewModel
{
    private const int RecordHotKeyId = 1;
    private const int PauseHotKeyId = 2;

    private GlobalHotKeys? _hotKeys;
    private bool _capturingHotKey;
    private string? _hotKeyError;
    private string? _pauseHotKeyError;

    /// <summary>Why the global record shortcut could not be registered; null when registered or off.</summary>
    public string? HotKeyError
    {
        get => _hotKeyError;
        private set => SetProperty(ref _hotKeyError, value);
    }

    /// <summary>Why the global pause shortcut could not be registered; null when registered or off.</summary>
    public string? PauseHotKeyError
    {
        get => _pauseHotKeyError;
        private set => SetProperty(ref _pauseHotKeyError, value);
    }

    /// <summary>A recorder in Settings › Shortcuts is taking a new shortcut: the registered ones are off meanwhile.</summary>
    public bool IsCapturingHotKey => _capturingHotKey;

    /// <summary>
    /// While a recorder takes a new shortcut the current ones are unregistered, so pressing one reaches the recorder
    /// instead of toggling a recording (macOS <c>setHotKeyCapture</c>).
    /// </summary>
    public void SetHotKeyCapture(bool capturing)
    {
        if (capturing == _capturingHotKey) return;
        _capturingHotKey = capturing;
        OnPropertyChanged(nameof(IsCapturingHotKey));
        SyncHotKeys();
    }

    /// <summary>Creates the shortcuts' window and registers them; on the normal launch path only, once the settings are loaded.</summary>
    private void StartHotKeys()
    {
        if (_closing || _hotKeys is not null) return;
        try
        {
            var hotKeys = new GlobalHotKeys(Trace);
            hotKeys.Pressed += OnHotKeyPressed;
            _hotKeys = hotKeys;
        }
        catch (Exception e)
        {
            // The app runs on without global shortcuts.
            Debug.WriteLine($"EchoDiar: could not set up the global shortcuts: {e}");
            return;
        }
        SyncHotKeys();
    }

    /// <summary>Unregisters the shortcuts; at quit, on the UI thread.</summary>
    private void StopHotKeys()
    {
        try
        {
            _hotKeys?.Dispose();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not unregister the global shortcuts: {e}");
        }
        _hotKeys = null;
    }

    /// <summary>
    /// Registers each shortcut when on (and no recorder is taking one), else unregisters it; a refusal is shown under it
    /// (macOS <c>syncHotKey</c>).
    /// </summary>
    private void SyncHotKeys()
    {
        if (_hotKeys is null || _closing) return;
        HotKeyError = Register(RecordHotKeyId, Settings.GlobalHotKeyEnabled && !_capturingHotKey ? Settings.GlobalHotKeyWindows : null);
        PauseHotKeyError = Register(
            PauseHotKeyId, Settings.GlobalPauseHotKeyEnabled && !_capturingHotKey ? Settings.GlobalPauseHotKeyWindows : null);
    }

    /// <summary>Replaces shortcut <paramref name="id"/> with <paramref name="key"/> (null only unregisters); why it failed, or null.</summary>
    private string? Register(int id, WindowsHotKey? key)
    {
        if (_hotKeys is null) return null;
        if (key is not { } hotKey)
        {
            _hotKeys.Unregister(id);
            return null;
        }
        var error = _hotKeys.Register(id, hotKey);
        if (error != 0) Trace($"hot key {id}: RegisterHotKey failed, {error}");
        return error switch
        {
            0 => null,
            GlobalHotKeys.ErrorAlreadyRegistered => Loc.Format(
                "%@ is already used by another app. Pick another shortcut.", GlobalHotKeys.Describe(hotKey)),
            _ => Loc.Format("%@ could not be registered (error %lld).", GlobalHotKeys.Describe(hotKey), error),
        };
    }

    /// <summary>Inside the shortcuts' window procedure: the action runs after it (macOS toggleRecording and togglePause).</summary>
    private void OnHotKeyPressed(int id)
    {
        Trace($"hot key {id} pressed");
        Post(() =>
        {
            switch (id)
            {
                case RecordHotKeyId:
                    ToggleRecording();
                    break;
                case PauseHotKeyId:
                    _ = TogglePauseAsync();
                    break;
            }
        });
    }

    /// <summary>A shortcut or its on/off changed (macOS <c>updateSettings</c> hotKeyChanged).</summary>
    private static bool HotKeysChanged(AppSettings old, AppSettings updated) =>
        old.GlobalHotKeyWindows != updated.GlobalHotKeyWindows
        || old.GlobalHotKeyEnabled != updated.GlobalHotKeyEnabled
        || old.GlobalPauseHotKeyWindows != updated.GlobalPauseHotKeyWindows
        || old.GlobalPauseHotKeyEnabled != updated.GlobalPauseHotKeyEnabled;
}
