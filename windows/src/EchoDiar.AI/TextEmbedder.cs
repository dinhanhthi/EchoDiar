using System.Globalization;
using System.Text;
using EchoDiar.Core.Settings;

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

/// <summary>
/// An OpenAI-compatible or Ollama embeddings endpoint (Swift <c>APITextEmbedder</c>), through
/// <see cref="OpenAICompatibleClient.EmbedAsync"/>: the key only in the auth header, error text redacted, response size
/// capped. Nothing to load: <see cref="WithLoadedAsync{T}"/> runs the body with the client.
/// </summary>
public sealed class APITextEmbedder : ITextEmbedder
{
    private readonly OpenAICompatibleClient _client;
    private readonly string _model;

    /// <param name="handler">Null for the shared transport; a test seam otherwise.</param>
    public APITextEmbedder(ProviderConfig config, string? apiKey, string model, HttpMessageHandler? handler = null)
    {
        _client = new OpenAICompatibleClient(config, apiKey, handler);
        _model = model;
        Key = "api-" + FolderSafe(config.Id + "-" + model);
        IsLocal = Uri.TryCreate(config.BaseURL.Trim(), UriKind.Absolute, out var url)
            && url.DnsSafeHost.ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1";
    }

    public string Key { get; }

    public bool IsLocal { get; }

    public Task<T> WithLoadedAsync<T>(Func<ILoadedTextEmbedder, Task<T>> body, CancellationToken cancellationToken = default) =>
        body(new Loaded(_client, _model));

    /// <summary>Lowercased, every character (grapheme) outside <c>[a-z0-9._-]</c> replaced by <c>-</c>.</summary>
    internal static string FolderSafe(string text)
    {
        var safe = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text.ToLowerInvariant());
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            safe.Append(element is [var c] && (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-') ? c : '-');
        }
        return safe.ToString();
    }

    private sealed class Loaded(OpenAICompatibleClient client, string model) : ILoadedTextEmbedder
    {
        /// <summary><paramref name="isQuery"/> is unused: API models take no query/passage prefix here.</summary>
        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, bool isQuery, CancellationToken cancellationToken = default) =>
            client.EmbedAsync(texts, model, cancellationToken);
    }
}
