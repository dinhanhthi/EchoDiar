using EchoDiar.AI;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;

namespace EchoDiar.LocalLLM;

/// <summary>
/// A downloaded GGUF embedding model run in-process with llama.cpp (LLamaSharp), the Windows counterpart of Swift
/// <c>MLXEmbedder</c>. <see cref="WithLoadedAsync{T}"/> loads the file, then frees the context and weights before it
/// returns. Callers run it inside <c>ModelLoadGate</c> (owner Embedder).
/// </summary>
/// <param name="entryID">Catalog entry id, also the index key.</param>
/// <param name="modelPath">The <c>.gguf</c> file.</param>
public sealed class LlamaEmbedder(string entryID, string modelPath) : ITextEmbedder
{
    public string EntryID { get; } = entryID;
    public string ModelPath { get; } = modelPath;

    public string Key => EntryID;
    public bool IsLocal => true;

    public async Task<T> WithLoadedAsync<T>(
        Func<ILoadedTextEmbedder, Task<T>> body, CancellationToken cancellationToken = default)
    {
        var flavor = EmbedderFlavor.For(EntryID);
        // One batch of texts at a time, each in its own sequence of at most MaxTokens.
        const int tokens = EmbedderFlavor.BatchSize * EmbedderFlavor.MaxTokens;
        var parameters = new ModelParams(ModelPath)
        {
            Embeddings = true,
            PoolingType = flavor.Pooling,
            SeqMax = EmbedderFlavor.BatchSize,
            ContextSize = tokens,
            // A KV stream of MaxTokens per sequence, so attention spans one text, not the whole batch.
            KVUnified = false,
            // The whole batch in one call; llama.cpp splits it into micro-batches of whole sequences (pooling needs
            // every token of a text in one). A 512-token micro-batch keeps the compute buffer near 300 MB for
            // Qwen3-Embedding (8192 tokens at once: about 4.8 GB, mostly the output head).
            BatchSize = tokens,
            UBatchSize = EmbedderFlavor.MaxTokens,
        };
        using var weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken);
        if (weights.NativeHandle is { HasEncoder: true, HasDecoder: true })
            throw LLMException.Provider("Encoder-decoder models cannot embed text.");
        using var context = weights.CreateContext(parameters);
        context.NativeHandle.SetEmbeddings(true);
        cancellationToken.ThrowIfCancellationRequested();
        return await body(new Loaded(context, flavor));
    }

    private sealed class Loaded(LLamaContext context, EmbedderFlavor flavor) : ILoadedTextEmbedder
    {
        private readonly LLamaBatch _batch = new();
        private readonly LLamaToken? _endToken = EndToken(context, flavor.EndToken);

        public async Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, bool isQuery, CancellationToken cancellationToken = default)
        {
            var prefixed = flavor.Prefixed(texts, isQuery);
            var vectors = new List<float[]>(texts.Count);
            foreach (var range in EmbedderFlavor.Batches(prefixed.Count))
            {
                cancellationToken.ThrowIfCancellationRequested();
                vectors.AddRange(await EmbedBatchAsync(prefixed.Skip(range.Start).Take(range.Count), cancellationToken));
            }
            return vectors;
        }

        /// <summary>
        /// One model call for up to <see cref="EmbedderFlavor.BatchSize"/> texts, each in its own sequence, so
        /// padding never exists and pooling (mean, or Qwen3's last token) sees only that text's tokens.
        /// </summary>
        private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken)
        {
            context.NativeHandle.MemoryClear();
            _batch.Clear();
            var count = 0;
            foreach (var text in texts)
            {
                // Model special tokens (BOS, BERT's [CLS]/[SEP]) but no special tokens parsed out of the text itself.
                var ids = context.Tokenize(text, addBos: true, special: false).ToList();
                if (_endToken is { } end && (ids.Count == 0 || ids[^1] != end)) ids.Add(end);
                // Keeps the closing special token the model was trained to end on.
                var kept = EmbedderFlavor.Truncated(ids, EmbedderFlavor.MaxTokens);
                _batch.AddRange(kept.ToArray(), 0, (LLamaSeqId)count, true);
                count++;
            }
            if (count == 0) return [];
            var model = context.NativeHandle.ModelHandle;
            if (model is { HasEncoder: true, HasDecoder: false })
            {
                var encoded = await context.EncodeAsync(_batch, cancellationToken);
                if (encoded != EncodeResult.Ok) throw LLMException.Provider($"llama.cpp could not embed the text ({encoded}).");
            }
            else
            {
                var decoded = await context.DecodeAsync(_batch, cancellationToken);
                if (decoded != DecodeResult.Ok) throw LLMException.Provider($"llama.cpp could not embed the text ({decoded}).");
            }
            var vectors = new float[count][];
            for (var seq = 0; seq < count; seq++)
            {
                // Normalised in Float32 (Swift EmbeddingMath.normalized).
                vectors[seq] = EmbeddingMath.Normalized(context.NativeHandle.GetEmbeddingsSeq((LLamaSeqId)seq).ToArray());
            }
            return vectors;
        }

        /// <summary>The model's token for <paramref name="text"/> (Qwen3's <c>&lt;|endoftext|&gt;</c>), when it is one token.</summary>
        private static LLamaToken? EndToken(LLamaContext context, string? text)
        {
            if (text is null) return null;
            var ids = context.Tokenize(text, addBos: false, special: true);
            return ids.Length == 1 ? ids[0] : null;
        }
    }
}

/// <summary>
/// Per-model prefixes and pooling (Swift <c>MLXEmbedder.Flavor</c>) and the batching and truncation plan. Pure: nothing
/// here loads the llama.cpp natives.
/// </summary>
internal sealed record EmbedderFlavor(
    string QueryPrefix = "", string PassagePrefix = "", LLamaPoolingType Pooling = LLamaPoolingType.Mean,
    string? EndToken = null)
{
    /// <summary>Texts per model call.</summary>
    internal const int BatchSize = 16;
    /// <summary>Tokens per text, special tokens included; longer texts are cut.</summary>
    internal const int MaxTokens = 512;

    internal const string Qwen3QueryPrefix =
        "Instruct: Given a question, retrieve conversation passages that answer it\nQuery: ";

    internal static EmbedderFlavor For(string entryID) => entryID switch
    {
        "gguf-qwen3-embedding-0.6b" => new(QueryPrefix: Qwen3QueryPrefix, Pooling: LLamaPoolingType.Last, EndToken: "<|endoftext|>"),
        "gguf-multilingual-e5-small" => new(QueryPrefix: "query: ", PassagePrefix: "passage: "),
        _ => new(),
    };

    internal IReadOnlyList<string> Prefixed(IReadOnlyList<string> texts, bool isQuery)
    {
        var prefix = isQuery ? QueryPrefix : PassagePrefix;
        return prefix.Length == 0 ? texts : texts.Select(text => prefix + text).ToList();
    }

    /// <summary>Consecutive ranges of at most <see cref="BatchSize"/> items covering <paramref name="count"/> items.</summary>
    internal static IEnumerable<(int Start, int Count)> Batches(int count)
    {
        for (var start = 0; start < count; start += BatchSize) yield return (start, Math.Min(BatchSize, count - start));
    }

    /// <summary>The first <paramref name="max"/> − 1 ids and the last one when longer than <paramref name="max"/>.</summary>
    internal static IReadOnlyList<T> Truncated<T>(IReadOnlyList<T> ids, int max) =>
        ids.Count <= max ? ids : [.. ids.Take(max - 1), ids[^1]];
}
