using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using EchoDiar.Core.Audio;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.KernelStreaming;
using Windows.Win32.Media.Multimedia;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace EchoDiar.Platform.Audio;

/// <summary>
/// Captures a microphone (the one with <c>endpointId</c>, else <see cref="AudioRouteMonitor.DefaultMicId"/>) in WASAPI
/// shared, event-driven mode at the device mix format, downmixed to mono Float32.
/// </summary>
public sealed class MicCapture : ICaptureSource
{
    private readonly CaptureLoop _loop;
    private string _name = "Microphone";

    public MicCapture(string? endpointId = null)
    {
        _loop = new CaptureLoop(
            "EchoDiar mic capture",
            () => CaptureLoop.ActivateEndpoint(endpointId, EDataFlow.eCapture, name => _name = name),
            client => CaptureLoop.InitializeWithMixFormat(client, 0),
            client => CaptureLoop.InitializeFloat48kStereo(
                client,
                PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY));
    }

    /// <inheritdoc cref="CaptureLoop.Failed"/>
    public event Action<Exception>? Failed
    {
        add => _loop.Failed += value;
        remove => _loop.Failed -= value;
    }

    /// <summary>The endpoint's friendly name, known once <see cref="Start"/> returns; "Microphone" before or without one.</summary>
    public string Name => _name;

    /// <summary>The device rate in Hz, known once <see cref="Start"/> returns.</summary>
    public int SampleRate => _loop.SampleRate;

    /// <inheritdoc cref="CaptureLoop.Format"/>
    public string? Format => _loop.Format;

    /// <inheritdoc cref="CaptureLoop.Start"/>
    public void Start(Action<ReadOnlySpan<float>, long, int> onSamples) => _loop.Start(onSamples);

    public void Stop() => _loop.Stop();

    public void Dispose() => _loop.Dispose();
}

/// <summary>A stream format the capture loop can downmix: Float32, or 16/24/32-bit integer PCM.</summary>
internal readonly record struct CaptureFormat(int SampleRate, int Channels, bool IsFloat, int BytesPerSample)
{
    public static unsafe CaptureFormat From(WAVEFORMATEX* format)
    {
        var isFloat = format->wFormatTag == PInvoke.WAVE_FORMAT_IEEE_FLOAT;
        var isPcm = format->wFormatTag == PInvoke.WAVE_FORMAT_PCM;
        if (format->wFormatTag == PInvoke.WAVE_FORMAT_EXTENSIBLE)
        {
            var subFormat = ((WAVEFORMATEXTENSIBLE*)format)->SubFormat;
            isFloat = subFormat == typeof(KSDATAFORMAT_SUBTYPE_IEEE_FLOAT).GUID;
            isPcm = subFormat == typeof(KSDATAFORMAT_SUBTYPE_PCM).GUID;
        }

        // wBitsPerSample is the container size: 32 for 24-bit samples left-justified in 32 bits.
        var bytes = format->wBitsPerSample / 8;
        var supported = (isFloat && bytes == 4) || (isPcm && bytes is 2 or 3 or 4);
        if (!supported || format->nChannels == 0)
        {
            throw new NotSupportedException(
                $"Unsupported capture format: tag {format->wFormatTag}, {format->wBitsPerSample} bits, {format->nChannels} channels.");
        }

        return new CaptureFormat((int)format->nSamplesPerSec, format->nChannels, isFloat, bytes);
    }
}

/// <summary>
/// The WASAPI capture thread shared by <see cref="MicCapture"/> and <see cref="LoopbackCapture"/>.
/// Every COM call runs on its own MTA thread: WASAPI objects created on the app's STA UI thread
/// cannot be called from another apartment.
/// </summary>
internal sealed class CaptureLoop : IDisposable
{
    private const long BufferDuration100ns = 1_000_000; // 100 ms engine buffer
    private const uint WaitTimeoutMs = 200; // endpoint loopback signals nothing while no app plays
    private const int AudclntEUnsupportedFormat = unchecked((int)0x88890008); // AUDCLNT_E_UNSUPPORTED_FORMAT

    private readonly string _name;
    private readonly Func<IAudioClient> _activate;
    private readonly Func<IAudioClient, CaptureFormat> _initialize;
    private readonly Func<IAudioClient, CaptureFormat>? _fallbackInitialize;
    private Action<ReadOnlySpan<float>, long, int>? _onSamples;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private bool _disposed;

    /// <param name="fallbackInitialize">
    /// Run on a newly activated client when <paramref name="initialize"/> fails with AUDCLNT_E_UNSUPPORTED_FORMAT.
    /// </param>
    public CaptureLoop(
        string name,
        Func<IAudioClient> activate,
        Func<IAudioClient, CaptureFormat> initialize,
        Func<IAudioClient, CaptureFormat>? fallbackInitialize = null)
    {
        _name = name;
        _activate = activate;
        _initialize = initialize;
        _fallbackInitialize = fallbackInitialize;
    }

