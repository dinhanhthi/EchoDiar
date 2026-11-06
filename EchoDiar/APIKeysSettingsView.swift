import EchoAI
import EchoCore
import SwiftUI

/// The cloud provider cards in sections, for a `Form` around them (AI Providers › Cloud): the LLM presets, the
/// speech-to-text APIs, then the custom OpenAI-compatible endpoints with "Add Endpoint". One card per cloud provider
/// that takes a key, with its key, Test and where to get a key, and its default models; Azure and custom endpoints also
/// set their base URL. Keys go to the Keychain only.
struct ProviderCardSections: View {
    let model: AppModel

    var body: some View {
        Section {
            ForEach(configs { $0.preset != nil && [.openAICompatible, .anthropic].contains($0.kind) }) { config in
                APIKeyCard(config: config, model: model)
            }
        } header: {
            Text("Language models")
        }
        Section {
            ForEach(configs { $0.kind == .speechToText }) { config in
                APIKeyCard(config: config, model: model)
            }
        } header: {
            Text("Speech-to-text APIs")
        }
        Section {
            ForEach(configs { $0.preset == nil && $0.kind == .openAICompatible }) { config in
                APIKeyCard(config: config, model: model)
            }
            Button("Add Endpoint") {
                model.updateSettings { $0.providers.append(.custom()) }
            }
            .controlSize(.small)
        } header: {
            Text("Custom OpenAI-compatible endpoints")
        }
    }

    private func configs(_ isIncluded: (ProviderConfig) -> Bool) -> [ProviderConfig] {
        model.providerConfigs.filter(isIncluded)
    }
}

/// A cloud provider's card: collapsed, its name, a checkmark once it is available, and "Base URL not set" for Azure or a
/// custom endpoint without one (else a custom endpoint's base URL); expanded, its key and Test, plus the base URL for
/// Azure and custom endpoints, and a custom endpoint's name, authentication and Remove; then its default model for each
/// kind of work it can do (text generation, transcription, diarization, embedding). No enabled toggle: a cloud API is
/// available once its key is saved.
struct APIKeyCard: View {
    let config: ProviderConfig
    let model: AppModel

    @State private var expanded = false
    @State private var pendingKey = ""

