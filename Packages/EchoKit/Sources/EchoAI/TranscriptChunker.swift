import EchoCore
import Foundation

/// Fits a transcript into a model's context window (docs/research/llm-providers.md F): single pass when it fits,
/// else map-reduce over chunks split on speaker turns; Q&A retrieval with BM25 on small windows.
public enum TranscriptChunker {
    public enum SummaryPlan: Equatable, Sendable {
        case singlePass
        /// Chunks in time order; consecutive chunks share ~10% of their turns.
        case mapReduce([[Transcript.Segment]])
    }

    /// Room kept for the answer.
    static let answerReserve = 0.2
    static let overlap = 0.1
    /// Room kept in each map prompt for the previous chunk's summary.
    static let previousSummaryAllowance = String(repeating: "x", count: 800)

    /// Tokens of `text`, estimated as UTF-8 bytes / 4, rounded up. English averages ~4 bytes per token; accented
    /// French and Vietnamese letters take 2-3 bytes and also split into more tokens, so the estimate grows with
    /// them. No tokenizer is needed, and every provider gets the same estimate.
    public static func estimateTokens(_ text: String) -> Int {
        (text.utf8.count + 3) / 4
    }

    /// Tokens left for the transcript (or notes): context − instructions − 20% of the context for the answer.
    public static func budget(contextWindow: Int, instructionTokens: Int) -> Int {
        max(contextWindow - instructionTokens - Int(Double(contextWindow) * answerReserve), 0)
    }

    // MARK: Summary

    public static func summaryPlan(
        conversation: Conversation, transcript: Transcript, language: OutputLanguage, contextWindow: Int
    ) -> SummaryPlan {
        let single = PromptBuilder.summaryMessages(conversation: conversation, transcript: transcript, language: language)
        if tokens(single) <= budget(contextWindow: contextWindow, instructionTokens: 0) { return .singlePass }
        let lines = lineTokens(transcript.segments, transcript: transcript)
        let map = PromptBuilder.mapMessages(
            conversation: conversation, transcript: transcript, chunk: [], part: 99, count: 99,
            previousSummary: previousSummaryAllowance, language: language)
        let chunkBudget = budget(contextWindow: contextWindow, instructionTokens: tokens(map))
        return .mapReduce(chunks(lines: lines, budget: chunkBudget).map { Array(transcript.segments[$0]) })
    }

    /// Consecutive groups of notes to reduce together, each fitting the budget when it can. A group holds at
    /// least two notes (one when only one is left), so each round shrinks the list; repeat until one group is left,
    /// whose reduce is the summary.
    public static func reduceGroups(
        notes: [String], conversation: Conversation, transcript: Transcript, language: OutputLanguage,
        contextWindow: Int
    ) -> [[String]] {
        let instructions = PromptBuilder.reduceMessages(
            conversation: conversation, transcript: transcript, notes: [], language: language)
        let limit = budget(contextWindow: contextWindow, instructionTokens: tokens(instructions))
        var groups: [[String]] = []
        var group: [String] = []
        var used = 0
        for note in notes {
            let cost = estimateTokens(note) + 3
            if group.count >= 2, used + cost > limit {
                groups.append(group)
                (group, used) = ([], 0)
            }
            group.append(note)
            used += cost
        }
        if !group.isEmpty { groups.append(group) }
        return groups
    }

    /// Index ranges of `lines` (token counts of consecutive turns), each within `budget` when possible: a turn is
    /// never split, so a turn larger than the budget is a chunk on its own. Each chunk after the first starts with
    /// the previous chunk's last turns, up to 10% of the budget (or its last turn when that is under 25%), and
    /// always adds at least one new turn.
    static func chunks(lines: [Int], budget: Int) -> [Range<Int>] {
        guard !lines.isEmpty else { return [] }
        let overlapLimit = Int(Double(budget) * overlap)
        var ranges: [Range<Int>] = []
        var start = 0
        while true {
            let covered = ranges.last?.upperBound ?? 0
            var end = start
            var used = 0
            while end < lines.count, end <= covered || used + lines[end] <= budget {
                used += lines[end]
                end += 1
            }
            ranges.append(start..<end)
            guard end < lines.count else { return ranges }
            var next = end
            var carried = 0
            while next - 1 > start, carried + lines[next - 1] <= overlapLimit {
                next -= 1
                carried += lines[next]
            }
            if next == end, end - 1 > start, lines[end - 1] <= budget / 4 { next = end - 1 }
            start = next
        }
    }

    // MARK: Q&A

    /// The transcript parts to send with a question: the whole transcript when it fits, else the BM25 top chunks
    /// that fit (next to the saved summary and the chat `history`), merged and in time order.
    public static func questionExcerpts(
        conversation: Conversation, transcript: Transcript, question: String, summary: String?, contextWindow: Int,
        history: [LLMMessage] = [], language: OutputLanguage? = nil
    ) -> [[Transcript.Segment]] {
        var empty = transcript
        empty.segments = []
        let instructions = PromptBuilder.questionMessages(
            conversation: conversation, transcript: empty, excerpts: [], summary: summary, question: question,
            history: history, language: language)
        let limit = budget(contextWindow: contextWindow, instructionTokens: tokens(instructions))
        let lines = lineTokens(transcript.segments, transcript: transcript)
        if lines.reduce(0, +) <= limit { return [transcript.segments] }

        let ranges = chunks(lines: lines, budget: max(limit / 4, 1))
        let documents = ranges.map { range in
            transcript.segments[range].map(\.text).joined(separator: " ")
        }
        var picked = IndexSet()
        var used = 0
        for index in bm25Rank(query: question, documents: documents) {
            let cost = ranges[index].reduce(0) { $0 + lines[$1] }
            guard used + cost <= limit else { continue }
            picked.insert(integersIn: ranges[index])
            used += cost
        }
        return picked.rangeView.map { Array(transcript.segments[$0]) }
    }

    /// Indices of `documents`, best BM25 match for `query` first (k1 = 1.2, b = 0.75); ties keep document order.
    public static func bm25Rank(query: String, documents: [String]) -> [Int] {
        let docs = documents.map(terms)
        let averageLength = max(Double(docs.reduce(0) { $0 + $1.count }) / Double(max(docs.count, 1)), 1)
        var frequency: [String: Int] = [:]
        for doc in docs { for term in Set(doc) { frequency[term, default: 0] += 1 } }
        let queryTerms = Set(terms(query))
        let count = Double(docs.count)
        let scores = docs.map { doc -> Double in
            var counts: [String: Int] = [:]
            for term in doc { counts[term, default: 0] += 1 }
            return queryTerms.reduce(0) { score, term in
                guard let tf = counts[term].map(Double.init), let df = frequency[term].map(Double.init) else { return score }
                let idf = log(1 + (count - df + 0.5) / (df + 0.5))
                return score + idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * Double(doc.count) / averageLength))
            }
        }
        return scores.indices.sorted { (scores[$0], $1) > (scores[$1], $0) }
    }

    /// Lowercased NFC words split on anything that is not a letter, mark or digit. Diacritics are kept: Vietnamese
    /// tones tell words apart.
    static func terms(_ text: String) -> [String] {
        text.lowercased().precomposedStringWithCanonicalMapping
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
    }

    // MARK: Helpers

    private static func lineTokens(_ segments: [Transcript.Segment], transcript: Transcript) -> [Int] {
        segments.map { estimateTokens(PromptBuilder.line($0, transcript: transcript)) + 1 }
    }

    private static func tokens(_ messages: [LLMMessage]) -> Int {
        messages.reduce(0) { $0 + estimateTokens($1.content) }
    }
}
