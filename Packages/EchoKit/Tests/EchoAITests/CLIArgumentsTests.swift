import EchoAI
import Foundation
import Testing

/// Guards the CLI security contract (read-only, no tools, transcript only on stdin) and the stream formats the
/// parsers depend on. No CLI is run.
struct CLIArgumentsTests {
    private let transcript = "Alice [00:05]: the launch moves to Friday"
    private var messages: [LLMMessage] {
        [LLMMessage(role: .system, content: "Summarize the meeting."), LLMMessage(role: .user, content: transcript)]
    }

    @Test(arguments: CLIPreset.allCases)
    func argvIsReadOnlyAndKeepsTranscriptOnStdin(preset: CLIPreset) {
        let command = CLIRunner.command(for: preset, messages: messages, model: "some-model")
        let argv = command.arguments

        #expect(!argv.contains { $0.contains("launch") || $0.contains("Summarize") })
        #expect(command.input.contains(transcript))
        #expect(!argv.contains { ["--bare", "--auto", "--force", "--yolo"].contains($0) || $0.hasPrefix("--dangerously") })
        switch preset {
        case .claude:
            #expect(adjacent(argv, "--tools", ""))
            #expect(adjacent(argv, "--permission-mode", "dontAsk"))
            #expect(adjacent(argv, "--output-format", "stream-json"))
            #expect(argv.contains("--include-partial-messages"))
        case .codex:
            #expect(adjacent(argv, "--sandbox", "read-only"))
            #expect(argv.contains("--ignore-user-config"))
            #expect(argv.contains("--ignore-rules"))
            #expect(adjacent(argv, "-c", #"approval_policy="never""#))
            #expect(adjacent(argv, "-c", #"web_search="disabled""#))
            for feature in ["plugins", "apps", "shell_tool", "unified_exec"] {
                #expect(adjacent(argv, "--disable", feature))
            }
            #expect(argv.contains("--json"))
            #expect(argv.last == "-")
        case .opencode:
            #expect(argv.contains("--standalone"))
            #expect(adjacent(argv, "--agent", "plan"))
            #expect(adjacent(argv, "--format", "json"))
        case .gemini:
            #expect(adjacent(argv, "--approval-mode", "plan"))
            #expect(argv.contains("--skip-trust"))
            #expect(adjacent(argv, "-o", "stream-json"))
            #expect(adjacent(argv, "-e", "none"))
            #expect(adjacent(argv, "--policy", CLIRunner.geminiPolicyFile))
            #expect(adjacent(argv, "--allowed-mcp-server-names", "echodiar-none"))
        }
        #expect((CLIRunner.containmentNote(for: preset) == nil) == (preset == .claude))
    }

    @Test(arguments: CLIPreset.allCases, ["--yolo", "m\n--yolo", "-", " --dangerously-skip-permissions", "m\u{1B}[2J", "m\u{2028}--yolo"])
    func modelThatCouldBeAFlagIsDropped(preset: CLIPreset, model: String) {
        let argv = CLIRunner.arguments(for: preset, instructions: "i", model: model)

        #expect(!argv.contains { $0.contains("yolo") || $0.contains("dangerously") || $0.contains("\u{1B}") })
        #expect(!argv.contains("--model") && !argv.contains("-m"))
        #expect(!argv.contains("-") || preset == .codex)
    }

    @Test(arguments: CLIPreset.allCases)
    func validModelIsPassed(preset: CLIPreset) {
        let argv = CLIRunner.arguments(for: preset, instructions: "i", model: " gpt-5.1-codex ")

        #expect(adjacent(argv, "--model", "gpt-5.1-codex") || adjacent(argv, "-m", "gpt-5.1-codex"))
    }

    @Test func geminiPolicyDeniesEveryToolAtTopUserPriority() {
        let lines = CLIRunner.geminiPolicy.split(separator: "\n").map(String.init)

        #expect(CLIRunner.geminiPolicyFile.hasSuffix(".toml") && !CLIRunner.geminiPolicyFile.contains("/"))
        #expect(lines.contains("[[rule]]"))
        #expect(lines.contains(#"toolName = "*""#))
        #expect(lines.contains(#"decision = "deny""#))
        #expect(lines.contains("priority = 999"))
        #expect(!lines.contains { $0.hasPrefix("modes") || $0.hasPrefix("mcpName") || $0.hasPrefix("interactive") })
    }

