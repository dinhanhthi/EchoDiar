using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoDiar.AI.Cloud;
using EchoDiar.App.Services;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using EchoDiar.Platform.SystemServices;
using EchoDiar.Transcribe;
using Microsoft.UI.Dispatching;

namespace EchoDiar.App.ViewModels;

public enum RouteKind { List, Conversation, Recording, Settings }

/// <summary>The conversation page's tab (macOS <c>AppModel.DetailTab</c>).</summary>
public enum DetailTab { Transcript, Summary, Chat }

/// <summary>What the main window shows: the library list, one conversation, the recording page or Settings.</summary>
public sealed record Route(RouteKind Kind, Guid? ConversationId = null)
{
    public static Route List { get; } = new(RouteKind.List);

    public static Route Recording { get; } = new(RouteKind.Recording);

    public static Route Settings { get; } = new(RouteKind.Settings);

    public static Route ForConversation(Guid id) => new(RouteKind.Conversation, id);
}

/// <summary>
/// The app state the screens show (the Windows counterpart of the macOS <c>AppModel</c>, MVP subset): navigation, the
/// library, recording, transcription jobs and model downloads. Lives on the UI thread; every service event is
/// dispatched to it.
/// </summary>
public sealed partial class AppViewModel : ObservableObject
{
    /// <summary>15 Hz: the elapsed time and level meters while recording.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(67);

    /// <summary>How long <see cref="Shutdown"/> waits for a start under way and the stop that finishes the audio.</summary>
    private const uint ShutdownWaitMilliseconds = 10_000;

    private readonly AppServices _services;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly Dictionary<string, CancellationTokenSource> _downloadTokens = [];

    /// <summary>
    /// Launch: settings load, then recovery. A recording starts only after it, so it uses the loaded settings and
    /// recovery never marks the new session.
    /// </summary>
    private Task _recovery = Task.CompletedTask;

    /// <summary>
    /// The window closed: a start under way stops its recording instead of leaving it running, and nothing reaches the
    /// UI any more (no property change, no dispatched event, no timer, no step after a recording).
    /// </summary>
    private volatile bool _closing;

    /// <summary>The engine start under way, which <see cref="Shutdown"/> waits for before stopping.</summary>
    private Task<string?>? _startTask;

    /// <summary>The running recording's folder, for the steps after it ends.</summary>
    private string? _activeFolder;

    private Route _route = Route.List;
    private DetailTab _detailTab = DetailTab.Transcript;
    private AppSettings _settings = new();
    private IReadOnlyList<Conversation> _conversations = [];
    private RecordingEngine.Snapshot _recordingSnapshot = new();
    private double _micLevel;
    private double _systemLevel;
    private bool _isStarting;
    private string? _recordingError;
    private IReadOnlyDictionary<Guid, JobProgress> _transcriptionProgress = new Dictionary<Guid, JobProgress>();
    private IReadOnlyDictionary<Guid, string> _transcriptionErrors = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<string, double> _downloads = new Dictionary<string, double>();
    private IReadOnlyDictionary<string, string> _downloadErrors = new Dictionary<string, string>();
    private IReadOnlySet<string> _installedModelIds = new HashSet<string>();
    private IReadOnlyList<AudioProcessGroup> _recordTargets = [];

    internal AppViewModel(AppServices services, DispatcherQueue dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;
        _pollTimer = dispatcher.CreateTimer();
        _pollTimer.Interval = PollInterval;
        _pollTimer.Tick += (_, _) => PollRecording();
        Updater = new Updater(dispatcher);
        Updater.PropertyChanged += OnUpdaterChanged;
    }

    // State

    public Route Route
    {
        get => _route;
        private set
        {
            // Another page (list, entry, Settings) clears the search (macOS MainView.swift), before the route changes: the
            // library lists every conversation again before the page and the sidebar are shown. Another conversation or
            // the recording keeps it, for the sidebar it filters.
            if (PageOf(value) != PageOf(_route)) SearchTerm = "";
            SetProperty(ref _route, value);
        }
    }

    /// <summary>The conversation page's tab: kept while another conversation opens from the sidebar.</summary>
    public DetailTab DetailTab
    {
        get => _detailTab;
        set => SetProperty(ref _detailTab, value);
    }

    public AppSettings Settings
    {
        get => _settings;
        private set
        {
            // Turning "wait" off releases a job waiting for energy saver.
            _services.Deferral.Enabled = value.DeferTranscriptionWhenConstrained;
            if (!SetProperty(ref _settings, value)) return;
            OnPropertyChanged(nameof(RecordingLanguage));
            SyncMeetingDetection();
        }
    }

