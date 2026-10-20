import EchoAI
import EchoCore
import SwiftUI

/// The RAG tab: the opt-in switch of the library chat, the embedder of its index (the catalog's MLX models with their
/// downloads, Apple's built-in one, or an enabled OpenAI-compatible or local-host provider with its model) and the
/// index's status with Rebuild. Picking a cloud embedder asks once per provider and destination
/// (`AppSettings.cloudEmbeddingConfirmed`).
struct RAGSettingsView: View {
    let model: AppModel

    /// A cloud API embedder picked (or in use and not confirmed) waiting for its confirmation; the setting changes
    /// only on Send.
    @State private var pendingCloud: ProviderConfig?
    /// The API embedder's model as typed; saved on Return or on leaving the field.
    @State private var apiModel = ""
    @FocusState private var apiModelFocused: Bool

    var body: some View {
        Form {
            enableSection
            localSection
            apiSection
            statusSection
        }
        .formStyle(.grouped)
        .task { model.refreshInstalledModels() }
        // The tab stays alive once opened: each time it is shown, provider, key and model changes made elsewhere are
        // picked up.
        .onChange(of: shown, initial: true) { _, shown in if shown { model.syncLibraryIndex() } }
        .onChange(of: model.settings.embeddingAPIModel, initial: true) { _, saved in apiModel = saved }
        // Leaving the field saves too, so a typed model is not lost without Return; on the form, as picking another
        // embedder removes the field.
        .onChange(of: apiModelFocused) { _, focused in if !focused { saveAPIModel() } }
        .alert(
            "Send transcripts to \(pendingCloud?.displayName ?? "")?",
            isPresented: Binding(get: { pendingCloud != nil }, set: { if !$0 { pendingCloud = nil } }),
            presenting: pendingCloud
        ) { config in
            Button("Send") { confirm(config) }
            Button("Cancel", role: .cancel) {}
        } message: { config in
            Text(consentMessage(config))
        }
    }

    /// The RAG tab is selected on the Settings page.
    private var shown: Bool { model.route == .settings && model.settingsTab == .rag }

    private var enableSection: some View {
        Section {
            Toggle(isOn: model.setting(\.ragEnabled)) {
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
            Text("On this Mac")
        } footer: {
            Text("The embedding model lets a question find passages with the same meaning, in any language. Models are downloaded only when you click Download.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var apiSection: some View {
        Section {
            let configs = apiConfigs
            if configs.isEmpty {
                Text("No provider enabled: turn on an OpenAI-compatible API or a local host in Settings › AI.")
                    .foregroundStyle(.secondary)
            }
            ForEach(configs) { config in
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    radio(AppModel.apiEmbedderPrefix + config.id, name: config.displayName) { selectAPI(config) }
                    VStack(alignment: .leading, spacing: 2) {
                        Text(config.displayName)
                        Text(Self.host(config))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 8)
                    Text(AppModel.AIProvider.isLoopback(baseURL: config.baseURL) ? "This Mac" : "Leaves this Mac")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            if model.settings.embeddingModelID.hasPrefix(AppModel.apiEmbedderPrefix) {
                TextField("Model", text: $apiModel, prompt: Text("e.g. text-embedding-3-small or nomic-embed-text"))
                    .focused($apiModelFocused)
                    .onSubmit(saveAPIModel)
            }
        } header: {
            Text("Via API")
        } footer: {
            Text("A cloud provider receives the transcripts and summaries to index, and each question.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
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

    /// A cloud destination not confirmed yet asks first; the selection stays until Send.
    private func selectAPI(_ config: ProviderConfig) {
        let id = AppModel.apiEmbedderPrefix + config.id
        guard model.settings.embeddingModelID != id else { return }
        if needsConsent(config) {
            pendingCloud = config
        } else {
            select(id)
        }
    }

    private func needsConsent(_ config: ProviderConfig) -> Bool {
        !AppModel.AIProvider.isLoopback(baseURL: config.baseURL)
            && !model.settings.cloudEmbeddingConfirmed.contains(model.embeddingDestination(config))
    }

    /// `confirmCloudEmbedding()` reads the embedder from the settings, so the pick is saved first.
    private func confirm(_ config: ProviderConfig) {
        select(AppModel.apiEmbedderPrefix + config.id)
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
