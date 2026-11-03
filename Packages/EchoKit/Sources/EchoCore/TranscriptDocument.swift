import Foundation

/// A transcript as one selectable text: per segment a header "mm:ss  ● Name", a line separator (U+2028, so header
/// and body stay one paragraph), then the body (the segment text, trimmed). Segments are joined by "\n"; the last one
/// has no trailing newline. Ranges are in UTF-16 code units (`NSString`), as the text view uses them.
public struct TranscriptDocument: Equatable, Sendable {
    /// One segment's place in `text`.
    public struct Entry: Equatable, Sendable {
        /// Index in the segments the document was built from.
        public let index: Int
        public let id: UUID
        /// Seconds from the start of the audio file.
        public let start: Double
        /// The display name the header shows and the copy uses.
        public let name: String
        /// `timestamp(start)`, as in the header and the copy.
        public let time: String
        /// "mm:ss  ● Name", without the line separator.
        public let headerRange: NSRange
        public let timeRange: NSRange
        public let nameRange: NSRange
        /// Empty (after the line separator) when the segment has no text.
        public let bodyRange: NSRange
    }

    public let text: String
    public let entries: [Entry]

    /// `names` gives each segment's display name (the renamed or localized speaker name); called once per segment.
    public init(segments: [Transcript.Segment], names: (Transcript.Segment) -> String) {
        var text = ""
        var offset = 0
        var entries: [Entry] = []
        entries.reserveCapacity(segments.count)
        func append(_ piece: String) -> NSRange {
            let range = NSRange(location: offset, length: piece.utf16.count)
            text += piece
            offset += range.length
            return range
        }
        for (index, segment) in segments.enumerated() {
            if index > 0 { _ = append("\n") }
            let name = names(segment), time = Self.timestamp(segment.start)
            let timeRange = append(time)
            _ = append("  ● ")
            let nameRange = append(name)
            _ = append("\u{2028}")
            let bodyRange = append(segment.text.trimmingCharacters(in: .whitespacesAndNewlines))
            entries.append(Entry(
                index: index, id: segment.id, start: segment.start, name: name, time: time,
                headerRange: NSRange(location: timeRange.location, length: NSMaxRange(nameRange) - timeRange.location),
                timeRange: timeRange, nameRange: nameRange, bodyRange: bodyRange
            ))
        }
        self.text = text
        self.entries = entries
    }

    /// The entry whose span, from its header start to its body end (both included, so a caret right after the body
    /// still belongs to it), holds `offset`; nil past the end or before the start.
    public func segment(at offset: Int) -> Entry? {
        // The last entry starting at or before `offset`.
        var low = 0, high = entries.count
        while low < high {
            let mid = (low + high) / 2
            if entries[mid].headerRange.location <= offset { low = mid + 1 } else { high = mid }
        }
        guard low > 0, offset <= NSMaxRange(entries[low - 1].bodyRange) else { return nil }
        return entries[low - 1]
    }

    /// The clipboard text for a selection: one "Name [mm:ss]: part" line per segment whose body the selection
    /// touches, the part being the selected piece of the body, joined by "\n". A header selected alone adds no line.
    /// When the selection touches no body at all (only headers), the selected text as it is.
    public func copyText(for range: NSRange) -> String {
        let string = text as NSString
        let lines = entries.compactMap { entry -> String? in
            guard let part = range.intersection(entry.bodyRange), part.length > 0 else { return nil }
            return "\(entry.name) [\(entry.time)]: \(string.substring(with: part))"
        }
        if !lines.isEmpty { return lines.joined(separator: "\n") }
        guard let part = range.intersection(NSRange(location: 0, length: string.length)) else { return "" }
        return string.substring(with: part)
    }

    /// mm:ss; minutes go past 59 in recordings longer than an hour. Same format as the transcript view and the AI
    /// prompts (`PromptBuilder.timestamp`): seconds rounded half to even.
    public static func timestamp(_ seconds: Double) -> String {
        Duration.seconds(max(seconds, 0)).formatted(.time(pattern: .minuteSecond(padMinuteToLength: 2)))
    }
}
