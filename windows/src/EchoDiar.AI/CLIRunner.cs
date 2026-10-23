using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EchoDiar.AI;

/// <summary>A subscription CLI (Swift <c>CLIPreset</c>); <see cref="CLIPresets.Slug"/> is its <c>ProviderConfig</c> preset slug.</summary>
public enum CLIPreset { Claude, Codex, OpenCode, Gemini }

public static class CLIPresets
{
    /// <summary>Swift <c>CLIPreset.allCases</c>, in the same order.</summary>
    public static IReadOnlyList<CLIPreset> All { get; } = Enum.GetValues<CLIPreset>();

    public static string Slug(this CLIPreset preset) => preset switch
    {
        CLIPreset.Claude => "claude-cli",
        CLIPreset.Codex => "codex-cli",
        CLIPreset.OpenCode => "opencode-cli",
        _ => "gemini-cli",
    };

    /// <summary>The preset with <paramref name="slug"/>; null for any other text (an API provider, a custom endpoint).</summary>
    public static CLIPreset? FromSlug(string? slug) =>
        All.Where(preset => preset.Slug() == slug).Select(preset => (CLIPreset?)preset).FirstOrDefault();

    /// <summary>The name looked up on PATH, without <c>.exe</c>/<c>.cmd</c>.</summary>
    public static string ExecutableName(this CLIPreset preset) => preset switch
    {
        CLIPreset.Claude => "claude",
        CLIPreset.Codex => "codex",
        CLIPreset.OpenCode => "opencode",
        _ => "gemini",
    };
}

public enum CLIEventKind { Delta, Segment, Skip }

/// <summary>
/// What one stdout line of a CLI means: <see cref="CLIEventKind.Delta"/> is a piece of the answer, streamed as it comes;
/// <see cref="CLIEventKind.Segment"/> is a whole answer segment, the last one being the answer (Codex agent messages,
/// OpenCode text parts).
/// </summary>
public readonly record struct CLIEvent(CLIEventKind Kind, string Text = "")
{
    public static CLIEvent Skip => new(CLIEventKind.Skip);
    public static CLIEvent Delta(string text) => new(CLIEventKind.Delta, text);
    public static CLIEvent Segment(string text) => new(CLIEventKind.Segment, text);
}

