import AVFoundation
import CoreMedia
import EchoCore
import Foundation
import os
import Speech

/// Apple's system model (macOS 26+): `SpeechAnalyzer` + `SpeechTranscriber`, one analysis per language range.
/// Language assets are reserved and installed through `AssetInventory` before use; nothing goes to `Models/`.
/// It has no audio language ID: Auto uses the default language from settings.
@available(macOS 26, *)
public struct AppleSpeechEngine: TranscriptionEngine {
    /// Share of a channel's progress taken by the asset install.
    static let installShare = 0.1
    /// Locale identifiers this app reserved, most recently used first (picks the one to release at the limit).
    private static let usageKey = "AppleSpeechLocaleUsage"
    private static let languages = OSAllocatedUnfairLock<[String]>(initialState: [])

    public let entry: ModelEntry

    public init(entry: ModelEntry) {
        self.entry = entry
    }

    public static var isAvailable: Bool { PlatformFeatures.speechAnalyzer && SpeechTranscriber.isAvailable }

    /// Language codes from `SpeechTranscriber.supportedLocales`, once `loadSupportedLanguages()` has run; else empty.
    public static var supportedLanguages: [String] { languages.withLock { $0 } }

    @discardableResult
    public static func loadSupportedLanguages() async -> [String] {
        let codes = Set(await SpeechTranscriber.supportedLocales.compactMap { $0.language.languageCode?.identifier })
            .sorted()
        languages.withLock { $0 = codes }
        return codes
    }

    public func transcribe(
        channelURL: URL, languageRanges: [LanguageRange], progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [RawSegment] {
        guard Self.isAvailable else { throw TranscriptionError.engineFailed("Apple Speech is not available on this Mac") }
        let duration = try MonoAudio.duration(of: channelURL)
        let total = max(1, duration)
        let fallback = Self.defaultLanguage()
        let ranges = languageRanges.filter { $0.start < min($0.end ?? duration, duration) }

        var locales: [String: Locale] = [:]
        for code in Set(ranges.map { $0.language ?? fallback }) {
            guard let locale = await Self.locale(for: code) else {
                throw TranscriptionError.engineFailed("Apple Speech does not support \(code)")
            }
            locales[code] = locale
        }
        for (index, locale) in locales.values.enumerated() {
            try Task.checkCancellation()
            let share = Self.installShare / Double(locales.count)
            try await Self.installAssets(for: locale) { progress(share * (Double(index) + $0)) }
        }

        var segments: [RawSegment] = []
        for (index, range) in ranges.enumerated() {
            try Task.checkCancellation()
            guard let locale = locales[range.language ?? fallback] else { continue }
            let end = min(range.end ?? duration, duration)
            let whole = range.start <= 0 && end >= duration
            let url = whole ? channelURL : try Self.excerpt(of: channelURL, from: range.start, to: end, index: index)
            defer { if !whole { try? FileManager.default.removeItem(at: url) } }
            segments += try await Self.analyze(url, locale: locale, offset: whole ? 0 : range.start)
            progress(Self.installShare + (1 - Self.installShare) * min(1, end / total))
        }
        await SpeechModels.endRetention()
        progress(1)
        return segments
    }

    // MARK: - Assets

    /// Reserves the locale for this app and installs its assets when missing; `progress` gets 0...1.
    public static func installAssets(for locale: Locale, progress: @escaping @Sendable (Double) -> Void) async throws {
        try await reserve(locale)
        let transcriber = SpeechTranscriber(locale: locale, preset: .transcription)
        guard let request = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) else {
            return progress(1)
        }
        let observation = request.progress.observe(\.fractionCompleted) { value, _ in progress(value.fractionCompleted) }
        defer { observation.invalidate() }
        try await request.downloadAndInstall()
        progress(1)
    }

    /// BCP-47 identifiers of the locales this app reserved, sorted.
    public static func reservedLocaleIdentifiers() async -> [String] {
        await AssetInventory.reservedLocales.map { $0.identifier(.bcp47) }.sorted()
    }

    /// Releases every locale this app reserved. The system removes the files later (other apps may share them).
    public static func releaseAssets() async {
        for locale in await AssetInventory.reservedLocales {
            await AssetInventory.release(reservedLocale: locale)
        }
        UserDefaults.standard.removeObject(forKey: usageKey)
    }

    /// At `maximumReservedLocales`, the least recently used reserved locale (unknown ones first) is released first.
    static func reserve(_ locale: Locale) async throws {
        let id = locale.identifier(.bcp47)
        let usage = UserDefaults.standard.stringArray(forKey: usageKey) ?? []
        let reserved = await AssetInventory.reservedLocales
        if !reserved.contains(where: { $0.identifier(.bcp47) == id }),
           reserved.count >= AssetInventory.maximumReservedLocales,
           let oldest = reserved.max(by: { rank($0, in: usage) < rank($1, in: usage) }) {
            await AssetInventory.release(reservedLocale: oldest)
        }
        try await AssetInventory.reserve(locale: locale)
        UserDefaults.standard.set([id] + usage.filter { $0 != id }, forKey: usageKey)
    }

