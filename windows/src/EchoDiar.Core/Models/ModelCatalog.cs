namespace EchoDiar.Core.Models;

public enum ModelKind { Asr, Diarization, Llm, Embedding }

public enum ModelTier { Fast, Balanced, Quality }

/// <summary>The engine that runs a catalog entry. Phase 24 adds the local LLM and embedder runtimes.</summary>
public enum ModelRuntime { WhisperNet, SherpaOnnx }

/// <summary>Files fetched from a second Hugging Face repo for the same model (a two-repo diarizer).</summary>
/// <param name="Repo">Hugging Face repo id.</param>
/// <param name="Revision">Full HF commit sha the download is pinned to (never <c>main</c>).</param>
/// <param name="Files">Allowlist of paths relative to that repo's root, matched as <see cref="ModelEntry.Files"/>.</param>
public sealed record ModelExtraFiles(string Repo, string Revision, IReadOnlyList<string> Files);

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

    // LLM and embedding: Phase 24 adds the GGUF entries (and their runtimes) here.

    public static readonly IReadOnlyList<ModelEntry> Llm = [];

    public static readonly IReadOnlyList<ModelEntry> Embedding = [];

    // Declared after the lists: static initializers run in text order.
    public static readonly IReadOnlyList<ModelEntry> All = [.. Asr, .. Diarization, .. Llm, .. Embedding];

    public static ModelEntry? Entry(string id) => All.FirstOrDefault(e => e.Id == id);

    public static ModelEntry DefaultASR => Asr.First(e => e.IsDefault);

    public static ModelEntry DefaultDiarizer => Diarization.First(e => e.IsDefault);

    /// <summary>Entries the Models tab lists: diarization always, the others only when their runtime is registered.</summary>
    public static IReadOnlyList<ModelEntry> VisibleEntries(IReadOnlySet<ModelRuntime> registeredEngineRuntimes) =>
        [.. All.Where(e => e.Kind == ModelKind.Diarization || registeredEngineRuntimes.Contains(e.Runtime))];
}
