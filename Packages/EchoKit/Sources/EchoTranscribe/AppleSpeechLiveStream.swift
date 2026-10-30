import AVFoundation
import CoreMedia
import EchoCore
import Foundation
import os
import Speech

/// One block of a channel for `AppleSpeechLiveStream.run`, or the Pause button. A plain type (no `@available`), so
/// `RecordingTranscriber` can hold the continuations on macOS 15.
enum AppleSpeechLiveEvent: Sendable {
    /// 16 kHz mono samples starting at `startSample` of the file. `language` is the marker's BCP-47 code; nil, empty
    /// or "auto" means the default language (Apple Speech has no audio language ID).
    case audio(samples: [Float], startSample: Int, language: String?)
    /// Finalizes the audio fed so far: the recording paused.
    case finalize
}

/// Streams one channel of a recording through Apple's `SpeechAnalyzer` + `SpeechTranscriber` (macOS 26+) while it is
/// made: volatile results become the channel's tentative text, final results its lines, in file time.
///
/// One analyzer per language: a marker that resolves to another locale finishes the current analyzer and starts a new
/// one at the block where it changed. Each analyzer reserves its locale and installs the assets first (events wait in
/// the stream meanwhile, nothing is dropped). Blocks are converted from 16 kHz Float32 to the analyzer's format and
/// timed by `bufferStartTime` = start sample / 16 000, so every range is file time without a run offset (measured,
/// spike 2026-10-30: a fresh analyzer honours a non-zero first `bufferStartTime`).
///
/// Without help the analyzer finalizes only every 10–20 s of audio, so an energy pause detector calls
/// `finalize(through:)` once per 0.5 s pause, and only when a volatile result came since the last final: finalizing
/// at every pause invents mic lines like "Oui." on noise (measured: phrase end → final 0.65 s median, 0 invented lines,
/// against 11.2 s without finalize and 16 invented lines over 315 s when ungated).
///
/// Speech objects never leave the task that calls `run` (`SpeechTranscriber` is not Sendable); results come back
/// through the callbacks, from another task.
@available(macOS 26, *)
enum AppleSpeechLiveStream {
    /// Volatile and fast results with audio time ranges (measured: `.fastResults` brings the first text from 5.6 s to
    /// 1.0 s median and volatiles from every ~11.5 s to every ~1 s of audio).
    static let preset = SpeechTranscriber.Preset.timeIndexedProgressiveTranscription
    /// The silence after speech that finalizes the channel's results: 0.5 s, the live chunker's pause.
    static let pauseFrames = RecordingTranscriber.liveShortPauseFrames
    static let sampleRate = SpeechChunker.sampleRate

    /// Runs one channel until `events` ends (finalizes the rest, delivering its finals, then returns) or the task is
    /// cancelled (drops the rest and throws `CancellationError`). `onPreparing` runs when a locale's assets must be
    /// downloaded, `onStarted` each time an analyzer started. `onVolatile` gets the non-empty text pending and its start
    /// in file samples, never before the channel's last final; `onFinal` each non-empty final in file seconds. Errors
    /// (an unsupported language, a failed install or analysis) are thrown.
    static func run(
        events: AsyncStream<AppleSpeechLiveEvent>,
        onPreparing: @escaping @Sendable () -> Void,
        onStarted: @escaping @Sendable () -> Void,
        onVolatile: @escaping @Sendable (String, Int) -> Void,
        onFinal: @escaping @Sendable (RawSegment) -> Void
    ) async throws {
        let shared = OSAllocatedUnfairLock(initialState: Shared())
        let callbacks = Callbacks(preparing: onPreparing, volatile: onVolatile, final: onFinal)
        var analysis: Analysis?
        var marker: String?
        let defaultCode = AppleSpeechEngine.defaultLanguage()
        var pauses = PauseDetector()
        var finalizedThisPause = false
        /// The end of the audio fed, in file samples.
        var fedEnd = 0
        do {
            for await event in events {
                if let error = shared.withLock({ $0.error }) { throw error }
                switch event {
                case .finalize:
                    analysis?.finalize(through: fedEnd)
                case .audio(let samples, let startSample, let language):
                    guard !samples.isEmpty else { continue }
                    if analysis == nil || language != marker {
                        marker = language
                        let code = Self.code(language) ?? defaultCode
                        guard let locale = await AppleSpeechEngine.locale(for: code) else {
                            throw TranscriptionError.engineFailed("Apple Speech does not support \(code)")
                        }
                        if locale.identifier(.bcp47) != analysis?.localeID {
                            if let current = analysis {
                                analysis = nil
                                try await current.finish()
                            }
                            analysis = try await Analysis.start(locale: locale, shared: shared, callbacks: callbacks)
                            onStarted()
                        }
                    }
                    try analysis?.feed(samples, startSample: startSample)
                    fedEnd = startSample + samples.count
                    if pauses.append(samples) { finalizedThisPause = false }
                    // Whichever comes second: the 0.5 s pause or a volatile result.
                    if pauses.inPause, !finalizedThisPause, shared.withLock({ $0.volatilePending }) {
                        finalizedThisPause = true
                        analysis?.finalize(through: fedEnd)
                    }
                }
            }
            try Task.checkCancellation()
            if let current = analysis {
                analysis = nil
                try await current.finish()
            }
        } catch {
            await analysis?.cancel()
            await SpeechModels.endRetention()
            throw error
        }
        await SpeechModels.endRetention()
    }

