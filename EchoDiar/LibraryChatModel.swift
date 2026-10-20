import EchoAI
import EchoCore
import EchoLocalLLM
import Foundation

// MARK: - Library index (RAG)

extension AppModel {
    /// `embeddingModelID` prefix of an API embedder: `"api:<provider id>"`.
    static let apiEmbedderPrefix = "api:"

    /// The global chat's chat and archived chats, beside the conversations.
    var globalChatFolder: URL {
        RecordingSession.conversationsRoot.deletingLastPathComponent()
            .appendingPathComponent("GlobalChat", isDirectory: true)
    }

    /// The embedder picked in Settings › RAG; nil (with `ragStatus.message` saying why) when its model is not
    /// downloaded, or its provider is not enabled or misses a model or key.
    func makeEmbedder() -> (any TextEmbedder)? {
        let id = settings.embeddingModelID
        if id == "apple-nl" { return AppleTextEmbedder() }
        if id.hasPrefix(Self.apiEmbedderPrefix) {
            guard let config = embeddingProviderConfig else {
                ragStatus.message = "Pick an enabled provider in Settings › RAG"
                return nil
            }
            let model = settings.embeddingAPIModel.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !model.isEmpty else {
                ragStatus.message = "Enter the embedding model of \(config.displayName) in Settings › RAG"
                return nil
            }
            let apiKey = config.authStyle == .none ? nil : try? KeychainStore.apiKey(forProvider: config.id)
            // As `makeProvider(for:)`: sent without a key, a preset only answers HTTP 401.
            if config.preset != nil, config.authStyle != .none, apiKey?.isEmpty ?? true {
                ragStatus.message = "Save a key for \(config.displayName) in Settings › AI"
                return nil
            }
            return APITextEmbedder(config: config, apiKey: apiKey, model: model)
        }
        guard let entry = ModelCatalog.entry(id: id), entry.kind == .embedding, entry.runtime == .mlx else {
            ragStatus.message = "Pick an embedding model in Settings › RAG"
            return nil
        }
        guard modelStore.isInstalled(id), let folder = modelStore.folder(for: id) else {
            ragStatus.message = "Download the embedding model in Settings › RAG"
            return nil
        }
        return MLXEmbedder(entryID: id, folder: folder)
    }

    /// The enabled OpenAI-compatible or local-host config (Ollama included) of an `"api:<provider id>"` embedder.
    private var embeddingProviderConfig: ProviderConfig? {
        let id = settings.embeddingModelID
        guard id.hasPrefix(Self.apiEmbedderPrefix) else { return nil }
        let providerID = String(id.dropFirst(Self.apiEmbedderPrefix.count))
        return providerConfigs.first {
            $0.id == providerID && $0.enabled && ($0.kind == .openAICompatible || $0.kind == .local)
        }
    }

    /// `<provider id>@<scheme://host[:port]>` (default ports left out), the key of its cloud embedding confirmation;
    /// a changed base URL asks again.
    func embeddingDestination(_ config: ProviderConfig) -> String {
        let url = URL(string: config.baseURL.trimmingCharacters(in: .whitespacesAndNewlines))
        let scheme = url?.scheme?.lowercased() ?? ""
        let host = url?.host()?.lowercased() ?? ""
        let port = url?.port.flatMap { $0 == ["http": 80, "https": 443][scheme] ? nil : $0 }
        return "\(config.id)@\(scheme)://\(host)" + (port.map { ":\($0)" } ?? "")
    }

    /// The API embedder sends transcripts off this Mac and its destination was not confirmed yet. Loopback hosts
    /// (`AIProvider.isLoopback`, wider than `APITextEmbedder.isLocal`) need no confirmation.
    var embedderNeedsConsent: Bool {
        guard let config = embeddingProviderConfig, !AIProvider.isLoopback(baseURL: config.baseURL) else { return false }
        return !settings.cloudEmbeddingConfirmed.contains(embeddingDestination(config))
    }

    /// Remembers the API embedder's destination as confirmed, then indexes.
    func confirmCloudEmbedding() {
        guard let config = embeddingProviderConfig else { return }
        let destination = embeddingDestination(config)
        if !settings.cloudEmbeddingConfirmed.contains(destination) {
            updateSettings { $0.cloudEmbeddingConfirmed.append(destination) }
        }
        syncLibraryIndex()
    }

    /// Brings the index up to date with the library: one pass at a time, a call during a pass runs another once it
    /// ends. `restart` cancels the running pass first (the embedder changed). Turning RAG off removes the index, in
    /// a pass of its own once the running one stopped; turning it on rebuilds it.
    func syncLibraryIndex(restart: Bool = false) {
        if !settings.ragEnabled, ragStatus != RAGStatus() { ragStatus = RAGStatus() }
        if restart || !settings.ragEnabled { libraryIndexTask?.cancel() }
        guard libraryIndexTask == nil else {
            libraryIndexPending = true
            return
        }
        libraryIndexTask = Task { [weak self] in
            await self?.indexLibrary()
            guard let self else { return }
            libraryIndexTask = nil
            ragStatus.running = false
            ragStatus.paused = false
            if libraryIndexPending {
                libraryIndexPending = false
                syncLibraryIndex()
            }
        }
    }

