import EchoCore
import Foundation
import Synchronization

/// Summaries and questions about one conversation (docs/research/llm-providers.md F). Each job streams its answer,
/// saves it in the conversation folder when it completes (a new summary version + `hasSummary`, or two more
/// `chat.json` messages) and saves nothing when cancelled or failed. `stop(_:)` ends a job early (`stop()` every job)
/// and saves the answer streamed until then (a summary marked stopped); nothing when no text was streamed yet.
/// `cancel(_:)` ends a job saving nothing, even after a stop, and waits for it. A
/// job's `jobID` must be unique among the running jobs. A title job names an archived chat and saves nothing. A
/// catch-up appends to the recording's `catchups.json` and never runs on an in-app model. In-app models
/// (`LocalLLMProvider`: Foundation Models, MLX) run the whole job inside `ModelLoadGate` (owner `.llm`), loaded once
/// and unloaded before the gate is released.
public actor AIService {
    public enum Update: Equatable, Sendable {
        /// What a long job is doing ("Reading part 2 of 5…"), before the answer starts.
        case status(String)
        /// The next piece of the answer.
        case text(String)
    }

    static let waitingStatus = "Waiting for live captions or transcription to finish…"
    static let loadingStatus = "Loading the model…"

    /// Where the answers of a job come from: a provider's stream, or an in-app model loaded for the whole job.
    enum Backend: Sendable {
        case remote(any LLMProvider)
        case local(any LocalLLMGenerator)
    }

    /// The text streamed by a job until it was cancelled; empty for a step that streams nothing (map and merge).
    struct Interrupted: Error {
        let text: String
    }

    /// The tasks of the running jobs by job id, and those `stop()` or `stop(_:)` ended.
    final class Jobs: Sendable {
        let running = Mutex<[UUID: Task<Void, Never>]>([:])
        let stopped = Mutex<Set<UUID>>([])
    }

    private let gate: ModelLoadGate
    private nonisolated let jobs = Jobs()

    public init(gate: ModelLoadGate) {
        self.gate = gate
    }

    /// Ends every running job, saving what each has streamed; their streams then finish without an error.
    public nonisolated func stop() {
        let tasks = jobs.running.withLock { $0 }
        jobs.stopped.withLock { $0.formUnion(tasks.keys) }
        for task in tasks.values { task.cancel() }
    }

    /// Ends the running job `jobID` as `stop()` does, the others keep running; nothing when it is not running.
    public nonisolated func stop(_ jobID: UUID) {
        // Marked under the running lock: a job that ends meanwhile cannot leave its id in `stopped`.
        let task = jobs.running.withLock { running -> Task<Void, Never>? in
            guard let task = running[jobID] else { return nil }
            jobs.stopped.withLock { _ = $0.insert(jobID) }
            return task
        }
        task?.cancel()
    }

    /// Cancels the running job `jobID` without saving anything, also after `stop(_:)`, and returns once its task has
    /// ended (a write it had already started is then done); at once when it is not running.
    public nonisolated func cancel(_ jobID: UUID) async {
        // Out of `running` too, so a `stop(_:)` meanwhile cannot mark it stopped again.
        let task = jobs.running.withLock { running -> Task<Void, Never>? in
            guard let task = running.removeValue(forKey: jobID) else { return nil }
            jobs.stopped.withLock { _ = $0.remove(jobID) }
            return task
        }
        task?.cancel()
        await task?.value
    }

    /// Streams a new summary: one pass when the transcript fits, else map over the chunks and reduce the notes until
    /// one group is left; only the last reduce is streamed. `language` is the summary-language setting.
    /// `providerID` is saved with the summary.
    public nonisolated func summarize(
        conversation: Conversation, transcript: Transcript, provider: any LLMProvider, providerID: String,
        model: String, language: String, jobID: UUID = UUID()
    ) -> AsyncThrowingStream<Update, Error> {
        job(id: jobID, provider: provider, model: model) { backend, yield, isStopped in
            let summary: String
            var cut = false
            do {
                summary = try await Self.summary(
                    conversation: conversation, transcript: transcript, provider: provider, backend: backend,
                    model: model, language: language, yield: yield)
            } catch let interrupted as Interrupted where isStopped() && !interrupted.text.isEmpty {
                (summary, cut) = (interrupted.text, true)
            }
            if Task.isCancelled && !isStopped() { throw CancellationError() }
            try conversation.writeSummary(summary, provider: providerID, model: model, stopped: cut)
        }
    }

    /// Streams the answer to `question`, sent with the transcript (or its BM25 excerpts on small contexts), the saved
    /// `summary` and the last `historyLimit` messages of `history` (0 or less = all of them); then appends the
    /// question and the answer to `chat.json`. `providerID` is saved with the answer. `language` is the
    /// response-language setting (`"auto"` = the question's).
    public nonisolated func ask(
        question: String, conversation: Conversation, transcript: Transcript, summary: String?,
        history: [ChatLog.Message], historyLimit: Int, provider: any LLMProvider, providerID: String, model: String,
        language: String, jobID: UUID = UUID()
    ) -> AsyncThrowingStream<Update, Error> {
        let asked = Date()
        return job(id: jobID, provider: provider, model: model) { [self] backend, yield, isStopped in
            let turns = Self.turns(history, limit: historyLimit)
            let language = Self.answerLanguage(setting: language, provider: provider)
            let context = await provider.contextWindow(model: model)
            let excerpts = TranscriptChunker.questionExcerpts(
                conversation: conversation, transcript: transcript, question: question, summary: summary,
                contextWindow: context, history: turns, language: language)
            let messages = PromptBuilder.questionMessages(
                conversation: conversation, transcript: transcript, excerpts: excerpts, summary: summary,
                question: question, history: turns, language: language)
            let answer: String
            do {
                answer = try await Self.answer(messages, backend: backend, model: model, context: context, yield: yield)
            } catch let interrupted as Interrupted where isStopped() && !interrupted.text.isEmpty {
                answer = interrupted.text
            }
            if Task.isCancelled && !isStopped() { throw CancellationError() }
            try await appendChat([
                ChatLog.Message(role: .user, text: question, timestamp: asked),
                ChatLog.Message(role: .assistant, text: answer, provider: providerID, model: model),
            ], folder: conversation.folder)
        }
    }

    /// Streams the answer to a question across the library, sent with the numbered `sources` and the last
    /// `historyLimit` messages of `history` (0 or less = all of them); then appends the question and the answer, with
    /// its `references`, to `folder`'s `chat.json`. `providerID` and `language` as in `ask`.
    public nonisolated func askLibrary(
        question: String, sources: [PromptBuilder.LibrarySource], references: [ChatLog.Reference],
        history: [ChatLog.Message], historyLimit: Int, provider: any LLMProvider, providerID: String, model: String,
        language: String, folder: URL, jobID: UUID = UUID()
    ) -> AsyncThrowingStream<Update, Error> {
        let asked = Date()
        return job(id: jobID, provider: provider, model: model) { [self] backend, yield, isStopped in
            let messages = PromptBuilder.libraryQuestionMessages(
                question: question, sources: sources, history: Self.turns(history, limit: historyLimit),
                language: Self.answerLanguage(setting: language, provider: provider))
            let context = await provider.contextWindow(model: model)
            let answer: String
            do {
                answer = try await Self.answer(messages, backend: backend, model: model, context: context, yield: yield)
            } catch let interrupted as Interrupted where isStopped() && !interrupted.text.isEmpty {
                answer = interrupted.text
            }
            if Task.isCancelled && !isStopped() { throw CancellationError() }
            // The global chat's folder does not exist before its first question.
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try await appendChat([
                ChatLog.Message(role: .user, text: question, timestamp: asked),
                ChatLog.Message(
                    role: .assistant, text: answer, provider: providerID, model: model, references: references),
            ], folder: folder)
        }
    }

    /// Streams a catch-up while recording: `messages` (`PromptBuilder.catchUpMessages` of `plan`), then appends a
    /// `CatchUp` covering `plan` to `folder`'s `catchups.json`, also when stopped with some text (`stopped`).
    /// `providerID` is saved with it and `elapsed` (seconds of recording when it was asked for) too. Fails at once
    /// with an in-app model (`LocalLLMProvider`): the live transcriber holds the model gate for the whole recording.
    public nonisolated func catchUp(
        plan: CatchUpPlan, messages: [LLMMessage], provider: any LLMProvider, providerID: String, model: String,
        elapsed: TimeInterval, folder: URL, jobID: UUID = UUID()
    ) -> AsyncThrowingStream<Update, Error> {
        guard !(provider is any LocalLLMProvider) else {
            return AsyncThrowingStream {
                $0.finish(throwing: LLMError.provider("A catch-up needs a provider outside the app."))
            }
        }
        let asked = Date()
        return job(id: jobID, provider: provider, model: model) { [self] backend, yield, isStopped in
            let context = await provider.contextWindow(model: model)
            let text: String
            var cut = false
            do {
                text = try await Self.answer(messages, backend: backend, model: model, context: context, yield: yield)
            } catch let interrupted as Interrupted where isStopped() && !interrupted.text.isEmpty {
                (text, cut) = (interrupted.text, true)
            }
            if Task.isCancelled && !isStopped() { throw CancellationError() }
            try await appendCatchUp(CatchUp(
                createdAt: asked, elapsed: elapsed, throughLineID: plan.throughLineID, throughTime: plan.throughTime,
                fullPass: plan.fullPass, text: text, provider: providerID, model: model, stopped: cut), folder: folder)
        }
    }

    /// The last `limit` messages of `history` (0 or less = all of them), starting with a question: some APIs reject a
    /// conversation whose first turn is an answer.
    private static func turns(_ history: [ChatLog.Message], limit: Int) -> [LLMMessage] {
        let recent = limit > 0 ? history.suffix(limit) : history[...]
        return recent.drop { $0.role != .user }.map {
            LLMMessage(role: $0.role == .user ? .user : .assistant, content: $0.text)
        }
    }

    /// The response-language `setting`, English when Foundation Models cannot write it.
    private static func answerLanguage(setting: String, provider: any LLMProvider) -> OutputLanguage? {
        var language = OutputLanguage.answer(setting: setting)
        if #available(macOS 26, *), let foundation = provider as? AppleFoundationModelProvider,
           let asked = language, !foundation.supports(language: asked.code) {
            language = asked.englishFallback()
        }
        return language
    }

    /// A short title for `chat`, from its questions: one `.text` update with the whole cleaned title (none when the
    /// model gave nothing usable), no text streamed before it; an in-app model's job sends its status lines first.
    public nonisolated func title(
        chat: [ChatLog.Message], provider: any LLMProvider, model: String, jobID: UUID = UUID()
    ) -> AsyncThrowingStream<Update, Error> {
        job(id: jobID, provider: provider, model: model) { backend, yield, _ in
            let context = await provider.contextWindow(model: model)
            let answer = try await Self.answer(
                PromptBuilder.titleMessages(chat: chat), backend: backend, model: model, context: context)
            let title = Self.titleLine(answer)
            if !title.isEmpty { yield(.text(title)) }
        }
    }

    /// The first non-empty line of a title answer, without surrounding quotes or Markdown marks; at most 60 characters.
    static func titleLine(_ answer: String) -> String {
        let marks = CharacterSet(charactersIn: "*#\"'“”‘’").union(.whitespacesAndNewlines)
        let line = answer.split(whereSeparator: \.isNewline).lazy
            .map { $0.trimmingCharacters(in: marks) }.first { !$0.isEmpty } ?? ""
        return String(line.prefix(60)).trimmingCharacters(in: .whitespaces)
    }

    /// Serialized here so two answers finishing together cannot drop each other's messages.
    private func appendChat(_ messages: [ChatLog.Message], folder: URL) throws {
        var log = ChatLog.read(from: folder)
        log.messages += messages
        try log.write(to: folder)
    }

    /// Serialized here so two catch-ups finishing together cannot drop each other; an unreadable file is not replaced.
    private func appendCatchUp(_ catchUp: CatchUp, folder: URL) throws {
        var log = try CatchUpLog.load(from: folder)
        log.append(catchUp)
        try log.save(to: folder)
    }

    /// Runs `work` as job `id` in a task that the stream's end cancels, and `stop()` or `stop(id)` too (`work`'s
    /// `isStopped` then says so and the stream finishes without an error). An in-app model is loaded inside the gate
    /// for the whole job and unloaded before the gate is released, also on error and cancellation.
    private nonisolated func job(
        id: UUID, provider: any LLMProvider, model: String,
        _ work: @escaping @Sendable (Backend, @escaping @Sendable (Update) -> Void, @escaping @Sendable () -> Bool)
            async throws -> Void
    ) -> AsyncThrowingStream<Update, Error> {
        let (gate, jobs) = (gate, jobs)
        let isStopped: @Sendable () -> Bool = { jobs.stopped.withLock { $0.contains(id) } }
        return AsyncThrowingStream { continuation in
            // Registered under the lock: the task's own removal waits for it, also when the job ends at once.
            let task = jobs.running.withLock { running in
                let task = Task(priority: .utility) {
                    defer {
                        jobs.running.withLock { $0[id] = nil }
                        jobs.stopped.withLock { _ = $0.remove(id) }
                    }
                    do {
                        let yield: @Sendable (Update) -> Void = { continuation.yield($0) }
                        if let local = provider as? any LocalLLMProvider {
                            // A recording transcriber holds the gate for the whole recording; a transcription for its run.
                            yield(.status(Self.waitingStatus))
                            try await gate.withExclusiveModel(owner: .llm) {
                                yield(.status(Self.loadingStatus))
                                try await local.withLoadedModel(model: model) {
                                    try await work(.local($0), yield, isStopped)
                                }
                            }
                        } else {
                            try await work(.remote(provider), yield, isStopped)
                        }
                        continuation.finish()
                    } catch where isStopped() {
                        continuation.finish()
                    } catch {
                        continuation.finish(throwing: error)
                    }
                }
                running[id] = task
                return task
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    // MARK: Summary

    private static func summary(
        conversation: Conversation, transcript: Transcript, provider: any LLMProvider, backend: Backend, model: String,
        language setting: String, yield: @escaping @Sendable (Update) -> Void
    ) async throws -> String {
        guard !transcript.segments.isEmpty else { throw LLMError.provider("The transcript is empty.") }
        var language = OutputLanguage.resolve(setting: setting, transcript: transcript)
        if #available(macOS 26, *), let foundation = provider as? AppleFoundationModelProvider,
           !foundation.supports(language: language.code) {
            language = language.englishFallback()
        }
        let context = await provider.contextWindow(model: model)
        let plan = TranscriptChunker.summaryPlan(
            conversation: conversation, transcript: transcript, language: language, contextWindow: context)
        guard case .mapReduce(let chunks) = plan else {
            let messages = PromptBuilder.summaryMessages(conversation: conversation, transcript: transcript, language: language)
            return try await answer(messages, backend: backend, model: model, context: context, yield: yield)
        }
        var notes: [String] = []
        for (index, chunk) in chunks.enumerated() {
            yield(.status("Reading part \(index + 1) of \(chunks.count)…"))
            let messages = PromptBuilder.mapMessages(
                conversation: conversation, transcript: transcript, chunk: chunk, part: index + 1, count: chunks.count,
                previousSummary: notes.last.map(summaryLine), language: language)
            notes.append(try await answer(messages, backend: backend, model: model, context: context))
        }
        while true {
            let groups = TranscriptChunker.reduceGroups(
                notes: notes, conversation: conversation, transcript: transcript, language: language,
                contextWindow: context)
            if groups.count <= 1 {
                yield(.status("Writing the summary…"))
                let messages = PromptBuilder.reduceMessages(
                    conversation: conversation, transcript: transcript, notes: groups.first ?? [], language: language)
                return try await answer(messages, backend: backend, model: model, context: context, yield: yield)
            }
            var merged: [String] = []
            for (index, group) in groups.enumerated() {
                yield(.status("Merging notes \(index + 1) of \(groups.count)…"))
                let messages = PromptBuilder.reduceMessages(
                    conversation: conversation, transcript: transcript, notes: group, language: language)
                merged.append(try await answer(messages, backend: backend, model: model, context: context))
            }
            notes = merged
        }
    }

    /// The map note's "Summary:" line (the next chunk's context), the text after it when the line has nothing else
    /// (`**Summary:**`), else the note's start; at most 800 UTF-8 bytes, the room `TranscriptChunker` keeps for it.
    static func summaryLine(_ note: String) -> String {
        let marks = CharacterSet(charactersIn: "*# ").union(.whitespacesAndNewlines)
        var text = note.trimmingCharacters(in: marks)
        if let label = note.range(of: "summary:", options: .caseInsensitive) {
            let rest = note[label.upperBound...]
            let line = rest.prefix { $0 != "\n" }.trimmingCharacters(in: marks)
            let after = rest.trimmingCharacters(in: marks)
            if !line.isEmpty {
                text = line
            } else if !after.isEmpty {
                text = after
            }
        }
        var cut = ""
        var bytes = 0
        for character in text {
            bytes += character.utf8.count
            guard bytes <= TranscriptChunker.previousSummaryAllowance.utf8.count else { break }
            cut.append(character)
        }
        return cut
    }

    // MARK: Streaming

    /// The whole answer to `messages`, passing each piece to `yield` when set. Throws `Interrupted` when the job was
    /// cancelled (a cancelled stream or generation just ends), with the text so far when it was streamed, and an
    /// error for an empty answer. An in-app model's returned text is the answer, even when its streamed pieces
    /// missed some.
    private static func answer(
        _ messages: [LLMMessage], backend: Backend, model: String, context: Int,
        yield: (@Sendable (Update) -> Void)? = nil
    ) async throws -> String {
        let maxTokens = maxTokens(context: context)
        var text = ""
        switch backend {
        case .local(let generator):
            text = try await generator.generate(messages: messages, maxTokens: maxTokens) { yield?(.text($0)) }
        case .remote(let provider):
            do {
                for try await piece in provider.stream(messages: messages, model: model, maxTokens: maxTokens) {
                    text += piece
                    yield?(.text(piece))
                }
            } catch where Task.isCancelled {}
        }
        let answer = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if Task.isCancelled { throw Interrupted(text: yield == nil ? "" : answer) }
        guard !answer.isEmpty else { throw LLMError.provider("The model returned an empty answer.") }
        return answer
    }

    /// The 20% of the context `TranscriptChunker` keeps for the answer, from 512 to 8192 tokens.
    static func maxTokens(context: Int) -> Int {
        min(max(Int(Double(context) * TranscriptChunker.answerReserve), 512), 8_192)
    }
}

/// `[mm:ss]` and `[h:mm:ss]` transcript citations in model answers (minutes may pass 59, as `PromptBuilder.timestamp`
/// writes them).
public enum TranscriptCitation {
    static var pattern: Regex<(Substring, Substring, Substring, Substring?)> {
        /\[(\d{1,3}):(\d{2})(?::(\d{2}))?\](?!\()/
    }

    /// Seconds into the recording of `"[01:05]"`, `"[1:02:03]"` or `"[75:30]"`; nil when it is not a citation.
    public static func seconds(_ citation: String) -> Double? {
        citation.wholeMatch(of: pattern).flatMap(seconds)
    }

    /// `text` with every citation turned into a Markdown link to `<scheme>:<seconds>`; citations already followed by
    /// a link target are left alone.
    public static func linked(_ text: String, scheme: String) -> String {
        text.replacing(pattern) { match in
            guard let seconds = seconds(match) else { return String(match.output.0) }
            return "\(match.output.0)(\(scheme):\(Int(seconds)))"
        }
    }

    private static func seconds(_ match: Regex<(Substring, Substring, Substring, Substring?)>.Match) -> Double? {
        let (_, first, second, third) = match.output
        guard let a = Int(first), let b = Int(second) else { return nil }
        if let third {
            guard let c = Int(third), b < 60, c < 60 else { return nil }
            return Double(a * 3_600 + b * 60 + c)
        }
        guard b < 60 else { return nil }
        return Double(a * 60 + b)
    }
}

/// `[n mm:ss]`, `[n h:mm:ss]` (also `[n, mm:ss]`) and `[n]` source citations in library-chat answers, `n` being a
/// `ChatLog.Reference` number.
public enum LibraryCitation {
    public static let scheme = "echodiar-ref"

    /// An existing Markdown link (kept whole), or a citation not followed by a link target.
    static var pattern: Regex<(Substring, Substring?, Substring?, Substring?)> {
        /(\[(?:[^\[\]\n]|\[[^\[\]\n]*\])*\]\([^)\s]*\))|\[(\d{1,3})(?:,? +(\d{1,3}:\d{2}(?::\d{2})?))?\](?!\()/
    }

    /// `text` with every citation of a known source turned into a Markdown link to `echodiar-ref:<uuid>:<seconds>`
    /// (`echodiar-ref:<uuid>` for `[n]`), as `TranscriptCitation.linked` does; citations of an unknown source and
    /// text inside Markdown links are left alone.
    public static func linked(_ text: String, references: [ChatLog.Reference]) -> String {
        text.replacing(pattern) { match in
            let (whole, link, number, time) = match.output
            guard link == nil, let number, let n = Int(number),
                  let reference = references.first(where: { $0.number == n }) else { return String(whole) }
            var target = "\(scheme):\(reference.conversationID.uuidString)"
            if let time {
                guard let seconds = TranscriptCitation.seconds("[\(time)]") else { return String(whole) }
                target += ":\(Int(seconds))"
            }
            return "\(whole)(\(target))"
        }
    }

    /// The conversation and the moment (nil for a summary citation) of an `echodiar-ref:<uuid>[:<seconds>]` link; nil
    /// when it is not one, or its seconds are not within 0...86 400.
    public static func parse(_ url: URL) -> (id: UUID, seconds: Double?)? {
        guard url.scheme?.lowercased() == scheme else { return nil }
        let parts = url.absoluteString.dropFirst(scheme.count + 1).split(separator: ":", omittingEmptySubsequences: false)
        guard (1...2).contains(parts.count), let id = UUID(uuidString: String(parts[0])) else { return nil }
        guard parts.count == 2 else { return (id, nil) }
        guard let seconds = Double(parts[1]), seconds.isFinite, (0...86_400).contains(seconds) else { return nil }
        return (id, seconds)
    }
}
