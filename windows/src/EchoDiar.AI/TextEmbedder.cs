namespace EchoDiar.AI;

/// <summary>
/// Turns text into vectors for the library index (Swift <c>TextEmbedder</c>). In-app models run inside
/// <c>ModelLoadGate</c> (owner Embedder), held per batch.
/// </summary>
public interface ITextEmbedder
{
    /// <summary>
    /// Folder-safe id that includes the model (<c>gguf-qwen3-embedding-0.6b</c>, <c>api-&lt;provider id&gt;-&lt;model&gt;</c>),
    /// so vectors of different models never share an index folder.
    /// </summary>
    string Key { get; }

    /// <summary>Whether no text leaves the PC (in-app models, loopback hosts).</summary>
    bool IsLocal { get; }

    /// <summary>
    /// Loads the model, runs <paramref name="body"/> with it, then unloads it. Returns (or throws) only once the model
    /// is unloaded, also when <paramref name="body"/> throws or is cancelled.
    /// </summary>
    Task<T> WithLoadedAsync<T>(Func<ILoadedTextEmbedder, Task<T>> body, CancellationToken cancellationToken = default);
}

/// <summary>A loaded embedder, valid inside <see cref="ITextEmbedder.WithLoadedAsync{T}"/>.</summary>
public interface ILoadedTextEmbedder
{
    /// <summary>
    /// One L2-normalised vector per text, in the order of <paramref name="texts"/>. <paramref name="isQuery"/> is true
    /// for a search question, false for indexed passages (models with query/passage prefixes use it).
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, bool isQuery, CancellationToken cancellationToken = default);
}
