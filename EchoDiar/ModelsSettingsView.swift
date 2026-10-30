import EchoAI
import EchoCore
import EchoTranscribe
import SwiftUI

/// The Transcription tab, one inner tab per section: the default language with the transcribe-while-recording switch
/// and deleting the audio after transcription, the transcription models (local by tier, then cloud: the configured
/// engines and the speech-to-text APIs), and the diarization models (local by tier, then the cloud diarizers), with
/// Download, Cancel, Delete and Set default. Rows that cannot transcribe the default language are disabled with a note.
struct ModelsSettingsView: View {
    let model: AppModel

    /// Ids in `SettingsTab.transcription.subTabs`; picked in the toolbar.
    private enum Tab: String {
        case general, transcriptionModels, diarizationModels
    }

    private var tab: Tab { model.settingsSubTab(.transcription).flatMap(Tab.init) ?? .general }

    private static let entries = ModelCatalog.visibleEntries(
        registeredEngineRuntimes: TranscriptionPipeline.registeredRuntimes
    ).filter { $0.kind == .asr || $0.kind == .diarization }

    var body: some View {
        ScrollViewReader { proxy in
            page
                // A cloud row's Set Up: the speech-to-text API's row below (which expands itself) comes into view.
                .onChange(of: model.settingsExpandedProvider, initial: true) { _, id in
                    guard let id, tab == .transcriptionModels else { return }
                    Task {
                        await Task.yield()
                        withAnimation { proxy.scrollTo(id, anchor: .top) }
                    }
                }
        }
    }

