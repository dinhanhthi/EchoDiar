namespace EchoDiar.Core.Models;

public enum ModelKind { Asr, Diarization, Llm, Embedding }

public enum ModelTier { Fast, Balanced, Quality }

/// <summary>The engine that runs a catalog entry.</summary>
public enum ModelRuntime
{
    WhisperNet,
    SherpaOnnx,
    /// <summary>Phi Silica through Windows AI (Copilot+ PCs): part of Windows, nothing to download.</summary>
    WindowsAI,
    /// <summary>A GGUF file run by llama.cpp through LLamaSharp (chat models and text embedders).</summary>
    Gguf,
}

/// <summary>Files fetched from a second Hugging Face repo for the same model (a two-repo diarizer).</summary>
/// <param name="Repo">Hugging Face repo id.</param>
/// <param name="Revision">Full HF commit sha the download is pinned to (never <c>main</c>).</param>
/// <param name="Files">Allowlist of paths relative to that repo's root, matched as <see cref="ModelEntry.Files"/>.</param>
/// <param name="Sha256">Expected SHA-256 per repo path, as <see cref="ModelEntry.Sha256"/>.</param>
public sealed record ModelExtraFiles(
    string Repo, string Revision, IReadOnlyList<string> Files, IReadOnlyDictionary<string, string>? Sha256 = null);

/// <summary>Short quality notes for the three first-class content languages.</summary>
public sealed record ModelQualityNotes(string En, string Fr, string Vi);

/// <summary>One downloadable model in the user-visible catalog (Swift <c>ModelEntry</c>).</summary>
/// <remarks>
/// Windows differences: no WhisperKit tokenizer repo (ggml files embed their tokenizer) and no minimum OS
/// version; sizes are exact byte counts from the HF tree, with <see cref="DiskMB"/> derived from them;
/// <see cref="ExtraFiles"/> holds a second repo's files.
/// </remarks>
public sealed record ModelEntry
{
    /// <summary>Stable catalog id (settings, harness args, <c>Models/&lt;id&gt;/</c> folder name).</summary>
    public required string Id { get; init; }
    public required ModelKind Kind { get; init; }
    public required ModelTier Tier { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Hugging Face repo id; null only for system runtimes.</summary>
    public required string? Repo { get; init; }
    /// <summary>Folder inside the repo that holds the model, relative to the repo root.</summary>
    public string? Subfolder { get; init; }
    /// <summary>Full HF commit sha the download is pinned to (never <c>main</c>); null only for system runtimes.</summary>
    public required string? Revision { get; init; }
    /// <summary>
    /// Allowlist of path prefixes to download, relative to the repo root, matched on whole path components
    /// (<c>model.onnx</c> matches <c>model.onnx</c> or <c>model.onnx/…</c>, not <c>model.onnx.bak</c>).
    /// null = everything under <see cref="Subfolder"/> (or the whole repo).
    /// </summary>
    public IReadOnlyList<string>? Files { get; init; }
    /// <summary>
    /// Expected SHA-256 (lowercase hex) per repo path, relative to the repo root. Required for every file the HF
    /// tree does not list as LFS (no listed hash); when present it is checked instead of the listed LFS oid.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Sha256 { get; init; }
    /// <summary>Files from a second repo, installed into the same <c>Models/&lt;id&gt;/</c> folder.</summary>
    public ModelExtraFiles? ExtraFiles { get; init; }
    public required ModelRuntime Runtime { get; init; }
    /// <summary>Download size in bytes (allowlisted files plus <see cref="ExtraFiles"/>), from the HF tree.</summary>
    public required long DiskBytes { get; init; }
    /// <summary>RAM while running, in MB.</summary>
    public required int RamMB { get; init; }
    public required bool RamIsEstimate { get; init; }
    /// <summary>BCP-47 codes; null = any language (or language-independent, e.g. diarization).</summary>
    public required IReadOnlyList<string>? Languages { get; init; }
    public required ModelQualityNotes QualityNotes { get; init; }
    public required string License { get; init; }
    public bool Streaming { get; init; }
    /// <summary>Catalog default for its kind (one per kind).</summary>
    public bool IsDefault { get; init; }
    /// <summary>Vietnamese-only model: hidden from the engine picker unless the language is <c>vi</c>.</summary>
    public bool VietnameseOnly { get; init; }
    /// <summary>Decode window override in seconds (null = engine default).</summary>
    public int? WindowSeconds { get; init; }

    /// <summary>Download size in MiB, rounded.</summary>
    public int DiskMB => (int)Math.Round(DiskBytes / 1_048_576.0);

    /// <summary>
    /// Whether the engine picker offers this entry for a conversation language ("auto" or a BCP-47 code).
    /// Vietnamese-only models are fixed-language, so "auto" does not select them.
    /// </summary>
    public bool IsSelectable(string language)
    {
        if (!VietnameseOnly) return true;
        var primary = language.ToLowerInvariant().Split('-', '_')[0];
        return primary == "vi";
    }
}

/// <summary>The named Windows model catalog. Sizes and revisions read from the Hugging Face API on 2026-10-22.</summary>
/// <remarks>Ids differ from the macOS catalog; macOS shows an unknown id as raw text and falls back to its default.</remarks>
public static class ModelCatalog
{
    private const string WhisperCppRepo = "ggerganov/whisper.cpp";
    private const string WhisperCppRevision = "5359861c739e955e79d9a303bcbc70fb988958b1";

