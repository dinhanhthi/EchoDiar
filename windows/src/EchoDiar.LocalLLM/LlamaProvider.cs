using System.Buffers;
using System.Text;
using EchoDiar.AI;
using EchoDiar.Core.Models;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;

namespace EchoDiar.LocalLLM;

/// <summary>
/// A downloaded GGUF chat model run in-process with llama.cpp (LLamaSharp), the Windows counterpart of Swift
/// <c>MLXProvider</c>. <see cref="WithLoadedModelAsync{T}"/> loads the file once for a whole job and frees the weights
/// and every context before it returns. Callers run it inside <c>ModelLoadGate</c> (owner LLM).
/// </summary>
/// <param name="entryID">Catalog entry id, also the only model <see cref="ListModelsAsync"/> reports.</param>
/// <param name="modelPath">The <c>.gguf</c> file.</param>
public sealed class LlamaProvider(string entryID, string modelPath) : ILocalLLMProvider
{
    public string EntryID { get; } = entryID;
    public string ModelPath { get; } = modelPath;

    /// <summary>Tokens the model is loaded with; see <see cref="LlamaSettings.ContextSize"/>.</summary>
    private static int ChosenContext => LlamaSettings.ContextSize(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    public async Task<T> WithLoadedModelAsync<T>(
        string model, Func<ILocalLLMGenerator, Task<T>> body, CancellationToken cancellationToken = default)
    {
        var parameters = new ModelParams(ModelPath) { ContextSize = (uint)ChosenContext };
        // Disposed after `body` has returned, so after the generator's last context is gone.
        using var weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // A model trained on a shorter window than the RAM cap gets its own (every catalog model takes 32K or more).
        if (weights.ContextSize > 0 && weights.ContextSize < ChosenContext)
            parameters = parameters with { ContextSize = (uint)weights.ContextSize };
        return await body(new Generator(weights, parameters));
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([EntryID]);

    /// <summary>
    /// The context the model is loaded with, without loading it: the RAM cap, or the catalog's training window when
    /// shorter (<see cref="ModelEntry.ContextTokens"/>, read from the GGUF header).
    /// </summary>
    public ValueTask<int> ContextWindowAsync(string model, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Math.Min(ChosenContext, ModelCatalog.Entry(EntryID)?.ContextTokens ?? int.MaxValue));

    /// <summary>
    /// Answers from loaded weights, each in a fresh context (KV cache) freed when the answer ends. Internal for the
    /// NativeLLM tests, which load the weights themselves.
    /// </summary>
    internal sealed class Generator(LLamaWeights weights, ModelParams parameters) : ILocalLLMGenerator
    {
        private readonly LlamaPrompt _prompt = new(weights);

        /// <summary>Streams the answer through <paramref name="onText"/>; on cancel returns the text so far (22.3).</summary>
        public async Task<string> GenerateAsync(
            IReadOnlyList<LLMMessage> messages, int maxTokens, Action<string> onText,
            CancellationToken cancellationToken = default)
        {
            var prompt = _prompt.Tokens(messages).Select(t => t.Token).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            using var context = weights.CreateContext(parameters);
            if (prompt.Count >= context.ContextSize)
                throw LLMException.Provider("The text is too long for this model's context.");
            // MLX's GenerateParameters defaults (the Mac app's sampling): temperature 0.6, no top-p, top-k or min-p.
            using var sampling = new DefaultSamplingPipeline { Temperature = 0.6f, TopP = 1f, TopK = 0, MinP = 0f };
            var decoder = new StreamingTokenDecoder(context);
            var batch = new LLamaBatch();
            var text = new StringBuilder();
            try
            {
                var (prefill, _, past) = await context.DecodeAsync(prompt, LLamaSeqId.Zero, batch, 0, cancellationToken);
                if (prefill != DecodeResult.Ok) throw LLMException.Provider($"llama.cpp could not read the prompt ({prefill}).");
                var limit = maxTokens < 0 ? int.MaxValue : maxTokens;
                // Ends at the model's end-of-turn token, maxTokens, cancellation or a full context.
                for (var i = 0; i < limit && past < context.ContextSize; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var token = sampling.Sample(context.NativeHandle, batch.TokenCount - 1);
                    if (token.IsEndOfGeneration(weights.Vocab)) break;
                    decoder.Add(token);
                    var piece = decoder.Read();
                    if (piece.Length > 0)
                    {
                        text.Append(piece);
                        onText(piece);
                    }
                    batch.Clear();
                    batch.Add(token, past++, LLamaSeqId.Zero, true);
                    var decoded = await context.DecodeAsync(batch, cancellationToken);
                    if (decoded != DecodeResult.Ok) throw LLMException.Provider($"llama.cpp could not continue the answer ({decoded}).");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            return text.ToString();
        }
    }
}

/// <summary>
/// The prompt of a chat as token ids, laid out so that only the chat template makes control tokens: the template's
/// text is tokenized with special tokens parsed, each message body without (so <c>&lt;|im_end|&gt;</c> in a
/// transcript stays text), and the user-defined tokens llama.cpp matches even then (Qwen's <c>&lt;think&gt;</c>,
/// Gemma's <c>&lt;|channel&gt;</c>) are broken in the bodies first.
/// </summary>
internal sealed class LlamaPrompt
{
    private readonly LLamaWeights _weights;
    private readonly string _template;
    private readonly IReadOnlyList<string> _userDefined;
    private readonly SearchValues<char> _userDefinedStarts;

    internal LlamaPrompt(LLamaWeights weights) : this(weights, UserDefinedTokens(weights.Vocab))
    {
    }

    /// <summary>
    /// With <paramref name="userDefined"/> as the token texts broken in bodies; the NativeLLM tests pass none to reach
    /// the check after tokenization.
    /// </summary>
    internal LlamaPrompt(LLamaWeights weights, IReadOnlyList<string> userDefined)
    {
        _weights = weights;
        _template = weights.NativeHandle.GetTemplate(strict: false)
            ?? throw LLMException.Provider("This model has no chat template.");
        _userDefined = userDefined;
        _userDefinedStarts = LlamaSettings.FirstCharacters(userDefined);
    }

    private static List<string> UserDefinedTokens(SafeLlamaModelHandle.Vocabulary vocab)
    {
        var userDefined = new List<string>();
        for (var id = 0; id < vocab.Count; id++)
        {
            LLamaToken token = id;
            if ((token.GetAttributes(vocab) & LLamaTokenAttr.UserDefined) != 0
                && vocab.LLamaTokenToString(token, isSpecialToken: true) is { Length: > 1 } text)
            {
                userDefined.Add(text);
            }
        }
        return userDefined;
    }

    /// <summary>
    /// The ids of <paramref name="messages"/> through the GGUF's chat template, each marked with whether it comes from
    /// a message body. Throws <see cref="LLMException"/> when the template cannot be applied or a body would still
    /// hold a control or user-defined token.
    /// </summary>
    internal List<(LLamaToken Token, bool FromBody)> Tokens(IReadOnlyList<LLMMessage> messages)
    {
        var tokens = new List<(LLamaToken, bool)>();
        foreach (var part in Parts(messages))
        {
            if (part.Text.Length == 0) continue;
            var text = part.IsBody ? LlamaSettings.Neutralized(part.Text, _userDefined, _userDefinedStarts) : part.Text;
            // BOS only before the first piece, when the model wants one (Gemma yes, Qwen no).
            var ids = _weights.Tokenize(text, add_bos: tokens.Count == 0, special: !part.IsBody, Encoding.UTF8);
            foreach (var id in ids)
            {
                if (part.IsBody && (id.GetAttributes(_weights.Vocab) & (LLamaTokenAttr.Control | LLamaTokenAttr.UserDefined)) != 0)
                {
                    throw LLMException.Provider("The text holds a token the model reserves for its chat format.");
                }
                tokens.Add((id, part.IsBody));
            }
        }
        return tokens;
    }

    /// <summary>
    /// Gemma 4 is laid out by hand: llama.cpp's built-in templates (all <c>llama_chat_apply_template</c> knows) have no
    /// Gemma 4 format. Others go through llama.cpp with a placeholder per body, then are cut at the placeholders.
    /// </summary>
    private List<PromptPart> Parts(IReadOnlyList<LLMMessage> messages)
    {
        if (LlamaSettings.IsGemma4(_template)) return LlamaSettings.Gemma4Parts(messages);
        var nonce = Guid.NewGuid().ToString("N");
        var template = new LLamaTemplate(_template) { AddAssistant = true };
        for (var i = 0; i < messages.Count; i++) template.Add(LlamaSettings.Role(messages[i].Role), LlamaSettings.Placeholder(nonce, i));
        string rendered;
        try
        {
            rendered = LLamaTemplate.Encoding.GetString(template.Apply());
        }
        catch (RuntimeError)
        {
            // MissingTemplateException's message holds the whole template source: never shown.
            throw LLMException.Provider("llama.cpp does not support this model's chat template.");
        }
        return LlamaSettings.SplitAtPlaceholders(LlamaSettings.WithThinkingOff(rendered, _template), messages, nonce);
    }
}

/// <summary>Pure parts of <see cref="LlamaProvider"/>; nothing here loads the llama.cpp natives.</summary>
internal static class LlamaSettings
{
    /// <summary>
    /// The 16 GB line, minus what Windows keeps from the OS (firmware, integrated GPU): a 16 GB PC reports about
    /// 15.4 to 15.9 GiB of physical memory.
    /// </summary>
    internal const long LargeMemoryBytes = 15L << 30;

    /// <summary>
    /// Room for a long transcript's KV cache next to the weights: 32K tokens on 16 GB PCs and up, 8K below (Swift
    /// <c>MLXProvider.contextWindow</c>).
    /// </summary>
    internal static int ContextSize(long totalMemoryBytes) => totalMemoryBytes >= LargeMemoryBytes ? 32_768 : 8_192;

    internal static string Role(LLMRole role) => role switch
    {
        LLMRole.System => "system",
        LLMRole.Assistant => "assistant",
        _ => "user",
    };

    /// <summary>What Qwen3's template writes after the assistant header when <c>enable_thinking</c> is false.</summary>
    internal const string EmptyThinking = "<think>\n\n</think>\n\n";

    /// <summary>
    /// llama.cpp's built-in templates cannot take <c>enable_thinking</c> (the Mac app passes false), so a template that
    /// reads it and uses <c>&lt;think&gt;</c> blocks gets the empty block its Jinja would write.
    /// </summary>
    internal static string WithThinkingOff(string prompt, string template) =>
        template.Contains("enable_thinking", StringComparison.Ordinal)
        && template.Contains("<think>", StringComparison.Ordinal)
        && !prompt.EndsWith(EmptyThinking, StringComparison.Ordinal)
            ? prompt + EmptyThinking
            : prompt;

    /// <summary>Gemma 4's chat template (<c>&lt;|turn&gt;role … &lt;turn|&gt;</c>), which llama.cpp's built-in matcher lacks.</summary>
    internal static bool IsGemma4(string template) =>
        template.Contains("<|turn>", StringComparison.Ordinal) && template.Contains("<turn|>", StringComparison.Ordinal);

    /// <summary>
    /// What Gemma 4's Jinja writes without tools and with <c>enable_thinking</c> false (the Mac app's setting): each
    /// message as <c>&lt;|turn&gt;role\n</c> + its trimmed text + <c>&lt;turn|&gt;\n</c> (the assistant's role is
    /// <c>model</c>), then <c>&lt;|turn&gt;model\n</c>; no <c>&lt;|think|&gt;</c> line, which would turn thinking on.
    /// Its <c>bos_token</c> is the tokenizer's BOS, added when the first piece is tokenized.
    /// </summary>
    internal static List<PromptPart> Gemma4Parts(IReadOnlyList<LLMMessage> messages)
    {
        var parts = new List<PromptPart>();
        var skeleton = new StringBuilder();
        foreach (var message in messages)
        {
            skeleton.Append("<|turn>").Append(message.Role == LLMRole.Assistant ? "model" : Role(message.Role)).Append('\n');
            parts.Add(new(skeleton.ToString(), IsBody: false));
            parts.Add(new(message.Content.Trim(), IsBody: true));
            skeleton.Clear().Append("<turn|>\n");
        }
        parts.Add(new(skeleton.Append("<|turn>model\n").ToString(), IsBody: false));
        return parts;
    }

    /// <summary>What stands for body <paramref name="index"/> while llama.cpp applies the template.</summary>
    internal static string Placeholder(string nonce, int index) => $"\u2063{nonce}:{index}\u2063";

    /// <summary>
    /// <paramref name="rendered"/> cut at each body's placeholder, in order, the bodies put back as body parts.
    /// Throws <see cref="LLMException"/> when a placeholder is missing, repeated or out of order (a template that
    /// rewrites its content).
    /// </summary>
    internal static List<PromptPart> SplitAtPlaceholders(string rendered, IReadOnlyList<LLMMessage> messages, string nonce)
    {
        var parts = new List<PromptPart>();
        var start = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var placeholder = Placeholder(nonce, i);
            var at = rendered.IndexOf(placeholder, start, StringComparison.Ordinal);
            if (at < 0 || rendered.IndexOf(placeholder, StringComparison.Ordinal) != at
                || rendered.IndexOf(placeholder, at + placeholder.Length, StringComparison.Ordinal) >= 0)
            {
                throw LLMException.Provider("llama.cpp does not support this model's chat template.");
            }
            parts.Add(new(rendered[start..at], IsBody: false));
            parts.Add(new(messages[i].Content, IsBody: true));
            start = at + placeholder.Length;
        }
        parts.Add(new(rendered[start..], IsBody: false));
        return parts;
    }

    /// <summary>
    /// <paramref name="text"/> with every occurrence of a <paramref name="tokens"/> text broken by a zero-width space
    /// after its first character, again until none is left (at most 8 passes), so the tokenizer cannot match it as
    /// that token.
    /// </summary>
    internal static string Neutralized(string text, IReadOnlyCollection<string> tokens) =>
        Neutralized(text, tokens, FirstCharacters(tokens));

    /// <summary>
    /// <see cref="Neutralized(string, IReadOnlyCollection{string})"/> with the first characters of
    /// <paramref name="tokens"/> (<see cref="FirstCharacters"/>): a text holding none of them, most of a long transcript's
    /// bodies, is returned at once instead of scanned once per token.
    /// </summary>
    internal static string Neutralized(string text, IReadOnlyCollection<string> tokens, SearchValues<char> firstCharacters)
    {
        if (!text.AsSpan().ContainsAny(firstCharacters)) return text;
        var changed = true;
        for (var pass = 0; pass < 8 && changed; pass++)
        {
            changed = false;
            foreach (var token in tokens)
            {
                if (token.Length < 2 || !text.Contains(token, StringComparison.Ordinal)) continue;
                text = text.Replace(token, token[0] + "\u200B" + token[1..], StringComparison.Ordinal);
                changed = true;
            }
        }
        return text;
    }

    /// <summary>The first character of each of <paramref name="tokens"/> that <see cref="Neutralized(string, IReadOnlyCollection{string})"/> breaks.</summary>
    internal static SearchValues<char> FirstCharacters(IEnumerable<string> tokens) =>
        SearchValues.Create([.. tokens.Where(t => t.Length >= 2).Select(t => t[0]).Distinct()]);
}

/// <summary>A piece of a prompt: chat template text (special tokens parsed) or a message body (never parsed).</summary>
internal readonly record struct PromptPart(string Text, bool IsBody);
