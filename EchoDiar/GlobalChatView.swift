import AppKit
import EchoAI
import EchoCore
import SwiftUI

/// The chat across the library (`AppModel.globalChatOpen`): questions answered from the indexed conversations, each
/// answer with its numbered sources under it. `[n mm:ss]` / `[n]` citations and the sources open the conversation at
/// that moment or its summary, the chat staying beside it. Laid out like the Chat tab (`AIPanelView`): the messages in
/// a card, then a bar with the menus, "Start a fresh chat", History and the question; the menus take two rows when the
/// pane is narrow (the 300 pt sidebar of the wide layout). With RAG off, an intro and the way to Settings › RAG.
/// Padded like `TranscriptView`'s panes, so it fills its slot as it is.
struct GlobalChatView: View {
    let model: AppModel
    @State private var chat: [ChatLog.Message] = []
    /// The open chat's title, when it was resumed from the history with one.
    @State private var chatTitle: String?
    @State private var question = ""
    @FocusState private var questionFocused: Bool
    @State private var toast: String?
    /// A web link from model output, opened only once its destination is confirmed.
    @State private var pendingLink: URL?
    /// The library chat's earlier chats, newest first.
    @State private var archives: [ChatArchive.Entry] = []
    /// The History panel is open.
    @State private var showingHistory = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private static let chatEnd = "chatEnd"
    /// The height of a one-line question field.
    private static let inputHeight = BottomBarButtonStyle.height
    /// The menu row, above the question: a bit smaller.
    private static let chatMenuHeight: CGFloat = 28
    /// The model badge and the round Copy under an answer.
    private static let answerFooterHeight: CGFloat = 22

    var body: some View {
        VStack(spacing: 10) {
            if model.settings.ragEnabled {
                chatPane
                errorText
                bottomBar
            } else {
                intro
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .overlay(alignment: .bottom) { toastView }
        .environment(\.openURL, OpenURLAction(handler: openLink))
        .modifier(WebLinkConfirmation(link: $pendingLink))
        .task { model.refreshInstalledModels() }
        // Opening the chat (or turning RAG on) puts the cursor in the question.
        .task(id: model.settings.ragEnabled) {
            guard model.settings.ragEnabled else { return }
            try? await Task.sleep(for: .milliseconds(50))
            if !Task.isCancelled { questionFocused = true }
        }
        // An answer saved, Start fresh, Resume and a new title.
        .task(id: LoadKey(jobsEnded: model.aiJobsEnded, changes: model.chatHistoryChanges)) { await loadChat() }
    }

    private struct LoadKey: Equatable {
        let jobsEnded: Int
        let changes: Int
    }

    // MARK: - RAG off

    /// What the chat does, and the way to turn it on.
    private var intro: some View {
        VStack(spacing: 12) {
            Image(systemName: "sparkles")
                .font(.system(size: 28))
                .foregroundStyle(.secondary)
            Text("Ask across conversations").font(.headline)
            Text("Ask a question about all your conversations: the answer comes from the passages that match it, with links to the moments it cites.")
                .foregroundStyle(.secondary)
            Text("It needs an index of your transcripts, built on this Mac by default.")
                .font(.callout)
                .foregroundStyle(.secondary)
            Button("Turn on in Settings › RAG") { model.openSettings(tab: .rag) }
                .buttonStyle(BottomBarButtonStyle(prominent: true))
                .padding(.top, 4)
        }
        .multilineTextAlignment(.center)
        .fixedSize(horizontal: false, vertical: true)
        .frame(maxWidth: 360)
        .padding(20)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .modifier(GlassCard(cornerRadius: 20))
    }

    // MARK: - Chat

    /// The messages, or a hint with example questions while there are none.
    private var chatPane: some View {
        Group {
            if chat.isEmpty && libraryJob == nil {
                emptyHint
            } else {
                messages
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .modifier(GlassCard(cornerRadius: 20))
        .overlay(alignment: .trailing) {
            if showingHistory {
                historyPanel
                    .transition(reduceMotion ? .opacity : .move(edge: .trailing).combined(with: .opacity))
            }
        }
    }

    private var emptyHint: some View {
        VStack(spacing: 10) {
            Text("Ask a question about all your conversations below.")
            VStack(spacing: 4) {
                Text("For example:")
                Text("What did we decide about the launch date?").italic()
                Text("Who mentioned the budget, and when?").italic()
                Text("What are my open action items?").italic()
            }
            .font(.callout)
        }
        .foregroundStyle(.secondary)
        .multilineTextAlignment(.center)
        .padding(20)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var messages: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    ForEach(Array(chat.enumerated()), id: \.offset) { _, message in
                        messageView(message.role, text: message.text, model: message.model,
                                    references: message.references ?? [])
                    }
                    if let job = libraryJob, case .question(let asked) = job.kind {
                        messageView(.user, text: asked, model: nil, references: [])
                        if job.text.isEmpty {
                            HStack(spacing: 6) {
                                ProgressView().controlSize(.small)
                                if let status = job.status {
                                    Text(status).foregroundStyle(.secondary)
                                }
                            }
                        } else {
                            // The sources are saved with the answer: its citations link once it is saved.
                            messageView(.assistant, text: job.text, model: currentModel, references: [],
                                        isStreaming: true)
                        }
                    }
                    Color.clear.frame(height: 1).id(Self.chatEnd)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 16)
            }
            .onAppear { proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: chat.count) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: libraryJob?.text.count) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
        }
    }

