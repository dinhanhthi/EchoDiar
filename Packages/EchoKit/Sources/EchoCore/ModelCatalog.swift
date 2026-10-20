import Foundation

/// One downloadable (or system) model in the user-visible catalog.
public struct ModelEntry: Sendable, Hashable, Identifiable {
    public enum Kind: String, Sendable, Hashable, CaseIterable {
        case asr, diarization, llm, embedding
    }

    public enum Tier: String, Sendable, Hashable, CaseIterable {
        case fast, balanced, quality
    }

    public enum Runtime: String, Sendable, Hashable, CaseIterable {
        case whisperKit, fluidAudio, appleSpeech, speechSwift, mlx, foundationModels
    }

    public enum Languages: Sendable, Hashable {
        /// Any language (or language-independent, e.g. diarization). System engines read their real list at runtime.
        case multilingual
        /// BCP-47 codes.
        case codes([String])
    }

    /// Short quality notes for the three first-class content languages.
    public struct QualityNotes: Sendable, Hashable {
        public var en: String
        public var fr: String
        public var vi: String

        public init(en: String, fr: String, vi: String) {
            self.en = en
            self.fr = fr
            self.vi = vi
        }
    }

    /// Stable catalog id (settings, harness args, `Models/<id>/` folder name).
    public var id: String
    public var kind: Kind
    public var tier: Tier
    public var displayName: String
    /// Hugging Face repo id; nil only for system runtimes (`appleSpeech`, `foundationModels`).
    public var repo: String?
    /// Folder inside the repo that holds the model, relative to the repo root.
    public var subfolder: String?
    /// Full HF commit sha the download is pinned to (never `main`); nil only for system runtimes.
    public var revision: String?
    /// Allowlist of path prefixes to download, relative to the repo root, matched on whole path components
    /// (`Encoder.mlmodelc` matches `Encoder.mlmodelc/…`, not `Encoder_v2.mlmodelc`).
    /// nil = everything under `subfolder` (or the whole repo). Mandatory for FluidAudio and MLX entries.
    public var files: [String]?
    /// Repo providing `tokenizer.json` + `tokenizer_config.json` when the model folder lacks them (WhisperKit).
    public var tokenizerRepo: String?
    /// Full HF commit sha for `tokenizerRepo`.
    public var tokenizerRevision: String?
    public var runtime: Runtime
    /// Download size in MiB (allowlisted files plus tokenizer files).
    public var diskMB: Int
    /// RAM while running, in MB.
    public var ramMB: Int
    public var ramIsEstimate: Bool
    public var languages: Languages
    public var qualityNotes: QualityNotes
    public var license: String
    /// Minimum macOS major version.
    public var minimumMacOS: Int
    public var streaming: Bool
    /// Only used for live captions; batch routing never selects it.
    public var liveOnly: Bool
    /// Catalog default for its kind (one per kind).
    public var isDefault: Bool
    /// Vietnamese-only model: hidden from the engine picker unless the language is `vi`.
    public var vietnameseOnly: Bool
    /// Decode window override in seconds (nil = engine default).
    public var windowSeconds: Int?

    public init(
        id: String,
        kind: Kind,
        tier: Tier,
        displayName: String,
        repo: String?,
        subfolder: String? = nil,
        revision: String?,
        files: [String]? = nil,
        tokenizerRepo: String? = nil,
        tokenizerRevision: String? = nil,
        runtime: Runtime,
        diskMB: Int,
        ramMB: Int,
        ramIsEstimate: Bool,
        languages: Languages,
        qualityNotes: QualityNotes,
        license: String,
        minimumMacOS: Int = 15,
        streaming: Bool = false,
        liveOnly: Bool = false,
        isDefault: Bool = false,
        vietnameseOnly: Bool = false,
        windowSeconds: Int? = nil
    ) {
        self.id = id
        self.kind = kind
        self.tier = tier
        self.displayName = displayName
        self.repo = repo
        self.subfolder = subfolder
        self.revision = revision
        self.files = files
        self.tokenizerRepo = tokenizerRepo
        self.tokenizerRevision = tokenizerRevision
        self.runtime = runtime
        self.diskMB = diskMB
        self.ramMB = ramMB
        self.ramIsEstimate = ramIsEstimate
        self.languages = languages
        self.qualityNotes = qualityNotes
        self.license = license
        self.minimumMacOS = minimumMacOS
        self.streaming = streaming
        self.liveOnly = liveOnly
        self.isDefault = isDefault
        self.vietnameseOnly = vietnameseOnly
        self.windowSeconds = windowSeconds
    }

