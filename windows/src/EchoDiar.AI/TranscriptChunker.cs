using System.Globalization;
using System.Text;
using EchoDiar.Core.Model;

namespace EchoDiar.AI;

/// <summary>How a summary fits the context window (Swift <c>TranscriptChunker.SummaryPlan</c>).</summary>
public abstract record SummaryPlan
{
    private SummaryPlan() { }

    /// <summary>One call over the whole transcript.</summary>
    public sealed record SinglePass : SummaryPlan;

    /// <summary>Chunks in time order; consecutive chunks share about 10% of their turns.</summary>
    public sealed record MapReduce(IReadOnlyList<IReadOnlyList<Transcript.Segment>> Chunks) : SummaryPlan;
}

/// <summary>
/// Fits a transcript into a model's context window (Swift <c>TranscriptChunker</c>): single pass when it fits, else
/// map-reduce over chunks split on speaker turns; Q&amp;A retrieval with BM25 on small windows.
/// </summary>
public static class TranscriptChunker
{
    /// <summary>Room kept for the answer.</summary>
    internal const double AnswerReserve = 0.2;
    internal const double Overlap = 0.1;
    /// <summary>Room kept in each map prompt for the previous chunk's summary.</summary>
    internal static readonly string PreviousSummaryAllowance = new('x', 800);

    /// <summary>
    /// Tokens of <paramref name="text"/>, estimated as UTF-8 bytes / 4, rounded up. English averages about 4 bytes per
    /// token; accented French and Vietnamese letters take 2-3 bytes and also split into more tokens, so the estimate
    /// grows with them. No tokenizer is needed, and every provider gets the same estimate.
    /// </summary>
    public static int EstimateTokens(string text) => (Encoding.UTF8.GetByteCount(text) + 3) / 4;

    /// <summary>Tokens left for the transcript (or notes): context − instructions − 20% of the context for the answer.</summary>
    public static int Budget(int contextWindow, int instructionTokens) =>
        Math.Max(contextWindow - instructionTokens - (int)(contextWindow * AnswerReserve), 0);

    // Summary

    /// <summary>Swift <c>summaryPlan(conversation:transcript:language:contextWindow:)</c>.</summary>
    public static SummaryPlan PlanSummary(
        Conversation conversation, Transcript transcript, OutputLanguage language, int contextWindow)
    {
        var single = PromptBuilder.SummaryMessages(conversation, transcript, language);
        if (Tokens(single) <= Budget(contextWindow, 0)) return new SummaryPlan.SinglePass();
        var lines = LineTokens(transcript.Segments, transcript);
        var map = PromptBuilder.MapMessages(
            conversation, transcript, [], part: 99, count: 99, PreviousSummaryAllowance, language);
        var chunkBudget = Budget(contextWindow, Tokens(map));
        return new SummaryPlan.MapReduce(Chunks(lines, chunkBudget)
            .Select(range => (IReadOnlyList<Transcript.Segment>)transcript.Segments[range.Start..range.End])
            .ToList());
    }

    /// <summary>
    /// Consecutive groups of notes to reduce together, each fitting the budget when it can. A group holds at least two
    /// notes (one when only one is left), so each round shrinks the list; repeat until one group is left, whose reduce
    /// is the summary.
    /// </summary>
    public static List<List<string>> ReduceGroups(
        IReadOnlyList<string> notes, Conversation conversation, Transcript transcript, OutputLanguage language,
        int contextWindow)
    {
        var instructions = PromptBuilder.ReduceMessages(conversation, transcript, [], language);
        var limit = Budget(contextWindow, Tokens(instructions));
        var groups = new List<List<string>>();
        var group = new List<string>();
        var used = 0;
        foreach (var note in notes)
        {
            var cost = EstimateTokens(note) + 3;
            if (group.Count >= 2 && used + cost > limit)
            {
                groups.Add(group);
                (group, used) = ([], 0);
            }
            group.Add(note);
            used += cost;
        }
        if (group.Count > 0) groups.Add(group);
        return groups;
    }

    /// <summary>
    /// Index ranges (<c>Start..End</c>, end exclusive) of <paramref name="lines"/> (token counts of consecutive turns),
    /// each within <paramref name="budget"/> when possible: a turn is never split, so a turn larger than the budget is a
    /// chunk on its own. Each chunk after the first starts with the previous chunk's last turns, up to 10% of the budget
    /// (or its last turn when that is under 25%), and always adds at least one new turn.
    /// </summary>
    internal static List<(int Start, int End)> Chunks(IReadOnlyList<int> lines, int budget)
    {
        var ranges = new List<(int Start, int End)>();
        if (lines.Count == 0) return ranges;
        var overlapLimit = (int)(budget * Overlap);
        var start = 0;
        while (true)
        {
            var covered = ranges.Count > 0 ? ranges[^1].End : 0;
            var end = start;
            var used = 0;
            while (end < lines.Count && (end <= covered || used + lines[end] <= budget))
            {
                used += lines[end];
                end++;
            }
            ranges.Add((start, end));
            if (end >= lines.Count) return ranges;
            var next = end;
            var carried = 0;
            while (next - 1 > start && carried + lines[next - 1] <= overlapLimit)
            {
                next--;
                carried += lines[next];
            }
            if (next == end && end - 1 > start && lines[end - 1] <= budget / 4) next = end - 1;
            start = next;
        }
    }