    /// nil for the default language (nil, empty or "auto"), else the trimmed code.
    private static func code(_ language: String?) -> String? {
        let trimmed = language?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return trimmed.isEmpty || trimmed.lowercased() == "auto" ? nil : trimmed
    }

    // MARK: - Analysis

    /// What a channel's results task and its feed share, across analyzers.
    private struct Shared {
        /// A non-empty volatile result arrived since the last final: the pause may finalize.
        var volatilePending = false
        /// The end of the channel's last final, in file samples; a volatile never starts before it.
        var finalEndSample = 0
        /// The analysis failed; the feed throws it at the next event.
        var error: (any Error)?
    }

    private struct Callbacks {
        let preparing: @Sendable () -> Void
        let volatile: @Sendable (String, Int) -> Void
        let final: @Sendable (RawSegment) -> Void
    }

    /// One analyzer for one locale, from its first block to its end.
    private struct Analysis {
        let localeID: String
        let analyzer: SpeechAnalyzer
        let inputs: AsyncStream<AnalyzerInput>.Continuation
        let inputFormat: AVAudioFormat
        let converter: AVAudioConverter
        let results: Task<Void, any Error>
        /// The latest `finalize(through:)`, chained after the previous one and awaited before the analyzer finishes.
        var finalizing: Task<Void, Never>?

