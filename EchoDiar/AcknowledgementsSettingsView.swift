import SwiftUI

/// Open-source software built into EchoDiar: the packages the app links, with the licenses copied from their
/// checkouts into `Licenses/`. Versions as in `Packages/EchoKit/Package.resolved` (Sparkle: `project.yml`).
struct Acknowledgement: Identifiable {
    let name: String
    let version: String
    let purpose: LocalizedStringResource
    let license: String
    /// The license's file in `Licenses/`, without `.txt`.
    let file: String

    var id: String { file }

    /// The bundled license text, nil when the file is missing from the bundle.
    var licenseText: String? {
        Bundle.main.url(forResource: file, withExtension: "txt").flatMap { try? String(contentsOf: $0, encoding: .utf8) }
    }

    static let all: [Acknowledgement] = [
        .init(name: "Sparkle", version: "2.10.0", purpose: "EchoDiar's updates", license: "MIT", file: "Sparkle"),
        .init(name: "WhisperKit", version: "1.1.1", purpose: "Whisper transcription", license: "MIT", file: "WhisperKit"),
        .init(name: "FluidAudio", version: "0.17.7", purpose: "Parakeet transcription and speaker diarization",
              license: "Apache 2.0", file: "FluidAudio"),
        .init(name: "Silero VAD", version: "6.2.1", purpose: "Speech detection for Whisper transcription",
              license: "MIT", file: "silero-vad"),
        .init(name: "speech-swift", version: "0.0.28", purpose: "Qwen3 transcription", license: "Apache 2.0",
              file: "speech-swift"),
        .init(name: "MLX Swift", version: "0.31.6", purpose: "Machine learning on Apple silicon", license: "MIT",
              file: "mlx-swift"),
        .init(name: "MLX Swift LM", version: "3.31.4", purpose: "Local language and embedding models", license: "MIT",
              file: "mlx-swift-lm"),
        .init(name: "swift-transformers", version: "1.3.5", purpose: "Tokenizers and model downloads",
              license: "Apache 2.0", file: "swift-transformers"),
        .init(name: "swift-huggingface", version: "1.0.0", purpose: "Hugging Face downloads", license: "Apache 2.0",
              file: "swift-huggingface"),
        .init(name: "Jinja", version: "2.5.2", purpose: "Chat templates of local models", license: "Apache 2.0",
              file: "swift-jinja"),
        .init(name: "EventSource", version: "1.5.1", purpose: "Used by swift-huggingface", license: "MIT",
              file: "EventSource"),
        .init(name: "yyjson", version: "0.12.0", purpose: "Used by swift-transformers", license: "MIT", file: "yyjson"),
        .init(name: "Swift Collections", version: "1.7.3", purpose: "Used by swift-transformers",
              license: "Apache 2.0", file: "swift-collections"),
        .init(name: "Swift Crypto", version: "4.5.2", purpose: "Used by swift-transformers", license: "Apache 2.0",
              file: "swift-crypto"),
        .init(name: "Swift Numerics", version: "1.1.1", purpose: "Used by MLX Swift", license: "Apache 2.0",
              file: "swift-numerics"),
    ]
}

/// The Acknowledgements tab: one row per package; choosing one shows its license in a sheet.
struct AcknowledgementsSettingsView: View {
    @State private var shown: Acknowledgement?

    var body: some View {
        Form {
            Section {
                ForEach(Acknowledgement.all) { item in
                    Button { shown = item } label: { AcknowledgementRow(item: item) }
                        .buttonStyle(.plain)
                }
            } header: {
                SectionHeader(description: "EchoDiar includes this open-source software. Choose one to read its license.")
            }
        }
        .formStyle(.grouped)
        .sheet(item: $shown) { LicenseSheet(item: $0) }
    }
}

/// Name and version, what EchoDiar uses it for, its license; a chevron like a row that opens more.
private struct AcknowledgementRow: View {
    let item: Acknowledgement

    var body: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(verbatim: item.name).fontWeight(.semibold)
                    Text(verbatim: item.version).font(.caption).foregroundStyle(.secondary)
                }
                Text(item.purpose).font(.callout).foregroundStyle(.secondary)
                Text(verbatim: item.license).font(.caption).foregroundStyle(.tertiary)
            }
            Spacer()
            Image(systemName: "chevron.right").foregroundStyle(.tertiary)
        }
        .contentShape(.rect)
    }
}

/// The package's license text, selectable; Done or Esc closes it.
private struct LicenseSheet: View {
    let item: Acknowledgement
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(verbatim: item.name).font(.title3.weight(.semibold))
                Text(verbatim: "\(item.version) · \(item.license)").foregroundStyle(.secondary)
            }
            ScrollView {
                Group {
                    if let text = item.licenseText {
                        Text(verbatim: text)
                    } else {
                        Text("The license text is missing from this build.")
                    }
                }
                .font(.callout.monospaced())
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(12)
            }
            .background(.background.secondary, in: .rect(cornerRadius: 8))
            HStack {
                Spacer()
                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 600, height: 520)
    }
}
