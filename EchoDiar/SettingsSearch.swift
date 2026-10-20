import EchoCore
import SwiftUI

/// A Settings row or group the search can find: its label as shown in the form, words users may type instead, and
/// where it lives (the sidebar section and, for sections with inner tabs, the raw id of that tab).
struct SettingsSearchItem: Identifiable {
    let title: LocalizedStringResource
    var keywords: [String] = []
    let tab: SettingsTab
    var subTab: String?

    var id: String { "\(tab)/\(subTab ?? "-")/\(title.key)" }

    /// True when `term` (trimmed, not empty) is in the title, a keyword, the section title or the inner tab title.
    func matches(_ term: String) -> Bool {
        let term = term.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !term.isEmpty else { return false }
        let texts = [String(localized: title), tab.label.title, subTabTitle].compactMap { $0 } + keywords
        return texts.contains { $0.localizedStandardContains(term) }
    }

    /// "Recording › Live transcript", with the inner tab in between when there is one and it is not the title itself.
    var path: String {
        let title = String(localized: title)
        let subTab = subTabTitle == title ? nil : subTabTitle
        return [tab.label.title, subTab, title].compactMap { $0 }.joined(separator: " › ")
    }

    /// The title of the inner tab `subTab`, as the toolbar shows it.
    var subTabTitle: String? {
        guard let subTab, let title = tab.subTabs.first(where: { $0.id == subTab })?.title else { return nil }
        return String(localized: title)
    }

    /// Every searchable row, in the order of the sidebar and of each section's tabs.
    @MainActor static let all: [SettingsSearchItem] = [
        // General
        .init(title: "Launch at login", keywords: ["startup", "login item", "open at login"], tab: .general),
        .init(title: "Show floating indicator while recording", keywords: ["pill", "overlay", "floating"], tab: .general),
        .init(title: "Hide it while an EchoDiar window is in front",
              keywords: ["floating indicator", "pill", "hide"], tab: .general),
        .init(title: "Delete conversations without confirmation", keywords: ["confirm", "trash", "library"], tab: .general),
        .init(title: "Copy consent message", keywords: ["consent", "privacy", "tell people"], tab: .general),
        .init(title: "Show onboarding again", keywords: ["permissions", "welcome", "setup"], tab: .general),

        // Recording
        .init(title: "Microphone", keywords: ["input device", "mic", "audio input"], tab: .recording, subTab: "microphone"),
        .init(title: "When the microphone disconnects", keywords: ["unplug", "headphones", "route change"],
              tab: .recording, subTab: "microphone"),
        .init(title: "Detect meetings", keywords: ["auto record", "meeting apps"] + MeetingApps.known.map(\.name),
              tab: .recording, subTab: "meetings"),
        .init(title: "Attach the indicator to the meeting window", keywords: ["pill", "anchor", "accessibility"],
              tab: .recording, subTab: "meetings"),
        .init(title: "Stop when the app is quiet for", keywords: ["auto-stop", "inactive", "idle"],
              tab: .recording, subTab: "autoStop"),
        .init(title: "Stop when the call seems over for", keywords: ["auto-stop", "call ended", "hang up"],
              tab: .recording, subTab: "autoStop"),
        .init(title: "Stop after silence of", keywords: ["auto-stop", "quiet", "silent"],
              tab: .recording, subTab: "autoStop"),
        .init(title: "Stop after a recording length of", keywords: ["auto-stop", "maximum duration", "time limit"],
              tab: .recording, subTab: "autoStop"),
        .init(title: "Transcribe automatically", keywords: ["auto transcribe", "after stop"],
              tab: .recording, subTab: "afterRecording"),
        .init(title: "Number of other speakers", keywords: ["speaker count", "diarization", "people"],
              tab: .recording, subTab: "afterRecording"),
        .init(title: "Wait when the Mac is hot or in Low Power Mode", keywords: ["battery", "thermal", "defer"],
              tab: .recording, subTab: "afterRecording"),

        // Transcription
        .init(title: "Default language", keywords: ["language", "English", "French", "Vietnamese"],
              tab: .transcription, subTab: "general"),
        .init(title: "Live transcript", keywords: ["captions", "real-time", "subtitles"],
              tab: .transcription, subTab: "general"),
        .init(title: "Live transcript engine", keywords: ["captions", "real-time", "Whisper", "Nemotron"],
              tab: .transcription, subTab: "general"),
        .init(title: "Transcription models", keywords: ["download", "Whisper", "fast", "balanced", "quality"],
              tab: .transcription, subTab: "transcriptionModels"),
        .init(title: "Cloud transcription engines", keywords: ["upload", "API", "default engine"]
              + speechToTextNames, tab: .transcription, subTab: "transcriptionModels"),
        .init(title: "Speech-to-text APIs", keywords: ["API key", "cloud transcription"] + speechToTextNames,
              tab: .transcription, subTab: "transcriptionModels"),
        .init(title: "Diarization models", keywords: ["speaker model", "speakers", "pyannote", "download"],
              tab: .transcription, subTab: "diarizationModels"),

        // AI
        .init(title: "AI models (summaries and chat)",
              keywords: ["LLM", "MLX", "Apple Foundation Models", "local", "summary", "chat"],
              tab: .providers, subTab: "onThisMac"),
        .init(title: "Local hosts", keywords: ["scan", "custom URL", "server"] + presetNames(.local),
              tab: .providers, subTab: "onThisMac"),
        .init(title: "Cloud APIs", keywords: ["API key", "provider", "LLM"] + presetNames(.openAICompatible)
              + presetNames(.anthropic), tab: .providers, subTab: "cloudAPIs"),
        .init(title: "Custom OpenAI-compatible endpoints", keywords: ["add endpoint", "base URL", "API key"],
              tab: .providers, subTab: "cloudAPIs"),
        .init(title: "CLIs", keywords: ["subscription", "command line", "terminal"] + presetNames(.cli),
              tab: .providers, subTab: "cli"),

        // Storage
        .init(title: "Total", keywords: ["disk space", "usage", "size", "storage used"], tab: .storage, subTab: "usage"),
        .init(title: "Largest conversations", keywords: ["biggest", "size"], tab: .storage, subTab: "usage"),
        .init(title: "Data folder", keywords: ["Finder", "location", "path", "open in Finder"],
              tab: .storage, subTab: "usage"),
        .init(title: "Free space", keywords: ["delete audio", "clean up", "transcribed"], tab: .storage, subTab: "audio"),
        .init(title: "Delete audio after transcription", keywords: ["remove audio", "trash", "save space"],
              tab: .storage, subTab: "audio"),
        .init(title: "Compression bitrate", keywords: ["kbps", "quality", "compress"], tab: .storage, subTab: "audio"),
        .init(title: "Keep uncompressed audio", keywords: ["CAF", "original", "lossless"],
              tab: .storage, subTab: "audio"),
        .init(title: "Auto-delete old conversations", keywords: ["retention", "clean up", "trash", "older than", "days"],
              tab: .storage, subTab: "autoDelete"),

        // Shortcuts
        .init(title: "Global record shortcut", keywords: ["hotkey", "keyboard", "record from any app"], tab: .shortcuts),
        .init(title: "In EchoDiar", keywords: ["keyboard shortcuts", "keys"]
              + ShortcutsSettingsView.inAppShortcuts.map(\.action), tab: .shortcuts),

        // About
        .init(title: "Version", keywords: ["about", "build", "update"], tab: .about),
        .init(title: "Licensed under AGPL-3.0", keywords: ["license", "open source"], tab: .about),
        .init(title: "github.com/dinhanhthi/EchoDiar", keywords: ["GitHub", "source code", "repository"], tab: .about),
    ]

