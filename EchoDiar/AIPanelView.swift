import AppKit
import EchoAI
import EchoCore
import SwiftUI

/// The Summary or Chat tab of the open conversation (`AppModel.detailTab`): Chat (questions with streamed answers) or
/// Summary (the kept summaries, the newest first with a menu for the earlier ones, Summarize / Re-summarize). The bar
/// at the bottom has menus for the model (every usable provider with its models) and the output language with the "i"
/// (where the data goes), then "Copy as prompt" and the tab's action; in Chat the menus sit above the question row,
/// with "Start a fresh chat" and History (the earlier chats, in a panel at the pane's trailing edge). `[mm:ss]`
/// citations open the transcript at that time.
struct AIPanelView: View {
    let conversation: Conversation
    let model: AppModel
    /// The pane shown: `TranscriptView`'s shown tab, which follows `model.detailTab` one update later.
    let tab: AppModel.DetailTab
    /// How Summary and Chat slide into each other (`TranscriptView`'s tab transition).
    let transition: AnyTransition
    @State private var transcript: Transcript?
    @State private var summary: String?
    /// Every kept summary, oldest first; `summary` is the newest.
    @State private var summaries: [SummaryVersion] = []
    /// The summary version shown; nil = the newest.
    @State private var shownVersion: Int?
    @State private var chat: [ChatLog.Message] = []
    /// The open chat's title, when it was resumed from the history with one.
    @State private var chatTitle: String?
    @State private var question = ""
    @FocusState private var questionFocused: Bool
    @State private var toast: String?
    /// Hides the toast 2 s after it shows; replaced by each new toast.
    @State private var toastTask: Task<Void, Never>?
    /// A web link from model output, opened only once its destination is confirmed.
    @State private var pendingLink: URL?
    /// The "i" popover about where the data goes is open.
    @State private var showingDataNotes = false
    /// The conversation's earlier chats, newest first.
    @State private var archives: [ChatArchive.Entry] = []
    /// The History panel is open.
    @State private var showingHistory = false
    /// The alert confirming that every earlier chat is deleted is open.
    @State private var confirmingClear = false
    /// The earlier chat under the pointer, which shows its Remove button.
    @State private var hoveredEntry: UUID?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private static let chatEnd = "chatEnd"
    /// The height of a one-line question field, and of every control of the bottom bar but Chat's menu row.
    private static let inputHeight = BottomBarButtonStyle.height
    /// Chat's menu row, above the question: a bit smaller.
    private static let chatMenuHeight: CGFloat = 28
    /// The model badge and the round Copy under an answer.
    private static let answerFooterHeight: CGFloat = 22

