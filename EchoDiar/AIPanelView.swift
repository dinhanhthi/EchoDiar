import AppKit
import EchoAI
import EchoCore
import SwiftUI

/// The Summary or Chat tab of the open conversation (`AppModel.detailTab`): Chat (questions with streamed answers) or
/// Summary (the saved summary, Generate / Regenerate). The bar at the bottom holds the tab's actions, "Copy as
/// prompt", and small menus for the model (every usable provider with its models) and the output language.
/// `[mm:ss]` citations open the transcript at that time.
struct AIPanelView: View {
    let conversation: Conversation
    let model: AppModel
    @State private var transcript: Transcript?
    @State private var summary: String?
    @State private var chat: [ChatLog.Message] = []
    @State private var question = ""
    @State private var toast: String?
    /// A web link from model output, opened only once its destination is confirmed.
    @State private var pendingLink: URL?

    private static let chatEnd = "chatEnd"

    var body: some View {
        VStack(spacing: 10) {
            ZStack {
                if model.detailTab == .chat {
                    chatPane
                } else {
                    summaryPane
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            errorText
            bottomBar
        }
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
        .task(id: LoadKey(id: conversation.id, hasTranscript: conversation.hasTranscript,
                          jobsEnded: model.aiJobsEnded)) { await load() }
    }

    private struct LoadKey: Equatable {
        let id: UUID
        let hasTranscript: Bool
        let jobsEnded: Int
    }

    // MARK: - Chat

    private var chatPane: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    if chat.isEmpty && questionJob == nil {
                        Text(emptyNote ?? "Ask a question about this conversation below.")
                            .foregroundStyle(.secondary)
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
                            messageView(.assistant, text: job.text, model: currentModel, isStreaming: true)
                        }
                    }
                    Color.clear.frame(height: 1).id(Self.chatEnd)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
            }
            .onAppear { proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: chat.count) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: questionJob?.text.count) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
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

    // MARK: - Summary

    private var summaryPane: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 8) {
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
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .modifier(GlassCard(cornerRadius: 20))
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

    /// The tab's actions, then the small menus and any notice about where the data goes.
    private var bottomBar: some View {
        VStack(alignment: .leading, spacing: 8) {
            busyElsewhere
            HStack(spacing: 12) {
                if model.detailTab == .chat {
                    TextField("Ask about this conversation", text: $question)
                        .textFieldStyle(.plain)
                        .onSubmit(ask)
                    copyButton
                    if questionJob != nil {
                        iconButton("Cancel", systemImage: "stop.circle", action: model.cancelAIJob)
                    } else {
                        iconButton("Ask", systemImage: "paperplane", action: ask)
                            .disabled(!canRun || question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    }
                } else {
                    if summaryJob != nil {
                        ProgressView().controlSize(.small)
                    }
                    Spacer(minLength: 0)
                    copyButton
                    if summaryJob != nil {
                        Button("Cancel", action: model.cancelAIJob)
                    } else {
                        Button(summary == nil ? "Generate" : "Regenerate") { start(.summary) }
                            .disabled(!canRun)
                    }
                }
            }
            HStack(spacing: 2) {
                modelMenu
                languageMenu
                Spacer(minLength: 0)
            }
            hints
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(GlassCard(cornerRadius: 20))
    }

    /// A job of another conversation or of the other tab: it blocks Ask and Generate until it ends, so it says what
    /// runs and can be cancelled from here.
    @ViewBuilder private var busyElsewhere: some View {
        if let job = model.aiJob, (model.detailTab == .chat ? questionJob : summaryJob) == nil {
            let title = model.conversations.first { $0.id == job.conversationID }?.title ?? "another conversation"
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text(job.kind == .summary ? "Summarizing \(title)…" : "Answering about \(title)…")
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: 0)
                Button("Cancel", action: model.cancelAIJob)
                    .controlSize(.small)
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
    }

    private var copyButton: some View {
        iconButton("Copy as prompt", systemImage: "doc.on.clipboard", action: copyPrompt)
            .disabled(transcript == nil)
            .help(model.detailTab == .chat
                  ? "Copy the transcript and the question as a prompt for any chat app"
                  : "Copy the transcript as a summary prompt for any chat app")
    }

    /// Every provider with the models it offers, on this Mac first; Foundation Models shows why it cannot run.
    private var modelMenu: some View {
        let providers = model.aiProviders
        let inApp = providers.filter { $0.kind == .inApp }
        let hosts = providers.filter { $0.kind != .inApp && !$0.leavesMac } + providers.filter(\.leavesMac)
        return Menu {
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
            menuLabel(modelTitle, systemImage: "cpu")
        }
        .modifier(CompactMenu(maxWidth: 220))
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
        return "\(provider.name) · \(id.isEmpty ? "Default" : id)"
    }

    private var languageMenu: some View {
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
            menuLabel(LanguageOptions.name(model.settings.summaryLanguage), systemImage: "globe")
        }
        .modifier(CompactMenu(maxWidth: nil))
        .help("Language of summaries and answers (Auto = the transcript's language for summaries, the question's for answers)")
    }

    private func menuLabel(_ title: String, systemImage: String) -> some View {
        Label {
            Text(title).lineLimit(1).truncationMode(.middle)
        } icon: {
            Image(systemName: systemImage)
        }
    }

    @ViewBuilder private var hints: some View {
        let provider = model.currentAIProvider
        if provider == nil {
            Label("No AI model: set up a provider or download a model in Settings › AI & Cloud.",
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
                .padding(.bottom, 110)
                .transition(.opacity)
        }
    }

    // MARK: - State

    private var languageBinding: Binding<String> {
        Binding(get: { model.settings.summaryLanguage }, set: { value in model.updateSettings { $0.summaryLanguage = value } })
    }

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
        let (loaded, savedSummary, messages) = await Task.detached(priority: .utility) {
            (try? Transcript.read(from: folder), Conversation.readSummary(from: folder), ChatLog.read(from: folder).messages)
        }.value
        guard !Task.isCancelled else { return }
        (transcript, summary, chat) = (loaded, savedSummary, messages)
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
            let id = conversation.id
            guard let seconds = Int(url.absoluteString.dropFirst(MarkdownText.citationScheme.count + 1)),
                  (0...86_400).contains(seconds) else { return .discarded }
            withAnimation(.snappy) { model.openTranscript(conversationID: id, at: Double(seconds)) }
            return .handled
        }
        guard ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return .discarded }
        pendingLink = url
        return .handled
    }
}

/// A small borderless menu of the AI pane's bottom bar: its title's width, a long one cut to `maxWidth` (never cut
/// without one).
private struct CompactMenu: ViewModifier {
    let maxWidth: CGFloat?

    func body(content: Content) -> some View {
        let menu = content
            .menuStyle(.borderlessButton)
            .font(.caption)
            .foregroundStyle(.secondary)
        if let maxWidth {
            menu.frame(maxWidth: maxWidth, alignment: .leading).fixedSize(horizontal: false, vertical: true)
        } else {
            menu.fixedSize()
        }
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