    private static func presetNames(_ kind: ProviderConfig.Kind) -> [String] {
        ProviderConfig.presets.filter { $0.kind == kind }.map(\.displayName)
    }

    private static var speechToTextNames: [String] { presetNames(.speechToText) }
}

/// The settings matching the search, as surface rows on the window background; a click opens the row's section and
/// inner tab and clears the search.
struct SettingsSearchResults: View {
    let model: AppModel
    @Binding var query: String

    var body: some View {
        let items = SettingsSearchItem.all.filter { $0.matches(query) }
        Group {
            if items.isEmpty {
                Text(emptyText)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 20)
                    .padding(.vertical, 16)
                    .modifier(GlassCard(cornerRadius: 20))
                    .padding(12)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(spacing: 8) {
                        ForEach(items) { item in
                            Button { open(item) } label: { row(item) }
                                .buttonStyle(.plain)
                        }
                    }
                    .padding(12)
                }
            }
        }
        .background(WindowGlass().ignoresSafeArea())
    }

    private var emptyText: String {
        "No settings match \"\(query.trimmingCharacters(in: .whitespacesAndNewlines))\""
    }

    private func row(_ item: SettingsSearchItem) -> some View {
        HStack(spacing: 10) {
            Image(systemName: item.tab.label.icon)
                .foregroundStyle(.secondary)
                .frame(width: 18)
            Text(item.path)
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer(minLength: 8)
            Image(systemName: "chevron.right")
                .font(.caption)
                .foregroundStyle(.tertiary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .contentShape(Rectangle())
        .modifier(GlassCard())
    }

    private func open(_ item: SettingsSearchItem) {
        model.openSettings(tab: item.tab, subTab: item.subTab)
        query = ""
    }
}