    private static readonly string[] ParakeetLanguages =
    [
        "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it",
        "lv", "lt", "mt", "pl", "pt", "ro", "sk", "sl", "es", "sv", "ru", "uk",
    ];

    private static ModelEntry Whisper(
        string id, string displayName, ModelTier tier, string file, long diskBytes, int ramMB,
        ModelQualityNotes qualityNotes, bool isDefault = false) => new()
    {
        Id = id, Kind = ModelKind.Asr, Tier = tier, DisplayName = displayName,
        Repo = WhisperCppRepo, Revision = WhisperCppRevision, Files = [file],
        Runtime = ModelRuntime.WhisperNet, DiskBytes = diskBytes, RamMB = ramMB, RamIsEstimate = true,
        Languages = null, QualityNotes = qualityNotes, License = "MIT", Streaming = true, IsDefault = isDefault,
    };

    // ASR

    public static readonly IReadOnlyList<ModelEntry> Asr =
    [
        Whisper("whisper-large-v3-turbo-q5", "Whisper Large v3 Turbo (q5_0, 547 MB)", ModelTier.Balanced,
            "ggml-large-v3-turbo-q5_0.bin", 574_041_195, 1000,
            new("Good (Open ASR avg 8.39%, unquantized turbo)", "Good", "Good (FLEURS vi 8.1-9.5%, unquantized turbo)"),
            isDefault: true),
        Whisper("whisper-large-v3-turbo", "Whisper Large v3 Turbo (1.5 GB)", ModelTier.Balanced,
            "ggml-large-v3-turbo.bin", 1_624_555_275, 2000,
            new("Good (turbo, Open ASR avg 8.39%)", "Good", "Good (FLEURS vi 8.1-9.5%)")),
        Whisper("whisper-large-v3-q5", "Whisper Large v3 (q5_0, 1 GB)", ModelTier.Quality,
            "ggml-large-v3-q5_0.bin", 1_081_140_203, 1700,
            new("Very good (LibriSpeech clean 2.48%, unquantized model)", "Very good", "Good (FLEURS vi 8.74%, unquantized model)")),
        Whisper("whisper-small", "Whisper Small (465 MB)", ModelTier.Fast,
            "ggml-small.bin", 487_601_967, 852,
            new("Fair (LibriSpeech clean 4.03%)", "Fair", "Poor (FLEURS vi 22.5%)")),
        Whisper("whisper-base", "Whisper Base (141 MB)", ModelTier.Fast,
            "ggml-base.bin", 147_951_465, 388,
            new("Fair (fastest, least accurate)", "Fair", "Poor")),
        // Third-party ggml conversion of vinai/PhoWhisper-medium (the repo card says Apache-2.0).
        new()
        {
            Id = "phowhisper-medium", Kind = ModelKind.Asr, Tier = ModelTier.Balanced,
            DisplayName = "PhoWhisper Medium (Vietnamese only, experimental)",
            Repo = "dongxiat/ggml-PhoWhisper-medium", Revision = "053b49372bcb9c587ec360f49b09687685ebb7db",
            Files = ["ggml-PhoWhisper-medium.bin"],
            Runtime = ModelRuntime.WhisperNet, DiskBytes = 1_533_763_076, RamMB = 2100, RamIsEstimate = true,
            Languages = ["vi"],
            QualityNotes = new(
                "Not supported (Vietnamese fine-tune)",
                "Not supported (Vietnamese fine-tune)",
                "Experimental (VIVOS 4.97%, CMV-Vi 8.27%; no FLEURS)"),
            License = "Apache-2.0", Streaming = true, VietnameseOnly = true, WindowSeconds = 15,
        },
        new()
        {
            Id = "parakeet-v3-int8", Kind = ModelKind.Asr, Tier = ModelTier.Fast,
            DisplayName = "Parakeet TDT 0.6B v3 (int8)",
            Repo = "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
            Revision = "2bda32ec70b097a55adaa07d9a7173915b43cc78",
            Files = ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"],
            // tokens.txt is a plain git blob (no LFS oid in the tree); its hash was read at this revision.
            Sha256 = new Dictionary<string, string>
            {
                ["tokens.txt"] = "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d",
            },
            // 652_184_281 + 11_845_275 + 6_355_277 + 93_939
            Runtime = ModelRuntime.SherpaOnnx, DiskBytes = 670_478_772, RamMB = 1000, RamIsEstimate = true,
            Languages = ParakeetLanguages,
            QualityNotes = new(
                "Very good (Open ASR avg 6.51%, FLEURS en 4.85%)",
                "Good (25 European languages)",
                "Not supported"),
            License = "CC-BY-4.0",
        },
    ];