    /// Whether the engine picker offers this entry for a conversation language ("auto" or a BCP-47 code).
    /// Vietnamese-only models are fixed-language, so "auto" does not select them.
    public func isSelectable(for language: String) -> Bool {
        guard vietnameseOnly else { return true }
        let primary = language.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).first
        return primary == "vi"
    }
}

/// The named model catalog. Revisions were looked up on Hugging Face on 2026-10-18.
public enum ModelCatalog {
    public static let all: [ModelEntry] = asr + diarization + llm + embedding

    public static func entry(id: String) -> ModelEntry? {
        all.first { $0.id == id }
    }

    public static var defaultASR: ModelEntry {
        asr.first { $0.isDefault }!
    }

    public static var defaultDiarizer: ModelEntry {
        diarization.first { $0.isDefault }!
    }

    /// Entries the Models tab lists: diarization always, ASR and LLM entries only when their runtime is
    /// registered (WhisperKit in Phase 4; FluidAudio, Apple Speech, speech-swift in Phase 7; MLX and
    /// Foundation Models in Phase 9).
    public static func visibleEntries(registeredEngineRuntimes: Set<ModelEntry.Runtime>) -> [ModelEntry] {
        all.filter { $0.kind == .diarization || registeredEngineRuntimes.contains($0.runtime) }
    }

    // MARK: - ASR

    private static let whisperKitRepo = "argmaxinc/whisperkit-coreml"
    private static let whisperKitRevision = "0f63a7800b00dd0226abd051b906c246e1907482"
    // WhisperKit 1.1.1 maps every large-v3 build (incl. v20240930 turbo) to the openai/whisper-large-v3 tokenizer.
    private static let largeV3Tokenizer = "openai/whisper-large-v3"
    private static let largeV3TokenizerRevision = "06f233fe06e710322aca913c1bc4249a0d71fce1"

    private static let parakeetLanguages = [
        "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it",
        "lv", "lt", "mt", "pl", "pt", "ro", "sk", "sl", "es", "sv", "ru", "uk",
    ]
    private static let qwen3ASRLanguages = [
        "zh", "en", "yue", "ar", "de", "fr", "es", "pt", "id", "it", "ko", "ru", "th", "vi", "ja",
        "tr", "hi", "ms", "nl", "sv", "da", "fi", "pl", "cs", "fil", "fa", "el", "hu", "mk", "ro",
    ]
    /// FluidAudio `ModelNames.ASR.requiredModelsV3(precision: .int8)` + `vocabularyFile` (v0.17.7).
    private static let parakeetInt8Files = [
        "Preprocessor.mlmodelc", "Encoder.mlmodelc", "Decoder.mlmodelc", "JointDecisionv3.mlmodelc",
        "parakeet_vocab.json",
    ]

