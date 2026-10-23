using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using EchoDiar.Core.Settings;

namespace EchoDiar.AI;

/// <summary>A local LLM host that answered its model-list probe.</summary>
/// <param name="Preset">The matching <see cref="ProviderConfig"/> preset slug (<c>lmstudio</c>, <c>ollama</c>, ...).</param>
/// <param name="BaseURL">The base URL that answered (llama-server can be on :9931 or :8080; Foundry Local's port varies).</param>
public sealed record DiscoveredHost(string Preset, string BaseURL, IReadOnlyList<string> Models);

/// <summary>
/// Probes the known local host ports (Swift <c>LocalHostDiscovery</c>), plus Foundry Local, whose port is read from
/// <c>foundry service status</c>. On demand only: no timers, no polling. No API key is ever sent.
/// </summary>
public static partial class LocalHostDiscovery
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(0.4);
    /// <summary>How long <c>foundry service status</c> may take.</summary>
    internal static readonly TimeSpan FoundryStatusTimeout = TimeSpan.FromSeconds(3);
    internal const string FoundryPreset = "foundry-local";

    internal static readonly (string Preset, string BaseURL)[] Probes =
    [
        ("lmstudio", "http://127.0.0.1:1234/v1"),
        ("ollama", "http://127.0.0.1:11434"),
        ("jan", "http://127.0.0.1:1337/v1"),
        ("llamacpp", "http://127.0.0.1:9931/v1"),
        ("llamacpp", "http://127.0.0.1:8080/v1"),
        ("vllm", "http://127.0.0.1:8000/v1"),
    ];

    /// <summary>
    /// All probes in parallel, each bounded by <see cref="DefaultTimeout"/>; hosts in probe order, Foundry Local last.
    /// Any error or non-2xx = not found. Call it off the UI thread: finding <c>foundry</c> reads the disk.
    /// </summary>
    public static Task<IReadOnlyList<DiscoveredHost>> ScanAsync(CancellationToken cancellationToken = default) =>
        ScanAsync(null, cancellationToken);

    /// <param name="onStarted">Called with the <c>foundry</c> process right after it starts, like CLIRunner's hook (the
    /// App passes <c>ChildProcessJob.Shared.Assign</c>); if it throws, the process tree is killed and Foundry is skipped.</param>
    public static Task<IReadOnlyList<DiscoveredHost>> ScanAsync(Action<Process>? onStarted, CancellationToken cancellationToken = default) =>
        ScanAsync(DefaultTimeout, null,
            token => FoundryStatusAsync(ShellEnvironment.Shared, FoundryStatusTimeout, onStarted, token), cancellationToken);

    /// <param name="handler">Null for the shared transport; a test seam otherwise (still behind the redirect guard).</param>
    /// <param name="foundryStatus">The output of <c>foundry service status</c>; null when it is not installed or failed.</param>
    internal static async Task<IReadOnlyList<DiscoveredHost>> ScanAsync(
        TimeSpan timeout, HttpMessageHandler? handler, Func<CancellationToken, Task<string?>> foundryStatus,
        CancellationToken cancellationToken)
    {
        var probes = Probes.Select(entry => ProbeAsync(entry.Preset, entry.BaseURL, timeout, handler, cancellationToken)).ToList();
        probes.Add(FoundryAsync(timeout, handler, foundryStatus, cancellationToken));
        var hosts = await Task.WhenAll(probes);
        return hosts.OfType<DiscoveredHost>().ToList();
    }

    private static async Task<DiscoveredHost?> FoundryAsync(
        TimeSpan timeout, HttpMessageHandler? handler, Func<CancellationToken, Task<string?>> foundryStatus,
        CancellationToken cancellationToken)
    {
        string? output;
        try
        {
            output = await foundryStatus(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        return output is not null && FoundryBaseURL(output) is { } baseURL
            ? await ProbeAsync(FoundryPreset, baseURL, timeout, handler, cancellationToken)
            : null;
    }

    /// <summary><c>GET {base}/models</c> (Ollama: <c>GET {base}/api/tags</c>), the same request the client's <c>ListModelsAsync</c> sends.</summary>
    private static async Task<DiscoveredHost?> ProbeAsync(
        string preset, string baseURL, TimeSpan timeout, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        var config = new ProviderConfig
        {
            Id = preset, Kind = ProviderKind.Local, Preset = preset, DisplayName = preset, BaseURL = baseURL,
            AuthStyle = AuthStyle.None,
        };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            var models = await new OpenAICompatibleClient(config, null, handler).ListModelsAsync(limit.Token);
            return new DiscoveredHost(preset, baseURL, models);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>http://127.0.0.1:&lt;port&gt;/v1</c> for the first loopback endpoint (<c>127.0.0.1</c> or <c>localhost</c>) in
    /// the output of <c>foundry service status</c> (<c>... is running on http://localhost:5273/openai/status</c>).
    /// Only the port is taken from the text; any other host is never matched, so a non-loopback URL is never probed.
    /// </summary>
    internal static string? FoundryBaseURL(string output)
    {
        foreach (Match match in LoopbackEndpoint().Matches(output))
        {
            if (int.TryParse(match.Groups["port"].Value, out var port) && port is > 0 and <= 65535)
                return $"http://127.0.0.1:{port}/v1";
        }
        return null;
    }

    // The port must end the authority: a path, whitespace, the end, or punctuation, never `@`, `.host` or more digits.
    [GeneratedRegex("""(?<![A-Za-z0-9+.\-])http://(?:127\.0\.0\.1|localhost):(?<port>[0-9]{1,5})(?=$|[/\s,;!?)\]"'])""", RegexOptions.IgnoreCase)]
    private static partial Regex LoopbackEndpoint();

    /// <summary>
    /// The output of <c>foundry service status</c> (at most 64 KB of stdout), run like the CLIs: resolved on the
    /// registry PATH (a <c>.cmd</c> that is not an npm shim counts as not installed), started directly (never through
    /// cmd.exe) with the allowlisted environment, killed after <paramref name="timeout"/>. Null when it is not
    /// installed, cannot start or times out.
    /// </summary>
    internal static async Task<string?> FoundryStatusAsync(
        ShellEnvironment shell, TimeSpan timeout, Action<Process>? onStarted, CancellationToken cancellationToken)
    {
        try
        {
            if (shell.Executable("foundry") is not { } executable) return null;
            var workDirectory = ShellEnvironment.CreateWorkDirectory();
            try
            {
                var info = ShellEnvironment.StartInfo(
                    executable, ["service", "status"], ShellEnvironment.BaseEnvironment(shell.Path, shell.Variable), workDirectory);
                info.RedirectStandardError = false;
                using var process = Process.Start(info)!;
                try
                {
                    onStarted?.Invoke(process);
                    process.StandardInput.Close();
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    limit.CancelAfter(timeout);
                    var output = await process.StandardOutput.BaseStream.ReadPrefixAsync(64 * 1024).WaitAsync(limit.Token);
                    return Encoding.UTF8.GetString(output);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        // Kill returns before the process is gone; its working directory can't be deleted until then.
                        process.WaitForExit(1000);
                    }
                }
            }
            finally
            {
                ShellEnvironment.DeleteWorkDirectory(workDirectory);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
