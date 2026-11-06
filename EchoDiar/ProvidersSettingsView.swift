import EchoAI
import EchoCore
import SwiftUI

/// The AI Providers tab, in inner tabs: local AI (the in-app language models, the local transcription, diarization and
/// embedding model downloads, and local hosts scanned on demand), the cloud provider cards (keys and models) and
/// subscription CLIs. Saved in `AppSettings.providers`.
struct ProvidersSettingsView: View {
    let model: AppModel

    @State private var hosts: [DiscoveredHost]?
    @State private var scanning = false
    @State private var customLocalURL = ""
    @State private var cliStatus: [CLIPreset: CLIStatus] = [:]
    /// Bumped by "Check Again" so the Foundation Models row reads its availability again.
    @State private var foundationModelsCheck = 0

    /// Ids in `SettingsTab.providers.subTabs`; picked in the toolbar.
    private enum Tab: String {
        case localAI, cloud, cli
    }

    private var tab: Tab { model.settingsSubTab(.providers).flatMap(Tab.init) ?? .localAI }

    private static let mlxEntries = ModelCatalog.llm.filter { $0.runtime == .mlx }

    var body: some View {
        ScrollViewReader { proxy in
            form
                // RAG's Set Up or a model card's "API key": once the inner tab shows, the local host's row or the
                // provider's card (which expands itself) comes into view.
                .onChange(of: model.settingsExpandedProvider, initial: true) { _, id in
                    guard let id else { return }
                    Task {
                        await Task.yield()
                        withAnimation { proxy.scrollTo(id, anchor: .top) }
                    }
                }
        }
    }

    private var form: some View {
        Form {
            switch tab {
            case .localAI:
                llmSection
                speechModelSections
                embeddingSection
                localHosts
            case .cloud:
                dataLeavesNotice
                ProviderCardSections(model: model)
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

    /// Models for summaries and chat: Apple Foundation Models' status (macOS 26) in its own box, then the MLX downloads.
    @ViewBuilder
    private var llmSection: some View {
        if #available(macOS 26, *), PlatformFeatures.foundationModels {
            Section {
                LabeledContent("Apple Foundation Models (system)") {
                    if let reason = AppleFoundationModelProvider.unavailableReason {
                        HStack {
                            Text(reason).foregroundStyle(.secondary)
                            Button("Check Again") { foundationModelsCheck += 1 }
                                .controlSize(.small)
                        }
                    } else {
                        Label("Available", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                    }
                }
                .id(foundationModelsCheck)
            }
        }
        Section {
            ForEach(Self.mlxEntries) { ModelRow(entry: $0, model: model) }
        } header: {
            SectionHeader(title: "Language models", description: "Models that run on this Mac, for summaries and chat.")
        }
    }

    // MARK: - Speech and embedding models

    /// The local transcription and diarization models by tier, with Download, Delete and Set default.
    @ViewBuilder
    private var speechModelSections: some View {
        Section {
            Text("Transcription turns speech into text. The transcription model listens to the recording and writes what was said, with timestamps.")
                .foregroundStyle(.secondary)
        } header: {
            Text("Transcription models")
        }
        ModelTierSections(kind: .asr, model: model)
        Section {
            Text("Diarization finds who spoke when. The diarization model tells the voices apart and labels each part of the transcript with its speaker.")
                .foregroundStyle(.secondary)
        } header: {
            Text("Diarization models")
        }
        ModelTierSections(kind: .diarization, model: model)
    }

    /// The local embedding models' downloads; the one in use is picked in Library chat.
    private var embeddingSection: some View {
        Section {
            ForEach(ModelCatalog.embedding) { ModelRow(entry: $0, model: model) }
        } header: {
            SectionHeader(
                title: "Embedding models",
                description: "The embedding model lets a question find passages with the same meaning, in any language. Models are downloaded only when you click Download.")
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
                        Text("\(host.baseURL) - \(host.models.count) models")
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

/// A local host: enable toggle; expanded, its base URL, model, embedding model, Test and Remove.
private struct APIProviderRow: View {
    let config: ProviderConfig
    let model: AppModel

    @State private var expanded = false

    var body: some View {
        disclosure
            .id(config.id)
            .onChange(of: model.settingsExpandedProvider, initial: true) { _, id in
                guard id == config.id else { return }
                expanded = true
                model.settingsExpandedProvider = nil
            }
    }

    private var disclosure: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 12) {
                FieldRow("Base URL") {
                    TextField("Base URL", text: binding(\.baseURL)).labelsHidden()
                }
                Divider()
                ModelField(config: config, model: model, prompt: "Model id") {
                    try await model.makeProvider(for: config)?.listModels() ?? []
                }
                // Every local host can embed (`RAGSettingsView.embeddingConfigs`).
                EmbeddingModelField(config: config, model: model)
                Divider()
                HStack(alignment: .top) {
                    TestButton(title: "Test", config: config, model: model)
                    Spacer()
                    Button("Remove", role: .destructive) { model.removeProvider(id: config.id) }
                }
                .inputColumn()
            }
            .fieldLabelColumn()
            .padding(.top, 6)
            .padding(.bottom, 4)
            .padding(.leading, disclosureIndent)
        } label: {
            Toggle(isOn: binding(\.enabled)) {
                Text(config.displayName.isEmpty ? String(localized: "Untitled") : config.displayName)
                Text(config.baseURL)
            }
        }
        .disclosureGroupStyle(TitleChevronDisclosureStyle())
    }

    private func binding<Value>(_ keyPath: WritableKeyPath<ProviderConfig, Value>) -> Binding<Value> {
        Binding(get: { config[keyPath: keyPath] },
                set: { value in model.updateProvider(id: config.id) { $0[keyPath: keyPath] = value } })
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
            .fieldLabelColumn()
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

    private var modelPrompt: LocalizedStringKey {
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

/// Free-text model id; "Load models" fills a menu from the provider's `listModels()` and a checklist of the models the
/// AI pane's model menu offers besides this one (`ProviderConfig.shownModels`).
struct ModelField: View {
    let config: ProviderConfig
    let model: AppModel
    var label: LocalizedStringKey = "Model"
    let prompt: LocalizedStringKey
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
            FieldRow(label) {
                TextField("Model", text: modelBinding, prompt: Text(prompt)).labelsHidden()
                if let models, !models.isEmpty {
                    Menu("\(models.count) models") {
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
                    id == defaultID ? Text("\(id) (default)") : Text(verbatim: id)
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
