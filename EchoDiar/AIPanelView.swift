import AppKit
import EchoAI
import EchoCore
import SwiftUI

/// The AI tab: a conversation, a provider and model, the summary language, then the saved summary (Generate /
/// Regenerate) and the chat (questions with streamed answers), "Copy as prompt" and Cancel. `[mm:ss]` citations
/// open the transcript at that time.
struct AIPanelView: View {
    let model: AppModel
    @State private var transcript: Transcript?
    @State private var summary: String?
    @State private var chat: [ChatLog.Message] = []
    @State private var question = ""
    @State private var modelDraft = ""
    @State private var toast: String?
    /// A web link from model output, opened only once its destination is confirmed.
    @State private var pendingLink: URL?

    private static let chatEnd = "chatEnd"

    var body: some View {
        VStack(spacing: 10) {
            header
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(spacing: 10) {
                        summaryCard.id(AppModel.AIFocus.summary)
                        chatCard
                        Color.clear.frame(height: 1).id(Self.chatEnd)
                    }
                }
                .onAppear { scroll(proxy, to: model.aiFocus) }
                .onChange(of: model.aiFocus) { _, focus in scroll(proxy, to: focus) }
                .onChange(of: chat.count) { _, _ in
                    if model.aiFocus == .chat { proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
                }
                .onChange(of: questionJob?.text.count) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            }
            inputBar
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .overlay(alignment: .bottom) { toastView }
        .environment(\.openURL, OpenURLAction(handler: openLink))
        .alert(
            "Open \(pendingLink?.host() ?? "this link")?",
            isPresented: Binding(get: { pendingLink != nil }, set: { if !$0 { pendingLink = nil } }),
            presenting: pendingLink
        ) { url in
            Button("Open") { NSWorkspace.shared.open(url) }
            Button("Cancel", role: .cancel) {}
        } message: { url in
            Text(url.absoluteString)
        }
        .task { model.refreshInstalledModels() }
        .task(id: LoadKey(id: conversation?.id, hasTranscript: conversation?.hasTranscript ?? false,
                          jobsEnded: model.aiJobsEnded)) { await load() }
        .onChange(of: model.currentAIProvider?.id, initial: true) { _, _ in
            modelDraft = model.currentAIProvider.map(model.aiModel(for:)) ?? ""
        }
    }

    private struct LoadKey: Equatable {
        let id: UUID?
        let hasTranscript: Bool
        let jobsEnded: Int
    }

    // MARK: - Header

