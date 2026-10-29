using System.Globalization;
using System.Text;
using EchoDiar.Core.Model;

namespace EchoDiar.Core;

/// <summary>One ASR segment of a single channel, as returned by an engine.</summary>
/// <param name="Start">Seconds from the start of the audio file.</param>
/// <param name="DetectedLanguage">BCP-47 code detected by the engine, if any.</param>
/// <param name="Speaker">The cloud provider's own speaker label ("A", "speaker_1", ...); null for local engines.</param>
public sealed record RawSegment(double Start, double End, string Text, string? DetectedLanguage = null, string? Speaker = null);

/// <summary>One speaker turn from the diarizer on the system channel. <paramref name="SpeakerIndex"/> is the diarizer's own label.</summary>
public sealed record DiarizationTurn(double Start, double End, int SpeakerIndex);

/// <summary>Merges the per-channel ASR output and the diarization turns into transcript segments.</summary>
public static class TranscriptMerger
{
    /// <summary>
    /// Minimum share of a mic segment's tokens found in the overlapping system text for the mic segment to be
    /// treated as speaker bleed.
    /// </summary>
    public const double BleedTokenRatio = 0.8;
    /// <summary>
    /// Mic segments shorter than this (in tokens) are dropped as bleed only on exact normalized equality, so a
    /// real "yeah" or "ok" survives when the other party said it too.
    /// </summary>
    public const int BleedMinTokens = 3;
    /// <summary>Seconds of slack when checking whether a mic and a system segment overlap in time.</summary>
    public const double BleedTimeTolerance = 0.5;

    /// <summary>
    /// Rules:
    /// <list type="bullet">
    /// <item>Mic segments get speaker <c>me</c>.</item>
    /// <item>A system segment with a provider speaker label (cloud engines; the pipeline then passes no turns) takes
    /// <c>s1…sN</c> by first appearance of its label in the system segments sorted by start.</item>
    /// <item>A system segment takes the single turn with the largest time overlap (ties: the earlier turn). Speakers
    /// are numbered <c>s1…sN</c> in order of first appearance in the turns sorted by start. With no turns, or no
    /// overlapping turn, the segment gets <c>s1</c> (the pipeline adds <see cref="Transcript.DiarizationMissingNote"/>
    /// when diarization did not run).</item>
    /// <item>Language: the last marker with <c>fileTime &lt;= start</c>; when it is <c>auto</c>, empty or missing, the
    /// engine-detected language.</item>
    /// <item>Speaker bleed: a mic segment is dropped when its normalized text equals an overlapping system
    /// segment's, or when it has at least <see cref="BleedMinTokens"/> tokens and at least
    /// <see cref="BleedTokenRatio"/> of them occur in the combined text of the overlapping system segments. A mic
    /// segment that is a lone "Merci." is dropped when any system segment overlaps it (Whisper's reading of the
    /// other party's residual voice).</item>
    /// <item>Output is sorted by start; ties put the mic first.</item>
    /// </list>
    /// </summary>
    public static List<Transcript.Segment> Merge(
        IReadOnlyList<RawSegment> mic,
        IReadOnlyList<RawSegment> system,
        IReadOnlyList<DiarizationTurn> turns,
        IReadOnlyList<RecordingSession.LanguageMarker> languageMarkers)
    {
        var markers = languageMarkers.OrderBy(m => m.FileTime).ToList();
        var sortedTurns = turns.OrderBy(t => t.Start).ToList();
        var labels = new Dictionary<int, string>();
        foreach (var turn in sortedTurns)
        {
            if (!labels.ContainsKey(turn.SpeakerIndex)) labels[turn.SpeakerIndex] = Transcript.Speaker.System(labels.Count + 1);
        }
        var providerLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in system.OrderBy(s => s.Start))
        {
            if (raw.Speaker is { } label && !providerLabels.ContainsKey(label))
                providerLabels[label] = Transcript.Speaker.System(providerLabels.Count + 1);
        }

        var systemTokens = system.Select(s => Tokens(s.Text)).ToList();
        var keptMic = mic.Where(segment => !IsBleed(segment, system, systemTokens)).ToList();

