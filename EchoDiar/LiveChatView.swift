import AppKit
import EchoCore
import SwiftUI

/// The recording's Chat tab: questions about the meeting so far (`AppModel.askLive`), answered with the catch-up
/// provider. The recording's chat (questions plain, answers Markdown with the model), then the answer being written
/// (or the last one that failed, with its error); under it the question row, or why no question can be asked.
struct LiveChatView: View {
    let model: AppModel
    /// The messages and the question row in cards of their own; false inside a panel that draws one (side by side).
    var framed = true

    private static let chatEnd = "chatEnd"
    /// The model badge under an answer.
    private static let answerFooterHeight: CGFloat = 22

    var body: some View {
        VStack(spacing: 10) {
            chatPane
            LiveChatInputBar(model: model, framed: framed)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        // Model Markdown: `[mm:ss]` citations keep the conversation chat's link style but open nothing, as in the
        // catch-ups: the live transcript has no scroll-to-time hook. Web links open nothing either.
        .environment(\.openURL, OpenURLAction { _ in .discarded })
    }

    private var chatPane: some View {
        Group {
            if model.liveChatMessages.isEmpty && model.liveChatRun == nil {
                Text("Ask about the meeting so far.")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(20)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                messages
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .modifier(FramedCard(framed: framed, cornerRadius: 20))
    }

    private var messages: some View {
        let run = model.liveChatRun
        return ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    ForEach(Array(model.liveChatMessages.enumerated()), id: \.offset) { _, message in
                        messageView(message.role, text: message.text, model: message.model)
                    }
                    if let run { runView(run) }
                    Color.clear.frame(height: 1).id(Self.chatEnd)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 16)
            }
            .onAppear { proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: model.liveChatMessages.count) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: run?.jobID) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: run?.text.count) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
            .onChange(of: run?.error) { _, _ in proxy.scrollTo(Self.chatEnd, anchor: .bottom) }
        }
    }

    /// The question, then the answer streamed so far, or a spinner with what the job is doing; the error under it.
    @ViewBuilder private func runView(_ run: AppModel.LiveChatRun) -> some View {
        messageView(.user, text: run.question, model: nil)
        if !run.text.isEmpty {
            messageView(.assistant, text: run.text, model: nil)
        } else if run.isRunning {
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                if let status = run.statusLine {
                    Text(status).foregroundStyle(.secondary)
                }
            }
        }
        if let error = run.error {
            Text(error)
                .font(.callout)
                .foregroundStyle(.red)
                .lineLimit(4)
        }
    }

    /// As the conversation chat's messages: the question in a light bubble on the trailing side, the answer as
    /// Markdown with its model.
    @ViewBuilder private func messageView(_ role: ChatLog.Message.Role, text: String, model: String?) -> some View {
        if role == .user {
            Text(text)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(Palette.hoverFill, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                .frame(maxWidth: .infinity, alignment: .trailing)
        } else {
            VStack(alignment: .leading, spacing: 6) {
                MarkdownText(text: text)
                if let model, !model.isEmpty {
                    Text(model)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 8)
                        .frame(height: Self.answerFooterHeight)
                        .background(Capsule().fill(Palette.inputSurface))
                }
            }
        }
    }
}

/// The question row (Return sends, Shift+Return starts a new line; Stop while an answer is written), or why no
/// question can be asked now. Its own view: `liveChatDisabledReason` follows the live captions, so they redraw only it.
private struct LiveChatInputBar: View {
    let model: AppModel
    var framed = true
    @State private var question = ""
    @FocusState private var questionFocused: Bool

    private static let inputHeight = BottomBarButtonStyle.height

    var body: some View {
        let running = model.liveChatRun?.isRunning == true
        // While an answer is written the reason is that answer: the row stays, with Stop.
        let reason = running ? nil : model.liveChatDisabledReason
        Group {
            if let reason {
                Text(reason)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: Self.inputHeight, alignment: .leading)
            } else {
                questionRow(running: running)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .modifier(FramedCard(framed: framed, cornerRadius: 20))
        // The cursor in the question when the tab opens.
        .task {
            try? await Task.sleep(for: .milliseconds(50))
            guard !Task.isCancelled else { return }
            questionFocused = true
        }
    }

    private func questionRow(running: Bool) -> some View {
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
            if running {
                Button(action: model.stopLiveChat) {
                    Label("Stop", systemImage: "stop.fill").labelStyle(.iconOnly)
                }
                .buttonStyle(BottomBarButtonStyle(prominent: true, horizontalPadding: 0))
                .disabled(model.liveChatRun?.stopping == true)
                .help("Stop the answer")
            } else {
                Button("Send", action: ask)
                    .buttonStyle(BottomBarButtonStyle(prominent: true))
                    .disabled(question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
    }

    /// Asks only when a question can be asked now, so a Return while an answer is written keeps the draft.
    private func ask() {
        let text = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, model.liveChatDisabledReason == nil else { return }
        model.askLive(text)
        question = ""
    }
}
