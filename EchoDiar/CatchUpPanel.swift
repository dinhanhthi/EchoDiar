import AppKit
import EchoCore
import SwiftUI

/// "Catch up" in the recording header: a quick summary of the meeting so far (`AppModel.startCatchUp`); disabled
/// with `catchUpDisabledReason` as its help, else its help names the provider and where the data goes. Its own view,
/// so the live captions it follows redraw only it.
struct CatchUpButton: View {
    let model: AppModel

    var body: some View {
        let reason = model.catchUpDisabledReason
        // The help sits on a wrapper: a disabled button may show none of its own.
        HStack {
            Button(action: model.startCatchUp) {
                Label("Catch up", systemImage: "list.bullet.clipboard")
            }
            .buttonBorderShape(.capsule)
            .controlSize(.small)
            .disabled(reason != nil)
        }
        .help(Text(reason ?? help))
    }

    /// What it does, the provider it runs on, and, as the AI pane's data notes, whether the data leaves this Mac and
    /// how far a CLI agent is contained.
    private var help: String {
        var lines = [String(localized: "Summarize the meeting so far, mostly its last 5 minutes")]
        if let provider = model.catchUpProvider {
            lines.append(String(localized: "\(String(localized: "Catch-up provider")): \(provider.name)"))
            lines += [provider.leavesMacNote, provider.containmentNote].compactMap { $0 }
        }
        return lines.joined(separator: "\n")
    }
}

/// The current recording's catch-ups, between its header and its live transcript, newest first: the one being written
/// on top (a placeholder until its text comes, then its streamed Markdown, with Stop), then the saved ones. The
/// newest is open and the others collapsed, each opening or closing with a click on its header; each has Copy. At
/// most `maxHeight` tall, scrolled past it; hidden while there are none.
struct CatchUpPanel: View {
    let model: AppModel
    /// The open state the user gave cards with a click; the others: the newest open, the older ones collapsed.
    @State private var openStates: [UUID: Bool] = [:]
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private static let maxHeight: CGFloat = 280

    var body: some View {
        let cards = self.cards
        if !cards.isEmpty {
            HeightCap(maxHeight: Self.maxHeight) {
                ScrollView {
                    VStack(spacing: 6) {
                        ForEach(Array(cards.enumerated()), id: \.element.id) { index, card in
                            let open = openStates[card.id] ?? (index == 0)
                            CatchUpCardView(
                                card: card, open: open, toggle: { toggle(card.id, open: !open) }, stop: model.stopCatchUp)
                        }
                    }
                    .padding(8)
                }
                .scrollBounceBehavior(.basedOnSize)
            }
            .modifier(GlassCard(cornerRadius: 14))
            // Model Markdown: citations and web links open nothing on the recording page.
            .environment(\.openURL, OpenURLAction { _ in .discarded })
        }
    }

    /// The run first, then the saved catch-ups, newest first.
    private var cards: [CatchUpCard] {
        var cards: [CatchUpCard] = []
        if let run = model.catchUpRun {
            cards.append(CatchUpCard(
                id: run.jobID, elapsed: run.elapsed, text: run.text, running: run.isRunning, stopping: run.stopping,
                statusLine: run.statusLine, error: run.error, provider: run.providerName, model: run.model))
        }
        let providers = model.aiProviders
        cards += model.recordingCatchUps.reversed().map { CatchUpCard($0, providers: providers) }
        return cards
    }

    private func toggle(_ id: UUID, open: Bool) {
        withAnimation(reduceMotion ? nil : Motion.standard) { openStates[id] = open }
    }
}

/// One card of `CatchUpPanel`: the catch-up being written or a saved one.
struct CatchUpCard: Identifiable {
    let id: UUID
    /// Seconds of recording when it was asked for.
    let elapsed: Double
    let text: String
    var running = false
    var stopping = false
    var statusLine: String?
    var error: String?
    var stopped = false
    /// The provider's name and the model that write or wrote it.
    var provider: String?
    var model: String?
}

extension CatchUpCard {
    /// A saved catch-up's card: its provider's name while that provider is still among `providers`, else none.
    init(_ catchUp: CatchUp, providers: [AppModel.AIProvider]) {
        self.init(
            id: catchUp.id, elapsed: catchUp.elapsed, text: catchUp.text, stopped: catchUp.stopped,
            provider: providers.first { $0.id == catchUp.provider }?.name, model: catchUp.model)
    }
}

