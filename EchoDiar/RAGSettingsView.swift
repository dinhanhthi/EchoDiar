import EchoAI
import EchoCore
import SwiftUI

/// The RAG tab: the opt-in switch of the library chat, the index's status with Rebuild, then the embedder of the index
/// (the catalog's MLX models with their downloads, Apple's built-in one, or an enabled OpenAI-compatible or local-host
/// provider with its model). Picking a cloud embedder asks once per provider and destination
/// (`AppSettings.cloudEmbeddingConfirmed`).
struct RAGSettingsView: View {
    let model: AppModel

    /// A cloud API embedder picked (or in use and not confirmed) waiting for its confirmation; the setting changes
    /// only on Understood.
    @State private var pendingCloud: ProviderConfig?
    /// The API embedder's model as typed; saved on Return or on leaving the field.
    @State private var apiModel = ""
    @FocusState private var apiModelFocused: Bool
    /// The picked catalog embedder, not installed when RAG was turned on: asks to download it.
    @State private var pendingDownload: ModelEntry?
    /// The API embedder's embedding models from "Load models"; nil until loaded for the provider in use.
    @State private var loadedModels: [String]?
    @State private var loadingModels = false
    @State private var loadError: String?

    var body: some View {
        ScrollViewReader { proxy in
            Form {
                enableSection
                statusSection
                localSection
                apiSection
            }
            .formStyle(.grouped)
            .alert(
                "Download \(pendingDownload?.displayName ?? "")?",
                isPresented: Binding(get: { pendingDownload != nil }, set: { if !$0 { pendingDownload = nil } }),
                presenting: pendingDownload
            ) { entry in
                Button("Download") {
                    model.downloadModel(entry.id)
                    // The download's progress shows in the model's row, below the index.
                    withAnimation { proxy.scrollTo(entry.id, anchor: .top) }
                }
                Button("Not Now", role: .cancel) {}
            } message: { entry in
                Text("Indexing your conversations on this Mac needs this embedding model (\(ModelRow.sizeText(entry.diskMB))). You can also pick another model below.")
            }
        }
        .task { model.refreshInstalledModels() }
        // The tab stays alive once opened: each time it is shown, provider, key and model changes made elsewhere are
        // picked up.
        .onChange(of: shown, initial: true) { _, shown in if shown { model.syncLibraryIndex() } }
        .onChange(of: model.settings.embeddingAPIModel, initial: true) { _, saved in apiModel = saved }
        // Loaded models belong to the provider they came from.
        .onChange(of: model.settings.embeddingModelID) {
            loadedModels = nil
            loadError = nil
        }
        // Leaving the field saves too, so a typed model is not lost without Return; on the form, as picking another
        // embedder removes the field.
        .onChange(of: apiModelFocused) { _, focused in if !focused { saveAPIModel() } }
        .alert(
            "Send transcripts to \(pendingCloud?.displayName ?? "")?",
            isPresented: Binding(get: { pendingCloud != nil }, set: { if !$0 { pendingCloud = nil } }),
            presenting: pendingCloud
        ) { config in
            Button("Understood") { confirm(config) }
            Button("Cancel", role: .cancel) {}
        } message: { config in
            Text(consentMessage(config))
        }
    }

    /// The RAG tab is selected on the Settings page.
    private var shown: Bool { model.route == .settings && model.settingsTab == .rag }

    private var enableSection: some View {
        Section {
            Toggle(isOn: Binding(get: { model.settings.ragEnabled }, set: { on in
                model.setting(\.ragEnabled).wrappedValue = on
                if on { askToDownloadIfMissing() }
            })) {
                Text("Ask across conversations")
                Text("Answers questions about all your transcribed conversations, citing the conversation and the moment. The index stays on this Mac unless you pick an API embedder.")
            }
        }
    }

    // MARK: - Embedding model

