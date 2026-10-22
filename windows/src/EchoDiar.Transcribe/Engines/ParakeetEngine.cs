using EchoDiar.Core;
using EchoDiar.Core.Models;
using SherpaOnnx;

namespace EchoDiar.Transcribe.Engines;

/// <summary>
/// Parakeet TDT int8 ONNX models through a sherpa-onnx NeMo transducer (Swift <c>ParakeetEngine</c>), from
/// <c>Models/&lt;id&gt;/</c>. The model is loaded per job and freed before <see cref="TranscribeAsync"/> returns.
/// Parakeet detects the language itself: the ranges' languages are not used.
/// </summary>
/// <remarks>
/// Audio is decoded in speech chunks of at most the entry's window (30 s): the sherpa-onnx encoder aborts the process
/// (an uncatchable onnxruntime error) beyond about 400 s, and its peak memory grows with the length (about 2.4 GB at
/// 60 s, 4.2 GB at 180 s).
/// </remarks>
public sealed class ParakeetEngine(ModelEntry entry, string folder) : ISessionTranscriptionEngine
{
    /// <summary>A pause this long between words starts a new segment.</summary>
    public const double SegmentGapSeconds = 1.0;

    /// <summary>
    /// Longest a token lasts: TDT's longest step, 4 encoder frames of 80 ms. sherpa-onnx 1.13.8's C#
    /// <c>OfflineRecognizerResult.Durations</c> holds garbage (about 1e-14), so token ends are estimated from starts.
    /// </summary>
    private const double MaxTokenSeconds = 0.32;

    private const int SampleRate = (int)MonoAudio.SampleRate;

    /// <summary>A shorter chunk is padded with silence (sherpa-onnx may abort the process on too few frames).</summary>
    private const int MinChunkSamples = (int)(0.3 * SampleRate);

    private sealed record Word(string Text, double Start, double End);

    /// <summary>A <see cref="TranscriptionPipeline.EngineFactory"/>.</summary>
    public static ITranscriptionEngine Create(ModelEntry entry, string folder) => new ParakeetEngine(entry, folder);

    /// <summary>
    /// The file cut into speech chunks (<see cref="TranscriptionPipeline.FillGapsAsync"/> over the whole file), each
    /// decoded by one <see cref="Session"/>; silence is not decoded.
    /// </summary>
    public async Task<IReadOnlyList<RawSegment>> TranscribeAsync(
        string channelPath, IReadOnlyList<LanguageRange> languageRanges, Action<double> progress,
        CancellationToken cancellationToken)
    {
        await using var session = await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var segments = await TranscriptionPipeline.FillGapsAsync(
            [new Span(0, MonoAudio.Duration(channelPath))], channelPath, entry, languageRanges, _ => Task.FromResult(session),
            progress, cancellationToken).ConfigureAwait(false);
        progress(1);
        return segments;
    }

    /// <summary>Loads the recognizer off the caller's thread; disposing the session frees it.</summary>
    public async Task<ITranscriptionSession> OpenSessionAsync(CancellationToken cancellationToken) =>
        new Session(await Task.Run(() => new OfflineRecognizer(Config()), cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// One recognizer for many chunks, each decoded from a fresh stream (Swift <c>ParakeetEngine.Session</c>). Not
    /// thread-safe.
    /// </summary>
    public sealed class Session(OfflineRecognizer recognizer) : ITranscriptionSession
    {
        /// <summary>
        /// <paramref name="language"/> is not used: Parakeet detects it. A chunk shorter than 300 ms is padded with
        /// silence.
        /// </summary>
        public Task<IReadOnlyList<RawSegment>> TranscribeAsync(
            float[] samples, string? language, CancellationToken cancellationToken) =>
            Task.Run<IReadOnlyList<RawSegment>>(() =>
            {
                var padded = samples.Length >= MinChunkSamples ? samples : [.. samples, .. new float[MinChunkSamples - samples.Length]];
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(SampleRate, padded);
                recognizer.Decode(stream);
                var result = stream.Result;
                var words = Words(result, (double)padded.Length / SampleRate);
                if (words.Count > 0) return Segments(words, 0);
                var text = result.Text.Trim();
                return text.Length == 0 ? [] : [new RawSegment(0, (double)samples.Length / SampleRate, text)];
            }, cancellationToken);

        public ValueTask DisposeAsync()
        {
            recognizer.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private OfflineRecognizerConfig Config()
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.ModelConfig.Transducer.Encoder = ModelFile("encoder");
        config.ModelConfig.Transducer.Decoder = ModelFile("decoder");
        config.ModelConfig.Transducer.Joiner = ModelFile("joiner");
        config.ModelConfig.Tokens = ModelFile("tokens");
        // sherpa-onnx cannot tell a NeMo transducer from the file names.
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Min(8, Environment.ProcessorCount);
        config.DecodingMethod = "greedy_search";
        return config;
    }

    /// <summary>The installed catalog file whose name starts with <paramref name="prefix"/> (<c>encoder.int8.onnx</c>).</summary>
    private string ModelFile(string prefix)
    {
        var file = entry.Files?.FirstOrDefault(f => Path.GetFileName(f).StartsWith(prefix + ".", StringComparison.Ordinal));
        var path = file is null ? null : Path.Combine(folder, file);
        if (path is null || !File.Exists(path)) throw new TranscriptionException(TranscriptionErrorKind.NeedsModel, entry.Id);
        return path;
    }

    /// <summary>
    /// The result's tokens joined into words: a token starting with a space (or <c>▁</c>) starts a word. A token ends at
    /// the next token's start (the last one at <paramref name="audioSeconds"/>), at most <see cref="MaxTokenSeconds"/>
    /// after its own.
    /// </summary>
    private static List<Word> Words(OfflineRecognizerResult result, double audioSeconds)
    {
        var tokens = result.Tokens ?? [];
        var starts = result.Timestamps ?? [];
        var count = Math.Min(tokens.Length, starts.Length);
        var words = new List<Word>();
        string text = "";
        double start = 0, end = 0;
        for (var i = 0; i < count; i++)
        {
            var token = tokens[i];
            if (token.Length == 0) continue;
            var tokenEnd = Math.Min(starts[i] + MaxTokenSeconds, i + 1 < count ? starts[i + 1] : audioSeconds);
            var startsWord = token[0] is ' ' or '▁' || text.Length == 0;
            if (startsWord)
            {
                if (text.Length > 0) words.Add(new Word(text, start, end));
                text = token.TrimStart(' ', '▁');
                start = starts[i];
            }
            else
            {
                text += token;
            }
            end = tokenEnd;
        }
        if (text.Length > 0) words.Add(new Word(text, start, end));
        return words;
    }

    /// <summary>Words grouped into segments, split after sentence-final punctuation or at a pause of <see cref="SegmentGapSeconds"/>.</summary>
    private static List<RawSegment> Segments(List<Word> words, double offset)
    {
        var segments = new List<RawSegment>();
        var current = new List<Word>();
        void Flush()
        {
            if (current.Count == 0) return;
            segments.Add(new RawSegment(offset + current[0].Start, offset + current[^1].End, string.Join(" ", current.Select(w => w.Text))));
            current.Clear();
        }
        foreach (var word in words)
        {
            if (current.Count > 0 && word.Start - current[^1].End >= SegmentGapSeconds) Flush();
            current.Add(word);
            if (word.Text[^1] is '.' or '?' or '!') Flush();
        }
        Flush();
        return segments;
    }
}
