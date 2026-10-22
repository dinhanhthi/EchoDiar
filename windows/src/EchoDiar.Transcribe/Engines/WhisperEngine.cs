using System.Text.RegularExpressions;
using EchoDiar.Core;
using EchoDiar.Core.Models;
using Whisper.net;

namespace EchoDiar.Transcribe.Engines;

/// <summary>
/// whisper.cpp ggml models through Whisper.net (Swift <c>WhisperKitEngine</c>), from <c>Models/&lt;id&gt;/</c>.
/// The model is loaded per job and freed before <see cref="TranscribeAsync"/> returns.
/// </summary>
public sealed partial class WhisperEngine(ModelEntry entry, string folder) : ITranscriptionEngine
{
    public const int DefaultWindowSeconds = 30;

    /// <summary>
    /// RMS (about −50 dBFS) that some 100 ms frame of a segment's audio must reach for the segment to be kept, see
    /// <see cref="IsSilent"/>: Whisper makes up "Thank you." from near silence.
    /// </summary>
    public const float SpeechRMS = 0.003f;

    private const int SampleRate = (int)MonoAudio.SampleRate;

    /// <summary>A <see cref="TranscriptionPipeline.EngineFactory"/>.</summary>
    public static ITranscriptionEngine Create(ModelEntry entry, string folder) => new WhisperEngine(entry, folder);

