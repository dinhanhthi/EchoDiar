using System.Diagnostics;
using System.Text.Json.Nodes;
using EchoDiar.AI;

namespace EchoDiar.Tests.AI;

// Guards the CLI security contract (read-only, no tools, transcript only on stdin, env allowlist, never cmd.exe) and
// the stream formats the parsers depend on (CLIArgumentsTests.swift), plus the Windows npm shim resolution (BatBadBut).
// No real CLI is run; the process tests run a fake CLI script on the Mac only.
public sealed class CLIArgumentsTests : IDisposable
{
    private const string Transcript = "Alice [00:05]: the launch moves to Friday";
    private readonly string root = Directory.CreateTempSubdirectory("EchoDiar-cli-tests-").FullName;

    private static readonly LLMMessage[] Messages =
        [new(LLMRole.System, "Summarize the meeting."), new(LLMRole.User, Transcript)];

    public static TheoryData<CLIPreset> Presets => new(CLIPresets.All);

    public void Dispose() => Directory.Delete(root, recursive: true);

    // MARK: - argv

    [Theory]
    [MemberData(nameof(Presets))]
    public void ArgvIsReadOnlyAndKeepsTranscriptOnStdin(CLIPreset preset)
    {
        var (argv, input) = CLIRunner.Command(preset, Messages, "some-model");

        Assert.DoesNotContain(argv, argument => argument.Contains("launch") || argument.Contains("Summarize"));
        Assert.Contains(Transcript, input);
        Assert.DoesNotContain(argv, argument =>
            argument is "--bare" or "--auto" or "--force" or "--yolo" || argument.StartsWith("--dangerously", StringComparison.Ordinal));
        switch (preset)
        {
            case CLIPreset.Claude:
                Assert.True(Adjacent(argv, "--tools", ""));
                Assert.True(Adjacent(argv, "--permission-mode", "dontAsk"));
                Assert.True(Adjacent(argv, "--output-format", "stream-json"));
                Assert.Contains("--include-partial-messages", argv);
                break;
            case CLIPreset.Codex:
                Assert.True(Adjacent(argv, "--sandbox", "read-only"));
                Assert.Contains("--ignore-user-config", argv);
                Assert.Contains("--ignore-rules", argv);
                Assert.True(Adjacent(argv, "-c", "approval_policy=\"never\""));
                Assert.True(Adjacent(argv, "-c", "web_search=\"disabled\""));
                foreach (var feature in (string[])["plugins", "apps", "shell_tool", "unified_exec"])
                    Assert.True(Adjacent(argv, "--disable", feature));
                Assert.Contains("--json", argv);
                Assert.Equal("-", argv[^1]);
                break;
            case CLIPreset.OpenCode:
                Assert.Contains("--standalone", argv);
                Assert.True(Adjacent(argv, "--agent", "plan"));
                Assert.True(Adjacent(argv, "--format", "json"));
                break;
            case CLIPreset.Gemini:
                Assert.True(Adjacent(argv, "--approval-mode", "plan"));
                Assert.Contains("--skip-trust", argv);
                Assert.True(Adjacent(argv, "-o", "stream-json"));
                Assert.True(Adjacent(argv, "-e", "none"));
                Assert.True(Adjacent(argv, "--policy", CLIRunner.GeminiPolicyFile));
                Assert.True(Adjacent(argv, "--allowed-mcp-server-names", "echodiar-none"));
                break;
        }
        Assert.Equal(preset == CLIPreset.Claude, CLIRunner.ContainmentNote(preset) is null);
    }

