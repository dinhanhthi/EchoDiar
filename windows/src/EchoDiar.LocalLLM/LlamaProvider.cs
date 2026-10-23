using System.Text;
using EchoDiar.AI;
using LLama;
using LLama.Common;
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

    /// <summary>The context the model is loaded with (the RAM cap), without loading it.</summary>
    public ValueTask<int> ContextWindowAsync(string model, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ChosenContext);

    private sealed class Generator(LLamaWeights weights, ModelParams parameters) : ILocalLLMGenerator
    {
        // Each answer gets a fresh context (KV cache), freed when the answer ends.
        private readonly StatelessExecutor _executor = new(weights, parameters);

        /// <summary>Streams the answer through <paramref name="onText"/>; on cancel returns the text so far (22.3).</summary>
        public async Task<string> GenerateAsync(
            IReadOnlyList<LLMMessage> messages, int maxTokens, Action<string> onText,
            CancellationToken cancellationToken = default)
        {
            var prompt = Prompt(messages);
            cancellationToken.ThrowIfCancellationRequested();
            var inference = new InferenceParams
            {
                MaxTokens = maxTokens,
                // MLX's GenerateParameters default (the Mac app's sampling).
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.6f },
            };
            var text = new StringBuilder();
            try
            {
                // The prompt prefill cannot be cancelled; the token loop stops at the next token.
                await foreach (var piece in _executor.InferAsync(prompt, inference, cancellationToken))
                {
                    if (piece.Length == 0) continue;
                    text.Append(piece);
                    onText(piece);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            return text.ToString();
        }

        /// <summary>The messages through the GGUF's chat template, with Qwen-style reasoning turned off.</summary>
        private string Prompt(IReadOnlyList<LLMMessage> messages)
        {
            var source = weights.NativeHandle.GetTemplate(strict: false)
                ?? throw LLMException.Provider("This model has no chat template.");
            var template = new LLamaTemplate(source) { AddAssistant = true };
            foreach (var message in messages) template.Add(LlamaSettings.Role(message.Role), message.Content);
            string prompt;
            try
            {
                prompt = LLamaTemplate.Encoding.GetString(template.Apply());
            }
            catch (LLama.Exceptions.MissingTemplateException)
            {
                throw LLMException.Provider("llama.cpp does not support this model's chat template.");
            }
            return LlamaSettings.WithThinkingOff(prompt, source);
        }
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
}