    /// <summary>The library, newest first.</summary>
    public IReadOnlyList<Conversation> Conversations
    {
        get => _conversations;
        private set
        {
            if (SetProperty(ref _conversations, value)) _ = LoadSearchDocumentsAsync();
        }
    }

    /// <summary>The recording state, with the elapsed time read at 15 Hz while recording.</summary>
    public RecordingEngine.Snapshot RecordingSnapshot
    {
        get => _recordingSnapshot;
        private set
        {
            if (SetProperty(ref _recordingSnapshot, value)) OnPropertyChanged(nameof(IsRecording));
        }
    }

    public bool IsRecording => RecordingSnapshot.State != RecordingEngine.State.Idle;

    /// <summary>The mic's peak level on a -60...0 dBFS scale (0...1), while recording.</summary>
    public double MicLevel
    {
        get => _micLevel;
        private set => SetProperty(ref _micLevel, value);
    }

    /// <summary>The system audio's peak level on a -60...0 dBFS scale (0...1), while recording.</summary>
    public double SystemLevel
    {
        get => _systemLevel;
        private set => SetProperty(ref _systemLevel, value);
    }

    /// <summary>A start is under way (waiting for recovery or opening the devices).</summary>
    public bool IsStarting
    {
        get => _isStarting;
        private set => SetProperty(ref _isStarting, value);
    }

    /// <summary>Why the last start or resume failed, or why the engine stopped on its own.</summary>
    public string? RecordingError
    {
        get => _recordingError;
        private set => SetProperty(ref _recordingError, value);
    }

    /// <summary>Queued and running jobs by conversation id, and "needs model" results.</summary>
    public IReadOnlyDictionary<Guid, JobProgress> TranscriptionProgress
    {
        get => _transcriptionProgress;
        private set => SetProperty(ref _transcriptionProgress, value);
    }

    /// <summary>The last failed job's error by conversation id.</summary>
    public IReadOnlyDictionary<Guid, string> TranscriptionErrors
    {
        get => _transcriptionErrors;
        private set => SetProperty(ref _transcriptionErrors, value);
    }

    /// <summary>Running model downloads: catalog id to fraction (0...1).</summary>
    public IReadOnlyDictionary<string, double> Downloads
    {
        get => _downloads;
        private set => SetProperty(ref _downloads, value);
    }

    /// <summary>The last failed download's error by catalog id.</summary>
    public IReadOnlyDictionary<string, string> DownloadErrors
    {
        get => _downloadErrors;
        private set => SetProperty(ref _downloadErrors, value);
    }

    /// <summary>Catalog ids of the installed models.</summary>
    public IReadOnlySet<string> InstalledModelIds
    {
        get => _installedModelIds;
        private set
        {
            var old = _installedModelIds;
            if (!SetProperty(ref _installedModelIds, value)) return;
            OnPropertyChanged(nameof(HasTranscriptionModel));
            OnPropertyChanged(nameof(NeedsDiarizer));
            if (EmbeddingInstallChanged(old, value)) SyncLibraryIndex();
        }
    }

    /// <summary>At least one speech recognition model is installed.</summary>
    public bool HasTranscriptionModel => ModelCatalog.Asr.Any(e => InstalledModelIds.Contains(e.Id));

    /// <summary>"Tell speakers apart" is on but no diarization model is installed and the default diarizer is no configured cloud one.</summary>
    public bool NeedsDiarizer =>
        Settings.Diarization && !ModelCatalog.Diarization.Any(e => InstalledModelIds.Contains(e.Id)) && !CloudDiarizerAvailable;

    /// <summary>The apps that can be recorded on their own, from the last refresh.</summary>
    public IReadOnlyList<AudioProcessGroup> RecordTargets
    {
        get => _recordTargets;
        private set => SetProperty(ref _recordTargets, value);
    }

    // Launch