    public static TheoryData<CLIPreset, string[]> ArgvTable => new()
    {
        {
            CLIPreset.Claude,
            [
                "-p", "i", "--tools", "", "--disallowedTools", "mcp__*", "--strict-mcp-config", "--disable-slash-commands",
                "--permission-mode", "dontAsk", "--no-session-persistence", "--output-format", "stream-json", "--verbose",
                "--include-partial-messages", "--model", "m1",
            ]
        },
        {
            CLIPreset.Codex,
            [
                "exec", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules", "--sandbox",
                "read-only", "-c", "approval_policy=\"never\"", "-c", "web_search=\"disabled\"", "--disable", "plugins",
                "--disable", "apps", "--disable", "shell_tool", "--disable", "unified_exec", "--json", "-m", "m1", "-",
            ]
        },
        { CLIPreset.OpenCode, ["run", "--standalone", "--agent", "plan", "--format", "json", "-m", "m1", "i"] },
        {
            CLIPreset.Gemini,
            [
                "-p", "i", "-o", "stream-json", "--approval-mode", "plan", "--skip-trust", "-e", "none", "--policy",
                "echodiar-deny-tools.toml", "--allowed-mcp-server-names", "echodiar-none", "-m", "m1",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(ArgvTable))]
    public void ArgvMatchesTheSwiftListExactly(CLIPreset preset, string[] expected)
    {
        Assert.Equal(expected, CLIRunner.Arguments(preset, "i", "m1"));
        var model = Array.IndexOf(expected, "m1");
        Assert.Equal(expected.Where((_, index) => index != model && index != model - 1), CLIRunner.Arguments(preset, "i", ""));
    }

    public static TheoryData<CLIPreset, string> UnsafeModels
    {
        get
        {
            var data = new TheoryData<CLIPreset, string>();
            string[] models =
            [
                "--yolo", "m\n--yolo", "-", " --dangerously-skip-permissions", "m\u001B[2J", "m\u2028--yolo",
                // Windows: cmd metacharacters, never passed even though cmd.exe is never used.
                "a&b", "a|b", "a\"b", "m^x", "%PATH%", "m!x", "a<b", "a>b",
                "m\u200E", "m\n",
            ];
            foreach (var preset in CLIPresets.All)
            foreach (var model in models)
                data.Add(preset, model);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UnsafeModels))]
    public void ModelThatCouldBeAFlagOrCarriesCmdMetacharactersIsDropped(CLIPreset preset, string model)
    {
        var argv = CLIRunner.Arguments(preset, "i", model);

        Assert.DoesNotContain(argv, argument => argument.Contains("yolo") || argument.Contains("dangerously") || argument.Contains('\u001B'));
        Assert.DoesNotContain("--model", argv);
        Assert.DoesNotContain("-m", argv);
        Assert.True(!argv.Contains("-") || preset == CLIPreset.Codex);
        Assert.Equal(CLIRunner.Arguments(preset, "i", ""), argv);
    }

    public static TheoryData<CLIPreset, string, string> ValidModels
    {
        get
        {
            var data = new TheoryData<CLIPreset, string, string>();
            foreach (var preset in CLIPresets.All)
            {
                data.Add(preset, " gpt-5.1-codex ", "gpt-5.1-codex");
                data.Add(preset, "\tsonnet[1m]", "sonnet[1m]");
                data.Add(preset, "openrouter/qwen/qwen2.5:7b@latest", "openrouter/qwen/qwen2.5:7b@latest");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ValidModels))]
    public void ValidModelIsPassed(CLIPreset preset, string model, string passed)
    {
        var argv = CLIRunner.Arguments(preset, "i", model);

        Assert.True(Adjacent(argv, "--model", passed) || Adjacent(argv, "-m", passed));
    }

    [Fact]
    public void CodexGetsTheInstructionsOnStdinAndTurnsAreLabelledOnlyWithAnAssistantTurn()
    {
        Assert.Equal(CLIRunner.Instructions + "\n\nSummarize the meeting.\n\n" + Transcript,
            CLIRunner.Command(CLIPreset.Codex, Messages, "").Input);
        LLMMessage[] chat =
            [new(LLMRole.System, "S"), new(LLMRole.User, "Q1"), new(LLMRole.Assistant, "A1"), new(LLMRole.User, "Q2")];
        Assert.Equal("S\n\nUser: Q1\n\nAssistant: A1\n\nUser: Q2", CLIRunner.Command(CLIPreset.Claude, chat, "").Input);
    }

    [Fact]
    public void GeminiPolicyDeniesEveryToolAtTopUserPriority()
    {
        var lines = CLIRunner.GeminiPolicy.Split('\n');

        Assert.True(CLIRunner.GeminiPolicyFile.EndsWith(".toml", StringComparison.Ordinal) && !CLIRunner.GeminiPolicyFile.Contains('/'));
        Assert.DoesNotContain('\\', CLIRunner.GeminiPolicyFile);
        Assert.Contains("[[rule]]", lines);
        Assert.Contains("toolName = \"*\"", lines);
        Assert.Contains("decision = \"deny\"", lines);
        Assert.Contains("priority = 999", lines);
        Assert.DoesNotContain(lines, line =>
            line.StartsWith("modes", StringComparison.Ordinal) || line.StartsWith("mcpName", StringComparison.Ordinal)
            || line.StartsWith("interactive", StringComparison.Ordinal));
        Assert.DoesNotContain('\r', CLIRunner.GeminiPolicy);
    }

    // MARK: - Environment

