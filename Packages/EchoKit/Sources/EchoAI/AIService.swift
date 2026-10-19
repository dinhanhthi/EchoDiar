import EchoCore
import Foundation

/// Summaries and questions about one conversation (docs/research/llm-providers.md F). Each job streams its answer,
/// saves it in the conversation folder when it completes (`summary.md` + `hasSummary`, or two more `chat.json`
/// messages) and saves nothing when cancelled or failed. In-app models (`LocalLLMProvider`: Foundation Models, MLX)
/// run the whole job inside `ModelLoadGate` (owner `.llm`), loaded once and unloaded before the gate is released.
public actor AIService {
    public enum Update: Equatable, Sendable {
        /// What a long job is doing ("Reading part 2 of 5…"), before the answer starts.
        case status(String)
        /// The next piece of the answer.
        case text(String)
    }

    /// Earlier chat messages sent with a question.
    static let historyLimit = 4
    static let waitingStatus = "Waiting for live captions or transcription to finish…"
    static let loadingStatus = "Loading the model…"

    /// Where the answers of a job come from: a provider's stream, or an in-app model loaded for the whole job.
    enum Backend: Sendable {
        case remote(any LLMProvider)
        case local(any LocalLLMGenerator)
    }

    private let gate: ModelLoadGate

    public init(gate: ModelLoadGate) {
        self.gate = gate
    }

    /// Streams a new summary: one pass when the transcript fits, else map over the chunks and reduce the notes until
    /// one group is left; only the last reduce is streamed. `language` is the summary-language setting.
    public nonisolated func summarize(
        conversation: Conversation, transcript: Transcript, provider: any LLMProvider, model: String, language: String
    ) -> AsyncThrowingStream<Update, Error> {
        job(provider: provider, model: model) { backend, yield in
            let summary = try await Self.summary(
                conversation: conversation, transcript: transcript, provider: provider, backend: backend, model: model,
                language: language, yield: yield)
            try Task.checkCancellation()
            try conversation.writeSummary(summary)
        }
    }

    /// Streams the answer to `question`, sent with the transcript (or its BM25 excerpts on small contexts), the saved
    /// `summary` and the last messages of `history`; then appends the question and the answer to `chat.json`.
    /// `providerID` is saved with the answer.
    public nonisolated func ask(
        question: String, conversation: Conversation, transcript: Transcript, summary: String?,
        history: [ChatLog.Message], provider: any LLMProvider, providerID: String, model: String
    ) -> AsyncThrowingStream<Update, Error> {
        let asked = Date()
        return job(provider: provider, model: model) { [self] backend, yield in
            // Starts with a question: some APIs reject a conversation whose first turn is an answer.
            let turns = history.suffix(Self.historyLimit).drop { $0.role != .user }.map {
                LLMMessage(role: $0.role == .user ? .user : .assistant, content: $0.text)
            }
            let context = await provider.contextWindow(model: model)
            let excerpts = TranscriptChunker.questionExcerpts(
                conversation: conversation, transcript: transcript, question: question, summary: summary,
                contextWindow: context, history: turns)
            let messages = PromptBuilder.questionMessages(
                conversation: conversation, transcript: transcript, excerpts: excerpts, summary: summary,
                question: question, history: turns)
            let answer = try await Self.answer(messages, backend: backend, model: model, context: context, yield: yield)
            try Task.checkCancellation()
            try await appendChat([
                ChatLog.Message(role: .user, text: question, timestamp: asked),
                ChatLog.Message(role: .assistant, text: answer, provider: providerID, model: model),
            ], folder: conversation.folder)
        }
    }

    /// Serialized here so two answers finishing together cannot drop each other's messages.
    private func appendChat(_ messages: [ChatLog.Message], folder: URL) throws {
        var log = ChatLog.read(from: folder)
        log.messages += messages
        try log.write(to: folder)
    }

    /// Runs `work` in a task that the stream's end cancels. An in-app model is loaded inside the gate for the whole
    /// job and unloaded before the gate is released, also on error and cancellation.
    private nonisolated func job(
        provider: any LLMProvider, model: String,
        _ work: @escaping @Sendable (Backend, @escaping @Sendable (Update) -> Void) async throws -> Void
    ) -> AsyncThrowingStream<Update, Error> {
        let gate = gate
        return AsyncThrowingStream { continuation in
            let task = Task(priority: .utility) {
                do {
                    let yield: @Sendable (Update) -> Void = { continuation.yield($0) }
                    if let local = provider as? any LocalLLMProvider {
                        // Live captions hold the gate for the whole recording; a transcription for its run.
                        yield(.status(Self.waitingStatus))
                        try await gate.withExclusiveModel(owner: .llm) {
                            yield(.status(Self.loadingStatus))
                            try await local.withLoadedModel(model: model) { try await work(.local($0), yield) }
                        }
                    } else {
                        try await work(.remote(provider), yield)
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
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

    /// The whole answer to `messages`, passing each piece to `yield` when set. Throws `CancellationError` when the
    /// job was cancelled (a cancelled stream or generation just ends) and an error for an empty answer. An in-app
    /// model's returned text is the answer, even when its streamed pieces missed some.
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
            for try await piece in provider.stream(messages: messages, model: model, maxTokens: maxTokens) {
                text += piece
                yield?(.text(piece))
            }
        }
        try Task.checkCancellation()
        let answer = text.trimmingCharacters(in: .whitespacesAndNewlines)
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