    /// `references` is never nil here: an answer without sources keeps its `[n]` and `[mm:ss]` as plain text.
    @ViewBuilder private func messageView(
        _ role: ChatLog.Message.Role, text: String, model: String?, references: [ChatLog.Reference],
        isStreaming: Bool = false
    ) -> some View {
        if role == .user {
            Text(text)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(Palette.hoverFill, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                .frame(maxWidth: .infinity, alignment: .trailing)
        } else {
            VStack(alignment: .leading, spacing: 6) {
                MarkdownText(text: text, references: references)
                if !references.isEmpty { sources(references) }
                HStack(spacing: 6) {
                    if let model, !model.isEmpty {
                        Text(model)
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .padding(.horizontal, 8)
                            .frame(height: Self.answerFooterHeight)
                            .background(Capsule().fill(Palette.inputSurface))
                    }
                    if !isStreaming {
                        Button { copyAnswer(text) } label: {
                            Label("Copy", systemImage: "square.on.square").labelStyle(.iconOnly).font(.caption2)
                        }
                        .buttonStyle(BottomBarButtonStyle(height: Self.answerFooterHeight, horizontalPadding: 0))
                        .help("Copy the answer")
                    }
                }
            }
        }
    }

    /// The answer's sources, "n · title · date": a click opens the conversation's summary; a deleted one is dimmed.
    private func sources(_ references: [ChatLog.Reference]) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("Sources")
                .font(.caption)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 8)
                .padding(.bottom, 2)
            ForEach(references, id: \.number) { reference in sourceRow(reference) }
        }
    }

    private func sourceRow(_ reference: ChatLog.Reference) -> some View {
        let conversation = model.conversations.first { $0.id == reference.conversationID }
        return Button {
            withAnimation(.snappy) { model.openReference(id: reference.conversationID, seconds: nil) }
        } label: {
            HStack(spacing: 0) {
                // Titles may come from a model: plain text, never Markdown.
                Text(verbatim: "\(reference.number) · \(conversation?.title ?? reference.title)")
                    .lineLimit(1)
                    .truncationMode(.tail)
                if let date = conversation?.createdAt {
                    Text(verbatim: " · \(date.formatted(date: .abbreviated, time: .omitted))")
                        .foregroundStyle(.secondary)
                        .fixedSize()
                }
                Spacer(minLength: 0)
            }
            .font(.caption)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
        }
        .buttonStyle(HistoryRowStyle())
        .disabled(conversation == nil)
        .help(conversation == nil ? "This conversation was deleted" : "Open this conversation")
    }

    // MARK: - History

    /// The open chat, then the earlier ones, newest first: a click makes one the open chat (the open one goes to the
    /// history), not while an answer streams.
    private var historyPanel: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("History").font(.headline)
                Spacer(minLength: 0)
                Button(action: closeHistory) {
                    Label("Close", systemImage: "xmark").labelStyle(.iconOnly)
                }
                .buttonStyle(RowIconButtonStyle())
                .keyboardShortcut(.cancelAction)
                .help("Close the history")
            }
            .padding(.horizontal, 14)
            .padding(.top, 12)
            ScrollView {
                VStack(spacing: 2) {
                    currentChatRow
                    ForEach(archives) { entry in historyRow(entry) }
                    if archives.isEmpty {
                        Text("No earlier chats")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .padding(.top, 8)
                    }
                }
                .padding(6)
            }
            .disabled(libraryJob != nil)
        }
        // At most the Chat tab's width: the 300 pt sidebar leaves it less.
        .frame(maxWidth: 260)
        .frame(maxHeight: .infinity, alignment: .top)
        .modifier(GlassCard(cornerRadius: 14, raised: true))
        .padding(8)
    }

    /// The open chat, first and highlighted: its title, else its first question; "New chat" while it is empty.
    private var currentChatRow: some View {
        VStack(alignment: .leading, spacing: 2) {
            // The title may come from a model: plain text, never Markdown.
            Text(chatTitle ?? chat.first { $0.role == .user }?.text ?? String(localized: "New chat")).lineLimit(2)
            (chat.isEmpty ? Text("Current") : Text("Current") + Text(verbatim: " · ") + Text("\(chat.count) messages"))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Palette.hoverFill))
    }

    private func historyRow(_ entry: ChatArchive.Entry) -> some View {
        Button {
            model.resumeLibraryChat(entry)
            closeHistory()
        } label: {
            VStack(alignment: .leading, spacing: 2) {
                // The title may come from a model: plain text, never Markdown.
                Text(entry.title).lineLimit(2)
                Text("\(entry.archivedAt.formatted(date: .abbreviated, time: .shortened)) · \(entry.messageCount) messages")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 8)
            .padding(.vertical, 6)
        }
        .buttonStyle(HistoryRowStyle())
    }

    private func closeHistory() {
        withAnimation(.snappy(duration: 0.25)) { showingHistory = false }
    }

    @ViewBuilder private var errorText: some View {
        if let error = model.aiError, error.conversationID == nil {
            Text(error.message)
                .font(.callout)
                .foregroundStyle(.red)
                .lineLimit(4)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    // MARK: - Bottom bar

    /// A job of a conversation and the index's progress, the menus, "Start a fresh chat" and History (one row, or two
    /// when narrow), the question with Send, then the missing-model warning.
    private var bottomBar: some View {
        VStack(alignment: .leading, spacing: 8) {
            busyElsewhere
            indexingLine
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 8) {
                    menus
                    chatButtons
                    Spacer(minLength: 0)
                }
                VStack(alignment: .leading, spacing: 8) {
                    HStack(spacing: 8) {
                        menus
                        Spacer(minLength: 0)
                    }
                    HStack(spacing: 8) {
                        chatButtons
                        Spacer(minLength: 0)
                    }
                }
            }
            questionRow
            AISetupHints(model: model)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(GlassCard(cornerRadius: 20))
    }

    @ViewBuilder private var menus: some View {
        AIModelMenu(model: model, height: Self.chatMenuHeight)
        AILanguageMenu(model: model, height: Self.chatMenuHeight)
    }

    @ViewBuilder private var chatButtons: some View {
        freshChatButton
        Button {
            withAnimation(.snappy(duration: 0.25)) { showingHistory.toggle() }
        } label: {
            Label("History", systemImage: "clock.arrow.circlepath").labelStyle(.iconOnly)
        }
        .buttonStyle(BottomBarButtonStyle(height: Self.chatMenuHeight, horizontalPadding: 0))
        .help("Earlier chats across conversations")
    }

    /// The question (1 to 6 lines; Return sends, Shift+Return starts a new line) and Send (Stop while answering), at
    /// the bottom of the field: centered with it while it has one line.
    private var questionRow: some View {
        HStack(alignment: .bottom, spacing: 8) {
            TextField("Ask about your conversations", text: $question, axis: .vertical)
                .textFieldStyle(.plain)
                .focused($questionFocused)
                .lineLimit(1...6)
                .onSubmit(ask)
                .onKeyPress(.return, phases: .down) { press in
                    guard press.modifiers.contains(.shift) else { return .ignored }
                    NSApp.sendAction(#selector(NSResponder.insertNewlineIgnoringFieldEditor(_:)), to: nil, from: nil)
                    return .handled
                }
                // The field has the focus whenever the chat shows, so the window's Esc (go back) never gets the key:
                // an empty field passes it on. With a question typed, Esc stays the field's own.
                .onKeyPress(.escape) {
                    guard question.isEmpty, !showingHistory else { return .ignored }
                    model.goBack()
                    return .handled
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
                .frame(maxWidth: .infinity, minHeight: Self.inputHeight, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: Self.inputHeight / 2, style: .continuous)
                    .fill(Palette.inputSurface))
            if libraryJob != nil {
                Button(action: model.stopAIJob) {
                    Label("Stop", systemImage: "stop.fill").labelStyle(.iconOnly)
                }
                .buttonStyle(BottomBarButtonStyle(prominent: true, horizontalPadding: 0))
                .help("Stop the answer")
            } else {
                Button("Send", action: ask)
                    .buttonStyle(BottomBarButtonStyle(prominent: true))
                    .disabled(!canAsk || question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
    }

    /// Archives the chat so the next question starts without history; not while it is empty or an answer streams.
    private var freshChatButton: some View {
        Button { model.startFreshLibraryChat() } label: {
            Label("Start a fresh chat", systemImage: "plus.bubble").labelStyle(.iconOnly)
        }
        .buttonStyle(BottomBarButtonStyle(height: Self.chatMenuHeight, horizontalPadding: 0))
        .help("Start a fresh chat")
        .disabled(chat.isEmpty || libraryJob != nil)
    }

    /// A job of a conversation: it blocks asking until it ends, so it says what runs and can be stopped from here.
    @ViewBuilder private var busyElsewhere: some View {
        if let job = model.aiJob, let id = job.conversationID {
            let title = model.conversations.first { $0.id == id }?.title ?? "another conversation"
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text(job.kind == .summary ? "Summarizing \(title)…" : "Answering about \(title)…")
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: 0)
                Button("Stop", action: model.stopAIJob)
                    .modifier(CapsuleButton())
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
    }

    /// While the index is built or still empty: how far it is, or why it waits. Asking stays allowed (the answer uses
    /// what is indexed so far).
    @ViewBuilder private var indexingLine: some View {
        let status = model.ragStatus
        // A missing embedder or confirmation stops the pass before it counts the conversations (`total` stays 0).
        let waiting = status.indexed == 0 && (status.message != nil || status.needsConsent || status.total > 0)
        if (status.running && status.indexed < status.total) || (!status.running && waiting) {
            HStack(spacing: 6) {
                if status.running {
                    ProgressView().controlSize(.small)
                    Text(status.paused ? "Indexing paused at \(status.indexed) of \(status.total)"
                         : "Indexing \(status.indexed) of \(status.total)…")
                        .lineLimit(1)
                } else {
                    Label(status.needsConsent ? String(localized: "Confirm the embedding provider in Settings › RAG.")
                          : status.message ?? String(localized: "Your conversations are not indexed yet."),
                          systemImage: "exclamationmark.triangle")
                        .lineLimit(2)
                    Spacer(minLength: 0)
                    Button("Settings") { model.openSettings(tab: .rag) }
                        .modifier(CapsuleButton())
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
    }

    @ViewBuilder private var toastView: some View {
        if let toast {
            Text(toast)
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .modifier(GlassCard(cornerRadius: 14))
                .padding(.bottom, 110)
                .transition(.opacity)
        }
    }

    // MARK: - State

    /// The current provider's picked model ("" = a CLI's own default).
    private var currentModel: String? {
        model.currentAIProvider.map(model.aiModel(for:))
    }

    /// The library question running now.
    private var libraryJob: AppModel.AIJob? {
        guard let job = model.aiJob, job.conversationID == nil, job.kind != .summary else { return nil }
        return job
    }

    /// As `askLibrary`'s guard: one AI job at a time.
    private var canAsk: Bool {
        model.aiJob == nil && model.settings.ragEnabled && model.currentAIProvider != nil
    }

    private func loadChat() async {
        let folder = model.globalChatFolder
        let (log, entries) = await Task.detached(priority: .utility) {
            (ChatLog.read(from: folder), ChatArchive.entries(in: folder))
        }.value
        guard !Task.isCancelled else { return }
        (chat, chatTitle, archives) = (log.messages, log.title, entries)
    }

    private func ask() {
        let text = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard canAsk, !text.isEmpty else { return }
        model.askLibrary(text)
        question = ""
    }

    private func copyAnswer(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        withAnimation { toast = "Copied" }
        Task {
            try? await Task.sleep(for: .seconds(2))
            withAnimation { toast = nil }
        }
    }

    /// Source citations open their conversation (at the cited moment, or its summary) when it is still in the
    /// library; web links open in the browser once the alert showing their destination is confirmed (the link text is
    /// model output); anything else is dropped.
    private func openLink(_ url: URL) -> OpenURLAction.Result {
        if url.scheme?.lowercased() == LibraryCitation.scheme {
            guard let target = LibraryCitation.parse(url),
                  model.conversations.contains(where: { $0.id == target.id }) else { return .discarded }
            withAnimation(.snappy) { model.openReference(id: target.id, seconds: target.seconds) }
            return .handled
        }
        guard ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return .discarded }
        pendingLink = url
        return .handled
    }
}
