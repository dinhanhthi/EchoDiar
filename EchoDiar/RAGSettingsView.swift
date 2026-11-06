import EchoAI
import EchoCore
import SwiftUI

/// AI Settings › Library chat: the opt-in switch of the library chat, the index's status with Rebuild, then the
/// embedder of the index (the catalog's MLX models, downloaded in AI Providers › Local AI, Apple's built-in one, or an
/// available OpenAI-compatible or local-host provider, whose model is set in its card). Picking a cloud embedder asks
/// once per provider and destination (`AppSettings.cloudEmbeddingConfirmed`).
struct RAGSettingsView: View {
    let model: AppModel

    /// A cloud API embedder picked (or in use and not confirmed) waiting for its confirmation; the setting changes
    /// only on Understood.
    @State private var pendingCloud: ProviderConfig?
    /// The picked catalog embedder, not installed when RAG was turned on: asks to download it.
    @State private var pendingDownload: ModelEntry?

    var body: some View {
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
            // The download's progress shows in the model's row here and in AI Providers › Local AI.
            Button("Download") { model.downloadModel(entry.id) }
            Button("Not Now", role: .cancel) {}
        } message: { entry in
            Text("Indexing your conversations on this Mac needs this embedding model (\(ModelRow.sizeText(entry.diskMB))). You can also pick another model below.")
        }
        .task { model.refreshInstalledModels() }
        // Each time it is shown (its inner tab picked, or the Settings page and AI Settings shown again), provider, key
        // and model changes made elsewhere are picked up.
        .onChange(of: shown, initial: true) { _, shown in if shown { model.syncLibraryIndex() } }
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

    /// AI Settings › Library chat is selected on the Settings page.
    private var shown: Bool {
        model.route == .settings && model.settingsTab == .aiSettings && model.settingsSubTab(.aiSettings) == "library"
    }

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
                    Text(entry.displayName)
                        .lineLimit(1)
                        .truncationMode(.tail)
                    Spacer(minLength: 8)
                    localState(entry)
                }
            }
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                radio("apple-nl", name: String(localized: "Apple (Basic, built-in)"))
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

    /// Installed, the download's progress, or the way to the download in AI Providers › Local AI.
    @ViewBuilder
    private func localState(_ entry: ModelEntry) -> some View {
        if let download = model.downloads[entry.id] {
            ProgressView(value: download.fraction)
                .frame(width: 90)
            Text(download.fraction.formatted(.percent.precision(.fractionLength(0))))
                .font(.caption)
                .monospacedDigit()
                .foregroundStyle(.secondary)
        } else if model.installedModelIDs.contains(entry.id) {
            Label("Installed", systemImage: "checkmark.circle.fill")
                .font(.caption)
                .foregroundStyle(.green)
        } else {
            Button("Download in AI Providers › Local AI") { model.openSettings(tab: .providers, subTab: "localAI") }
                .controlSize(.small)
        }
    }

    /// "Via API": a note when the provider in use has no embedding model (its own box, as it follows the pick), then
    /// the providers.
    @ViewBuilder private var apiSection: some View {
        if let config = currentAPIConfig,
           config.embeddingModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            Section {
                LabeledContent {
                    Button("Set Up…") { setUp(config) }
                        .controlSize(.small)
                } label: {
                    Text("Embedding model")
                    Text("Not set for \(config.displayName). Enter it in its card in AI Providers.")
                }
            } header: {
                apiHeader
            }
            providersSection(header: false)
        } else {
            providersSection(header: true)
        }
    }

    /// AI Providers on `config`'s row, expanded: Local AI for a local host (which has a row only once added), Cloud
    /// for an API.
    private func setUp(_ config: ProviderConfig) {
        if config.kind != .local || model.settings.providers.contains(where: { $0.id == config.id }) {
            model.settingsExpandedProvider = config.id
        }
        model.openSettings(tab: .providers, subTab: config.kind == .local ? "localAI" : "cloud")
    }

    private func providersSection(header: Bool) -> some View {
        Section {
            ForEach(embeddingConfigs) { config in
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    let available = isAvailable(config)
                    radio(AppModel.apiEmbedderPrefix + config.id, name: config.displayName) { selectAPI(config) }
                        .disabled(!available)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(config.displayName)
                        Text(Self.host(config).isEmpty ? String(localized: "Base URL not set") : Self.host(config))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 8)
                    if available {
                        Text(AppModel.AIProvider.isLoopback(baseURL: config.baseURL) ? "This Mac" : "Leaves this Mac")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    } else if config.kind == .local {
                        Button("Set Up…") { setUp(config) }
                        .controlSize(.small)
                        .help("Turn on \(config.displayName) in Settings › AI Providers › Local AI")
                    }
                    if config.kind != .local { APIKeyStatus(config: config, model: model) }
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

    /// Embedding models are named after it (text-embedding-3-small, mistral-embed, nomic-embed-text) or their family.
    static func isEmbeddingModel(_ id: String) -> Bool {
        let id = id.lowercased()
        return ["embed", "bge", "e5-", "gte-", "minilm", "arctic"].contains { id.contains($0) }
    }

    /// The model filled in when a provider is picked, by preset.
    static let suggestedModels = [
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

    /// The available OpenAI-compatible and local-host providers (Ollama included), as `makeEmbedder()` accepts them.
    private var apiConfigs: [ProviderConfig] {
        model.providerConfigs.filter { isAvailable($0) && ($0.kind == .openAICompatible || $0.kind == .local) }
    }

    /// A cloud API once its key is saved, a local host when enabled (`ProviderConfig.isAvailable(hasKey:)`).
    private func isAvailable(_ config: ProviderConfig) -> Bool {
        _ = model.keychainVersion
        return config.isAvailable(hasKey: model.hasSavedKey(config.id))
    }

    /// The config of the API embedder in use, when its provider is still available.
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

    /// A provider with no embedding model yet gets the preset's suggestion; a model typed before stays. Filled first,
    /// so the index never restarts with the provider picked and no model.
    private func selectAPIEmbedder(_ config: ProviderConfig) {
        let saved = model.providerConfigs.first { $0.id == config.id }?.embeddingModel ?? ""
        if let suggested = Self.suggestedModels[config.preset ?? ""],
           saved.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            model.updateProvider(id: config.id) { $0.embeddingModel = suggested }
        }
        model.updateSettings { $0.embeddingModelID = AppModel.apiEmbedderPrefix + config.id }
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
        return String(localized: "Transcripts and summaries of \(count) conversations, and your questions, will be sent to \(Self.host(config)) to build and search the index, and billed by \(config.displayName). The provider may keep them under its own retention policy. You are asked once per provider and destination.")
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
