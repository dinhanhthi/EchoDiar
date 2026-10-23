using EchoDiar.App.Services;
using EchoDiar.Core.Model;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// App Installer updates (<see cref="Services.Updater"/>): installing closes the app, so it never starts while work runs
/// that the close would cut off (macOS hides Update while recording; here transcriptions and AI jobs count too), and no
/// recording, transcription or AI job starts while it runs (<see cref="InstallingNotice"/> says why instead).
/// </summary>
public sealed partial class AppViewModel
{
    internal Updater Updater { get; }

    /// <summary>What a recording, transcription or AI job asked for while the update installs shows instead.</summary>
    private static string InstallingNotice => Loc.T("Installing the update…");

    /// <summary>
    /// A recording (or its start, or its last steps), a compression, a transcription or an AI job runs: the Update button
    /// hides and the tray item is off until it ends.
    /// </summary>
    public bool UpdateBlocked =>
        IsStarting || IndexingPaused || Conversations.Any(c => c.Status == ConversationStatus.Compressing);

    /// <summary>
    /// The install failed (or waits for the next launch): the notices it left (a transcription's failure reason, an AI
    /// panel's or the library chat's error) go, as nothing failed.
    /// </summary>
    private void OnUpdaterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Updater.IsInstalling) || Updater.IsInstalling) return;
        var notice = InstallingNotice;
        if (AIError?.Message == notice) AIError = null;
        if (TranscriptionErrors.Values.Contains(notice))
            TranscriptionErrors = TranscriptionErrors.Where(p => p.Value != notice).ToDictionary(p => p.Key, p => p.Value);
    }

    /// <summary>
    /// The confirmed update: the settings change still waiting for its delay is written first (Windows closes the app
    /// without the window's Closed), then the install starts. False when work started meanwhile.
    /// </summary>
    internal async Task<bool> InstallUpdateAsync()
    {
        if (_closing || UpdateBlocked) return false;
        FlushSettings();
        await _settingsWrite;
        if (_closing || UpdateBlocked) return false;
        await Updater.InstallAsync();
        return true;
    }
}
