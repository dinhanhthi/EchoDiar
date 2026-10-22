namespace EchoDiar.Core;

/// <summary>
/// The library layout under one root, with the same folder names as macOS <c>Application Support/EchoDiar</c>.
/// The root is injectable: tests pass a temp folder, and the App may pass another (the Phase 1 fallback).
/// </summary>
public sealed class AppPaths(string root)
{
    /// <summary>The real <c>%LOCALAPPDATA%\EchoDiar</c> (the package manifest turns write virtualization off).</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoDiar");

    public AppPaths() : this(DefaultRoot) { }

    public string Root { get; } = root;

    /// <summary>One folder per recording, <c>&lt;yyyy-MM-dd_HHmmss&gt;-&lt;id8&gt;</c>.</summary>
    public string Conversations => Path.Combine(Root, "Conversations");

    /// <summary>Downloaded models, <c>&lt;catalog id&gt;/</c> each.</summary>
    public string Models => Path.Combine(Root, "Models");

    /// <summary>Library index shards, <c>&lt;embedder key&gt;/&lt;conversation id&gt;.json</c>.</summary>
    public string Rag => Path.Combine(Root, "RAG");

    /// <summary>The library-level chat: <c>chat.json</c> and <c>chats/</c>, as in a conversation folder.</summary>
    public string GlobalChat => Path.Combine(Root, "GlobalChat");

    /// <summary>DEBUG harness output (capture and native probes).</summary>
    public string Probes => Path.Combine(Root, "Probes");

    /// <summary>The app settings file.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");
}