    private var isCustom: Bool { config.preset == nil }
    private var takesKey: Bool { CloudTranscription.needsKey(config) }
    private var generatesText: Bool { [.openAICompatible, .anthropic].contains(config.kind) }
    private var diarizes: Bool { CloudTranscription.diarizationPresets.contains(config.preset ?? "") }
    /// The rule of `RAGSettingsView.embeddingConfigs` without the local hosts: OpenAI-compatible APIs with an
    /// embeddings endpoint, custom endpoints included.
    private var embeds: Bool {
        config.kind == .openAICompatible && (config.preset.map(["openai", "gemini", "mistral", "azure"].contains) ?? true)
    }

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
                if isCustom {
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
                if takesKey { APIKeyField(providerID: config.id, model: model, key: $pendingKey) }
                if isCustom || config.id == "azure" {
                    FieldRow("Base URL") {
                        TextField("Base URL", text: binding(\.baseURL), prompt: Text(config.id == "azure" ? "https://<resource>.openai.azure.com/openai/v1" : ""))
                            .labelsHidden()
                    }
                }
                if config.id == "azure" {
                    Text("Required: your resource's https://<resource>.openai.azure.com/openai/v1 URL.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .inputColumn()
                }
                Divider()
                models
                Divider()
                HStack(alignment: .top) {
                    testButton
                    Spacer()
                    if isCustom {
                        Button("Remove", role: .destructive) { model.removeProvider(id: config.id) }
                    }
                }
                .inputColumn()
            }
            .fieldLabelColumn()
            .padding(.top, 6)
            .padding(.bottom, 4)
            .padding(.leading, TitleChevronDisclosureStyle.indent)
        } label: {
            // Collapsed, it still says whether the provider is set up: its checkmark, or its missing base URL.
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(config.displayName.isEmpty ? String(localized: "Untitled") : config.displayName)
                    if (isCustom || config.id == "azure"),
                       config.baseURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        Text("Base URL not set").font(.caption).foregroundStyle(.secondary)
                    } else if isCustom {
                        Text(config.baseURL).font(.caption).foregroundStyle(.secondary)
                    }
                }
                Spacer()
                AvailabilityCheckmark(config: config, model: model)
            }
        }
        .disclosureGroupStyle(TitleChevronDisclosureStyle())
    }

    @ViewBuilder private var models: some View {
        if generatesText {
            ModelField(config: config, model: model, label: "Text generation model",
                       prompt: config.id == "azure" ? "Deployment name" : "Model id") {
                try await model.makeProvider(for: config)?.listModels() ?? []
            }
        }
        if CloudTranscription.supports(config) {
            VStack(alignment: .leading, spacing: 4) {
                CloudModelField(config: config, model: model, label: "Transcription model")
                CloudPriceBadge(config: config).inputColumn()
                Text("Prices are approximate list prices per audio hour; the mic and system channels are sent and billed separately.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .inputColumn()
                if config.preset == "gemini" {
                    Text("LLM transcription — timestamps approximate.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .inputColumn()
                }
                // A custom endpoint transcribes once its base URL and its model are set.
                if isCustom, config.baseURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    Text("Base URL not set")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .inputColumn()
                }
            }
        }
        if diarizes {
            VStack(alignment: .leading, spacing: 4) {
                CloudModelField(config: config, model: model, diarization: true, label: "Diarization model")
                Text("Only the system channel is uploaded, as one extra transcription billed by the provider; only the speaker labels are kept. Not used when the transcription model already labels speakers. Prices are approximate list prices per audio hour.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .inputColumn()
                if config.preset == "gemini" {
                    Text("LLM diarization — timestamps approximate, speakers can be mislabeled.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .inputColumn()
                }
            }
        }
        if embeds {
            EmbeddingModelField(config: config, model: model)
        }
    }

    /// A speech-to-text API, or an LLM without a model yet, checks its key with a free GET (no audio is uploaded, no
    /// model needed); the others send a one-token ping.
    @ViewBuilder private var testButton: some View {
        if config.kind == .speechToText {
            TestButton(title: "Test", config: config, model: model, pendingKey: $pendingKey) {
                guard let key = try KeychainStore.apiKey(forProvider: config.id) else {
                    throw LLMError.provider(String(localized: "Save a key first."))
                }
                do {
                    try await CloudTranscription.checkKey(config: config, apiKey: key)
                } catch let error as LLMError {
                    throw LLMError.provider(CloudTranscription.failureReason(error))
                }
            }
        } else if config.defaultModel.trimmingCharacters(in: .whitespaces).isEmpty {
            // No model yet (Azure, OpenRouter, a new custom endpoint; it is picked above): the ping would fail
            // on the missing model, so the key is checked with the model list instead, a free GET.
            TestButton(title: "Test", config: config, model: model, pendingKey: takesKey ? $pendingKey : nil) {
                _ = try await model.makeProvider(for: config)?.listModels()
            }
        } else {
            TestButton(title: "Test", config: config, model: model, pendingKey: takesKey ? $pendingKey : nil)
        }
    }

    private func binding<Value>(_ keyPath: WritableKeyPath<ProviderConfig, Value>) -> Binding<Value> {
        Binding(get: { config[keyPath: keyPath] },
                set: { value in model.updateProvider(id: config.id) { $0[keyPath: keyPath] = value } })
    }
}

/// A provider's embedding model for Library chat (`ProviderConfig.embeddingModel`), saved on Return, on leaving the
/// field, or when the field goes away (its card collapsed, another tab or page shown: on macOS a click there does not
/// take the focus first), so a typed model is not lost without Return. Not on each keystroke: each change restarts
/// the index while the provider embeds it. "Load models" lists the ones that embed.
struct EmbeddingModelField: View {
    let config: ProviderConfig
    let model: AppModel

    @State private var text = ""
    @State private var loaded: [String]?
    @State private var loading = false
    @State private var error: String?
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            FieldRow("Embedding model") {
                TextField("Embedding model", text: $text, prompt: Text(
                    RAGSettingsView.suggestedModels[config.preset ?? ""] ?? String(localized: "Embedding model id")))
                    .labelsHidden()
                    .focused($focused)
                    .onSubmit(save)
                    .onChange(of: focused) { _, focused in if !focused { save() } }
                if let loaded, !loaded.isEmpty {
                    Menu("\(loaded.count) models") {
                        ForEach(loaded, id: \.self) { id in
                            Button(id) {
                                text = id
                                save()
                            }
                        }
                    }
                    .fixedSize()
                }
                Button("Load models") { Task { await load() } }
                    .disabled(loading)
                if loading { ProgressView().controlSize(.small) }
            }
            if let error {
                Text(error).font(.caption).foregroundStyle(.red).lineLimit(2).inputColumn()
            } else if loaded?.isEmpty == true {
                Text("No embedding models listed; type the model id.")
                    .font(.caption).foregroundStyle(.secondary).inputColumn()
            }
        }
        .onChange(of: config.embeddingModel, initial: true) { _, saved in text = saved }
        .onDisappear {
            // A removed local host is not brought back by its preset (`updateProvider`).
            guard config.isCloud || model.settings.providers.contains(where: { $0.id == config.id }) else { return }
            save()
        }
    }

    private func save() {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        text = trimmed
        guard trimmed != config.embeddingModel else { return }
        model.updateProvider(id: config.id) { $0.embeddingModel = trimmed }
    }

    /// The provider's models that embed, by name: its chat models can't.
    private func load() async {
        loading = true
        defer { loading = false }
        do {
            let all = try await model.makeProvider(for: config)?.listModels() ?? []
            loaded = all.filter(RAGSettingsView.isEmbeddingModel).sorted()
            error = nil
        } catch {
            self.error = shortMessage(error)
        }
    }
}

