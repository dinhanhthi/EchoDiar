#if DEBUG
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using EchoDiar.Core.Audio;
using EchoDiar.Platform.Audio;
using LLama.Native;
using Microsoft.UI.Dispatching;
using SherpaOnnx;
using Whisper.net;

namespace EchoDiar.App;

/// <summary>
/// Debug-only headless harnesses for the Phase 2 VM gate: <c>--capture-probe</c> records the mic and
/// system sources to CAF files, <c>--native-probe</c> loads every native engine once. Each writes its
/// result JSON under <c>Probes\</c> and the app then exits without a window.
/// </summary>
internal static class CaptureProbe
{
    private const string CaptureFlag = "--capture-probe";
    private const string NativeFlag = "--native-probe";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Starts the probe named in <paramref name="commandLine"/>, if any, and calls <paramref name="exit"/> on this thread when it ends.</summary>
    public static bool TryRun(string[] commandLine, Action exit)
    {
        var index = Array.FindIndex(commandLine, arg => arg is CaptureFlag or NativeFlag);
        if (index < 0) return false;

        var rest = commandLine[(index + 1)..];
        Func<string[], Task> probe = commandLine[index] == CaptureFlag ? RunCaptureAsync : RunNativeAsync;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(async () =>
        {
            try
            {
                await probe(rest);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Probe failed: {ex}");
            }
            finally
            {
                dispatcher.TryEnqueue(() => exit());
            }
        });
        return true;
    }

    private static string ProbesDir => Path.Combine(App.DataRoot, "Probes");

    private static string Timestamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static string Describe(string label, Exception ex) =>
        $"{label}: {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}";

    // --capture-probe <seconds> <all|pid:N|exe:name.exe>

