import EchoTranscribe
import SwiftUI

/// Live captions on the recording page, under its header (setting "Transcribe while recording"): the transcriber's
/// state (how far behind decoding is when it falls behind), then the newest lines and the open chunks' greyed
/// tentative text as one selectable text (`LiveCaptionsTextView`), scrolled to the bottom as they arrive, filling the
/// page. Cloud realtime says when it connects, reconnects or a connection stopped (a short message per failure kind);
/// a display-only transcript says the saved one is made after Stop.
struct LiveTranscriptView: View {
    let snapshot: RecordingTranscriber.Snapshot
    /// The default cloud provider's name, for the cloud realtime status.
    var providerName: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Group {
                if snapshot.source == .cloudRealtime {
                    Label(realtimeStatusText, systemImage: "captions.bubble")
                } else {
                    Label(statusText, systemImage: "captions.bubble")
                }
            }
            .font(.caption)
            .foregroundStyle(statusIsError ? AnyShapeStyle(.orange) : AnyShapeStyle(.secondary))
            .help(failureMessage ?? "")
            if !snapshot.lines.isEmpty || !snapshot.tentative.isEmpty {
                LiveCaptionsTextView(
                    lines: snapshot.lines, tentative: snapshot.tentative, name: Self.name,
                    color: { NSColor(Self.color($0)) })
            }
            if snapshot.displayOnly {
                Text("The saved transcript is made after Stop and can differ.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
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

    /// Cloud realtime: connecting until a channel is live, then reconnecting or why a channel stopped; never the
    /// failure's raw kind.
    private var realtimeStatusText: String {
        let name = providerName ?? String(localized: "the provider")
        switch snapshot.state {
        case .failed:
            return realtimeFailureText(name) + " " + String(localized: "The transcript is made after Stop.")
        case .paused:
            return String(localized: "Live transcript: paused")
        case _ where snapshot.realtimeFailure != nil:
            // One channel stopped, the other is live or still connecting.
            return realtimeFailureText(name)
        case .loading, .preparing:
            return String(localized: "Connecting to \(name)…")
        case .listening:
            if snapshot.reconnecting { return String(localized: "Reconnecting…") }
            return snapshot.lines.isEmpty && snapshot.tentative.isEmpty
                ? String(localized: "Live transcript: listening…") : String(localized: "Live transcript")
        }
    }

    /// A short message for `Snapshot.realtimeFailure`'s kind; other kinds (network, HTTP codes) are a lost connection.
    private func realtimeFailureText(_ name: String) -> String {
        switch snapshot.realtimeFailure {
        case "auth": String(localized: "\(name) rejected the API key. Check it in Settings › AI Providers › Cloud.")
        case "payment": String(localized: "\(name) needs billing or credits on your account.")
        case "plan_limit": String(localized: "Your \(name) plan's limit on live sessions was reached.")
        case "config": String(localized: "\(name) doesn't support these settings or this language live.")
        default: String(localized: "The connection to \(name) was lost.")
        }
    }

    /// Why the transcriber stopped, as a tooltip on the status; cloud realtime's status already says it.
    private var failureMessage: String? {
        guard snapshot.source != .cloudRealtime else { return nil }
        if case .failed(let message) = snapshot.state { return message } else { return nil }
    }

    private var statusIsError: Bool {
        switch snapshot.state {
        case .failed: true
        case .loading, .preparing, .listening: snapshot.source == .cloudRealtime && snapshot.realtimeFailure != nil
        case .paused: false
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
