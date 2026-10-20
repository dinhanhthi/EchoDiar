import EchoAI
import EchoCore
import SwiftUI

/// The AI tab, in inner tabs: on this Mac (the AI models for summaries and chat, local hosts scanned on demand),
/// cloud API presets with custom OpenAI-compatible endpoints, and subscription CLIs (the speech-to-text APIs are in
/// Transcription). API keys go to the Keychain only; everything else is saved in `AppSettings.providers`.
struct ProvidersSettingsView: View {
    let model: AppModel

    @State private var hosts: [DiscoveredHost]?
    @State private var scanning = false
    @State private var customLocalURL = ""
    @State private var cliStatus: [CLIPreset: CLIStatus] = [:]

    /// Ids in `SettingsTab.providers.subTabs`; picked in the toolbar.
    private enum Tab: String {
        case onThisMac, cloudAPIs, cli
    }

    private var tab: Tab { model.settingsSubTab(.providers).flatMap(Tab.init) ?? .onThisMac }

    private static let mlxEntries = ModelCatalog.llm.filter { $0.runtime == .mlx }

    var body: some View {
        Form {
            switch tab {
            case .onThisMac:
                llmSection
                localHosts
            case .cloudAPIs:
                dataLeavesNotice
                Section {
                    ForEach(configs { $0.preset != nil && [.openAICompatible, .anthropic].contains($0.kind) }) { config in
                        APIProviderRow(config: config, model: model)
                    }
                }
                Section {
                    ForEach(configs { $0.preset == nil && $0.kind == .openAICompatible }) { config in
                        APIProviderRow(config: config, model: model)
                    }
                    Button("Add Endpoint") {
                        model.updateSettings { $0.providers.append(.custom()) }
                    }
                    .controlSize(.small)
                } header: {
                    Text("Custom OpenAI-compatible endpoints")
                }
            case .cli:
                dataLeavesNotice
                Section {
                    ForEach(configs { $0.kind == .cli }) { config in
                        if let preset = CLIPreset(rawValue: config.id) {
                            CLIProviderRow(config: config, preset: preset, status: cliStatus[preset], model: model) {
                                detect(preset)
                            }
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .task { await scan() }
        .task { model.refreshInstalledModels() }
        .task {
            // Once per window: the first PATH lookup runs the login shell (~4 s) and each check spawns `--version`.
            if cliStatus.isEmpty { CLIPreset.allCases.forEach(detect) }
        }
    }

    private func configs(_ isIncluded: (ProviderConfig) -> Bool) -> [ProviderConfig] {
        model.providerConfigs.filter(isIncluded)
    }

    private var dataLeavesNotice: some View {
        Section {
            Label("Data sent to cloud providers or CLIs leaves this Mac.", systemImage: "info.circle")
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - AI models

    /// Models for summaries and chat: Apple Foundation Models' status (macOS 26), then the MLX downloads.
    private var llmSection: some View {
        Section {
            if #available(macOS 26, *), PlatformFeatures.foundationModels {
                LabeledContent("Apple Foundation Models (system)") {
                    if let reason = AppleFoundationModelProvider.unavailableReason {
                        Text(reason).foregroundStyle(.secondary)
                    } else {
                        Label("Available", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                    }
                }
            }
            ForEach(Self.mlxEntries) { ModelRow(entry: $0, model: model) }
        } header: {
            Text("AI models (summaries and chat)")
        }
    }

    // MARK: - Local hosts

    /// Scan sits on the header's line, at its trailing edge.
    private var localHosts: some View {
        Section {
            ForEach(hosts ?? [], id: \.baseURL) { host in
                HStack {
                    VStack(alignment: .leading) {
                        Text(ProviderConfig.presets.first { $0.id == host.preset }?.displayName ?? host.preset)
                        Text("\(host.baseURL) - \(host.models.count) model\(host.models.count == 1 ? "" : "s")")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    if model.settings.providers.contains(where: { $0.id == host.preset && $0.baseURL == host.baseURL && $0.enabled }) {
                        Text("Added").font(.caption).foregroundStyle(.secondary)
                    } else {
                        Button("Add") { add(host) }.controlSize(.small)
                    }
                }
            }
            ForEach(model.settings.providers.filter { $0.kind == .local }) { config in
                APIProviderRow(config: config, model: model)
            }
            HStack {
                TextField("Custom URL", text: $customLocalURL, prompt: Text("http://127.0.0.1:8080/v1"))
                    .onSubmit(addCustomLocal)
                Button("Add", action: addCustomLocal)
                    .controlSize(.small)
                    .disabled(Self.hostAndPort(customLocalURL) == nil)
            }
        } header: {
            HStack {
                Text("Local hosts")
                Spacer()
                if scanning {
                    ProgressView().controlSize(.small)
                } else if hosts?.isEmpty == true {
                    Text("None found").font(.callout).fontWeight(.regular).foregroundStyle(.secondary)
                }
                Button("Scan") { Task { await scan() } }
                    .controlSize(.small)
                    .disabled(scanning)
            }
        }
    }

    private func scan() async {
        guard !scanning else { return }
        scanning = true
        hosts = await LocalHostDiscovery.scan()
        scanning = false
    }

    private func add(_ host: DiscoveredHost) {
        model.updateProvider(id: host.preset) { config in
            config.baseURL = host.baseURL
            config.enabled = true
            if config.defaultModel.isEmpty { config.defaultModel = host.models.first ?? "" }
        }
    }

    private func addCustomLocal() {
        let url = customLocalURL.trimmingCharacters(in: .whitespaces)
        guard let name = Self.hostAndPort(url) else { return }
        let config = ProviderConfig(
            id: UUID().uuidString, kind: .local, preset: nil, displayName: name, baseURL: url, authStyle: .none, enabled: true)
        model.updateSettings { $0.providers.append(config) }
        customLocalURL = ""
    }

    /// `host:port` of an http(s) URL, nil when the text is not one.
    private static func hostAndPort(_ text: String) -> String? {
        guard let url = URL(string: text.trimmingCharacters(in: .whitespaces)),
              ["http", "https"].contains(url.scheme?.lowercased()), let host = url.host() else { return nil }
        return url.port.map { "\(host):\($0)" } ?? host
    }

    // MARK: - CLIs

    private func detect(_ preset: CLIPreset) {
        cliStatus[preset] = .checking
        let override = model.providerConfigs.first { $0.id == preset.rawValue }?.baseURL
        Task {
            let environment = ShellEnvironment.shared
            guard let url = await environment.executable(named: preset.executableName, override: override) else {
                cliStatus[preset] = .notInstalled
                return
            }
            cliStatus[preset] = .found(path: url.path, version: await environment.version(of: url))
        }
    }
}

private enum CLIStatus: Equatable {
    case checking
    case notInstalled
    case found(path: String, version: String?)
}

// MARK: - Rows

/// A cloud preset, local host or custom endpoint: enable toggle; expanded, its key, base URL, model and Test.
private struct APIProviderRow: View {
    let config: ProviderConfig
    let model: AppModel

    @State private var expanded = false
    @State private var pendingKey = ""

    private var isCustom: Bool { config.preset == nil }

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 12) {
                if isCustom && config.kind == .openAICompatible {
                    FieldRow("Name") {
                        TextField("Name", text: binding(\.displayName)).labelsHidden()
                    }
                    FieldRow("Authentication") {
                        Picker("Authentication", selection: binding(\.authStyle)) {
                            Text("Bearer token").tag(ProviderConfig.AuthStyle.bearer)
                            Text("api-key header").tag(ProviderConfig.AuthStyle.apiKeyHeader)
                            Text("x-api-key header").tag(ProviderConfig.AuthStyle.xApiKey)
                            Text("None").tag(ProviderConfig.AuthStyle.none)
                        }
                        .labelsHidden()
                        .fixedSize()
                    }
                }
                if config.authStyle != .none { APIKeyField(providerID: config.id, model: model, key: $pendingKey) }
                FieldRow("Base URL") {
                    TextField("Base URL", text: binding(\.baseURL), prompt: Text(config.id == "azure" ? "https://<resource>.openai.azure.com/openai/v1" : ""))
                        .labelsHidden()
                }
                if config.id == "azure" {
                    Text("Required: your resource's https://<resource>.openai.azure.com/openai/v1 URL.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .inputColumn()
                }
                Divider()
                ModelField(config: config, model: model, prompt: config.id == "azure" ? "Deployment name" : "Model id") {
                    try await model.makeProvider(for: config)?.listModels() ?? []
                }
                if CloudTranscription.supports(config) { TranscriptionModelField(config: config, model: model) }
                Divider()
                HStack(alignment: .top) {
                    TestButton(title: "Test", config: config, model: model,
                               pendingKey: config.authStyle != .none ? $pendingKey : nil)
                    Spacer()
                    if config.kind == .local || isCustom {
                        Button("Remove", role: .destructive) { model.removeProvider(id: config.id) }
                    }
                }
                .inputColumn()
            }
            .padding(.top, 6)
            .padding(.bottom, 4)
            .padding(.leading, disclosureIndent)
        } label: {
            Toggle(isOn: binding(\.enabled)) {
                Text(config.displayName.isEmpty ? "Untitled" : config.displayName)
                if config.kind == .local || (isCustom && !config.baseURL.isEmpty) { Text(config.baseURL) }
            }
        }
        .disclosureGroupStyle(TitleChevronDisclosureStyle())
    }

    private func binding<Value>(_ keyPath: WritableKeyPath<ProviderConfig, Value>) -> Binding<Value> {
        Binding(get: { config[keyPath: keyPath] },
                set: { value in model.updateProvider(id: config.id) { $0[keyPath: keyPath] = value } })
    }
}

/// A speech-to-text API (Transcription's models tab): enable toggle; expanded, its key, transcription model and a key
/// check.
struct SpeechProviderRow: View {
    let config: ProviderConfig
    let model: AppModel

    @State private var expanded = false
    @State private var pendingKey = ""

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 12) {
                APIKeyField(providerID: config.id, model: model, key: $pendingKey)
                if config.preset == "assemblyai" {
                    Text("AssemblyAI picks its model for the language (Universal).")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .inputColumn()
                } else {
                    TranscriptionModelField(config: config, model: model)
                }
                if let url = providerLinks[config.id]?.models {
                    ExternalLink("Browse models", url: url).inputColumn()
                }
                Divider()
                // A free GET that needs a valid key; no audio is uploaded.
                TestButton(title: "Test", config: config, model: model, pendingKey: $pendingKey) {
                    guard let key = try KeychainStore.apiKey(forProvider: config.id) else {
                        throw LLMError.provider("Save a key first.")
                    }
                    do {
                        try await CloudTranscription.checkKey(config: config, apiKey: key)
                    } catch let error as LLMError {
                        throw LLMError.provider(CloudTranscription.failureReason(error))
                    }
                }
                .inputColumn()
            }
            .padding(.top, 6)
            .padding(.bottom, 4)
            .padding(.leading, disclosureIndent)
        } label: {
            Toggle(isOn: Binding(get: { config.enabled },
                                 set: { value in model.updateProvider(id: config.id) { $0.enabled = value } })) {
                Text(config.displayName)
            }
        }
        .disclosureGroupStyle(TitleChevronDisclosureStyle())
    }
}

/// A subscription CLI: detected path and version, path override, model, enable toggle and Test.
private struct CLIProviderRow: View {
    let config: ProviderConfig
    let preset: CLIPreset
    let status: CLIStatus?
    let model: AppModel
    let redetect: () -> Void

    @State private var expanded = false

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 12) {
                FieldRow("Path override") {
                    TextField("Path override", text: binding(\.baseURL), prompt: Text("Search PATH"))
                        .labelsHidden()
                        .onSubmit(redetect)
                }
                ModelField(config: config, model: model, prompt: modelPrompt) {
                    try await model.makeProvider(for: config)?.listModels() ?? []
                }
                if let note = CLIRunner.containmentNote(for: preset) {
                    Label(note, systemImage: "exclamationmark.shield")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .inputColumn()
                }
                Divider()
                TestButton(title: "Test (uses your subscription)", config: config, model: model)
                    .disabled(!isInstalled)
                    .inputColumn()
            }
            .padding(.top, 6)
            .padding(.bottom, 4)
            .padding(.leading, disclosureIndent)
        } label: {
            Toggle(isOn: binding(\.enabled)) {
                HStack(spacing: 6) {
                    Text(config.displayName)
                    if status == .checking || status == nil { ProgressView().controlSize(.mini) }
                }
                statusText
            }
        }
        .disclosureGroupStyle(TitleChevronDisclosureStyle())
    }

