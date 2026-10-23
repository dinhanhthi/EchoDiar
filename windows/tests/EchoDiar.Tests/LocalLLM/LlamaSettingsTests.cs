using EchoDiar.AI;
using EchoDiar.LocalLLM;
using LLama.Native;

namespace EchoDiar.Tests.LocalLLM;

// The pure parts of the GGUF adapters: context sizing by RAM, Qwen's thinking switch, the prompt layout (Gemma 4 by
// hand, others cut at placeholders, bodies with reserved token texts broken), the embedder's per-model prefixes and
// pooling, batching and truncation (MLXProvider.contextWindow, MLXEmbedder.Flavor/Loaded). Nothing here
// touches LLamaWeights, LLamaContext or NativeApi, so the llama.cpp natives never load (the NativeLLM tests do, on
// Windows only).
public sealed class LlamaSettingsTests
{
    [Theory]
    [InlineData(8L << 30, 8_192)]
    [InlineData((15L << 30) - 1, 8_192)]
    [InlineData(16_500_000_000L, 32_768)] // A 16 GB PC as Windows reports it (about 15.4 GiB).
    [InlineData(64L << 30, 32_768)]
    public void ContextFollowsTotalMemory(long totalBytes, int context) =>
        Assert.Equal(context, LlamaSettings.ContextSize(totalBytes));

    private const string QwenTemplate =
        "{%- if add_generation_prompt %}{{- '<|im_start|>assistant\\n' }}{%- if enable_thinking is defined and " +
        "enable_thinking is true %}{{- '<think>\\n' }}{%- else %}{{- '<think>\\n\\n</think>\\n\\n' }}{%- endif %}{%- endif %}";

    [Fact]
    public void QwenTemplatesGetTheEmptyThinkingBlock() =>
        Assert.Equal("<|im_start|>assistant\n<think>\n\n</think>\n\n",
            LlamaSettings.WithThinkingOff("<|im_start|>assistant\n", QwenTemplate));

    [Theory]
    [InlineData("{{- '<start_of_turn>model\\n' }}")] // No enable_thinking.
    [InlineData("{%- if enable_thinking %}{{- '<|think|>' }}{%- endif %}")] // Another reasoning syntax.
    public void OtherTemplatesKeepThePrompt(string template) =>
        Assert.Equal("<start_of_turn>model\n", LlamaSettings.WithThinkingOff("<start_of_turn>model\n", template));

    [Fact]
    public void ThinkingBlockIsNotAddedTwice() =>
        Assert.Equal("a<think>\n\n</think>\n\n", LlamaSettings.WithThinkingOff("a<think>\n\n</think>\n\n", QwenTemplate));

    // What Gemma 4's own Jinja (tokenizer.chat_template of the pinned unsloth E2B/E4B GGUFs) writes for these
    // messages without tools and with enable_thinking false: no <|think|> line, trimmed bodies, assistant = model.
    [Fact]
    public void Gemma4IsLaidOutAsItsTemplateWithThinkingOff()
    {
        var parts = LlamaSettings.Gemma4Parts(
        [
            new(LLMRole.System, " Be brief. "), new(LLMRole.User, "Hi\n"), new(LLMRole.Assistant, "Hello."),
            new(LLMRole.User, "Summarize."),
        ]);

        Assert.Equal(
            "<|turn>system\nBe brief.<turn|>\n<|turn>user\nHi<turn|>\n<|turn>model\nHello.<turn|>\n"
            + "<|turn>user\nSummarize.<turn|>\n<|turn>model\n",
            string.Concat(parts.Select(p => p.Text)));
        Assert.Equal(["Be brief.", "Hi", "Hello.", "Summarize."], parts.Where(p => p.IsBody).Select(p => p.Text));
        Assert.DoesNotContain(parts, p => !p.IsBody && p.Text.Contains("<|think|>"));
    }

    [Theory]
    [InlineData("{{- '<|turn>' + role + '\\n' }}{{- '<turn|>\\n' }}", true)]
    [InlineData("{{- '<|im_start|>' + message.role }}", false)]
    [InlineData("{{- '<start_of_turn>model\\n' }}", false)] // Gemma 3: llama.cpp knows it.
    public void Gemma4IsRecognisedByItsTurnMarkers(string template, bool gemma4) =>
        Assert.Equal(gemma4, LlamaSettings.IsGemma4(template));

