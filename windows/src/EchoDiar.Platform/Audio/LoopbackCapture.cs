using EchoDiar.Core.Audio;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

namespace EchoDiar.Platform.Audio;

/// <summary>
/// Captures what apps play, downmixed to mono Float32: one process tree, everything except one
/// process tree (both process loopback, 48 kHz), or the whole default render endpoint.
/// </summary>
public sealed class LoopbackCapture : ICaptureSource
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    private readonly CaptureLoop _loop;
    private string _name;

    private LoopbackCapture(
        string threadName, string name, Func<LoopbackCapture, IAudioClient> activate, Func<IAudioClient, CaptureFormat> initialize)
    {
        _name = name;
        _loop = new CaptureLoop(threadName, () => activate(this), initialize);
    }

    /// <summary>Audio played by <paramref name="pid"/> and its child processes.</summary>
    /// <param name="name">The app's display name (the group's name) for <see cref="Name"/>; null gives "Process &lt;pid&gt;".</param>
    public static LoopbackCapture ForProcessTree(int pid, string? name = null) =>
        ProcessLoopback(pid, name ?? $"Process {pid}", PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE);

    /// <summary>All system audio except what <paramref name="ownPid"/> and its child processes play.</summary>
    public static LoopbackCapture AllExcept(int ownPid) =>
        ProcessLoopback(ownPid, "All system audio", PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE);

    /// <summary>
    /// Classic loopback of the default render endpoint at its mix format: the fallback when process
    /// loopback fails. It also captures EchoDiar's own playback. <see cref="Name"/> is the endpoint's friendly name.
    /// </summary>
    public static LoopbackCapture Endpoint() => new(
        "EchoDiar endpoint loopback",
        "System audio",
        capture => CaptureLoop.ActivateEndpoint(null, EDataFlow.eRender, name => capture._name = name),
        client => CaptureLoop.InitializeWithMixFormat(client, PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK));

    /// <summary>What is captured: "All system audio", the app's name, or the render endpoint's friendly name
    /// (known once <see cref="Start"/> returns).</summary>
    public string Name => _name;

    /// <inheritdoc cref="CaptureLoop.Failed"/>
    public event Action<Exception>? Failed
    {
        add => _loop.Failed += value;
        remove => _loop.Failed -= value;
    }

    /// <summary>The stream rate in Hz, known once <see cref="Start"/> returns.</summary>
    public int SampleRate => _loop.SampleRate;

    /// <inheritdoc cref="CaptureLoop.Start"/>
    public void Start(Action<ReadOnlySpan<float>, long, int> onSamples) => _loop.Start(onSamples);

    public void Stop() => _loop.Stop();

    public void Dispose() => _loop.Dispose();

    private static LoopbackCapture ProcessLoopback(int pid, string name, PROCESS_LOOPBACK_MODE mode) => new(
        "EchoDiar process loopback",
        name,
        _ => ActivateProcessLoopback(pid, mode),
        // Process loopback has no mix format (GetMixFormat fails): ask for 48 kHz Float32 stereo.
        client => CaptureLoop.InitializeFloat48kStereo(
            client,
            PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK | PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM));

    private static unsafe IAudioClient ActivateProcessLoopback(int pid, PROCESS_LOOPBACK_MODE mode)
    {
        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
        };
        activationParams.ProcessLoopbackParams.TargetProcessId = (uint)pid;
        activationParams.ProcessLoopbackParams.ProcessLoopbackMode = mode;

        var propVariant = new PROPVARIANT();
        propVariant.vt = VARENUM.VT_BLOB;
        propVariant.blob.cbSize = (uint)sizeof(AUDIOCLIENT_ACTIVATION_PARAMS);
        propVariant.blob.pBlobData = (byte*)&activationParams;

        // activationParams and propVariant live on this stack frame, which waits for the completion.
        var handler = new ActivationHandler();
        PInvoke.ActivateAudioInterfaceAsync(
            PInvoke.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
            typeof(IAudioClient).GUID,
            propVariant,
            handler,
            out var operation).ThrowOnFailure();
        if (!handler.Completed.Wait(ActivationTimeout)) throw new TimeoutException("Process loopback activation did not complete.");
        GC.KeepAlive(operation);
        return handler.Client
            ?? throw (handler.Error ?? new InvalidOperationException("Process loopback activation returned no audio client."));
    }

    /// <summary>
    /// ActivateAudioInterfaceAsync completion. CsWin32 0.3.366 generates [ComImport] interfaces, so this
    /// is a plain class exposed through built-in COM interop. ActivateAudioInterfaceAsync rejects
    /// handlers that are not agile (E_ILLEGAL_METHOD_CALL), hence IAgileObject.
    /// </summary>
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Completed = new();
        public IAudioClient? Client;
        public Exception? Error;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out var result, out var activated);
                result.ThrowOnFailure();
                Client = (IAudioClient)activated;
            }
            catch (Exception error)
            {
                Error = error; // rethrown by ActivateProcessLoopback on the waiting thread
            }
            finally
            {
                Completed.Set();
            }
        }
    }
}
