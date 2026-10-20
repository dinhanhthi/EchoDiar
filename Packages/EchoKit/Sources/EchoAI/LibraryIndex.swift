import EchoCore
import Foundation

public enum LibraryIndexError: Error, Equatable, Sendable {
    /// The embedder key is not a plain folder name, or `load(key:)` was not called.
    case invalidKey
    /// The embedder returned a different number of vectors than texts, vectors of different lengths, or a length
    /// other than the index's.
    case vectorMismatch
}

/// The RAG index of the library: one shard per conversation at `root/<embedder key>/<conversation id>.json`, every
/// shard of the loaded key kept in memory. Retrieval fuses cosine similarity and BM25 with reciprocal rank fusion.
/// Local embedders run inside `ModelLoadGate` (owner `.embedder`): the caller holds it while it passes a loaded
/// embedder to `index`, so the index itself takes no gate.
public actor LibraryIndex {
    /// A passage that is embedded and searched: a window of transcript turns, or a part of the summary.
    public struct Chunk: Codable, Equatable, Sendable {
        public enum Kind: String, Codable, Sendable {
            case transcript, summary
        }

        public var kind: Kind
        /// Seconds of the window's first turn start and last turn end; nil for summary chunks.
        public var start: Double?
        public var end: Double?
        public var text: String

        public init(kind: Kind, start: Double? = nil, end: Double? = nil, text: String) {
            self.kind = kind
            self.start = start
            self.end = end
            self.text = text
        }
    }

    public struct Hit: Equatable, Sendable {
        public var conversationID: UUID
        public var chunk: Chunk
        /// Reciprocal rank fusion score; higher is better.
        public var score: Double
    }

    /// The shard file format, version 1. `vectors` holds `chunks.count × dimension` Float32 values, little-endian,
    /// chunk after chunk.
    struct Shard: Codable {
        var version = 1
        var fingerprint: String
        var dimension: Int
        var chunks: [Chunk]
        var vectors: Data
    }

    private struct Entry {
        var fingerprint: String
        var chunks: [Chunk]
        /// `chunks.count × dimension` values, chunk after chunk.
        var vectors: [Float]
        /// BM25 terms of each chunk.
        var terms: [[String]]
    }

    /// Estimated tokens per chunk: Apple's embedder reads 256 real tokens, and the estimate runs a little low.
    static let chunkTokens = 220
    /// Candidates taken from each ranking before fusion.
    static let fusionDepth = 50
    static let rrfK = 60.0

    private let root: URL
    private var key: String?
    private var dimension: Int?
    private var entries: [UUID: Entry] = [:]

    public init(root: URL) {
        self.root = root
    }

    /// Indexed conversations of the loaded key.
    public var count: Int { entries.count }
    public var indexedIDs: Set<UUID> { Set(entries.keys) }

    // MARK: Chunking

    /// Windows of consecutive transcript turns (`PromptBuilder` lines, speaker display names) of about
    /// `chunkTokens` estimated tokens, each sharing its first turn with the previous window's last when that turn and
    /// the next one fit a window together; then the summary
    /// split on blank lines, paragraphs merged or split to fit `chunkTokens`.
    public static func chunks(transcript: Transcript?, summary: String?) -> [Chunk] {
        var chunks: [Chunk] = []
        if let transcript, !transcript.segments.isEmpty {
            let segments = transcript.segments
            let lines = segments.map { PromptBuilder.line($0, transcript: transcript) }
            let costs = lines.map { TranscriptChunker.estimateTokens($0) + 1 }
            var start = 0
            while true {
                var end = start
                var used = 0
                while end < lines.count {
                    let cost = costs[end]
                    if end > start, used + cost > chunkTokens { break }
                    used += cost
                    end += 1
                }
                chunks.append(Chunk(
                    kind: .transcript, start: segments[start].start, end: segments[end - 1].end,
                    text: lines[start..<end].joined(separator: "\n")))
                guard end < lines.count else { break }
                start = end - 1 > start && costs[end - 1] + costs[end] <= chunkTokens ? end - 1 : end
            }
        }
        if let summary {
            let paragraphs = summary.split(separator: /\n[ \t]*\n/)
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }
            chunks += pack(paragraphs.flatMap { split($0) }, separator: "\n\n")
                .map { Chunk(kind: .summary, text: $0) }
        }
        return chunks
    }

    /// `text` as parts of at most `chunkTokens`: by lines, then by words. A single word is never split.
    private static func split(_ text: String) -> [String] {
        if TranscriptChunker.estimateTokens(text) <= chunkTokens { return [text] }
        let lines = text.components(separatedBy: .newlines).filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        if lines.count > 1 { return pack(lines.flatMap { split($0) }, separator: "\n") }
        let words = text.split(whereSeparator: \.isWhitespace).map(String.init)
        return words.count > 1 ? pack(words, separator: " ") : [text]
    }

    /// Consecutive `parts` joined while the result fits `chunkTokens`.
    private static func pack(_ parts: [String], separator: String) -> [String] {
        var packed: [String] = []
        var current = ""
        for part in parts {
            let joined = current.isEmpty ? part : current + separator + part
            if !current.isEmpty, TranscriptChunker.estimateTokens(joined) > chunkTokens {
                packed.append(current)
                current = part
            } else {
                current = joined
            }
        }
        if !current.isEmpty { packed.append(current) }
        return packed
    }

    // MARK: Lifecycle

    /// Modification date and size of `transcript.json` and of `summary.md` (the newest summary, rewritten with
    /// every version), `-` for a missing file. A changed fingerprint means the shard is out of date.
    public static func fingerprint(folder: URL) -> String {
        [Transcript.fileName, Conversation.summaryFileName].map { name in
            let path = folder.appendingPathComponent(name).path
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: path),
                  let date = attributes[.modificationDate] as? Date,
                  let size = (attributes[.size] as? NSNumber)?.int64Value else { return "-" }
            return "\(date.timeIntervalSince1970)-\(size)"
        }.joined(separator: "|")
    }

    /// Reads every shard of `key` into memory, replacing what was loaded. Unreadable shards, and shards whose
    /// vector length differs from the first one read, are skipped (their conversations come back as stale).
    public func load(key: String) {
        entries = [:]
        dimension = nil
        guard let folder = folder(for: key) else {
            self.key = nil
            return
        }
        self.key = key
        for (id, url) in shardFiles(in: folder) {
            guard let data = try? Data(contentsOf: url), let shard = try? JSONDecoder().decode(Shard.self, from: data),
                  shard.version == 1,
                  let vectors = Self.floats(shard.vectors, chunks: shard.chunks.count, dimension: shard.dimension)
            else { continue }
            if !shard.chunks.isEmpty {
                guard shard.dimension > 0, dimension ?? shard.dimension == shard.dimension else { continue }
                dimension = shard.dimension
            }
            entries[id] = Entry(
                fingerprint: shard.fingerprint, chunks: shard.chunks, vectors: vectors,
                terms: shard.chunks.map { TranscriptChunker.terms($0.text) })
        }
    }

    /// The conversations with a transcript whose shard is missing or out of date (fingerprint), in list order.
    /// Shards of conversations not in `conversations`, or without a transcript, are deleted.
    public func stale(_ conversations: [Conversation]) -> [Conversation] {
        let transcribed = conversations.filter(\.hasTranscript)
        guard let key, let folder = folder(for: key) else { return transcribed }
        let keep = Set(transcribed.map(\.id))
        for (id, url) in shardFiles(in: folder) where !keep.contains(id) {
            try? FileManager.default.removeItem(at: url)
        }
        entries = entries.filter { keep.contains($0.key) }
        return transcribed.filter { entries[$0.id]?.fingerprint != Self.fingerprint(folder: $0.folder) }
    }

    /// Chunks the conversation, embeds every chunk as a passage in one call, and writes its shard. When another key
    /// is loaded meanwhile, nothing is written.
    public func index(
        _ conversation: Conversation, transcript: Transcript?, summary: String?, embedder: any LoadedTextEmbedder
    ) async throws {
        guard let key, let folder = folder(for: key) else { throw LibraryIndexError.invalidKey }
        let fingerprint = Self.fingerprint(folder: conversation.folder)
        let chunks = Self.chunks(transcript: transcript, summary: summary)
        let vectors = chunks.isEmpty ? [] : try await embedder.embed(chunks.map(\.text), query: false)
        let length = vectors.first?.count ?? 0
        guard vectors.count == chunks.count, vectors.allSatisfy({ $0.count == length }),
              chunks.isEmpty || length > 0 else { throw LibraryIndexError.vectorMismatch }
        guard self.key == key else { return }
        if !chunks.isEmpty {
            guard dimension ?? length == length else { throw LibraryIndexError.vectorMismatch }
        }
        let flat = vectors.flatMap { $0 }
        let shard = Shard(fingerprint: fingerprint, dimension: length, chunks: chunks, vectors: Self.data(flat))
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try JSONEncoder().encode(shard)
            .write(to: folder.appendingPathComponent("\(conversation.id.uuidString).json"), options: .atomic)
        if !chunks.isEmpty { dimension = length }
        entries[conversation.id] = Entry(
            fingerprint: fingerprint, chunks: chunks, vectors: flat, terms: chunks.map { TranscriptChunker.terms($0.text) })
    }

    /// Deletes the index folders of every key but `key`.
    public func removeAll(except key: String) {
        guard folder(for: key) != nil else { return }
        let fm = FileManager.default
        guard let names = try? fm.contentsOfDirectory(atPath: root.path) else { return }
        for name in names where name != key {
            try? fm.removeItem(at: root.appendingPathComponent(name, isDirectory: true))
        }
        if self.key != key {
            self.key = nil
            entries = [:]
            dimension = nil
        }
    }

    /// Deletes the index folder of `key` (Rebuild); when it is loaded, memory is emptied and the key stays loaded.
    public func remove(key: String) {
        guard let folder = folder(for: key) else { return }
        try? FileManager.default.removeItem(at: folder)
        if self.key == key {
            entries = [:]
            dimension = nil
        }
    }

    // MARK: Search

    /// The `limit` best chunks for `question`: the top `fusionDepth` by cosine similarity with `vector` and the top
    /// `fusionDepth` by BM25 (chunks sharing a term with the question), fused by reciprocal rank (k = 60). BM25
    /// alone when `vector` is nil or not of the index's length.
    public func search(question: String, vector: [Float]?, limit: Int) -> [Hit] {
        let ids = entries.keys.sorted { $0.uuidString < $1.uuidString }
        var refs: [(id: UUID, chunk: Int)] = []
        var documents: [[String]] = []
        for id in ids {
            guard let entry = entries[id] else { continue }
            for index in entry.chunks.indices {
                refs.append((id, index))
                documents.append(entry.terms[index])
            }
        }
        var fused = [Double](repeating: 0, count: refs.count)
        let queryTerms = TranscriptChunker.terms(question)
        let querySet = Set(queryTerms)
        let lexical = TranscriptChunker.bm25Rank(queryTerms: queryTerms, documents: documents)
            .lazy.filter { !querySet.isDisjoint(with: documents[$0]) }.prefix(Self.fusionDepth)
        for (rank, index) in lexical.enumerated() { fused[index] += 1 / (Self.rrfK + Double(rank + 1)) }
        if let vector, let dimension, vector.count == dimension {
            let similarities = refs.map { ref -> Float in
                let vectors = entries[ref.id]!.vectors
                let offset = ref.chunk * dimension
                var dot: Float = 0
                for i in 0..<dimension { dot += vectors[offset + i] * vector[i] }
                return dot
            }
            let semantic = similarities.indices.sorted { (similarities[$0], $1) > (similarities[$1], $0) }
                .prefix(Self.fusionDepth)
            for (rank, index) in semantic.enumerated() { fused[index] += 1 / (Self.rrfK + Double(rank + 1)) }
        }
        return fused.indices.filter { fused[$0] > 0 }
            .sorted { (fused[$0], $1) > (fused[$1], $0) }
            .prefix(max(limit, 0))
            .map { index in
                let ref = refs[index]
                return Hit(conversationID: ref.id, chunk: entries[ref.id]!.chunks[ref.chunk], score: fused[index])
            }
    }

    // MARK: Helpers

    /// `root/key`, or nil when `key` is not a plain folder name (empty, `.`, `..`, contains `/`).
    private func folder(for key: String) -> URL? {
        guard !key.isEmpty, key != ".", key != "..", !key.contains("/") else { return nil }
        return root.appendingPathComponent(key, isDirectory: true)
    }

    /// `<uuid>.json` files of `folder`, by conversation id.
    private func shardFiles(in folder: URL) -> [(UUID, URL)] {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []
        return names.compactMap { name in
            guard name.hasSuffix(".json"), let id = UUID(uuidString: String(name.dropLast(5))) else { return nil }
            return (id, folder.appendingPathComponent(name))
        }
    }

    private static func data(_ floats: [Float]) -> Data {
        var data = Data(capacity: floats.count * 4)
        for value in floats {
            withUnsafeBytes(of: value.bitPattern.littleEndian) { data.append(contentsOf: $0) }
        }
        return data
    }

    /// `chunks × dimension` Float32 little-endian values, or nil when `data` has another size.
    private static func floats(_ data: Data, chunks: Int, dimension: Int) -> [Float]? {
        let (count, overflow) = chunks.multipliedReportingOverflow(by: dimension)
        guard dimension >= 0, !overflow, count <= data.count / 4, data.count == count * 4 else { return nil }
        return data.withUnsafeBytes { raw in
            (0..<count).map {
                Float(bitPattern: UInt32(littleEndian: raw.loadUnaligned(fromByteOffset: $0 * 4, as: UInt32.self)))
            }
        }
    }
}