    private var localSection: some View {
        Section {
            ForEach(ModelCatalog.embedding) { entry in
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    radio(entry.id, name: entry.displayName)
                    ModelRow(entry: entry, model: model)
                }
                .id(entry.id)
            }
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                radio("apple-nl", name: "Apple (Basic, built-in)")
                VStack(alignment: .leading, spacing: 2) {
                    Text("Apple (Basic, built-in)")
                    Text("Nothing to download. Rougher ranking than the models above.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 8)
                Text("Built in")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        } header: {
            SectionHeader(
                title: "On this Mac",
                description: "The embedding model lets a question find passages with the same meaning, in any language. Models are downloaded only when you click Download.")
        }
    }

    /// "Via API": the model of the provider in use (its own box, as it follows the pick), then the providers.
    @ViewBuilder private var apiSection: some View {
        if let config = currentAPIConfig {
            Section {
                apiModelField(config)
            } header: {
                apiHeader
            }
            providersSection(header: false)
        } else {
            providersSection(header: true)
        }
    }

    private func providersSection(header: Bool) -> some View {
        Section {
            ForEach(embeddingConfigs) { config in
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    radio(AppModel.apiEmbedderPrefix + config.id, name: config.displayName) { selectAPI(config) }
                        .disabled(!config.enabled)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(config.displayName)
                        Text(Self.host(config).isEmpty ? "Base URL not set" : Self.host(config))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 8)
                    if config.enabled {
                        Text(AppModel.AIProvider.isLoopback(baseURL: config.baseURL) ? "This Mac" : "Leaves this Mac")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    } else {
                        Button("Set Up…") {
                            // Local hosts have a row only once added (scanned or by URL).
                            if config.kind != .local || model.settings.providers.contains(where: { $0.id == config.id }) {
                                model.settingsExpandedProvider = config.id
                            }
                            model.openSettings(tab: .providers, subTab: config.kind == .local ? "onThisMac" : "cloudAPIs")
                        }
                        .controlSize(.small)
                        .help("Turn on \(config.displayName) in Settings › AI")
                    }
                }
            }
        } header: {
            if header { apiHeader }
        }
    }

    private var apiHeader: some View {
        SectionHeader(
            title: "Via API",
            description: "A cloud provider receives the transcripts and summaries to index, and each question.")
    }

    /// A radio button that picks `id`; `action` replaces the plain pick (API embedders ask for consent first).
    private func radio(_ id: String, name: String, action: (() -> Void)? = nil) -> some View {
        let selected = model.settings.embeddingModelID == id
        return Button {
            if let action { action() } else { select(id) }
        } label: {
            Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                .foregroundStyle(selected ? Color.accentColor : .secondary)
        }
        .buttonStyle(.plain)
        .help("Use \(name)")
        .accessibilityLabel("Use \(name)")
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    /// The model id with "Load models": a menu of the provider's embedding models once loaded.
    private func apiModelField(_ config: ProviderConfig) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                TextField("Model", text: $apiModel,
                          prompt: Text(Self.suggestedModels[config.preset ?? ""] ?? "Embedding model id"))
                    .focused($apiModelFocused)
                    .onSubmit(saveAPIModel)
                if let loadedModels, !loadedModels.isEmpty {
                    Menu("\(loadedModels.count) model\(loadedModels.count == 1 ? "" : "s")") {
                        ForEach(loadedModels, id: \.self) { id in
                            Button(id) {
                                apiModel = id
                                saveAPIModel()
                            }
                        }
                    }
                    .fixedSize()
                }
                Button("Load models") { Task { await loadModels(config) } }
                    .disabled(loadingModels)
                if loadingModels { ProgressView().controlSize(.small) }
            }
            if let loadError {
                Text(loadError).font(.caption).foregroundStyle(.red).lineLimit(2)
            } else if loadedModels?.isEmpty == true {
                Text("No embedding models listed; type the model id.").font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    /// The provider's models that embed, by name: its chat models can't.
    private func loadModels(_ config: ProviderConfig) async {
        loadingModels = true
        defer { loadingModels = false }
        do {
            let all = try await model.makeProvider(for: config)?.listModels() ?? []
            loadedModels = all.filter(Self.isEmbeddingModel).sorted()
            loadError = nil
        } catch {
            loadError = error.localizedDescription.split(whereSeparator: \.isNewline).first.map(String.init)
        }
    }

    /// Embedding models are named after it (text-embedding-3-small, mistral-embed, nomic-embed-text) or their family.
    private static func isEmbeddingModel(_ id: String) -> Bool {
        let id = id.lowercased()
        return ["embed", "bge", "e5-", "gte-", "minilm", "arctic"].contains { id.contains($0) }
    }

    /// The model filled in when a provider is picked, by preset.
    private static let suggestedModels = [
        "openai": "text-embedding-3-small", "gemini": "gemini-embedding-001", "mistral": "mistral-embed",
        "ollama": "nomic-embed-text",
    ]

    /// The providers that can embed, enabled or not: OpenAI-compatible APIs with an embeddings endpoint (custom
    /// endpoints included) and the local hosts. Anthropic, xAI, Groq, DeepSeek and the CLIs have none.
    private var embeddingConfigs: [ProviderConfig] {
        model.providerConfigs.filter { config in
            switch config.kind {
            case .local: true
            case .openAICompatible: config.preset.map(["openai", "gemini", "mistral", "azure"].contains) ?? true
            default: false
            }
        }
    }

    /// The enabled OpenAI-compatible and local-host providers (Ollama included), as `makeEmbedder()` accepts them.
    private var apiConfigs: [ProviderConfig] {
        model.providerConfigs.filter { $0.enabled && ($0.kind == .openAICompatible || $0.kind == .local) }
    }

    /// The config of the API embedder in use, when its provider is still enabled.
    private var currentAPIConfig: ProviderConfig? {
        let id = model.settings.embeddingModelID
        guard id.hasPrefix(AppModel.apiEmbedderPrefix) else { return nil }
        return apiConfigs.first { AppModel.apiEmbedderPrefix + $0.id == id }
    }

    private func select(_ id: String) {
        model.updateSettings { $0.embeddingModelID = id }
    }

    private func askToDownloadIfMissing() {
        let id = model.settings.embeddingModelID
        guard let entry = ModelCatalog.embedding.first(where: { $0.id == id }),
              !model.installedModelIDs.contains(id), model.downloads[id] == nil
        else { return }
        pendingDownload = entry
    }

    /// A cloud destination not confirmed yet asks first; the selection stays until Understood.
    private func selectAPI(_ config: ProviderConfig) {
        let id = AppModel.apiEmbedderPrefix + config.id
        guard model.settings.embeddingModelID != id else { return }
        if needsConsent(config) {
            pendingCloud = config
        } else {
            selectAPIEmbedder(config)
        }
    }

    /// The previous provider's model name rarely exists on this one: the preset's suggestion replaces it.
    private func selectAPIEmbedder(_ config: ProviderConfig) {
        model.updateSettings {
            $0.embeddingModelID = AppModel.apiEmbedderPrefix + config.id
            $0.embeddingAPIModel = Self.suggestedModels[config.preset ?? ""] ?? ""
        }
    }

    private func needsConsent(_ config: ProviderConfig) -> Bool {
        !AppModel.AIProvider.isLoopback(baseURL: config.baseURL)
            && !model.settings.cloudEmbeddingConfirmed.contains(model.embeddingDestination(config))
    }

    /// `confirmCloudEmbedding()` reads the embedder from the settings, so the pick is saved first.
    private func confirm(_ config: ProviderConfig) {
        selectAPIEmbedder(config)
        model.confirmCloudEmbedding()
    }

    private func consentMessage(_ config: ProviderConfig) -> String {
        let count = model.conversations.filter(\.hasTranscript).count
        return "Transcripts and summaries of \(count) conversation\(count == 1 ? "" : "s"), and your questions, will be sent to \(Self.host(config)) to build and search the index, and billed by \(config.displayName). The provider may keep them under its own retention policy. You are asked once per provider and destination."
    }

    private func saveAPIModel() {
        let trimmed = apiModel.trimmingCharacters(in: .whitespacesAndNewlines)
        apiModel = trimmed
        guard trimmed != model.settings.embeddingAPIModel else { return }
        model.updateSettings { $0.embeddingAPIModel = trimmed }
    }

    private static func host(_ config: ProviderConfig) -> String {
        URL(string: config.baseURL.trimmingCharacters(in: .whitespacesAndNewlines))?.host() ?? config.baseURL
    }

    // MARK: - Index

    private var statusSection: some View {
        Section {
            let status = model.ragStatus
            if model.settings.ragEnabled {
                LabeledContent("Indexed \(status.indexed) of \(status.total) conversations") {
                    if status.running && !status.paused { ProgressView().controlSize(.small) }
                }
                if status.paused {
                    Text("Paused while recording or transcribing").foregroundStyle(.secondary)
                }
                if status.needsConsent, let config = currentAPIConfig {
                    LabeledContent {
                        Button("Confirm…") { pendingCloud = config }
                            .controlSize(.small)
                    } label: {
                        Text("Needs confirmation")
                        Text("Nothing is sent to \(Self.host(config)) until you confirm.")
                    }
                }
                if let message = status.message {
                    Text(message).foregroundStyle(.red)
                }
            }
            LabeledContent {
                Button("Rebuild") { model.rebuildLibraryIndex() }
                    .controlSize(.small)
                    .disabled(!model.settings.ragEnabled)
            } label: {
                Text("Rebuild index")
                Text("Deletes the index and embeds every conversation again.")
            }
        } header: {
            Text("Index")
        }
    }
}