    public static let asr: [ModelEntry] = [
        ModelEntry(
            id: "whisperkit-large-v3-turbo-626", kind: .asr, tier: .balanced,
            displayName: "Whisper Large v3 Turbo (626 MB)",
            repo: whisperKitRepo, subfolder: "openai_whisper-large-v3-v20240930_626MB",
            revision: whisperKitRevision,
            tokenizerRepo: largeV3Tokenizer, tokenizerRevision: largeV3TokenizerRevision,
            runtime: .whisperKit, diskMB: 600, ramMB: 1250, ramIsEstimate: true,
            languages: .multilingual,
            qualityNotes: .init(
                en: "Good (turbo, Open ASR avg 8.39%)",
                fr: "Good",
                vi: "Good (FLEURS vi 8.1-9.5%, uncompressed turbo)"),
            license: "MIT", streaming: true, isDefault: true),
        ModelEntry(
            id: "whisperkit-small", kind: .asr, tier: .fast,
            displayName: "Whisper Small (216 MB)",
            repo: whisperKitRepo, subfolder: "openai_whisper-small_216MB",
            revision: whisperKitRevision,
            tokenizerRepo: "openai/whisper-small",
            tokenizerRevision: "973afd24965f72e36ca33b3055d56a652f456b4d",
            runtime: .whisperKit, diskMB: 210, ramMB: 500, ramIsEstimate: true,
            languages: .multilingual,
            qualityNotes: .init(
                en: "Fair (LibriSpeech clean 4.03%)",
                fr: "Fair",
                vi: "Poor (FLEURS vi 22.5%)"),
            license: "MIT", streaming: true),
        ModelEntry(
            id: "whisperkit-large-v3", kind: .asr, tier: .quality,
            displayName: "Whisper Large v3 (947 MB)",
            repo: whisperKitRepo, subfolder: "openai_whisper-large-v3_947MB",
            revision: whisperKitRevision,
            tokenizerRepo: largeV3Tokenizer, tokenizerRevision: largeV3TokenizerRevision,
            runtime: .whisperKit, diskMB: 907, ramMB: 1750, ramIsEstimate: true,
            languages: .multilingual,
            qualityNotes: .init(
                en: "Very good (LibriSpeech clean 2.48%)",
                fr: "Very good",
                vi: "Good (FLEURS vi 8.74%)"),
            license: "MIT", streaming: true),
        ModelEntry(
            id: "whisperkit-turbo-632", kind: .asr, tier: .balanced,
            displayName: "Whisper Large v3 Turbo, streaming (632 MB)",
            repo: whisperKitRepo, subfolder: "openai_whisper-large-v3-v20240930_turbo_632MB",
            revision: whisperKitRevision,
            tokenizerRepo: largeV3Tokenizer, tokenizerRevision: largeV3TokenizerRevision,
            runtime: .whisperKit, diskMB: 618, ramMB: 1250, ramIsEstimate: true,
            languages: .multilingual,
            qualityNotes: .init(
                en: "Good (streaming-optimized turbo, best for live)",
                fr: "Good",
                vi: "Good (FLEURS vi 8.1-9.5%, uncompressed turbo)"),
            license: "MIT", streaming: true),
        // Third-party pack; revision = the 2026-09-30 tokenizer fix (openai/whisper-medium tokenizer bundled).
        ModelEntry(
            id: "phowhisper-medium", kind: .asr, tier: .balanced,
            displayName: "PhoWhisper Medium (Vietnamese only, experimental)",
            repo: "aoiandroid/whisper-vi-phowhisper-medium-whisperkit-coreml-macos",
            revision: "c355aebf105ab2ffef9f9ed98d27d214bf0c9201",
            files: [
                "AudioEncoder.mlmodelc", "MelSpectrogram.mlmodelc", "TextDecoder.mlmodelc",
                "config.json", "generation_config.json", "tokenizer.json", "tokenizer_config.json",
            ],
            runtime: .whisperKit, diskMB: 557, ramMB: 1250, ramIsEstimate: true,
            languages: .codes(["vi"]),
            qualityNotes: .init(
                en: "Not supported (Vietnamese fine-tune)",
                fr: "Not supported (Vietnamese fine-tune)",
                vi: "Experimental (VIVOS 4.97%, CMV-Vi 8.27%; no FLEURS)"),
            license: "BSD-3-Clause", streaming: true, vietnameseOnly: true, windowSeconds: 15),
        // FluidAudio tracks Parakeet and Nemotron at `main`; we pin the HF sha for our download and
        // ModelStore writes no `.fluidaudio-revision` marker for them.
        ModelEntry(
            id: "parakeet-v3", kind: .asr, tier: .fast,
            displayName: "Parakeet TDT 0.6B v3",
            repo: "FluidInference/parakeet-tdt-0.6b-v3-coreml",
            revision: "7dd20fe6b1797d35f5e3307e8b1732d9a178edfe",
            files: parakeetInt8Files,
            runtime: .fluidAudio, diskMB: 461, ramMB: 800, ramIsEstimate: true,
            languages: .codes(parakeetLanguages),
            qualityNotes: .init(
                en: "Very good (Open ASR avg 6.51%, FLEURS en 4.85%)",
                fr: "Good (25 European languages)",
                vi: "Not supported"),
            license: "CC-BY-4.0"),
        ModelEntry(
            id: "parakeet-ultra", kind: .asr, tier: .fast,
            displayName: "Parakeet Ultra",
            repo: "FluidInference/parakeet-ultra-coreml",
            revision: "95eaa59a39d4394f047a4dc5cce480388a60d1b6",
            files: parakeetInt8Files,
            runtime: .fluidAudio, diskMB: 603, ramMB: 900, ramIsEstimate: true,
            languages: .codes(parakeetLanguages),
            qualityNotes: .init(
                en: "Very good (LibriSpeech clean 2.13%)",
                fr: "Good (FLEURS 24-language mean 11.67%)",
                vi: "Not supported"),
            license: "CC-BY-4.0"),
        ModelEntry(
            id: "nemotron-3.5-streaming", kind: .asr, tier: .balanced,
            displayName: "Nemotron 3.5 ASR Streaming (live captions)",
            repo: "FluidInference/Nemotron-3.5-ASR-Streaming-Multilingual-0.6b-CoreML",
            subfolder: "multilingual/2240ms",
            revision: "1a41b75758b0337ff67db7d5408280aaaf23074e",
            files: ["multilingual/2240ms"],
            runtime: .fluidAudio, diskMB: 634, ramMB: 1000, ramIsEstimate: true,
            languages: .multilingual,
            qualityNotes: .init(
                en: "Fair (Open ASR avg 10.51%)",
                fr: "Documented by FluidAudio",
                vi: "Experimental, untested in FluidAudio (FLEURS vi 11.2-14.0%)"),
            license: "OpenMDW-1.1", streaming: true, liveOnly: true),
        ModelEntry(
            id: "apple-speech", kind: .asr, tier: .fast,
            displayName: "Apple Speech (system)",
            repo: nil, revision: nil,
            runtime: .appleSpeech, diskMB: 0, ramMB: 0, ramIsEstimate: false,
            languages: .multilingual,
            qualityNotes: .init(
                en: "Good (earnings22 14.0%)",
                fr: "Supported, unmeasured",
                vi: "Listed, quality unmeasured"),
            license: "Apple (system)", minimumMacOS: 26, streaming: true),
        ModelEntry(
            id: "qwen3-asr-0.6b", kind: .asr, tier: .balanced,
            displayName: "Qwen3-ASR 0.6B (4-bit)",
            repo: "aufklarer/Qwen3-ASR-0.6B-MLX-4bit",
            revision: "bc441bd1e4295c1f42d9879f056049a925b6e013",
            files: [
                "config.json", "model.safetensors", "model.safetensors.index.json",
                "vocab.json", "merges.txt", "tokenizer_config.json",
            ],
            runtime: .speechSwift, diskMB: 680, ramMB: 1000, ramIsEstimate: false,
            languages: .codes(qwen3ASRLanguages),
            qualityNotes: .init(
                en: "Very good (LibriSpeech clean 2.20%, 4-bit)",
                fr: "Supported",
                vi: "Good (FLEURS vi 9.2-9.3% fp16/Q8, 13.0% int4)"),
            license: "Apache-2.0"),
        ModelEntry(
            id: "qwen3-asr-1.7b", kind: .asr, tier: .quality,
            displayName: "Qwen3-ASR 1.7B (4-bit)",
            repo: "aufklarer/Qwen3-ASR-1.7B-MLX-4bit",
            revision: "d4f9fa3bb45489219f0fcbec671c92d9c1a68fb6",
            files: [
                "config.json", "model.safetensors",
                "vocab.json", "merges.txt", "tokenizer_config.json",
            ],
            // 1.92 GB measured on the 5-bit build; this 4-bit build is not measured.
            runtime: .speechSwift, diskMB: 2127, ramMB: 1920, ramIsEstimate: true,
            languages: .codes(qwen3ASRLanguages),
            qualityNotes: .init(
                en: "Excellent (Open ASR avg 5.75%)",
                fr: "Supported",
                vi: "Best measured (FLEURS vi 4.9-6.2%)"),
            license: "Apache-2.0"),
    ]