    /// <summary>
    /// Raised on the capture thread when capture ends on an error after <see cref="Start"/> returned
    /// (device removed, callback threw). Without a handler the error is rethrown on the capture
    /// thread, which ends the process: an error is never dropped.
    /// </summary>
    public event Action<Exception>? Failed;

    public int SampleRate { get; private set; }

    /// <summary>
    /// The stream format, known once <see cref="Start"/> returns: "primary" (the mix format for
    /// endpoints) or "fallback" for the initialize that succeeded, then rate, channels and sample type.
    /// </summary>
    public string? Format { get; private set; }

    /// <summary>
    /// Opens the device and starts capturing; opening errors are thrown here. <c>onSamples</c> runs on
    /// the capture thread with mono samples (valid only during the call), the packet's QPC time in
    /// 100 ns units and the sample rate.
    /// </summary>
    public void Start(Action<ReadOnlySpan<float>, long, int> onSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) throw new InvalidOperationException("Capture is already running.");

        _onSamples = onSamples;
        _stopRequested = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = _name };
        _thread.Start();
        try
        {
            started.Task.GetAwaiter().GetResult();
        }
        catch
        {
            _thread.Join();
            _thread = null;
            throw;
        }
    }

    /// <summary>Stops capturing and waits for the capture thread. Do not call it from the callback.</summary>
    public void Stop()
    {
        if (_thread is null) return;
        _stopRequested = true;
        _thread.Join();
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        _disposed = true;
    }

    /// <param name="onName">Gets the endpoint's friendly name, when it has one, before the client is activated.</param>
    internal static IAudioClient ActivateEndpoint(string? endpointId, EDataFlow flow, Action<string>? onName = null)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? device = null;
        try
        {
            if (endpointId is null && flow == EDataFlow.eCapture) enumerator.GetDevice(AudioRouteMonitor.DefaultMicId(enumerator), out device);
            else if (endpointId is null) enumerator.GetDefaultAudioEndpoint(flow, ERole.eConsole, out device);
            else enumerator.GetDevice(endpointId, out device);
            if (onName is not null && FriendlyName(device) is { } name) onName(name);
            device.Activate(typeof(IAudioClient).GUID, CLSCTX.CLSCTX_ALL, null, out var client);
            return (IAudioClient)client;
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>The endpoint's display name (PKEY_Device_FriendlyName), or null when it cannot be read.</summary>
    internal static unsafe string? FriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        var value = default(PROPVARIANT);
        try
        {
            device.OpenPropertyStore(STGM.STGM_READ, out store);
            var key = PInvoke.PKEY_Device_FriendlyName;
            store.GetValue(&key, out value);
            return value.vt == VARENUM.VT_LPWSTR && value.pwszVal.Value != null ? value.pwszVal.ToString() : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            PInvoke.PropVariantClear(ref value);
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    internal static unsafe CaptureFormat InitializeWithMixFormat(IAudioClient client, uint streamFlags)
    {
        client.GetMixFormat(out var mixFormat);
        try
        {
            return Initialize(client, streamFlags, mixFormat);
        }
        finally
        {
            PInvoke.CoTaskMemFree(mixFormat);
        }
    }

    /// <summary>Initializes a 48 kHz Float32 stereo stream (with AUTOCONVERTPCM the engine converts to it).</summary>
    internal static unsafe CaptureFormat InitializeFloat48kStereo(IAudioClient client, uint streamFlags)
    {
        var format = new WAVEFORMATEX
        {
            wFormatTag = (ushort)PInvoke.WAVE_FORMAT_IEEE_FLOAT,
            nChannels = 2,
            nSamplesPerSec = 48_000,
            wBitsPerSample = 32,
            nBlockAlign = 2 * 4,
            nAvgBytesPerSec = 48_000 * 2 * 4,
        };
        return Initialize(client, streamFlags, &format);
    }

    /// <summary>Initializes a shared-mode, event-driven stream (EVENTCALLBACK is added here).</summary>
    internal static unsafe CaptureFormat Initialize(IAudioClient client, uint streamFlags, WAVEFORMATEX* format)
    {
        var captureFormat = CaptureFormat.From(format);
        client.Initialize(
            AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
            streamFlags | PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
            BufferDuration100ns,
            0,
            format,
            null);
        return captureFormat;
    }

    /// <summary>
    /// Gets the client's IAudioCaptureClient as a built-in COM wrapper. Not CsWin32's generic
    /// <c>GetService&lt;T&gt;</c>: it hands any object that answers QueryInterface for IInspectable to
    /// C#/WinRT, whose WinRT.IInspectable does not cast to [ComImport] interfaces, and the capture
    /// client of a microphone endpoint does answer it.
    /// </summary>
    private static unsafe IAudioCaptureClient GetCaptureClient(IAudioClient client)
    {
        var iid = typeof(IAudioCaptureClient).GUID;
        client.GetService(&iid, out var pointer);
        try
        {
            return (IAudioCaptureClient)Marshal.GetObjectForIUnknown((nint)pointer);
        }
        finally
        {
            Marshal.Release((nint)pointer); // the wrapper holds its own reference
        }
    }

    private static string Describe(string source, CaptureFormat format) =>
        $"{source} {format.SampleRate} Hz {format.Channels} ch {(format.IsFloat ? "float32" : $"int{format.BytesPerSample * 8}")}";

    private void Run(TaskCompletionSource started)
    {
        // MMCSS only raises the thread priority; capture still works when it fails (service off).
        uint taskIndex = 0;
        using var mmcss = PInvoke.AvSetMmThreadCharacteristics("Audio", ref taskIndex);

        IAudioClient? client = null;
        IAudioCaptureClient? captureClient = null;
        SafeFileHandle? audioEvent = null;
        var running = false;
        try
        {
            client = _activate();
            CaptureFormat format;
            string source;
            try
            {
                format = _initialize(client);
                source = "primary";
            }
            catch (COMException error) when (error.HResult == AudclntEUnsupportedFormat && _fallbackInitialize is not null)
            {
                // A client whose Initialize failed is not reused: activate a new one for the fallback format.
                Marshal.FinalReleaseComObject(client);
                client = null;
                client = _activate();
                format = _fallbackInitialize(client);
                source = "fallback";
            }

            audioEvent = PInvoke.CreateEvent(null, false, false, null);
            if (audioEvent.IsInvalid) throw new Win32Exception();
            client.SetEventHandle(audioEvent);
            client.GetBufferSize(out var bufferFrames);
            captureClient = GetCaptureClient(client);
            client.Start();
            SampleRate = format.SampleRate;
            Format = Describe(source, format);
            running = true;
            started.SetResult();

            Pump(captureClient, audioEvent, format, new float[bufferFrames]);
            client.Stop();
        }
        catch (Exception error)
        {
            if (!running) started.SetException(error);
            else if (Failed is { } failed) failed(error);
            else ExceptionDispatchInfo.Throw(error);
        }
        finally
        {
            audioEvent?.Dispose();
            if (captureClient is not null) Marshal.FinalReleaseComObject(captureClient);
            if (client is not null) Marshal.FinalReleaseComObject(client);
        }
    }

    private unsafe void Pump(IAudioCaptureClient captureClient, SafeFileHandle audioEvent, CaptureFormat format, float[] mono)
    {
        while (!_stopRequested)
        {
            if (PInvoke.WaitForSingleObject(audioEvent, WaitTimeoutMs) == WAIT_EVENT.WAIT_FAILED) throw new Win32Exception();

            while (true)
            {
                captureClient.GetNextPacketSize(out var packetFrames);
                if (packetFrames == 0) break;

                captureClient.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc100ns);
                var count = (int)frames;
                if (count > mono.Length) mono = new float[count];
                var samples = mono.AsSpan(0, count);
                if ((flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0) samples.Clear();
                else Downmix(data, format, samples);
                captureClient.ReleaseBuffer(frames);

                // Same clock as u64QPCPosition (QPC in 100 ns units), for packets without a valid one.
                if ((flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0)
                {
                    qpc100ns = (ulong)Stopwatch.GetElapsedTime(0).Ticks;
                }

                _onSamples!(samples, (long)qpc100ns, format.SampleRate);
            }
        }
    }

    private static unsafe void Downmix(byte* data, CaptureFormat format, Span<float> mono)
    {
        var channels = format.Channels;
        var scale = 1f / channels;
        for (var frame = 0; frame < mono.Length; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++) sum += Sample(data, frame * channels + channel, format);
            mono[frame] = sum * scale;
        }
    }

    private static unsafe float Sample(byte* data, int index, CaptureFormat format)
    {
        if (format.IsFloat) return ((float*)data)[index];
        switch (format.BytesPerSample)
        {
            case 2:
                return ((short*)data)[index] / 32768f;
            case 3:
                var p = data + index * 3;
                return (p[0] | p[1] << 8 | (sbyte)p[2] << 16) / 8388608f;
            default:
                return ((int*)data)[index] / 2147483648f;
        }
    }
}