    // Diarization

    public static readonly IReadOnlyList<ModelEntry> Diarization =
    [
        // sherpa-onnx OfflineSpeakerDiarization: pyannote segmentation 3.0 plus a 3D-Speaker CAM++ embedding.
        new()
        {
            Id = "sherpa-pyannote-3", Kind = ModelKind.Diarization, Tier = ModelTier.Balanced,
            DisplayName = "pyannote 3.0 speaker diarization",
            Repo = "csukuangfj/sherpa-onnx-pyannote-segmentation-3-0",
            Revision = "9403a6902bb58e3d5ae8c7e77c3422de279db2e0",
            Files = ["model.onnx"],
            ExtraFiles = new(
                "csukuangfj/speaker-embedding-models", "0743f301363dec56491a490f6d6cbc9d67f9a3bf",
                ["3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"]),
            // 5_992_913 + 28_281_164
            Runtime = ModelRuntime.SherpaOnnx, DiskBytes = 34_274_077, RamMB = 150, RamIsEstimate = true,
            Languages = null,
            QualityNotes = new("Language-independent", "Language-independent", "Language-independent"),
            License = "MIT (segmentation), Apache-2.0 (embedding)", IsDefault = true,
        },
    ];

    // LLM

    // Swift mlx(...): the Q4_K_M file of the unsloth GGUF repo (Swift: the mlx-community 4-bit repo). Sizes,
    // revisions and hashes read from the Hugging Face API on 2026-10-23. RAM = the file plus the headroom Swift's
    // MLX entries allow over their weights (KV cache and compute buffers).
    // Gemma 4 cards say apache-2.0 (license_link: the Gemma 4 license page), as Swift's comment notes.
    private static ModelEntry Gguf(
        string id, string displayName, ModelTier tier, string repo, string revision, string file, long diskBytes,
        string sha256, int ramMB, string vi, bool isDefault = false) => new()
    {
        Id = id, Kind = ModelKind.Llm, Tier = tier, DisplayName = displayName,
        Repo = repo, Revision = revision, Files = [file],
        Sha256 = new Dictionary<string, string> { [file] = sha256 },
        Runtime = ModelRuntime.Gguf, DiskBytes = diskBytes, RamMB = ramMB, RamIsEstimate = true,
        Languages = null, QualityNotes = new("Good", "Good", vi), License = "Apache-2.0", IsDefault = isDefault,
    };

