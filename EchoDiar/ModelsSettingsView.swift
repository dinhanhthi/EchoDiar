import EchoAI
import EchoCore
import EchoTranscribe
import SwiftUI

/// The Transcription tab, one inner tab per section: the default language with the live transcript switch and engine,
/// the ASR and diarization catalog by tier (one tab each) with Download, Cancel, Delete and Set default, and the
/// configured cloud engines. Rows that cannot transcribe the default language are disabled with a note.
struct ModelsSettingsView: View {
    let model: AppModel

    @State private var tab = Tab.general

    private enum Tab: Hashable, CaseIterable {
        case general, tier(ModelEntry.Tier), cloud

        static let allCases: [Tab] = [.general] + ModelEntry.Tier.allCases.map(Tab.tier) + [.cloud]

        /// The stable id deep links use: "general", "tier-<tier>", "cloud".
        var id: String {
            switch self {
            case .general: "general"
            case .tier(let tier): "tier-\(tier.rawValue)"
            case .cloud: "cloud"
            }
        }

        init?(id: String) {
            guard let tab = Self.allCases.first(where: { $0.id == id }) else { return nil }
            self = tab
        }

        var title: LocalizedStringKey {
            switch self {
            case .general: "General"
            case .tier(let tier): LocalizedStringKey(tier.rawValue.capitalized)
            case .cloud: "Cloud"
            }
        }
    }

    private static let entries = ModelCatalog.visibleEntries(
        registeredEngineRuntimes: TranscriptionPipeline.registeredRuntimes
    ).filter { $0.kind != .llm }

    var body: some View {
        VStack(spacing: 0) {
            SettingsSectionTabs(selection: $tab, title: \.title) { tab in
                switch tab {
                case .general: generalSection
                case .tier(let tier):
                    Section {
                        ForEach(Self.entries.filter { $0.tier == tier }) { ModelRow(entry: $0, model: model) }
                    }
                case .cloud: cloudSection
                }
            }
            HStack {
                Text("Installed: \(ByteCountFormatter.string(fromByteCount: model.installedModelsBytes, countStyle: .file))")
                Spacer()
                Text("Models are downloaded only when you click Download.")
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 20)
            .padding(.bottom, 12)
        }
        .task { model.refreshInstalledModels() }
        .onChange(of: model.pendingSettingsSubTab, initial: true) { showPendingTab() }
        .onChange(of: model.settingsTab) { showPendingTab() }
    }

    /// Shows the inner tab a deep link asked for (`AppModel.openSettings(tab:subTab:)`) once this section is selected.
    private func showPendingTab() {
        guard model.settingsTab == .transcription, let id = model.pendingSettingsSubTab, let pending = Tab(id: id) else { return }
        tab = pending
        model.pendingSettingsSubTab = nil
    }

    private var generalSection: some View {
        Section {
            LanguagePicker(title: "Default language", selection: defaultLanguage, model: model)
            Toggle(isOn: liveTranscript) {
                Text("Live transcript")
                Text("Shows text while recording with the live engine below (more CPU). The transcript made after Stop stays the one that is saved.")
            }
            Picker(selection: liveTranscriptEngine) {
                Text("Whisper Large v3 Turbo").tag(AppSettings.defaultLiveTranscriptEngine)
                Text("Nemotron 3.5 streaming").tag(NemotronStreamingEngine.entryID)
            } label: {
                Text("Live transcript engine")
                Text("Nemotron is recommended for Vietnamese live captions (Vietnamese is experimental). Without the picked model installed, another installed live model is used.")
            }
        }
    }