    // MARK: - Diarization

    public static let diarization: [ModelEntry] = [
        // Revision = FluidAudio 0.17.7's `Repo.diarizer.revision` pin, not HF HEAD.
        // Files = `ModelNames.OfflineDiarizer.requiredModels`.
        ModelEntry(
            id: "pyannote-community-1", kind: .diarization, tier: .balanced,
            displayName: "pyannote Community-1 speaker diarization",
            repo: "FluidInference/speaker-diarization-coreml",
            revision: "df2625ac79a7ac6b65ad868fee6d80f320da4232",
            files: [
                "Segmentation.mlmodelc", "FBank.mlmodelc", "Embedding.mlmodelc", "PldaRho.mlmodelc",
                "plda-parameters.json",
            ],
            runtime: .fluidAudio, diskMB: 21, ramMB: 150, ramIsEstimate: true,
            languages: .multilingual,
            qualityNotes: .init(
                en: "Language-independent (DER 10.6% AMI SDM, 15.1% VoxConverse)",
                fr: "Language-independent",
                vi: "Language-independent"),
            license: "CC-BY-4.0", isDefault: true),
    ]

    // MARK: - LLM

    private static func mlx(
        _ id: String, _ displayName: String, tier: ModelEntry.Tier, repo: String, revision: String,
        files: [String], diskMB: Int, ramMB: Int, vi: String, isDefault: Bool = false
    ) -> ModelEntry {
        ModelEntry(
            id: id, kind: .llm, tier: tier, displayName: displayName,
            repo: repo, revision: revision, files: files,
            runtime: .mlx, diskMB: diskMB, ramMB: ramMB, ramIsEstimate: true,
            languages: .multilingual,
            qualityNotes: .init(en: "Good", fr: "Good", vi: vi),
            // Gemma 4 mlx-community cards say "gemma"; upstream google/gemma-4-* is Apache-2.0.
            license: "Apache-2.0", isDefault: isDefault)
    }