    [Theory]
    [MemberData(nameof(Presets))]
    public void EnvironmentIsTheAllowlistOnlyWithOpenCodeDenyAllConfig(CLIPreset preset)
    {
        var parent = new Dictionary<string, string>
        {
            ["USERPROFILE"] = @"C:\Users\x", ["APPDATA"] = @"C:\Users\x\AppData\Roaming", ["LOCALAPPDATA"] = @"C:\Users\x\AppData\Local",
            ["SystemRoot"] = @"C:\Windows", ["TEMP"] = @"C:\Temp", ["TMP"] = @"C:\Tmp", ["USERNAME"] = "x",
            ["PATH"] = @"C:\ignored", ["HOME"] = @"C:\ignored", ["ANTHROPIC_API_KEY"] = "sk-ant", ["OPENAI_API_KEY"] = "sk-oa",
            ["NODE_OPTIONS"] = "--require evil.js", ["ComSpec"] = @"C:\Windows\system32\cmd.exe",
        };

        var environment = CLIRunner.EnvironmentFor(preset, @"C:\bin", name => parent.GetValueOrDefault(name));

        var expected = new Dictionary<string, string>
        {
            ["PATH"] = @"C:\bin", ["USERPROFILE"] = @"C:\Users\x", ["HOME"] = @"C:\Users\x",
            ["APPDATA"] = @"C:\Users\x\AppData\Roaming", ["LOCALAPPDATA"] = @"C:\Users\x\AppData\Local",
            ["SystemRoot"] = @"C:\Windows", ["TEMP"] = @"C:\Temp", ["TMP"] = @"C:\Tmp", ["USERNAME"] = "x",
        };
        if (preset != CLIPreset.OpenCode)
        {
            Assert.Equal(expected, environment);
            return;
        }
        Assert.Equal(expected.Count + 1, environment.Count);
        Assert.All(expected, pair => Assert.Equal(pair.Value, environment[pair.Key]));
        var json = JsonNode.Parse(environment["OPENCODE_CONFIG_CONTENT"])!.AsObject();
        Assert.Equal(
            """{"*":"deny","edit":"deny","bash":"deny","webfetch":"deny","websearch":"deny","task":"deny","external_directory":"deny"}""",
            json["permission"]!.ToJsonString());
        Assert.Equal("""{"*":false}""", json["tools"]!.ToJsonString());
        Assert.Equal("""{"*":"deny"}""", json["agent"]!["plan"]!["permission"]!.ToJsonString());
    }

    [Fact]
    public void MissingVariablesAreLeftOut()
    {
        Assert.Equal(new Dictionary<string, string> { ["PATH"] = "p" }, CLIRunner.EnvironmentFor(CLIPreset.Claude, "p", _ => null));
    }

    [Fact]
    public void PathIsTheBasePathThenTheKnownDirectoriesWithoutRelativeOrRepeatedEntries()
    {
        var home = Path.Combine(root, "home");
        var variables = new Dictionary<string, string>
        {
            ["APPDATA"] = Path.Combine(home, "Roaming"), ["USERPROFILE"] = home, ["LOCALAPPDATA"] = Path.Combine(home, "Local"),
        };
        var system = Path.Combine(root, "system");
        var shell = new ShellEnvironment(
            () => $"{system};;.;relative\\bin;\"{Path.Combine(root, "quoted")}\";{system.ToUpperInvariant()};{Path.Combine(home, "Roaming", "npm")}",
            name => variables.GetValueOrDefault(name));

        Assert.Equal(
            string.Join(';', system, Path.Combine(root, "quoted"), Path.Combine(home, "Roaming", "npm"),
                Path.Combine(home, ".local", "bin"), Path.Combine(home, "scoop", "shims"), Path.Combine(home, "Local", "Programs")),
            shell.Path);
    }

    // MARK: - Executable resolution (BatBadBut)

    /// <summary>cmd-shim 2-3 (npm 6 and older).</summary>
    private const string OldShim =
        "@IF EXIST \"%~dp0\\node.exe\" (\r\n"
        + "  \"%~dp0\\node.exe\"  \"%~dp0\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n"
        + ") ELSE (\r\n"
        + "  @SETLOCAL\r\n"
        + "  @SET PATHEXT=%PATHEXT:;.JS;=;%\r\n"
        + "  node  \"%~dp0\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n"
        + ")";

