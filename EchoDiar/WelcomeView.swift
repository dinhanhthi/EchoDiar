import SwiftUI

/// The library page's detail when no entry is shown (the list, a conversation that is gone, a recording that starts or
/// has ended): the logo, "Welcome to EchoDiar" and a hint to pick a conversation in the sidebar ("Starting…" while a
/// recording starts). With an empty library and nothing recording, it invites the first recording instead: the
/// shortcut that starts one and a Record button ("Starting…" while the recording starts).
/// In a card filling the detail, like `TranscriptView`'s panes.
struct WelcomeView: View {
    let model: AppModel

    var body: some View {
        VStack(spacing: 10) {
            if let logo = AboutInfo.logo {
                Image(nsImage: logo)
                    .resizable()
                    .interpolation(.high)
                    .frame(width: 128, height: 128)
                    .accessibilityHidden(true)
            }
            Text("Welcome to EchoDiar").font(.title2.weight(.semibold))
            if firstRecording {
                Text("Record your first conversation")
                    .foregroundStyle(.secondary)
                Text(shortcutHint)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                // The toolbar's Record and ⌘R, which open the recording page for the live transcript (this pane while
                // it starts: the button turns to a disabled "Starting…", as the toolbar's Record is disabled meanwhile).
                Button {
                    model.toggleRecording()
                } label: {
                    if model.isStarting {
                        HStack(spacing: 6) {
                            ProgressView().controlSize(.small)
                            Text("Starting…")
                        }
                    } else {
                        Text("Record")
                    }
                }
                .disabled(model.isStarting)
                .padding(.top, 6)
            } else if model.isStarting {
                // The recording page's placeholder until the recording runs.
                StartingHint()
            } else {
                Text("Pick a conversation in the sidebar.")
                    .foregroundStyle(.secondary)
            }
        }
        .multilineTextAlignment(.center)
        .frame(maxWidth: 360)
        .padding(20)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .modifier(GlassCard(cornerRadius: 20))
        .padding(12)
    }

    /// The library is empty and no recording runs (one starting keeps the invite, as the sidebar is still empty).
    private var firstRecording: Bool {
        model.conversations.isEmpty && !model.isRecording
    }

    /// The global record shortcut while it is on; else ⌘R, which works while EchoDiar is active.
    private var shortcutHint: LocalizedStringKey {
        if model.settings.globalHotKeyEnabled {
            "Press \(model.settings.globalHotKey.displayString) to start recording from anywhere."
        } else {
            "Press ⌘R to start recording."
        }
    }
}

/// "Starting…" with a spinner, in place of the hint while a recording starts; it fades in once the start takes 150 ms,
/// as `DetailLoadingPane`'s spinner, so a fast start does not flash it.
private struct StartingHint: View {
    @State private var shown = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 6) {
            ProgressView().controlSize(.small)
            Text("Starting…")
        }
        .foregroundStyle(.secondary)
        .opacity(shown ? 1 : 0)
        .task {
            try? await Task.sleep(for: .milliseconds(150))
            guard !Task.isCancelled else { return }
            withAnimation(reduceMotion ? nil : Motion.standard) { shown = true }
        }
    }
}