    private var isInstalled: Bool {
        if case .found = status { return true }
        return false
    }

    @ViewBuilder private var statusText: some View {
        switch status {
        case .found(let path, let version): Text([path, version].compactMap { $0 }.joined(separator: " - "))
        case .notInstalled: Text("Not installed")
        case .checking, nil: Text("Looking for \(preset.executableName)...")
        }
    }

    private var modelPrompt: String {
        switch preset {
        case .opencode: "provider/model"
        case .claude: "sonnet, opus or haiku (empty = default)"
        case .codex, .gemini: "Empty = the CLI's default"
        }
    }

    private func binding<Value>(_ keyPath: WritableKeyPath<ProviderConfig, Value>) -> Binding<Value> {
        Binding(get: { config[keyPath: keyPath] },
                set: { value in model.updateProvider(id: config.id) { $0[keyPath: keyPath] = value } })
    }
}

// MARK: - Fields

/// Leading inset of an expanded row's content, so it lines up with the row's title after the disclosure chevron.
private let disclosureIndent = TitleChevronDisclosureStyle.indent

/// Width of the label column of an expanded row: every input starts at the same x.
private let fieldLabelWidth: CGFloat = 136
private let fieldGap: CGFloat = 12

/// Where to get a preset's API key and look up its model ids; custom endpoints, local hosts and CLIs have none.
private let providerLinks: [String: (keys: URL, models: URL)] = [
    "openai": (URL(string: "https://platform.openai.com/api-keys")!,
               URL(string: "https://developers.openai.com/api/docs/models")!),
    "anthropic": (URL(string: "https://platform.claude.com/settings/keys")!,
                  URL(string: "https://platform.claude.com/docs/en/models/overview")!),
    "xai": (URL(string: "https://console.x.ai")!, URL(string: "https://docs.x.ai/developers/models")!),
    "gemini": (URL(string: "https://aistudio.google.com/apikey")!,
               URL(string: "https://ai.google.dev/gemini-api/docs/models")!),
    "mistral": (URL(string: "https://console.mistral.ai/api-keys")!, URL(string: "https://docs.mistral.ai/models")!),
    "azure": (URL(string: "https://ai.azure.com")!,
              URL(string: "https://learn.microsoft.com/azure/ai-foundry/openai/concepts/models")!),
    "openrouter": (URL(string: "https://openrouter.ai/settings/keys")!, URL(string: "https://openrouter.ai/models")!),
    "groq": (URL(string: "https://console.groq.com/keys")!, URL(string: "https://console.groq.com/docs/models")!),
    "deepseek": (URL(string: "https://platform.deepseek.com/api_keys")!,
                 URL(string: "https://api-docs.deepseek.com/quick_start/pricing")!),
    "elevenlabs": (URL(string: "https://elevenlabs.io/app/developers/api-keys")!,
                   URL(string: "https://elevenlabs.io/docs/overview/models")!),
    "deepgram": (URL(string: "https://console.deepgram.com")!,
                 URL(string: "https://developers.deepgram.com/docs/models-languages-overview")!),
    "assemblyai": (URL(string: "https://www.assemblyai.com/dashboard/api-keys")!,
                   URL(string: "https://www.assemblyai.com/docs/getting-started/models")!),
]

