import CryptoKit
import EchoCore
import FluidAudio
import Foundation
import os

public struct ModelDownloadProgress: Sendable {
    public var completedBytes: Int64
    public var totalBytes: Int64
    /// Repo path of the file being fetched.
    public var file: String

    public var fraction: Double {
        totalBytes > 0 ? min(1, Double(completedBytes) / Double(totalBytes)) : 0
    }
}

public enum ModelStoreError: Error, LocalizedError {
    case unknownModel(String)
    case notDownloadable(String)
    case unsupportedLayout(String)
    case allowlistMissesRequired(String, [String])
    case unsafePath(String)
    case listingFailed(String)
    case noFiles(String)
    case missingTokenizerFile(String)
    case httpStatus(Int, String)
    case sizeMismatch(String)
    case checksumMismatch(String)
    case insufficientSpace(needed: Int64, available: Int64)

    public var errorDescription: String? {
        switch self {
        case .unknownModel(let id): "\(id) is not in the model catalog."
        case .notDownloadable(let id): "\(id) is a system model and cannot be downloaded."
        case .unsupportedLayout(let id): "\(id) has no known folder layout."
        case .allowlistMissesRequired(let id, let names):
            "The file list of \(id) misses files FluidAudio requires: \(names.joined(separator: ", "))."
        case .unsafePath(let path): "Refusing unsafe model path \(path)."
        case .listingFailed(let detail): "Could not list the model files (\(detail))."
        case .noFiles(let id): "No files to download for \(id)."
        case .missingTokenizerFile(let name): "The tokenizer repo has no \(name)."
        case .httpStatus(let code, let path): "HTTP \(code) while downloading \(path)."
        case .sizeMismatch(let path): "Size mismatch for \(path)."
        case .checksumMismatch(let path): "SHA-256 mismatch for \(path)."
        case .insufficientSpace(let needed, let available):
            "Not enough disk space: \(needed / 1_048_576) MiB needed, \(available / 1_048_576) MiB available."
        }
    }
}