/// A cloud model card's key status, for the feature tabs: a checkmark once the provider is available (its key saved,
/// or a custom endpoint without auth with its base URL set), and "API key", which opens its card in AI Providers › Cloud.
struct APIKeyStatus: View {
    let config: ProviderConfig
    let model: AppModel

    var body: some View {
        HStack(spacing: 8) {
            AvailabilityCheckmark(config: config, model: model)
            Button("API key") { model.showAPIKey(for: config.id) }
                .controlSize(.small)
        }
    }
}

/// A green checkmark once the provider is available: its key saved, or a custom endpoint without auth with its base
/// URL set. Nothing otherwise.
struct AvailabilityCheckmark: View {
    let config: ProviderConfig
    let model: AppModel

    var body: some View {
        if isAvailable {
            Label("Saved", systemImage: "checkmark.circle.fill")
                .labelStyle(.iconOnly)
                .foregroundStyle(.green)
        }
    }

    private var isAvailable: Bool {
        // Saving or removing a key bumps it; `hasSavedKey` reads a cache it clears.
        _ = model.keychainVersion
        return config.isAvailable(hasKey: model.hasSavedKey(config.id))
    }
}

// MARK: - Fields

/// Space between the label column of an expanded row and its inputs.
let fieldGap: CGFloat = 12

extension EnvironmentValues {
    /// Width of the label column of an expanded row: every input starts at the same x. A card sets it to its widest
    /// label (`fieldLabelColumn()`); elsewhere it stays 136.
    @Entry var fieldLabelWidth: CGFloat = 136
}

/// The widest `FieldRow` label inside a `fieldLabelColumn()` container, at its ideal (untruncated) width.
private struct FieldLabelWidthKey: PreferenceKey {
    static let defaultValue: CGFloat = 0

    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

/// Where to get a preset's API key and look up its model ids; custom endpoints, local hosts and CLIs have none.
let providerLinks: [String: (keys: URL, models: URL)] = [
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
    "speechmatics": (URL(string: "https://portal.speechmatics.com/settings/api-keys/")!,
                     URL(string: "https://docs.speechmatics.com/speech-to-text/models")!),
    "soniox": (URL(string: "https://console.soniox.com/org/projects/api/keys/")!,
               URL(string: "https://docs.soniox.com/stt/models")!),
    "gladia": (URL(string: "https://app.gladia.io/apikeys")!,
               URL(string: "https://docs.gladia.io/chapters/introduction/models")!),
]

extension View {
    /// Lines a note, error or button row up with the inputs, past the label column.
    func inputColumn() -> some View {
        modifier(InputColumn())
    }

