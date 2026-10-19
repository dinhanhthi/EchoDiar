import EchoAI
import EchoCore
import SwiftUI

/// The AI & Cloud tab: cloud API presets, speech-to-text APIs, local hosts (scanned on demand), subscription CLIs and
/// custom OpenAI-compatible endpoints. API keys go to the Keychain only; everything else is saved in `AppSettings.providers`.
struct ProvidersSettingsView: View {
    let model: AppModel

    @State private var hosts: [DiscoveredHost]?
    @State private var scanning = false
    @State private var customLocalURL = ""
    @State private var cliStatus: [CLIPreset: CLIStatus] = [:]

    var body: some View {
        Form {
            Section {
                Label("Data sent to cloud providers or CLIs leaves this Mac.", systemImage: "info.circle")
                    .foregroundStyle(.secondary)
            }
            Section("Cloud APIs") {
                ForEach(configs { $0.preset != nil && [.openAICompatible, .anthropic].contains($0.kind) }) { config in
                    APIProviderRow(config: config, model: model)
                }
            }
            Section {
                ForEach(configs { $0.kind == .speechToText }) { config in
                    SpeechProviderRow(config: config, model: model)
                }
            } header: {
                Text("Speech to text")
            } footer: {
                Text("Cloud transcription engines for Settings > Transcription. OpenAI, Groq, Mistral, Google Gemini and custom endpoints above transcribe too, with their transcription model.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            localHosts
            Section("Subscription CLIs") {
                ForEach(configs { $0.kind == .cli }) { config in
                    if let preset = CLIPreset(rawValue: config.id) {
                        CLIProviderRow(config: config, preset: preset, status: cliStatus[preset], model: model) {
                            detect(preset)
                        }
                    }
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
        }
        .formStyle(.grouped)
        .task { await scan() }
        .task {
            // Once per window: the first PATH lookup runs the login shell (~4 s) and each check spawns `--version`.
            if cliStatus.isEmpty { CLIPreset.allCases.forEach(detect) }
        }
    }

    private func configs(_ isIncluded: (ProviderConfig) -> Bool) -> [ProviderConfig] {
        model.providerConfigs.filter(isIncluded)
    }

    // MARK: - Local hosts

    private var localHosts: some View {
        Section("Local hosts") {
            HStack {
                Button("Scan") { Task { await scan() } }
                    .controlSize(.small)
                    .disabled(scanning)
                if scanning {
                    ProgressView().controlSize(.small)
                } else if hosts?.isEmpty == true {
                    Text("None found").foregroundStyle(.secondary)
                }
                Spacer()
            }
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

    private var isCustom: Bool { config.preset == nil }

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 8) {
                if isCustom && config.kind == .openAICompatible {
                    TextField("Name", text: binding(\.displayName))
                    Picker("Authentication", selection: binding(\.authStyle)) {
                        Text("Bearer token").tag(ProviderConfig.AuthStyle.bearer)
                        Text("api-key header").tag(ProviderConfig.AuthStyle.apiKeyHeader)
                        Text("x-api-key header").tag(ProviderConfig.AuthStyle.xApiKey)
                        Text("None").tag(ProviderConfig.AuthStyle.none)
                    }
                }
                if config.authStyle != .none { APIKeyField(providerID: config.id, model: model) }
                TextField("Base URL", text: binding(\.baseURL), prompt: Text(config.id == "azure" ? "https://<resource>.openai.azure.com/openai/v1" : ""))
                if config.id == "azure" {
                    Text("Required: your resource's https://<resource>.openai.azure.com/openai/v1 URL.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                ModelField(config: config, model: model, prompt: config.id == "azure" ? "Deployment name" : "Model id") {
                    try await model.makeProvider(for: config)?.listModels() ?? []
                }
                if CloudTranscription.supports(config) { TranscriptionModelField(config: config, model: model) }
                HStack {
                    TestButton(title: "Test", config: config, model: model)
                    Spacer()
                    if config.kind == .local || isCustom {
                        Button("Remove", role: .destructive) { model.removeProvider(id: config.id) }
                            .controlSize(.small)
                    }
                }
            }
            .padding(.vertical, 4)
        } label: {
            Toggle(isOn: binding(\.enabled)) {
                Text(config.displayName.isEmpty ? "Untitled" : config.displayName)
                if config.kind == .local || (isCustom && !config.baseURL.isEmpty) { Text(config.baseURL) }
            }
        }
    }

    private func binding<Value>(_ keyPath: WritableKeyPath<ProviderConfig, Value>) -> Binding<Value> {
        Binding(get: { config[keyPath: keyPath] },
                set: { value in model.updateProvider(id: config.id) { $0[keyPath: keyPath] = value } })
    }
}

/// A speech-to-text API: enable toggle; expanded, its key, transcription model and a key check.
private struct SpeechProviderRow: View {
    let config: ProviderConfig
    let model: AppModel

    @State private var expanded = false

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 8) {
                APIKeyField(providerID: config.id, model: model)
                if config.preset == "assemblyai" {
                    Text("AssemblyAI picks its model for the language (Universal).")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } else {
                    TranscriptionModelField(config: config, model: model)
                }
                // A free GET that needs a valid key; no audio is uploaded.
                TestButton(title: "Test", config: config, model: model) {
                    guard let key = try KeychainStore.apiKey(forProvider: config.id) else {
                        throw LLMError.provider("Save a key first.")
                    }
                    do {
                        try await CloudTranscription.checkKey(config: config, apiKey: key)
                    } catch let error as LLMError {
                        throw LLMError.provider(CloudTranscription.failureReason(error))
                    }
                }
            }
            .padding(.vertical, 4)
        } label: {
            Toggle(isOn: Binding(get: { config.enabled },
                                 set: { value in model.updateProvider(id: config.id) { $0.enabled = value } })) {
                Text(config.displayName)
            }
        }
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
            VStack(alignment: .leading, spacing: 8) {
                TextField("Path override", text: binding(\.baseURL), prompt: Text("Search PATH"))
                    .onSubmit(redetect)
                ModelField(config: config, model: model, prompt: modelPrompt) {
                    try await model.makeProvider(for: config)?.listModels() ?? []
                }
                if let note = CLIRunner.containmentNote(for: preset) {
                    Label(note, systemImage: "exclamationmark.shield")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                TestButton(title: "Test (uses your subscription)", config: config, model: model)
                    .disabled(!isInstalled)
            }
            .padding(.vertical, 4)
        } label: {
            Toggle(isOn: binding(\.enabled)) {
                HStack(spacing: 6) {
                    Text(config.displayName)
                    if status == .checking || status == nil { ProgressView().controlSize(.mini) }
                }
                statusText
            }
        }
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

/// The API key, written to the Keychain on Return. Only "Saved" is shown; the key is never read back into the UI.
private struct APIKeyField: View {
    let providerID: String
    let model: AppModel

    @State private var key = ""
    @State private var saved = false
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                SecureField("API key", text: $key, prompt: Text(saved ? "Enter a new key to replace it" : "Paste the key, then Return"))
                    .onSubmit(save)
                if saved {
                    Label("Saved", systemImage: "checkmark.circle.fill")
                        .font(.caption)
                        .foregroundStyle(.green)
                    Button("Remove") { perform { try KeychainStore.deleteAPIKey(forProvider: providerID) } }
                        .controlSize(.small)
                }
            }
            if let error {
                Text(error).font(.caption).foregroundStyle(.red)
            }
        }
        .onAppear(perform: refresh)
    }