    private static async Task RunCaptureAsync(string[] args)
    {
        var dir = Path.Combine(ProbesDir, Timestamp());
        Directory.CreateDirectory(dir);
        var errors = new ErrorList();
        var mic = new ProbeChannel(Path.Combine(dir, "mic.caf"));
        var system = new ProbeChannel(Path.Combine(dir, "system.caf"));
        var mode = "none";
        MicCapture? micCapture = null;
        LoopbackCapture? loopback = null;
        try
        {
            if (args.Length < 2 || !int.TryParse(args[0], CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                errors.Add($"usage: {CaptureFlag} <seconds> <all|pid:N|exe:name.exe>");
                return;
            }

            micCapture = StartMic(mic, errors);
            (loopback, mode) = StartSystem(args[1], system, errors);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            StopAndFinish("mic", micCapture is null ? null : micCapture.Dispose, mic, errors);
            StopAndFinish("system", loopback is null ? null : loopback.Dispose, system, errors);
            var result = new
            {
                micRate = mic.Rate,
                systemRate = system.Rate,
                micFrames = mic.Frames,
                systemFrames = system.Frames,
                micPeak = mic.Peak,
                systemPeak = system.Peak,
                mode,
                firstMicQpc = mic.FirstQpc,
                firstSystemQpc = system.FirstQpc,
                errors = errors.ToArray(),
            };
            await File.WriteAllTextAsync(Path.Combine(dir, "probe-result.json"), JsonSerializer.Serialize(result, JsonOptions));
        }
    }

    private static MicCapture? StartMic(ProbeChannel channel, ErrorList errors)
    {
        var capture = new MicCapture();
        try
        {
            capture.Failed += ex => errors.Add(Describe("mic failed", ex));
            capture.Start(channel.OnSamples);
            channel.Rate = capture.SampleRate;
            return capture;
        }
        catch (Exception ex)
        {
            errors.Add(Describe("mic", ex));
            capture.Dispose();
            return null;
        }
    }

    private static (LoopbackCapture? Capture, string Mode) StartSystem(string target, ProbeChannel channel, ErrorList errors)
    {
        Func<LoopbackCapture> create;
        string mode;
        if (target == "all")
        {
            create = () => LoopbackCapture.AllExcept(Environment.ProcessId);
            mode = "all";
        }
        else if (target.StartsWith("pid:", StringComparison.Ordinal) && int.TryParse(target[4..], CultureInfo.InvariantCulture, out var pid))
        {
            create = () => LoopbackCapture.ForProcessTree(pid);
            mode = "pid";
        }
        else if (target.StartsWith("exe:", StringComparison.Ordinal))
        {
            var root = FindProcess(target[4..]);
            if (root is null)
            {
                errors.Add($"system: no running process named {target[4..]}");
                return (null, "none");
            }

            create = () => LoopbackCapture.ForProcessTree(root.Value);
            mode = "exe";
        }
        else
        {
            errors.Add($"system: unknown target {target} (all|pid:N|exe:name.exe)");
            return (null, "none");
        }

        if (StartLoopback(create, "system", channel, errors) is { } capture) return (capture, mode);
        if (StartLoopback(LoopbackCapture.Endpoint, "system endpoint fallback", channel, errors) is { } fallback)
        {
            return (fallback, "endpoint-fallback");
        }

        return (null, "none");
    }

    private static LoopbackCapture? StartLoopback(Func<LoopbackCapture> create, string label, ProbeChannel channel, ErrorList errors)
    {
        LoopbackCapture? capture = null;
        try
        {
            capture = create();
            capture.Failed += ex => errors.Add(Describe($"{label} failed", ex));
            capture.Start(channel.OnSamples);
            channel.Rate = capture.SampleRate;
            return capture;
        }
        catch (Exception ex)
        {
            errors.Add(Describe(label, ex));
            capture?.Dispose();
            return null;
        }
    }

    /// <summary>The earliest started process with this exe name: the likely root of a multi-process app.</summary>
    private static int? FindProcess(string exeName)
    {
        var name = Path.GetFileNameWithoutExtension(exeName);
        var processes = System.Diagnostics.Process.GetProcessesByName(name);
        try
        {
            return processes
                .OrderBy(p => StartTimeOrMax(p))
                .Select(p => (int?)p.Id)
                .FirstOrDefault();
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static DateTime StartTimeOrMax(System.Diagnostics.Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTime.MaxValue;
        }
    }

    private static void StopAndFinish(string label, Action? dispose, ProbeChannel channel, ErrorList errors)
    {
        try
        {
            dispose?.Invoke();
        }
        catch (Exception ex)
        {
            errors.Add(Describe($"{label} stop", ex));
        }

        try
        {
            channel.Finish();
        }
        catch (Exception ex)
        {
            errors.Add(Describe($"{label} file", ex));
        }
    }

    /// <summary>One source's mono Float32 CAF file and stats. Written on its capture thread, read after Stop joins it.</summary>
    private sealed class ProbeChannel(string path)
    {
        private CafFile.Writer? _writer;

        public int? Rate { get; set; }
        public long Frames { get; private set; }
        public float Peak { get; private set; }
        public long? FirstQpc { get; private set; }

        public void OnSamples(ReadOnlySpan<float> samples, long qpc100ns, int sampleRate)
        {
            if (_writer is null)
            {
                _writer = new CafFile.Writer(path, sampleRate, 1, CafFile.SampleFormat.Float32);
                FirstQpc = qpc100ns;
            }

            _writer.Append(samples);
            Frames += samples.Length;
            foreach (var sample in samples)
            {
                var magnitude = Math.Abs(sample);
                if (magnitude > Peak) Peak = magnitude; // NaN never wins, so the JSON stays valid
            }
        }

        /// <summary>Patches the data size; a started source that sent nothing still gets an empty file.</summary>
        public void Finish()
        {
            if (_writer is null && Rate is { } rate)
            {
                _writer = new CafFile.Writer(path, rate, 1, CafFile.SampleFormat.Float32);
            }

            _writer?.Finish();
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>Errors from the probe thread and the capture threads' Failed events.</summary>
    private sealed class ErrorList
    {
        private readonly List<string> _items = [];

        public void Add(string error)
        {
            lock (_items) _items.Add(error);
        }

        public string[] ToArray()
        {
            lock (_items) return [.. _items];
        }
    }

    // --native-probe <modelsDir>, with sample.wav and two-voice.wav in <modelsDir>\..

    private static async Task RunNativeAsync(string[] args)
    {
        Directory.CreateDirectory(ProbesDir);
        var path = Path.Combine(ProbesDir, $"native-{Timestamp()}.json");
        var errors = new List<string>();
        string? whisperText = null;
        int? sherpaSegments = null;
        int? sherpaSpeakers = null;
        string? llamaSystemInfo = null;
        try
        {
            if (args.Length < 1)
            {
                errors.Add($"usage: {NativeFlag} <modelsDir>");
                return;
            }

            var models = Path.GetFullPath(args[0]);
            var fixtures = Path.GetFullPath(Path.Combine(models, ".."));

            try
            {
                whisperText = await TranscribeAsync(Path.Combine(models, "ggml-tiny.bin"), Path.Combine(fixtures, "sample.wav"));
            }
            catch (Exception ex)
            {
                errors.Add(Describe("whisper", ex));
            }

            try
            {
                (sherpaSegments, sherpaSpeakers) = Diarize(
                    Path.Combine(models, "model.onnx"),
                    Path.Combine(models, "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"),
                    Path.Combine(fixtures, "two-voice.wav"));
            }
            catch (Exception ex)
            {
                errors.Add(Describe("sherpa", ex));
            }

            try
            {
                llamaSystemInfo = Marshal.PtrToStringUTF8(NativeApi.llama_print_system_info()); // static buffer owned by llama.cpp
            }
            catch (Exception ex)
            {
                errors.Add(Describe("llama", ex));
            }
        }
        finally
        {
            var result = new
            {
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                whisperText,
                sherpaSegments,
                sherpaSpeakers,
                llamaSystemInfo,
                errors,
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOptions));
        }
    }

    private static async Task<string> TranscribeAsync(string modelPath, string wavPath)
    {
        var (samples, rate) = ReadWav(wavPath);
        if (rate != 16000) throw new InvalidDataException($"{wavPath} is {rate} Hz; Whisper needs 16000 Hz.");

        using var factory = WhisperFactory.FromPath(modelPath);
        await using var processor = factory.CreateBuilder().WithLanguage("en").Build();
        var text = new System.Text.StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples))
        {
            text.Append(segment.Text);
        }

        return text.ToString().Trim();
    }

    private static (int Segments, int Speakers) Diarize(string segmentationModel, string embeddingModel, string wavPath)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentationModel;
        config.Embedding.Model = embeddingModel;
        config.Clustering.NumClusters = -1; // let the threshold decide how many speakers there are
        config.Clustering.Threshold = 0.5f;

        using var diarizer = new OfflineSpeakerDiarization(config);
        var (samples, rate) = ReadWav(wavPath);
        if (rate != diarizer.SampleRate)
        {
            throw new InvalidDataException($"{wavPath} is {rate} Hz; the diarizer needs {diarizer.SampleRate} Hz.");
        }

        var segments = diarizer.Process(samples);
        return (segments.Length, segments.Select(s => s.Speaker).Distinct().Count());
    }

