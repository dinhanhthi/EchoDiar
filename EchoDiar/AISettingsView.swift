import EchoAI
import EchoCore
import SwiftUI

/// The AI Settings tab, in inner tabs: the chat model with the chat context and the answer language, the summary model,
/// catch-up while recording, the default transcription and diarization models (the Transcription page's settings), and
/// Library chat (`RAGSettingsView`). The providers themselves are set up in AI Providers.
struct AISettingsView: View {
    let model: AppModel

    /// Ids in `SettingsTab.aiSettings.subTabs`; picked in the toolbar.
    private enum Tab: String {
        case chat, summary, catchUp, transcription, library
    }

    private var tab: Tab { model.settingsSubTab(.aiSettings).flatMap(Tab.init) ?? .chat }

    var body: some View {
        switch tab {
        case .library:
            RAGSettingsView(model: model)
        case .transcription:
            Form {
                Section("Transcription model") { DefaultEnginePicker(kind: .asr, model: model) }
                Section("Speakers") { DefaultEnginePicker(kind: .diarization, model: model) }
            }
            .formStyle(.grouped)
            .task { model.refreshInstalledModels() }
        case .chat, .summary, .catchUp:
            Form {
                switch tab {
                case .chat: chatSection
                case .summary: summarySection
                default: catchUpSection
                }
            }
            .formStyle(.grouped)
        }
    }

    // MARK: - Chat and summary

    private var chatSection: some View {
        Section {
            AIModelPicker(
                model: model, purpose: .chat, title: "Chat model",
                caption: "Used for questions in the Chat tab and Library chat. The model menu in the Chat tab changes it too.")
            // How many earlier messages each chat question carries, for every provider.
            LabeledContent {
                Picker("Chat context", selection: model.setting(\.chatContextMessages)) {
                    ForEach(AppSettings.chatContextOptions, id: \.self) { Text("\($0) messages").tag($0) }
                    Text("Whole chat").tag(0)
                }
                .labelsHidden()
                .fixedSize()
            } label: {
                Text("Chat context")
                Text("Earlier messages sent with each question.")
            }
            languagePicker
        }
    }

    private var summarySection: some View {
        Section {
            AIModelPicker(
                model: model, purpose: .summary, title: "Summary model",
                caption: "Used for new summaries. The model menu in the Summary tab changes it too.")
            languagePicker
        }
    }

    /// `summaryLanguage`, one setting for summaries and answers, with the options of the AI pane's language menu.
    private var languagePicker: some View {
        Picker("AI answer language", selection: AILanguageMenu.languageBinding(model)) {
            AILanguageMenu.options(model.settings.summaryLanguage)
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
            } else {
                // Automatic runs the Summary model: shown, not picked, so the row (and its search entry) is always here.
                LabeledContent {
                    Text(automaticCatchUpModelLabel)
                } label: {
                    Text("Catch-up model")
                    Text("The Summary model. Pick a provider above to choose another.")
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
                Text("Catch up needs an AI provider outside the app: a cloud API, a local server or a command-line tool. Add one in Settings › AI Providers.")
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

    /// What an Automatic catch-up runs with: "Automatic (model)", or "Automatic" with no provider or a CLI's own default.
    private var automaticCatchUpModelLabel: String {
        guard let provider = model.catchUpProvider else { return String(localized: "Automatic") }
        let id = model.catchUpModel(for: provider)
        return id.isEmpty ? String(localized: "Automatic") : String(localized: "Automatic (\(id))")
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
}

/// `purpose`'s default model as a Form row, with the choices of the AI pane's model menu (`AIModelMenu`) that can run
/// now: the in-app models first, then each provider's listed models.
private struct AIModelPicker: View {
    let model: AppModel
    let purpose: AppModel.AIPurpose
    let title: LocalizedStringKey
    let caption: LocalizedStringKey

    /// A provider and one of its models ("" = a CLI's own default).
    private struct Choice: Hashable {
        let providerID: String
        let model: String
    }

    /// A menu section: the in-app models (id "") or one provider's.
    private struct ChoiceSection {
        let id: String
        let title: String
        let items: [(choice: Choice, title: String)]
    }

    var body: some View {
        LabeledContent {
            if let current {
                Picker(title, selection: binding(current)) {
                    ForEach(groups(current), id: \.id) { group in
                        Section(group.title) {
                            ForEach(group.items, id: \.choice) { item in Text(item.title).tag(item.choice) }
                        }
                    }
                }
                .labelsHidden()
                .fixedSize()
            } else {
                Text("No AI model").foregroundStyle(.secondary)
            }
        } label: {
            Text(title)
            Text(caption)
        }
    }

    /// What `purpose` runs with now; nil when no provider can run.
    private var current: Choice? {
        model.currentAIProvider(for: purpose).map {
            Choice(providerID: $0.id, model: model.aiModel(for: $0, purpose: purpose))
        }
    }

    private func binding(_ current: Choice) -> Binding<Choice> {
        Binding(get: { current }, set: { choice in
            guard let provider = model.aiProviders.first(where: { $0.id == choice.providerID }) else { return }
            model.setAIModel(choice.model, of: provider, purpose: purpose)
        })
    }

    /// The usable providers in the menu's order, each with its models; `current` is added to its provider when it is
    /// not listed (a host without a model yet), so the picker always shows what runs.
    private func groups(_ current: Choice) -> [ChoiceSection] {
        let usable = model.aiProviders.filter { $0.unavailableReason == nil }
        let inApp = usable.filter { $0.kind == .inApp }
        let hosts = usable.filter { $0.kind != .inApp && !$0.leavesMac } + usable.filter(\.leavesMac)
        var groups: [ChoiceSection] = []
        if !inApp.isEmpty {
            groups.append(ChoiceSection(id: "", title: String(localized: "On this Mac"), items: inApp.map {
                (Choice(providerID: $0.id, model: $0.id == current.providerID ? current.model : $0.defaultModel), $0.name)
            }))
        }
        for provider in hosts {
            var ids = provider.models
            if current.providerID == provider.id && !ids.contains(current.model) { ids.append(current.model) }
            guard !ids.isEmpty else { continue }
            let title = provider.leavesMac ? String(localized: "\(provider.name) (leaves this Mac)") : provider.name
            groups.append(ChoiceSection(id: provider.id, title: title, items: ids.map {
                (Choice(providerID: provider.id, model: $0), $0.isEmpty ? String(localized: "Default") : $0)
            }))
        }
        return groups
    }
}