        /// Reserves the locale and installs its assets (`preparing` only when something must be downloaded: the
        /// status reads `.supported` until this process reserves the locale, measured), then starts the analyzer.
        static func start(
            locale: Locale, shared: OSAllocatedUnfairLock<Shared>, callbacks: Callbacks
        ) async throws -> Analysis {
            let transcriber = SpeechTranscriber(locale: locale, preset: AppleSpeechLiveStream.preset)
            try await AppleSpeechEngine.reserve(locale)
            if let request = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) {
                callbacks.preparing()
                try await request.downloadAndInstall()
            }
            // 16 kHz mono Int16 (measured), so the converter only changes the sample type.
            let rate = Double(AppleSpeechLiveStream.sampleRate)
            guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]),
                  format.sampleRate == rate,
                  let inputFormat = AVAudioFormat(
                      commonFormat: .pcmFormatFloat32, sampleRate: rate, channels: 1, interleaved: false),
                  let converter = AVAudioConverter(from: inputFormat, to: format) else {
                throw TranscriptionError.engineFailed("Apple Speech has no 16 kHz audio format")
            }
            let analyzer = SpeechAnalyzer(
                modules: [transcriber], options: .init(priority: .utility, modelRetention: .whileInUse))
            try await analyzer.prepareToAnalyze(in: format)
            let stream = transcriber.results
            let results = Task {
                do {
                    try await collect(stream, shared: shared, callbacks: callbacks)
                } catch {
                    shared.withLock { $0.error = error }
                    throw error
                }
            }
            let (input, inputs) = AsyncStream<AnalyzerInput>.makeStream()
            do {
                try await analyzer.start(inputSequence: input)
            } catch {
                inputs.finish()
                await analyzer.cancelAndFinishNow()
                results.cancel()
                throw error
            }
            return Analysis(
                localeID: locale.identifier(.bcp47), analyzer: analyzer, inputs: inputs, inputFormat: inputFormat,
                converter: converter, results: results)
        }

        /// Converts a block and yields it at its file time.
        func feed(_ samples: [Float], startSample: Int) throws {
            let count = AVAudioFrameCount(samples.count)
            guard let input = AVAudioPCMBuffer(pcmFormat: inputFormat, frameCapacity: count),
                  let output = AVAudioPCMBuffer(pcmFormat: converter.outputFormat, frameCapacity: count) else {
                throw TranscriptionError.engineFailed("Apple Speech audio buffer")
            }
            input.frameLength = count
            samples.withUnsafeBufferPointer { input.floatChannelData![0].update(from: $0.baseAddress!, count: $0.count) }
            try converter.convert(to: output, from: input)
            let start = CMTime(value: CMTimeValue(startSample), timescale: CMTimeScale(AppleSpeechLiveStream.sampleRate))
            inputs.yield(AnalyzerInput(buffer: output, bufferStartTime: start))
        }

        /// Finalizes the results through `sample` without blocking the feed (it takes ~0.15 s, measured). A failure
        /// only delays the final: the end of input finalizes that audio again.
        mutating func finalize(through sample: Int) {
            let analyzer = analyzer
            let previous = finalizing
            let time = CMTime(value: CMTimeValue(sample), timescale: CMTimeScale(AppleSpeechLiveStream.sampleRate))
            finalizing = Task {
                await previous?.value
                try? await analyzer.finalize(through: time)
            }
        }

        /// Finalizes everything fed and waits for the last results.
        func finish() async throws {
            inputs.finish()
            await finalizing?.value
            do {
                try await analyzer.finalizeAndFinishThroughEndOfInput()
            } catch {
                await cancel()
                throw error
            }
            try await results.value
        }

        /// Drops what is not finalized and ends the results.
        func cancel() async {
            inputs.finish()
            finalizing?.cancel()
            await analyzer.cancelAndFinishNow()
            results.cancel()
        }

        /// Hands each result to its callback; text via `String(result.text.characters)`, like the batch engine.
        private static func collect<Results: AsyncSequence>(
            _ results: Results, shared: OSAllocatedUnfairLock<Shared>, callbacks: Callbacks
        ) async throws where Results.Element == SpeechTranscriber.Result {
            let rate = Double(AppleSpeechLiveStream.sampleRate)
            for try await result in results {
                let text = String(result.text.characters).trimmingCharacters(in: .whitespacesAndNewlines)
                if result.isFinal {
                    let end = Int((result.range.end.seconds * rate).rounded())
                    shared.withLock {
                        $0.volatilePending = false
                        $0.finalEndSample = max($0.finalEndSample, end)
                    }
                    guard !text.isEmpty else { continue }
                    callbacks.final(RawSegment(start: result.range.start.seconds, end: result.range.end.seconds, text: text))
                } else if !text.isEmpty {
                    // A volatile's range starts at the finalization frontier, leading silence included (measured).
                    let floor = shared.withLock {
                        $0.volatilePending = true
                        return $0.finalEndSample
                    }
                    callbacks.volatile(text, max(Int((result.range.start.seconds * rate).rounded()), floor))
                }
            }
        }
    }

    // MARK: - Pauses

    /// `SpeechChunker`'s energy VAD on 20 ms frames: speech opens after `openFrames` loud frames, and a pause starts
    /// after `pauseFrames` quiet ones.
    private struct PauseDetector {
        /// From a pause after speech until the next speech.
        private(set) var inPause = false
        private var floorDB = SpeechChunker.initialFloorDB
        private var speaking = false
        private var speechRun = 0
        private var silenceRun = 0
        /// The start of a frame not yet complete.
        private var partial: [Float] = []

        /// Feeds the next samples; true when a pause started in them.
        mutating func append(_ samples: [Float]) -> Bool {
            var started = false
            var index = 0
            while index < samples.count {
                let take = min(SpeechChunker.frameSamples - partial.count, samples.count - index)
                partial += samples[index..<index + take]
                index += take
                guard partial.count == SpeechChunker.frameSamples else { continue }
                started = process(partial) || started
                partial.removeAll(keepingCapacity: true)
            }
            return started
        }

        private mutating func process(_ frame: [Float]) -> Bool {
            let db = SpeechChunker.rmsDB(frame)
            let isSpeech = db >= max(floorDB + SpeechChunker.speechAboveFloorDB, SpeechChunker.speechMinDB)
            floorDB = min(db, floorDB + SpeechChunker.floorRiseDBPerSecond / Float(SpeechChunker.framesPerSecond))
            guard !isSpeech else {
                speechRun += 1
                silenceRun = 0
                if !speaking, speechRun >= SpeechChunker.openFrames {
                    speaking = true
                    inPause = false
                }
                return false
            }
            speechRun = 0
            silenceRun += 1
            guard speaking, silenceRun >= AppleSpeechLiveStream.pauseFrames else { return false }
            speaking = false
            inPause = true
            return true
        }
    }
}
