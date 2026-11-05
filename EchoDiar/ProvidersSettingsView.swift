import EchoAI
import EchoCore
import SwiftUI

/// The AI tab, in inner tabs: on this Mac (the AI models for summaries and chat, local hosts scanned on demand), the
/// models of the cloud API presets and custom OpenAI-compatible endpoints (their keys and endpoints are in API Keys),
/// and subscription CLIs (the speech-to-text APIs' models are in Transcription). Saved in `AppSettings.providers`.
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
        case onThisMac, cloud, cli
    }

    private var tab: Tab { model.settingsSubTab(.providers).flatMap(Tab.init) ?? .onThisMac }

    private static let mlxEntries = ModelCatalog.llm.filter { $0.runtime == .mlx }

    var body: some View {
        ScrollViewReader { proxy in
            form
                // RAG's Set Up: once the inner tab shows, its local host's row (which expands itself) comes into view.
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
            case .onThisMac:
                llmSection
                chatSection
                catchUpSection
                localHosts
            case .cloud:
                dataLeavesNotice
                Section {
                    ForEach(configs { $0.preset != nil && [.openAICompatible, .anthropic].contains($0.kind) }) { config in
                        CloudModelRow(config: config, model: model)
                    }
                }
                let custom = configs { $0.preset == nil && $0.kind == .openAICompatible }
                if !custom.isEmpty {
                    Section {
                        ForEach(custom) { config in
                            CloudModelRow(config: config, model: model)
                        }
                    } header: {
                        Text("Custom OpenAI-compatible endpoints")
                    }
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
            Text("AI models (summaries and chat)")
        }
    }

    /// How many earlier messages each chat question carries, for every provider.
    private var chatSection: some View {
        Section {
            Picker("Chat context", selection: model.setting(\.chatContextMessages)) {
                ForEach(AppSettings.chatContextOptions, id: \.self) { Text("\($0) messages").tag($0) }
                Text("Whole chat").tag(0)
            }
        } header: {
            SectionHeader(title: "Chat", description: "Earlier messages sent with each question.")
        }
    }

    // MARK: - Catch-up

    /// Where "Catch up" runs while recording: a provider outside the app (as `AppModel.catchUpProvider` resolves it),
    /// its model, and whether each catch-up reads the recording from the start.
    private var catchUpSection: some View {
        let external = catchUpProviders
        let picked = external.first { $0.id == model.settings.catchUpProviderID }
        return Section {
            Picker("Catch-up provider", selection: catchUpProviderBinding(external)) {
                if let automatic = model.automaticCatchUpProvider {
                    let label = String(localized: "Automatic (\(automatic.name))")
                    Text(automatic.leavesMac ? String(localized: "\(label) (leaves this Mac)") : label).tag("")
                } else {
                    Text("Automatic").tag("")
                }
                ForEach(external) { provider in
                    Text(provider.leavesMac ? String(localized: "\(provider.name) (leaves this Mac)") : provider.name)
                        .tag(provider.id)
                }
            }
            .disabled(external.isEmpty)
            if let picked {
                Picker("Catch-up model", selection: catchUpModelBinding) {
                    Text("Automatic").tag("")
                    ForEach(catchUpModels(picked), id: \.self) { id in Text(id).tag(id) }
                }
                if picked.kind == .cloud, let config = model.providerConfigs.first(where: { $0.id == picked.id }) {
                    LabeledContent(picked.name) { APIKeyStatus(config: config, model: model) }
                }
            }
            // As the AI pane's data notes, for the provider a catch-up runs on.
            if let note = model.catchUpProvider?.leavesMacNote {
                Label(note, systemImage: "info.circle")
                    .foregroundStyle(.secondary)
            }
            if let note = model.catchUpProvider?.containmentNote {
                Label(note, systemImage: "exclamationmark.shield")
                    .foregroundStyle(.secondary)
            }
            if external.isEmpty {
                Text("Catch up needs an AI provider outside the app: a cloud API, a local server or a command-line tool. Add one in Settings › AI.")
                    .foregroundStyle(.secondary)
            }
            Toggle(isOn: model.setting(\.catchUpFromStart)) {
                Text("Re-summarize from the start each time")
                Text("Off: each catch-up adds what was said since the previous one. On: each one reads the whole recording again.")
            }
            Label("In-app models are not offered: the live transcript holds the model during a recording.",
                  systemImage: "info.circle")
                .foregroundStyle(.secondary)
        } header: {
            SectionHeader(title: "Catch-up", description: "Quick summaries of the meeting so far, asked for while recording.")
        }
    }

    /// The usable providers outside the app, as `AppModel.catchUpProvider` filters them.
    private var catchUpProviders: [AppModel.AIProvider] {
        model.aiProviders.filter { $0.kind != .inApp && $0.unavailableReason == nil }
    }

    /// The picked provider, "Automatic" when it is not usable now (as `catchUpProvider` then runs); picking one
    /// clears the model picked for the previous one.
    private func catchUpProviderBinding(_ external: [AppModel.AIProvider]) -> Binding<String> {
        Binding(
            get: {
                let id = model.settings.catchUpProviderID
                return external.contains { $0.id == id } ? id : ""
            },
            set: { id in
                model.updateSettings {
                    $0.catchUpProviderID = id
                    $0.catchUpModel = ""
                }
            })
    }

    private var catchUpModelBinding: Binding<String> {
        Binding(
            get: { model.settings.catchUpModel.trimmingCharacters(in: .whitespacesAndNewlines) },
            set: { id in model.updateSettings { $0.catchUpModel = id } })
    }

    /// `provider`'s models without a CLI's own default ("Automatic" covers it), then the saved pick when it is no
    /// longer listed, so the picker shows what runs.
    private func catchUpModels(_ provider: AppModel.AIProvider) -> [String] {
        var ids = provider.models.filter { !$0.isEmpty }
        let saved = model.settings.catchUpModel.trimmingCharacters(in: .whitespacesAndNewlines)
        if !saved.isEmpty && !ids.contains(saved) { ids.append(saved) }
        return ids
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

/// A cloud LLM preset or custom endpoint on the Cloud tab: its key status ("API key" opens its card in API Keys);
/// expanded, its model. A missing base URL (Azure, a custom endpoint) is said under its name.
private struct CloudModelRow: View {
    let config: ProviderConfig
    let model: AppModel

    @State private var expanded = false

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            ModelField(config: config, model: model, prompt: config.id == "azure" ? "Deployment name" : "Model id") {
                try await model.makeProvider(for: config)?.listModels() ?? []
            }
            .padding(.top, 6)
            .padding(.bottom, 4)
            .padding(.leading, disclosureIndent)
        } label: {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(config.displayName.isEmpty ? String(localized: "Untitled") : config.displayName)
                    if config.baseURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        Text("Base URL not set").font(.caption).foregroundStyle(.secondary)
                    } else if config.preset == nil {
                        Text(config.baseURL).font(.caption).foregroundStyle(.secondary)
                    }
                }
                Spacer()
                APIKeyStatus(config: config, model: model)
            }
        }
        .disclosureGroupStyle(TitleChevronDisclosureStyle())
        .id(config.id)
    }
}

/// A local host: enable toggle; expanded, its base URL, model, Test and Remove.
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
                Divider()
                HStack(alignment: .top) {
                    TestButton(title: "Test", config: config, model: model)
                    Spacer()
                    Button("Remove", role: .destructive) { model.removeProvider(id: config.id) }
                }
                .inputColumn()
            }
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
private struct ModelField: View {
    let config: ProviderConfig
    let model: AppModel
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
            FieldRow("Model") {
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
