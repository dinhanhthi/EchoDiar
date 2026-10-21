import EchoTranscribe
import SwiftUI

/// Live captions on the recording page, under its header (setting "Transcribe while recording"): the transcriber's
/// state, then the newest lines, scrolled to the bottom as they arrive, filling the page.
struct LiveTranscriptView: View {
    let snapshot: RecordingTranscriber.Snapshot

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(statusText, systemImage: "captions.bubble")
                .font(.caption)
                .foregroundStyle(statusIsError ? AnyShapeStyle(.orange) : AnyShapeStyle(.secondary))
                .help(failureMessage ?? "")
            if !snapshot.lines.isEmpty {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 4) {
                            ForEach(snapshot.lines) { line in
                                Text("\(Text(line.speaker.rawValue).bold().foregroundStyle(Self.color(line.speaker))): \(line.text)")
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .id(line.id)
                            }
                        }
                        .textSelection(.enabled)
                    }
                    .onAppear { scrollToBottom(proxy) }
                    .onChange(of: snapshot.lines.last?.id) { scrollToBottom(proxy) }
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .modifier(GlassCard(cornerRadius: 14))
    }

    private var statusText: String {
        switch snapshot.state {
        case .loading: "Live transcript: loading the model…"
        case .paused: "Live transcript: paused"
        case .listening where snapshot.deferred:
            "Paused while the Mac is hot or in Low Power Mode. The rest is transcribed after Stop."
        case .listening: snapshot.lines.isEmpty ? "Live transcript: listening…" : "Live transcript"
        case .failed: "Live transcript stopped. The transcript is made after Stop."
        }
    }

    /// Why the transcriber stopped, as a tooltip on the status.
    private var failureMessage: String? {
        if case .failed(let message) = snapshot.state { message } else { nil }
    }

    private var statusIsError: Bool {
        switch snapshot.state {
        case .failed: true
        case .loading, .listening, .paused: false
        }
    }

    /// Same colors as the transcript: Me in the accent color.
    private static func color(_ speaker: RecordingTranscriber.Speaker) -> Color {
        speaker == .me ? .accentColor : .orange
    }

    private func scrollToBottom(_ proxy: ScrollViewProxy) {
        guard let id = snapshot.lines.last?.id else { return }
        proxy.scrollTo(id, anchor: .bottom)
    }
}
