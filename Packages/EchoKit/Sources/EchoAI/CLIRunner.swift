import EchoCore
import Foundation
import os

/// A subscription CLI; the raw value is its `ProviderConfig` preset slug.
public enum CLIPreset: String, CaseIterable, Sendable {
    case claude = "claude-cli"
    case codex = "codex-cli"
    case opencode = "opencode-cli"
    case gemini = "gemini-cli"

    public var executableName: String {
        switch self {
        case .claude: "claude"
        case .codex: "codex"
        case .opencode: "opencode"
        case .gemini: "gemini"
        }
    }
}

/// What one stdout line of a CLI means.
public enum CLIEvent: Equatable, Sendable {
    /// A piece of the answer, streamed as it comes.
    case delta(String)
    /// A whole answer segment; the last one is the answer (Codex agent messages, OpenCode text parts).
    case segment(String)
    case skip
}

/// Runs a user-installed subscription CLI as a text-only subprocess (docs/research/llm-providers.md C): no tools,
/// no edits, no MCP, cwd = a fresh empty temp dir, env = PATH + HOME + USER only. The prompt and transcript always go to
/// stdin (argv is visible in `ps` and capped by ARG_MAX); argv carries only the fixed `instructions`. What a CLI
/// can still do despite its flags is in `containmentNote(for:)`.
public struct CLIRunner: LLMProvider {
    public static let defaultTimeout: Duration = .seconds(600)
    /// The only prompt text in argv.
    public static let instructions = "Follow the instructions in the input. Reply with the answer text only; do not use tools or read or edit files."
    static let stderrLimit = 4_096

    public let preset: CLIPreset
    private let executableOverride: String?
    private let timeout: Duration

    /// `executableOverride`: the path set in settings, nil to search PATH.
    public init(preset: CLIPreset, executableOverride: String? = nil, timeout: Duration = defaultTimeout) {
        self.preset = preset
        self.executableOverride = executableOverride
        self.timeout = timeout
    }