    /// <summary>Loads the settings, recovers sessions a crash left open, then lists the library and the models.</summary>
    public async Task StartAsync()
    {
        _services.Library.Changed += list =>
        {
            StartupTrace.Write($"Library.Changed raised ({list.Count} conversations)");
            Post(() =>
            {
                StartupTrace.Write($"begin Conversations = {list.Count}");
                Conversations = list;
                StartupTrace.Write("end Conversations");
                OnLibraryListed();
            });
        };
        _services.Pipeline.Progress += progress => Post(() => ApplyProgress(progress));
        _services.Engine.SnapshotChanged += snapshot => Post(() => ApplySnapshot(snapshot));
        // An install that failed its check was deleted: its rows show Download again.
        _services.Models.InstallRemoved += removed => Post(() => _ = RefreshInstalledModelsAsync());
        FollowRouteChanges();
        _recovery = LoadAndRecoverAsync();
        await _recovery;
        // Made with the loaded settings, so the cloud engines' keys are read for every saved provider (off the UI thread).
        ProvidersModel.Changed += OnProvidersChanged;
        // With the loaded settings (detection on or off).
        StartMeetingServices();
        // With the loaded settings (each shortcut on or off).
        StartHotKeys();
        // With the settings, before the library (macOS AppModel.start()).
        if (!Settings.OnboardingCompleted && !_closing) OnboardingRequested?.Invoke();
        StartupTrace.Write("begin library load");
        var list = await _services.Library.RefreshAsync();
        StartupTrace.Write($"end library load ({list.Count} conversations)");
        await RefreshInstalledModelsAsync();
        StartupTrace.Write($"installed models: {InstalledModelIds.Count}");
        _launchScan = CompressLeftoversAsync();
        // Auto-delete's launch run waits for the scan (RunRetentionAsync).
        SyncRetention();
        _ = VerifyInstalledModelsAsync();
        // After the library, so its check does not compete with the launch.
        if (!_closing) Updater.Start();
    }

    /// <summary>
    /// Once launch has settled (after the leftover scan): each installed model without a valid marker (an install from
    /// before markers) is hashed now, one at a time on a lowest-priority thread, so a load or a recording start seldom
    /// has to (<see cref="ModelStore.VerifiedFolder"/>; one that fails is deleted and its row refreshed).
    /// </summary>
    private async Task VerifyInstalledModelsAsync()
    {
        await _launchScan;
        var entries = ModelCatalog.All.Where(e => InstalledModelIds.Contains(e.Id) && !Downloads.ContainsKey(e.Id)).ToList();
        if (entries.Count == 0 || _closing) return;
        await Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Lowest;
            foreach (var entry in entries)
            {
                if (_closing) return;
                try
                {
                    _services.Models.VerifiedFolder(entry);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"EchoDiar: could not verify model {entry.Id}: {e.GetType().Name}");
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private async Task LoadAndRecoverAsync()
    {
        Settings = await Task.Run(() =>
        {
            StartupTrace.Write("begin settings load");
            var settings = _services.Settings.Load();
            StartupTrace.Write("end settings load");
            return settings;
        });
        OnPropertyChanged(nameof(NeedsDiarizer));
        StartupTrace.Write("settings applied");
        await Task.Run(Recover);
    }

    /// <summary>
    /// Stops a running recording before the app quits, so its audio file is finished; a start under way is waited for
    /// and stops itself. The last settings write is waited for too, then the live transcribers' unload (which does not
    /// decide the result). All run off the UI thread, waited for at most 10 s without pumping its messages: a managed
    /// wait on this STA thread would run queued XAML work while the window closes. False when the wait timed out (a
    /// device open that hangs holds the engine): the engine must then not be touched again, and launch recovery
    /// finishes the session next time.
    /// </summary>
    public bool Shutdown()
    {
        _closing = true;
        // On the UI thread, before anything waits (the system events' window lives on it); silent, as _closing is set.
        StopMeetingServices();
        // Same thread rule: the shortcuts' window lives on the UI thread.
        StopHotKeys();
        // The AI job stops and keeps what it streamed; nothing waits for it.
        ShutdownAI();
        // The index pass stops; nothing waits for it either.
        ShutdownRAG();
        // An update check under way is cancelled (an install goes on: Windows quits the app for it).
        Updater.Dispose();
        _pollTimer.Stop();
        // A run under way asks before each deletion, and is told every conversation is busy now.
        _retention?.Stop();
        foreach (var token in _downloadTokens.Values) token.Cancel();
        // Queued compressions never start; one under way is cut off by the exit and redone at the next launch.
        _compressionCancel.Cancel();
        // A settings change still waiting for its debounce is written now, and waited for with the stop.
        FlushSettings();
        var settingsWrite = _settingsWrite;
        // Live transcribers stop now, in parallel with the recording's stop; waited for after it, apart.
        var transcribers = DisposeRecordingTranscribers();
        var waited = Stopwatch.StartNew();
        var start = _startTask;
        var stop = Task.Run(() =>
        {
            try
            {
                start?.Wait();
            }
            catch (AggregateException)
            {
                // A failed start left nothing running.
            }
            _services.Engine.Stop();
        });
        var handle = ((IAsyncResult)Task.WhenAll(stop, settingsWrite)).AsyncWaitHandle.SafeWaitHandle;
        if (WaitForSingleObject(handle, ShutdownWaitMilliseconds) != WaitObject0)
        {
            Debug.WriteLine("EchoDiar: the recording did not stop in time on quit.");
            return false;
        }
        if (stop.Exception is { } error) Debug.WriteLine($"EchoDiar: could not stop recording on quit: {error}");
        // The engine no longer calls the transcribers; one still decoding only delays the exit, within the same 10 s.
        var left = (uint)Math.Max(0, ShutdownWaitMilliseconds - waited.ElapsedMilliseconds);
        var transcribersHandle = ((IAsyncResult)transcribers).AsyncWaitHandle.SafeWaitHandle;
        if (WaitForSingleObject(transcribersHandle, left) != WaitObject0)
        {
            Debug.WriteLine("EchoDiar: the live transcriber did not stop in time on quit.");
        }
        return true;
    }

    private const uint WaitObject0 = 0;

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeWaitHandle handle, uint milliseconds);

    /// <summary>Runs <paramref name="action"/> on the UI thread, unless the window closed before or meanwhile.</summary>
    private void Post(Action action)
    {
        if (_closing) return;
        _dispatcher.TryEnqueue(() =>
        {
            if (!_closing) action();
        });
    }

    /// <summary>Silent once the window closed: its controls fail every call, and a failure in a dispatched callback ends the app.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_closing) base.OnPropertyChanged(e);
    }

