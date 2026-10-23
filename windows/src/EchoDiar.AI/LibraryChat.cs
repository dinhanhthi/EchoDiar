using System.Text.RegularExpressions;
using EchoDiar.AI.Cloud;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;

namespace EchoDiar.AI;

/// <summary>
/// The pure parts of the chat across the library (macOS LibraryChatModel.swift): the sources of a question from the
/// index's hits, earlier answers without their citations, and the key of a cloud embedder's one-time confirmation.
/// </summary>
public static partial class LibraryChat
{
    /// <summary>At most this many passages go into one question's sources.</summary>
    internal const int MaxPassages = 12;

    /// <summary>The prompt's instructions besides the sources, history and question, in estimated tokens.</summary>
    internal const int InstructionTokens = 600;

    [GeneratedRegex(@"\[((?:[^\[\]\n]|\[[^\[\]\n]*\])*)\]\(echodiar-ref:[^)\s]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceLinkRegex();

    [GeneratedRegex(@"[ \t]*\[\d{1,3}(?:,? +\d{1,3}:\d{2}(?::\d{2})?)?\](?!\()", RegexOptions.CultureInvariant)]
    private static partial Regex CitationRegex();

    /// <summary>
    /// <paramref name="text"/> without source citations (macOS <c>withoutCitations</c>): <c>echodiar-ref</c> links become
    /// their text and <c>[n]</c> / <c>[n mm:ss]</c> markers go, so an earlier answer's numbers never point at the next
    /// question's sources.
    /// </summary>
    public static string WithoutCitations(string text) =>
        CitationRegex().Replace(ReferenceLinkRegex().Replace(text, match => match.Groups[1].Value), "");

    /// <summary>
    /// What the prompt holds besides the sources (macOS <c>libraryAnswer</c>): its instructions (about 600 tokens), the
    /// last <paramref name="historyLimit"/> messages of <paramref name="history"/> (0 or less = all) and the question.
    /// </summary>
    public static int PromptTokens(string question, IReadOnlyList<ChatLog.Message> history, int historyLimit)
    {
        var kept = historyLimit > 0 ? history.Skip(Math.Max(history.Count - historyLimit, 0)) : history;
        return kept.Aggregate(InstructionTokens + TranscriptChunker.EstimateTokens(question),
            (total, message) => total + TranscriptChunker.EstimateTokens(message.Text));
    }

    /// <summary>
    /// The best <paramref name="hits"/> of conversations still in <paramref name="conversations"/>, at most 12 passages
    /// fitting <paramref name="budget"/> estimated tokens, grouped per conversation in first-hit order and numbered from
    /// 1; with the answer's references (macOS <c>librarySources</c>). A hit that does not fit is skipped, a smaller later
    /// one may still fit.
    /// </summary>
    public static (IReadOnlyList<PromptBuilder.LibrarySource> Sources, IReadOnlyList<ChatLog.Reference> References) Sources(
        IReadOnlyList<LibraryIndex.Hit> hits, IReadOnlyList<Conversation> conversations, int budget)
    {
        var byId = new Dictionary<Guid, Conversation>();
        foreach (var conversation in conversations) byId.TryAdd(conversation.Id, conversation);
        List<Guid> order = [];
        Dictionary<Guid, List<string>> passages = [];
        var count = 0;
        var used = 0;
        foreach (var hit in hits)
        {
            if (count >= MaxPassages) break;
            if (!byId.TryGetValue(hit.ConversationID, out var conversation)) continue;
            var isNew = !passages.ContainsKey(hit.ConversationID);
            // A new source adds its tags and its line "Conversation n: title, date"; a passage its separator.
            var cost = TranscriptChunker.EstimateTokens(hit.Chunk.Text) + 2
                + (isNew ? TranscriptChunker.EstimateTokens(conversation.Title) + 20 : 0);
            if (used + cost > budget) continue;
            used += cost;
            if (isNew)
            {
                order.Add(hit.ConversationID);
                passages[hit.ConversationID] = [];
            }
            passages[hit.ConversationID].Add(hit.Chunk.Text);
            count++;
        }
        List<PromptBuilder.LibrarySource> sources = [];
        List<ChatLog.Reference> references = [];
        foreach (var (id, index) in order.Select((id, index) => (id, index)))
        {
            var conversation = byId[id];
            sources.Add(new PromptBuilder.LibrarySource(index + 1, conversation.Title, conversation.CreatedAt, passages[id]));
            references.Add(new ChatLog.Reference { Number = index + 1, ConversationID = id, Title = conversation.Title });
        }
        return (sources, references);
    }

    /// <summary>
    /// <c>&lt;provider id&gt;@&lt;scheme://host[:port]&gt;</c> (lowercased, default port left out), the key of a cloud
    /// embedder's confirmation in <c>cloudEmbeddingConfirmed</c> (macOS <c>embeddingDestination</c>): a changed base URL
    /// asks again. Null for a base URL that is not an absolute URL with a host, which is then never confirmed.
    /// </summary>
    public static string? EmbeddingDestination(ProviderConfig config) =>
        Uri.TryCreate(config.BaseURL.Trim(), UriKind.Absolute, out var url)
            ? CloudTranscription.Destination(config.Id, url)?.ConsentKey
            : null;
}
