import Accelerate
import EchoCore
import Foundation
import Synchronization
import WhisperKit

/// Opt-in live captions while recording, in memory only: the batch transcript after Stop stays the source of truth.
/// Takes the writer's 16 kHz blocks and decodes ~10 s windows per channel (mic lines "Me", system lines "Others")
/// with one WhisperKit model, loaded by `start()` and unloaded by `stop()`.
///
/// Samples wait in a buffer capped at one window, so when decoding falls behind the oldest audio is dropped.
/// The model lives only inside the decode task, never in actor state (`WhisperKit` is not Sendable).
public actor LiveTranscriber {
    public enum State: Sendable, Equatable {
        case loading, listening
        /// No WhisperKit model is installed; nothing is loaded.
        case noModel
        case failed(String)
    }

    public enum Speaker: String, Sendable {
        case me = "Me", others = "Others"
    }

    public struct Line: Sendable, Equatable, Identifiable {
        public let id: Int
        public let speaker: Speaker
        public let text: String
    }

    public struct Snapshot: Sendable, Equatable {
        public var state: State
        /// Oldest first, at most `maxLines`.
        public var lines: [Line]

        public init(state: State, lines: [Line] = []) {
            self.state = state
            self.lines = lines
        }
    }

    /// The live model when installed; else the smallest installed multilingual WhisperKit entry.
    public static let preferredModelID = "whisperkit-turbo-632"
    static let maxLines = 200
    static let defaultWindowSeconds = 10
    /// The end of a window is decoded again at the start of the next, so a word cut at the boundary is not lost.
    static let overlapSeconds = 1

    /// Every change, for a single consumer; finished by `stop()`.
    public nonisolated let updates: AsyncStream<Snapshot>
    private nonisolated let updatesContinuation: AsyncStream<Snapshot>.Continuation
    /// Filled by `append` on the writer queue, emptied by the decode task.
    private nonisolated let buffer = Mutex(Buffer())
    /// At most one pending wake-up: a full window is waiting.
    private nonisolated let wake: AsyncStream<Void>
    private nonisolated let wakeContinuation: AsyncStream<Void>.Continuation
    private nonisolated let modelStore: ModelStore
    private nonisolated let language: @Sendable () -> String?
    private var snapshot = Snapshot(state: .loading)
    private var nextLineID = 0
    private var decoder: Task<Void, Never>?

    /// `language` gives the current marker language ("auto", a BCP-47 code, or nil = detect), read per window.
    public init(modelStore: ModelStore, language: @escaping @Sendable () -> String?) {
        self.modelStore = modelStore
        self.language = language
        (updates, updatesContinuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(1))
        (wake, wakeContinuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(1))
    }

    /// Loads the model at `.utility` QoS and starts decoding; publishes `noModel` instead when none is installed.
    public func start() {
        guard decoder == nil, !buffer.withLock({ $0.closed }) else { return }
        guard let entry = Self.model(in: modelStore), let folder = modelStore.folder(for: entry.id) else {
            buffer.withLock { $0.close() }
            publish(.noModel)
            return
        }
        let windowSamples = (entry.windowSeconds ?? Self.defaultWindowSeconds) * WhisperKit.sampleRate
        buffer.withLock { $0.windowSamples = windowSamples }
        publish(.loading)
        decoder = Task.detached(priority: .utility) { [self] in await decode(entry: entry, folder: folder) }
    }

    /// Stops decoding and returns once the model is unloaded.
    public func stop() async {
        buffer.withLock { $0.close() }
        wakeContinuation.finish()
        decoder?.cancel()
        await decoder?.value
        decoder = nil
        updatesContinuation.finish()
    }

    /// `RecordingEngine.liveSink`: copies the writer's interleaved Int16 block (see `RecordingSession.Channel`)
    /// on the writer queue, never on an audio IO thread.
    public nonisolated func append(interleaved samples: UnsafePointer<Int16>, frames: Int, channels: Int) {
        guard frames > 0, channels > max(RecordingSession.Channel.mic, RecordingSession.Channel.system) else { return }
        let mic = Self.channel(RecordingSession.Channel.mic, of: samples, frames: frames, channels: channels)
        let system = Self.channel(RecordingSession.Channel.system, of: samples, frames: frames, channels: channels)
        if buffer.withLock({ $0.append(mic: mic, system: system) }) { wakeContinuation.yield() }
    }

    // MARK: - Decode task

    /// Owns the model from load to unload.
    private nonisolated func decode(entry: ModelEntry, folder: URL) async {
        let whisper: WhisperKit
        do {
            whisper = try await WhisperKitEngine.load(entry: entry, folder: folder)
        } catch {
            buffer.withLock { $0.close() }
            if !Task.isCancelled { await publish(.failed(error.localizedDescription)) }
            return
        }
        await publish(.listening)
        let overlap = Self.overlapSeconds * WhisperKit.sampleRate
        for await _ in wake {
            while let window = buffer.withLock({ $0.take(keeping: overlap) }) {
                let lines = await transcribe(window, with: whisper)
                if !lines.isEmpty { await add(lines) }
            }
        }
        await whisper.unloadModels()
    }

    /// Speech of each non-silent channel, in start order across both.
    private nonisolated func transcribe(_ window: Window, with whisper: WhisperKit) async -> [(Speaker, String)] {
        let code = language().flatMap { $0.isEmpty || $0.lowercased() == "auto" ? nil : WhisperKitEngine.whisperLanguage($0) }
        let options = DecodingOptions(
            language: code, detectLanguage: code == nil, skipSpecialTokens: true, wordTimestamps: false)
        var found: [(start: Float, speaker: Speaker, text: String)] = []
        for (speaker, samples) in [(Speaker.me, window.mic), (.others, window.system)]
        where Self.peak(samples) >= TranscriptionPipeline.silencePeak {
            guard let results = try? await whisper.transcribe(audioArray: samples, decodeOptions: options) else {
                continue
            }
            for segment in results.flatMap(\.segments) {
                let text = WhisperKitEngine.speechText(segment.text)
                if !text.isEmpty { found.append((segment.start, speaker, text)) }
            }
        }
        return found.sorted { $0.start < $1.start }.map { ($0.speaker, $0.text) }
    }

    // MARK: - State

    private func publish(_ state: State) {
        snapshot.state = state
        updatesContinuation.yield(snapshot)
    }

    private func add(_ lines: [(Speaker, String)]) {
        for (speaker, text) in lines {
            snapshot.lines.append(Line(id: nextLineID, speaker: speaker, text: text))
            nextLineID += 1
        }
        snapshot.lines.removeFirst(max(0, snapshot.lines.count - Self.maxLines))
        updatesContinuation.yield(snapshot)
    }

    // MARK: - Helpers

    /// The preferred model if installed, else the smallest installed WhisperKit entry usable in any language.
    static func model(in store: ModelStore) -> ModelEntry? {
        if let preferred = ModelCatalog.entry(id: preferredModelID), store.isInstalled(preferred.id) { return preferred }
        return ModelCatalog.asr
            .filter { $0.runtime == .whisperKit && !$0.vietnameseOnly && !$0.liveOnly && store.isInstalled($0.id) }
            .min { $0.diskMB < $1.diskMB }
    }

    private static func channel(_ index: Int, of samples: UnsafePointer<Int16>, frames: Int, channels: Int) -> [Float] {
        [Float](unsafeUninitializedCapacity: frames) { output, count in
            var scale: Float = 1 / 32_768
            vDSP_vflt16(samples + index, vDSP_Stride(channels), output.baseAddress!, 1, vDSP_Length(frames))
            vDSP_vsmul(output.baseAddress!, 1, &scale, output.baseAddress!, 1, vDSP_Length(frames))
            count = frames
        }
    }

    private static func peak(_ samples: [Float]) -> Float {
        var peak: Float = 0
        vDSP_maxmgv(samples, 1, &peak, vDSP_Length(samples.count))
        return peak
    }

    private struct Window {
        var mic: [Float]
        var system: [Float]
    }

    /// Both channels grow in step (the writer writes equal frames per channel).
    private struct Buffer {
        var mic: [Float] = []
        var system: [Float] = []
        var windowSamples = LiveTranscriber.defaultWindowSeconds * WhisperKit.sampleRate
        var closed = false

        /// Keeps only the newest window; true when a full window waits.
        mutating func append(mic newMic: [Float], system newSystem: [Float]) -> Bool {
            guard !closed else { return false }
            mic += newMic
            system += newSystem
            mic.removeFirst(max(0, mic.count - windowSamples))
            system.removeFirst(max(0, system.count - windowSamples))
            return mic.count >= windowSamples
        }

        /// The full window, leaving its last `overlap` samples for the next one.
        mutating func take(keeping overlap: Int) -> Window? {
            guard !closed, mic.count >= windowSamples else { return nil }
            let window = Window(mic: mic, system: system)
            mic = Array(mic.suffix(overlap))
            system = Array(system.suffix(overlap))
            return window
        }

        mutating func close() {
            closed = true
            mic = []
            system = []
        }
    }
}