    private static func rank(_ locale: Locale, in usage: [String]) -> Int {
        usage.firstIndex(of: locale.identifier(.bcp47)) ?? Int.max
    }

    // MARK: - Analysis

    /// Final results only, one segment each, with times shifted by `offset`.
    private static func analyze(_ url: URL, locale: Locale, offset: Double) async throws -> [RawSegment] {
        let transcriber = SpeechTranscriber(
            locale: locale, transcriptionOptions: [], reportingOptions: [], attributeOptions: [.audioTimeRange])
        let analyzer = SpeechAnalyzer(
            modules: [transcriber], options: .init(priority: .utility, modelRetention: .whileInUse))
        let file = try AVAudioFile(forReading: url)
        let results = transcriber.results
        async let collected = Self.segments(from: results, offset: offset)
        try await withTaskCancellationHandler {
            do {
                if let last = try await analyzer.analyzeSequence(from: file) {
                    try await analyzer.finalizeAndFinish(through: last)
                } else {
                    await analyzer.cancelAndFinishNow()
                }
            } catch {
                // Ends the results stream so `collected` returns and the model gate is released.
                await analyzer.cancelAndFinishNow()
                throw error
            }
        } onCancel: {
            Task { await analyzer.cancelAndFinishNow() }
        }
        return try await collected
    }

    private static func segments<Results: AsyncSequence>(
        from results: Results, offset: Double
    ) async throws -> [RawSegment] where Results.Element == SpeechTranscriber.Result {
        var segments: [RawSegment] = []
        for try await result in results where result.isFinal {
            let text = String(result.text.characters).trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            segments.append(RawSegment(
                start: offset + result.range.start.seconds, end: offset + result.range.end.seconds, text: text))
        }
        return segments
    }

    /// `start...end` seconds of a mono channel file as its own file next to it, copied in 10 min slabs.
    private static func excerpt(of url: URL, from start: Double, to end: Double, index: Int) throws -> URL {
        let destination = url.deletingPathExtension().appendingPathExtension("range\(index).caf")
        guard let format = AVAudioFormat(
            commonFormat: .pcmFormatFloat32, sampleRate: MonoAudio.sampleRate, channels: 1, interleaved: false) else {
            throw TranscriptionError.audioUnreadable(url.lastPathComponent)
        }
        let writer = try AVAudioFile(
            forWriting: destination, settings: format.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        var slabStart = start
        while slabStart < end {
            let samples = try MonoAudio.read(url, from: slabStart, to: min(slabStart + 600, end))
            guard !samples.isEmpty,
                  let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(samples.count)) else {
                break
            }
            buffer.frameLength = AVAudioFrameCount(samples.count)
            buffer.floatChannelData![0].update(from: samples, count: samples.count)
            try writer.write(from: buffer)
            slabStart += 600
        }
        writer.close()
        return destination
    }

    // MARK: - Locales

    /// The settings' default language, or the system language when it is Auto too.
    static func defaultLanguage() -> String {
        let setting = SettingsStore().load().defaultLanguage
        if setting != "auto", !setting.isEmpty { return setting }
        return Locale.current.language.languageCode?.identifier ?? "en"
    }

    /// The supported locale for a BCP-47 code, the same on every call so live and batch use one regional model. A
    /// code with a region gets Apple's equivalent. A bare language (same script) prefers a locale this app reserved,
    /// then an installed one, then the current region, then the language's likely region (`fr` → fr-FR), then the
    /// first by identifier. `supportedLocale(equivalentTo:)` alone returns a random region for a bare language when
    /// none is reserved (measured: fr_BE, fr_CA, fr_CH or fr_FR across launches, spike 2026-10-30).
    static func locale(for code: String) async -> Locale? {
        let requested = Locale(identifier: code)
        if requested.region != nil, let locale = await SpeechTranscriber.supportedLocale(equivalentTo: requested) {
            return locale
        }
        guard let language = requested.language.languageCode else { return nil }
        let likely = Locale.Language(identifier: requested.language.maximalIdentifier)
        let matches = await SpeechTranscriber.supportedLocales
            .filter {
                $0.language.languageCode == language
                    && Locale.Language(identifier: $0.language.maximalIdentifier).script == likely.script
            }
            .sorted { $0.identifier(.bcp47) < $1.identifier(.bcp47) }
        let ids = Set(matches.map { $0.identifier(.bcp47) })
        let reserved = await AssetInventory.reservedLocales.map { $0.identifier(.bcp47) }.sorted()
        let installed = await SpeechTranscriber.installedLocales.map { $0.identifier(.bcp47) }.sorted()
        if let id = reserved.first(where: ids.contains) ?? installed.first(where: ids.contains) {
            return matches.first { $0.identifier(.bcp47) == id }
        }
        let regions = [Locale.current.region, likely.region].compactMap { $0 }
        if let locale = regions.lazy.compactMap({ region in matches.first { $0.region == region } }).first
            ?? matches.first {
            return locale
        }
        return await SpeechTranscriber.supportedLocale(equivalentTo: requested)
    }
}