    private var header: some View {
        VStack(alignment: .leading, spacing: 8) {
            Picker("Conversation", selection: conversationBinding) {
                ForEach(conversations) { Text($0.title).tag(Optional($0.id)) }
            }
            HStack(spacing: 10) {
                providerMenu
                if let provider = model.currentAIProvider, provider.kind != .inApp {
                    TextField("Model", text: $modelDraft, prompt: Text(provider.kind == .cli ? "Default" : "Model id"))
                        .textFieldStyle(.roundedBorder)
                        .frame(maxWidth: 200)
                }
                Spacer(minLength: 0)
                Picker("Language", selection: languageBinding) {
                    Text("Auto").tag("auto")
                    Text("English").tag("en")
                    Text("French").tag("fr")
                    Text("Vietnamese").tag("vi")
                    if !["auto", "en", "fr", "vi"].contains(model.settings.summaryLanguage) {
                        Text(model.settings.summaryLanguage).tag(model.settings.summaryLanguage)
                    }
                }
                .fixedSize()
                .help("Summary language (Auto = the transcript's language)")
            }
            hints
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(GlassCard(cornerRadius: 20))
    }

    /// In-app and local-host models first, then cloud APIs and CLIs; Foundation Models shows why it cannot run.
    private var providerMenu: some View {
        let providers = model.aiProviders
        return Menu {
            Section("On this Mac") {
                ForEach(providers.filter { !$0.leavesMac }) { providerItem($0) }
            }
            let remote = providers.filter(\.leavesMac)
            if !remote.isEmpty {
                Section("Cloud and CLIs") {
                    ForEach(remote) { providerItem($0) }
                }
            }
            Divider()
            Button("Set up providers…") { model.openSettings(tab: .providers) }
            Button("Download models…") { model.openSettings(tab: .transcription) }
        } label: {
            Label(model.currentAIProvider?.name ?? "No AI provider", systemImage: "cpu")
        }
        .fixedSize()
        .help("Provider for summaries and questions")
    }

    private func providerItem(_ provider: AppModel.AIProvider) -> some View {
        Toggle(isOn: Binding(
            get: { model.currentAIProvider?.id == provider.id },
            set: { if $0 { model.setAIProvider(provider.id) } }
        )) {
            Text(provider.name)
            if let reason = provider.unavailableReason { Text(reason) }
        }
        .disabled(provider.unavailableReason != nil)
    }

    @ViewBuilder private var hints: some View {
        let provider = model.currentAIProvider
        if provider == nil {
            Label("No AI provider: set one up in Settings › AI & Cloud, or download a model in Settings › Transcription.",
                  systemImage: "exclamationmark.triangle")
            if let reason = AppModel.foundationModelsUnavailableReason {
                Label(reason, systemImage: "info.circle")
            }
        }
        if let provider, provider.leavesMac {
            Label("Data sent to \(provider.name) leaves this Mac.", systemImage: "info.circle")
        }
        if let note = provider?.cliPreset.flatMap(CLIRunner.containmentNote(for:)) {
            Label(note, systemImage: "exclamationmark.shield")
        }
    }

    // MARK: - Summary

    private var summaryCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Summary").font(.headline)
                Spacer()
                if summaryJob != nil { ProgressView().controlSize(.small) }
                Button(summary == nil ? "Generate" : "Regenerate") { start(.summary) }
                    .disabled(!canRun)
            }
            if let job = summaryJob {
                if let status = job.status {
                    Text(status).foregroundStyle(.secondary)
                }
                MarkdownText(text: job.text, isStreaming: true)
            } else if let summary {
                MarkdownText(text: summary)
            } else {
                Text(emptyNote ?? "No summary yet.").foregroundStyle(.secondary)
            }
            errorText
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .modifier(GlassCard(cornerRadius: 20))
    }

    /// Why nothing can run for this conversation.
    private var emptyNote: String? {
        guard let conversation else { return "No transcribed conversation yet." }
        return conversation.hasTranscript ? nil : "This conversation has no transcript yet."
    }

    @ViewBuilder private var errorText: some View {
        if let error = model.aiError, error.conversationID == conversation?.id {
            Text(error.message)
                .font(.callout)
                .foregroundStyle(.red)
                .lineLimit(4)
        }
    }

    // MARK: - Chat

    private var chatCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Chat").font(.headline)
            if chat.isEmpty && questionJob == nil {
                Text("Ask a question about this conversation below.").foregroundStyle(.secondary)
            }
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
                    messageView(.assistant, text: job.text, model: modelDraft, isStreaming: true)
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .modifier(GlassCard(cornerRadius: 20))
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
            VStack(alignment: .leading, spacing: 2) {
                MarkdownText(text: text, isStreaming: isStreaming)
                if let model, !model.isEmpty {
                    Text(model).font(.caption2).foregroundStyle(.tertiary)
                }
            }
        }
    }

    // MARK: - Input

    private var inputBar: some View {
        HStack(spacing: 12) {
            TextField("Ask about this conversation", text: $question)
                .textFieldStyle(.plain)
                .onSubmit(ask)
            if model.aiJob != nil {
                iconButton("Cancel", systemImage: "stop.circle", action: model.cancelAIJob)
            } else {
                iconButton("Ask", systemImage: "paperplane", action: ask)
                    .disabled(!canRun || question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            iconButton("Copy as prompt", systemImage: "doc.on.clipboard", action: copyPrompt)
                .disabled(transcript == nil)
                .help("Copy the transcript and the question as a prompt for any chat app")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(GlassCard(cornerRadius: 20))
    }

    private func iconButton(_ title: String, systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .labelStyle(.iconOnly)
                .font(.system(size: 15))
        }
        .buttonStyle(RowIconButtonStyle())
        .foregroundStyle(.secondary)
        .help(title)
    }

    @ViewBuilder private var toastView: some View {
        if let toast {
            Text(toast)
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .modifier(GlassCard(cornerRadius: 14))
                .padding(.bottom, 70)
                .transition(.opacity)
        }
    }

    // MARK: - State

    /// Transcribed conversations, plus the selected one even without a transcript.
    private var conversations: [Conversation] {
        model.conversations.filter { $0.hasTranscript || $0.id == model.selectedConversationID }
    }

    /// The card's conversation, else the newest transcribed one.
    private var conversation: Conversation? {
        let all = conversations
        return all.first { $0.id == model.selectedConversationID } ?? all.first { $0.hasTranscript }
    }

    private var conversationBinding: Binding<UUID?> {
        Binding(get: { conversation?.id }, set: { model.selectedConversationID = $0 })
    }

    private var languageBinding: Binding<String> {
        Binding(get: { model.settings.summaryLanguage }, set: { value in model.updateSettings { $0.summaryLanguage = value } })
    }

    private var summaryJob: AppModel.AIJob? {
        guard let job = model.aiJob, job.conversationID == conversation?.id, job.kind == .summary else { return nil }
        return job
    }

    private var questionJob: AppModel.AIJob? {
        guard let job = model.aiJob, job.conversationID == conversation?.id, job.kind != .summary else { return nil }
        return job
    }

    private var canRun: Bool {
        model.aiJob == nil && transcript != nil && model.currentAIProvider != nil
    }

    private func scroll(_ proxy: ScrollViewProxy, to focus: AppModel.AIFocus) {
        if focus == .summary {
            proxy.scrollTo(AppModel.AIFocus.summary, anchor: .top)
        } else {
            proxy.scrollTo(Self.chatEnd, anchor: .bottom)
        }
    }

    private func load() async {
        guard let folder = conversation?.folder else {
            (transcript, summary, chat) = (nil, nil, [])
            return
        }
        let (loaded, savedSummary, messages) = await Task.detached(priority: .utility) {
            (try? Transcript.read(from: folder), Conversation.readSummary(from: folder), ChatLog.read(from: folder).messages)
        }.value
        guard !Task.isCancelled else { return }
        (transcript, summary, chat) = (loaded, savedSummary, messages)
    }

    private func start(_ kind: AppModel.AIJob.Kind) {
        guard let conversation, let transcript, let provider = model.currentAIProvider else { return }
        model.startAIJob(
            kind, conversation: conversation, transcript: transcript, summary: summary, history: chat,
            provider: provider, model: modelDraft)
    }

    private func ask() {
        let text = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard canRun, !text.isEmpty else { return }
        model.aiFocus = .chat
        start(.question(text))
        question = ""
    }

    private func copyPrompt() {
        guard let conversation, let transcript else { return }
        let prompt = PromptBuilder.copyPrompt(
            conversation: conversation, transcript: transcript, question: question,
            language: OutputLanguage.resolve(setting: model.settings.summaryLanguage, transcript: transcript))
        // Transient (nspasteboard.org) and this Mac only: clipboard managers and Universal Clipboard skip the transcript.
        let pasteboard = NSPasteboard.general
        let transient = NSPasteboard.PasteboardType("org.nspasteboard.TransientType")
        pasteboard.prepareForNewContents(with: .currentHostOnly)
        pasteboard.addTypes([.string, transient], owner: nil)
        pasteboard.setString(prompt, forType: .string)
        pasteboard.setString("", forType: transient)
        withAnimation { toast = "Copied — paste into any chat" }
        Task {
            try? await Task.sleep(for: .seconds(2))
            withAnimation { toast = nil }
        }
    }

    /// Citations open the transcript at their time (whole seconds, at most 24 h); web links open in the browser
    /// once the alert showing their destination is confirmed (the link text is model output); anything else is
    /// dropped.
    private func openLink(_ url: URL) -> OpenURLAction.Result {
        if url.scheme == MarkdownText.citationScheme {
            guard let id = conversation?.id,
                  let seconds = Int(url.absoluteString.dropFirst(MarkdownText.citationScheme.count + 1)),
                  (0...86_400).contains(seconds) else { return .discarded }
            withAnimation(.snappy) { model.openTranscript(conversationID: id, at: Double(seconds)) }
            return .handled
        }
        guard ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return .discarded }
        pendingLink = url
        return .handled
    }
}

/// Model Markdown, line by line: `#` headings, `-`/`*` bullets (indented by their leading spaces) and paragraphs,
/// each with inline Markdown; `[mm:ss]` citations become `echodiar-time:<seconds>` links. No HTML, no web view.
/// A streaming answer is plain text, parsed once it is saved.
struct MarkdownText: View {
    let text: String
    var isStreaming = false

    static let citationScheme = "echodiar-time"

    var body: some View {
        if isStreaming {
            Text(text).frame(maxWidth: .infinity, alignment: .leading)
        } else {
            markdown
        }
    }

    private var markdown: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(Array(Self.lines(text).enumerated()), id: \.offset) { _, line in
                switch line.kind {
                case .heading:
                    Text(Self.inline(line.text)).font(.headline).padding(.top, 4)
                case .bullet:
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text("•")
                        Text(Self.inline(line.text))
                    }
                    .padding(.leading, CGFloat(line.indent) * 14)
                case .paragraph:
                    Text(Self.inline(line.text))
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

    static func inline(_ text: String) -> AttributedString {
        let linked = TranscriptCitation.linked(text, scheme: citationScheme)
        return (try? AttributedString(
            markdown: linked, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(text)
    }
}
