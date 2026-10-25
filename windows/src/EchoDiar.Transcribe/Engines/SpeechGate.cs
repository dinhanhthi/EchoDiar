using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

namespace EchoDiar.Transcribe.Engines;

/// <summary>
/// The Whisper speech gate (Swift <c>WhisperKitEngine</c>'s Silero gate): Silero VAD 6.2.1 through sherpa-onnx finds the
/// speech in one decode window, so a window without speech is not decoded and a segment Whisper makes up outside speech
/// is dropped. sherpa reports speech segments, not macOS's per-frame probabilities; each window starts from a fresh
/// state. Not thread-safe.
/// </summary>
internal sealed class SpeechGate : IDisposable
{
    /// <summary>
    /// Silero's probability that a frame is speech, from which it counts as voiced (macOS <c>speechProbability</c>), the
    /// one tunable constant. Above silero-vad's 0.5 default because each window starts from a fresh state: on macOS,
    /// breath and keys reached 0.88 on a noisy mic while speech, even one "Merci.", stays at 0.99–1.0.
    /// </summary>
    public const float SpeechThreshold = 0.9f;

    /// <summary>Seconds each speech interval is widened by on both sides to keep a segment: one macOS 256 ms frame.</summary>
    public const double Padding = 0.256;

    /// <summary>Samples per Silero frame at 16 kHz.</summary>
    private const int FrameSamples = 512;

    /// <summary>
    /// Shortest speech sherpa reports. sherpa only starts speech after about 9 consecutive 32 ms frames above
    /// <see cref="SpeechThreshold"/> (~288 ms), so this changes little; macOS needs one 256 ms frame at or above it.
    /// </summary>
    private const float MinSpeechSeconds = 0.25f;

    private const int SampleRate = (int)MonoAudio.SampleRate;

    /// <summary>Where the Transcribe project's Content puts the model: next to the app's assemblies.</summary>
    private static readonly string ModelPath = Path.Combine(AppContext.BaseDirectory, "Models", "silero_vad.onnx");

    private readonly VoiceActivityDetector _detector;

    private SpeechGate(VoiceActivityDetector detector) => _detector = detector;

    /// <summary>
    /// The gate for windows of at most <paramref name="windowSamples"/>; null (logged) when the model is missing or
    /// sherpa-onnx cannot load it: windows are then decoded without the gate.
    /// </summary>
    public static SpeechGate? TryLoad(int windowSamples) => TryLoad(windowSamples, ModelPath);

    internal static SpeechGate? TryLoad(int windowSamples, string modelPath)
    {
        try
        {
            // A model sherpa cannot read aborts the process in onnxruntime: open it here first (throws when missing too).
            using (File.OpenRead(modelPath)) { }
            // sherpa's C# API passes the path in the ANSI code page and its native side reads it as UTF-8.
            if (modelPath.Any(c => c > 127)) throw new IOException($"sherpa-onnx cannot open a non-ASCII path: {modelPath}");
            // One whole window and a second: the buffer holds it, and sherpa never raises its threshold for long speech.
            var windowSeconds = (float)windowSamples / SampleRate + 1;
            var config = new VadModelConfig();
            config.SileroVad.Model = modelPath;
            config.SileroVad.Threshold = SpeechThreshold;
            config.SileroVad.WindowSize = FrameSamples;
            config.SileroVad.MinSpeechDuration = MinSpeechSeconds;
            config.SileroVad.MaxSpeechDuration = windowSeconds;
            config.SampleRate = SampleRate;
            var detector = new VoiceActivityDetector(config, windowSeconds);
            // sherpa does not throw for a model it cannot open: it returns a detector that hears no speech anywhere.
            if (IsOpen(detector)) return new SpeechGate(detector);
            detector.Dispose();
            throw new IOException($"sherpa-onnx could not open the Silero VAD model: {modelPath}");
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: Silero VAD failed to load, transcribing without it: {e}");
            return null;
        }
    }

    /// <summary>
    /// False when sherpa rejected the config (it logs why) and gave a null detector. Its handle is private: a missing
    /// field (a renamed one in a later sherpa-onnx) counts as not open, so the gate is off rather than deaf.
    /// </summary>
    internal static bool IsOpen(VoiceActivityDetector detector) =>
        typeof(VoiceActivityDetector).GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(detector)
            is HandleRef handle && handle.Handle != IntPtr.Zero;

    /// <summary>The speech in one window, as (start, end) seconds from its first sample; empty when Silero hears none.</summary>
    public List<(double Start, double End)> Intervals(ReadOnlySpan<float> window)
    {
        _detector.Reset();
        for (var first = 0; first < window.Length; first += FrameSamples)
        {
            _detector.AcceptWaveform(window[first..Math.Min(first + FrameSamples, window.Length)].ToArray());
        }
        _detector.Flush();
        var intervals = new List<(double Start, double End)>();
        while (!_detector.IsEmpty())
        {
            var segment = _detector.Front();
            intervals.Add(((double)segment.Start / SampleRate, (double)(segment.Start + segment.Samples.Length) / SampleRate));
            _detector.Pop();
        }
        return intervals;
    }

    /// <summary>
    /// True when <paramref name="start"/>–<paramref name="end"/> (seconds into the window) overlaps one of the window's
    /// speech <paramref name="intervals"/> widened by <see cref="Padding"/>; a NaN or infinite time has none.
    /// </summary>
    public static bool HasSpeech(List<(double Start, double End)> intervals, double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end)) return false;
        return intervals.Exists(i => start <= i.End + Padding && end >= i.Start - Padding);
    }

    public void Dispose() => _detector.Dispose();
}
