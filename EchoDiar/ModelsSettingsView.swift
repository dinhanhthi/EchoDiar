import EchoAI
import EchoCore
import EchoTranscribe
import SwiftUI

/// The Transcription tab, one inner tab per section: the default language with the transcribe-while-recording switch
/// and deleting the audio after transcription, the transcription models (local by tier, then the cloud engines with
/// their models), and the diarization models (local by tier, then the cloud diarizers), with Download, Cancel, Delete
/// and Set default. Rows that cannot transcribe the default language are disabled with a note.
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
        VStack(spacing: 0) {
            Form {
                switch tab {
                case .general: generalSection
                case .transcriptionModels:
                    intro("Transcription turns speech into text. The transcription model listens to the recording and writes what was said, with timestamps.")
                    tierSections(.asr)
                    cloudSection(diarization: false)
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
                Text("Shows the transcript during the recording. WhisperKit and Parakeet models keep it, so it is ready within seconds of Stop; Apple Speech (macOS 26+) only shows it and transcribes after Stop. Uses more CPU and memory while recording. A cloud engine can show it with a local model or its realtime stream: see Transcription model › Cloud.")
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

    /// "Cloud": every provider that transcribes, with its model, its key status and Set default once configured
    /// (available, with a model). With `diarization`, the providers that label speakers, with their diarization model
    /// (configured: available).
    private func cloudSection(diarization: Bool) -> some View {
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
                if !diarization, model.defaultCloudConfig?.id == config.id {
                    CloudLiveOptions(config: config, model: model)
                }
            }
        } header: {
            cloudHeader(diarization: diarization)
        }
    }

    private func cloudHeader(diarization: Bool) -> some View {
        SectionHeader(
            title: "Cloud",
            description: diarization
                ? "Only the system channel is uploaded, as one extra transcription billed by the provider; only the speaker labels are kept. Not used when the transcription model already labels speakers. Prices are approximate list prices per audio hour."
                : "Prices are approximate list prices per audio hour; the mic and system channels are sent and billed separately.")
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
                if entry.repo != nil {  // a system model's tier badge already says "System"
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
                // would end it. An in-app AI job, running or queued, may be using it too.
                .disabled(model.transcriptionProgress.keys.contains(where: model.isTranscribing)
                          || model.recordingCaptions != nil || model.aiJobs.hasInAppJob)
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
        case .system: String(localized: "System")
        }
    }

    /// Fast is orange (speed), balanced teal, quality purple (best output), system pink (built into macOS); none is the
    /// accent blue of Default nor the green of Installed.
    private static func tierColor(_ tier: ModelEntry.Tier) -> Color {
        switch tier {
        case .fast: .orange
        case .balanced: .teal
        case .quality: .purple.mix(with: .white, by: 0.35)  // plain purple is hard to read on the dark cards
        case .system: .pink
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
    /// Available, with a model (available alone with `diarization`): it can be the default. Its key is saved in API
    /// Keys, which "API key" opens.
    let configured: Bool
    /// A cloud diarizer (Diarization models): its diarization model, Default and Set default of the default diarizer.
    var diarization = false

    /// Approximate pay-as-you-go prices per audio hour (docs/research/asr-diarization-models.md F, 2026-10-18; xAI,
    /// Speechmatics Enhanced, Soniox and Gladia solaria-1 2026-11-04).
    private static let prices: [String: String] = [
        "openai": "~$0.36/h", "groq": "~$0.04/h", "mistral": "~$0.18/h", "gemini": "~$0.23/h",
        "elevenlabs": "~$0.22/h", "deepgram": "~$0.26/h", "assemblyai": "~$0.23/h",
        "xai": "~$0.10/h", "speechmatics": "~$0.38/h", "soniox": "~$0.10/h", "gladia": "~€0.27/h",
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
                if configured && defaultID != engineID {
                    Button("Set default", action: setDefault)
                        .controlSize(.small)
                }
                APIKeyStatus(config: config, model: model)
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
            CloudModelField(config: config, model: model, diarization: diarization)
            // A custom endpoint transcribes once its base URL (set in API Keys) and its model are set.
            if config.preset == nil, config.baseURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                Text("Base URL not set")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .inputColumn()
            }
        }
        .padding(.vertical, 2)
    }

    private func setDefault() {
        if diarization { model.setDefaultDiarizer(engineID) } else { model.setDefaultASRModel(engineID) }
    }
}

