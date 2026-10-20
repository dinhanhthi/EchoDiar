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
        resolveEmbedder { ragStatus.message = $0 }
    }

    /// `makeEmbedder()` without touching `ragStatus`: `problem` gets why there is none.
    private func resolveEmbedder(problem: (String) -> Void = { _ in }) -> (any TextEmbedder)? {
        let id = settings.embeddingModelID
        if id == "apple-nl" { return AppleTextEmbedder() }
        if id.hasPrefix(Self.apiEmbedderPrefix) {
            guard let config = embeddingProviderConfig else {
                problem("Pick an enabled provider in Settings › RAG")
                return nil
            }
            let model = settings.embeddingAPIModel.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !model.isEmpty else {
                problem("Enter the embedding model of \(config.displayName) in Settings › RAG")
                return nil
            }
            let apiKey = config.authStyle == .none ? nil : try? KeychainStore.apiKey(forProvider: config.id)
            // As `makeProvider(for:)`: sent without a key, a preset only answers HTTP 401.
            if config.preset != nil, config.authStyle != .none, apiKey?.isEmpty ?? true {
                problem("Save a key for \(config.displayName) in Settings › AI")
                return nil
            }
            return APITextEmbedder(config: config, apiKey: apiKey, model: model)
        }
        guard let entry = ModelCatalog.entry(id: id), entry.kind == .embedding, entry.runtime == .mlx else {
            problem("Pick an embedding model in Settings › RAG")
            return nil
        }
        guard modelStore.isInstalled(id), let folder = modelStore.folder(for: id) else {
            problem("Download the embedding model in Settings › RAG")
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

// MARK: - Library chat

extension AppModel {
    /// Asks `question` across the library as the AI job (`conversationID` nil) with the current AI provider and model:
    /// searches the index, then streams the answer, saved with its references in `globalChatFolder`'s `chat.json`.
    /// The job is set before the search so indexing yields. With no source found the question is still asked: the
    /// prompt has the model say the sources hold no answer.
    func askLibrary(_ question: String) {
        guard aiJob == nil, settings.ragEnabled, let provider = currentAIProvider else { return }
        let model = aiModel(for: provider).trimmingCharacters(in: .whitespacesAndNewlines)
        aiError = nil
        let client: (any LLMProvider)?
        do {
            client = try llmClient(for: provider)
        } catch {
            aiError = (nil, "\(provider.name): \(error.localizedDescription)")
            return
        }
        guard let client else {
            aiError = (nil, "\(provider.name) is not available.")
            return
        }
        let task = Task { [weak self] in
            let stream = await self?.libraryAnswer(question, client: client, providerID: provider.id, model: model)
            if let stream {
                do {
                    for try await update in stream { self?.applyAIUpdate(update) }
                } catch where !Task.isCancelled {
                    self?.aiError = (nil, error.localizedDescription)
                } catch {}
            }
            guard let self else { return }
            flushAIText()
            aiJob = nil
            aiJobsEnded += 1
            syncLibraryIndex()
        }
        aiJob = AIJob(conversationID: nil, kind: .question(question), status: "Searching your conversations…", task: task)
    }

    /// The search and the answer stream of `askLibrary`; nil when the job was stopped during the search.
    private func libraryAnswer(
        _ question: String, client: any LLMProvider, providerID: String, model: String
    ) async -> AsyncThrowingStream<AIService.Update, Error>? {
        let vector = await queryVector(question)
        let context = await client.contextWindow(model: model)
        let folder = globalChatFolder
        let history = ChatLog.read(from: folder).messages.map { message in
            guard message.role == .assistant else { return message }
            var plain = message
            plain.text = Self.withoutCitations(message.text)
            return plain
        }
        let limit = settings.chatContextMessages
        // What the prompt holds besides the sources: its instructions (about 600 tokens), the history and the question.
        let promptTokens = (limit > 0 ? history.suffix(limit) : history[...])
            .reduce(600 + TranscriptChunker.estimateTokens(question)) { $0 + TranscriptChunker.estimateTokens($1.text) }
        let hits = await libraryIndex.search(question: question, vector: vector, limit: 40)
        let (sources, references) = librarySources(
            hits, budget: TranscriptChunker.budget(contextWindow: context, instructionTokens: promptTokens))
        guard !Task.isCancelled else { return nil }
        aiJob?.status = nil
        return aiService.askLibrary(
            question: question, sources: sources, references: references, history: history, historyLimit: limit,
            provider: client, providerID: providerID, model: model, language: settings.summaryLanguage, folder: folder)
    }

    /// The question's embedding; nil (BM25 only) without a usable embedder, while the API embedder's destination is
    /// not confirmed (the confirmation covers questions too), or on error. Loads the embedder's index when another
    /// one is loaded. In-process models load inside `modelGate`, for this one query.
    private func queryVector(_ question: String) async -> [Float]? {
        guard let embedder = resolveEmbedder() else { return nil }
        if !embedder.isLocal && embedderNeedsConsent { return nil }
        let key = embedder.key
        if libraryIndexKey != key {
            await libraryIndex.load(key: key)
            libraryIndexKey = key
        }
        if !(embedder is APITextEmbedder) && modelGateBusy { return nil }
        let work: @Sendable () async throws -> [Float]? = {
            try await embedder.withLoaded { try await $0.embed([question], query: true).first }
        }
        do {
            return embedder is APITextEmbedder
                ? try await work() : try await modelGate.withExclusiveModel(owner: .embedder, work)
        } catch {
            if !Task.isCancelled { NSLog("EchoDiar: could not embed the question: \(error)") }
            return nil
        }
    }

    /// The best `hits` of conversations still in the library, at most 12 passages fitting `budget` estimated tokens,
    /// grouped per conversation in first-hit order and numbered from 1; with the answer's references.
    private func librarySources(
        _ hits: [LibraryIndex.Hit], budget: Int
    ) -> ([PromptBuilder.LibrarySource], [ChatLog.Reference]) {
        let byID = Dictionary(conversations.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        var order: [UUID] = []
        var passages: [UUID: [String]] = [:]
        var used = 0
        for hit in hits where passages.values.reduce(0, { $0 + $1.count }) < 12 {
            guard let conversation = byID[hit.conversationID] else { continue }
            let isNew = passages[hit.conversationID] == nil
            // A new source adds its tags and its line "Conversation n: title, date"; a passage its separator.
            let cost = TranscriptChunker.estimateTokens(hit.chunk.text) + 2
                + (isNew ? TranscriptChunker.estimateTokens(conversation.title) + 20 : 0)
            guard used + cost <= budget else { continue }
            used += cost
            if isNew { order.append(hit.conversationID) }
            passages[hit.conversationID, default: []].append(hit.chunk.text)
        }
        var sources: [PromptBuilder.LibrarySource] = []
        var references: [ChatLog.Reference] = []
        for (index, id) in order.enumerated() {
            guard let conversation = byID[id] else { continue }
            sources.append(PromptBuilder.LibrarySource(
                number: index + 1, title: conversation.title, date: conversation.createdAt, passages: passages[id] ?? []))
            references.append(ChatLog.Reference(number: index + 1, conversationID: id, title: conversation.title))
        }
        return (sources, references)
    }

    /// `text` without source citations: `echodiar-ref` links become their text and `[n]` / `[n mm:ss]` markers go, so
    /// an earlier answer's numbers never point at the next question's sources.
    static func withoutCitations(_ text: String) -> String {
        text.replacing(#/\[((?:[^\[\]\n]|\[[^\[\]\n]*\])*)\]\(echodiar-ref:[^)\s]*\)/#) { String($0.output.1) }
            .replacing(#/[ \t]*\[\d{1,3}(?:,? +\d{1,3}:\d{2}(?::\d{2})?)?\](?!\()/#, with: "")
    }

    /// Archives the library chat so the next question starts without history, titled as `startFreshChat` does.
    func startFreshLibraryChat() {
        let folder = globalChatFolder
        changeChat(of: nil, in: folder) { try ChatArchive.archive(in: folder) }
    }

    /// Makes the archived library chat `entry` the open one, as `resumeChat` does.
    func resumeLibraryChat(_ entry: ChatArchive.Entry) {
        let folder = globalChatFolder
        changeChat(of: nil, in: folder) { try ChatArchive.resume(entry.id, in: folder) }
    }

    /// Deletes the archived library chat `entry`, as `deleteChat` does.
    func deleteLibraryChat(_ entry: ChatArchive.Entry) {
        let folder = globalChatFolder
        changeChat(of: nil, in: folder) {
            try ChatArchive.delete(entry.id, in: folder)
            return nil
        }
    }

    /// Deletes every archived library chat, as `clearChats` does.
    func clearLibraryChats() {
        let folder = globalChatFolder
        changeChat(of: nil, in: folder) {
            try ChatArchive.deleteAll(in: folder)
            return nil
        }
    }

    /// A reference of a library answer: the conversation at `seconds` in its transcript, or its summary; the library
    /// chat stays open beside it. Does nothing when the conversation is gone.
    func openReference(id: UUID, seconds: Double?) {
        guard conversations.contains(where: { $0.id == id }) else { return }
        globalChatOpen = true
        if let seconds {
            openTranscript(conversationID: id, at: seconds)
        } else {
            open(id, tab: .summary)
        }
    }
}