    var body: some View {
        VStack(spacing: 10) {
            ZStack {
                if tab == .chat {
                    chatPane
                        .transition(transition)
                } else {
                    summaryPane
                        .transition(transition)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .clipped()
            errorText
            bottomBar
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .overlay(alignment: .bottom) { toastView }
        .environment(\.openURL, OpenURLAction(handler: openLink))
        .modifier(WebLinkConfirmation(link: $pendingLink))
        .task { model.refreshInstalledModels() }
        // Opening Chat (a card's Ask AI, a tab switch) puts the cursor in the question, once the tab's slide has put
        // the field in the window.
        .task(id: model.detailTab) {
            guard model.detailTab == .chat else { return }
            try? await Task.sleep(for: .milliseconds(50))
            if !Task.isCancelled { questionFocused = true }
        }
        .task(id: LoadKey(id: conversation.id, hasTranscript: conversation.hasTranscript,
                          jobsEnded: model.aiJobsEnded)) { await load() }
        // Start fresh, Resume and a new title: the chat and the history only (`load()` resets the shown version).
        .task(id: HistoryKey(id: conversation.id, changes: model.chatHistoryChanges)) { await loadChat() }
        // A new conversation rebuilds the view (`MainView` gives it the conversation's id), which closes the panel too.
        .onChange(of: model.detailTab) { _, new in
            if new != .chat { showingHistory = false }
        }
    }

    private struct LoadKey: Equatable {
        let id: UUID
        let hasTranscript: Bool
        let jobsEnded: Int
    }

    private struct HistoryKey: Equatable {
        let id: UUID
        let changes: Int
    }

    // MARK: - Chat

    /// The messages, or a note in the middle while there are none.
    private var chatPane: some View {
        Group {
            if chat.isEmpty && questionJob == nil {
                Text(emptyNote ?? "Ask a question about this conversation below.")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(20)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
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

    private var messages: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    ForEach(Array(chat.enumerated()), id: \.offset) { _, message in
                        messageView(message.role, text: message.text, model: message.model)
                    }
                    if let job = questionJob, case .question(let asked) = job.kind {
                        messageView(.user, text: asked, model: nil)
                        if job.text.isEmpty {
                            HStack(spacing: 6) {
                                ProgressView().controlSize(.small)
                                if let status = job.status {
                                    Text(status).foregroundStyle(.secondary)
                                }
                            }
                        } else {
                            messageView(.assistant, text: job.text, model: currentModel, isStreaming: true)
                        }
                    }
                    Color.clear.frame(height: 1).id(Self.chatEnd)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 16)
            }
            .onAppear { proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: chat.count) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: questionJob?.text.count) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
        }
    }

    @ViewBuilder private func messageView(
        _ role: ChatLog.Message.Role, text: String, model: String?, isStreaming: Bool = false
    ) -> some View {
        if role == .user {
            Text(text)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(Palette.hoverFill, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                .frame(maxWidth: .infinity, alignment: .trailing)
        } else {
            VStack(alignment: .leading, spacing: 6) {
                MarkdownText(text: text)
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

    // MARK: - History

    /// The open chat, then the earlier ones, newest first: a click makes one the open chat (the open one goes to the
    /// history), not while an answer streams.
    private var historyPanel: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("History").font(.headline)
                Spacer(minLength: 0)
                Button { confirmingClear = true } label: {
                    Label("Delete all earlier chats", systemImage: "trash").labelStyle(.iconOnly)
                }
                .buttonStyle(RowIconButtonStyle())
                .disabled(archives.isEmpty || questionJob != nil)
                .help("Delete all earlier chats")
                .padding(.trailing, 6)
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
            .disabled(questionJob != nil)
        }
        .frame(width: 260)
        .frame(maxHeight: .infinity, alignment: .top)
        .modifier(GlassCard(cornerRadius: 14, raised: true))
        .padding(8)
        .alert("Delete all earlier chats?", isPresented: $confirmingClear) {
            Button("Delete", role: .destructive) { model.clearChats(of: conversation) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The open chat stays. This can't be undone.")
        }
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
            model.resumeChat(entry.id, of: conversation)
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
            .padding(.leading, 8)
            // Room for the Remove button.
            .padding(.trailing, 28)
            .padding(.vertical, 6)
        }
        .buttonStyle(HistoryRowStyle(hovered: hoveredEntry == entry.id))
        .overlay(alignment: .trailing) {
            if hoveredEntry == entry.id {
                Button { model.deleteChat(entry.id, of: conversation) } label: {
                    Label("Remove", systemImage: "trash").labelStyle(.iconOnly).font(.caption)
                }
                .buttonStyle(RowIconButtonStyle())
                .help("Remove this chat")
                .padding(.trailing, 10)
                .transition(.opacity)
            }
        }
        .onHover { hovering in
            withAnimation(Motion.hover) {
                if hovering {
                    hoveredEntry = entry.id
                } else if hoveredEntry == entry.id {
                    hoveredEntry = nil
                }
            }
        }
    }

    private func closeHistory() {
        withAnimation(Motion.standard) { showingHistory = false }
    }

    // MARK: - Summary

    /// The summary with the version menu pinned above it, or a note in the middle while there is none.
    private var summaryPane: some View {
        Group {
            if summaryJob == nil && summaries.isEmpty {
                Text(emptyNote ?? "No summary yet.")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(20)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                VStack(alignment: .leading, spacing: 0) {
                    if summaryJob == nil, let shown = shownSummary {
                        versionMenu(shown)
                            .padding(.horizontal, 14)
                            .padding(.top, 16)
                    }
                    ScrollView {
                        VStack(alignment: .leading, spacing: 8) {
                            if let job = summaryJob {
                                // Until the first text: a remote provider sends no status before it.
                                if job.text.isEmpty {
                                    HStack(spacing: 6) {
                                        ProgressView().controlSize(.small)
                                        Text(job.status ?? String(localized: "Writing the summary…"))
                                            .foregroundStyle(.secondary)
                                    }
                                } else {
                                    MarkdownText(text: job.text)
                                }
                            } else if let shown = shownSummary {
                                MarkdownText(text: shown.text)
                            }
                        }
                        .padding(.horizontal, 14)
                        // Below the version menu (shown while no summary is being written), its gap stays as it was.
                        .padding(.top, summaryJob == nil ? 10 : 16)
                        .padding(.bottom, 16)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .modifier(GlassCard(cornerRadius: 20))
    }

    private var shownSummary: SummaryVersion? {
        summaries.last { $0.number == shownVersion } ?? summaries.last
    }

    /// The kept summaries, newest first, each with its model, date and whether it was stopped.
    private func versionMenu(_ shown: SummaryVersion) -> some View {
        Menu {
            ForEach(summaries.reversed(), id: \.number) { version in
                Toggle(isOn: Binding(
                    get: { version.number == shown.number },
                    set: { if $0 { shownVersion = version.number } }
                )) {
                    Text("Version \(version.number)")
                    let details = versionDetails(version)
                    if !details.isEmpty { Text(details) }
                }
            }
        } label: {
            AIMenuLabel(title: ([String(localized: "Version \(shown.number)")] + [versionModel(shown)].compactMap { $0 })
                .joined(separator: " · "), systemImage: "clock.arrow.circlepath")
        }
        .modifier(CompactMenu(maxWidth: 260, height: Self.chatMenuHeight))
        .help("Earlier summaries of this conversation")
    }

    /// The model, else the provider's name, of a kept summary; nil for one saved before versions.
    private func versionModel(_ version: SummaryVersion) -> String? {
        if let id = version.model, !id.isEmpty { return id }
        guard let provider = version.provider else { return nil }
        return model.aiProviders.first { $0.id == provider }?.name ?? provider
    }

    private func versionDetails(_ version: SummaryVersion) -> String {
        [versionModel(version), version.createdAt?.formatted(date: .abbreviated, time: .shortened),
         version.stopped ? String(localized: "Stopped") : nil]
            .compactMap { $0 }
            .joined(separator: " · ")
    }

    /// Why nothing can run for this conversation.
    private var emptyNote: String? {
        conversation.hasTranscript ? nil : "This conversation has no transcript yet."
    }

    @ViewBuilder private var errorText: some View {
        if let error = model.aiError, error.conversationID == conversation.id {
            Text(error.message)
                .font(.callout)
                .foregroundStyle(.red)
                .lineLimit(4)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    // MARK: - Bottom bar

    /// Summary: one row with the model menu, the "i" (where the data goes) and the language menu, then "Copy as
    /// prompt" and the tab's action. Chat: the menus, the "i", "Start a fresh chat" and History (smaller), then the
    /// question with "Copy as prompt" and Send. Then the missing-model warning.
    private var bottomBar: some View {
        VStack(alignment: .leading, spacing: 8) {
            busyElsewhere
            if model.detailTab == .chat {
                HStack(spacing: 8) {
                    AIModelMenu(model: model, height: Self.chatMenuHeight)
                    AILanguageMenu(model: model, height: Self.chatMenuHeight)
                    dataNotesButton(height: Self.chatMenuHeight)
                    freshChatButton
                    Button {
                        withAnimation(Motion.standard) { showingHistory.toggle() }
                    } label: {
                        Label("History", systemImage: "clock.arrow.circlepath").labelStyle(.iconOnly)
                    }
                    .buttonStyle(BottomBarButtonStyle(height: Self.chatMenuHeight, horizontalPadding: 0))
                    .help("Earlier chats of this conversation")
                    Spacer(minLength: 0)
                }
                questionRow
            } else {
                HStack(spacing: 8) {
                    AIModelMenu(model: model, height: Self.inputHeight)
                    dataNotesButton(height: Self.inputHeight)
                    AILanguageMenu(model: model, height: Self.inputHeight)
                    Spacer(minLength: 0)
                    copyButton
                    if summaryJob != nil {
                        Button(action: model.stopAIJob) {
                            Label("Summarizing", systemImage: "stop.fill")
                        }
                        .buttonStyle(BottomBarButtonStyle(prominent: true))
                        .help("Stop the summary and keep what is written so far")
                    } else if summaries.isEmpty {
                        Button("Summarize") { start(.summary) }
                            .buttonStyle(BottomBarButtonStyle(prominent: true))
                            .disabled(!canRun)
                    } else {
                        Button("Re-summarize") { start(.summary) }
                            .buttonStyle(BottomBarButtonStyle(prominent: true))
                            .disabled(!canRun)
                            .help("Make a new summary; the earlier ones stay in the version menu")
                    }
                }
            }
            AISetupHints(model: model)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(GlassCard(cornerRadius: 20))
    }

    /// The question (1 to 6 lines; Return sends, Shift+Return starts a new line), "Copy as prompt" and Send
    /// (Cancel while answering), at the bottom of the field: centered with it while it has one line.
    private var questionRow: some View {
        HStack(alignment: .bottom, spacing: 8) {
            TextField("Ask about this conversation", text: $question, axis: .vertical)
                .textFieldStyle(.plain)
                .focused($questionFocused)
                .lineLimit(1...6)
                .onSubmit(ask)
                .onKeyPress(.return, phases: .down) { press in
                    guard press.modifiers.contains(.shift) else { return .ignored }
                    NSApp.sendAction(#selector(NSResponder.insertNewlineIgnoringFieldEditor(_:)), to: nil, from: nil)
                    return .handled
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
                .frame(maxWidth: .infinity, minHeight: Self.inputHeight, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: Self.inputHeight / 2, style: .continuous)
                    .fill(Palette.inputSurface))
            copyButton
            if questionJob != nil {
                Button(action: model.stopAIJob) {
                    Label("Stop", systemImage: "stop.fill").labelStyle(.iconOnly)
                }
                .buttonStyle(BottomBarButtonStyle(prominent: true, horizontalPadding: 0))
                .help("Stop the answer")
            } else {
                Button("Send", action: ask)
                    .buttonStyle(BottomBarButtonStyle(prominent: true))
                    .disabled(!canRun || question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
    }

    /// Archives the chat so the next question starts without history; not while it is empty or an answer streams.
    private var freshChatButton: some View {
        Button { model.startFreshChat(conversation) } label: {
            Label("Start a fresh chat", systemImage: "plus.bubble").labelStyle(.iconOnly)
        }
        .buttonStyle(BottomBarButtonStyle(height: Self.chatMenuHeight, horizontalPadding: 0))
        .help("Start a fresh chat")
        .disabled(chat.isEmpty || questionJob != nil)
    }

    /// A job of another conversation or of the other tab: it blocks Ask and Generate until it ends, so it says what
    /// runs and can be stopped from here.
    @ViewBuilder private var busyElsewhere: some View {
        if let job = model.aiJob, (model.detailTab == .chat ? questionJob : summaryJob) == nil {
            let title = model.conversations.first { $0.id == job.conversationID }?.title ?? "another conversation"
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                (job.conversationID == nil ? Text("Answering a question about your conversations…")
                    : Text(job.kind == .summary ? "Summarizing \(title)…" : "Answering about \(title)…"))
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

    /// The "i": where the data goes with the current provider, in a popover; only when there is something to say.
    @ViewBuilder private func dataNotesButton(height: CGFloat) -> some View {
        if hasDataNotes {
            Button { showingDataNotes.toggle() } label: {
                Image(systemName: "info.circle")
            }
            .buttonStyle(BottomBarButtonStyle(height: height, horizontalPadding: 0))
            .help("About where data goes")
            .popover(isPresented: $showingDataNotes, arrowEdge: .top) {
                VStack(alignment: .leading, spacing: 8) { dataNotes }
                    .font(.callout)
                    .padding(14)
                    .frame(width: 280, alignment: .leading)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var copyButton: some View {
        Button(action: copyPrompt) {
            Label("Copy as prompt", systemImage: "square.on.square").labelStyle(.iconOnly)
        }
        .buttonStyle(BottomBarButtonStyle(horizontalPadding: 0))
        .disabled(transcript == nil)
            .help(model.detailTab == .chat
                  ? "Copy the transcript and the question as a prompt for any chat app"
                  : "Copy the transcript as a summary prompt for any chat app")
    }

    /// Where the data goes with the current provider: off this Mac, and how far a CLI agent is contained.
    @ViewBuilder private var dataNotes: some View {
        let provider = model.currentAIProvider
        if let provider, provider.leavesMac {
            Label("Data sent to \(provider.name) leaves this Mac.", systemImage: "info.circle")
        }
        if let note = containmentNote {
            Label(note, systemImage: "exclamationmark.shield")
        }
    }

    private var containmentNote: String? {
        model.currentAIProvider?.cliPreset.flatMap(CLIRunner.containmentNote(for:))
    }

    private var hasDataNotes: Bool {
        model.currentAIProvider?.leavesMac == true || containmentNote != nil
    }

    @ViewBuilder private var toastView: some View {
        if let toast {
            Text(toast)
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .modifier(GlassCard(cornerRadius: 14))
                .padding(.bottom, 110)
                .transition(reduceMotion ? .opacity : .offset(y: 8).combined(with: .opacity))
        }
    }

    // MARK: - State

    /// The current provider's picked model ("" = a CLI's own default).
    private var currentModel: String? {
        model.currentAIProvider.map(model.aiModel(for:))
    }

    private var summaryJob: AppModel.AIJob? {
        guard let job = model.aiJob, job.conversationID == conversation.id, job.kind == .summary else { return nil }
        return job
    }

    private var questionJob: AppModel.AIJob? {
        guard let job = model.aiJob, job.conversationID == conversation.id, job.kind != .summary else { return nil }
        return job
    }

    private var canRun: Bool {
        model.aiJob == nil && transcript != nil && model.currentAIProvider != nil
    }

    private func load() async {
        let folder = conversation.folder
        let (loaded, versions, messages) = await Task.detached(priority: .utility) {
            (try? Transcript.read(from: folder), Conversation.readSummaryVersions(from: folder),
             ChatLog.read(from: folder).messages)
        }.value
        guard !Task.isCancelled else { return }
        (transcript, summaries, summary, chat) = (loaded, versions, versions.last?.text, messages)
        shownVersion = nil
    }

    private func loadChat() async {
        let folder = conversation.folder
        let (log, entries) = await Task.detached(priority: .utility) {
            (ChatLog.read(from: folder), ChatArchive.entries(in: folder))
        }.value
        guard !Task.isCancelled else { return }
        (chat, chatTitle, archives) = (log.messages, log.title, entries)
    }

    private func start(_ kind: AppModel.AIJob.Kind) {
        guard let transcript, let provider = model.currentAIProvider else { return }
        model.startAIJob(
            kind, conversation: conversation, transcript: transcript, summary: summary, history: chat,
            provider: provider, model: model.aiModel(for: provider))
    }

    private func ask() {
        let text = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard canRun, !text.isEmpty else { return }
        start(.question(text))
        question = ""
    }

    private func copyPrompt() {
        guard let transcript else { return }
        let prompt = PromptBuilder.copyPrompt(
            conversation: conversation, transcript: transcript, question: model.detailTab == .chat ? question : nil,
            languageSetting: model.settings.summaryLanguage)
        // Transient (nspasteboard.org) and this Mac only: clipboard managers and Universal Clipboard skip the transcript.
        let pasteboard = NSPasteboard.general
        let transient = NSPasteboard.PasteboardType("org.nspasteboard.TransientType")
        pasteboard.prepareForNewContents(with: .currentHostOnly)
        pasteboard.addTypes([.string, transient], owner: nil)
        pasteboard.setString(prompt, forType: .string)
        pasteboard.setString("", forType: transient)
        showToast("Copied — paste into any chat")
    }

    /// Shows `text` for 2 s; a newer toast replaces it and gets its own 2 s.
    private func showToast(_ text: String) {
        toastTask?.cancel()
        withAnimation(.snappy(duration: 0.2)) { toast = text }
        toastTask = Task {
            try? await Task.sleep(for: .seconds(2))
            guard !Task.isCancelled else { return }
            withAnimation(.snappy(duration: 0.2)) { toast = nil }
        }
    }

    private func copyAnswer(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        showToast("Copied")
    }

    /// Citations open the transcript at their time (whole seconds, at most 24 h); web links open in the browser
    /// once the alert showing their destination is confirmed (the link text is model output); anything else is
    /// dropped.
    private func openLink(_ url: URL) -> OpenURLAction.Result {
        if url.scheme == MarkdownText.citationScheme {
            let id = conversation.id
            guard let seconds = Int(url.absoluteString.dropFirst(MarkdownText.citationScheme.count + 1)),
                  (0...86_400).contains(seconds) else { return .discarded }
            withAnimation(Motion.standard) { model.openTranscript(conversationID: id, at: Double(seconds)) }
            return .handled
        }
        guard ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return .discarded }
        pendingLink = url
        return .handled
    }
}

/// Every provider with the models it offers, on this Mac first; Foundation Models shows why it cannot run (the AI
/// pane's bottom bar and the global chat's).
struct AIModelMenu: View {
    let model: AppModel
    let height: CGFloat

    var body: some View {
        let providers = model.aiProviders
        let inApp = providers.filter { $0.kind == .inApp }
        let hosts = providers.filter { $0.kind != .inApp && !$0.leavesMac } + providers.filter(\.leavesMac)
        Menu {
            if !inApp.isEmpty {
                Section("On this Mac") {
                    ForEach(inApp) { provider in modelItem(provider, provider.defaultModel, title: provider.name) }
                }
            }
            ForEach(hosts) { provider in
                Section(provider.leavesMac ? "\(provider.name) (leaves this Mac)" : provider.name) {
                    if provider.models.isEmpty {
                        Text("No model: choose one in Settings")
                    }
                    ForEach(provider.models, id: \.self) { id in
                        modelItem(provider, id, title: id.isEmpty ? "Default" : id)
                    }
                }
            }
            Divider()
            Button("Choose Models…") { model.openSettings(tab: .providers) }
        } label: {
            AIMenuLabel(title: modelTitle, systemImage: "cpu")
        }
        .modifier(CompactMenu(maxWidth: 180, height: height))
        .layoutPriority(1)
        .help("Model for summaries and questions")
    }

    private func modelItem(_ provider: AppModel.AIProvider, _ id: String, title: String) -> some View {
        Toggle(isOn: Binding(
            get: { model.currentAIProvider?.id == provider.id && currentModel == id },
            set: { if $0 { model.setAIModel(id, of: provider) } }
        )) {
            Text(title)
            if let reason = provider.unavailableReason { Text(reason) }
        }
        .disabled(provider.unavailableReason != nil)
    }

    private var modelTitle: String {
        guard let provider = model.currentAIProvider else { return "No AI model" }
        guard provider.kind != .inApp else { return provider.name }
        let id = currentModel ?? ""
        return id.isEmpty ? provider.name : id
    }

    /// The current provider's picked model ("" = a CLI's own default).
    private var currentModel: String? {
        model.currentAIProvider.map(model.aiModel(for:))
    }
}

/// The output language of summaries and answers (the AI pane's bottom bar and the global chat's).
struct AILanguageMenu: View {
    let model: AppModel
    let height: CGFloat

    var body: some View {
        Menu {
            Picker("Language", selection: languageBinding) {
                Text("Auto").tag("auto")
                Text("English").tag("en")
                Text("French").tag("fr")
                Text("Vietnamese").tag("vi")
                if !["auto", "en", "fr", "vi"].contains(model.settings.summaryLanguage) {
                    Text(model.settings.summaryLanguage).tag(model.settings.summaryLanguage)
                }
            }
            .pickerStyle(.inline)
            .labelsHidden()
        } label: {
            AIMenuLabel(title: LanguageOptions.shortCode(model.settings.summaryLanguage), systemImage: "globe")
        }
        .modifier(CompactMenu(maxWidth: nil, height: height))
        .help("Language of summaries and answers (Auto = the transcript's language for summaries, the question's for answers)")
    }

    private var languageBinding: Binding<String> {
        Binding(get: { model.settings.summaryLanguage }, set: { value in model.updateSettings { $0.summaryLanguage = value } })
    }
}

/// The label of a bottom bar's menu: the bar's style draws no menu arrow, so the chevron says it opens a menu.
struct AIMenuLabel: View {
    let title: String
    let systemImage: String

    var body: some View {
        HStack(spacing: 6) {
            Label {
                Text(title).lineLimit(1).truncationMode(.tail)
            } icon: {
                Image(systemName: systemImage)
            }
            MenuChevron()
        }
    }
}

/// Why no model can run: no provider set up, and why Foundation Models is unavailable.
struct AISetupHints: View {
    let model: AppModel

    var body: some View {
        if model.currentAIProvider == nil {
            Label("No AI model: set up a provider or download a model in Settings › AI.",
                  systemImage: "exclamationmark.triangle")
            if let reason = AppModel.foundationModelsUnavailableReason {
                Label(reason, systemImage: "info.circle")
            }
        }
    }
}

/// Opens a web link from model output once the alert showing its destination is confirmed; `link` is the one asked.
struct WebLinkConfirmation: ViewModifier {
    @Binding var link: URL?

    func body(content: Content) -> some View {
        content.alert(
            "Open \(link?.host() ?? "this link")?",
            isPresented: Binding(get: { link != nil }, set: { if !$0 { link = nil } }),
            presenting: link
        ) { url in
            Button("Open") { NSWorkspace.shared.open(url) }
            Button("Cancel", role: .cancel) {}
        } message: { url in
            Text(url.absoluteString)
        }
    }
}

/// A small capsule button of the AI pane's busy line.
struct CapsuleButton: ViewModifier {
    func body(content: Content) -> some View {
        content
            .buttonStyle(.bordered)
            .buttonBorderShape(.capsule)
        .controlSize(.small)
    }
}

/// A row of the History panel: lighter on hover, darker while pressed, dimmed when disabled. `hovered`, when set, is
/// the row's hover in place of the label's own, so a button over the row keeps it highlighted.
struct HistoryRowStyle: ButtonStyle {
    var hovered: Bool?

    func makeBody(configuration: Configuration) -> some View {
        Highlight(configuration: configuration, rowHovered: hovered)
    }

    private struct Highlight: View {
        let configuration: ButtonStyleConfiguration
        let rowHovered: Bool?
        @State private var hovered = false
        @Environment(\.isEnabled) private var isEnabled

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
            let highlighted = (rowHovered ?? hovered) && isEnabled
            configuration.label
                .background(shape.fill(
                    configuration.isPressed ? Palette.pressedFill : highlighted ? Palette.hoverFill : .clear))
                .contentShape(shape)
                .opacity(isEnabled ? 1 : 0.45)
                .animation(Motion.hover, value: hovered)
                .onHover { hovered = $0 }
        }
    }
}

/// A button of a tab's bottom bar (also the Transcript tab's): `height` tall (a circle for an icon, with no
/// `horizontalPadding`), a capsule of the input color, or the accent color when `prominent`; lighter on hover, darker
/// while pressed, dimmed when disabled.
struct BottomBarButtonStyle: ButtonStyle {
    /// Every control of the bottom bars: buttons, menus, speaker chips and the one-line question field.
    static let height: CGFloat = 34

    var height = Self.height
    var prominent = false
    var horizontalPadding: CGFloat = 14

    func makeBody(configuration: Configuration) -> some View {
        Highlight(configuration: configuration, height: height, prominent: prominent, horizontalPadding: horizontalPadding)
    }

    private struct Highlight: View {
        let configuration: ButtonStyleConfiguration
        let height: CGFloat
        let prominent: Bool
        let horizontalPadding: CGFloat
        @State private var hovered = false
        @Environment(\.isEnabled) private var isEnabled

        var body: some View {
            configuration.label
                .font(.body.weight(prominent ? .semibold : .regular))
                .foregroundStyle(prominent ? AnyShapeStyle(.white) : AnyShapeStyle(.primary))
                .padding(.horizontal, horizontalPadding)
                .frame(minWidth: height, minHeight: height, maxHeight: height)
                .background(
                    Capsule()
                        .fill(prominent ? AnyShapeStyle(Color.accentColor) : AnyShapeStyle(Palette.inputSurface))
                        .overlay(Capsule().fill(
                            configuration.isPressed ? Color.black.opacity(0.2)
                                : hovered && isEnabled ? Palette.hoverFill : .clear)))
                .contentShape(Capsule())
                .opacity(isEnabled ? 1 : 0.45)
                .animation(Motion.hover, value: hovered)
                .onHover { hovered = $0 }
        }
    }
}

/// The arrow of a bottom bar's menu (the Transcript tab's Re-transcribe too).
struct MenuChevron: View {
    var body: some View {
        Image(systemName: "chevron.down")
            .font(.system(size: 9, weight: .bold))
            .accessibilityHidden(true)
    }
}

/// A capsule menu of the AI pane's bottom bar, `height` tall like the buttons beside it: its title's width, a long one
/// cut to `maxWidth` with an ellipsis (never cut without one).
struct CompactMenu: ViewModifier {
    let maxWidth: CGFloat?
    let height: CGFloat

    func body(content: Content) -> some View {
        let menu = content
            .menuStyle(.button)
            .buttonStyle(BottomBarButtonStyle(height: height, horizontalPadding: height < BottomBarButtonStyle.height ? 12 : 14))
        if let maxWidth {
            WidthCap(maxWidth: maxWidth) { menu }
        } else {
            menu.fixedSize()
        }
    }
}

/// Its content at the proposed width, at most `maxWidth`, and no wider than the content: unlike
/// `frame(maxWidth:)`, a short title leaves no gap after it.
struct WidthCap: Layout {
    let maxWidth: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard let content = subviews.first else { return .zero }
        let width = min(proposal.width ?? content.sizeThatFits(.unspecified).width, maxWidth)
        return content.sizeThatFits(ProposedViewSize(width: width, height: proposal.height))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        subviews.first?.place(at: bounds.origin, proposal: ProposedViewSize(width: bounds.width, height: bounds.height))
    }
}

/// Model Markdown, line by line: `#` headings, `-`/`*` bullets (indented by their leading spaces) and paragraphs,
/// each with inline Markdown; `[mm:ss]` citations become `echodiar-time:<seconds>` links, or with `references` (a
/// library-chat answer) `[n mm:ss]` / `[n]` citations become `echodiar-ref:` links. No HTML, no web view; images
/// (`![alt](url)`) show their alt text only, nothing is fetched. A streaming answer is parsed on every update, so it is
/// formatted as it arrives.
struct MarkdownText: View {
    let text: String
    /// The sources of a library-chat answer; nil for an answer about one conversation.
    var references: [ChatLog.Reference]? = nil

    static let citationScheme = "echodiar-time"

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(Array(Self.lines(text).enumerated()), id: \.offset) { _, line in
                switch line.kind {
                case .heading:
                    Text(Self.inline(line.text, references: references)).font(.headline).padding(.top, 4)
                case .bullet:
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text("•")
                        Text(Self.inline(line.text, references: references))
                    }
                    .padding(.leading, CGFloat(line.indent) * 14)
                case .paragraph:
                    Text(Self.inline(line.text, references: references))
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    enum Kind { case heading, bullet, paragraph }

    static func lines(_ text: String) -> [(kind: Kind, indent: Int, text: String)] {
        text.components(separatedBy: .newlines).compactMap { raw in
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty else { return nil }
            let indent = raw.prefix { $0 == " " || $0 == "\t" }.count / 2
            let hashes = line.prefix { $0 == "#" }.count
            if (1...6).contains(hashes), line.dropFirst(hashes).first == " " {
                return (.heading, 0, String(line.dropFirst(hashes + 1)))
            }
            if let marker = line.first, "-*+•".contains(marker), line.dropFirst().first == " " {
                return (.bullet, indent, String(line.dropFirst(2)))
            }
            return (.paragraph, 0, line)
        }
    }

    static func inline(_ text: String, references: [ChatLog.Reference]? = nil) -> AttributedString {
        let linked = references.map { LibraryCitation.linked(text, references: $0) }
            ?? TranscriptCitation.linked(text, scheme: citationScheme)
        return (try? AttributedString(
            markdown: linked, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(text)
    }
}