/// "While recording" in the default cloud engine's card: one live source at a time (off, a local model, or the
/// provider's realtime stream; `setCloudLiveSource`), the realtime model and host, and "Use the realtime result as the
/// transcript", each with what it costs. Greyed with the reason while a master switch is off.
private struct CloudLiveOptions: View {
    let config: ProviderConfig
    let model: AppModel

    /// The realtime destination's one-time consent, asked before the source switches to realtime.
    @State private var confirmingRealtime = false

    /// Billing basis of each realtime API (`stt-protocols.md`); not stated for xAI, Speechmatics and Gladia.
    private enum Billing { case connection, audio }

    private static let billing: [String: Billing] = [
        "assemblyai": .connection, "soniox": .connection,
        "elevenlabs": .audio, "deepgram": .audio, "openai": .audio,
    ]

    /// Approximate realtime list prices per hour of one connection, by "<preset>/<model>"
    /// (docs/research/asr-diarization-models.md F.1, 2026-11-04).
    private static let prices: [String: String] = [
        "elevenlabs/scribe_v2_realtime": "$0.39", "deepgram/nova-3": "$0.46",
        "assemblyai/universal-3-6-pro": "$0.45", "assemblyai/universal-streaming-multilingual": "$0.15",
        "assemblyai/universal-streaming-english": "$0.15", "openai/gpt-live-transcribe": "$1.02",
        "openai/gpt-transcribe": "$0.27", "openai/gpt-realtime-whisper": "$1.02", "xai/grok-voice-transcribe-2.0": "$0.20",
        "speechmatics/enhanced": "$0.40", "speechmatics/standard": "$0.23", "speechmatics/melia-1": "$0.20",
        "speechmatics/oak-1": "$0.23", "soniox/stt-rt-v6": "$0.12", "gladia/solaria-1": "€0.68",
    ]

    /// With streaming speaker labels ("Tell speakers apart"): Deepgram and AssemblyAI add $0.12/h.
    private static let speakerPrices: [String: String] = [
        "deepgram/nova-3": "$0.58", "assemblyai/universal-3-6-pro": "$0.57",
        "assemblyai/universal-streaming-multilingual": "$0.27", "assemblyai/universal-streaming-english": "$0.27",
    ]

    private var source: CloudLiveSource { model.settings.cloudLiveSource }

    /// "Transcribe automatically" or "Transcribe while recording" is off: nothing runs while recording.
    private var masterOff: Bool { !model.settings.autoTranscribe || !model.settings.transcribeWhileRecording }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("While recording")
                .font(.headline)
            if masterOff {
                Text(RecordingDetailView.noCaptionsReason(settings: model.settings))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        Group {
            localRows
            if model.realtimeAvailable(for: config) { realtimeRows }
        }
        .disabled(masterOff)
    }

    @ViewBuilder private var localRows: some View {
        Toggle(isOn: Binding(get: { source == .local }, set: { model.setCloudLiveSource($0 ? .local : .off) })) {
            Text("Live with a local model")
            Text("Shows the transcript while recording with a model on this Mac. Free: nothing is uploaded while recording, but it uses CPU, memory and battery. The saved transcript still comes from \(config.displayName) after Stop, so it can differ from what you saw.")
        }
        let choices = model.cloudLiveModelChoices
        if choices.isEmpty {
            Text("No live model installed. Download a WhisperKit or Parakeet model above.")
                .font(.caption)
                .foregroundStyle(.orange)
        } else if source == .local {
            Picker("Live model", selection: Binding(
                get: { choices.contains { $0.id == model.settings.cloudLiveModelID } ? model.settings.cloudLiveModelID : nil },
                set: { id in model.updateSettings { $0.cloudLiveModelID = id } }
            )) {
                Text("Automatic").tag(String?.none)
                ForEach(choices) { entry in Text(entry.displayName).tag(Optional(entry.id)) }
            }
        }
    }