    /// Text-model files only (no README, vision/video processor configs).
    private static func mlxFiles(_ weights: [String], extra: [String] = []) -> [String] {
        ["config.json", "tokenizer.json", "tokenizer_config.json", "chat_template.jinja"] + weights + extra
    }

    public static let llm: [ModelEntry] = [
        ModelEntry(
            id: "apple-foundation-models", kind: .llm, tier: .fast,
            displayName: "Apple Foundation Models (system)",
            repo: nil, revision: nil,
            runtime: .foundationModels, diskMB: 0, ramMB: 0, ramIsEstimate: false,
            languages: .multilingual,
            qualityNotes: .init(en: "Fair (small context)", fr: "Fair", vi: "Fair (macOS 26.1+)"),
            license: "Apple (system)", minimumMacOS: 26),
        mlx("mlx-qwen3.5-2b", "Qwen3.5 2B", tier: .fast,
            repo: "mlx-community/Qwen3.5-2B-MLX-4bit",
            revision: "93760be4f1f69842a46bc13dbdc0f19e291392a3",
            files: mlxFiles(["model.safetensors", "model.safetensors.index.json"]),
            diskMB: 1662, ramMB: 3500, vi: "Fair"),
        mlx("mlx-gemma-4-e2b", "Gemma 4 E2B", tier: .fast,
            repo: "mlx-community/gemma-4-e2b-it-4bit",
            revision: "238767527555cb75a05732a84dff5d6ba0dd6809",
            files: mlxFiles(["model.safetensors", "model.safetensors.index.json"], extra: ["generation_config.json"]),
            diskMB: 3417, ramMB: 5000, vi: "Fair-Good"),
        mlx("mlx-qwen3.5-4b", "Qwen3.5 4B", tier: .balanced,
            repo: "mlx-community/Qwen3.5-4B-MLX-4bit",
            revision: "32f3e8ecf65426fc3306969496342d504bfa13f3",
            files: mlxFiles(["model.safetensors", "model.safetensors.index.json"]),
            diskMB: 2913, ramMB: 5500, vi: "Good", isDefault: true),
        mlx("mlx-gemma-4-e4b", "Gemma 4 E4B", tier: .balanced,
            repo: "mlx-community/gemma-4-e4b-it-4bit",
            revision: "475b9088d29754a3379866cf5aeb6b41acd313c2",
            files: mlxFiles(["model.safetensors", "model.safetensors.index.json"], extra: ["generation_config.json"]),
            diskMB: 4939, ramMB: 7000, vi: "Good"),
        mlx("mlx-qwen3.5-9b", "Qwen3.5 9B", tier: .balanced,
            repo: "mlx-community/Qwen3.5-9B-MLX-4bit",
            revision: "938d8919941c6e7efd3c7150eff7fe9d12afa631",
            files: mlxFiles([
                "model-00001-of-00002.safetensors", "model-00002-of-00002.safetensors",
                "model.safetensors.index.json",
            ]),
            diskMB: 5694, ramMB: 9000, vi: "Good-Very good"),
        mlx("mlx-ministral-3-8b", "Ministral 3 8B", tier: .balanced,
            repo: "mlx-community/Ministral-3-8B-Instruct-2512-4bit",
            revision: "182f003f01daa75f9de0f2c4d379722fd0bc1c61",
            files: mlxFiles([
                "model-00001-of-00002.safetensors", "model-00002-of-00002.safetensors",
                "model.safetensors.index.json",
            ], extra: ["generation_config.json", "tekken.json"]),
            diskMB: 5370, ramMB: 8500, vi: "Fair-Good"),
        mlx("mlx-gemma-4-12b", "Gemma 4 12B (QAT)", tier: .quality,
            repo: "mlx-community/gemma-4-12B-it-qat-4bit",
            revision: "e70c6b3ba0979b3357dcd2f223ad8bde7787a6b6",
            files: mlxFiles([
                "model-00001-of-00003.safetensors", "model-00002-of-00003.safetensors",
                "model-00003-of-00003.safetensors", "model.safetensors.index.json",
            ], extra: ["generation_config.json"]),
            diskMB: 10510, ramMB: 13500, vi: "Very good"),
        mlx("mlx-gpt-oss-20b", "GPT-OSS 20B (MoE)", tier: .quality,
            repo: "mlx-community/gpt-oss-20b-MXFP4-Q8",
            revision: "773a7da77e569019bb0fd17a554b263738d669a3",
            files: mlxFiles([
                "model-00001-of-00003.safetensors", "model-00002-of-00003.safetensors",
                "model-00003-of-00003.safetensors", "model.safetensors.index.json",
            ], extra: ["generation_config.json", "special_tokens_map.json"]),
            diskMB: 11543, ramMB: 14000, vi: "Fair-Good (English-centric)"),
        mlx("mlx-qwen3.6-35b-a3b", "Qwen3.6 35B-A3B (MoE)", tier: .quality,
            repo: "mlx-community/Qwen3.6-35B-A3B-4bit",
            revision: "38740b847e4cb78f352aba30aa41c76e08e6eb46",
            files: mlxFiles([
                "model-00001-of-00004.safetensors", "model-00002-of-00004.safetensors",
                "model-00003-of-00004.safetensors", "model-00004-of-00004.safetensors",
                "model.safetensors.index.json",
            ], extra: ["generation_config.json"]),
            diskMB: 19476, ramMB: 23000, vi: "Very good"),
        mlx("mlx-qwen3.8-27b", "Qwen3.8 27B", tier: .quality,
            repo: "mlx-community/Qwen3.8-27B-4bit",
            revision: "10c35caafbb80f7dc6a7a432cdd11af10a6d4818",
            files: mlxFiles([
                "model-00001-of-00003.safetensors", "model-00002-of-00003.safetensors",
                "model-00003-of-00003.safetensors", "model.safetensors.index.json",
            ], extra: ["generation_config.json"]),
            diskMB: 15330, ramMB: 19500, vi: "Very good"),
    ]

