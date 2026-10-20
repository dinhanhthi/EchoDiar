import EchoCore
@testable import EchoTranscribe
import Testing

/// Batch engine routing without models: the default resolution and the unsupported-language fallback.
struct EngineRoutingTests {
    private static func entry(_ id: String) -> ModelEntry { ModelCatalog.entry(id: id)! }

    private static func usable(_ ids: Set<String>) -> (ModelEntry) -> Bool { { ids.contains($0.id) } }

    private static func fallback(
        _ selected: String, markers: [String], installed: Set<String>, supported: [String]? = nil
    ) -> (entry: ModelEntry, engineFallback: String?) {
        let entry = entry(selected)
        return TranscriptionPipeline.languageFallback(
            for: entry, supported: supported ?? TranscriptionPipeline.supportedLanguages(for: entry),
            markerLanguages: markers, language: markers.first ?? "auto", isInstalled: { installed.contains($0) })
    }

    // MARK: - Default resolution (no engine chosen)

    @Test func defaultResolutionFallsBackToAnyInstalledBatchEngine() {
        let resolve = { (ids: Set<String>, language: String) in
            TranscriptionPipeline.defaultEngine(language: language, isUsable: Self.usable(ids))?.id
        }
        #expect(resolve(["parakeet-v3"], "auto") == "parakeet-v3")
        #expect(resolve(["qwen3-asr-0.6b"], "vi") == "qwen3-asr-0.6b")
        #expect(resolve(["parakeet-v3", "whisperkit-small"], "auto") == "whisperkit-small")
        #expect(resolve(["whisperkit-small", "whisperkit-large-v3-turbo-626"], "auto") == "whisperkit-large-v3-turbo-626")
        #expect(resolve(["parakeet-v3", "qwen3-asr-0.6b"], "auto") == "parakeet-v3")
        #expect(resolve(["parakeet-v3", "qwen3-asr-0.6b"], "vi") == "qwen3-asr-0.6b")
        #expect(resolve(["phowhisper-medium"], "en") == nil)
        #expect(resolve(["phowhisper-medium"], "vi") == "phowhisper-medium")
        #expect(resolve(["nemotron-3.5-streaming", "pyannote-community-1"], "auto") == nil)
        #expect(resolve([], "auto") == nil)
    }

    /// The Models tab's default is a preference: used only when usable and supporting the first language.
    @Test func storedDefaultIsUsedOnlyWhenUsableForLanguage() {
        let prefer = { (id: String?, ids: Set<String>, language: String) in
            TranscriptionPipeline.preferredEngineID(id, language: language, isUsable: Self.usable(ids))
        }
        let installed: Set<String> = ["phowhisper-medium", "parakeet-v3"]
        #expect(prefer("phowhisper-medium", installed, "vi") == "phowhisper-medium")
        #expect(prefer("phowhisper-medium", installed, "auto") == nil)
        #expect(prefer("parakeet-v3", installed, "auto") == "parakeet-v3")
        #expect(prefer("parakeet-v3", installed, "vi") == nil)
        #expect(prefer("parakeet-v3", [], "auto") == nil)
        #expect(prefer("nemotron-3.5-streaming", ["nemotron-3.5-streaming"], "auto") == nil)
        #expect(prefer(nil, installed, "auto") == nil)
    }

    // MARK: - Local diarizer

    /// The default diarizer when installed, except a Nemotron pick above its speaker limit with pyannote installed;
    /// else catalog order (cloud ids and nil too).
    @Test func localDiarizerFollowsDefaultThenCatalogOrder() {
        let resolve = { (preferred: String?, hint: Int?, installed: Set<String>) in
            TranscriptionPipeline.localDiarizerID(
                preferred: preferred, speakerCountHint: hint, isInstalled: { installed.contains($0) })
        }
        let pyannote = "pyannote-community-1", c128 = "nemotron-3-diarization-c128"
        let fast128 = "nemotron-3-diarization-fast128"
        #expect(resolve(fast128, nil, [pyannote, fast128]) == fast128)
        #expect(resolve(c128, 10, [pyannote, c128]) == pyannote)
        #expect(resolve(c128, 10, [c128]) == c128)
        #expect(resolve("cloud:openai", nil, [c128, fast128]) == c128)
        #expect(resolve(nil, nil, [pyannote, fast128]) == pyannote)
        #expect(resolve(nil, nil, []) == nil)
    }

    // MARK: - Unsupported marker language

    @Test func unsupportedMarkerLanguageFallsBackToInstalledWhisper() {
        let result = Self.fallback("parakeet-v3", markers: ["vi"], installed: ["whisperkit-large-v3-turbo-626"])
        #expect(result.entry.id == "whisperkit-large-v3-turbo-626")
        #expect(result.engineFallback == "parakeet-v3")

        let smallest = Self.fallback(
            "parakeet-v3", markers: ["en", "ja"], installed: ["whisperkit-large-v3", "whisperkit-small"])
        #expect(smallest.entry.id == "whisperkit-small")
        #expect(smallest.engineFallback == "parakeet-v3")
    }

    @Test func supportedOrUnknownLanguagesKeepSelectedEngine() {
        let installed: Set<String> = ["whisperkit-large-v3-turbo-626"]
        let supported = Self.fallback("parakeet-v3", markers: ["en", "fr"], installed: installed)
        #expect(supported.entry.id == "parakeet-v3")
        #expect(supported.engineFallback == nil)

        let unknown = Self.fallback("parakeet-v3", markers: ["vi"], installed: installed, supported: [])
        #expect(unknown.entry.id == "parakeet-v3")
        #expect(unknown.engineFallback == nil)

        let detect = Self.fallback("parakeet-v3", markers: [], installed: installed)
        #expect(detect.entry.id == "parakeet-v3")

        let noWhisper = Self.fallback("parakeet-v3", markers: ["vi"], installed: [])
        #expect(noWhisper.entry.id == "parakeet-v3")
        #expect(noWhisper.engineFallback == nil)
    }

    /// One engine transcribes the whole job: a stand-in that lacks one of the marker languages is never chosen.
    @Test func standInMustSupportEveryMarkerLanguage() {
        let viOnly = Self.fallback("parakeet-v3", markers: ["vi", "ja"], installed: ["phowhisper-medium"])
        #expect(viOnly.entry.id == "parakeet-v3")
        #expect(viOnly.engineFallback == nil)

        let both = Self.fallback(
            "parakeet-v3", markers: ["vi", "ja"], installed: ["phowhisper-medium", "whisperkit-large-v3-turbo-626"])
        #expect(both.entry.id == "whisperkit-large-v3-turbo-626")
        #expect(both.engineFallback == "parakeet-v3")

        let viOnlyForVietnamese = Self.fallback("parakeet-v3", markers: ["vi"], installed: ["phowhisper-medium"])
        #expect(viOnlyForVietnamese.entry.id == "phowhisper-medium")
        #expect(viOnlyForVietnamese.engineFallback == "parakeet-v3")
    }
}