    @ViewBuilder private var realtimeRows: some View {
        Toggle(isOn: Binding(get: { source == .realtime }, set: setRealtime)) {
            Text("Live with \(config.displayName) realtime")
            Text(realtimeCopy)
        }
        .alert("Send audio to \(config.displayName)?", isPresented: $confirmingRealtime) {
            Button("Send") {
                model.confirmRealtimeDestination(for: config)
                model.setCloudLiveSource(.realtime)
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Audio leaves this Mac: while recording, the mic and system channels are streamed live to \(config.displayName) (\(model.realtimeHost(for: config) ?? "")) for transcription and billed by them. The provider may keep the audio under its own retention policy. You are asked once per provider and destination.")
        }
        if source == .realtime {
            VStack(alignment: .leading, spacing: 4) {
                CloudModelField(config: config, model: model, realtime: true)
                if let host = model.realtimeHost(for: config) {
                    Text("Streams to \(host)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .inputColumn()
                }
            }
        }
        Toggle(isOn: Binding(get: { model.settings.realtimeAsFinal }, set: { model.setRealtimeAsFinal($0) })) {
            Text("Use the realtime result as the transcript")
            Text(finalCopy)
        }
        .disabled(source != .realtime)
    }

    /// Turning realtime on asks the destination's consent first when it is not confirmed; the source stays as it is
    /// until Send.
    private func setRealtime(_ on: Bool) {
        if on, !model.realtimeDestinationConfirmed(for: config) {
            confirmingRealtime = true
        } else {
            model.setCloudLiveSource(on ? .realtime : .off)
        }
    }

    private var realtimeCopy: String {
        let name = config.displayName
        return String(localized: "Streams the mic and the system audio to \(name) as two live connections for the whole recording. Costs money: \(billingText), on top of the normal transcription after Stop. Long recordings reconnect before the provider's session limit and stream about 30 s twice at each reconnect so speakers keep their labels. A 1-hour meeting costs about 2 hours of streaming plus 2 hours of transcription.")
    }

    /// "billed by connection time, about $0.45 per hour for each connection", from the streamed model's price (the
    /// default model's when it has none listed); the speaker-label add-on included with "Tell speakers apart".
    private var billingText: String {
        let preset = config.preset ?? ""
        let key = "\(preset)/\(model.realtimeModel(for: config))"
        let defaultKey = "\(preset)/\(model.realtimeDefaultModel(for: config))"
        let speakers = model.settings.diarization
        let price = (speakers ? Self.speakerPrices[key] : nil) ?? Self.prices[key]
            ?? (speakers ? Self.speakerPrices[defaultKey] : nil) ?? Self.prices[defaultKey]
        guard let price else { return String(localized: "billed by \(config.displayName)") }
        switch Self.billing[preset] {
        case .connection: return String(localized: "billed by connection time, about \(price) per hour for each connection")
        case .audio: return String(localized: "billed per streamed minute, about \(price) per hour for each connection")
        case nil: return String(localized: "about \(price) per hour of streaming for each connection")
        }
    }

    private var finalCopy: String {
        let name = config.displayName
        let batch = model.cloudEngineName(config)
        let copy = String(localized: "Saves the streamed transcript instead of uploading the recording again after Stop, so you pay for streaming only. Parts missed while the connection was down are sent to \(name) after Stop, with about 30 s of audio on each side, and around each pause, so speakers keep their labels. A cloud diarizer is billed separately. Realtime models can be less accurate than \(batch).")
        // The batch model labelled the speakers itself; the realtime one does not, so the diarizer comes back.
        let streamsSpeakers = CloudRealtime.make(config: config, apiKey: "", language: nil, diarize: true)?
            .streamsSpeakers ?? false
        guard model.settings.diarization, !streamsSpeakers,
              CloudTranscription.diarizes(config, model: CloudTranscription.model(for: config)) else { return copy }
        return copy + " " + String(localized: "This realtime model labels no speakers, so the diarizer runs on the recording after Stop; a cloud diarizer asks for its own consent.")
    }
}

/// A cloud engine's transcription model: free text, a menu of the known models and, for the APIs that list their
/// models, "Load models" (only the ones that transcribe). With `diarization`, the cloud diarizer's diarization model:
/// only models that label speakers are listed, and a note says when the typed one does not. With `realtime`, the
/// realtime model streamed while recording: its known models only.
private struct CloudModelField: View {
    let config: ProviderConfig
    let model: AppModel
    /// Edits `diarizationModel` (Diarization models) instead of `transcriptionModel`.
    var diarization = false
    /// Edits `realtimeModel` ("Live with <Provider> realtime") instead of `transcriptionModel`.
    var realtime = false

    @State private var loaded: [String]?
    @State private var loading = false
    @State private var error: String?

    /// The speech-to-text APIs have no model list; their known models are offered.
    private static let knownModels: [String: [String]] = OpenAICompatibleTranscriber.presetModels.merging([
        "elevenlabs": [ElevenLabsTranscriber.defaultModel], "deepgram": [DeepgramTranscriber.defaultModel],
        "gemini": [GeminiAudioTranscriber.defaultModel], "xai": [XAITranscriber.defaultModel],
        "speechmatics": [SpeechmaticsTranscriber.defaultModel, "standard", "melia-1"],
        "soniox": [SonioxTranscriber.defaultModel], "gladia": [GladiaTranscriber.defaultModel],
    ]) { first, _ in first }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            if config.preset == "assemblyai", !realtime {
                FieldRow("Model") {
                    Text(diarization
                         ? "Picked by AssemblyAI (Universal, speaker labels)"
                         : "Picked by AssemblyAI for the language (Universal)")
                }
            } else {
                FieldRow(realtime ? "Realtime model" : "Model") {
                    TextField("Model", text: Binding(
                        get: {
                            realtime ? config.realtimeModel
                                : diarization ? config.diarizationModel : config.transcriptionModel
                        },
                        set: { value in setModel(value) }
                    ), prompt: Text(prompt))
                    .labelsHidden()
                    let known = realtime ? model.realtimeModels(for: config)
                        : diarization ? [CloudTranscription.defaultDiarizationModel(for: config)]
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
                    if config.kind == .openAICompatible, !realtime {
                        Button("Load models") { Task { await load() } }
                            .disabled(loading)
                        if loading { ProgressView().controlSize(.small) }
                    }
                }
                if let error {
                    Text(error).font(.caption).foregroundStyle(.red).lineLimit(2).inputColumn()
                } else if loaded?.isEmpty == true {
                    Text(diarization
                         ? "No diarization models listed; type the model id."
                         : "No transcription models listed; type the model id.")
                        .font(.caption).foregroundStyle(.secondary).inputColumn()
                }
                // A streaming-only diarization model falls back to this Mac's diarizer: the note below says so.
                if !diarization, !realtime, CloudTranscription.isStreamingOnly(CloudTranscription.model(for: config)) {
                    Text("This model only streams live audio and can't transcribe a recording. Pick another model.")
                        .font(.caption).foregroundStyle(.red).inputColumn()
                }
                if diarization,
                   !CloudTranscription.diarizes(config, model: CloudTranscription.diarizationModel(for: config)) {
                    Text("This model does not label speakers; a model on this Mac is used instead.")
                        .font(.caption).foregroundStyle(.orange).inputColumn()
                }
            }
        }
    }

    private var prompt: String {
        if realtime { return model.realtimeDefaultModel(for: config) }
        if diarization {
            let fallback = CloudTranscription.defaultDiarizationModel(for: config)
            return fallback.isEmpty ? String(localized: "Diarization model id") : fallback
        }
        let fallback = CloudTranscription.defaultModel(for: config)
        return fallback.isEmpty ? String(localized: "Transcription model id, e.g. whisper-1") : fallback
    }

    private func setModel(_ id: String) {
        model.updateProvider(id: config.id) { provider in
            if realtime {
                provider.realtimeModel = id
            } else if diarization {
                provider.diarizationModel = id
            } else {
                provider.transcriptionModel = id
            }
        }
    }

    private func load() async {
        loading = true
        defer { loading = false }
        do {
            let all = try await model.makeProvider(for: config)?.listModels() ?? []
            loaded = all.filter { id in
                Self.transcribes(id, preset: config.preset) && !CloudTranscription.isStreamingOnly(id)
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
