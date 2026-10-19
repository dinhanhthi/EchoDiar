import Foundation

/// One ASR segment of a single channel, as returned by an engine.
public struct RawSegment: Codable, Equatable, Sendable {
    /// Seconds from the start of the audio file.
    public var start: Double
    public var end: Double
    public var text: String
    /// BCP-47 code detected by the engine, if any.
    public var detectedLanguage: String?
    /// The cloud provider's own speaker label ("A", "speaker_1", ...); nil for local engines.
    public var speaker: String?

    public init(start: Double, end: Double, text: String, detectedLanguage: String? = nil, speaker: String? = nil) {
        self.start = start
        self.end = end
        self.text = text
        self.detectedLanguage = detectedLanguage
        self.speaker = speaker
    }
}

/// One speaker turn from the diarizer on the system channel. `speakerIndex` is the diarizer's own label.
public struct DiarizationTurn: Codable, Equatable, Sendable {
    public var start: Double
    public var end: Double
    public var speakerIndex: Int

    public init(start: Double, end: Double, speakerIndex: Int) {
        self.start = start
        self.end = end
        self.speakerIndex = speakerIndex
    }
}

/// Merges the per-channel ASR output and the diarization turns into transcript segments.
public enum TranscriptMerger {
    /// Minimum share of a mic segment's tokens found in the overlapping system text for the mic segment to be
    /// treated as speaker bleed.
    public static let bleedTokenRatio = 0.8
    /// Mic segments shorter than this (in tokens) are dropped as bleed only on exact normalized equality, so a
    /// real "yeah" or "ok" survives when the other party said it too.
    public static let bleedMinTokens = 3
    /// Seconds of slack when checking whether a mic and a system segment overlap in time.
    public static let bleedTimeTolerance = 0.5

    /// Rules:
    /// - Mic segments get speaker `me`.
    /// - A system segment with a provider speaker label (cloud engines; the pipeline then passes no turns) takes
    ///   `s1…sN` by first appearance of its label in the system segments sorted by start.
    /// - A system segment takes the single turn with the largest time overlap (ties: the earlier turn). Speakers
    ///   are numbered `s1…sN` in order of first appearance in the turns sorted by start. With no turns, or no
    ///   overlapping turn, the segment gets `s1` (the pipeline adds `Transcript.diarizationMissingNote` when
    ///   diarization did not run).
    /// - Language: the last marker with `fileTime <= start`; when it is `auto`, empty or missing, the
    ///   engine-detected language.
    /// - Speaker bleed: a mic segment is dropped when its normalized text equals an overlapping system
    ///   segment's, or when it has at least `bleedMinTokens` tokens and at least `bleedTokenRatio` of them occur
    ///   in the combined text of the overlapping system segments.
    /// - Output is sorted by start; ties put the mic first.
    public static func merge(
        mic: [RawSegment],
        system: [RawSegment],
        turns: [DiarizationTurn],
        languageMarkers: [RecordingSession.LanguageMarker]
    ) -> [Transcript.Segment] {
        let markers = languageMarkers.sorted { $0.fileTime < $1.fileTime }
        let sortedTurns = turns.sorted { $0.start < $1.start }
        var labels: [Int: String] = [:]
        for turn in sortedTurns where labels[turn.speakerIndex] == nil {
            labels[turn.speakerIndex] = Transcript.Speaker.system(labels.count + 1)
        }
        var providerLabels: [String: String] = [:]
        for raw in system.sorted(by: { $0.start < $1.start }) {
            if let label = raw.speaker, providerLabels[label] == nil {
                providerLabels[label] = Transcript.Speaker.system(providerLabels.count + 1)
            }
        }

        let systemTokens = system.map { tokens(of: $0.text) }
        let keptMic = mic.filter { segment in
            !isBleed(segment, system: system, systemTokens: systemTokens)
        }

        var entries: [(rank: Int, index: Int, segment: Transcript.Segment)] = []
        for (index, raw) in keptMic.enumerated() {
            entries.append((0, index, segment(raw, channel: .mic, speaker: Transcript.Speaker.me, markers: markers)))
        }
        for (index, raw) in system.enumerated() {
            let speaker = raw.speaker.flatMap { providerLabels[$0] }
                ?? bestTurn(for: raw, in: sortedTurns).flatMap { labels[$0.speakerIndex] }
                ?? Transcript.Speaker.system(1)
            entries.append((1, index, segment(raw, channel: .system, speaker: speaker, markers: markers)))
        }
        entries.sort {
            if $0.segment.start != $1.segment.start { return $0.segment.start < $1.segment.start }
            if $0.rank != $1.rank { return $0.rank < $1.rank }
            return $0.index < $1.index
        }
        return entries.map(\.segment)
    }

    /// Lowercased words: runs of letters and digits, everything else is a separator (keeps diacritics).
    public static func tokens(of text: String) -> [String] {
        text.lowercased()
            .split { !($0.isLetter || $0.isNumber) }
            .map(String.init)
    }

    private static func segment(
        _ raw: RawSegment,
        channel: Transcript.Channel,
        speaker: String,
        markers: [RecordingSession.LanguageMarker]
    ) -> Transcript.Segment {
        Transcript.Segment(
            start: raw.start,
            end: max(raw.start, raw.end),
            channel: channel,
            speaker: speaker,
            text: raw.text.trimmingCharacters(in: .whitespacesAndNewlines),
            language: language(at: raw.start, markers: markers) ?? raw.detectedLanguage
        )
    }

    /// The marker language in effect at `time`, or nil for `auto`, empty or no marker. `markers` sorted by time.
    private static func language(at time: Double, markers: [RecordingSession.LanguageMarker]) -> String? {
        guard let marker = markers.last(where: { $0.fileTime <= time }) else { return nil }
        let language = marker.language.trimmingCharacters(in: .whitespacesAndNewlines)
        return language.isEmpty || language.lowercased() == "auto" ? nil : language
    }

    private static func overlap(_ aStart: Double, _ aEnd: Double, _ bStart: Double, _ bEnd: Double) -> Double {
        max(0, min(max(aStart, aEnd), max(bStart, bEnd)) - max(aStart, bStart))
    }

    private static func bestTurn(for raw: RawSegment, in turns: [DiarizationTurn]) -> DiarizationTurn? {
        var best: DiarizationTurn?
        var bestOverlap = 0.0
        for turn in turns {
            let value = overlap(raw.start, raw.end, turn.start, turn.end)
            if value > bestOverlap {
                best = turn
                bestOverlap = value
            }
        }
        return best
    }

    private static func isBleed(_ mic: RawSegment, system: [RawSegment], systemTokens: [[String]]) -> Bool {
        let micTokens = tokens(of: mic.text)
        guard !micTokens.isEmpty else { return false }
        let micStart = mic.start - bleedTimeTolerance
        let micEnd = max(mic.start, mic.end) + bleedTimeTolerance
        let overlapping = system.indices.filter { overlap(micStart, micEnd, system[$0].start, system[$0].end) > 0 }
        if overlapping.contains(where: { systemTokens[$0] == micTokens }) { return true }
        guard micTokens.count >= bleedMinTokens else { return false }

        var bag: [String: Int] = [:]
        for index in overlapping {
            for token in systemTokens[index] { bag[token, default: 0] += 1 }
        }
        var matched = 0
        for token in micTokens where bag[token, default: 0] > 0 {
            bag[token, default: 0] -= 1
            matched += 1
        }
        return Double(matched) >= bleedTokenRatio * Double(micTokens.count)
    }
}
