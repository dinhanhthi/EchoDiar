using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;

namespace EchoDiar.AI;

public enum AIUpdateKind { Status, Text }

/// <summary>
/// One update of an <see cref="AIService"/> job (Swift <c>AIService.Update</c>): <see cref="AIUpdateKind.Status"/> is
/// what a long job is doing ("Reading part 2 of 5…"), before the answer starts; <see cref="AIUpdateKind.Text"/> is the
/// next piece of the answer.
/// </summary>
public readonly record struct AIUpdate(AIUpdateKind Kind, string Value)
{
    public static AIUpdate Status(string status) => new(AIUpdateKind.Status, status);
    public static AIUpdate Text(string text) => new(AIUpdateKind.Text, text);
}

/// <summary>
/// Summaries and questions about one conversation (Swift <c>AIService</c>). Each job streams its answer, saves it in
/// the conversation folder when it completes (a new summary version + <c>hasSummary</c>, or two more <c>chat.json</c>
/// messages) and saves nothing when cancelled (the caller's token, or leaving the enumeration early) or failed.
/// <see cref="Stop"/> ends a job early and saves the answer streamed until then (a summary marked stopped); nothing
/// when no text was streamed yet. A title job names an archived chat and saves nothing. In-app models
/// (<see cref="ILocalLLMProvider"/>) run the whole job inside <see cref="ModelLoadGate"/> (owner LLM), loaded once and
/// unloaded before the gate is released.
/// </summary>
/// <remarks>
/// The Swift actor's only isolated member is the <c>chat.json</c> append; here a lock serializes it, so two answers
/// finishing together cannot drop each other's messages. The running jobs (Swift <c>Jobs</c>, two mutexes) are a list
/// under a second lock that <see cref="Stop"/> snapshots. Each job runs on the thread pool and passes its updates
/// through a channel; leaving the enumeration cancels the job and waits for it, so nothing is written after that.
/// </remarks>
public sealed class AIService(ModelLoadGate gate)
{
    internal const string WaitingStatus = "Waiting for live captions or transcription to finish…";
    internal const string LoadingStatus = "Loading the model…";

    /// <summary>Where the answers of a job come from: a provider's stream, or an in-app model loaded for the whole job.</summary>
    private sealed record Backend(ILLMProvider? Remote, ILocalLLMGenerator? Local);

    /// <summary>The text streamed by a job until it was cancelled; empty for a step that streams nothing (map and merge).</summary>
    private sealed class Interrupted(string text) : Exception
    {
        public string Text { get; } = text;
    }

    /// <summary>A running job: its cancellation, and whether <see cref="Stop"/> ended it.</summary>
    private sealed class Job(CancellationTokenSource cancel)
    {
        private volatile bool _stopped;

        public CancellationToken Token { get; } = cancel.Token;
        public bool IsStopped => _stopped;