    // MARK: - Embedding

    /// Text embedders for the library index (MLXEmbedders). Revisions looked up on Hugging Face on 2026-10-20.
    private static func embedder(
        _ id: String, _ displayName: String, tier: ModelEntry.Tier, repo: String, revision: String,
        files: [String], diskMB: Int, ramMB: Int, vi: String, license: String, isDefault: Bool = false
    ) -> ModelEntry {
        ModelEntry(
            id: id, kind: .embedding, tier: tier, displayName: displayName,
            repo: repo, revision: revision, files: files,
            runtime: .mlx, diskMB: diskMB, ramMB: ramMB, ramIsEstimate: true,
            languages: .multilingual,
            qualityNotes: .init(en: "Good", fr: "Good", vi: vi),
            license: license, isDefault: isDefault)
    }

    public static let embedding: [ModelEntry] = [
        embedder("mlx-qwen3-embedding-0.6b", "Qwen3 Embedding 0.6B", tier: .balanced,
            repo: "mlx-community/Qwen3-Embedding-0.6B-4bit-DWQ",
            revision: "6c3ae70858513f1a78e9cdca3cae330d9075cd2a",
            files: [
                "config.json", "model.safetensors", "model.safetensors.index.json",
                "tokenizer.json", "tokenizer_config.json", "special_tokens_map.json",
            ],
            diskMB: 331, ramMB: 800, vi: "Good", license: "Apache-2.0", isDefault: true),
        embedder("mlx-multilingual-e5-small", "Multilingual E5 Small", tier: .fast,
            repo: "intfloat/multilingual-e5-small",
            revision: "614241f622f53c4eeff9890bdc4f31cfecc418b3",
            files: [
                "config.json", "model.safetensors", "tokenizer.json", "tokenizer_config.json",
                "special_tokens_map.json", "1_Pooling/config.json",
            ],
            diskMB: 465, ramMB: 600, vi: "Fair-Good", license: "MIT"),
        // BAAI/bge-m3 ships only pytorch_model.bin on main; this is the fp16 safetensors conversion.
        embedder("mlx-bge-m3", "BGE-M3", tier: .quality,
            repo: "mlx-community/bge-m3-mlx-fp16",
            revision: "a37eddded9a6a1273a87fb8b0da0d1cdbd98aeec",
            files: [
                "config.json", "model.safetensors", "model.safetensors.index.json",
                "tokenizer.json", "tokenizer_config.json", "special_tokens_map.json",
            ],
            diskMB: 1099, ramMB: 1500, vi: "Good", license: "MIT"),
    ]
}