private extension View {
    /// Lines a note, error or button row up with the inputs, past the label column.
    func inputColumn() -> some View {
        padding(.leading, fieldLabelWidth + fieldGap)
    }
}

/// A link that opens in the browser, with an arrow saying so.
private struct ExternalLink: View {
    let title: LocalizedStringKey
    let url: URL

    init(_ title: LocalizedStringKey, url: URL) {
        self.title = title
        self.url = url
    }

    var body: some View {
        Link(destination: url) {
            HStack(spacing: 2) {
                Text(title)
                Image(systemName: "arrow.up.right").imageScale(.small)
            }
        }
        .font(.callout)
        .fixedSize()
    }
}

/// A secondary label in a fixed-width column, then the input stretched over the rest of the row, followed by its
/// buttons. Plain labeled fields in the grouped form right-align their text or butt against the label.
private struct FieldRow<Input: View>: View {
    let label: LocalizedStringKey
    @ViewBuilder let input: Input

    init(_ label: LocalizedStringKey, @ViewBuilder input: () -> Input) {
        self.label = label
        self.input = input()
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: fieldGap) {
            Text(label)
                .foregroundStyle(.secondary)
                .frame(width: fieldLabelWidth, alignment: .leading)
            input.textFieldStyle(.roundedBorder)
        }
    }
}

