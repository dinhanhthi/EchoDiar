import EchoCore
import FluidAudio
import Foundation

/// Live-only Nemotron 3.5 ASR streaming (FluidAudio CoreML) from `Models/nemotron-3.5-streaming/multilingual/2240ms/`.
/// The weights are loaded once and shared by one streaming manager per channel, each with its own encoder cache,
/// so context carries over from one window to the next. Vietnamese is experimental.
public struct NemotronStreamingEngine: Sendable {
    public static let entryID = "nemotron-3.5-streaming"
    /// Streaming chunks per live window: 2 × 2240 ms.
    static let chunksPerWindow = 2

    private let managers: [StreamingNemotronMultilingualAsrManager]
    private let promptDictionary: [String: Int]
    private let defaultPromptID: Int
    /// Samples per live window, an exact multiple of the model's chunk, so `finish()` never pads.
    let windowSamples: Int

    /// Loads from the leaf folder (`ModelStore.folder(for:)`) without ModelHub, on the Neural Engine.
    static func load(folder: URL, channels: Int) async throws -> NemotronStreamingEngine {
        let shared = try await StreamingNemotronMultilingualAsrManager.preloadShared(from: folder)
        var managers: [StreamingNemotronMultilingualAsrManager] = []
        for _ in 0..<channels {
            let manager = StreamingNemotronMultilingualAsrManager()
            try await manager.loadFromShared(shared)
            managers.append(manager)
        }
        return NemotronStreamingEngine(
            managers: managers, promptDictionary: shared.config.promptDictionary,
            defaultPromptID: shared.config.defaultPromptId,
            windowSamples: shared.config.chunkSamples * chunksPerWindow)
    }

    /// Feeds one window of 16 kHz samples to the channel's stream and returns the text decoded from it.
    /// `language` ("auto", a BCP-47 code or nil) applies from this window on, without reloading.
    func transcribe(_ samples: [Float], channel: Int, language: String?) async throws -> String {
        let manager = managers[channel]
        await manager.setPromptId(promptID(for: language))
        _ = try await manager.process(samples: samples)
        return try await manager.finish().trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func unload() async {
        for manager in managers { await manager.cleanup() }
    }

    /// The prompt for a code: exact, else the first region of its language ("vi" → "vi-VN"), else auto.
    private func promptID(for language: String?) -> Int {
        let auto = promptDictionary["auto"] ?? defaultPromptID
        guard let language, !language.isEmpty, language.lowercased() != "auto" else { return auto }
        if let id = promptDictionary[language] { return id }
        let primary = WhisperKitEngine.whisperLanguage(language)
        if let id = promptDictionary[primary] { return id }
        return promptDictionary.filter { $0.key.lowercased().hasPrefix(primary + "-") }
            .min { $0.key < $1.key }?.value ?? auto
    }
}