    /// <summary>cmd-shim 4+ (npm 7 and newer).</summary>
    private static string NewShim(string script = @"node_modules\@anthropic-ai\claude-code\cli.js", string prog = "node", string args = "") =>
        "@ECHO off\r\n"
        + "GOTO start\r\n"
        + ":find_dp0\r\n"
        + "SET dp0=%~dp0\r\n"
        + "EXIT /b\r\n"
        + ":start\r\n"
        + "SETLOCAL\r\n"
        + "CALL :find_dp0\r\n"
        + "\r\n"
        + $"IF EXIST \"%dp0%\\{prog}.exe\" (\r\n"
        + $"  SET \"_prog=%dp0%\\{prog}.exe\"\r\n"
        + ") ELSE (\r\n"
        + $"  SET \"_prog={prog}\"\r\n"
        + "  SET PATHEXT=%PATHEXT:;.JS;=;%\r\n"
        + ")\r\n"
        + "\r\n"
        + $"endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\" {args} \"%dp0%\\{script}\" %*\r\n";

    /// <summary>pnpm's @zkochan/cmd-shim (a global pnpm install).</summary>
    private const string PnpmShim =
        "@SETLOCAL\r\n"
        + "@IF NOT DEFINED NODE_PATH (\r\n"
        + "  @SET \"NODE_PATH=C:\\Users\\x\\AppData\\Local\\pnpm\\global\\5\\node_modules\"\r\n"
        + ") ELSE (\r\n"
        + "  @SET \"NODE_PATH=%NODE_PATH%;C:\\Users\\x\\AppData\\Local\\pnpm\\global\\5\\node_modules\"\r\n"
        + ")\r\n"
        + "@IF EXIST \"%~dp0\\node.exe\" (\r\n"
        + "  \"%~dp0\\node.exe\"  \"%~dp0\\global\\5\\node_modules\\@google\\gemini-cli\\dist\\index.js\" %*\r\n"
        + ") ELSE (\r\n"
        + "  @SET PATHEXT=%PATHEXT:;.JS;=;%\r\n"
        + "  node  \"%~dp0\\global\\5\\node_modules\\@google\\gemini-cli\\dist\\index.js\" %*\r\n"
        + ")\r\n";

    public static TheoryData<string, string> KnownShims => new()
    {
        { OldShim, @"node_modules\@anthropic-ai\claude-code\cli.js" },
        { NewShim(), @"node_modules\@anthropic-ai\claude-code\cli.js" },
        { NewShim(@"node_modules\@openai\codex\bin\codex.js").Replace("\r\n", "\n"), @"node_modules\@openai\codex\bin\codex.js" },
        { NewShim(@"node_modules\opencode-ai\bin\opencode"), @"node_modules\opencode-ai\bin\opencode" },
        { PnpmShim, @"global\5\node_modules\@google\gemini-cli\dist\index.js" },
    };

    [Theory]
    [MemberData(nameof(KnownShims))]
    public void KnownNpmShimFormatsGiveTheirScript(string shim, string script)
    {
        Assert.Equal(script, ShellEnvironment.ShimScript(shim));
    }

    public static TheoryData<string, string> UnknownShims => new()
    {
        { "bash shebang", NewShim(@"node_modules\x\run.sh", prog: "bash") },
        { "pwsh shebang", NewShim(@"node_modules\x\run.ps1", prog: "pwsh") },
        { "shebang arguments", NewShim(args: "--experimental-modules") },
        { "old shebang arguments", OldShim.Replace("node.exe\"  \"", "node.exe\" --inspect \"") },
        { "text after %*", NewShim().Replace(" %*\r\n", " %* & calc.exe\r\n") },
        { "old text after %*", OldShim.Replace("cli.js\" %*\r\n) ELSE", "cli.js\" %* | calc\r\n) ELSE") },
        { "variable in script", NewShim(@"node_modules\%EVIL%\cli.js") },
        { "delayed expansion in script", NewShim(@"node_modules\!x!\cli.js") },
        { "caret in script", NewShim(@"node_modules\a^&b\cli.js") },
        { "native target, no node", "@ECHO off\r\n\"%~dp0\\node_modules\\x\\bin\\x.exe\"   %*\r\n" },
        { "plain cmd", "@echo off\r\ncmd /c \"%~dp0\\x.js\" %*\r\n" },
        { "two scripts", OldShim.Replace("  node  \"%~dp0\\node_modules", "  \"%~dp0\\node.exe\"  \"%~dp0\\other") },
        { "extra _prog", NewShim().Replace("SETLOCAL\r\n", "SETLOCAL\r\nSET \"_prog=cmd.exe\"\r\n") },
        { "drive script", NewShim(@"C:\x.js") },
        { "old drive script", OldShim.Replace(@"node_modules\@anthropic-ai\claude-code\cli.js", @"C:\x.js") },
        { "drive-relative script", NewShim(@"node_modules\C:x.js") },
        { "UNC script", NewShim(@"\\host\share\x.js") },
        { "old UNC script", OldShim.Replace(@"node_modules\@anthropic-ai\claude-code\cli.js", @"\\host\share\x.js") },
        { "rooted script", NewShim(@"\x.js") },
        { "slash-rooted script", NewShim("/x.js") },
        { "empty", "" },
    };