    /// Sizes the label column of the `FieldRow`s inside to their widest label, so a card's inputs start just past its
    /// own labels; `inputColumn()` follows it.
    func fieldLabelColumn() -> some View {
        modifier(FieldLabelColumn())
    }
}

private struct InputColumn: ViewModifier {
    @Environment(\.fieldLabelWidth) private var labelWidth

    func body(content: Content) -> some View {
        content.padding(.leading, labelWidth + fieldGap)
    }
}

private struct FieldLabelColumn: ViewModifier {
    @State private var width: CGFloat = 0

    func body(content: Content) -> some View {
        content
            .environment(\.fieldLabelWidth, width)
            .onPreferenceChange(FieldLabelWidthKey.self) { width = $0 }
    }
}

/// A link that opens in the browser, with an arrow saying so.
struct ExternalLink: View {
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

/// A secondary label in the label column (`fieldLabelWidth`, never truncated), then the input stretched over the rest
/// of the row, followed by its buttons. Plain labeled fields in the grouped form right-align their text or butt
/// against the label. The label reports its width to the enclosing `fieldLabelColumn()`.
struct FieldRow<Input: View>: View {
    let label: LocalizedStringKey
    @ViewBuilder let input: Input

    @Environment(\.fieldLabelWidth) private var labelWidth

    init(_ label: LocalizedStringKey, @ViewBuilder input: () -> Input) {
        self.label = label
        self.input = input()
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: fieldGap) {
            Text(label)
                .foregroundStyle(.secondary)
                // Measured at its ideal width, so the column it sets does not feed back into it.
                .fixedSize(horizontal: true, vertical: false)
                .background(GeometryReader { proxy in
                    Color.clear.preference(key: FieldLabelWidthKey.self, value: proxy.size.width)
                })
                .frame(minWidth: labelWidth, alignment: .leading)
            input.textFieldStyle(.roundedBorder)
        }
    }
}

/// The API key, written to the Keychain on Return, on leaving the field, or by Save and Test (`TestButton`). Only
/// "Saved" is shown; the key is never read back into the UI.
struct APIKeyField: View {
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

/// Sends a one-token "ping" (CLIs: a tiny prompt), or runs `check` when set, and shows OK beside it or a short error
/// below.
/// With `pendingKey`, a Save button writes the typed key to the Keychain, and Test saves it first. Not run while idle.
struct TestButton: View {
    let title: LocalizedStringKey
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
                result = (true, String(localized: "OK"))
            } catch {
                // Cancel surfaces as CancellationError or URLError.cancelled: no result then.
                result = Task.isCancelled ? nil : (false, shortMessage(error))
            }
            task = nil
        }
    }
}

/// Writes a typed API key to the Keychain (`AppModel.saveAPIKey`) and clears the field; does nothing when it is blank.
/// Spaces and line breaks are dropped anywhere: a key copied across a wrapped line has one inside, and URLSession then
/// silently drops the whole auth header (the provider answers "header is required").
@MainActor func saveAPIKey(_ key: Binding<String>, providerID: String, model: AppModel) throws {
    let cleaned = key.wrappedValue.filter { !$0.isWhitespace }
    guard !cleaned.isEmpty else { return }
    try model.saveAPIKey(cleaned, for: providerID)
    key.wrappedValue = ""
}

/// The first line of an error, at most 160 characters.
func shortMessage(_ error: Error) -> String {
    let line = error.localizedDescription.split(whereSeparator: \.isNewline).first.map(String.init) ?? ""
    return line.count > 160 ? String(line.prefix(160)) + "..." : line
}