/// The API key, written to the Keychain on Return, on leaving the field, or by Save and Test (`TestButton`). Only
/// "Saved" is shown; the key is never read back into the UI.
private struct APIKeyField: View {
    let providerID: String
    let model: AppModel
    @Binding var key: String

    @State private var saved = false
    @State private var error: String?
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            FieldRow("API key") {
                SecureField("API key", text: $key, prompt: Text(saved ? "Enter a new key to replace it" : "Paste the key, then Save"))
                    .labelsHidden()
                    .focused($focused)
                    .onSubmit(save)
                    // Leaving the field saves too, so a pasted key is not lost without Return.
                    .onChange(of: focused) { _, focused in if !focused { save() } }
                if saved {
                    Label("Saved", systemImage: "checkmark.circle.fill")
                        .font(.caption)
                        .foregroundStyle(.green)
                    Button("Remove") { perform { try KeychainStore.deleteAPIKey(forProvider: providerID) } }
                } else if let url = providerLinks[providerID]?.keys {
                    ExternalLink("Get an API key", url: url)
                }
            }
            if let error {
                Text(error).font(.caption).foregroundStyle(.red).inputColumn()
            }
        }
        .onAppear(perform: refresh)
        // Save and Test write the key outside this field.
        .onChange(of: model.keychainVersion) { refresh() }
    }

    private func save() {
        perform { try saveAPIKey($key, providerID: providerID, model: model) }
    }

    private func perform(_ change: () throws -> Void) {
        do {
            try change()
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
        refresh()
        model.keychainChanged()
    }

    private func refresh() {
        saved = (try? KeychainStore.hasAPIKey(forProvider: providerID)) ?? false
    }
}