    /// Configured cloud engines (AI & Cloud: enabled, with a key and a model) with Set default.
    private var cloudSection: some View {
        Section {
            Label("Audio leaves this Mac: cloud engines upload the recording to the provider.", systemImage: "exclamationmark.icloud")
                .foregroundStyle(.orange)
            let configs = model.cloudEngineConfigs
            if configs.isEmpty {
                HStack {
                    Text("No cloud engine configured.").foregroundStyle(.secondary)
                    Spacer()
                    Button("Add an API key…") { model.settingsTab = .providers }
                        .controlSize(.small)
                }
            }
            ForEach(configs) { CloudEngineRow(config: $0, model: model) }
        } footer: {
            Text("Prices are approximate list prices per audio hour; the mic and system channels are sent and billed separately.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var liveTranscript: Binding<Bool> {
        Binding(get: { model.settings.liveTranscript },
                set: { isOn in model.updateSettings { $0.liveTranscript = isOn } })
    }

    private var liveTranscriptEngine: Binding<String> {
        Binding(get: { model.settings.liveTranscriptEngine },
                set: { id in model.updateSettings { $0.liveTranscriptEngine = id } })
    }

    private var defaultLanguage: Binding<String> {
        Binding(get: { model.settings.defaultLanguage },
                set: { code in model.updateSettings { $0.defaultLanguage = code } })
    }
}

struct ModelRow: View {
    let entry: ModelEntry
    let model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Text(entry.displayName)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .help(entry.displayName)
                if isDefault { Badge(text: "Default", color: .accentColor) }
                Spacer(minLength: 8)
                actions
            }
            HStack(spacing: 4) {
                Badge(text: entry.tier.rawValue.capitalized)
                if entry.kind == .diarization { Badge(text: "Speakers") }
                if entry.liveOnly { Badge(text: "Live only") }
                if entry.repo == nil {
                    Badge(text: "System")
                } else {
                    Badge(text: Self.sizeText(entry.diskMB))
                    Badge(text: (entry.ramIsEstimate ? "~" : "") + Self.ramText(entry.ramMB) + " RAM")
                }
                Badge(text: Self.languagesText(entry.languages))
                Badge(text: entry.license)
                Badge(text: "macOS \(entry.minimumMacOS)+")
            }
            notes
            if let unsupportedNote {
                Text(unsupportedNote)
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
            if entry.runtime == .appleSpeech, model.installedModelIDs.contains(entry.id) { appleSpeechDetails }
            if UInt64(entry.ramMB) * 1_048_576 > ProcessInfo.processInfo.physicalMemory / 2 {
                Label("Needs more than half of this Mac's memory.", systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
            if let error = model.downloadErrors[entry.id] {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
            }
        }
        .padding(.vertical, 2)
    }

    /// The default ASR model of new recordings, or the catalog default of other kinds.
    private var isDefault: Bool {
        entry.kind == .asr ? model.defaultASRModelID == entry.id : entry.isDefault
    }

    /// Set for an ASR row that cannot transcribe the default language (e.g. "No Vietnamese"); Auto never disables.
    private var unsupportedNote: String? {
        let language = model.settings.defaultLanguage
        guard entry.kind == .asr, language != LanguageOptions.auto, !language.isEmpty else { return nil }
        let primary = String(language.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).first ?? "")
        let supported = TranscriptionPipeline.supportedLanguages(for: entry)
        guard (!supported.isEmpty && !supported.contains(primary)) || !entry.isSelectable(for: language) else {
            return nil
        }
        return "No \(LanguageOptions.name(primary))"
    }

    @ViewBuilder private var actions: some View {
        if entry.kind == .asr, !entry.liveOnly, !isDefault, model.installedModelIDs.contains(entry.id) {
            Button("Set default") { model.setDefaultASRModel(entry.id) }
                .controlSize(.small)
                .disabled(unsupportedNote != nil)
        }
        if entry.runtime == .appleSpeech, !model.installedModelIDs.contains(entry.id) {
            Text("Not available on this Mac")
                .font(.caption)
                .foregroundStyle(.secondary)
        } else if entry.repo == nil {
            Text("Built in")
                .font(.caption)
                .foregroundStyle(.secondary)
        } else if let download = model.downloads[entry.id] {
            ProgressView(value: download.fraction)
                .frame(width: 90)
            Text(download.fraction.formatted(.percent.precision(.fractionLength(0))))
                .font(.caption)
                .monospacedDigit()
                .foregroundStyle(.secondary)
            Button("Cancel") { model.cancelDownload(entry.id) }
                .controlSize(.small)
        } else if model.installedModelIDs.contains(entry.id) {
            Label("Installed", systemImage: "checkmark.circle.fill")
                .font(.caption)
                .foregroundStyle(.green)
            Button("Delete") { model.deleteModel(entry.id) }
                .controlSize(.small)
                .disabled(model.transcriptionProgress.keys.contains(where: model.isTranscribing))
        } else {
            Button("Download") { model.downloadModel(entry.id) }
                .controlSize(.small)
                .disabled(unsupportedNote != nil || !ProcessInfo.processInfo.isOperatingSystemAtLeast(
                    OperatingSystemVersion(majorVersion: entry.minimumMacOS, minorVersion: 0, patchVersion: 0)))
        }
    }

    /// Apple Speech keeps its language assets outside `Models/`: EchoDiar can only release its reservations.
    @ViewBuilder private var appleSpeechDetails: some View {
        let reserved = model.appleSpeechReservedLocales
        VStack(alignment: .leading, spacing: 4) {
            Text("System model — language assets installed on demand. Auto uses the default language (no language detection).")
            HStack {
                Text("Reserved by EchoDiar: \(reserved.isEmpty ? "none" : reserved.joined(separator: ", "))")
                Spacer(minLength: 8)
                Button("Remove language assets") { model.releaseAppleSpeechAssets() }
                    .controlSize(.small)
                    .disabled(reserved.isEmpty)
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
    }

    @ViewBuilder private var notes: some View {
        let fr = entry.qualityNotes.fr
        let vi = entry.qualityNotes.vi
        Group {
            if !fr.isEmpty && fr == vi {
                Text("French and Vietnamese: \(fr)")
            } else {
                if !fr.isEmpty { Text("French: \(fr)") }
                if !vi.isEmpty { Text("Vietnamese: \(vi)") }
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
    }

    private static func sizeText(_ mib: Int) -> String {
        mib < 1024 ? "\(mib) MiB" : String(format: "%.1f GB", Double(mib) / 1024)
    }

    private static func ramText(_ mb: Int) -> String {
        mb < 1024 ? "\(mb) MB" : String(format: "%.1f GB", Double(mb) / 1024)
    }

    private static func languagesText(_ languages: ModelEntry.Languages) -> String {
        switch languages {
        case .multilingual:
            "Multilingual"
        case .codes(let codes) where codes.count == 1:
            Locale(identifier: "en").localizedString(forLanguageCode: codes[0]) ?? codes[0]
        case .codes(let codes):
            "\(codes.count) languages"
        }
    }
}

private struct CloudEngineRow: View {
    let config: ProviderConfig
    let model: AppModel

    /// Approximate pay-as-you-go prices per audio hour (docs/research/asr-diarization-models.md F, 2026-10-18).
    private static let prices: [String: String] = [
        "openai": "~$0.36/h", "groq": "~$0.04/h", "mistral": "~$0.18/h", "gemini": "~$0.23/h + output",
        "elevenlabs": "~$0.22/h", "deepgram": "~$0.26/h", "assemblyai": "~$0.23/h",
    ]

    private var engineID: String { CloudTranscription.engineID(providerID: config.id) }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Text(model.cloudEngineName(config))
                    .lineLimit(1)
                    .truncationMode(.tail)
                if model.defaultASRModelID == engineID { Badge(text: "Default", color: .accentColor) }
                Spacer(minLength: 8)
                if model.defaultASRModelID != engineID {
                    Button("Set default") { model.setDefaultASRModel(engineID) }
                        .controlSize(.small)
                }
            }
            HStack(spacing: 4) {
                Badge(text: "Cloud")
                if let price = config.preset.flatMap({ Self.prices[$0] }) { Badge(text: price + " (approx.)") }
            }
            if config.preset == "gemini" {
                Text("LLM transcription — timestamps approximate.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 2)
    }
}

private struct Badge: View {
    let text: String
    var color: Color = .secondary

    var body: some View {
        Text(text)
            .font(.caption2.weight(.medium))
            .foregroundStyle(color)
            .lineLimit(1)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(color.opacity(0.18), in: Capsule())
    }
}