    /// `maxTokens` is ignored: none of the CLIs can limit the answer length.
    public func stream(messages: [LLMMessage], model: String, maxTokens: Int) -> AsyncThrowingStream<String, Error> {
        let command = Self.command(for: preset, messages: messages, model: model)
        let (preset, override, timeout) = (preset, executableOverride, timeout)
        return AsyncThrowingStream { continuation in
            let control = ProcessControl()
            continuation.onTermination = { termination in
                if case .cancelled = termination { control.stop(.cancelled) }
            }
            Task {
                do {
                    try await Self.run(
                        preset: preset, command: command, override: override, timeout: timeout, control: control
                    ) { continuation.yield($0) }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
        }
    }

    /// Claude Code model aliases; the other CLIs use their own default when no model is set.
    public func listModels() async throws -> [String] {
        preset == .claude ? ["sonnet", "opus", "haiku"] : []
    }

    public func contextWindow(model: String) async -> Int {
        switch preset {
        case .claude: 200_000
        case .gemini: 1_000_000
        case .codex, .opencode: 128_000
        }
    }

    /// argv and stdin for one request. Pure, for tests.
    public static func command(for preset: CLIPreset, messages: [LLMMessage], model: String) -> (arguments: [String], input: String) {
        let prompt = stdinPrompt(messages)
        // Codex reads its whole prompt from stdin (`-`), so the instructions go there too.
        let input = preset == .codex ? instructions + "\n\n" + prompt : prompt
        return (arguments(for: preset, instructions: instructions, model: model), input)
    }

    /// The single source of truth for CLI flags. Never `--bare` (skips the subscription login), `--auto`, `--force`,
    /// `--yolo` or `--dangerously-*`. A model that could be read as a flag (leading `-`) or carries control characters
    /// is dropped, so the CLI uses its default.
    public static func arguments(for preset: CLIPreset, instructions: String, model: String) -> [String] {
        var model = model.trimmingCharacters(in: .whitespaces)
        if model.hasPrefix("-") || model.rangeOfCharacter(from: .controlCharacters.union(.newlines)) != nil { model = "" }
        switch preset {
        case .claude:
            return [
                "-p", instructions, "--tools", "", "--disallowedTools", "mcp__*", "--strict-mcp-config",
                "--disable-slash-commands", "--permission-mode", "dontAsk", "--no-session-persistence",
                "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            ] + (model.isEmpty ? [] : ["--model", model])
        case .codex:
            // No user config.toml (its MCP servers, hooks, notify, plugins), no execpolicy allow rules, escalations
            // rejected, no web search, no shell; the read-only sandbox also keeps the network off.
            return [
                "exec", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules",
                "--sandbox", "read-only", "-c", #"approval_policy="never""#, "-c", #"web_search="disabled""#,
                "--disable", "plugins", "--disable", "apps", "--disable", "shell_tool", "--disable", "unified_exec",
                "--json",
            ] + (model.isEmpty ? [] : ["-m", model]) + ["-"]
        case .opencode:
            // A private server: the shared background service would ignore this process's env (the deny config).
            return ["run", "--standalone", "--agent", "plan", "--format", "json"]
                + (model.isEmpty ? [] : ["-m", model]) + [instructions]
        case .gemini:
            // `geminiPolicyFile` (written to the cwd by `run`) denies every tool; no MCP server is on the allow list.
            return [
                "-p", instructions, "-o", "stream-json", "--approval-mode", "plan", "--skip-trust", "-e", "none",
                "--policy", geminiPolicyFile, "--allowed-mcp-server-names", "echodiar-none",
            ] + (model.isEmpty ? [] : ["-m", model])
        }
    }

    /// OpenCode config: every permission denied, globally and for the plan agent (the last matching rule wins).
    public static let openCodeConfig =
        #"{"permission":{"*":"deny","edit":"deny","bash":"deny","webfetch":"deny","websearch":"deny","task":"deny","external_directory":"deny"},"#
        + #""tools":{"*":false},"agent":{"plan":{"permission":{"*":"deny"}}}}"#

    /// Gemini CLI policy file name, relative to the CLI's cwd (the temp work dir).
    public static let geminiPolicyFile = "echodiar-deny-tools.toml"
    /// User-tier policy denying every tool, MCP tools included; only a system admin policy ranks above it.
    public static let geminiPolicy = """
        [[rule]]
        toolName = "*"
        decision = "deny"
        priority = 999
        denyMessage = "Tools are disabled. Reply with text only."

        """

    /// What a CLI can still do despite the flags above, for the settings UI; nil when fully contained.
    public static func containmentNote(for preset: CLIPreset) -> String? {
        switch preset {
        case .claude: nil
        case .codex:
            "Codex runs without your ~/.codex/config.toml (its model, MCP servers and hooks) in a read-only, offline sandbox, but its remaining built-in tools can still read files on this Mac."
        case .opencode:
            "OpenCode denies every tool, but MCP servers in your OpenCode config still start (their tools are denied)."
        case .gemini:
            "Gemini CLI denies every tool and loads no MCP server or extension, unless a system-wide admin policy allows them."
        }
    }

    /// The child's whole environment: PATH + HOME + USER (+ OpenCode's deny-all config). Nothing else from the app.
    /// Claude Code finds its login in the Keychain under `USER`; without it, it answers "Not logged in".
    public static func environment(for preset: CLIPreset, path: String, home: String, user: String) -> [String: String] {
        var environment = ["PATH": path, "HOME": home, "USER": user]
        if preset == .opencode {
            environment["OPENCODE_CONFIG_CONTENT"] = openCodeConfig
        }
        return environment
    }

    /// System messages first, then the conversation; turns are labelled only when there is an assistant turn.
    static func stdinPrompt(_ messages: [LLMMessage]) -> String {
        let system = messages.filter { $0.role == .system }.map(\.content)
        let turns = messages.filter { $0.role != .system }
        let labelled = turns.contains { $0.role == .assistant }
        let conversation = turns.map { labelled ? "\($0.role == .user ? "User" : "Assistant"): \($0.content)" : $0.content }
        return (system + conversation).joined(separator: "\n\n")
    }

    /// One stdout line (JSONL) of `preset`. Lines that are not the expected JSON are skipped. Pure, for tests.
    public static func parse(_ line: String, preset: CLIPreset) throws -> CLIEvent {
        let data = Data(line.utf8)
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        switch preset {
        case .claude:
            guard let event = try? decoder.decode(ClaudeLine.self, from: data) else { return .skip }
            if event.type == "stream_event", event.event?.delta?.type == "text_delta", let text = event.event?.delta?.text {
                return .delta(text)
            }
            if event.type == "result", event.isError == true {
                throw LLMError.provider(event.result ?? event.subtype ?? "Claude Code failed.")
            }
        case .codex:
            guard let event = try? decoder.decode(CodexLine.self, from: data) else { return .skip }
            switch event.type {
            case "item.completed" where event.item?.type == "agent_message":
                if let text = event.item?.text { return .segment(text) }
            case "turn.failed": throw LLMError.provider(event.error?.message ?? "Codex failed.")
            case "error": throw LLMError.provider(event.message ?? "Codex failed.")
            default: break
            }
        case .opencode:
            guard let event = try? decoder.decode(OpenCodeLine.self, from: data) else { return .skip }
            switch event.type {
            case "text": if let text = event.part?.text { return .segment(text) }
            case "error":
                throw LLMError.provider(event.error?.data?.message ?? event.error?.message ?? event.error?.name ?? "OpenCode failed.")
            default: break
            }
        case .gemini:
            guard let event = try? decoder.decode(GeminiLine.self, from: data) else { return .skip }
            switch event.type {
            case "message" where event.role == "assistant" && event.delta == true:
                if let content = event.content { return .delta(content) }
            case "error": throw LLMError.provider(event.message ?? "Gemini CLI failed.")
            case "result" where event.status == "error":
                throw LLMError.provider(event.error?.message ?? "Gemini CLI failed.")
            default: break
            }
        }
        return .skip
    }

    // MARK: - Process

    private static func run(
        preset: CLIPreset, command: (arguments: [String], input: String), override: String?, timeout: Duration,
        control: ProcessControl, yield: @Sendable (String) -> Void
    ) async throws {
        let shell = ShellEnvironment.shared
        guard let executable = await shell.executable(named: preset.executableName, override: override) else {
            throw LLMError.provider("\(preset.executableName) was not found. Install it or set its path in Settings.")
        }
        let fileManager = FileManager.default
        let workDirectory = fileManager.temporaryDirectory.appending(path: "EchoDiar-cli-\(UUID().uuidString)", directoryHint: .isDirectory)
        try fileManager.createDirectory(at: workDirectory, withIntermediateDirectories: true)
        defer { try? fileManager.removeItem(at: workDirectory) }
        if preset == .gemini {
            try Data(geminiPolicy.utf8).write(to: workDirectory.appending(path: geminiPolicyFile))
        }

        let process = Process()
        process.executableURL = executable
        process.arguments = command.arguments
        process.environment = environment(for: preset, path: await shell.path(), home: ShellEnvironment.home, user: NSUserName())
        process.currentDirectoryURL = workDirectory
        let (stdin, stdout, stderr) = (Pipe(), Pipe(), Pipe())
        process.standardInput = stdin
        process.standardOutput = stdout
        process.standardError = stderr

        let (chunks, chunkSink) = AsyncStream<Data>.makeStream()
        stdout.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if data.isEmpty {
                handle.readabilityHandler = nil
                chunkSink.finish()
            } else {
                chunkSink.yield(data)
            }
        }
        let errorTail = OSAllocatedUnfairLock(initialState: Data())
        stderr.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            guard !data.isEmpty else {
                handle.readabilityHandler = nil
                return
            }
            errorTail.withLock { $0 = Data(($0 + data).suffix(stderrLimit)) }
        }
        let (exit, exitSink) = AsyncStream<Int32>.makeStream()
        process.terminationHandler = { process in
            exitSink.yield(process.terminationStatus)
            exitSink.finish()
            // A grandchild that inherited stdout would keep it open: stop reading shortly after the exit.
            DispatchQueue.global().asyncAfter(deadline: .now() + 2) { chunkSink.finish() }
        }
        defer {
            stdout.fileHandleForReading.readabilityHandler = nil
            stderr.fileHandleForReading.readabilityHandler = nil
        }

        do {
            try process.run()
        } catch {
            throw LLMError.provider("Could not start \(executable.path): \(error.localizedDescription)")
        }
        control.attach(process)
        write(command.input, to: stdin.fileHandleForWriting)
        let timer = Task {
            try await Task.sleep(for: timeout)
            control.stop(.timedOut)
        }
        defer { timer.cancel() }

        // Split on LF only (U+2028/U+2029 may appear unescaped inside JSON strings).
        var pending = Data()
        var answer: String?
        var failure: Error?
        func consume(_ line: Data) {
            guard failure == nil, !line.isEmpty else { return }
            do {
                switch try parse(String(decoding: line, as: UTF8.self), preset: preset) {
                case .delta(let text): yield(text)
                case .segment(let text): answer = text
                case .skip: break
                }
            } catch {
                failure = error
                control.stop(.failed)
            }
        }
        for await chunk in chunks {
            pending.append(chunk)
            while let newline = pending.firstIndex(of: UInt8(ascii: "\n")) {
                consume(pending[pending.startIndex..<newline])
                pending.removeSubrange(pending.startIndex...newline)
            }
        }
        consume(pending)
        var status: Int32 = -1
        for await code in exit { status = code }

        switch control.reason {
        case .cancelled: throw CancellationError()
        case .timedOut:
            throw LLMError.provider("\(preset.executableName) did not finish within \(timeout.formatted(.units(allowed: [.minutes, .seconds], width: .wide))).")
        case .failed, nil: break
        }
        if let failure { throw failure }
        guard status == 0 else {
            let message = stderrMessage(String(decoding: errorTail.withLock { $0 }, as: UTF8.self))
            throw LLMError.provider(message.isEmpty ? "\(preset.executableName) exited with status \(status)." : message)
        }
        if let answer { yield(answer) }
    }

    /// stderr without warnings and stack frames, so the cause comes first (Gemini CLI prints a 256-color warning
    /// before its error). Pure, for tests.
    public static func stderrMessage(_ stderr: String) -> String {
        stderr.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && !$0.hasPrefix("Warning:") && !$0.hasPrefix("at ") }
            .joined(separator: "\n")
    }

