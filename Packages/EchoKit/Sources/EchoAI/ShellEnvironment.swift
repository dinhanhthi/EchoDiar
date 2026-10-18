import Foundation
import os

/// The user's PATH for spawning subscription CLIs. A GUI app only gets launchd's minimal PATH, so `~/.local/bin`
/// tools and npm launchers (`#!/usr/bin/env node`) would not be found. Resolved once, on first use.
public actor ShellEnvironment {
    public static let shared = ShellEnvironment()

    static let home = NSHomeDirectory()
    private var resolved: Task<String, Never>?

    /// The login shell's PATH, then the known install directories, deduplicated.
    public func path() async -> String {
        if let resolved { return await resolved.value }
        let task = Task.detached(priority: .utility) { await Self.resolvePath() }
        resolved = task
        return await task.value
    }

    /// `override` (a path from settings, `~` allowed) when set, else the first `name` on `path()`.
    public func executable(named name: String, override: String? = nil) async -> URL? {
        if let override = override?.trimmingCharacters(in: .whitespaces), !override.isEmpty {
            let path = (override as NSString).expandingTildeInPath
            return Self.isExecutable(path) ? URL(filePath: path) : nil
        }
        for directory in await path().split(separator: ":") {
            let candidate = URL(filePath: String(directory)).appending(path: name)
            if Self.isExecutable(candidate.path) { return candidate }
        }
        return nil
    }

    /// First line of `<executable> --version`, nil if it fails or takes more than 5 s.
    public func version(of executable: URL) async -> String? {
        let environment = ["PATH": await path(), "HOME": Self.home]
        let output = await Self.capture(executable, arguments: ["--version"], environment: environment, timeout: .seconds(5))
        let line = String(decoding: output, as: UTF8.self).split(whereSeparator: \.isNewline).first
        return line.map { $0.trimmingCharacters(in: .whitespaces) }
    }

    private static func resolvePath() async -> String {
        let output = await capture(
            URL(filePath: loginShell()), arguments: ["-ilc", "echo __BEGIN__; command env; echo __END__"],
            environment: nil, timeout: .seconds(8), until: Data("\n__END__\n".utf8))
        let base = parsePath(String(decoding: output, as: UTF8.self))
            ?? ProcessInfo.processInfo.environment["PATH"] ?? "/usr/bin:/bin:/usr/sbin:/sbin"
        return mergedPath(base, extra: knownDirectories(home: home))
    }

    /// The `PATH=` line between the markers of the login shell's output (rc files may print around them).
    static func parsePath(_ output: String) -> String? {
        guard let begin = output.range(of: "__BEGIN__\n") else { return nil }
        for line in output[begin.upperBound...].split(separator: "\n") {
            if line == "__END__" { break }
            if line.hasPrefix("PATH=") {
                let path = line.dropFirst(5).replacing(/\u{1B}\[[0-9;?]*[A-Za-z]/, with: "")
                return path.trimmingCharacters(in: .whitespacesAndNewlines)
            }
        }
        return nil
    }

    static func knownDirectories(home: String) -> [String] {
        var directories = ["\(home)/.local/bin", "/opt/homebrew/bin", "/usr/local/bin", "\(home)/.npm-global/bin"]
        if let nvm = nvmBin(home: home) { directories.append(nvm) }
        return directories + ["\(home)/.opencode/bin", "\(home)/.bun/bin"]
    }

    /// The installed nvm version matching `~/.nvm/alias/default` (e.g. `22` or `v22.11.0`), else the newest one.
    static func nvmBin(home: String) -> String? {
        let versions = "\(home)/.nvm/versions/node"
        guard let installed = try? FileManager.default.contentsOfDirectory(atPath: versions) else { return nil }
        let alias = (try? String(contentsOfFile: "\(home)/.nvm/alias/default", encoding: .utf8))?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let wanted = alias.hasPrefix("v") ? alias : "v" + alias
        let newest = { (names: [String]) in names.max { $0.compare($1, options: .numeric) == .orderedAscending } }
        let version = newest(installed.filter { $0 == wanted || $0.hasPrefix(wanted + ".") })
            ?? newest(installed.filter { $0.hasPrefix("v") })
        return version.map { "\(versions)/\($0)/bin" }
    }

    static func mergedPath(_ base: String, extra: [String]) -> String {
        var seen = Set<String>()
        let directories = base.split(separator: ":").map(String.init) + extra
        return directories.filter { !$0.isEmpty && seen.insert($0).inserted }.joined(separator: ":")
    }

    private static func loginShell() -> String {
        if let shell = ProcessInfo.processInfo.environment["SHELL"], !shell.isEmpty { return shell }
        if let entry = getpwuid(getuid()), let shell = entry.pointee.pw_shell { return String(cString: shell) }
        return "/bin/zsh"
    }

    private static func isExecutable(_ path: String) -> Bool {
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory) && !isDirectory.boolValue
            && FileManager.default.isExecutableFile(atPath: path)
    }

    /// Stdout of `executable` (empty stdin) up to `marker` or EOF, for at most `timeout`; the process is
    /// terminated if still running. Reading stops at the marker because a daemon started by an rc file can keep
    /// the pipe open after the shell exits.
    static func capture(
        _ executable: URL, arguments: [String], environment: [String: String]?, timeout: Duration, until marker: Data? = nil
    ) async -> Data {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        if let environment { process.environment = environment }
        process.standardInput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        let stdout = Pipe()
        process.standardOutput = stdout
        let output = OSAllocatedUnfairLock(initialState: Data())
        let (finished, finish) = AsyncStream<Void>.makeStream()
        stdout.fileHandleForReading.readabilityHandler = { handle in
            let chunk = handle.availableData
            let complete = chunk.isEmpty || output.withLock { data in
                data.append(chunk)
                return marker.map { data.range(of: $0) != nil } ?? false
            }
            if complete { finish.finish() }
        }
        defer { stdout.fileHandleForReading.readabilityHandler = nil }
        do { try process.run() } catch { return Data() }
        let timer = Task {
            try await Task.sleep(for: timeout)
            finish.finish()
        }
        for await _ in finished {}
        timer.cancel()
        if process.isRunning { process.terminate() }
        return output.withLock { $0 }
    }
}
