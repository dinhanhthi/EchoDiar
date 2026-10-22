using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using EchoDiar.Platform.SystemServices;
using EchoDiar.Transcribe;
using EchoDiar.Transcribe.Engines;

namespace EchoDiar.App.Services;

/// <summary>
/// The app's long-lived services over one library root: settings, library, models, the model load gate, the
/// transcription pipeline and the recording engine with the Platform capture sources. Created once, on launch.
/// </summary>
internal sealed class AppServices : IDisposable
{
    private readonly HttpClient _http;

    public AppServices(AppPaths paths)
    {
        Paths = paths;
        Settings = new SettingsStore(paths);
        Library = new LibraryStore(paths.Conversations, new ShellFiles());
        // No overall timeout: large models take longer than the 100 s default; cancellation stops a download.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Models = new ModelStore(paths.Models, _http);
        Gate = new ModelLoadGate();
        Pipeline = new TranscriptionPipeline(
            new Dictionary<ModelRuntime, TranscriptionPipeline.EngineFactory>
            {
                [ModelRuntime.WhisperNet] = WhisperEngine.Create,
                [ModelRuntime.SherpaOnnx] = ParakeetEngine.Create,
            },
            SherpaDiarizer.Create,
            Models.IsInstalled,
            Models.Folder,
            Gate);
        Engine = new RecordingEngine(OpenSystem, OpenMic)
        {
            IsTargetPlayingOutput = IsTargetPlayingOutput,
        };
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public LibraryStore Library { get; }
    public ModelStore Models { get; }
    public ModelLoadGate Gate { get; }
    public TranscriptionPipeline Pipeline { get; }
    public RecordingEngine Engine { get; }

    /// <summary>Stops a running recording (its CAF is finished) and releases the HTTP client.</summary>
    public void Dispose()
    {
        Engine.Dispose();
        _http.Dispose();
    }

    /// <summary>
    /// All system audio except EchoDiar's own process tree, or the picked app's process tree (named after the app in
    /// <c>session.json</c>); an app that cannot be isolated (<see cref="AudioProcessGroup.NeedsGlobalTap"/>) is
    /// recorded with all system audio.
    /// </summary>
    private static ICaptureSource OpenSystem(CaptureTarget target) =>
        target.TapMode == TapMode.PerApp && target.App is { } app
            ? LoopbackCapture.ForProcessTree(app.RootPid, app.Name)
            : LoopbackCapture.AllExcept(Environment.ProcessId);

    /// <summary>The input with this WASAPI endpoint id, null for the default input.</summary>
    private static ICaptureSource OpenMic(string? endpointId) => new MicCapture(endpointId);

    /// <summary>
    /// The silent-tap check for a picked app: whether its process tree plays. All system audio has no single process
    /// to ask, so the check is skipped there (false: a silent tap is never suspected).
    /// </summary>
    private static bool IsTargetPlayingOutput(CaptureTarget target) =>
        target.App is { } app && AudioSessions.IsPlayingOutput(app.RootPid);
}