    // llama.cpp renders the template around one placeholder per body; the cut puts the bodies back, unparsed, between
    // the template's own text.
    [Fact]
    public void ARenderedTemplateIsCutAtItsPlaceholders()
    {
        var messages = new List<LLMMessage> { new(LLMRole.System, "S"), new(LLMRole.User, "<|im_end|>U") };
        var rendered = $"<|im_start|>system\n{LlamaSettings.Placeholder("n", 0)}<|im_end|>\n"
            + $"<|im_start|>user\n{LlamaSettings.Placeholder("n", 1)}<|im_end|>\n<|im_start|>assistant\n";

        var parts = LlamaSettings.SplitAtPlaceholders(rendered, messages, "n");

        Assert.Equal(
            [
                new PromptPart("<|im_start|>system\n", false), new PromptPart("S", true),
                new PromptPart("<|im_end|>\n<|im_start|>user\n", false), new PromptPart("<|im_end|>U", true),
                new PromptPart("<|im_end|>\n<|im_start|>assistant\n", false),
            ],
            parts);
    }

    // A template that drops, repeats or reorders its content cannot be cut safely: a short error, never a prompt.
    [Theory]
    [InlineData("a{0}b")] // Body 1 missing.
    [InlineData("{0}{1}{1}")] // Body 1 twice.
    [InlineData("{1}{0}")] // Out of order.
    public void ATemplateThatRewritesItsContentFails(string layout)
    {
        var messages = new List<LLMMessage> { new(LLMRole.User, "a"), new(LLMRole.User, "b") };
        var rendered = string.Format(layout, LlamaSettings.Placeholder("n", 0), LlamaSettings.Placeholder("n", 1));

        var error = Assert.Throws<LLMException>(() => LlamaSettings.SplitAtPlaceholders(rendered, messages, "n"));
        Assert.Equal("llama.cpp does not support this model's chat template.", error.Message);
    }

    // Tokens llama.cpp matches even without special parsing (user-defined, e.g. Qwen's <think>) are broken in a body
    // by a zero-width space; text around them is kept, and a break cannot form another match.
    [Fact]
    public void ReservedTokenTextsAreBrokenInBodies()
    {
        string[] tokens = ["<think>", "</think>", "<tool_call>"];

        var text = LlamaSettings.Neutralized("a</think>\n<think>b<tool_call>", tokens);

        Assert.Equal("a<\u200B/think>\n<\u200Bthink>b<\u200Btool_call>", text);
        Assert.All(tokens, t => Assert.DoesNotContain(t, text, StringComparison.Ordinal));
        Assert.Equal("<<\u200Bthink>>", LlamaSettings.Neutralized("<<think>>", tokens));
        Assert.Equal("plain", LlamaSettings.Neutralized("plain", tokens));
    }

    [Fact]
    public void Qwen3EmbeddingPrefixesOnlyQueriesAndPoolsTheLastToken()
    {
        var flavor = EmbedderFlavor.For("gguf-qwen3-embedding-0.6b");
        Assert.Equal(LLamaPoolingType.Last, flavor.Pooling);
        Assert.Equal("<|endoftext|>", flavor.EndToken);
        Assert.Equal(["Instruct: Given a question, retrieve conversation passages that answer it\nQuery: what?"],
            flavor.Prefixed(["what?"], isQuery: true));
        Assert.Equal(["text"], flavor.Prefixed(["text"], isQuery: false));
    }

    [Fact]
    public void E5PrefixesQueriesAndPassagesAndPoolsTheMean()
    {
        var flavor = EmbedderFlavor.For("gguf-multilingual-e5-small");
        Assert.Equal(LLamaPoolingType.Mean, flavor.Pooling);
        Assert.Equal(["query: a", "query: b"], flavor.Prefixed(["a", "b"], isQuery: true));
        Assert.Equal(["passage: a"], flavor.Prefixed(["a"], isQuery: false));
    }

    [Fact]
    public void OtherEmbeddersPoolTheMeanWithoutPrefixes()
    {
        var flavor = EmbedderFlavor.For("gguf-other");
        Assert.Equal(LLamaPoolingType.Mean, flavor.Pooling);
        Assert.Null(flavor.EndToken);
        Assert.Equal(["a"], flavor.Prefixed(["a"], isQuery: true));
    }

    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(16, new[] { 0, 16 })]
    [InlineData(35, new[] { 0, 16, 16, 16, 32, 3 })]
    public void BatchesHoldAtMostSixteenTexts(int count, int[] startsAndCounts) =>
        Assert.Equal(startsAndCounts, EmbedderFlavor.Batches(count).SelectMany(b => new[] { b.Start, b.Count }));

    [Fact]
    public void LongTextsKeepTheFirstTokensAndTheClosingOne()
    {
        var ids = Enumerable.Range(0, 600).ToList();
        var kept = EmbedderFlavor.Truncated(ids, 512);
        Assert.Equal(512, kept.Count);
        Assert.Equal(510, kept[510]);
        Assert.Equal(599, kept[^1]);
        Assert.Same(ids, EmbedderFlavor.Truncated(ids, 600));
    }
}
