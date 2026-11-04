import Foundation

/// Joins the segments of audio sent in parts (split uploads, realtime reconnects, gap uploads), where each part's
/// provider labels speakers on its own: "A" in one part is not "A" in the next. Parts share an overlap of audio;
/// a new label takes the earlier label it shares most of its speaking time with there, else a part-scoped label of
/// its own, so two different people are never merged. Pure, for tests.
public enum SpeakerLabelReconciler {
    /// Seconds of audio a later part repeats from before the cut.
    public static let labelOverlapSeconds = 30.0
    /// Shared speaking time in the overlap a match needs, so a stray word does not merge two speakers.
    public static let minimumSharedSeconds = 3.0

    /// `earlier` followed by `next` (both in absolute time), with `next`'s labels reconciled. `overlap` is the audio
    /// both cover: `max(0, cut - labelOverlapSeconds)...cut`, its upper bound the cut. The later part owns what
    /// reaches past the cut, since its audio is whole there: next keeps the segments ending after the cut, earlier
    /// the segments starting before the first of those starts (all when none does), whole; a word may repeat at the
    /// join, none is lost.
    /// A next label maps to the earlier label holding over half its speaking time in the overlap and at least
    /// `minimumSharedSeconds`, one to one, largest shared time first; any other label becomes `"p<N>:<label>"`,
    /// N the smallest number from `partIndex` (the 0-based part index, so the second part is 1) that no earlier
    /// label uses as its `"p<N>:"` prefix. Unlabelled segments only go through the cut.
    public static func append(
        _ next: [CloudSegment], to earlier: [CloudSegment], overlap: ClosedRange<Double>, partIndex: Int
    ) -> [CloudSegment] {
        let cut = overlap.upperBound
        let added = next.filter { $0.end > cut }
        let joint = added.map(\.start).min() ?? .infinity
        let kept = earlier.filter { $0.start < joint }
        let mapping = labels(for: next, earlier: earlier, overlap: overlap, partIndex: partIndex)
        return kept + added.map { segment in
            var segment = segment
            if let label = speaker(of: segment) { segment.speaker = mapping[label] }
            return segment
        }
    }

    /// Every labelled `next` speaker's reconciled label.
    private static func labels(
        for next: [CloudSegment], earlier: [CloudSegment], overlap: ClosedRange<Double>, partIndex: Int
    ) -> [String: String] {
        var total: [String: Double] = [:]
        var shared: [Pair: Double] = [:]
        for segment in next {
            guard let label = speaker(of: segment) else { continue }
            total[label, default: 0] += length(segment.start...max(segment.start, segment.end), in: overlap)
            for other in earlier {
                guard let earlierLabel = speaker(of: other) else { continue }
                let start = max(segment.start, other.start), end = min(segment.end, other.end)
                guard end > start else { continue }
                shared[Pair(next: label, earlier: earlierLabel), default: 0] += length(start...end, in: overlap)
            }
        }
        let candidates = shared.filter { pair, seconds in
            seconds >= minimumSharedSeconds && seconds > 0.5 * (total[pair.next] ?? 0)
        }.sorted { lhs, rhs in
            if lhs.value != rhs.value { return lhs.value > rhs.value }
            if lhs.key.next != rhs.key.next { return lhs.key.next < rhs.key.next }
            return lhs.key.earlier < rhs.key.earlier
        }
        var mapping: [String: String] = [:]
        var taken: Set<String> = []
        for (pair, _) in candidates where mapping[pair.next] == nil && !taken.contains(pair.earlier) {
            mapping[pair.next] = pair.earlier
            taken.insert(pair.earlier)
        }
        let earlierLabels = Set(earlier.compactMap(speaker(of:)))
        var scope = max(0, partIndex)
        while earlierLabels.contains(where: { $0.hasPrefix("p\(scope):") }) { scope += 1 }
        for label in total.keys where mapping[label] == nil {
            mapping[label] = "p\(scope):\(label)"
        }
        return mapping
    }

    private struct Pair: Hashable {
        var next: String
        var earlier: String
    }

    private static func speaker(of segment: CloudSegment) -> String? {
        guard let speaker = segment.speaker, !speaker.isEmpty else { return nil }
        return speaker
    }

    private static func length(_ span: ClosedRange<Double>, in overlap: ClosedRange<Double>) -> Double {
        max(0, min(span.upperBound, overlap.upperBound) - max(span.lowerBound, overlap.lowerBound))
    }
}
