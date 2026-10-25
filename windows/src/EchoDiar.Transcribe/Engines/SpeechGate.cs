using System.Diagnostics;
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

    /// <summary>Shortest speech sherpa reports: about one macOS 256 ms frame.</summary>
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
    public static SpeechGate? TryLoad(int windowSamples)
    {
        try
        {
            // sherpa does not throw for a missing model: it returns a detector that hears no speech anywhere.
            if (!File.Exists(ModelPath)) throw new FileNotFoundException("The Silero VAD model is missing.", ModelPath);
            var config = new VadModelConfig();
            config.SileroVad.Model = ModelPath;
            config.SileroVad.Threshold = SpeechThreshold;
            config.SileroVad.WindowSize = FrameSamples;
            config.SileroVad.MinSpeechDuration = MinSpeechSeconds;
            config.SampleRate = SampleRate;
            // The buffer holds one whole window.
            return new SpeechGate(new VoiceActivityDetector(config, (float)windowSamples / SampleRate + 1));
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: Silero VAD failed to load, transcribing without it: {e}");
            return null;
        }
    }

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
