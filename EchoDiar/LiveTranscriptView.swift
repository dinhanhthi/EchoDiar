import EchoTranscribe
import SwiftUI

/// Live captions on the recording page, under its header (setting "Transcribe while recording"): the transcriber's
/// state (how far behind decoding is when it falls behind), then the newest lines and the open chunks' greyed
/// tentative text as one selectable text (`LiveCaptionsTextView`), scrolled to the bottom as they arrive, filling the
/// page.
struct LiveTranscriptView: View {
    let snapshot: RecordingTranscriber.Snapshot

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(statusText, systemImage: "captions.bubble")
                .font(.caption)
                .foregroundStyle(statusIsError ? AnyShapeStyle(.orange) : AnyShapeStyle(.secondary))
                .help(failureMessage ?? "")
            if !snapshot.lines.isEmpty || !snapshot.tentative.isEmpty {
                LiveCaptionsTextView(
                    lines: snapshot.lines, tentative: snapshot.tentative, name: Self.name,
                    color: { NSColor(Self.color($0)) })
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .modifier(GlassCard(cornerRadius: 14))
    }

    private var statusText: LocalizedStringKey {
        switch snapshot.state {
        case .loading: "Live transcript: loading the model…"
        case .preparing: "Live transcript: preparing the language…"
        case .paused: "Live transcript: paused"
        case .listening where snapshot.deferred:
            "Paused while the Mac is hot or in Low Power Mode. The rest is transcribed after Stop."
        case .listening:
            if let behind = RecordingTranscriber.behindSeconds(snapshot) {
                "Live transcript: \(behind) s behind"
            } else if snapshot.lines.isEmpty && snapshot.tentative.isEmpty {
                "Live transcript: listening…"
            } else {
                "Live transcript"
            }
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
        case .loading, .preparing, .listening, .paused: false
        }
    }

    /// The line's label; `Speaker` raw values stay English.
    private static func name(_ speaker: RecordingTranscriber.Speaker) -> String {
        switch speaker {
        case .me: String(localized: "Me")
        case .others: String(localized: "Others")
        }
    }

    /// Same colors as the transcript: Me in the accent color.
    private static func color(_ speaker: RecordingTranscriber.Speaker) -> Color {
        speaker == .me ? .accentColor : .orange
    }
}