        var entries = new List<(int Rank, int Index, Transcript.Segment Segment)>();
        for (var index = 0; index < keptMic.Count; index++)
            entries.Add((0, index, Segment(keptMic[index], TranscriptChannel.Mic, Transcript.Speaker.Me, markers)));
        for (var index = 0; index < system.Count; index++)
        {
            var raw = system[index];
            var speaker = (raw.Speaker is { } label ? providerLabels.GetValueOrDefault(label) : null)
                ?? (BestTurn(raw, sortedTurns) is { } turn ? labels.GetValueOrDefault(turn.SpeakerIndex) : null)
                ?? Transcript.Speaker.System(1);
            entries.Add((1, index, Segment(raw, TranscriptChannel.System, speaker, markers)));
        }
        return entries
            .OrderBy(e => e.Segment.Start)
            .ThenBy(e => e.Rank)
            .ThenBy(e => e.Index)
            .Select(e => e.Segment)
            .ToList();
    }

    /// <summary>
    /// Lowercased words: runs of letters and digits, everything else is a separator (keeps diacritics). Like Swift,
    /// a character (grapheme cluster) counts by its first scalar; tokens are NFC so canonically equal text compares
    /// equal, as Swift strings do.
    /// </summary>
    public static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text.ToLowerInvariant());
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var first = Rune.GetRuneAt(element, 0);
            if (Rune.IsLetter(first) || Rune.IsNumber(first))
            {
                current.Append(element);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString().Normalize(NormalizationForm.FormC));
                current.Clear();
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString().Normalize(NormalizationForm.FormC));
        return tokens;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a lone "Merci." (any case or punctuation), Whisper's usual reading of
    /// residual voice.
    /// </summary>
    public static bool IsLoneMerci(string text) => Tokens(text) is ["merci"];

    private static Transcript.Segment Segment(
        RawSegment raw,
        TranscriptChannel channel,
        string speaker,
        List<RecordingSession.LanguageMarker> markers) =>
        new()
        {
            Start = raw.Start,
            End = Math.Max(raw.Start, raw.End),
            Channel = channel,
            Speaker = speaker,
            Text = raw.Text.Trim(),
            Language = Language(raw.Start, markers) ?? raw.DetectedLanguage,
        };

    /// <summary>The marker language in effect at <paramref name="time"/>, or null for <c>auto</c>, empty or no marker. <paramref name="markers"/> sorted by time.</summary>
    private static string? Language(double time, List<RecordingSession.LanguageMarker> markers)
    {
        var marker = markers.LastOrDefault(m => m.FileTime <= time);
        if (marker is null) return null;
        var language = marker.Language.Trim();
        return language.Length == 0 || language.ToLowerInvariant() == "auto" ? null : language;
    }

    private static double Overlap(double aStart, double aEnd, double bStart, double bEnd) =>
        Math.Max(0, Math.Min(Math.Max(aStart, aEnd), Math.Max(bStart, bEnd)) - Math.Max(aStart, bStart));

    private static DiarizationTurn? BestTurn(RawSegment raw, List<DiarizationTurn> turns)
    {
        DiarizationTurn? best = null;
        var bestOverlap = 0.0;
        foreach (var turn in turns)
        {
            var value = Overlap(raw.Start, raw.End, turn.Start, turn.End);
            if (value > bestOverlap)
            {
                best = turn;
                bestOverlap = value;
            }
        }
        return best;
    }

    private static bool IsBleed(RawSegment mic, IReadOnlyList<RawSegment> system, List<List<string>> systemTokens)
    {
        var micTokens = Tokens(mic.Text);
        if (micTokens.Count == 0) return false;
        var micStart = mic.Start - BleedTimeTolerance;
        var micEnd = Math.Max(mic.Start, mic.End) + BleedTimeTolerance;
        var overlapping = Enumerable.Range(0, system.Count)
            .Where(i => Overlap(micStart, micEnd, system[i].Start, system[i].End) > 0)
            .ToList();
        if (overlapping.Any(i => systemTokens[i].SequenceEqual(micTokens, StringComparer.Ordinal))) return true;
        if (overlapping.Count > 0 && IsLoneMerci(mic.Text)) return true;
        if (micTokens.Count < BleedMinTokens) return false;

        var bag = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var index in overlapping)
        {
            foreach (var token in systemTokens[index]) bag[token] = bag.GetValueOrDefault(token) + 1;
        }
        var matched = 0;
        foreach (var token in micTokens)
        {
            if (bag.GetValueOrDefault(token) <= 0) continue;
            bag[token]--;
            matched++;
        }
        return matched >= BleedTokenRatio * micTokens.Count;
    }
}