    /// Deletes the current embedder's shards, then indexes every conversation again.
    func rebuildLibraryIndex() {
        libraryIndexRebuild = true
        syncLibraryIndex(restart: true)
    }

    /// One pass: with RAG off or an empty library, deletes the whole index; else drops the other embedders' folders,
    /// loads the current one's shards when the embedder changed, then indexes the stale conversations in batches of one model load each (`indexBatch`), so recordings and AI
    /// jobs can take the gate between them. An error stops the pass; the next change retries.
    private func indexLibrary() async {
        // A missing or empty index folder makes this cheap on every call while RAG stays off.
        guard settings.ragEnabled else {
            await libraryIndex.removeAll()
            libraryIndexKey = nil
            ragStatus = RAGStatus()
            return
        }
        // Before the library's first emit: `stale` deletes the shards of every conversation not listed.
        guard libraryLoaded else { return }
        let conversations = conversations
        ragStatus.message = nil
        ragStatus.needsConsent = false
        // An empty library keeps no shard, whatever the embedder.
        guard !conversations.isEmpty else {
            await libraryIndex.removeAll()
            libraryIndexKey = nil
            ragStatus.total = 0
            ragStatus.indexed = 0
            return
        }
        // Before the embedder checks: trashed conversations lose their shards even with no usable embedder.
        await libraryIndex.prune(conversations)
        guard let embedder = makeEmbedder() else { return }
        if !embedder.isLocal && embedderNeedsConsent {
            ragStatus.needsConsent = true
            return
        }
        let key = embedder.key
        await libraryIndex.removeAll(except: key)
        if libraryIndexKey != key {
            await libraryIndex.load(key: key)
            libraryIndexKey = key
        }
        if libraryIndexRebuild {
            libraryIndexRebuild = false
            await libraryIndex.remove(key: key)
        }
        let stale = await libraryIndex.stale(conversations)
        let transcribed = Set(conversations.filter(\.hasTranscript).map(\.id))
        ragStatus.total = transcribed.count
        ragStatus.indexed = await libraryIndex.indexedIDs.intersection(transcribed).count
        guard !stale.isEmpty, !Task.isCancelled else { return }
        ragStatus.running = true
        // In-process models only: an API embedder (a loopback Ollama too) loads nothing here.
        let gated = !(embedder is APITextEmbedder)
        var next = stale.startIndex
        while next < stale.endIndex {
            while indexingPaused {
                ragStatus.paused = true
                try? await Task.sleep(for: .seconds(5))
                if Task.isCancelled { return }
            }
            ragStatus.paused = false
            guard !Task.isCancelled else { return }
            let batch = stale[next...]
            let work: @Sendable () async throws -> (handled: Int, stopped: Bool) = { [self] in
                try await embedder.withLoaded { loaded in
                    await self.indexBatch(batch, transcribed: transcribed, embedder: loaded)
                }
            }
            let result: (handled: Int, stopped: Bool)
            do {
                result = gated ? try await modelGate.withExclusiveModel(owner: .embedder, work) : try await work()
            } catch {
                guard !Task.isCancelled else { return }
                NSLog("EchoDiar: indexing failed to load the embedder: \(error)")
                ragStatus.message = "Indexing stopped: \(error.localizedDescription)"
                return
            }
            guard !result.stopped, !Task.isCancelled else { return }
            next += result.handled
        }
    }

    /// Indexes `batch` in order with one loaded embedder until about 20 s have passed, indexing pauses or the pass
    /// is cancelled; at least one conversation each time. Returns how many it went through, and `stopped` after an
    /// error (in `ragStatus.message`).
    private func indexBatch(
        _ batch: ArraySlice<Conversation>, transcribed: Set<UUID>, embedder: any LoadedTextEmbedder
    ) async -> (handled: Int, stopped: Bool) {
        let start = ContinuousClock.now
        var handled = 0
        for conversation in batch {
            handled += 1
            // Deleted during the pass.
            if conversations.contains(where: { $0.id == conversation.id }) {
                let folder = conversation.folder
                do {
                    // `LibraryIndex.index` takes the fingerprint after these reads (logged follow-up).
                    let (transcript, summary) = try await Task.detached(priority: .utility) {
                        (try Transcript.read(from: folder), Conversation.readSummary(from: folder))
                    }.value
                    try await libraryIndex.index(conversation, transcript: transcript, summary: summary, embedder: embedder)
                } catch {
                    guard !Task.isCancelled else { return (handled, true) }
                    NSLog("EchoDiar: indexing failed for \(folder.lastPathComponent): \(error)")
                    ragStatus.message = "Indexing stopped at \"\(conversation.title)\": \(error.localizedDescription)"
                    return (handled, true)
                }
                ragStatus.indexed = await libraryIndex.indexedIDs.intersection(transcribed).count
            }
            if indexingPaused || Task.isCancelled || start.duration(to: .now) >= .seconds(20) { break }
        }
        return (handled, false)
    }
}