    /// Writes stdin off the reading path (a long transcript exceeds the pipe buffer), then closes it.
    private static func write(_ input: String, to handle: FileHandle) {
        // EPIPE instead of SIGPIPE (which would kill the app) if the CLI exits before reading everything.
        _ = fcntl(handle.fileDescriptor, F_SETNOSIGPIPE, 1)
        DispatchQueue.global(qos: .utility).async {
            try? handle.write(contentsOf: Data(input.utf8))
            try? handle.close()
        }
    }
}

/// Stops a CLI process at most once (SIGINT, then SIGTERM after 3 s) and remembers why.
private final class ProcessControl: Sendable {
    enum Reason { case cancelled, timedOut, failed }

    private let state = OSAllocatedUnfairLock<(process: Process?, reason: Reason?)>(initialState: (nil, nil))

    var reason: Reason? { state.withLock { $0.reason } }

    /// Stops `process` right away when a stop came before it started.
    func attach(_ process: Process) {
        let stopped = state.withLock { state in
            state.process = process
            return state.reason != nil
        }
        if stopped { Self.interrupt(process) }
    }

    func stop(_ reason: Reason) {
        let process: Process? = state.withLock { state in
            guard state.reason == nil else { return nil }
            state.reason = reason
            return state.process
        }
        if let process { Self.interrupt(process) }
    }

    private static func interrupt(_ process: Process) {
        guard process.isRunning else { return }
        process.interrupt()
        DispatchQueue.global().asyncAfter(deadline: .now() + 3) {
            if process.isRunning { process.terminate() }
        }
    }
}

private struct ClaudeLine: Decodable {
    struct Event: Decodable {
        struct Delta: Decodable {
            var type: String?
            var text: String?
        }

        var delta: Delta?
    }

    var type: String
    var event: Event?
    var isError: Bool?
    var result: String?
    var subtype: String?
}

private struct CodexLine: Decodable {
    struct Item: Decodable {
        var type: String?
        var text: String?
    }

    var type: String
    var item: Item?
    /// `turn.failed`.
    var error: APIErrorBody?
    /// `error`.
    var message: String?
}

private struct OpenCodeLine: Decodable {
    struct Part: Decodable { var text: String? }
    struct Failure: Decodable {
        struct Details: Decodable { var message: String? }
        var name: String?
        var message: String?
        var data: Details?
    }

    var type: String
    var part: Part?
    var error: Failure?
}

private struct GeminiLine: Decodable {
    var type: String
    var role: String?
    var content: String?
    var delta: Bool?
    var message: String?
    var status: String?
    var error: APIErrorBody?
}