/// The cloud transcription model; empty = the vendor default shown as the prompt.
private struct TranscriptionModelField: View {
    let config: ProviderConfig
    let model: AppModel

    var body: some View {
        let defaultModel = CloudTranscription.defaultModel(for: config)
        let known = OpenAICompatibleTranscriber.presetModels[config.preset ?? ""] ?? []
        FieldRow("Transcription model") {
            TextField("Transcription model", text: Binding(
                get: { config.transcriptionModel },
                set: { value in model.updateProvider(id: config.id) { $0.transcriptionModel = value } }
            ), prompt: Text(defaultModel.isEmpty ? "Model id, e.g. whisper-1" : defaultModel))
            .labelsHidden()
            if known.count > 1 {
                Menu("Models") {
                    ForEach(known, id: \.self) { id in
                        Button(id) { model.updateProvider(id: config.id) { $0.transcriptionModel = id } }
                    }
                }
                .fixedSize()
            }
        }
    }
}

/// Free-text model id; "Load models" fills a menu from the provider's `listModels()` and a checklist of the models the
/// AI pane's model menu offers besides this one (`ProviderConfig.shownModels`).
private struct ModelField: View {
    let config: ProviderConfig
    let model: AppModel
    let prompt: String
    let load: () async throws -> [String]

    @State private var models: [String]?
    @State private var loading = false
    @State private var error: String?
    @State private var filter = ""
    @State private var addedModel = ""