    private var page: some View {
        VStack(spacing: 0) {
            Form {
                switch tab {
                case .general: generalSection
                case .transcriptionModels:
                    intro("Transcription turns speech into text. The transcription model listens to the recording and writes what was said, with timestamps.")
                    tierSections(.asr)
                    cloudSection(diarization: false)
                    speechToTextSection
                case .diarizationModels:
                    Section {
                        SpeakerSeparationToggle(model: model)
                        if model.settings.diarization { SpeakerCountPicker(model: model) }
                    }
                    intro("Diarization finds who spoke when. The diarization model tells the voices apart and labels each part of the transcript with its speaker.")
                    tierSections(.diarization)
                    cloudSection(diarization: true)
                }
            }
            .formStyle(.grouped)
            HStack {
                Text("Installed: \(ByteCountFormatter.string(fromByteCount: model.installedModelsBytes, countStyle: .file))")
                Spacer()
                Text("Models are downloaded only when you click Download.")
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
        }
        .task { model.refreshInstalledModels() }
    }

    private var generalSection: some View {
        Section {
            LanguagePicker(title: "Default language", selection: defaultLanguage, model: model)
            Toggle(isOn: model.setting(\.rememberRecordingLanguage)) {
                Text("Remember last record language")
                Text("Uses the language picked in the Record menu for the next recordings instead of the default language. Languages changed during a recording don't count.")
            }
            Toggle(isOn: model.setting(\.transcribeWhileRecording)) {
                Text("Transcribe while recording")
                Text("Shows the transcript during the recording. WhisperKit and Parakeet models keep it, so it is ready within seconds of Stop; Apple Speech (macOS 26+) only shows it and transcribes after Stop. Uses more CPU and memory while recording. Other engines transcribe after Stop.")
            }
            Picker(selection: model.setting(\.modelIdleMinutes)) {
                ForEach(idleMinutesOptions, id: \.self) { Text("\($0) min").tag($0) }
            } label: {
                Text("Free unused models after")
                Text("While a recording stays paused this long, the model that transcribes it is freed; it loads again when you resume. Other models are freed as soon as their job ends.")
            }
            // Also in Storage › Audio: the same setting.
            Toggle(isOn: model.setting(\.deleteAudioAfterTranscription)) {
                Text("Delete audio after transcription")
                Text("Moves the audio to the Trash once a transcription succeeds.")
            }
        }
    }

    /// What the page's models do, above their sections.
    private func intro(_ text: LocalizedStringKey) -> some View {
        Section {
            Text(text).foregroundStyle(.secondary)
        }
    }

    /// One section per tier that has `kind` entries, titled with the tier.
    private func tierSections(_ kind: ModelEntry.Kind) -> some View {
        ForEach(ModelEntry.Tier.allCases, id: \.self) { tier in
            let entries = Self.entries.filter { $0.kind == kind && $0.tier == tier }
            if !entries.isEmpty {
                Section(ModelRow.tierTitle(tier)) {
                    ForEach(entries) { ModelRow(entry: $0, model: model) }
                }
            }
        }
    }

    /// "Cloud": the model of the default cloud engine (its own box, as it follows the default), then every provider
    /// that transcribes, with Set default once configured (enabled, with a key and a model) or Set Up. With
    /// `diarization`, the default cloud diarizer's model and the providers that label speakers (configured: enabled,
    /// with a key).
    @ViewBuilder private func cloudSection(diarization: Bool) -> some View {
        if let config = defaultCloudConfig(diarization: diarization) {
            Section {
                CloudModelField(config: config, model: model, diarization: diarization)
            } header: {
                cloudHeader(diarization: diarization)
            }
            cloudProviders(header: false, diarization: diarization)
        } else {
            cloudProviders(header: true, diarization: diarization)
        }
    }

    private func cloudProviders(header: Bool, diarization: Bool) -> some View {
        Section {
            Label(diarization
                  ? "Audio leaves this Mac: the cloud diarizer uploads the system channel to the provider."
                  : "Audio leaves this Mac: cloud engines upload the recording to the provider.",
                  systemImage: "exclamationmark.icloud")
                .foregroundStyle(.orange)
            let configured = Set((diarization ? model.cloudDiarizerConfigs : model.cloudEngineConfigs).map(\.id))
            ForEach(model.providerConfigs.filter { config in
                CloudTranscription.supports(config)
                    && (!diarization || CloudTranscription.diarizationPresets.contains(config.preset ?? ""))
            }) { config in
                CloudEngineRow(config: config, model: model, configured: configured.contains(config.id),
                               diarization: diarization)
            }
        } header: {
            if header { cloudHeader(diarization: diarization) }
        }
    }

    private func cloudHeader(diarization: Bool) -> some View {
        SectionHeader(
            title: "Cloud",
            description: diarization
                ? "Only the system channel is uploaded, as one extra transcription billed by the provider; only the speaker labels are kept. Not used when the transcription model already labels speakers. Prices are approximate list prices per audio hour."
                : "Prices are approximate list prices per audio hour; the mic and system channels are sent and billed separately.")
    }

    /// The provider of the default transcription model (or, with `diarization`, of the default diarizer) when it is a
    /// configured cloud engine.
    private func defaultCloudConfig(diarization: Bool) -> ProviderConfig? {
        let defaultID = diarization ? model.defaultDiarizerModelID : model.defaultASRModelID
        guard let id = CloudTranscription.providerID(engineID: defaultID) else { return nil }
        return (diarization ? model.cloudDiarizerConfigs : model.cloudEngineConfigs).first { $0.id == id }
    }

    /// The speech-to-text APIs' keys and models; the LLM APIs that transcribe too are set up in AI.
    private var speechToTextSection: some View {
        Section {
            ForEach(model.providerConfigs.filter { $0.kind == .speechToText }) { config in
                SpeechProviderRow(config: config, model: model)
            }
        } header: {
            SectionHeader(
                title: "Speech-to-text APIs",
                description: "OpenAI, Groq, Mistral, Google Gemini and custom endpoints transcribe too, with their transcription model: set them up in Settings › AI.")
        }
    }

    /// The offered idle times, plus a hand-edited one, so the picker never shows an empty selection.
    private var idleMinutesOptions: [Int] {
        let current = model.settings.modelIdleMinutes
        let options = AppSettings.modelIdleMinutesOptions
        return options.contains(current) ? options : (options + [current]).sorted()
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
                if isDefault { Badge(text: String(localized: "Default"), color: .accentColor, filled: true) }
                Spacer(minLength: 8)
                actions
            }
            HStack(spacing: 4) {
                Badge(text: Self.tierTitle(entry.tier), color: Self.tierColor(entry.tier))
                if entry.kind == .diarization { Badge(text: String(localized: "Speakers")) }
                if entry.repo == nil {
                    Badge(text: String(localized: "System"))
                } else {
                    Badge(text: Self.sizeText(entry.diskMB))
                    let ram = (entry.ramIsEstimate ? "~" : "") + Self.ramText(entry.ramMB)
                    Badge(text: String(localized: "\(ram) RAM"))
                }
                Badge(text: Self.languagesText(entry.languages))
                Badge(text: entry.license)
                Badge(text: String(localized: "macOS \(entry.minimumMacOS)+"))
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

    /// The default ASR model or diarizer of new recordings, or the catalog default of other kinds.
    private var isDefault: Bool {
        switch entry.kind {
        case .asr: model.defaultASRModelID == entry.id
        case .diarization: model.defaultDiarizerModelID == entry.id
        default: entry.isDefault
        }
    }

    /// Set for an ASR row that cannot transcribe the next recording's language (e.g. "No Vietnamese"); Auto never
    /// disables.
    private var unsupportedNote: String? {
        let language = model.recordingLanguage
        guard entry.kind == .asr, language != LanguageOptions.auto, !language.isEmpty else { return nil }
        let primary = String(language.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).first ?? "")
        let supported = TranscriptionPipeline.supportedLanguages(for: entry)
        guard (!supported.isEmpty && !supported.contains(primary)) || !entry.isSelectable(for: language) else {
            return nil
        }
        return String(localized: "No \(LanguageOptions.name(primary))", comment: "A model that cannot transcribe the language, e.g. No Vietnamese")
    }

    @ViewBuilder private var actions: some View {
        if entry.kind == .asr || entry.kind == .diarization, !isDefault,
           model.installedModelIDs.contains(entry.id) {
            Button("Set default") {
                if entry.kind == .asr { model.setDefaultASRModel(entry.id) } else { model.setDefaultDiarizer(entry.id) }
            }
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
                // The recording's transcriber loads its model again after a long pause: deleting it meanwhile
                // would end it.
                .disabled(model.transcriptionProgress.keys.contains(where: model.isTranscribing)
                          || model.recordingCaptions != nil)
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
                if reserved.isEmpty {
                    Text("Reserved by EchoDiar: none")
                } else {
                    Text("Reserved by EchoDiar: \(reserved.joined(separator: ", "))")
                }
                Spacer(minLength: 8)
                Button("Remove language assets") { model.releaseAppleSpeechAssets() }
                    .controlSize(.small)
                    .disabled(reserved.isEmpty)
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
    }

    /// The quality in the app's three main languages, on one line ("Quality in English and French: Good · Vietnamese:
    /// Fair"), so it reads as a rating of those, not as the only languages; the languages badge says which it takes.
    /// The speaker model's quality does not depend on the language: its English note says so.
    @ViewBuilder private var notes: some View {
        let notes = entry.qualityNotes
        let rated = [("en", notes.en), ("fr", notes.fr), ("vi", notes.vi)]
            .filter { !$0.1.isEmpty }
            .map { (LanguageOptions.name($0.0), $0.1) }
        Group {
            if entry.kind == .diarization {
                if !notes.en.isEmpty { Text(notes.en) }
            } else if !rated.isEmpty {
                Text("Quality in \(Self.qualityText(rated))")
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
    }

    /// Languages with the same note joined: "English and French: Good · Vietnamese: Fair".
    private static func qualityText(_ rated: [(language: String, note: String)]) -> String {
        var groups: [(languages: [String], note: String)] = []
        for (language, note) in rated {
            if let index = groups.firstIndex(where: { $0.note == note }) {
                groups[index].languages.append(language)
            } else {
                groups.append(([language], note))
            }
        }
        return groups
            .map { "\(ListFormatter.localizedString(byJoining: $0.languages)): \($0.note)" }
            .joined(separator: " · ")
    }

    static func tierTitle(_ tier: ModelEntry.Tier) -> String {
        switch tier {
        case .fast: String(localized: "Fast")
        case .balanced: String(localized: "Balanced")
        case .quality: String(localized: "Quality")
        }
    }

    /// Fast is orange (speed), balanced teal, quality purple (best output); none is the accent blue of Default.
    private static func tierColor(_ tier: ModelEntry.Tier) -> Color {
        switch tier {
        case .fast: .orange
        case .balanced: .teal
        case .quality: .purple.mix(with: .white, by: 0.35)  // plain purple is hard to read on the dark cards
        }
    }

    static func sizeText(_ mib: Int) -> String {
        mib < 1024 ? String(localized: "\(mib) MiB") : String(localized: "\(String(format: "%.1f", Double(mib) / 1024)) GB")
    }

    private static func ramText(_ mb: Int) -> String {
        mb < 1024 ? String(localized: "\(mb) MB") : String(localized: "\(String(format: "%.1f", Double(mb) / 1024)) GB")
    }

    private static func languagesText(_ languages: ModelEntry.Languages) -> String {
        switch languages {
        case .multilingual:
            String(localized: "Multilingual")
        case .codes(let codes) where codes.count == 1:
            LanguageOptions.name(codes[0])
        case .codes(let codes):
            String(localized: "\(codes.count) languages")
        }
    }
}

private struct CloudEngineRow: View {
    let config: ProviderConfig
    let model: AppModel
    /// Enabled, with a key and a model (a key alone with `diarization`): it can be the default. Else Set Up opens where
    /// it is configured.
    let configured: Bool
    /// A cloud diarizer (Diarization models): its diarization model, Default and Set default of the default diarizer.
    var diarization = false

    /// Approximate pay-as-you-go prices per audio hour (docs/research/asr-diarization-models.md F, 2026-10-18).
    private static let prices: [String: String] = [
        "openai": "~$0.36/h", "groq": "~$0.04/h", "mistral": "~$0.18/h", "gemini": "~$0.23/h",
        "elevenlabs": "~$0.22/h", "deepgram": "~$0.26/h", "assemblyai": "~$0.23/h",
    ]

    private var engineID: String { CloudTranscription.engineID(providerID: config.id) }

    /// The default transcription model, or the default diarizer.
    private var defaultID: String { diarization ? model.defaultDiarizerModelID : model.defaultASRModelID }

    /// "Deepgram (nova-3)" with the model it runs; the provider name alone when it picks the model.
    private var name: String {
        guard diarization else { return model.cloudEngineName(config) }
        let id = CloudTranscription.diarizationModel(for: config)
        return id.isEmpty ? config.displayName : "\(config.displayName) (\(id))"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Text(configured ? name : config.displayName)
                    .lineLimit(1)
                    .truncationMode(.tail)
                if configured && defaultID == engineID {
                    Badge(text: String(localized: "Default"), color: .accentColor, filled: true)
                }
                Spacer(minLength: 8)
                if !configured {
                    Button("Set Up…", action: setUp)
                        .controlSize(.small)
                        .help(setUpHelp)
                } else if defaultID != engineID {
                    Button("Set default", action: setDefault)
                        .controlSize(.small)
                }
            }
            HStack(spacing: 4) {
                Badge(text: String(localized: "Cloud"))
                if let price = config.preset.flatMap({ Self.prices[$0] }) {
                    // Gemini also bills its output.
                    Badge(text: config.preset == "gemini" ? String(localized: "\(price) + output (approx.)")
                          : String(localized: "\(price) (approx.)"))
                }
            }
            if config.preset == "gemini" {
                Text(diarization
                     ? "LLM diarization — timestamps approximate, speakers can be mislabeled."
                     : "LLM transcription — timestamps approximate.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 2)
    }

    private var setUpHelp: LocalizedStringKey {
        guard config.kind == .speechToText else { return "Turn on \(config.displayName) in Settings › AI" }
        return diarization
            ? "Add the key of \(config.displayName) in Transcription models"
            : "Add the key of \(config.displayName) below"
    }

    private func setDefault() {
        if diarization { model.setDefaultDiarizer(engineID) } else { model.setDefaultASRModel(engineID) }
    }

    /// Speech-to-text APIs are set up in their row on the Transcription models page (from Diarization models, that
    /// page is opened first, so its scroll to the expanded row applies), the LLM APIs in Settings › AI.
    private func setUp() {
        if diarization && config.kind == .speechToText {
            model.openSettings(tab: .transcription, subTab: "transcriptionModels")
        }
        model.settingsExpandedProvider = config.id
        if config.kind != .speechToText { model.openSettings(tab: .providers, subTab: "cloudAPIs") }
    }
}

/// The default cloud engine's transcription model: free text, a menu of the known models and, for the APIs that list
/// their models, "Load models" (only the ones that transcribe). With `diarization`, the default cloud diarizer's
/// diarization model: only models that label speakers are listed, and a note says when the typed one does not.
private struct CloudModelField: View {
    let config: ProviderConfig
    let model: AppModel
    /// Edits `diarizationModel` (Diarization models) instead of `transcriptionModel`.
    var diarization = false

    @State private var loaded: [String]?
    @State private var loading = false
    @State private var error: String?

    /// The speech-to-text APIs have no model list; their known models are offered.
    private static let knownModels: [String: [String]] = OpenAICompatibleTranscriber.presetModels.merging([
        "elevenlabs": [ElevenLabsTranscriber.defaultModel], "deepgram": [DeepgramTranscriber.defaultModel],
        "gemini": [GeminiAudioTranscriber.defaultModel],
    ]) { first, _ in first }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            if config.preset == "assemblyai" {
                LabeledContent("Model") {
                    Text(diarization
                         ? "Picked by AssemblyAI (Universal, speaker labels)"
                         : "Picked by AssemblyAI for the language (Universal)")
                }
            } else {
                HStack(spacing: 8) {
                    TextField("Model", text: Binding(
                        get: { diarization ? config.diarizationModel : config.transcriptionModel },
                        set: { value in setModel(value) }
                    ), prompt: Text(prompt))
                    let known = diarization
                        ? [CloudTranscription.defaultDiarizationModel(for: config)]
                        : Self.knownModels[config.preset ?? ""] ?? []
                    let choices = Self.unique(known + (loaded ?? []))
                    if !choices.isEmpty {
                        Menu("\(choices.count) models") {
                            ForEach(choices, id: \.self) { id in
                                Button(id) { setModel(id) }
                            }
                        }
                        .fixedSize()
                    }
                    if config.kind == .openAICompatible {
                        Button("Load models") { Task { await load() } }
                            .disabled(loading)
                        if loading { ProgressView().controlSize(.small) }
                    }
                }
                if let error {
                    Text(error).font(.caption).foregroundStyle(.red).lineLimit(2)
                } else if loaded?.isEmpty == true {
                    Text(diarization
                         ? "No diarization models listed; type the model id."
                         : "No transcription models listed; type the model id.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                if diarization,
                   !CloudTranscription.diarizes(config, model: CloudTranscription.diarizationModel(for: config)) {
                    Text("This model does not label speakers; a model on this Mac is used instead.")
                        .font(.caption).foregroundStyle(.orange)
                }
            }
        }
        // Loaded models belong to the provider they came from.
        .onChange(of: config.id) {
            loaded = nil
            error = nil
        }
    }

    private var prompt: String {
        if diarization {
            let fallback = CloudTranscription.defaultDiarizationModel(for: config)
            return fallback.isEmpty ? String(localized: "Diarization model id") : fallback
        }
        let fallback = CloudTranscription.defaultModel(for: config)
        return fallback.isEmpty ? String(localized: "Transcription model id, e.g. whisper-1") : fallback
    }

    private func setModel(_ id: String) {
        model.updateProvider(id: config.id) { provider in
            if diarization { provider.diarizationModel = id } else { provider.transcriptionModel = id }
        }
    }

    private func load() async {
        loading = true
        defer { loading = false }
        do {
            let all = try await model.makeProvider(for: config)?.listModels() ?? []
            loaded = all.filter { id in
                Self.transcribes(id, preset: config.preset)
                    && (!diarization || CloudTranscription.diarizes(config, model: id))
            }.sorted()
            error = nil
        } catch {
            self.error = error.localizedDescription.split(whereSeparator: \.isNewline).first.map(String.init)
        }
    }

    /// Transcription models are named after it (whisper-1, gpt-4o-transcribe, voxtral-mini); Gemini transcribes with
    /// its own models through the native audio API.
    private static func transcribes(_ id: String, preset: String?) -> Bool {
        let id = id.lowercased()
        if preset == "gemini" { return id.contains("gemini") }
        return ["whisper", "transcribe", "voxtral", "scribe", "nova", "speech"].contains { id.contains($0) }
    }

    /// Non-empty ids, first occurrence kept.
    private static func unique(_ ids: [String]) -> [String] {
        var seen: [String] = []
        for id in ids where !id.isEmpty && !seen.contains(id) { seen.append(id) }
        return seen
    }
}

private struct Badge: View {
    let text: String
    var color: Color = .secondary
    /// White on a solid fill, for the one badge that must stand out ("Default"); else tinted text on a light fill.
    var filled = false

    var body: some View {
        Text(text)
            .font(.caption2.weight(filled ? .semibold : .medium))
            .foregroundStyle(filled ? Color.white : color)
            .lineLimit(1)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(filled ? color : color.opacity(0.18), in: Capsule())
    }
}