    private void Recover()
    {
        DateTimeOffset? startedAt = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            startedAt = process.StartTime;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Without a start time every session with this pid counts as this process's own.
        }
        StartupTrace.Write("begin recovery");
        var recovered = SessionRecovery.Recover(_services.Paths.Conversations, ProcessInfo.IsAlive, Environment.ProcessId, startedAt);
        foreach (var result in recovered) Debug.WriteLine($"EchoDiar: {result.State} {result.Folder} ({result.Duration:0.0} s)");
        StartupTrace.Write($"end recovery ({recovered.Count} recovered)");
    }

    // Navigation

    /// <summary>
    /// Shows the conversation: on its Transcript tab from the library list, on the shown tab from the sidebar (macOS
    /// ConversationCard <c>open</c>).
    /// </summary>
    public void Open(Guid id)
    {
        if (Route.Kind == RouteKind.List) DetailTab = DetailTab.Transcript;
        Route = Route.ForConversation(id);
    }

    public void ShowRecording() => Route = Route.Recording;

    /// <summary>The library list, from any page, without the library chat (macOS showLibrary, ⌘2).</summary>
    public void ShowLibrary()
    {
        Route = Route.List;
        GlobalChatOpen = false;
    }

    /// <summary>The main window was closed to the tray: it opens again on the list, without a search (macOS windowWillClose).</summary>
    internal void MainWindowHidden()
    {
        if (_closing) return;
        Route = Route.List;
        SearchTerm = "";
    }

    /// <summary>
    /// The onboarding window should show, from its first step (macOS <c>OnboardingWindow.shared.show</c>): at launch
    /// until it is completed, and from "Show onboarding again". Raised on the UI thread.
    /// </summary>
    public event Action? OnboardingRequested;

    /// <summary>"Show onboarding again" in General: not completed until Done again (macOS AppModel <c>showOnboarding</c>).</summary>
    public void ShowOnboarding()
    {
        if (_closing) return;
        ApplySettings(Settings with { OnboardingCompleted = false });
        OnboardingRequested?.Invoke();
    }

    /// <summary>The onboarding's Done: saved through the settings queue like every settings change.</summary>
    public void CompleteOnboarding() => ApplySettings(Settings with { OnboardingCompleted = true });

    /// <summary>
    /// Back: a conversation or the recording goes to the list (the library chat stays open), the list closes the library
    /// chat; Settings goes to the page it was opened from, or the list when that conversation is gone or that recording has
    /// ended (macOS AppModel <c>goBack</c>).
    /// </summary>
    public void GoBack()
    {
        if (Route.Kind == RouteKind.List) GlobalChatOpen = false;
        else Route = Route.Kind == RouteKind.Settings ? SettingsReturnRoute() : Route.List;
    }

    /// <summary>The pages that slide on macOS (MainView.swift <c>Page</c>): a conversation and the recording share one.</summary>
    public static int PageOf(Route route) => route.Kind switch
    {
        RouteKind.List => 0,
        RouteKind.Settings => 2,
        _ => 1,
    };

    // Deleting

    /// <summary>Not while it is recorded, compressed or transcribed.</summary>
    public bool CanDelete(Conversation conversation) =>
        conversation.Status is not (ConversationStatus.Recording or ConversationStatus.Compressing) && !IsTranscribing(conversation.Id);

    /// <summary>
    /// Raised on the UI thread before a conversation's audio or folder is deleted: its open player must close the
    /// file first, or the move to the Recycle Bin finds it in use.
    /// </summary>
    public event Action<Guid>? ReleasingAudio;

    /// <summary>
    /// Moves the conversation to the Recycle Bin; its page goes back to the list first, and its AI job is cancelled first
    /// so it does not write into the moved folder.
    /// </summary>
    public async Task DeleteConversationAsync(Guid id)
    {
        ReleasingAudio?.Invoke(id);
        if (Route.ConversationId == id) Route = Route.List;
        await CancelAIJobAsync(id);
        try
        {
            await _services.Library.DeleteAsync(id);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not delete conversation {id}: {e}");
        }
    }

    /// <summary>Moves the conversation's audio to the Recycle Bin; its transcript, summary and chat stay.</summary>
    public async Task DeleteAudioAsync(Guid id)
    {
        ReleasingAudio?.Invoke(id);
        try
        {
            await _services.Library.DeleteAudioAsync(id);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not delete the audio of conversation {id}: {e}");
        }
    }

    // Recording

    /// <summary>Records <paramref name="app"/>'s audio (null: all system audio) and the mic, after launch recovery.</summary>
    public async Task StartRecordingAsync(AudioProcessGroup? app)
    {
        if (IsRecording || IsStarting || _closing) return;
        if (Updater.IsInstalling)
        {
            // Windows closes the app once the update is downloaded: the recording would be cut off.
            AutoStartedAppName = null;
            RecordingError = InstallingNotice;
            return;
        }
        IsStarting = true;
        RecordingError = null;
        RecordingSourceName = app?.Name;
        RecordingSourceRootPid = app?.RootPid;
        var root = _services.Paths.Conversations;
        try
        {
            await _recovery;
            var language = RecordingLanguage;
            var micEndpointId = Settings.MicDeviceUID;
            // Before the engine starts, with the engine's first marker language, so the prior's engine is the one the
            // Stop job resolves to.
            await AttachRecordingTranscriberAsync(language);
            var target = CaptureTarget.Global;
            _startTask = Task.Run<string?>(() =>
            {
                if (_closing) return null;
                target = Resolved(app);
                var folder = _services.Engine.Start(target, language, micEndpointId, root);
                if (!_closing) return folder;
                // The window closed while the devices opened: nothing else would stop this recording.
                _services.Engine.Stop();
                return null;
            });
            var folder = await _startTask;
            // The window may have closed while the devices opened: Shutdown stopped the timer for good.
            if (_closing) return;
            _activeFolder = folder;
            if (_activeFolder is null) return;
            _pollTimer.Start();
            BeginEndWatch(target);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not start recording: {e}");
            CancelRecordingTranscriber();
            RecordingSourceName = null;
            RecordingSourceRootPid = null;
            AutoStartedAppName = null;
            RecordingError = e is RecordingDiskFullException
                ? "There is not enough free disk space to record."
                : $"The recording could not start ({e.Message}).";
        }
        finally
        {
            IsStarting = false;
        }
    }

    /// <summary>Stops the recording, else records the picked target (macOS <c>toggleRecording</c>).</summary>
    public void ToggleRecording()
    {
        if (IsRecording) _ = StopRecordingAsync();
        else _ = StartRecordingAsync(SelectedTarget);
    }

    /// <summary>Pauses (both devices closed) or resumes the recording.</summary>
    public async Task TogglePauseAsync()
    {
        var state = RecordingSnapshot.State;
        if (state == RecordingEngine.State.Idle) return;
        var transcriber = _recordingTranscriber;
        try
        {
            await Task.Run(() =>
            {
                if (state == RecordingEngine.State.Recording)
                {
                    _services.Engine.Pause();
                    // The writer flushed its last frames through the sink before Pause returned.
                    transcriber?.Flush();
                }
                else
                {
                    _services.Engine.Resume();
                }
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not resume recording: {e}");
            RecordingError = $"The recording could not resume ({e.Message}).";
        }
    }

    /// <summary>Stops the recording; the end snapshot runs the steps after it.</summary>
    public Task StopRecordingAsync() => Task.Run(() => _services.Engine.Stop());

    /// <summary>Reloads the apps for the target picker, off the UI thread.</summary>
    public async Task RefreshRecordTargetsAsync()
    {
        try
        {
            RecordTargets = await Task.Run(() => AudioSessions.Groups());
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not list audio apps: {e}");
        }
    }

    /// <summary>A picked app gets its current processes (they may have changed since the picker was filled).</summary>
    private static CaptureTarget Resolved(AudioProcessGroup? app)
    {
        if (app is null) return CaptureTarget.Global;
        var fresh = AudioSessions.Groups().FirstOrDefault(g => g.Exe == app.Exe);
        return CaptureTarget.ForApp(fresh ?? app);
    }

    private void PollRecording()
    {
        if (_closing) return;
        var snapshot = _services.Engine.CurrentSnapshot();
        if (snapshot.State == RecordingEngine.State.Idle)
        {
            StopPolling();
            return;
        }
        RecordingSnapshot = snapshot;
        var (mic, system) = _services.Engine.CurrentLevels();
        MicLevel = Fraction(mic);
        SystemLevel = Fraction(system);
    }

    private void StopPolling()
    {
        _pollTimer.Stop();
        MicLevel = 0;
        SystemLevel = 0;
    }

    /// <summary>The single consumer of the engine's snapshots: an end snapshot, user stop or not, finishes the recording.</summary>
    private void ApplySnapshot(RecordingEngine.Snapshot snapshot)
    {
        if (_closing) return;
        RecordingSnapshot = snapshot;
        if (snapshot.State != RecordingEngine.State.Idle)
        {
            if (!_pollTimer.IsRunning) _pollTimer.Start();
            return;
        }
        StopPolling();
        RecordingSourceName = null;
        RecordingSourceRootPid = null;
        if (snapshot.EndReason is not { } reason) return;
        EndEndWatch();
        // The next recording starts with the default language again.
        if (_languageOverride is not null)
        {
            _languageOverride = null;
            OnPropertyChanged(nameof(RecordingLanguage));
        }
        if (StopMessage(reason) is { } message) RecordingError = message;
        var folder = _activeFolder;
        _activeFolder = null;
        var transcriber = TakeRecordingTranscriber();
        if (folder is not null) _ = FinishRecordingAsync(folder, transcriber);
        else if (transcriber is not null) CancelRecordingTranscriber(transcriber);
    }

    /// <summary>
    /// Lists the recording, opens it when the recording page is showing, then compresses and transcribes it with what
    /// the live transcriber transcribed, which drains meanwhile.
    /// </summary>
    private async Task FinishRecordingAsync(string folder, RecordingTranscriber? transcriber)
    {
        var prior = transcriber is null ? null : FinishRecordingTranscriberAsync(transcriber);
        if (_closing) return;
        try
        {
            var list = await _services.Library.RefreshAsync();
            var session = await Task.Run(() => ReadSession(folder));
            if (_closing) return;
            if (Route.Kind == RouteKind.Recording)
            {
                DetailTab = DetailTab.Transcript;
                Route = session is not null && list.Any(c => c.Id == session.Id) ? Route.ForConversation(session.Id) : Route.List;
            }
            await CompressAndTranscribeAsync(folder, session, prior);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not finish {folder}: {e}");
        }
    }

    private static string? StopMessage(RecordingEngine.EndReason reason) => reason switch
    {
        RecordingEngine.EndReason.DiskFull => "Recording stopped: disk is full.",
        RecordingEngine.EndReason.WriteFailed => "Recording stopped: could not write audio.",
        RecordingEngine.EndReason.CaptureFailed => "Recording stopped: audio capture failed.",
        _ => null,
    };

    /// <summary>A peak level (0...1) on a -60...0 dBFS scale.</summary>
    private static double Fraction(float level) =>
        level > 0 ? Math.Clamp((20 * Math.Log10(level) + 60) / 60, 0, 1) : 0;

    // Transcription

    /// <summary>Queued or running, or still between its recording's end and compression; a "needs model" result is not.</summary>
    public bool IsTranscribing(Guid id) => _finishing.Contains(id) || HasJob(id);

    /// <summary>A job is queued or running; a "needs model" result is not.</summary>
    private bool HasJob(Guid id) =>
        TranscriptionProgress.TryGetValue(id, out var progress) && progress.Step != JobStep.NeedsModel;

    /// <summary>
    /// The conversation's Transcribe and Retry, and the transcript's Re-transcribe (<paramref name="engineId"/>: a
    /// catalog or cloud engine id from "Retry with another engine" or "Transcribe again with", null = the default resolution;
    /// <paramref name="language"/> replaces the recording's language markers, null keeps them): queues it unless a job
    /// for it is active or it has no audio.
    /// </summary>
    public async Task TranscribeAsync(Guid id, string? engineId = null, string? language = null)
    {
        if (IsTranscribing(id) || Conversations.FirstOrDefault(c => c.Id == id) is not { HasAudio: true } conversation) return;
        if (language is not null) RememberLanguage(language);
        var session = await Task.Run(() => ReadSession(conversation.Folder));
        // The launch scan may have started compressing it meanwhile.
        if (session is not null && !IsTranscribing(id)) _ = SubmitTranscription(conversation.Folder, session, engineId, language);
    }

    /// <summary>
    /// Queues a job with <paramref name="engineId"/>, else the default engine when it is installed and handles the
    /// recording's first language (else the pipeline's default resolution), and the speaker settings;
    /// <paramref name="language"/> replaces the recording's language markers. A failed or probe recording is never
    /// transcribed. <paramref name="deleteAudioAfter"/> null = "Delete audio after transcription", read when the job
    /// succeeds (macOS <c>submitTranscription</c>). <paramref name="prior"/>: what the recording's live transcriber
    /// transcribed (see <see cref="TranscriptionJob.Prior"/>). <paramref name="processNow"/> runs it even while energy
    /// saver would defer it. A cloud engine or diarizer runs as <see cref="PlanCloud"/> decides; the first run per
    /// provider and destination waits for the main window's confirmation instead (nothing is queued or uploaded then).
    /// Returns the job (true when it succeeded), null when none was queued.
    /// </summary>
    private Task<bool>? SubmitTranscription(
        string folder, RecordingSession session, string? engineId = null, string? language = null, bool? deleteAudioAfter = null,
        PriorTranscription? prior = null, bool processNow = false)
    {
        var id = session.Id;
        if (_closing || session.Probe || session.State == SessionState.Failed || HasJob(id)) return null;
        if (Updater.IsInstalling)
        {
            // Windows closes the app once the update is downloaded: the job would be cut off.
            TranscriptionErrors = With(TranscriptionErrors, id, InstallingNotice);
            return null;
        }
        if (PlanCloud(engineId, id, folder, language) is not { } cloud) return null;
        engineId = cloud.EngineId;
        var firstLanguage = language ?? session.LanguageMarkers.MinBy(m => m.FileTime)?.Language ?? "auto";
        var job = new TranscriptionJob
        {
            ConversationID = id,
            Folder = folder,
            // The mono fallback: session.json names the mic file; the system file sits next to it.
            SystemFileName = session.AudioFileName == AudioCompressor.MicFileName ? AudioCompressor.SystemFileName : null,
            EngineID = engineId ?? PreferredEngine(firstLanguage),
            Language = language,
            ProcessNow = processNow,
            SpeakerCountHint = Settings.SpeakerCountHint,
            DiarizerID = Settings.DefaultDiarizerModelID,
            Diarize = Settings.Diarization,
            Prior = prior,
        };
        TranscriptionErrors = Without(TranscriptionErrors, id);
        // Set now so the screens show the job before the pipeline's first event arrives.
        TranscriptionProgress = With(TranscriptionProgress, id, new JobProgress(id, JobStep.Queued, 0));
        return RunTranscriptionAsync(job, deleteAudioAfter, cloud);
    }

    /// <summary>
    /// True when the job succeeded; its audio then goes to the Recycle Bin when asked, before the reload. The cloud
    /// engines of <paramref name="cloud"/> are made with their keys off the UI thread.
    /// </summary>
    private async Task<bool> RunTranscriptionAsync(TranscriptionJob job, bool? deleteAudioAfter, CloudPlan cloud)
    {
        var succeeded = false;
        try
        {
            await Task.Run(() =>
            {
                var asr = cloud.Asr is { } config ? _services.MakeCloudEngine(config) : null;
                var diarizer = cloud.Diarizer is { } diarizerConfig
                    ? _services.MakeCloudEngine(diarizerConfig, CloudTranscription.DiarizationModel(diarizerConfig))
                    : null;
                return _services.Pipeline.TranscribeAsync(job, asr, diarizer);
            });
            succeeded = true;
        }
        catch (TranscriptionException e) when (e.Kind == TranscriptionErrorKind.Cloud)
        {
            // The kind only ("HTTP 400", "invalid API key"): the reason can quote the provider's error body.
            Debug.WriteLine($"EchoDiar: cloud transcription failed for {job.Folder}: {e.Detail.Split(':').Take(2).LastOrDefault()?.Trim()}");
        }
        catch (Exception e)
        {
            // The pipeline's Failed or NeedsModel event carries it to the screens.
            Debug.WriteLine($"EchoDiar: transcription failed for {job.Folder}: {e}");
        }
        if (succeeded && !_closing && (deleteAudioAfter ?? Settings.DeleteAudioAfterTranscription))
        {
            await DeleteAudioAsync(job.ConversationID);
        }
        try
        {
            await _services.Library.ReloadAsync(job.ConversationID);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not reload {job.Folder}: {e}");
        }
        return succeeded;
    }

    /// <summary>The default ASR model from the settings when it is installed and handles <paramref name="language"/>; else null.</summary>
    private string? PreferredEngine(string language) =>
        Settings.DefaultASRModelID is { } id
        && ModelCatalog.Entry(id) is { Kind: ModelKind.Asr } entry
        && _services.Models.IsInstalled(entry)
        && TranscriptionPipeline.Supports(entry, language)
            ? id
            : null;

    /// <summary>Keeps one entry per job; the conversation is reloaded at each step change (the pipeline writes its status).</summary>
    private void ApplyProgress(JobProgress progress)
    {
        var id = progress.ConversationID;
        TranscriptionProgress.TryGetValue(id, out var previous);
        switch (progress.Step)
        {
            case JobStep.Finished:
                TranscriptionProgress = Without(TranscriptionProgress, id);
                break;
            case JobStep.Failed:
                TranscriptionProgress = Without(TranscriptionProgress, id);
                TranscriptionErrors = With(TranscriptionErrors, id, progress.Detail ?? "Transcription failed.");
                break;
            default:
                // Whole percents only: the screens redraw at most 100 times per job step.
                if (previous is null || previous.Step != progress.Step || Percent(previous) != Percent(progress))
                {
                    TranscriptionProgress = With(TranscriptionProgress, id, progress);
                }
                break;
        }
        if (previous?.Step != progress.Step) _ = ReloadAsync(id);
    }

    private async Task ReloadAsync(Guid id)
    {
        try
        {
            await _services.Library.ReloadAsync(id);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not reload {id}: {e}");
        }
    }

    private static int Percent(JobProgress progress) => (int)(progress.Fraction * 100);

    private static RecordingSession? ReadSession(string folder)
    {
        try
        {
            return RecordingSession.Read(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // Models

    /// <summary>Downloads a catalog model into <c>Models\&lt;id&gt;\</c>, with its progress in <see cref="Downloads"/>.</summary>
    public void DownloadModel(string id)
    {
        if (Downloads.ContainsKey(id) || ModelCatalog.Entry(id) is not { } entry) return;
        var cancellation = new CancellationTokenSource();
        _downloadTokens[id] = cancellation;
        DownloadErrors = Without(DownloadErrors, id);
        Downloads = With(Downloads, id, 0.0);
        _ = RunDownloadAsync(entry, cancellation);
    }

    public void CancelDownload(string id)
    {
        if (_downloadTokens.TryGetValue(id, out var cancellation)) cancellation.Cancel();
    }

    private async Task RunDownloadAsync(ModelEntry entry, CancellationTokenSource cancellation)
    {
        var progress = new DownloadProgress(fraction => Post(() =>
        {
            if (Downloads.ContainsKey(entry.Id)) Downloads = With(Downloads, entry.Id, fraction);
        }));
        try
        {
            await Task.Run(() => _services.Models.DownloadAsync(entry, progress, cancellation.Token));
            // Any installed model can stand in for a missing default: jobs that needed one can run now.
            TranscriptionProgress = TranscriptionProgress
                .Where(p => p.Value.Step != JobStep.NeedsModel)
                .ToDictionary(p => p.Key, p => p.Value);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: model download failed for {entry.Id}: {e}");
            DownloadErrors = With(DownloadErrors, entry.Id, e.Message);
        }
        finally
        {
            _downloadTokens.Remove(entry.Id);
            cancellation.Dispose();
            Downloads = Without(Downloads, entry.Id);
        }
        await RefreshInstalledModelsAsync();
    }

    private async Task RefreshInstalledModelsAsync()
    {
        try
        {
            InstalledModelIds = await Task.Run(() =>
            {
                StartupTrace.Write("begin installed models scan");
                var ids = (IReadOnlySet<string>)ModelCatalog.All.Where(_services.Models.IsInstalled).Select(e => e.Id).ToHashSet();
                StartupTrace.Write("end installed models scan");
                return ids;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"EchoDiar: could not read the installed models: {e}");
        }
    }

    /// <summary>Reports a download's fraction at most every half percent, from any thread.</summary>
    private sealed class DownloadProgress(Action<double> report) : IProgress<ModelDownloadProgress>
    {
        private int _step = -1;

        public void Report(ModelDownloadProgress value)
        {
            var step = (int)(value.Fraction * 200);
            int last;
            do
            {
                last = Volatile.Read(ref _step);
                if (step <= last) return;
            }
            while (Interlocked.CompareExchange(ref _step, step, last) != last);
            report(value.Fraction);
        }
    }

    // Immutable dictionary updates: every change is a new instance, so bindings see it.

    private static Dictionary<TKey, TValue> With<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> source, TKey key, TValue value)
        where TKey : notnull => new(source) { [key] = value };

    private static IReadOnlyDictionary<TKey, TValue> Without<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> source, TKey key)
        where TKey : notnull
    {
        if (!source.ContainsKey(key)) return source;
        var copy = new Dictionary<TKey, TValue>(source);
        copy.Remove(key);
        return copy;
    }
}