    // Q&A

    /// <summary>
    /// The transcript parts to send with a question: the whole transcript when it fits, else the BM25 top chunks that
    /// fit (next to the saved summary and the chat <paramref name="history"/>), merged and in time order.
    /// </summary>
    public static List<List<Transcript.Segment>> QuestionExcerpts(
        Conversation conversation, Transcript transcript, string question, string? summary, int contextWindow,
        IReadOnlyList<LLMMessage>? history = null, OutputLanguage? language = null)
    {
        var empty = transcript with { Segments = [] };
        var instructions = PromptBuilder.QuestionMessages(
            conversation, empty, [], summary, question, history, language);
        var limit = Budget(contextWindow, Tokens(instructions));
        var segments = transcript.Segments;
        var lines = LineTokens(segments, transcript);
        if (lines.Sum() <= limit) return [segments];

        var ranges = Chunks(lines, Math.Max(limit / 4, 1));
        var documents = ranges.Select(range => string.Join(" ", segments[range.Start..range.End].Select(s => s.Text))).ToList();
        var picked = new bool[segments.Count];
        var used = 0;
        foreach (var index in Bm25Rank(question, documents))
        {
            var (start, end) = ranges[index];
            var cost = 0;
            for (var i = start; i < end; i++) cost += lines[i];
            if (used + cost > limit) continue;
            for (var i = start; i < end; i++) picked[i] = true;
            used += cost;
        }
        // Picked chunks that overlap or touch merge into one excerpt.
        var excerpts = new List<List<Transcript.Segment>>();
        for (var i = 0; i < picked.Length; i++)
        {
            if (!picked[i]) continue;
            if (i == 0 || !picked[i - 1]) excerpts.Add([]);
            excerpts[^1].Add(segments[i]);
        }
        return excerpts;
    }

    /// <summary>Indices of <paramref name="documents"/>, best BM25 match for <paramref name="query"/> first (k1 = 1.2, b = 0.75); ties keep document order.</summary>
    public static List<int> Bm25Rank(string query, IReadOnlyList<string> documents) =>
        Bm25Rank(Terms(query), documents.Select(Terms).ToList());

    /// <summary><see cref="Bm25Rank(string, IReadOnlyList{string})"/> over already tokenised (<see cref="Terms"/>) query and documents.</summary>
    internal static List<int> Bm25Rank(IReadOnlyList<string> queryTerms, IReadOnlyList<IReadOnlyList<string>> docs)
    {
        var averageLength = Math.Max((double)docs.Sum(doc => doc.Count) / Math.Max(docs.Count, 1), 1);
        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var doc in docs)
        {
            foreach (var term in doc.Distinct(StringComparer.Ordinal)) frequency[term] = frequency.GetValueOrDefault(term) + 1;
        }
        var uniqueQuery = queryTerms.Distinct(StringComparer.Ordinal).ToList();
        var count = (double)docs.Count;
        var scores = docs.Select(doc =>
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in doc) counts[term] = counts.GetValueOrDefault(term) + 1;
            var score = 0.0;
            foreach (var term in uniqueQuery)
            {
                if (!counts.TryGetValue(term, out var tfCount) || !frequency.TryGetValue(term, out var dfCount)) continue;
                double tf = tfCount, df = dfCount;
                var idf = Math.Log(1 + (count - df + 0.5) / (df + 0.5));
                score += idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * doc.Count / averageLength));
            }
            return score;
        }).ToList();
        return Enumerable.Range(0, scores.Count).OrderByDescending(i => scores[i]).ThenBy(i => i).ToList();
    }

    /// <summary>
    /// Lowercased (invariant) NFC words split on anything that is not a letter, mark or digit (Swift
    /// <c>CharacterSet.alphanumerics</c>: categories L*, M*, N*). Diacritics are kept: Vietnamese tones tell words apart.
    /// </summary>
    internal static List<string> Terms(string text)
    {
        var lowered = text.ToLowerInvariant();
        string normalized;
        try
        {
            normalized = lowered.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // A lone surrogate (possible in JSON text): Swift strings hold U+FFFD there.
            normalized = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(lowered)).Normalize(NormalizationForm.FormC);
        }
        var terms = new List<string>();
        var word = new StringBuilder();
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (IsAlphanumeric(rune))
            {
                word.Append(rune.ToString());
            }
            else if (word.Length > 0)
            {
                terms.Add(word.ToString());
                word.Clear();
            }
        }
        if (word.Length > 0) terms.Add(word.ToString());
        return terms;
    }

    private static bool IsAlphanumeric(Rune rune) =>
        Rune.IsLetter(rune) || Rune.IsNumber(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    // Helpers

    private static List<int> LineTokens(IEnumerable<Transcript.Segment> segments, Transcript transcript) =>
        segments.Select(s => EstimateTokens(PromptBuilder.Line(s, transcript)) + 1).ToList();

    private static int Tokens(IEnumerable<LLMMessage> messages) => messages.Sum(m => EstimateTokens(m.Content));
}