    [Theory]
    [MemberData(nameof(UnknownShims))]
    public void UnknownShimFormatsAreRefused(string name, string shim)
    {
        Assert.True(ShellEnvironment.ShimScript(shim) is null, name);
    }

    [Fact]
    public void NpmShimResolvesToNodeNextToItAndTheScriptNeverCmd()
    {
        var npm = Directory.CreateDirectory(Path.Combine(root, "npm")).FullName;
        var script = Touch(npm, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        var node = Touch(npm, "node.exe");
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), NewShim());
        Touch(npm, "claude"); // the sh launcher npm writes for Git Bash; never run
        Touch(npm, "claude.ps1");

        var executable = Shell(npm).Executable("claude");

        Assert.NotNull(executable);
        Assert.Equal(node, executable.FileName);
        Assert.Equal([script], executable.LeadingArguments);
        Assert.Equal(Path.Combine(npm, "claude.cmd"), executable.DisplayPath);
        Assert.DoesNotContain(executable.LeadingArguments.Append(executable.FileName), part =>
            part.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || part.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShimScriptOutsideItsFolderIsRefused()
    {
        // `..` leaves the shim's folder; npm2 shares npm's prefix, so only a separator-aware check refuses it.
        var npm = Directory.CreateDirectory(Path.Combine(root, "npm")).FullName;
        Touch(npm, "node.exe");
        Touch(root, "npm2", "x.js");
        Touch(root, "x.js");
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), NewShim(@"..\npm2\x.js"));
        File.WriteAllText(Path.Combine(npm, "codex.cmd"), NewShim(@"node_modules\..\..\x.js"));