    private func save() {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        perform { try KeychainStore.setAPIKey(trimmed, forProvider: providerID) }
        key = ""
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
        HStack {
            TextField("Transcription model", text: Binding(
                get: { config.transcriptionModel },
                set: { value in model.updateProvider(id: config.id) { $0.transcriptionModel = value } }
            ), prompt: Text(defaultModel.isEmpty ? "Model id, e.g. whisper-1" : defaultModel))
            if known.count > 1 {
                Menu("Models") {
                    ForEach(known, id: \.self) { id in
                        Button(id) { model.updateProvider(id: config.id) { $0.transcriptionModel = id } }
                    }
                }
                .fixedSize()
                .controlSize(.small)
            }
        }
    }
}

/// Free-text model id; "Load models" fills a menu from the provider's `listModels()`.
private struct ModelField: View {
    let config: ProviderConfig
    let model: AppModel
    let prompt: String
    let load: () async throws -> [String]

    @State private var models: [String]?
    @State private var loading = false
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                TextField("Model", text: modelBinding, prompt: Text(prompt))
                if let models, !models.isEmpty {
                    Menu("\(models.count) model\(models.count == 1 ? "" : "s")") {
                        ForEach(models, id: \.self) { id in
                            Button(id) { modelBinding.wrappedValue = id }
                        }
                    }
                    .fixedSize()
                    .controlSize(.small)
                }
                Button("Load models") { Task { await loadModels() } }
                    .controlSize(.small)
                    .disabled(loading)
                if loading { ProgressView().controlSize(.small) }
            }
            if let error {
                Text(error).font(.caption).foregroundStyle(.red).lineLimit(2)
            } else if models?.isEmpty == true {
                Text("No models listed; type the model id.").font(.caption).foregroundStyle(.secondary)
            }
        }
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

/// Sends a one-token "ping" (CLIs: a tiny prompt), or runs `check` when set, and shows OK or a short error. Not run
/// while idle.
private struct TestButton: View {
    let title: String
    let config: ProviderConfig
    let model: AppModel
    var check: (@MainActor () async throws -> Void)?

    @State private var task: Task<Void, Never>?
    @State private var result: (ok: Bool, text: String)?

    var body: some View {
        HStack(spacing: 6) {
            Button(task == nil ? title : "Cancel") {
                if let task { task.cancel() } else { run() }
            }
            .controlSize(.small)
            if task != nil {
                ProgressView().controlSize(.small)
            } else if let result {
                Text(result.text)
                    .font(.caption)
                    .foregroundStyle(result.ok ? .green : .red)
                    .lineLimit(2)
            }
        }
        .onDisappear { task?.cancel() }
    }

    private func run() {
        result = nil
        let provider = model.makeProvider(for: config)
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

/// The first line of an error, at most 160 characters.
private func shortMessage(_ error: Error) -> String {
    let line = error.localizedDescription.split(whereSeparator: \.isNewline).first.map(String.init) ?? ""
    return line.count > 160 ? String(line.prefix(160)) + "..." : line
}
