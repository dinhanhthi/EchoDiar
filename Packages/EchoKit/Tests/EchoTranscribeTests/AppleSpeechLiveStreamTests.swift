import AVFoundation
import EchoCore
@testable import EchoTranscribe
import Foundation
import os
import Speech
import Testing

/// Bug regression (Stop hung, gate 2026-10-30): `finalize(through:)` returns only once more audio comes, so nothing may
/// await it before the end of input. Speech from `say`, then 1 s of silence so the pause detector finalizes too. Runs
/// only where Apple Speech is available with an English locale installed (never downloads); each wait races
/// `timeout`, so a hang fails instead of blocking.
struct AppleSpeechLiveStreamTests {
    static let timeout = 30.0
    static let rate = SpeechChunker.sampleRate

    /// Pause then Stop: `flush()`, then `finish()` returns nil (display only) within the timeout.
    @Test func finishReturnsAfterAPause() async throws {
        guard #available(macOS 26, *), let speech = await Self.speech(),
              let entry = ModelCatalog.entry(id: "apple-speech") else { return }
        let transcriber = RecordingTranscriber(
            entry: entry, folder: FileManager.default.temporaryDirectory, gate: ModelLoadGate(),
            deferWhenConstrained: false, language: { "en" })
        let seen = OSAllocatedUnfairLock(initialState: (listening: false, text: false))
        let watcher = Task {
            for await snapshot in transcriber.updates {
                seen.withLock {
                    if snapshot.state == .listening { $0.listening = true }
                    if !snapshot.tentative.isEmpty || !snapshot.lines.isEmpty { $0.text = true }
                }
            }
        }
        defer { watcher.cancel() }
        await transcriber.start()
        Self.append(speech, to: transcriber)
        // Before the runner started, `finish()` would return without touching the analyzer.
        #expect(await Self.wait(until: { seen.withLock { $0.listening } }))
        _ = await Self.wait(10, until: { seen.withLock { $0.text } })
        Self.append([Float](repeating: 0, count: Self.rate), to: transcriber)
        await transcriber.flush()

        let prior = await Self.within(Self.timeout) { await transcriber.finish() == nil }
        #expect(prior == true)
    }

    /// Pause, Resume, then the stream ends: the analyzer restarted after the pause gives a final at the resumed file
    /// time (seconds, not samples), and `run` returns within the timeout.
    @Test func runReturnsWhenTheStreamEndsAfterAFinalize() async throws {
        guard #available(macOS 26, *), let speech = await Self.speech() else { return }
        let (events, feed) = AsyncStream<AppleSpeechLiveEvent>.makeStream()
        let text = OSAllocatedUnfairLock(initialState: false)
        let volatileStart = OSAllocatedUnfairLock(initialState: 0)
        let finals = OSAllocatedUnfairLock(initialState: [RawSegment]())
        let run = Task {
            try await AppleSpeechLiveStream.run(
                events: events, onPreparing: {}, onStarted: {},
                onVolatile: { _, start in
                    text.withLock { $0 = true }
                    volatileStart.withLock { $0 = max($0, start) }
                },
                onFinal: { segment in
                    text.withLock { $0 = true }
                    finals.withLock { $0.append(segment) }
                })
        }
        let silence = [Float](repeating: 0, count: Self.rate)
        feed.yield(.audio(samples: speech, startSample: 0, language: "en"))
        _ = await Self.wait(10, until: { text.withLock { $0 } })
        feed.yield(.audio(samples: silence, startSample: speech.count, language: "en"))
        feed.yield(.finalize)
        // Resume: a new analyzer starts at this file time; the silence after its first volatile finalizes it.
        let resume = speech.count + Self.rate
        feed.yield(.audio(samples: speech, startSample: resume, language: "en"))
        _ = await Self.wait(10, until: { volatileStart.withLock { $0 } >= resume })
        feed.yield(.audio(samples: silence, startSample: resume + speech.count, language: "en"))
        // Finals are in seconds: back to samples, rounded like the stream.
        #expect(await Self.wait(until: {
            finals.withLock { $0.contains { Int(($0.start * Double(Self.rate)).rounded()) >= resume } }
        }))
        feed.finish()

        let returned = await Self.within(Self.timeout) { (try? await run.value) != nil }
        #expect(returned == true)
    }

    // MARK: - Helpers

    /// `say` in 16 kHz mono, nil unless Apple Speech can run in English here without a download.
    @available(macOS 26, *)
    private static func speech() async -> [Float]? {
        guard AppleSpeechEngine.isAvailable,
              await SpeechTranscriber.installedLocales.contains(where: { $0.language.languageCode == .english })
        else { return nil }
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("apple-live-\(UUID().uuidString).wav")
        defer { try? FileManager.default.removeItem(at: url) }
        let say = Process()
        say.executableURL = URL(fileURLWithPath: "/usr/bin/say")
        say.arguments = ["-o", url.path, "--data-format=LEI16@\(rate)",
                         "Did we fix the crash on older machines? Yes, the patch went out yesterday."]
        guard (try? say.run()) != nil else { return nil }
        say.waitUntilExit()
        guard say.terminationStatus == 0,
              let file = try? AVAudioFile(forReading: url, commonFormat: .pcmFormatFloat32, interleaved: false),
              let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat,
                                            frameCapacity: AVAudioFrameCount(file.length)),
              (try? file.read(into: buffer)) != nil, let channel = buffer.floatChannelData?[0]
        else { return nil }
        return Array(UnsafeBufferPointer(start: channel, count: Int(buffer.frameLength)))
    }

    /// `samples` on the mic, silence on the system channel, in 100 ms blocks like the writer.
    private static func append(_ samples: [Float], to transcriber: RecordingTranscriber) {
        let block = rate / 10
        for start in stride(from: 0, to: samples.count, by: block) {
            let frames = min(block, samples.count - start)
            var interleaved = [Int16](repeating: 0, count: 2 * frames)
            for frame in 0..<frames {
                interleaved[2 * frame + RecordingSession.Channel.mic] =
                    Int16(max(-1, min(1, samples[start + frame])) * Float(Int16.max))
            }
            interleaved.withUnsafeBufferPointer {
                transcriber.append(interleaved: $0.baseAddress!, frames: frames, channels: 2)
            }
        }
    }

    /// Polls `condition` every 50 ms; false after `seconds`.
    private static func wait(_ seconds: Double = timeout, until condition: @escaping @Sendable () -> Bool) async -> Bool {
        let deadline = ContinuousClock.now + .seconds(seconds)
        while !condition() {
            guard ContinuousClock.now < deadline else { return false }
            try? await Task.sleep(for: .milliseconds(50))
        }
        return true
    }

    /// `operation`'s value, or nil when it takes longer than `seconds` (it keeps running, unawaited).
    private static func within<T: Sendable>(_ seconds: Double, _ operation: @escaping @Sendable () async -> T) async
        -> T?
    {
        let first = OSAllocatedUnfairLock(initialState: true)
        let take: @Sendable () -> Bool = { first.withLock { value in defer { value = false }; return value } }
        return await withCheckedContinuation { continuation in
            Task {
                let value = await operation()
                if take() { continuation.resume(returning: value) }
            }
            Task {
                try? await Task.sleep(for: .seconds(seconds))
                if take() { continuation.resume(returning: nil) }
            }
        }
    }
}
