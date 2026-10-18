import Foundation

/// Reads and writes `settings.json`. Only immutable state; every file access runs on one serial queue.
public final class SettingsStore: Sendable {
    public let directory: URL
    private let queue = DispatchQueue(label: "dinh.thi.EchoDiar.SettingsStore")

    public init(
        directory: URL = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("EchoDiar", isDirectory: true)
    ) {
        self.directory = directory
    }

    public var fileURL: URL { directory.appendingPathComponent("settings.json") }

    /// The saved settings, or defaults when the file is missing. An unreadable file is moved aside to
    /// `settings.json.unreadable-<now>` (never overwritten), then defaults are returned.
    public func load(now: Date = Date()) -> AppSettings {
        queue.sync {
            let url = fileURL
            guard FileManager.default.fileExists(atPath: url.path) else { return AppSettings() }
            do {
                return try JSONDecoder().decode(AppSettings.self, from: Data(contentsOf: url))
            } catch {
                try? FileManager.default.moveItem(at: url, to: asideURL(now))
                return AppSettings()
            }
        }
    }

    /// Writes atomically, creating the folder.
    public func save(_ settings: AppSettings) throws {
        try queue.sync {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(settings)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try data.write(to: fileURL, options: .atomic)
        }
    }

    /// `settings.json.unreadable-<yyyyMMdd-HHmmss-SSS, local time>`, with `-1`, `-2`, ... when taken.
    private func asideURL(_ date: Date) -> URL {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss-SSS"
        let base = "settings.json.unreadable-\(formatter.string(from: date))"
        var url = directory.appendingPathComponent(base)
        var n = 0
        while FileManager.default.fileExists(atPath: url.path) {
            n += 1
            url = directory.appendingPathComponent("\(base)-\(n)")
        }
        return url
    }
}
