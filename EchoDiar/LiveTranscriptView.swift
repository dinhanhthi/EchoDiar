import EchoTranscribe
import SwiftUI

/// Live captions under the recording header (setting "Live transcript"): the model state, then the newest lines,
/// scrolled to the bottom as they arrive.
struct LiveTranscriptView: View {
    let snapshot: LiveTranscriber.Snapshot

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(statusText, systemImage: "captions.bubble")
                .font(.caption)
                .foregroundStyle(statusIsError ? AnyShapeStyle(.orange) : AnyShapeStyle(.secondary))
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
                    .frame(maxHeight: 120)
                    .onAppear { scrollToBottom(proxy) }
                    .onChange(of: snapshot.lines.last?.id) { scrollToBottom(proxy) }
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .modifier(GlassCard(cornerRadius: 14))
    }

    private var statusText: String {
        switch snapshot.state {
        case .loading: "Live transcript: loading the model…"
        case .listening: snapshot.lines.isEmpty ? "Live transcript: listening…" : "Live transcript"
        case .noModel: "No live model installed — download a Whisper model in Settings › Transcription."
        case .failed(let message): "Live transcript stopped: \(message)"
        }
    }

    private var statusIsError: Bool {
        switch snapshot.state {
        case .noModel, .failed: true
        case .loading, .listening: false
        }
    }

    /// Same colors as the transcript: Me in the accent color.
    private static func color(_ speaker: LiveTranscriber.Speaker) -> Color {
        speaker == .me ? .accentColor : .orange
    }

    private func scrollToBottom(_ proxy: ScrollViewProxy) {
        guard let id = snapshot.lines.last?.id else { return }
        proxy.scrollTo(id, anchor: .bottom)
    }
}