/// The header (chevron, "Catch-up at mm:ss", the provider, the model and "Stopped", then Stop or Copy) and, open,
/// the text.
struct CatchUpCardView: View {
    let card: CatchUpCard
    let open: Bool
    let toggle: () -> Void
    let stop: () -> Void
    /// Copy shows a checkmark and "Copied" for 2 s.
    @State private var copied = false
    @State private var copiedReset: Task<Void, Never>?

    private static let footerHeight: CGFloat = 22

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Button(action: toggle) {
                    HStack(spacing: 6) {
                        Image(systemName: open ? "chevron.down" : "chevron.right")
                            .font(.system(size: 10, weight: .bold))
                            .frame(width: 12)
                            .foregroundStyle(.secondary)
                        Text("Catch-up at \(Self.timestamp(card.elapsed))")
                            .font(.callout.weight(.semibold))
                        if let details {
                            Text(details)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                                .truncationMode(.tail)
                        }
                        Spacer(minLength: 0)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityValue(open ? Text("Expanded") : Text("Collapsed"))
                if card.running {
                    Button("Stop", action: stop)
                        .buttonBorderShape(.capsule)
                        .controlSize(.small)
                        .disabled(card.stopping)
                        .help("Stop the catch-up and keep what is written")
                } else if !card.text.isEmpty {
                    Button(action: copy) {
                        Label(copied ? "Copied" : "Copy", systemImage: copied ? "checkmark" : "square.on.square")
                            .labelStyle(.iconOnly)
                            .font(.caption2)
                    }
                    .buttonStyle(BottomBarButtonStyle(height: Self.footerHeight, horizontalPadding: 0))
                    .help(copied ? "Copied" : "Copy the catch-up")
                }
            }
            if open { content }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Palette.inputSurface))
    }

    /// The provider, the model and whether it was stopped.
    private var details: String? {
        let parts = [card.provider, card.model, card.stopped ? String(localized: "Stopped") : nil]
            .compactMap { $0?.isEmpty == false ? $0 : nil }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    /// Until the first text, a placeholder whose spinner fades in after 150 ms; then the text; the error under it.
    @ViewBuilder private var content: some View {
        if card.text.isEmpty && card.running {
            HStack(spacing: 6) {
                DelayedSpinner()
                Text(card.statusLine ?? String(localized: "Writing the catch-up…"))
                    .foregroundStyle(.secondary)
            }
        } else if !card.text.isEmpty {
            MarkdownText(text: card.text)
        }
        if let error = card.error {
            Text(error)
                .font(.callout)
                .foregroundStyle(.red)
                .lineLimit(4)
        }
    }

    private func copy() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(card.text, forType: .string)
        copied = true
        copiedReset?.cancel()
        copiedReset = Task {
            try? await Task.sleep(for: .seconds(2))
            guard !Task.isCancelled else { return }
            copied = false
        }
    }

    /// As the recording clock: m:ss, or h:mm:ss from an hour.
    private static func timestamp(_ seconds: Double) -> String {
        let pattern: Duration.TimeFormatStyle.Pattern = seconds >= 3_600 ? .hourMinuteSecond : .minuteSecond
        return Duration.seconds(seconds).formatted(.time(pattern: pattern))
    }
}

/// A small spinner shown only after 150 ms (as `DetailLoadingPane`'s), so a fast answer does not flash it.
private struct DelayedSpinner: View {
    @State private var shown = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ProgressView()
            .controlSize(.small)
            .opacity(shown ? 1 : 0)
            .task {
                try? await Task.sleep(for: .milliseconds(150))
                guard !Task.isCancelled else { return }
                withAnimation(reduceMotion ? nil : Motion.standard) { shown = true }
            }
    }
}

/// Its content at its ideal height (a scroll view's: its content's), at most `maxHeight`, at the proposed width; in
/// one layout pass, so a new card shows in the frame it is added.
private struct HeightCap: Layout {
    let maxHeight: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard let content = subviews.first else { return .zero }
        let ideal = content.sizeThatFits(ProposedViewSize(width: proposal.width, height: nil))
        return CGSize(width: proposal.width ?? ideal.width, height: min(ideal.height, maxHeight))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        subviews.first?.place(at: bounds.origin, proposal: ProposedViewSize(width: bounds.width, height: bounds.height))
    }
}