    @Test(arguments: CLIPreset.allCases)
    func environmentIsPathAndHomeOnlyWithOpenCodeDenyAllConfig(preset: CLIPreset) throws {
        let environment = CLIRunner.environment(for: preset, path: "/usr/bin", home: "/Users/x")

        #expect(environment["PATH"] == "/usr/bin")
        #expect(environment["HOME"] == "/Users/x")
        guard preset == .opencode else {
            #expect(environment.count == 2)
            return
        }
        let config = try #require(environment["OPENCODE_CONFIG_CONTENT"])
        let json = try #require(JSONSerialization.jsonObject(with: Data(config.utf8)) as? [String: Any])
        #expect(json["permission"] as? [String: String] == [
            "*": "deny", "edit": "deny", "bash": "deny", "webfetch": "deny", "websearch": "deny", "task": "deny",
            "external_directory": "deny",
        ])
        #expect(json["tools"] as? [String: Bool] == ["*": false])
        let plan = (json["agent"] as? [String: Any])?["plan"] as? [String: Any]
        #expect(plan?["permission"] as? [String: String] == ["*": "deny"])
    }

    @Test func claudeParsesTextDeltasOnly() throws {
        #expect(try parse(.claude, [
            #"{"type":"system","subtype":"init","session_id":"s"}"#,
            #"{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}}"#,
            #"{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}}"#,
            #"{"type":"assistant","message":{"content":[{"type":"text","text":"Hello"}]}}"#,
            #"{"type":"result","subtype":"success","is_error":false,"result":"Hello"}"#,
        ]) == [.skip, .delta("Hel"), .delta("lo"), .skip, .skip])
        #expect(throws: LLMError.self) {
            try CLIRunner.parse(#"{"type":"result","subtype":"success","is_error":true,"result":"Not logged in"}"#, preset: .claude)
        }
    }

    @Test func codexTakesAgentMessages() throws {
        #expect(try parse(.codex, [
            #"{"type":"thread.started","thread_id":"t"}"#,
            #"{"type":"item.completed","item":{"id":"item_0","type":"reasoning","text":"thinking"}}"#,
            #"{"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"Summary."}}"#,
            #"{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":2}}"#,
        ]) == [.skip, .skip, .segment("Summary."), .skip])
        #expect(throws: LLMError.self) {
            try CLIRunner.parse(#"{"type":"turn.failed","error":{"message":"usage limit"}}"#, preset: .codex)
        }
    }

    @Test func openCodeTakesTextParts() throws {
        #expect(try parse(.opencode, [
            #"{"type":"step_start","part":{"type":"step-start"}}"#,
            #"{"type":"text","part":{"type":"text","text":"Summary."}}"#,
            #"{"type":"step_finish","part":{"type":"step-finish"}}"#,
        ]) == [.skip, .segment("Summary."), .skip])
    }

    @Test func geminiTakesAssistantDeltas() throws {
        #expect(try parse(.gemini, [
            #"{"type":"init","session_id":"s","model":"m"}"#,
            #"{"type":"message","role":"user","content":"Alice [00:05]: the launch"}"#,
            #"{"type":"message","role":"assistant","content":"Sum","delta":true}"#,
            #"{"type":"message","role":"assistant","content":"mary.","delta":true}"#,
            #"{"type":"result","status":"success","stats":{}}"#,
            "Loaded cached credentials.",
        ]) == [.skip, .skip, .delta("Sum"), .delta("mary."), .skip, .skip])
    }

    private func parse(_ preset: CLIPreset, _ lines: [String]) throws -> [CLIEvent] {
        try lines.map { try CLIRunner.parse($0, preset: preset) }
    }

    private func adjacent(_ argv: [String], _ flag: String, _ value: String) -> Bool {
        zip(argv, argv.dropFirst()).contains { $0 == flag && $1 == value }
    }
}