/// Opt-in downloads of catalog models into `Models/<id>/`, in the folder layout each loader expects.
/// Only catalog repos at their pinned revisions are fetched; nothing else in the app downloads models.
public final class ModelStore: Sendable {
    public static var defaultRoot: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("EchoDiar", isDirectory: true)
            .appendingPathComponent("Models", isDirectory: true)
    }

    public let root: URL
    /// Runs after a successful download at `.utility` QoS (e.g. a CoreML compile); nil = no prewarm.
    private let prewarm: (@Sendable (String) async -> Void)?
    private let session = URLSession(configuration: .ephemeral)

    public init(root: URL = ModelStore.defaultRoot, prewarm: (@Sendable (String) async -> Void)? = nil) {
        // FluidAudio must never download on its own; ModelStore is the only downloader.
        ModelHub.offlineMode = true
        self.root = root
        self.prewarm = prewarm
    }

    // MARK: - Layout

    /// The folder each loader expects; nil for system entries and unknown ids.
    public func folder(for entryID: String) -> URL? {
        guard let entry = ModelCatalog.entry(id: entryID), entry.repo != nil else { return nil }
        let base = root.appendingPathComponent(entry.id, isDirectory: true)
        switch entry.runtime {
        case .whisperKit, .speechSwift, .mlx:
            return base
        case .fluidAudio:
            guard let repo = fluidRepo(entry) else { return nil }
            switch repo {
            case .diarizer: return base
            case .nemotronMultilingual: return entry.subfolder.map { append($0, to: base) }
            default: return base.appendingPathComponent(repo.folderName, isDirectory: true)
            }
        case .appleSpeech, .foundationModels:
            return nil
        }
    }

    /// Where repo files land under `Models/<id>/`: `base` components are prepended after `strip` is removed.
    private struct Layout {
        var base: [String]
        var strip: [String]
    }

    private func layout(_ entry: ModelEntry) -> Layout? {
        let subfolder = entry.subfolder.flatMap(components) ?? []
        switch entry.runtime {
        case .whisperKit, .speechSwift, .mlx:
            return Layout(base: [], strip: subfolder)
        case .fluidAudio:
            guard let repo = fluidRepo(entry) else { return nil }
            // Nemotron keeps the HF layout (`multilingual/2240ms/`); the others go under `Repo.folderName`.
            if repo == .nemotronMultilingual { return Layout(base: [], strip: []) }
            return Layout(base: components(repo.folderName) ?? [], strip: subfolder)
        case .appleSpeech, .foundationModels:
            return nil
        }
    }

    private func fluidRepo(_ entry: ModelEntry) -> Repo? {
        guard entry.runtime == .fluidAudio, let repo = entry.repo else { return nil }
        return Repo(rawValue: repo)
    }

    /// Path components relative to `Models/<id>/` for a repo path, or nil when it is outside the layout.
    private func destination(_ repoPath: [String], _ layout: Layout) -> [String]? {
        guard repoPath.starts(with: layout.strip) else { return nil }
        let relative = Array(repoPath.dropFirst(layout.strip.count))
        return relative.isEmpty && layout.base.isEmpty ? nil : layout.base + relative
    }

    /// Whole-component path split; nil for empty, `.`, `..` components or absolute paths.
    private func components(_ path: String) -> [String]? {
        guard !path.isEmpty, !path.hasPrefix("/") else { return nil }
        let parts = path.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        guard parts.allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." && !$0.contains("\0") }) else { return nil }
        return parts
    }

    private func append(_ path: String, to base: URL) -> URL {
        (components(path) ?? []).reduce(base) { $0.appendingPathComponent($1) }
    }

    /// `base/components`, or nil when it resolves outside `base` or crosses a symlink.
    private func safeURL(_ parts: [String], in base: URL) -> URL? {
        let url = parts.reduce(base) { $0.appendingPathComponent($1) }
        let basePath = base.standardizedFileURL.path
        guard url.standardizedFileURL.path.hasPrefix(basePath + "/") else { return nil }
        var check = url.deletingLastPathComponent()
        while check.standardizedFileURL.path.hasPrefix(basePath + "/") {
            if (try? check.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink == true { return nil }
            check = check.deletingLastPathComponent()
        }
        return url
    }

    private func allowlist(_ entry: ModelEntry) -> [[String]] {
        if let files = entry.files { return files.compactMap(components) }
        return entry.subfolder.flatMap(components).map { [$0] } ?? []
    }

    private func isAllowed(_ path: [String], _ allowlist: [[String]]) -> Bool {
        allowlist.isEmpty || allowlist.contains { path.starts(with: $0) }
    }

    /// Repo paths FluidAudio's loader needs, from its public names (`getRequiredModelNames` is internal).
    private func fluidRequired(_ entry: ModelEntry) -> [String]? {
        guard let repo = fluidRepo(entry) else { return nil }
        switch repo {
        case .diarizer:
            return Array(ModelNames.OfflineDiarizer.requiredModels)
        case .parakeetV3:
            return Array(ModelNames.ASR.requiredModelsV3(precision: .int8)) + [ModelNames.ASR.vocabularyFile]
        case .parakeetUltra:
            return Array(ModelNames.ASR.requiredModelsV3()) + [ModelNames.ASR.vocabularyFile]
        case .nemotronMultilingual:
            typealias Names = ModelNames.NemotronMultilingualStreaming
            let names = [
                Names.preprocessorFile, Names.encoderFile, Names.decoderFile, Names.jointFile,
                Names.tokenizer, Names.metadata,
            ]
            return names.map { name in entry.subfolder.map { "\($0)/\(name)" } ?? name }
        default:
            return nil
        }
    }

    private func checkFluidCoverage(_ entry: ModelEntry) throws {
        guard let required = fluidRequired(entry) else { throw ModelStoreError.unsupportedLayout(entry.id) }
        let allowed = allowlist(entry)
        let missing = required.filter { name in
            guard let parts = components(name) else { return true }
            return !(entry.files != nil && isAllowed(parts, allowed))
        }
        guard missing.isEmpty else { throw ModelStoreError.allowlistMissesRequired(entry.id, missing.sorted()) }
    }

    // MARK: - Installed state

    /// All allowlisted files are in place (plus `tokenizer.json` for WhisperKit and FluidAudio's required names).
    public func isInstalled(_ entryID: String) -> Bool {
        guard let entry = ModelCatalog.entry(id: entryID), entry.repo != nil, let entryLayout = layout(entry) else {
            return false
        }
        let base = root.appendingPathComponent(entry.id, isDirectory: true)
        var required: [[String]] = []
        if entry.files != nil {
            required += allowlist(entry)
        } else if entry.runtime == .whisperKit {
            required += [["AudioEncoder.mlmodelc"], ["MelSpectrogram.mlmodelc"], ["TextDecoder.mlmodelc"]]
                .map { (entry.subfolder.flatMap(components) ?? []) + $0 }
        }
        required += (fluidRequired(entry) ?? []).compactMap(components)
        var paths = required.compactMap { destination($0, entryLayout) }
        if entry.runtime == .whisperKit { paths.append(["tokenizer.json"]) }
        guard !paths.isEmpty else { return false }
        return paths.allSatisfy { parts in
            guard let url = safeURL(parts, in: base) else { return false }
            return FileManager.default.fileExists(atPath: url.path)
        }
    }

    /// Bytes on disk under `Models/<id>/`.
    public func installedSize(_ entryID: String) -> Int64 {
        guard ModelCatalog.entry(id: entryID) != nil else { return 0 }
        return Self.size(of: root.appendingPathComponent(entryID, isDirectory: true))
    }

    /// Bytes of all installed models (partial downloads excluded).
    public func totalSize() -> Int64 {
        let folders = (try? FileManager.default.contentsOfDirectory(
            at: root, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
        return folders.reduce(0) { $0 + Self.size(of: $1) }
    }

    private static func size(of folder: URL) -> Int64 {
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey]
        guard let enumerator = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: keys) else {
            return 0
        }
        var total: Int64 = 0
        for case let url as URL in enumerator {
            guard let values = try? url.resourceValues(forKeys: Set(keys)), values.isRegularFile == true else { continue }
            total += Int64(values.fileSize ?? 0)
        }
        return total
    }

    // MARK: - Delete

    /// Removes `Models/<id>/` and any partial download.
    public func delete(_ entryID: String) throws {
        guard ModelCatalog.entry(id: entryID) != nil else { throw ModelStoreError.unknownModel(entryID) }
        let fm = FileManager.default
        for url in [root.appendingPathComponent(entryID, isDirectory: true), partialFolder(entryID)]
        where fm.fileExists(atPath: url.path) {
            try fm.removeItem(at: url)
        }
    }

    public func partialFolder(_ entryID: String) -> URL {
        root.appendingPathComponent(".partial-\(entryID)", isDirectory: true)
    }

    // MARK: - Download

    private struct RemoteFile: Sendable {
        var repo: String
        var revision: String
        var path: String
        var destination: [String]
        var size: Int64
        var sha256: String?
    }

    private struct TreeEntry: Decodable {
        struct LFS: Decodable { var oid: String }
        var type: String
        var path: String
        var size: Int64?
        var lfs: LFS?
    }

    /// Downloads a catalog entry at its pinned revision into `.partial-<id>`, verifies it, then moves it to
    /// `Models/<id>/`. On failure or cancellation the partial folder is removed and the error rethrown
    /// (`CancellationError` when cancelled).
    public func download(
        _ entryID: String, progress: @escaping @Sendable (ModelDownloadProgress) -> Void = { _ in }
    ) async throws {
        guard let entry = ModelCatalog.entry(id: entryID) else { throw ModelStoreError.unknownModel(entryID) }
        guard let repo = entry.repo, let revision = entry.revision else {
            throw ModelStoreError.notDownloadable(entryID)
        }
        guard let entryLayout = layout(entry) else { throw ModelStoreError.unsupportedLayout(entryID) }
        if entry.runtime == .fluidAudio { try checkFluidCoverage(entry) }

        let fm = FileManager.default
        try fm.createDirectory(at: root, withIntermediateDirectories: true)
        let partial = partialFolder(entryID)
        try? fm.removeItem(at: partial)
        let staging = partial.appendingPathComponent(entryID, isDirectory: true)
        do {
            let allowed = allowlist(entry)
            var files = try await list(repo: repo, revision: revision, path: entry.subfolder).compactMap { item in
                try remoteFile(item, repo: repo, revision: revision, allowlist: allowed, layout: entryLayout)
            }
            if let tokenizerRepo = entry.tokenizerRepo, let tokenizerRevision = entry.tokenizerRevision {
                files += try await tokenizerFiles(repo: tokenizerRepo, revision: tokenizerRevision)
            }
            guard !files.isEmpty else { throw ModelStoreError.noFiles(entryID) }
            let total = files.reduce(0) { $0 + $1.size }
            try checkSpace(needed: total + total / 10)
            try fm.createDirectory(at: staging, withIntermediateDirectories: true)

            var done: Int64 = 0
            for file in files {
                try Task.checkCancellation()
                guard let url = safeURL(file.destination, in: staging) else {
                    throw ModelStoreError.unsafePath(file.path)
                }
                progress(ModelDownloadProgress(completedBytes: done, totalBytes: total, file: file.path))
                let before = done
                try await fetch(file, to: url) { bytes in
                    progress(ModelDownloadProgress(completedBytes: before + bytes, totalBytes: total, file: file.path))
                }
                try verify(file, at: url)
                done += file.size
            }
            progress(ModelDownloadProgress(completedBytes: done, totalBytes: total, file: ""))

            if fluidRepo(entry) == .diarizer {
                let marker = staging.appendingPathComponent(Repo.diarizer.folderName, isDirectory: true)
                    .appendingPathComponent(".fluidaudio-revision")
                try Data(revision.utf8).write(to: marker, options: .atomic)
            }
            try Task.checkCancellation()
            let final = root.appendingPathComponent(entryID, isDirectory: true)
            if fm.fileExists(atPath: final.path) { try fm.removeItem(at: final) }
            try fm.moveItem(at: staging, to: final)
            try? fm.removeItem(at: partial)
        } catch {
            try? fm.removeItem(at: partial)
            if error is CancellationError || (error as? URLError)?.code == .cancelled || Task.isCancelled {
                throw CancellationError()
            }
            throw error
        }
        if let prewarm {
            await Task.detached(priority: .utility) { await prewarm(entryID) }.value
        }
    }

    private func remoteFile(
        _ item: TreeEntry, repo: String, revision: String, allowlist: [[String]], layout: Layout
    ) throws -> RemoteFile? {
        guard item.type == "file" else { return nil }
        guard let parts = components(item.path) else { throw ModelStoreError.unsafePath(item.path) }
        guard isAllowed(parts, allowlist) else { return nil }
        guard let target = destination(parts, layout) else { throw ModelStoreError.unsafePath(item.path) }
        return RemoteFile(
            repo: repo, revision: revision, path: item.path, destination: target,
            size: item.size ?? 0, sha256: item.lfs?.oid.lowercased())
    }

    /// `tokenizer.json` and `tokenizer_config.json` from the tokenizer repo, into the entry root.
    private func tokenizerFiles(repo: String, revision: String) async throws -> [RemoteFile] {
        let items = try await list(repo: repo, revision: revision, path: nil)
        return try ["tokenizer.json", "tokenizer_config.json"].map { name in
            guard let item = items.first(where: { $0.type == "file" && $0.path == name }) else {
                throw ModelStoreError.missingTokenizerFile(name)
            }
            return RemoteFile(
                repo: repo, revision: revision, path: name, destination: [name],
                size: item.size ?? 0, sha256: item.lfs?.oid.lowercased())
        }
    }

    private func list(repo: String, revision: String, path: String?) async throws -> [TreeEntry] {
        var urlString = "https://huggingface.co/api/models/\(repo)/tree/\(revision)"
        if let path { urlString += "/" + encoded(path) }
        var next = URL(string: urlString + "?recursive=1")
        var items: [TreeEntry] = []
        while let url = next {
            let (data, response) = try await session.data(from: url)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
                throw ModelStoreError.listingFailed("HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0) for \(repo)")
            }
            items += try JSONDecoder().decode([TreeEntry].self, from: data)
            next = Self.nextPage(http.value(forHTTPHeaderField: "Link"))
        }
        return items
    }

    /// The `rel="next"` URL of an HF `Link` header (paginated tree listings).
    private static func nextPage(_ link: String?) -> URL? {
        guard let link else { return nil }
        for part in link.split(separator: ",") where part.contains("rel=\"next\"") {
            guard let start = part.firstIndex(of: "<"), let end = part.firstIndex(of: ">"), start < end else { continue }
            let url = URL(string: String(part[part.index(after: start)..<end]))
            return url?.host == "huggingface.co" ? url : nil
        }
        return nil
    }

    private func encoded(_ path: String) -> String {
        path.split(separator: "/").map {
            String($0).addingPercentEncoding(withAllowedCharacters: .urlPathAllowed.subtracting(["/"])) ?? String($0)
        }.joined(separator: "/")
    }

    private func checkSpace(needed: Int64) throws {
        let values = try root.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        guard let available = values.volumeAvailableCapacityForImportantUsage else { return }
        guard available >= needed else { throw ModelStoreError.insufficientSpace(needed: needed, available: available) }
    }

    /// One file via a URLSession download task; progress is polled every 250 ms while it runs.
    private func fetch(_ file: RemoteFile, to destination: URL, onBytes: @escaping @Sendable (Int64) -> Void) async throws {
        guard let url = URL(string: "https://huggingface.co/\(file.repo)/resolve/\(file.revision)/\(encoded(file.path))") else {
            throw ModelStoreError.unsafePath(file.path)
        }
        try FileManager.default.createDirectory(
            at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        let current = OSAllocatedUnfairLock<URLSessionDownloadTask?>(initialState: nil)
        let poller = Task {
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(250))
                if let task = current.withLock({ $0 }) { onBytes(task.countOfBytesReceived) }
            }
        }
        defer { poller.cancel() }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
                let task = session.downloadTask(with: url) { location, response, error in
                    if let error { return continuation.resume(throwing: error) }
                    let status = (response as? HTTPURLResponse)?.statusCode ?? 0
                    guard status == 200, let location else {
                        return continuation.resume(throwing: ModelStoreError.httpStatus(status, file.path))
                    }
                    // The temporary file is deleted when this handler returns.
                    do {
                        try FileManager.default.moveItem(at: location, to: destination)
                        continuation.resume()
                    } catch {
                        continuation.resume(throwing: error)
                    }
                }
                current.withLock { $0 = task }
                task.resume()
                if Task.isCancelled { task.cancel() }
            }
        } onCancel: {
            current.withLock { $0 }?.cancel()
        }
    }

    /// Size always; SHA-256 when the tree listing gives an LFS oid. A mismatching file is deleted.
    private func verify(_ file: RemoteFile, at url: URL) throws {
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize.map(Int64.init) ?? -1
        guard size == file.size else {
            try? FileManager.default.removeItem(at: url)
            throw ModelStoreError.sizeMismatch(file.path)
        }
        guard let expected = file.sha256 else { return }
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 8 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        let digest = hasher.finalize().map { String(format: "%02x", $0) }.joined()
        guard digest == expected else {
            try? FileManager.default.removeItem(at: url)
            throw ModelStoreError.checksumMismatch(file.path)
        }
    }
}