/// <summary>
/// Runs a user-installed subscription CLI as a text-only subprocess (Swift <c>CLIRunner</c>; docs/research/llm-providers.md
/// C): no tools, no edits, no MCP, cwd = a fresh empty temp dir, env = an allowlist only
/// (<see cref="ShellEnvironment.BaseEnvironment"/>). The prompt and transcript always go to stdin (argv is visible to
/// other processes and capped in length); argv carries only the fixed <see cref="Instructions"/>. The CLI is started
/// directly, never through cmd.exe (see <see cref="ShellEnvironment"/>). What a CLI can still do despite its flags is in
/// <see cref="ContainmentNote"/>.
/// </summary>
public sealed class CLIRunner : ILLMProvider
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(600);
    /// <summary>The only prompt text in argv.</summary>
    public const string Instructions =
        "Follow the instructions in the input. Reply with the answer text only; do not use tools or read or edit files.";
    internal const int StderrLimit = 4096;
    /// <summary>How long a stopped CLI gets to exit once its stdin is closed, before its process tree is killed.</summary>
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(3);
    /// <summary>How long stdout/stderr are still read after the CLI exits (a grandchild may keep them open).</summary>
    internal static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);
    private const int InputChunk = 64 * 1024;

    private readonly string? _executableOverride;
    private readonly TimeSpan _timeout;
    private readonly Action<Process>? _onStarted;
    private readonly ShellEnvironment _shell;

    /// <param name="executableOverride">The path set in settings (a <c>.exe</c> or npm <c>.cmd</c>); null or empty to search PATH.</param>
    /// <param name="onStarted">Called with the CLI's process right after it starts (Phase 23.4 puts it in the job object).</param>
    public CLIRunner(CLIPreset preset, string? executableOverride = null, TimeSpan? timeout = null, Action<Process>? onStarted = null)
        : this(preset, executableOverride, timeout, onStarted, ShellEnvironment.Shared)
    {
    }

    internal CLIRunner(
        CLIPreset preset, string? executableOverride, TimeSpan? timeout, Action<Process>? onStarted, ShellEnvironment shell)
    {
        Preset = preset;
        _executableOverride = executableOverride;
        _timeout = timeout ?? DefaultTimeout;
        _onStarted = onStarted;
        _shell = shell;
    }

    public CLIPreset Preset { get; }

    /// <summary>
    /// <paramref name="maxTokens"/> is ignored: none of the CLIs can limit the answer length. Cancelling or disposing the
    /// enumerator stops the CLI in the background (stdin closed, then its process tree killed after 3 s).
    /// </summary>
    public IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LLMMessage> messages, string model, int maxTokens, CancellationToken cancellationToken = default) =>
        StreamCommand(Command(Preset, messages, model), cancellationToken);

    /// <summary>Claude Code model aliases; the other CLIs use their own default when no model is set.</summary>
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Preset == CLIPreset.Claude ? ["sonnet", "opus", "haiku"] : []);

    public ValueTask<int> ContextWindowAsync(string model, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Preset switch
        {
            CLIPreset.Claude => 200_000,
            CLIPreset.Gemini => 1_000_000,
            _ => 128_000,
        });

    /// <summary>argv and stdin for one request. Pure, for tests.</summary>
    public static (IReadOnlyList<string> Arguments, string Input) Command(
        CLIPreset preset, IReadOnlyList<LLMMessage> messages, string model)
    {
        var prompt = StdinPrompt(messages);
        // Codex reads its whole prompt from stdin (`-`), so the instructions go there too.
        var input = preset == CLIPreset.Codex ? Instructions + "\n\n" + prompt : prompt;
        return (Arguments(preset, Instructions, model), input);
    }

    /// <summary>
    /// The single source of truth for CLI flags. Never <c>--bare</c> (skips the subscription login), <c>--auto</c>,
    /// <c>--force</c>, <c>--yolo</c> or <c>--dangerously-*</c>. A model that could be read as a flag (leading <c>-</c>),
    /// carries control characters or a cmd metacharacter (<c>" &amp; | &lt; &gt; ^ % !</c>) is dropped, so the CLI uses
    /// its default.
    /// </summary>
    public static IReadOnlyList<string> Arguments(CLIPreset preset, string instructions, string model)
    {
        model = SafeModel(model);
        string[] modelFlag(string flag) => model.Length == 0 ? [] : [flag, model];
        return preset switch
        {
            CLIPreset.Claude =>
            [
                "-p", instructions, "--tools", "", "--disallowedTools", "mcp__*", "--strict-mcp-config",
                "--disable-slash-commands", "--permission-mode", "dontAsk", "--no-session-persistence",
                "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                .. modelFlag("--model"),
            ],
            // No user config.toml (its MCP servers, hooks, notify, plugins), no execpolicy allow rules, escalations
            // rejected, no web search, no shell; the read-only sandbox also keeps the network off.
            CLIPreset.Codex =>
            [
                "exec", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules",
                "--sandbox", "read-only", "-c", "approval_policy=\"never\"", "-c", "web_search=\"disabled\"",
                "--disable", "plugins", "--disable", "apps", "--disable", "shell_tool", "--disable", "unified_exec",
                "--json", .. modelFlag("-m"), "-",
            ],
            // A private server: the shared background service would ignore this process's env (the deny config).
            CLIPreset.OpenCode =>
                ["run", "--standalone", "--agent", "plan", "--format", "json", .. modelFlag("-m"), instructions],
            // GeminiPolicyFile (written to the cwd by the run) denies every tool; no MCP server is on the allow list.
            _ =>
            [
                "-p", instructions, "-o", "stream-json", "--approval-mode", "plan", "--skip-trust", "-e", "none",
                "--policy", GeminiPolicyFile, "--allowed-mcp-server-names", "echodiar-none", .. modelFlag("-m"),
            ],
        };
    }

    /// <summary>OpenCode config: every permission denied, globally and for the plan agent (the last matching rule wins).</summary>
    public const string OpenCodeConfig =
        "{\"permission\":{\"*\":\"deny\",\"edit\":\"deny\",\"bash\":\"deny\",\"webfetch\":\"deny\",\"websearch\":\"deny\",\"task\":\"deny\",\"external_directory\":\"deny\"},"
        + "\"tools\":{\"*\":false},\"agent\":{\"plan\":{\"permission\":{\"*\":\"deny\"}}}}";

    /// <summary>Gemini CLI policy file name, relative to the CLI's cwd (the temp work dir).</summary>
    public const string GeminiPolicyFile = "echodiar-deny-tools.toml";

    /// <summary>User-tier policy denying every tool, MCP tools included; only a system admin policy ranks above it.</summary>
    public const string GeminiPolicy =
        "[[rule]]\n"
        + "toolName = \"*\"\n"
        + "decision = \"deny\"\n"
        + "priority = 999\n"
        + "denyMessage = \"Tools are disabled. Reply with text only.\"\n";

    /// <summary>What a CLI can still do despite the flags above, for the settings UI; null when fully contained.</summary>
    public static string? ContainmentNote(CLIPreset preset) => preset switch
    {
        CLIPreset.Claude => null,
        CLIPreset.Codex =>
            "Codex runs without your ~/.codex/config.toml (its model, MCP servers and hooks) in a read-only, offline sandbox, but its remaining built-in tools can still read files on this PC.",
        CLIPreset.OpenCode =>
            "OpenCode denies every tool, but MCP servers in your OpenCode config still start (their tools are denied).",
        _ => "Gemini CLI denies every tool and loads no MCP server or extension, unless a system-wide admin policy allows them.",
    };

    /// <summary>
    /// The child's whole environment: <see cref="ShellEnvironment.BaseEnvironment"/> from <paramref name="variable"/>
    /// (+ OpenCode's deny-all config). Nothing else from the app.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EnvironmentFor(CLIPreset preset, string path, Func<string, string?> variable)
    {
        var environment = ShellEnvironment.BaseEnvironment(path, variable);
        if (preset == CLIPreset.OpenCode) environment["OPENCODE_CONFIG_CONTENT"] = OpenCodeConfig;
        return environment;
    }

    /// <summary>System messages first, then the conversation; turns are labelled only when there is an assistant turn.</summary>
    internal static string StdinPrompt(IReadOnlyList<LLMMessage> messages)
    {
        var system = messages.Where(message => message.Role == LLMRole.System).Select(message => message.Content);
        var turns = messages.Where(message => message.Role != LLMRole.System).ToList();
        var labelled = turns.Any(message => message.Role == LLMRole.Assistant);
        var conversation = turns.Select(message =>
            labelled ? $"{(message.Role == LLMRole.User ? "User" : "Assistant")}: {message.Content}" : message.Content);
        return string.Join("\n\n", system.Concat(conversation));
    }

    /// <summary>One stdout line (JSONL) of <paramref name="preset"/>. Lines that are not the expected JSON are skipped. Pure, for tests.</summary>
    public static CLIEvent Parse(string line, CLIPreset preset)
    {
        switch (preset)
        {
            case CLIPreset.Claude:
                if (Decode<ClaudeLine>(line) is not { } claude) return CLIEvent.Skip;
                if (claude.Type == "stream_event" && claude.Event?.Delta is { Type: "text_delta", Text: { } delta })
                    return CLIEvent.Delta(delta);
                if (claude.Type == "result" && claude.IsError == true)
                    throw LLMException.Provider(claude.Result ?? claude.Subtype ?? "Claude Code failed.");
                break;
            case CLIPreset.Codex:
                if (Decode<CodexLine>(line) is not { } codex) return CLIEvent.Skip;
                switch (codex.Type)
                {
                    case "item.completed" when codex.Item?.Type == "agent_message":
                        if (codex.Item.Text is { } text) return CLIEvent.Segment(text);
                        break;
                    case "turn.failed": throw LLMException.Provider(codex.Error?.Message ?? "Codex failed.");
                    case "error": throw LLMException.Provider(codex.Message ?? "Codex failed.");
                }
                break;
            case CLIPreset.OpenCode:
                if (Decode<OpenCodeLine>(line) is not { } openCode) return CLIEvent.Skip;
                switch (openCode.Type)
                {
                    case "text":
                        if (openCode.Part?.Text is { } text) return CLIEvent.Segment(text);
                        break;
                    case "error":
                        throw LLMException.Provider(
                            openCode.Error?.Data?.Message ?? openCode.Error?.Message ?? openCode.Error?.Name ?? "OpenCode failed.");
                }
                break;
            default:
                if (Decode<GeminiLine>(line) is not { } gemini) return CLIEvent.Skip;
                switch (gemini.Type)
                {
                    case "message" when gemini.Role == "assistant" && gemini.Delta == true:
                        if (gemini.Content is { } content) return CLIEvent.Delta(content);
                        break;
                    case "error": throw LLMException.Provider(gemini.Message ?? "Gemini CLI failed.");
                    case "result" when gemini.Status == "error":
                        throw LLMException.Provider(gemini.Error?.Message ?? "Gemini CLI failed.");
                }
                break;
        }
        return CLIEvent.Skip;
    }

    /// <summary>
    /// stderr without warnings and stack frames, so the cause comes first (Gemini CLI prints a 256-color warning before
    /// its error). Pure, for tests.
    /// </summary>
    public static string StderrMessage(string stderr) =>
        string.Join("\n", stderr
            .Split(['\n', '\r', '\v', '\f', '\u0085', '\u2028', '\u2029'])
            .Select(TrimSpaces)
            .Where(line => line.Length > 0
                           && !line.StartsWith("Warning:", StringComparison.Ordinal)
                           && !line.StartsWith("at ", StringComparison.Ordinal)));

    /// <summary>"10 minutes", "1 minute, 30 seconds": Swift's wide minutes-and-seconds duration format.</summary>
    internal static string DurationText(TimeSpan duration)
    {
        var minutes = (long)duration.TotalMinutes;
        var parts = new List<string>();
        if (minutes > 0) parts.Add(minutes == 1 ? "1 minute" : $"{minutes} minutes");
        if (duration.Seconds > 0 || parts.Count == 0) parts.Add(duration.Seconds == 1 ? "1 second" : $"{duration.Seconds} seconds");
        return string.Join(", ", parts);
    }

    /// <summary>The model trimmed of spaces and tabs (not newlines, like Swift); empty when it is not safe to pass.</summary>
    private static string SafeModel(string model)
    {
        model = TrimSpaces(model);
        var unsafeRune = model.EnumerateRunes().Any(rune =>
            Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            || rune.IsBmp && "\"&|<>^%!".Contains((char)rune.Value));
        return model.StartsWith('-') || unsafeRune ? "" : model;
    }

    /// <summary>Swift <c>trimmingCharacters(in: .whitespaces)</c>: space separators and tabs.</summary>
    private static string TrimSpaces(string text)
    {
        static bool IsSpace(char c) => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start])) start++;
        while (end > start && IsSpace(text[end - 1])) end--;
        return text[start..end];
    }

    private static T? Decode<T>(string line) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, ProviderHttp.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // MARK: - Process

    /// <summary>
    /// Runs the CLI in the background, feeding its text through a channel, so disposing the enumerator only asks for a
    /// stop and returns at once (Swift's stream <c>onTermination</c>).
    /// </summary>
    private async IAsyncEnumerable<string> StreamCommand(
        (IReadOnlyList<string> Arguments, string Input) command, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var control = new ProcessControl();
        await using var registration = cancellationToken.Register(() => control.Stop(StopReason.Cancelled));
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(command, control, text => channel.Writer.TryWrite(text));
                channel.Writer.TryComplete();
            }
            catch (Exception error)
            {
                channel.Writer.TryComplete(error);
            }
        }, CancellationToken.None);
        var finished = false;
        try
        {
            await foreach (var text in channel.Reader.ReadAllAsync(cancellationToken)) yield return text;
            finished = true;
        }
        finally
        {
            if (!finished) control.Stop(StopReason.Cancelled);
        }
    }

    private async Task RunAsync((IReadOnlyList<string> Arguments, string Input) command, ProcessControl control, Action<string> yield)
    {
        var name = Preset.ExecutableName();
        var executable = _shell.Executable(name, _executableOverride)
                         ?? throw LLMException.Provider($"{name} was not found. Install it or set its path in Settings.");
        var workDirectory = ShellEnvironment.CreateWorkDirectory();
        try
        {
            if (Preset == CLIPreset.Gemini)
                await File.WriteAllTextAsync(Path.Combine(workDirectory, GeminiPolicyFile), GeminiPolicy, CancellationToken.None);
            var info = ShellEnvironment.StartInfo(
                executable, command.Arguments, EnvironmentFor(Preset, _shell.Path, _shell.Variable), workDirectory);
            using var process = new Process { StartInfo = info };
            try
            {
                process.Start();
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                throw LLMException.Provider($"Could not start {executable.DisplayPath}: {error.Message}");
            }
            try
            {
                if (_onStarted is not null)
                {
                    try
                    {
                        _onStarted(process);
                    }
                    catch
                    {
                        Kill(process);
                        throw;
                    }
                }
                await RunStartedAsync(process, command.Input, control, yield);
            }
            finally
            {
                control.Detach();
            }
        }
        finally
        {
            ShellEnvironment.DeleteWorkDirectory(workDirectory);
        }
    }

    private async Task RunStartedAsync(Process process, string input, ProcessControl control, Action<string> yield)
    {
        var name = Preset.ExecutableName();
        // The only WaitForExitAsync on this process: each call adds and removes an Exited handler, and concurrent
        // calls can lose one (the event's add/remove is not thread-safe), leaving that wait pending forever.
        var exited = process.WaitForExitAsync(CancellationToken.None);
        control.Attach(process, exited);
        _ = Task.Run(() => WriteInputAsync(process, input, control.InputStopped));
        var exitGrace = AfterExitAsync(exited);
        var errorTail = new MemoryStream();
        var errors = PumpAsync(process.StandardError.BaseStream, exitGrace, (buffer, count) =>
        {
            errorTail.Write(buffer, 0, count);
            if (errorTail.Length > 2 * StderrLimit) Keep(errorTail, StderrLimit);
        });
        using var timer = new CancellationTokenSource(_timeout);
        await using var timeout = timer.Token.Register(() => control.Stop(StopReason.TimedOut));

        // Split on LF only (U+2028/U+2029 may appear unescaped inside JSON strings).
        var line = new MemoryStream();
        string? answer = null;
        Exception? failure = null;
        void Consume()
        {
            var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            line.SetLength(0);
            if (failure is not null || text.Length == 0) return;
            try
            {
                var parsed = Parse(text, Preset);
                if (parsed.Kind == CLIEventKind.Delta) yield(parsed.Text);
                else if (parsed.Kind == CLIEventKind.Segment) answer = parsed.Text;
            }
            catch (LLMException error)
            {
                failure = error;
                control.Stop(StopReason.Failed);
            }
        }
        await PumpAsync(process.StandardOutput.BaseStream, exitGrace, (buffer, count) =>
        {
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                line.Write(buffer, start, i - start);
                start = i + 1;
                Consume();
            }
            if (failure is not null) return;
            line.Write(buffer, start, count - start);
            if (line.Length <= ProviderHttp.LineLimit) return;
            line.SetLength(0);
            failure = LLMException.Provider($"{name} sent a line longer than 4 MB.");
            control.Stop(StopReason.Failed);
        });
        Consume();
        await exited;
        await errors;
        var status = process.ExitCode;

        switch (control.Reason)
        {
            case StopReason.Cancelled: throw new OperationCanceledException();
            case StopReason.TimedOut: throw LLMException.Provider($"{name} did not finish within {DurationText(_timeout)}.");
        }
        if (failure is not null) throw failure;
        if (status != 0)
        {
            Keep(errorTail, StderrLimit);
            var message = StderrMessage(Encoding.UTF8.GetString(errorTail.GetBuffer(), 0, (int)errorTail.Length));
            throw LLMException.Provider(message.Length == 0 ? $"{name} exited with status {status}." : message);
        }
        if (answer is not null) yield(answer);
    }

    /// <summary>Completes <see cref="ExitGrace"/> after <paramref name="exited"/>.</summary>
    private static async Task AfterExitAsync(Task exited)
    {
        await exited;
        await Task.Delay(ExitGrace);
    }

    /// <summary>
    /// Reads <paramref name="stream"/> until EOF, or until <paramref name="exitGrace"/> completes: a grandchild that
    /// inherited the pipe would keep it open, so the pending read is then abandoned (a blocked pipe read cannot be
    /// cancelled).
    /// </summary>
    private static async Task PumpAsync(Stream stream, Task exitGrace, Action<byte[], int> onData)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = stream.ReadAsync(buffer).AsTask();
                if (await Task.WhenAny(read, exitGrace) != read)
                {
                    _ = read.ContinueWith(task => task.Exception, TaskScheduler.Default);
                    return;
                }
                var count = await read;
                if (count == 0) break;
                onData(buffer, count);
            }
        }
        catch (IOException)
        {
            // A broken pipe ends the output like EOF.
        }
        await stream.DisposeAsync();
    }

    /// <summary>
    /// Writes stdin off the reading path (a long transcript exceeds the pipe buffer) in chunks, stopping early when
    /// <paramref name="stop"/> fires, then closes it. Only this method touches stdin.
    /// </summary>
    private static async Task WriteInputAsync(Process process, string input, CancellationToken stop)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var stream = process.StandardInput.BaseStream;
            for (var offset = 0; offset < bytes.Length && !stop.IsCancellationRequested; offset += InputChunk)
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(InputChunk, bytes.Length - offset)), CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The CLI exited before reading everything.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Already gone.
            }
        }
    }

    /// <summary>Keeps the last <paramref name="limit"/> bytes of <paramref name="data"/>.</summary>
    private static void Keep(MemoryStream data, int limit)
    {
        if (data.Length <= limit) return;
        var tail = data.GetBuffer().AsSpan((int)data.Length - limit, limit).ToArray();
        data.SetLength(0);
        data.Write(tail);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited.
        }
    }

    private enum StopReason { Cancelled, TimedOut, Failed }

    /// <summary>
    /// Stops a CLI process at most once and remembers why: stdin is closed (the writer stops), then the whole process
    /// tree is killed if it is still running after <see cref="StopGrace"/>. Windows has no SIGINT for a console-less
    /// child, so closing stdin replaces Swift's interrupt.
    /// </summary>
    private sealed class ProcessControl
    {
        private readonly Lock _lock = new();
        private readonly CancellationTokenSource _input = new();
        private Process? _process;
        private Task? _exited;
        private StopReason? _reason;

        /// <summary>Fires when the CLI is asked to stop: the stdin writer stops and closes stdin.</summary>
        public CancellationToken InputStopped => _input.Token;

        public StopReason? Reason
        {
            get
            {
                lock (_lock) return _reason;
            }
        }

        /// <summary>
        /// Stops <paramref name="process"/> right away when a stop came before it started. <paramref name="exited"/> is
        /// the run's own wait for its exit.
        /// </summary>
        public void Attach(Process process, Task exited)
        {
            bool stopped;
            lock (_lock)
            {
                _process = process;
                _exited = exited;
                stopped = _reason is not null;
            }
            if (stopped) Interrupt(process, exited);
        }

        /// <summary>The run is over; a later stop only records its reason.</summary>
        public void Detach()
        {
            lock (_lock)
            {
                _process = null;
                _exited = null;
            }
        }

        public void Stop(StopReason reason)
        {
            Process? process;
            Task? exited;
            lock (_lock)
            {
                if (_reason is not null) return;
                _reason = reason;
                process = _process;
                exited = _exited;
            }
            if (process is not null && exited is not null) Interrupt(process, exited);
        }

        private void Interrupt(Process process, Task exited)
        {
            _input.Cancel();
            _ = Task.Run(async () =>
            {
                try
                {
                    await exited.WaitAsync(StopGrace);
                }
                catch (TimeoutException)
                {
                    Kill(process);
                }
            });
        }
    }

    private sealed record ClaudeLine(
        string Type, ClaudeLine.EventBody? Event = null, bool? IsError = null, string? Result = null, string? Subtype = null)
    {
        internal sealed record EventBody(DeltaBody? Delta = null);
        internal sealed record DeltaBody(string? Type = null, string? Text = null);
    }

    /// <param name="Error"><c>turn.failed</c>.</param>
    /// <param name="Message"><c>error</c>.</param>
    private sealed record CodexLine(string Type, CodexLine.ItemBody? Item = null, APIErrorBody? Error = null, string? Message = null)
    {
        internal sealed record ItemBody(string? Type = null, string? Text = null);
    }

    private sealed record OpenCodeLine(string Type, OpenCodeLine.PartBody? Part = null, OpenCodeLine.Failure? Error = null)
    {
        internal sealed record PartBody(string? Text = null);
        internal sealed record Failure(string? Name = null, string? Message = null, Details? Data = null);
        internal sealed record Details(string? Message = null);
    }

    private sealed record GeminiLine(
        string Type, string? Role = null, string? Content = null, bool? Delta = null, string? Message = null,
        string? Status = null, APIErrorBody? Error = null);
}