    /// Above this many models the checklist scrolls and gets a filter field.
    private static let longList = 8

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            FieldRow("Model") {
                TextField("Model", text: modelBinding, prompt: Text(prompt)).labelsHidden()
                if let models, !models.isEmpty {
                    Menu("\(models.count) model\(models.count == 1 ? "" : "s")") {
                        ForEach(models, id: \.self) { id in
                            Button(id) { modelBinding.wrappedValue = id }
                        }
                    }
                    .fixedSize()
                }
                Button("Load models") { Task { await loadModels() } }
                    .disabled(loading)
                if loading { ProgressView().controlSize(.small) }
            }
            if let url = providerLinks[config.id]?.models {
                ExternalLink("Browse models", url: url).inputColumn()
            }
            if let error {
                Text(error).font(.caption).foregroundStyle(.red).lineLimit(2).inputColumn()
            } else if models?.isEmpty == true {
                Text("No models listed; type the model id.").font(.caption).foregroundStyle(.secondary).inputColumn()
            }
            shownModels
        }
    }

    /// The default model (always offered), the shown ones and the loaded ones, each with its checkbox.
    @ViewBuilder private var shownModels: some View {
        let defaultID = config.defaultModel.trimmingCharacters(in: .whitespaces)
        let choices = Self.unique([defaultID] + config.shownModels + (models ?? []))
        // Only while its field is shown: a filter left over from a longer list would hide checkboxes for good.
        let term = choices.count > Self.longList ? filter.trimmingCharacters(in: .whitespaces) : ""
        let shown = term.isEmpty ? choices : choices.filter { $0.localizedStandardContains(term) }
        let box = RoundedRectangle(cornerRadius: 8, style: .continuous)
        VStack(alignment: .leading, spacing: 8) {
            Text("In the AI pane's model menu")
                .font(.caption)
                .foregroundStyle(.secondary)
            if choices.count > Self.longList {
                TextField("Filter", text: $filter, prompt: Text("Filter models"))
                    .labelsHidden()
                    .textFieldStyle(.roundedBorder)
                ScrollView {
                    checklist(shown, defaultID: defaultID)
                }
                .frame(height: 160)
            } else {
                checklist(shown, defaultID: defaultID)
            }
            TextField("Add a model", text: $addedModel, prompt: Text("Add a model id"))
                .labelsHidden()
                .textFieldStyle(.roundedBorder)
                .frame(maxWidth: 260)
                .onSubmit(addModel)
            if models == nil {
                Text("Type a model id and press Return, or load the models to pick from.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(box.fill(Color.black.opacity(0.15)))
        .overlay(box.strokeBorder(Palette.surfaceBorder, lineWidth: 1).allowsHitTesting(false))
        .inputColumn()
    }

    private func addModel() {
        let id = addedModel.trimmingCharacters(in: .whitespacesAndNewlines)
        addedModel = ""
        guard !id.isEmpty else { return }
        model.updateProvider(id: config.id) { config in
            if !config.shownModels.contains(id) { config.shownModels.append(id) }
        }
    }

    /// Non-empty ids, first occurrence kept.
    private static func unique(_ ids: [String]) -> [String] {
        var seen: [String] = []
        for id in ids where !id.isEmpty && !seen.contains(id) { seen.append(id) }
        return seen
    }

    private func checklist(_ ids: [String], defaultID: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(ids, id: \.self) { id in
                Toggle(isOn: shownBinding(id, defaultID: defaultID)) {
                    Text(id == defaultID ? "\(id) (default)" : id)
                }
                .toggleStyle(.checkbox)
                .disabled(id == defaultID)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func shownBinding(_ id: String, defaultID: String) -> Binding<Bool> {
        Binding(
            get: { id == defaultID || config.shownModels.contains(id) },
            set: { isOn in
                model.updateProvider(id: config.id) { config in
                    config.shownModels.removeAll { $0 == id }
                    if isOn { config.shownModels.append(id) }
                }
            })
    }

    private var modelBinding: Binding<String> {
        Binding(get: { config.defaultModel },
                set: { value in model.updateProvider(id: config.id) { $0.defaultModel = value } })
    }

    private func loadModels() async {
        loading = true
        defer { loading = false }
        do {
            models = try await load().sorted()
            error = nil
        } catch {
            self.error = shortMessage(error)
        }
    }
}

/// Sends a one-token "ping" (CLIs: a tiny prompt), or runs `check` when set, and shows OK beside it or a short error
/// below.
/// With `pendingKey`, a Save button writes the typed key to the Keychain, and Test saves it first. Not run while idle.
private struct TestButton: View {
    let title: String
    let config: ProviderConfig
    let model: AppModel
    var pendingKey: Binding<String>?
    var check: (@MainActor () async throws -> Void)?

    @State private var task: Task<Void, Never>?
    @State private var result: (ok: Bool, text: String)?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                Button(task == nil ? title : "Cancel") {
                    if let task { task.cancel() } else { run() }
                }
                if let pendingKey {
                    Button("Save") { _ = save(pendingKey) }
                        .disabled(task != nil || pendingKey.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
                if task != nil {
                    ProgressView().controlSize(.small)
                } else if let result, result.ok {
                    Text(result.text).font(.caption).foregroundStyle(.green)
                }
            }
            if task == nil, let result, !result.ok {
                Text(result.text)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .lineLimit(3)
            }
        }
        .onDisappear { task?.cancel() }
    }

    /// Writes the typed key; false, with the error shown, when the Keychain refuses it.
    private func save(_ key: Binding<String>) -> Bool {
        do {
            try saveAPIKey(key, providerID: config.id, model: model)
            result = nil
            return true
        } catch {
            result = (false, shortMessage(error))
            return false
        }
    }

    private func run() {
        result = nil
        // A key pasted without Return is tested, not the one already in the Keychain.
        if let pendingKey, !save(pendingKey) { return }
        let provider: (any LLMProvider)?
        do {
            provider = try model.makeProvider(for: config)
        } catch {
            result = (false, shortMessage(error))
            return
        }
        guard provider != nil || check != nil else { return }
        let modelID = config.defaultModel.trimmingCharacters(in: .whitespaces)
        task = Task {
            do {
                if let check {
                    try await check()
                } else if let provider {
                    let stream = provider.stream(messages: [LLMMessage(role: .user, content: "ping")], model: modelID, maxTokens: 1)
                    for try await _ in stream { break }
                }
                try Task.checkCancellation()
                result = (true, "OK")
            } catch {
                // Cancel surfaces as CancellationError or URLError.cancelled: no result then.
                result = Task.isCancelled ? nil : (false, shortMessage(error))
            }
            task = nil
        }
    }
}

/// Writes a typed API key to the Keychain and clears the field; does nothing when it is blank. Spaces and line breaks
/// are dropped anywhere: a key copied across a wrapped line has one inside, and URLSession then silently drops the
/// whole auth header (the provider answers "header is required").
@MainActor private func saveAPIKey(_ key: Binding<String>, providerID: String, model: AppModel) throws {
    let cleaned = key.wrappedValue.filter { !$0.isWhitespace }
    guard !cleaned.isEmpty else { return }
    try KeychainStore.setAPIKey(cleaned, forProvider: providerID)
    key.wrappedValue = ""
    model.keychainChanged()
}

/// The first line of an error, at most 160 characters.
private func shortMessage(_ error: Error) -> String {
    let line = error.localizedDescription.split(whereSeparator: \.isNewline).first.map(String.init) ?? ""
    return line.count > 160 ? String(line.prefix(160)) + "..." : line
}
