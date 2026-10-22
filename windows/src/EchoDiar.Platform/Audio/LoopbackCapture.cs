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
public sealed class LoopbackCapture : IDisposable
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    private readonly CaptureLoop _loop;

    private LoopbackCapture(string name, Func<IAudioClient> activate, Func<IAudioClient, CaptureFormat> initialize)
    {
        _loop = new CaptureLoop(name, activate, initialize);
    }

    /// <summary>Audio played by <paramref name="pid"/> and its child processes.</summary>
    public static LoopbackCapture ForProcessTree(int pid) =>
        ProcessLoopback(pid, PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE);

    /// <summary>All system audio except what <paramref name="ownPid"/> and its child processes play.</summary>
    public static LoopbackCapture AllExcept(int ownPid) =>
        ProcessLoopback(ownPid, PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE);

    /// <summary>
    /// Classic loopback of the default render endpoint at its mix format: the fallback when process
    /// loopback fails. It also captures EchoDiar's own playback.
    /// </summary>
    public static LoopbackCapture Endpoint() => new(
        "EchoDiar endpoint loopback",
        () => CaptureLoop.ActivateEndpoint(null, EDataFlow.eRender),
        client => CaptureLoop.InitializeWithMixFormat(client, PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK));

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

    private static unsafe LoopbackCapture ProcessLoopback(int pid, PROCESS_LOOPBACK_MODE mode) => new(
        "EchoDiar process loopback",
        () => ActivateProcessLoopback(pid, mode),
        client =>
        {
            // Process loopback has no mix format (GetMixFormat fails): ask for 48 kHz Float32 stereo.
            var format = new WAVEFORMATEX
            {
                wFormatTag = (ushort)PInvoke.WAVE_FORMAT_IEEE_FLOAT,
                nChannels = 2,
                nSamplesPerSec = 48_000,
                wBitsPerSample = 32,
                nBlockAlign = 2 * 4,
                nAvgBytesPerSec = 48_000 * 2 * 4,
            };
            return CaptureLoop.Initialize(
                client,
                PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK | PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                &format);
        });

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