    public async Task<IReadOnlyList<RawSegment>> TranscribeAsync(
        string channelPath, IReadOnlyList<LanguageRange> languageRanges, Action<double> progress,
        CancellationToken cancellationToken)
    {
        var model = entry.Files is [var file] ? Path.Combine(folder, file) : null;
        if (model is null || !File.Exists(model)) throw new TranscriptionException(TranscriptionErrorKind.NeedsModel, entry.Id);
        using var factory = WhisperFactory.FromPath(model);
        return await DecodeAsync(factory, channelPath, languageRanges, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One decode per language range; each range is read in slabs of at most <see cref="MonoAudio.SlabSeconds"/>, each
    /// slab is split at silences into windows of the entry's window length, and every window detects its own language
    /// when the range has none.
    /// </summary>
    private async Task<IReadOnlyList<RawSegment>> DecodeAsync(
        WhisperFactory factory, string channelPath, IReadOnlyList<LanguageRange> ranges, Action<double> progress,
        CancellationToken cancellationToken)
    {
        var duration = MonoAudio.Duration(channelPath);
        var total = Math.Max(1, duration);
        var windowSamples = (entry.WindowSeconds ?? DefaultWindowSeconds) * SampleRate;
        var segments = new List<RawSegment>();
        foreach (var range in ranges)
        {
            var language = range.Language is { } code ? WhisperLanguages.Primary(code) : null;
            // No context: each window is decoded on its own, as WhisperKit does, so a hallucination never seeds the next.
            await using var processor = factory.CreateBuilder().WithLanguage(language ?? "auto").WithNoContext().Build();
            var rangeEnd = Math.Min(range.End ?? duration, duration);
            var slabStart = range.Start;
            while (slabStart < rangeEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slabEnd = Math.Min(slabStart + MonoAudio.SlabSeconds, rangeEnd);
                var samples = MonoAudio.Read(channelPath, slabStart, slabEnd);
                if (samples.Length == 0) break;
                var windows = Windows(samples, windowSamples);
                // Before the last slab, the last window ends at the slab cut, maybe mid-word: it is decoded with the
                // next slab, which starts where it starts (a VAD silence).
                var nextStart = slabEnd;
                if (slabEnd < rangeEnd && windows.Count > 1)
                {
                    nextStart = slabStart + (double)windows[^1].Start / SampleRate;
                    windows.RemoveAt(windows.Count - 1);
                }
                foreach (var (start, length) in windows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = slabStart + (double)start / SampleRate;
                    var window = samples.AsMemory(start, length);
                    await foreach (var segment in processor.ProcessAsync(window, cancellationToken).ConfigureAwait(false))
                    {
                        var text = SpeechText(segment.Text);
                        if (text.Length == 0 || IsSilent(window.Span, (float)segment.Start.TotalSeconds, (float)segment.End.TotalSeconds)) continue;
                        segments.Add(new RawSegment(
                            offset + segment.Start.TotalSeconds, offset + segment.End.TotalSeconds, text,
                            language is null ? segment.Language : null));
                    }
                    progress(Math.Min(1, (offset + (double)length / SampleRate) / total));
                }
                slabStart = nextStart;
            }
        }
        progress(1);
        return segments;
    }

    // Text rules

    [GeneratedRegex(@"<\|[^|]*\|>")]
    private static partial Regex SpecialToken();

    [GeneratedRegex(@"\[[^\[\]]*\]|\([^()]*\)|\*[^*]+\*")]
    private static partial Regex Annotation();

    [GeneratedRegex(@"^(?:\[[^\[\]]*\]|\([^()]*\)|\*[^*]+\*)")]
    private static partial Regex LeadingAnnotation();

    [GeneratedRegex(@"(?:\[[^\[\]]*\]|\([^()]*\)|\*[^*]+\*)\s*$")]
    private static partial Regex TrailingAnnotation();

    [GeneratedRegex("blank[ _]?audio|silence|music|noise|applause|laugh|inaudible|no[ _]speech|static", RegexOptions.IgnoreCase)]
    private static partial Regex NonSpeech();

    /// <summary>
    /// Segment text without Whisper's non-speech output; empty means the segment is dropped.
    /// <list type="bullet">
    /// <item>Special tokens such as <c>&lt;|endoftext|&gt;</c> are removed wherever they are.</item>
    /// <item>Annotations (<c>[...]</c>, <c>(...)</c>, <c>*...*</c>) are removed when they make up the whole text, e.g.
    /// <c>[BLANK_AUDIO]</c>, <c>[ Silence ]</c>, <c>(music)</c>, <c>*music*</c>.</item>
    /// <item>At the start or end of speech, only annotations naming a known non-speech sound are removed, so speech
    /// that merely contains brackets is kept.</item>
    /// </list>
    /// </summary>
    public static string SpeechText(string raw)
    {
        var text = SpecialToken().Replace(raw, "").Trim();
        if (Annotation().Replace(text, "").Trim().Length == 0) return "";
        while (LeadingAnnotation().Match(text) is { Success: true } match && NonSpeech().IsMatch(match.Value))
        {
            text = text[match.Length..].Trim();
        }
        while (TrailingAnnotation().Match(text) is { Success: true } match && NonSpeech().IsMatch(match.Value))
        {
            text = text[..match.Index].Trim();
        }
        return text;
    }

    /// <summary>
    /// True when no 100 ms frame of <paramref name="samples"/> from <paramref name="start"/> to <paramref name="end"/>
    /// (seconds) reaches <see cref="SpeechRMS"/>: Whisper made the segment up from silence. The range is clamped to the
    /// buffer, as Whisper's timestamps can run past the audio.
    /// </summary>
    public static bool IsSilent(ReadOnlySpan<float> samples, float start, float end, int sampleRate = SampleRate)
    {
        float rate = sampleRate;
        var lower = (int)Math.Min(samples.Length, Math.Max(0, Math.Floor((double)(start * rate))));
        var upper = (int)Math.Min(samples.Length, Math.Max(lower, Math.Ceiling((double)(end * rate))));
        var frame = sampleRate / 10;
        for (var first = lower; first < upper; first += frame)
        {
            if (Rms(samples[first..Math.Min(first + frame, upper)]) >= SpeechRMS) return false;
        }
        return true;
    }

    private static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    // Windows: WhisperKit's VADAudioChunker with its default EnergyVAD

    /// <summary>Samples after the last cut that are not decoded: a shorter tail is mostly where Whisper hallucinates.</summary>
    private const int WindowPadding = SampleRate;

    /// <summary>100 ms frames.</summary>
    private const int VadFrame = SampleRate / 10;

    /// <summary>RMS above which a frame is voice.</summary>
    private const float VadEnergyThreshold = 0.02f;

    /// <summary>
    /// <paramref name="samples"/> split into windows of at most <paramref name="maxWindow"/> samples, each cut at the
    /// middle of the longest silence in the second half of the window (WhisperKit <c>VADAudioChunker.chunkAll</c>).
    /// Audio that fits in one window is one window; after a cut, a tail of <see cref="WindowPadding"/> samples or less
    /// is dropped, as WhisperKit does.
    /// </summary>
    private static List<(int Start, int Length)> Windows(float[] samples, int maxWindow)
    {
        if (samples.Length <= maxWindow) return [(0, samples.Length)];
        var windows = new List<(int Start, int Length)>();
        var start = 0;
        while (start < samples.Length - WindowPadding)
        {
            var end = samples.Length;
            if (start + maxWindow < end) end = SplitOnMiddleOfLongestSilence(samples, start, Math.Min(samples.Length, start + maxWindow));
            if (end <= start) break;
            windows.Add((start, end - start));
            start = end;
        }
        return windows;
    }

    private static int SplitOnMiddleOfLongestSilence(float[] samples, int start, int end)
    {
        var middle = start + (end - start) / 2;
        var half = samples.AsSpan(middle, end - middle);
        var frames = (half.Length + VadFrame - 1) / VadFrame;
        int? longestStart = null;
        var longestCount = 0;
        for (var index = 0; index < frames;)
        {
            if (IsVoice(half, index)) { index++; continue; }
            var silenceEnd = index;
            while (silenceEnd < frames && !IsVoice(half, silenceEnd)) silenceEnd++;
            if (silenceEnd - index > longestCount)
            {
                longestCount = silenceEnd - index;
                longestStart = index;
            }
            index = silenceEnd;
        }
        if (longestStart is not { } silenceStart) return end;
        return middle + (silenceStart + longestCount / 2) * VadFrame;
    }

    private static bool IsVoice(ReadOnlySpan<float> samples, int frame)
    {
        var first = frame * VadFrame;
        return Rms(samples[first..Math.Min(first + VadFrame, samples.Length)]) > VadEnergyThreshold;
    }
}
