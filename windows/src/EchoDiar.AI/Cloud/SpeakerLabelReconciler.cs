namespace EchoDiar.AI.Cloud;

/// <summary>
/// Joins the segments of audio sent in parts (split uploads, realtime reconnects, gap uploads), where each part's
/// provider labels speakers on its own: "A" in one part is not "A" in the next (Swift <c>SpeakerLabelReconciler</c>).
/// Parts share an overlap of audio; a new label takes the earlier label it shares most of its speaking time with there,
/// else a part-scoped label of its own, so two different people are never merged. Pure, for tests.
/// </summary>
public static class SpeakerLabelReconciler
{
    /// <summary>Seconds of audio a later part repeats from before the cut.</summary>
    public const double LabelOverlapSeconds = 30.0;

    /// <summary>Shared speaking time in the overlap a match needs, so a stray word does not merge two speakers.</summary>
    public const double MinimumSharedSeconds = 3.0;

    /// <summary>
    /// <paramref name="earlier"/> followed by <paramref name="next"/> (both in absolute time), with <paramref name="next"/>'s
    /// labels reconciled. <paramref name="overlap"/> is the audio both cover: <c>max(0, cut - LabelOverlapSeconds)..cut</c>,
    /// its end the cut. The later part owns what reaches past the cut, since its audio is whole there: next keeps the
    /// segments ending after the cut, earlier the segments starting before the first of those starts (all when none
    /// does), whole; a word may repeat at the join, none is lost. A next label maps to the earlier label holding over
    /// half its speaking time in the overlap and at least <see cref="MinimumSharedSeconds"/>, one to one, largest shared
    /// time first; any other label becomes <c>"p&lt;N&gt;:&lt;label&gt;"</c>, N the smallest number from
    /// <paramref name="partIndex"/> (the 0-based part index, so the second part is 1) that no earlier label uses as its
    /// <c>"p&lt;N&gt;:"</c> prefix. Unlabelled segments only go through the cut.
    /// </summary>
    public static List<CloudSegment> Append(
        IReadOnlyList<CloudSegment> next, IReadOnlyList<CloudSegment> earlier, TimeRange overlap, int partIndex)
    {
        var cut = overlap.End;
        var mapping = Labels(next, earlier, overlap, partIndex);
        var added = next.Where(segment => segment.End > cut).ToList();
        var joint = added.Count > 0 ? added.Min(segment => segment.Start) : double.PositiveInfinity;
        var result = earlier.Where(segment => segment.Start < joint).ToList();
        foreach (var segment in added)
            result.Add(Speaker(segment) is { } label ? segment with { Speaker = mapping[label] } : segment);
        return result;
    }

    /// <summary>Every labelled <paramref name="next"/> speaker's reconciled label.</summary>
    private static Dictionary<string, string> Labels(
        IReadOnlyList<CloudSegment> next, IReadOnlyList<CloudSegment> earlier, TimeRange overlap, int partIndex)
    {
        var total = new Dictionary<string, double>(StringComparer.Ordinal);
        var shared = new Dictionary<(string Next, string Earlier), double>();
        foreach (var segment in next)
        {
            if (Speaker(segment) is not { } label) continue;
            total[label] = total.GetValueOrDefault(label) + Length(segment.Start, Math.Max(segment.Start, segment.End), overlap);
            foreach (var other in earlier)
            {
                if (Speaker(other) is not { } earlierLabel) continue;
                var start = Math.Max(segment.Start, other.Start);
                var end = Math.Min(segment.End, other.End);
                if (!(end > start)) continue;
                shared[(label, earlierLabel)] = shared.GetValueOrDefault((label, earlierLabel)) + Length(start, end, overlap);
            }
        }
        var candidates = shared
            .Where(pair => pair.Value >= MinimumSharedSeconds && pair.Value > 0.5 * total.GetValueOrDefault(pair.Key.Next))
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key.Next, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Earlier, StringComparer.Ordinal);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pair, _) in candidates)
        {
            if (mapping.ContainsKey(pair.Next) || taken.Contains(pair.Earlier)) continue;
            mapping[pair.Next] = pair.Earlier;
            taken.Add(pair.Earlier);
        }
        var earlierLabels = earlier.Select(Speaker).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var scope = Math.Max(0, partIndex);
        while (earlierLabels.Any(label => label.StartsWith($"p{scope}:", StringComparison.Ordinal))) scope++;
        foreach (var label in total.Keys)
            if (!mapping.ContainsKey(label)) mapping[label] = $"p{scope}:{label}";
        return mapping;
    }

    private static string? Speaker(CloudSegment segment) => string.IsNullOrEmpty(segment.Speaker) ? null : segment.Speaker;

    private static double Length(double start, double end, TimeRange overlap) =>
        Math.Max(0, Math.Min(end, overlap.End) - Math.Max(start, overlap.Start));
}