        Assert.Null(Shell(npm).Executable("claude"));
        Assert.Null(Shell(npm).Executable("codex"));
    }

    [Fact]
    public void ShimWithoutNodeNextToItUsesNodeExeOnPath()
    {
        var npm = Directory.CreateDirectory(Path.Combine(root, "npm")).FullName;
        var nodeDirectory = Directory.CreateDirectory(Path.Combine(root, "nodejs")).FullName;
        var script = Touch(npm, "node_modules", "@openai", "codex", "bin", "codex.js");
        File.WriteAllText(Path.Combine(npm, "codex.cmd"), OldShim.Replace(@"@anthropic-ai\claude-code\cli.js", @"@openai\codex\bin\codex.js"));
        Touch(nodeDirectory, "node.cmd"); // never a .cmd node

        Assert.Null(Shell(npm, nodeDirectory).Executable("codex"));

        var node = Touch(nodeDirectory, "node.exe");
        var executable = Shell(npm, nodeDirectory).Executable("codex");

        Assert.Equal(node, executable?.FileName);
        Assert.Equal([script], executable?.LeadingArguments);
    }

    [Fact]
    public void ShimWhoseScriptIsMissingOrFormatIsUnknownIsSkipped()
    {
        var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        Touch(first, "node.exe");
        Touch(second, "node.exe");
        File.WriteAllText(Path.Combine(first, "gemini.cmd"), NewShim(@"node_modules\gemini\missing.js"));
        File.WriteAllText(Path.Combine(second, "gemini.cmd"), "@echo off\r\ncmd /c \"%~dp0\\x.js\" %*\r\n");
        Touch(second, "x.js");

        Assert.Null(Shell(first, second).Executable("gemini"));
    }

    [Fact]
    public void ExeAnywhereOnPathWinsOverAnEarlierShimAndRelativeEntriesAreIgnored()
    {
        var npm = Directory.CreateDirectory(Path.Combine(root, "npm")).FullName;
        var local = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
        Touch(npm, "node.exe");
        Touch(npm, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), NewShim());
        var native = Touch(local, "claude.exe");

        var executable = Shell(npm, local).Executable("claude");

        Assert.Equal(native, executable?.FileName);
        Assert.Empty(executable!.LeadingArguments);
        Assert.Equal("node.exe", Path.GetFileName(Shell(npm, "local", Path.Combine(".", "local")).Executable("claude")?.FileName));
    }

    [Fact]
    public void OverrideTakesOnlyAnAbsoluteExeOrNpmShimAndNeverCmdExe()
    {
        var tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
        var native = Touch(tools, "claude.exe");
        Touch(tools, "cmd.exe");
        Touch(tools, "claude.bat");
        Touch(tools, "claude.ps1");
        Touch(tools, "claude");
        Touch(tools, "node.exe");
        Touch(tools, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        File.WriteAllText(Path.Combine(tools, "shim.cmd"), OldShim);
        var shell = Shell();

        Assert.Equal(native, shell.Executable("claude", $"  {native} ")?.FileName);
        Assert.Equal(Path.Combine(tools, "node.exe"), shell.Executable("claude", Path.Combine(tools, "shim.cmd"))?.FileName);
        foreach (var name in (string[])["cmd.exe", "claude.bat", "claude.ps1", "claude", "missing.exe"])
            Assert.Null(shell.Executable("claude", Path.Combine(tools, name)));
        Assert.Null(shell.Executable("claude", Path.Combine("tools", "claude.exe")));
    }

    [Theory]
    [InlineData("claude.cmd")]
    [InlineData("claude.bat")]
    [InlineData("cmd.exe")]
    [InlineData("CMD.EXE")]
    [InlineData("claude")]
    public void StartInfoRefusesAnythingButAnExe(string name)
    {
        var executable = new CLIExecutable(Path.Combine(root, name), [], Path.Combine(root, name));

        Assert.Throws<LLMException>(() => ShellEnvironment.StartInfo(executable, ["-p"], new Dictionary<string, string>(), root));
    }

    [Fact]
    public void StartInfoPassesArgumentsAsAListAndOnlyTheGivenEnvironment()
    {
        var executable = new CLIExecutable(Path.Combine(root, "node.exe"), [Path.Combine(root, "cli.js")], "claude.cmd");

        var info = ShellEnvironment.StartInfo(executable, ["-p", "a b \"c\"", ""], new Dictionary<string, string> { ["PATH"] = "p" }, root);

        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(Path.Combine(root, "node.exe"), info.FileName);
        Assert.Equal("", info.Arguments);
        Assert.Equal([Path.Combine(root, "cli.js"), "-p", "a b \"c\"", ""], info.ArgumentList);
        Assert.Equal(["PATH"], info.Environment.Keys);
        Assert.Equal(root, info.WorkingDirectory);
        Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
    }

    // MARK: - Stream parsing

    [Fact]
    public void ClaudeParsesTextDeltasOnly()
    {
        Assert.Equal(
            [CLIEvent.Skip, CLIEvent.Delta("Hel"), CLIEvent.Delta("lo"), CLIEvent.Skip, CLIEvent.Skip],
            Parse(CLIPreset.Claude,
                """{"type":"system","subtype":"init","session_id":"s"}""",
                """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}}""",
                """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello"}]}}""",
                """{"type":"result","subtype":"success","is_error":false,"result":"Hello"}"""));
        var error = Assert.Throws<LLMException>(() =>
            CLIRunner.Parse("""{"type":"result","subtype":"success","is_error":true,"result":"Not logged in"}""", CLIPreset.Claude));
        Assert.Equal("Not logged in", error.Message);
    }

    [Fact]
    public void CodexTakesAgentMessages()
    {
        Assert.Equal(
            [CLIEvent.Skip, CLIEvent.Skip, CLIEvent.Segment("Summary."), CLIEvent.Skip],
            Parse(CLIPreset.Codex,
                """{"type":"thread.started","thread_id":"t"}""",
                """{"type":"item.completed","item":{"id":"item_0","type":"reasoning","text":"thinking"}}""",
                """{"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"Summary."}}""",
                """{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":2}}"""));
        var error = Assert.Throws<LLMException>(() =>
            CLIRunner.Parse("""{"type":"turn.failed","error":{"message":"usage limit"}}""", CLIPreset.Codex));
        Assert.Equal("usage limit", error.Message);
    }

    [Fact]
    public void OpenCodeTakesTextParts()
    {
        Assert.Equal(
            [CLIEvent.Skip, CLIEvent.Segment("Summary."), CLIEvent.Skip],
            Parse(CLIPreset.OpenCode,
                """{"type":"step_start","part":{"type":"step-start"}}""",
                """{"type":"text","part":{"type":"text","text":"Summary."}}""",
                """{"type":"step_finish","part":{"type":"step-finish"}}"""));
        var error = Assert.Throws<LLMException>(() =>
            CLIRunner.Parse("""{"type":"error","error":{"name":"APIError","data":{"message":"model not found"}}}""", CLIPreset.OpenCode));
        Assert.Equal("model not found", error.Message);
    }

    [Fact]
    public void GeminiTakesAssistantDeltas()
    {
        Assert.Equal(
            [CLIEvent.Skip, CLIEvent.Skip, CLIEvent.Delta("Sum"), CLIEvent.Delta("mary."), CLIEvent.Skip, CLIEvent.Skip],
            Parse(CLIPreset.Gemini,
                """{"type":"init","session_id":"s","model":"m"}""",
                """{"type":"message","role":"user","content":"Alice [00:05]: the launch"}""",
                """{"type":"message","role":"assistant","content":"Sum","delta":true}""",
                """{"type":"message","role":"assistant","content":"mary.","delta":true}""",
                """{"type":"result","status":"success","stats":{}}""",
                "Loaded cached credentials."));
        var error = Assert.Throws<LLMException>(() =>
            CLIRunner.Parse("""{"type":"result","status":"error","error":{"message":"quota"}}""", CLIPreset.Gemini));
        Assert.Equal("quota", error.Message);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void LinesThatAreNotTheExpectedJsonAreSkipped(CLIPreset preset)
    {
        Assert.All(
            Parse(preset, "", "null", "[]", "42", "{}", """{"type":null}""", """{"type":7}""", "{\"type\":\"text\"", "Warning: x"),
            parsed => Assert.Equal(CLIEvent.Skip, parsed));
    }

    [Fact]
    public void StderrMessageDropsWarningsAndStackFrames()
    {
        const string stderr =
            "Warning: 256-color support not detected. Using a terminal with at least 256-color support is recommended.\n"
            + "Error authenticating: IneligibleTierError: This client is no longer supported.\n"
            + "    at throwIneligibleOrProjectIdError (file:///gemini/bundle/chunk.js:307474:11)\n"
            + "\n";

        Assert.Equal("Error authenticating: IneligibleTierError: This client is no longer supported.", CLIRunner.StderrMessage(stderr));
        Assert.Equal("a\nb", CLIRunner.StderrMessage("\ta \r\n\r\n  at x\r\nb\u2028"));
    }

    [Fact]
    public void TimeoutIsWrittenInMinutesAndSeconds()
    {
        Assert.Equal("10 minutes", CLIRunner.DurationText(CLIRunner.DefaultTimeout));
        Assert.Equal("1 minute, 30 seconds", CLIRunner.DurationText(TimeSpan.FromSeconds(90)));
        Assert.Equal("1 second", CLIRunner.DurationText(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ModelsAndContextWindowsPerCli()
    {
        Assert.Equal(["sonnet", "opus", "haiku"], await new CLIRunner(CLIPreset.Claude).ListModelsAsync(Token));
        Assert.Empty(await new CLIRunner(CLIPreset.Codex).ListModelsAsync(Token));
        Assert.Equal(1_000_000, await new CLIRunner(CLIPreset.Gemini).ContextWindowAsync("", Token));
        Assert.Equal(["claude-cli", "codex-cli", "opencode-cli", "gemini-cli"], CLIPresets.All.Select(preset => preset.Slug()));
        Assert.Equal(CLIPreset.OpenCode, CLIPresets.FromSlug("opencode-cli"));
        Assert.Null(CLIPresets.FromSlug("openai"));
    }

    // MARK: - Process (a fake CLI script; Mac only: Windows cannot run a script named .exe)

    [Fact]
    public async Task RunFeedsStdinInAFreshWorkDirectoryWithTheAllowlistOnly()
    {
        SkipOnWindows();
        Process? started = null;
        var runner = FakeCli(
            """
            IFS= read -r first
            printf '{"type":"stream_event","event":{"delta":{"type":"text_delta","text":"%s|"}}}\n' "$first"
            printf '{"type":"stream_event","event":{"delta":{"type":"text_delta","text":"%s|%s|%s"}}}\n' "$HOME" "${USER-unset}" "$PWD"
            """,
            onStarted: process => started = process);

        var text = string.Concat(await Collect(runner.StreamAsync(Messages, "", 0, Token)));

        var parts = text.Split('|');
        Assert.Equal("Summarize the meeting.", parts[0]);
        Assert.Equal(Path.Combine(root, "home"), parts[1]);
        Assert.Equal("unset", parts[2]);
        Assert.StartsWith("EchoDiar-cli-", Path.GetFileName(parts[3]));
        Assert.False(Directory.Exists(parts[3]));
        Assert.NotNull(started);
    }

    [Fact]
    public async Task NonZeroExitKeepsTheStderrCause()
    {
        SkipOnWindows();
        var runner = FakeCli(
            """
            printf 'Warning: 256-color support not detected.\n' >&2
            printf 'Error authenticating: IneligibleTierError\n' >&2
            printf '    at frame (file:///x.js:1:1)\n' >&2
            exit 3
            """);

        var error = await Assert.ThrowsAsync<LLMException>(() => Collect(runner.StreamAsync(Messages, "", 0, Token)));
        Assert.Equal("Error authenticating: IneligibleTierError", error.Message);

        var silent = await Assert.ThrowsAsync<LLMException>(() => Collect(FakeCli("exit 3").StreamAsync(Messages, "", 0, Token)));
        Assert.Equal("claude exited with status 3.", silent.Message);
    }

    [Fact]
    public async Task InStreamErrorFailsAndSegmentsGiveTheLastOne()
    {
        SkipOnWindows();
        var codex = FakeCli(
            """
            printf '{"type":"item.completed","item":{"type":"agent_message","text":"draft"}}\n'
            printf '{"type":"item.completed","item":{"type":"agent_message","text":"final"}}'
            """, CLIPreset.Codex);
        Assert.Equal(["final"], await Collect(codex.StreamAsync(Messages, "", 0, Token)));

        var failing = FakeCli("""printf '{"type":"result","is_error":true,"result":"Not logged in"}\n'""");
        var error = await Assert.ThrowsAsync<LLMException>(() => Collect(failing.StreamAsync(Messages, "", 0, Token)));
        Assert.Equal("Not logged in", error.Message);
    }

    [Fact]
    public async Task TimeoutKillsTheProcessTree()
    {
        SkipOnWindows();
        var pid = 0;
        var runner = FakeCli("exec /bin/sleep 600", timeout: TimeSpan.FromSeconds(1), onStarted: process => pid = process.Id);

        var error = await Assert.ThrowsAsync<LLMException>(() => Collect(runner.StreamAsync(Messages, "", 0, Token)));

        Assert.Equal("claude did not finish within 1 second.", error.Message);
        Assert.False(IsRunning(pid));
    }

    [Fact]
    public async Task LeavingTheStreamStopsTheProcess()
    {
        SkipOnWindows();
        var pid = 0;
        var runner = FakeCli(
            """
            printf '{"type":"stream_event","event":{"delta":{"type":"text_delta","text":"first"}}}\n'
            exec /bin/sleep 600
            """, onStarted: process => pid = process.Id);

        await foreach (var text in runner.StreamAsync(Messages, "", 0, Token))
        {
            Assert.Equal("first", text);
            break;
        }

        for (var wait = 0; wait < 100 && IsRunning(pid); wait++) await Task.Delay(100, Token);
        Assert.False(IsRunning(pid));
    }

    [Fact]
    public async Task MissingCliIsReported()
    {
        var error = await Assert.ThrowsAsync<LLMException>(() =>
            Collect(new CLIRunner(CLIPreset.Gemini, null, null, null, Shell()).StreamAsync(Messages, "", 0, Token)));
        Assert.Equal("gemini was not found. Install it or set its path in Settings.", error.Message);
    }

    // MARK: - Helpers

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ShellEnvironment Shell(params string[] path)
    {
        var home = Path.Combine(root, "home");
        return new(() => string.Join(';', path), name => name == "USERPROFILE" ? home : null);
    }

    private CLIRunner FakeCli(
        string body, CLIPreset preset = CLIPreset.Claude, TimeSpan? timeout = null, Action<Process>? onStarted = null)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin-" + Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(bin, preset.ExecutableName() + ".exe");
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n") + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        return new CLIRunner(preset, null, timeout, onStarted, Shell(bin));
    }

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("The fake CLI is a shell script; Windows starts only real .exe files.");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> stream)
    {
        var texts = new List<string>();
        await foreach (var text in stream) texts.Add(text);
        return texts;
    }

    private static string Touch(string directory, params string[] parts)
    {
        var path = Path.Combine([directory, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private static List<CLIEvent> Parse(CLIPreset preset, params string[] lines) =>
        lines.Select(line => CLIRunner.Parse(line, preset)).ToList();

    private static bool Adjacent(IReadOnlyList<string> argv, string flag, string value) =>
        argv.Zip(argv.Skip(1)).Any(pair => pair.First == flag && pair.Second == value);
}