    public static readonly IReadOnlyList<ModelEntry> Llm =
    [
        // Swift apple-foundation-models: the system model, no download (Repo and Revision null).
        new()
        {
            Id = "windows-phi-silica", Kind = ModelKind.Llm, Tier = ModelTier.Fast,
            DisplayName = "Windows AI Phi Silica (system)",
            Repo = null, Revision = null,
            Runtime = ModelRuntime.WindowsAI, DiskBytes = 0, RamMB = 0, RamIsEstimate = false,
            Languages = null,
            QualityNotes = new("Fair (small context, Copilot+ PCs)", "Unverified", "Unverified"),
            License = "Microsoft (system)",
        },
        Gguf("gguf-qwen3.5-2b", "Qwen3.5 2B", ModelTier.Fast,
            "unsloth/Qwen3.5-2B-GGUF", "f6d5376be1edb4d416d56da11e5397a961aca8ae",
            "Qwen3.5-2B-Q4_K_M.gguf", 1_280_835_840,
            "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223", 3100, "Fair"),
        Gguf("gguf-gemma-4-e2b", "Gemma 4 E2B", ModelTier.Fast,
            "unsloth/gemma-4-E2B-it-GGUF", "0314792d7f1f7e229411f620751375812bb9faf2",
            "gemma-4-E2B-it-Q4_K_M.gguf", 3_106_738_272,
            "740185b21d22ceb83a11c3aa62ad5842ef32c70f6096d756bbee85a1e4ec34b8", 4500, "Fair-Good"),
        Gguf("gguf-qwen3.5-4b", "Qwen3.5 4B", ModelTier.Balanced,
            "unsloth/Qwen3.5-4B-GGUF", "e87f176479d0855a907a41277aca2f8ee7a09523",
            "Qwen3.5-4B-Q4_K_M.gguf", 2_740_937_888,
            "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4", 5200, "Good", isDefault: true),
        Gguf("gguf-gemma-4-e4b", "Gemma 4 E4B", ModelTier.Balanced,
            "unsloth/gemma-4-E4B-it-GGUF", "bfc15c382204943c3a8fff0c750b94ae2364d7a3",
            "gemma-4-E4B-it-Q4_K_M.gguf", 4_977_171_584,
            "85a896a047553e842f25297ee5b031d64ff30147d9c4af17b1e4b394cd1fab87", 6800, "Good"),
        Gguf("gguf-qwen3.5-9b", "Qwen3.5 9B", ModelTier.Balanced,
            "unsloth/Qwen3.5-9B-GGUF", "3885219b6810b007914f3a7950a8d1b469d598a5",
            "Qwen3.5-9B-Q4_K_M.gguf", 5_680_522_464,
            "03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8", 8700, "Good-Very good"),
    ];

    // Embedding

    /// <summary>
    /// Text embedders for the library index. As in Swift, <c>api:&lt;provider id&gt;</c> (an enabled provider's
    /// embeddings endpoint) is not a catalog entry: it is an <c>embeddingModelID</c> value the app resolves.
    /// </summary>
    public static readonly IReadOnlyList<ModelEntry> Embedding =
    [
        // RAM: about 1.2 GB measured while loaded (16 sequences of 512 tokens, task 25.1).
        new()
        {
            Id = "gguf-qwen3-embedding-0.6b", Kind = ModelKind.Embedding, Tier = ModelTier.Balanced,
            DisplayName = "Qwen3 Embedding 0.6B",
            Repo = "Qwen/Qwen3-Embedding-0.6B-GGUF", Revision = "370f27d7550e0def9b39c1f16d3fbaa13aa67728",
            Files = ["Qwen3-Embedding-0.6B-Q8_0.gguf"],
            Sha256 = new Dictionary<string, string>
            {
                ["Qwen3-Embedding-0.6B-Q8_0.gguf"] = "06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439",
            },
            Runtime = ModelRuntime.Gguf, DiskBytes = 639_150_592, RamMB = 1200, RamIsEstimate = true,
            Languages = null, QualityNotes = new("Good", "Good", "Good"), License = "Apache-2.0", IsDefault = true,
        },
    ];

    /// <summary><c>embeddingModelID</c> prefix of an API embedder: <c>api:&lt;provider id&gt;</c> (Swift <c>apiEmbedderPrefix</c>).</summary>
    public const string ApiEmbedderPrefix = "api:";

    // Declared after the lists: static initializers run in text order.
    public static readonly IReadOnlyList<ModelEntry> All = [.. Asr, .. Diarization, .. Llm, .. Embedding];

    public static ModelEntry? Entry(string id) => All.FirstOrDefault(e => e.Id == id);

    public static ModelEntry DefaultASR => Asr.First(e => e.IsDefault);

    public static ModelEntry DefaultDiarizer => Diarization.First(e => e.IsDefault);

    /// <summary>Entries the Models tab lists: diarization always, the others only when their runtime is registered.</summary>
    public static IReadOnlyList<ModelEntry> VisibleEntries(IReadOnlySet<ModelRuntime> registeredEngineRuntimes) =>
        [.. All.Where(e => e.Kind == ModelKind.Diarization || registeredEngineRuntimes.Contains(e.Runtime))];
}