    /// <summary>Reads a 16-bit PCM WAV file as mono floats (channels averaged).</summary>
    private static (float[] Samples, int SampleRate) ReadWav(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (ReadId(reader) != "RIFF") throw new InvalidDataException($"{path} is not a RIFF file.");
        reader.ReadInt32();
        if (ReadId(reader) != "WAVE") throw new InvalidDataException($"{path} is not a WAVE file.");

        int channels = 0, rate = 0, bits = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = ReadId(reader);
            var size = reader.ReadUInt32();
            var next = reader.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                var format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadUInt16(); // block align
                bits = reader.ReadUInt16();
                if (format != 1 || bits != 16) throw new InvalidDataException($"{path}: only 16-bit PCM is supported (format {format}, {bits} bits).");
            }
            else if (id == "data")
            {
                if (channels == 0) throw new InvalidDataException($"{path}: data chunk before fmt chunk.");
                var bytes = reader.ReadBytes((int)Math.Min(size, reader.BaseStream.Length - reader.BaseStream.Position));
                var pcm = MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, bytes.Length - bytes.Length % (2 * channels)));
                var samples = new float[pcm.Length / channels];
                for (var frame = 0; frame < samples.Length; frame++)
                {
                    var sum = 0f;
                    for (var channel = 0; channel < channels; channel++) sum += pcm[frame * channels + channel];
                    samples[frame] = sum / channels / 32768f;
                }

                return (samples, rate);
            }

            reader.BaseStream.Position = next;
        }

        throw new InvalidDataException($"{path} has no data chunk.");
    }

    private static string ReadId(BinaryReader reader) => System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
}
#endif
