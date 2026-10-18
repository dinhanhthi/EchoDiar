import EchoCore
import EchoTranscribe
import SwiftUI

/// The Transcription tab: the ASR and diarization catalog by tier, with Download, Cancel and Delete.
struct ModelsSettingsView: View {
    let model: AppModel

    /// LLM entries join in Phase 9.
    private static let entries = ModelCatalog.visibleEntries(
        registeredEngineRuntimes: TranscriptionPipeline.registeredRuntimes
    ).filter { $0.kind != .llm }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                ForEach(ModelEntry.Tier.allCases, id: \.self) { tier in
                    let entries = Self.entries.filter { $0.tier == tier }
                    if !entries.isEmpty {
                        Section(tier.rawValue.capitalized) {
                            ForEach(entries) { ModelRow(entry: $0, model: model) }
                        }
                    }
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
            .padding(.bottom, 12)
        }
        .task { model.refreshInstalledModels() }
    }
}

private struct ModelRow: View {
    let entry: ModelEntry
    let model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Text(entry.displayName)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .help(entry.displayName)
                if entry.isDefault { Badge(text: "Default", color: .accentColor) }
                Spacer(minLength: 8)
                actions
            }
            HStack(spacing: 4) {
                if entry.kind == .diarization { Badge(text: "Speakers") }
                Badge(text: Self.sizeText(entry.diskMB))
                Badge(text: (entry.ramIsEstimate ? "~" : "") + Self.ramText(entry.ramMB) + " RAM")
                Badge(text: Self.languagesText(entry.languages))
                Badge(text: entry.license)
                Badge(text: "macOS \(entry.minimumMacOS)+")
            }
            notes
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

    @ViewBuilder private var actions: some View {
        if let download = model.downloads[entry.id] {
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
                .disabled(model.transcriptionProgress.keys.contains(where: model.isTranscribing))
        } else if entry.repo == nil {
            Text("Built in")
                .font(.caption)
                .foregroundStyle(.secondary)
        } else {
            Button("Download") { model.downloadModel(entry.id) }
                .controlSize(.small)
                .disabled(!ProcessInfo.processInfo.isOperatingSystemAtLeast(
                    OperatingSystemVersion(majorVersion: entry.minimumMacOS, minorVersion: 0, patchVersion: 0)))
        }
    }

    @ViewBuilder private var notes: some View {
        let fr = entry.qualityNotes.fr
        let vi = entry.qualityNotes.vi
        Group {
            if !fr.isEmpty && fr == vi {
                Text("French and Vietnamese: \(fr)")
            } else {
                if !fr.isEmpty { Text("French: \(fr)") }
                if !vi.isEmpty { Text("Vietnamese: \(vi)") }
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
    }

    private static func sizeText(_ mib: Int) -> String {
        mib < 1024 ? "\(mib) MiB" : String(format: "%.1f GB", Double(mib) / 1024)
    }

    private static func ramText(_ mb: Int) -> String {
        mb < 1024 ? "\(mb) MB" : String(format: "%.1f GB", Double(mb) / 1024)
    }

    private static func languagesText(_ languages: ModelEntry.Languages) -> String {
        switch languages {
        case .multilingual:
            "Multilingual"
        case .codes(let codes) where codes.count == 1:
            Locale(identifier: "en").localizedString(forLanguageCode: codes[0]) ?? codes[0]
        case .codes(let codes):
            "\(codes.count) languages"
        }
    }
}

private struct Badge: View {
    let text: String
    var color: Color = .secondary

    var body: some View {
        Text(text)
            .font(.caption2.weight(.medium))
            .foregroundStyle(color)
            .lineLimit(1)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(color.opacity(0.18), in: Capsule())
    }
}
