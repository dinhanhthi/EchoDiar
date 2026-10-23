using EchoDiar.LocalLLM;
using LLama.Native;

namespace EchoDiar.Tests.LocalLLM;

// The pure parts of the GGUF adapters: context sizing by RAM, Qwen's thinking switch, the embedder's per-model
// prefixes and pooling, batching and truncation (MLXProvider.contextWindow, MLXEmbedder.Flavor/Loaded). Nothing here
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