        public void Stop()
        {
            _stopped = true;
            try
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job ended meanwhile.
            }
        }
    }

    private delegate Task Work(Backend backend, Action<AIUpdate> yield, Job job);

    private readonly Lock _jobsLock = new();
    private readonly List<Job> _jobs = [];
    private readonly Lock _chatLock = new();

    /// <summary>Ends every running job, saving what each has streamed; their enumerations then finish without an error.</summary>
    public void Stop()
    {
        Job[] running;
        lock (_jobsLock) running = [.. _jobs];
        foreach (var job in running) job.Stop();
    }

    /// <summary>
    /// Streams a new summary: one pass when the transcript fits, else map over the chunks and reduce the notes until one
    /// group is left; only the last reduce is streamed. <paramref name="language"/> is the summary-language setting.
    /// <paramref name="providerID"/> is saved with the summary.
    /// </summary>
    public IAsyncEnumerable<AIUpdate> Summarize(
        Conversation conversation, Transcript transcript, ILLMProvider provider, string providerID, string model,
        string language, CancellationToken cancellationToken = default) =>
        RunAsync(provider, model, async (backend, yield, job) =>
        {
            string summary;
            var cut = false;
            try
            {
                summary = await SummaryAsync(conversation, transcript, provider, backend, model, language, yield, job.Token);
            }
            catch (Interrupted interrupted) when (job.IsStopped && interrupted.Text.Length > 0)
            {
                (summary, cut) = (interrupted.Text, true);
            }
            if (job.Token.IsCancellationRequested && !job.IsStopped) throw new OperationCanceledException(job.Token);
            conversation.WriteSummary(summary, providerID, model, cut);
        }, cancellationToken);

    /// <summary>
    /// Streams the answer to <paramref name="question"/>, sent with the transcript (or its BM25 excerpts on small
    /// contexts), the saved <paramref name="summary"/> and the last <paramref name="historyLimit"/> messages of
    /// <paramref name="history"/> (0 or less = all of them); then appends the question and the answer to
    /// <c>chat.json</c>. <paramref name="providerID"/> is saved with the answer. <paramref name="language"/> is the
    /// response-language setting (<c>"auto"</c> = the question's).
    /// </summary>
    public IAsyncEnumerable<AIUpdate> Ask(
        string question, Conversation conversation, Transcript transcript, string? summary,
        IReadOnlyList<ChatLog.Message> history, int historyLimit, ILLMProvider provider, string providerID, string model,
        string language, CancellationToken cancellationToken = default)
    {
        var asked = DateTimeOffset.UtcNow;
        return RunAsync(provider, model, async (backend, yield, job) =>
        {
            var turns = Turns(history, historyLimit);
            var answerLanguage = OutputLanguage.Answer(language);
            var context = await provider.ContextWindowAsync(model, job.Token);
            var excerpts = TranscriptChunker.QuestionExcerpts(
                conversation, transcript, question, summary, context, turns, answerLanguage);
            var messages = PromptBuilder.QuestionMessages(
                conversation, transcript, excerpts, summary, question, turns, answerLanguage);
            var answer = await StoppableAnswerAsync(messages, backend, model, context, yield, job);
            AppendChat(conversation.Folder, [
                new ChatLog.Message { Role = ChatRole.User, Text = question, Timestamp = asked },
                new ChatLog.Message
                {
                    Role = ChatRole.Assistant, Text = answer, Provider = providerID, Model = model,
                    Timestamp = DateTimeOffset.UtcNow,
                },
            ]);
        }, cancellationToken);
    }

    /// <summary>
    /// Streams the answer to a question across the library, sent with the numbered <paramref name="sources"/> and the
    /// last <paramref name="historyLimit"/> messages of <paramref name="history"/> (0 or less = all of them); then
    /// appends the question and the answer, with its <paramref name="references"/>, to <paramref name="folder"/>'s
    /// <c>chat.json</c>. <paramref name="providerID"/> and <paramref name="language"/> as in <see cref="Ask"/>. The
    /// caller builds the sources and their references from the library index (Swift <c>librarySources</c>).
    /// </summary>
    public IAsyncEnumerable<AIUpdate> AskLibrary(
        string question, IReadOnlyList<PromptBuilder.LibrarySource> sources, IReadOnlyList<ChatLog.Reference> references,
        IReadOnlyList<ChatLog.Message> history, int historyLimit, ILLMProvider provider, string providerID, string model,
        string language, string folder, CancellationToken cancellationToken = default)
    {
        var asked = DateTimeOffset.UtcNow;
        return RunAsync(provider, model, async (backend, yield, job) =>
        {
            var messages = PromptBuilder.LibraryQuestionMessages(
                question, sources, Turns(history, historyLimit), OutputLanguage.Answer(language));
            var context = await provider.ContextWindowAsync(model, job.Token);
            var answer = await StoppableAnswerAsync(messages, backend, model, context, yield, job);
            // The global chat's folder does not exist before its first question.
            Directory.CreateDirectory(folder);
            AppendChat(folder, [
                new ChatLog.Message { Role = ChatRole.User, Text = question, Timestamp = asked },
                new ChatLog.Message
                {
                    Role = ChatRole.Assistant, Text = answer, Provider = providerID, Model = model,
                    Timestamp = DateTimeOffset.UtcNow, References = [.. references],
                },
            ]);
        }, cancellationToken);
    }

    /// <summary>
    /// A short title for <paramref name="chat"/>, from its questions: one text update with the whole cleaned title (none
    /// when the model gave nothing usable), no text streamed before it; an in-app model's job sends its status lines
    /// first.
    /// </summary>
    public IAsyncEnumerable<AIUpdate> Title(
        IReadOnlyList<ChatLog.Message> chat, ILLMProvider provider, string model,
        CancellationToken cancellationToken = default) =>
        RunAsync(provider, model, async (backend, yield, job) =>
        {
            var context = await provider.ContextWindowAsync(model, job.Token);
            var answer = await AnswerAsync(PromptBuilder.TitleMessages(chat), backend, model, context, null, job.Token);
            var title = TitleLine(answer);
            if (title.Length > 0) yield(AIUpdate.Text(title));
        }, cancellationToken);

    /// <summary>The first non-empty line of a title answer, without surrounding quotes or Markdown marks; at most 60 characters.</summary>
    internal static string TitleLine(string answer)
    {
        foreach (var line in answer.Split(PromptBuilder.NewlineCharacters))
        {
            var trimmed = TrimMarks(line, "*#\"'“”‘’");
            if (trimmed.Length > 0) return PromptBuilder.PrefixCharacters(trimmed, 60).Trim();
        }
        return "";
    }

    /// <summary>
    /// The last <paramref name="limit"/> messages of <paramref name="history"/> (0 or less = all of them), starting with
    /// a question: some APIs reject a conversation whose first turn is an answer.
    /// </summary>
    private static List<LLMMessage> Turns(IReadOnlyList<ChatLog.Message> history, int limit) =>
        (limit > 0 ? history.TakeLast(limit) : history)
        .SkipWhile(m => m.Role != ChatRole.User)
        .Select(m => new LLMMessage(m.Role == ChatRole.User ? LLMRole.User : LLMRole.Assistant, m.Text))
        .ToList();

    /// <summary>Serialized so two answers finishing together cannot drop each other's messages.</summary>
    private void AppendChat(string folder, IEnumerable<ChatLog.Message> messages)
    {
        lock (_chatLock)
        {
            var log = ChatLog.Read(folder);
            log.Messages.AddRange(messages);
            log.Write(folder);
        }
    }

    /// <summary>
    /// The answer of a question job: the whole answer, or the part streamed until <see cref="Stop"/>. Throws when the
    /// job was cancelled otherwise.
    /// </summary>
    private static async Task<string> StoppableAnswerAsync(
        IReadOnlyList<LLMMessage> messages, Backend backend, string model, int context, Action<AIUpdate> yield, Job job)
    {
        string answer;
        try
        {
            answer = await AnswerAsync(messages, backend, model, context, yield, job.Token);
        }
        catch (Interrupted interrupted) when (job.IsStopped && interrupted.Text.Length > 0)
        {
            answer = interrupted.Text;
        }
        if (job.Token.IsCancellationRequested && !job.IsStopped) throw new OperationCanceledException(job.Token);
        return answer;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool; leaving the enumeration early or <paramref name="cancellationToken"/>
    /// cancels it, and <see cref="Stop"/> too (the enumeration then finishes without an error). An in-app model is
    /// loaded inside the gate for the whole job and unloaded before the gate is released, also on error and
    /// cancellation. The enumeration ends only once the job has ended, after its file writes.
    /// </summary>
    private async IAsyncEnumerable<AIUpdate> RunAsync(
        ILLMProvider provider, string model, Work work, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var updates = Channel.CreateUnbounded<AIUpdate>(new UnboundedChannelOptions { SingleReader = true });
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var job = new Job(cancel);
        lock (_jobsLock) _jobs.Add(job);
        var task = Task.Run(() => RunJobAsync(job, provider, model, work, updates.Writer), CancellationToken.None);
        Exception? failure;
        try
        {
            await foreach (var update in updates.Reader.ReadAllAsync(CancellationToken.None)) yield return update;
        }
        finally
        {
            // The consumer left early: cancel and wait, so the model is unloaded and nothing is saved afterwards.
            if (!task.IsCompleted) cancel.Cancel();
            failure = await task;
        }
        // Rethrown here, not through the channel, which would wrap it in a ChannelClosedException.
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    /// <summary>The job's error; null when it completed or was stopped. Never throws.</summary>
    private async Task<Exception?> RunJobAsync(
        Job job, ILLMProvider provider, string model, Work work, ChannelWriter<AIUpdate> updates)
    {
        try
        {
            void Yield(AIUpdate update) => updates.TryWrite(update);
            if (provider is ILocalLLMProvider local)
            {
                // A recording transcriber holds the gate for the whole recording; a transcription for its run.
                Yield(AIUpdate.Status(WaitingStatus));
                await gate.WithExclusiveModelAsync(ModelLoadGate.Owner.Llm, async token =>
                {
                    Yield(AIUpdate.Status(LoadingStatus));
                    return await local.WithLoadedModelAsync(model, async generator =>
                    {
                        await work(new Backend(null, generator), Yield, job);
                        return true;
                    }, token);
                }, job.Token);
            }
            else
            {
                await work(new Backend(provider, null), Yield, job);
            }
            return null;
        }
        catch (Exception) when (job.IsStopped)
        {
            return null;
        }
        catch (Exception error)
        {
            return job.Token.IsCancellationRequested && error is not OperationCanceledException
                ? new OperationCanceledException(job.Token)
                : error;
        }
        finally
        {
            lock (_jobsLock) _jobs.Remove(job);
            updates.TryComplete();
        }
    }

    // Summary

    private static async Task<string> SummaryAsync(
        Conversation conversation, Transcript transcript, ILLMProvider provider, Backend backend, string model,
        string setting, Action<AIUpdate> yield, CancellationToken cancellationToken)
    {
        if (transcript.Segments.Count == 0) throw LLMException.Provider("The transcript is empty.");
        var language = OutputLanguage.Resolve(setting, transcript);
        var context = await provider.ContextWindowAsync(model, cancellationToken);
        var plan = TranscriptChunker.PlanSummary(conversation, transcript, language, context);
        if (plan is not SummaryPlan.MapReduce { Chunks: var chunks })
        {
            var messages = PromptBuilder.SummaryMessages(conversation, transcript, language);
            return await AnswerAsync(messages, backend, model, context, yield, cancellationToken);
        }
        var notes = new List<string>();
        for (var index = 0; index < chunks.Count; index++)
        {
            yield(AIUpdate.Status($"Reading part {index + 1} of {chunks.Count}…"));
            var messages = PromptBuilder.MapMessages(
                conversation, transcript, chunks[index], index + 1, chunks.Count,
                notes.Count > 0 ? SummaryLine(notes[^1]) : null, language);
            notes.Add(await AnswerAsync(messages, backend, model, context, null, cancellationToken));
        }
        while (true)
        {
            var groups = TranscriptChunker.ReduceGroups(notes, conversation, transcript, language, context);
            if (groups.Count <= 1)
            {
                yield(AIUpdate.Status("Writing the summary…"));
                var messages = PromptBuilder.ReduceMessages(
                    conversation, transcript, groups.Count > 0 ? groups[0] : [], language);
                return await AnswerAsync(messages, backend, model, context, yield, cancellationToken);
            }
            var merged = new List<string>();
            for (var index = 0; index < groups.Count; index++)
            {
                yield(AIUpdate.Status($"Merging notes {index + 1} of {groups.Count}…"));
                var messages = PromptBuilder.ReduceMessages(conversation, transcript, groups[index], language);
                merged.Add(await AnswerAsync(messages, backend, model, context, null, cancellationToken));
            }
            notes = merged;
        }
    }

    /// <summary>
    /// The map note's "Summary:" line (the next chunk's context), the text after it when the line has nothing else
    /// (<c>**Summary:**</c>), else the note's start; at most 800 UTF-8 bytes, the room <see cref="TranscriptChunker"/>
    /// keeps for it.
    /// </summary>
    internal static string SummaryLine(string note)
    {
        const string marks = "*# ";
        var text = TrimMarks(note, marks);
        var label = note.IndexOf("summary:", StringComparison.OrdinalIgnoreCase);
        if (label >= 0)
        {
            var rest = note[(label + "summary:".Length)..];
            var newline = rest.IndexOf('\n');
            var line = TrimMarks(newline < 0 ? rest : rest[..newline], marks);
            var after = TrimMarks(rest, marks);
            if (line.Length > 0) text = line;
            else if (after.Length > 0) text = after;
        }
        var allowance = Encoding.UTF8.GetByteCount(TranscriptChunker.PreviousSummaryAllowance);
        var length = 0;
        var bytes = 0;
        while (length < text.Length)
        {
            var next = StringInfo.GetNextTextElementLength(text, length);
            bytes += Encoding.UTF8.GetByteCount(text.AsSpan(length, next));
            if (bytes > allowance) break;
            length += next;
        }
        return text[..length];
    }

    /// <summary><paramref name="text"/> without leading and trailing whitespace, newlines and <paramref name="marks"/>.</summary>
    private static string TrimMarks(string text, string marks)
    {
        bool IsMark(char c) => char.IsWhiteSpace(c) || marks.Contains(c);
        var start = 0;
        var end = text.Length;
        while (start < end && IsMark(text[start])) start++;
        while (end > start && IsMark(text[end - 1])) end--;
        return text[start..end];
    }

    // Streaming

    /// <summary>
    /// The whole answer to <paramref name="messages"/>, passing each piece to <paramref name="yield"/> when set. Throws
    /// <see cref="Interrupted"/> when the job was cancelled (a cancelled stream or generation just ends), with the text
    /// so far when it was streamed, and an error for an empty answer. An in-app model's returned text is the answer,
    /// even when its streamed pieces missed some.
    /// </summary>
    private static async Task<string> AnswerAsync(
        IReadOnlyList<LLMMessage> messages, Backend backend, string model, int context, Action<AIUpdate>? yield,
        CancellationToken cancellationToken)
    {
        var maxTokens = MaxTokens(context);
        string text;
        if (backend.Local is { } generator)
        {
            text = await generator.GenerateAsync(
                messages, maxTokens, piece => yield?.Invoke(AIUpdate.Text(piece)), cancellationToken);
        }
        else
        {
            var streamed = new StringBuilder();
            try
            {
                await foreach (var piece in backend.Remote!.StreamAsync(messages, model, maxTokens, cancellationToken))
                {
                    streamed.Append(piece);
                    yield?.Invoke(AIUpdate.Text(piece));
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Stopped or cancelled: the text so far is handled below.
            }
            text = streamed.ToString();
        }
        var answer = text.Trim();
        if (cancellationToken.IsCancellationRequested) throw new Interrupted(yield is null ? "" : answer);
        if (answer.Length == 0) throw LLMException.Provider("The model returned an empty answer.");
        return answer;
    }

    /// <summary>The 20% of the context <see cref="TranscriptChunker"/> keeps for the answer, from 512 to 8192 tokens.</summary>
    internal static int MaxTokens(int context) =>
        Math.Clamp((int)(context * TranscriptChunker.AnswerReserve), 512, 8_192);
}

/// <summary>
/// <c>[mm:ss]</c> and <c>[h:mm:ss]</c> transcript citations in model answers (Swift <c>TranscriptCitation</c>; minutes may
/// pass 59, as <see cref="PromptBuilder.Timestamp"/> writes them). From 1000 minutes on, the minutes may carry a
/// thousands separator (<c>[1,000:05]</c>, as macOS writes them in some locales) or none (<c>[1000:05]</c>, as Windows
/// writes them).
/// </summary>
public static partial class TranscriptCitation
{
    /// <summary>1-3 digits with thousands groups, or up to 5 plain digits.</summary>
    internal const string Minutes = @"[0-9]{1,3}(?:[,.'  ][0-9]{3})+|[0-9]{1,5}";

    private const string Pattern = @"\[(?<a>" + Minutes + @"):(?<b>[0-9]{2})(?::(?<c>[0-9]{2}))?\](?!\()";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex CitationRegex();

    [GeneratedRegex(@"\A" + Pattern + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex WholeRegex();

    /// <summary>Seconds into the recording of <c>"[01:05]"</c>, <c>"[1:02:03]"</c> or <c>"[75:30]"</c>; null when it is not a citation.</summary>
    public static double? Seconds(string citation) =>
        WholeRegex().Match(citation) is { Success: true } match ? Seconds(match) : null;

    /// <summary>
    /// <paramref name="text"/> with every citation turned into a Markdown link to <c>&lt;scheme&gt;:&lt;seconds&gt;</c>;
    /// citations already followed by a link target are left alone.
    /// </summary>
    public static string Linked(string text, string scheme) =>
        CitationRegex().Replace(text, match =>
            Seconds(match) is { } seconds ? $"{match.Value}({scheme}:{(long)seconds})" : match.Value);

    private static double? Seconds(Match match)
    {
        if (!TryNumber(match.Groups["a"].Value, out var a) || !TryNumber(match.Groups["b"].Value, out var b)) return null;
        if (match.Groups["c"].Success)
        {
            if (!TryNumber(match.Groups["c"].Value, out var c) || b >= 60 || c >= 60) return null;
            return a * 3_600 + b * 60 + c;
        }
        if (b >= 60) return null;
        return a * 60 + b;
    }

    /// <summary>The digits of <paramref name="text"/>, thousands separators skipped.</summary>
    private static bool TryNumber(string text, out long value)
    {
        var digits = string.Concat(text.Where(char.IsAsciiDigit));
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// <c>[n mm:ss]</c>, <c>[n h:mm:ss]</c> (also <c>[n, mm:ss]</c>) and <c>[n]</c> source citations in library-chat answers
/// (Swift <c>LibraryCitation</c>), <c>n</c> being a <see cref="ChatLog.Reference"/> number.
/// </summary>
public static partial class LibraryCitation
{
    public const string Scheme = "echodiar-ref";

    /// <summary>An existing Markdown link (kept whole), or a citation not followed by a link target.</summary>
    [GeneratedRegex(
        @"(?<link>\[(?:[^\[\]\n]|\[[^\[\]\n]*\])*\]\([^)\s]*\))"
        + @"|\[(?<n>[0-9]{1,3})(?:,? +(?<t>(?:" + TranscriptCitation.Minutes + @"):[0-9]{2}(?::[0-9]{2})?))?\](?!\()",
        RegexOptions.CultureInvariant)]
    private static partial Regex CitationRegex();

    /// <summary>
    /// <paramref name="text"/> with every citation of a known source turned into a Markdown link to
    /// <c>echodiar-ref:&lt;uuid&gt;:&lt;seconds&gt;</c> (<c>echodiar-ref:&lt;uuid&gt;</c> for <c>[n]</c>), as
    /// <see cref="TranscriptCitation.Linked"/> does; citations of an unknown source and text inside Markdown links are
    /// left alone.
    /// </summary>
    public static string Linked(string text, IReadOnlyList<ChatLog.Reference> references) =>
        CitationRegex().Replace(text, match =>
        {
            if (match.Groups["link"].Success
                || !int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                || references.FirstOrDefault(r => r.Number == n) is not { } reference)
                return match.Value;
            var target = $"{Scheme}:{reference.ConversationID.ToString("D").ToUpperInvariant()}";
            if (match.Groups["t"].Success)
            {
                if (TranscriptCitation.Seconds($"[{match.Groups["t"].Value}]") is not { } seconds) return match.Value;
                target += $":{(long)seconds}";
            }
            return $"{match.Value}({target})";
        });

    /// <summary>
    /// The conversation and the moment (null for a summary citation) of an <c>echodiar-ref:&lt;uuid&gt;[:&lt;seconds&gt;]</c>
    /// link; null when it is not one, or its seconds are not within 0...86 400.
    /// </summary>
    public static (Guid Id, double? Seconds)? Parse(Uri url)
    {
        var text = url.OriginalString;
        if (!text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = text[(Scheme.Length + 1)..].Split(':');
        if (parts.Length is < 1 or > 2 || !Guid.TryParseExact(parts[0], "D", out var id)) return null;
        if (parts.Length == 1) return (id, null);
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds is < 0 or > 86_400)
            return null;
        return (id, seconds);
    }
}
