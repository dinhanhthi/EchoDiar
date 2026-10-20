import Accelerate
import EchoCore
import Foundation
import Synchronization
import WhisperKit

/// Opt-in live captions while recording, in memory only: the batch transcript after Stop stays the source of truth.
/// Takes the writer's 16 kHz blocks and decodes windows per channel (mic lines "Me", system lines "Others") with
/// one model, loaded by `start()` (once the `ModelLoadGate` is free) and unloaded by `stop()`: a WhisperKit entry
/// (~10 s windows) or the Nemotron streaming entry (4.48 s windows fed to one stream per channel).
///
/// Samples wait in a buffer capped at one window, so when decoding falls behind the oldest audio is dropped.
/// The model lives only inside the decode task, never in actor state (`WhisperKit` is not Sendable).
public actor LiveTranscriber {
    public enum State: Sendable, Equatable {
        case loading, listening
        /// No live model is installed; nothing is loaded.
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
    public static let preferredModelID = AppSettings.defaultLiveTranscriptEngine
    static let maxLines = 200
    static let defaultWindowSeconds = 10
    /// WhisperKit decodes at least the last second of a window again at the start of the next, so a word cut at the
    /// boundary is not lost; a sentence still running there is held back from its start (see `cut`).
    static let overlapSeconds = 1
    /// The most a sentence running into the window end is held back; a longer one is shown as decoded.
    static let maxKeepSeconds = 5

    /// Every change, for a single consumer; finished by `stop()`.
    public nonisolated let updates: AsyncStream<Snapshot>
    private nonisolated let updatesContinuation: AsyncStream<Snapshot>.Continuation
    /// Filled by `append` on the writer queue, emptied by the decode task.
    private nonisolated let buffer = Mutex(Buffer())
    /// At most one pending wake-up: a full window is waiting.
    private nonisolated let wake: AsyncStream<Void>
    private nonisolated let wakeContinuation: AsyncStream<Void>.Continuation
    private nonisolated let modelStore: ModelStore
    private nonisolated let gate: ModelLoadGate
    private nonisolated let engineID: String
    private nonisolated let language: @Sendable () -> String?
    private var snapshot = Snapshot(state: .loading)
    private var nextLineID = 0
    private var decoder: Task<Void, Never>?

    /// `engineID` is the catalog id picked in settings (see `model(in:engineID:)`). `language` gives the current
    /// marker language ("auto", a BCP-47 code, or nil = detect), read per window.
    public init(
        modelStore: ModelStore, gate: ModelLoadGate, engineID: String, language: @escaping @Sendable () -> String?
    ) {
        self.modelStore = modelStore
        self.gate = gate
        self.engineID = engineID
        self.language = language
        (updates, updatesContinuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(1))
        (wake, wakeContinuation) = AsyncStream.makeStream(bufferingPolicy: .bufferingNewest(1))
    }

    /// Loads the model at `.utility` QoS and starts decoding; publishes `noModel` instead when none is installed.
    public func start() {
        guard decoder == nil, !buffer.withLock({ $0.closed }) else { return }
        guard let entry = Self.model(in: modelStore, engineID: engineID), let folder = modelStore.folder(for: entry.id) else {
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

    /// Holds the model gate from load to unload.
    private nonisolated func decode(entry: ModelEntry, folder: URL) async {
        do {
            try await gate.withExclusiveModel(owner: .liveTranscript) { [self] in
                try await run(entry: entry, folder: folder)
            }
        } catch {
            buffer.withLock { $0.close() }
            if !Task.isCancelled { await publish(.failed(error.localizedDescription)) }
        }
    }

    /// Owns the model from load to unload. Nemotron streams keep their context, so its windows do not overlap.
    private nonisolated func run(entry: ModelEntry, folder: URL) async throws {
        if entry.id == NemotronStreamingEngine.entryID {
            let engine = try await NemotronStreamingEngine.load(folder: folder, channels: 2)
            buffer.withLock { $0.resize(windowSamples: engine.windowSamples) }
            await listen(overlap: 0) { Decoded(lines: await transcribe($0, with: engine)) }
            await engine.unload()
        } else {
            let whisper = try await WhisperKitEngine.load(entry: entry, folder: folder)
            await listen(overlap: Self.overlapSeconds * WhisperKit.sampleRate) { await transcribe($0, with: whisper) }
            await whisper.unloadModels()
        }
    }

    /// Decodes every full window until `stop()`, then puts its tail back for the next one. When the tail no longer
    /// fits, the held lines are shown at once and only `overlap` samples (or what fits) go back.
    private nonisolated func listen(overlap: Int, _ transcribe: (Window) async -> Decoded) async {
        await publish(.listening)
        for await _ in wake {
            while let window = buffer.withLock({ $0.take() }) {
                let decoded = await transcribe(window)
                var lines = decoded.lines
                if !buffer.withLock({ $0.restore(window, keep: decoded.keep, overlap: overlap) }) {
                    lines += decoded.held
                }
                if !lines.isEmpty { await add(lines) }
            }
        }
    }

    /// Speech of each non-silent channel, in start order across both; segments from the cut on are held back and
    /// the window is kept from there, so they are decoded again whole with the next audio.
    private nonisolated func transcribe(_ window: Window, with whisper: WhisperKit) async -> Decoded {
        let code = language().flatMap { $0.isEmpty || $0.lowercased() == "auto" ? nil : WhisperKitEngine.whisperLanguage($0) }
        let options = DecodingOptions(
            language: code, detectLanguage: code == nil, skipSpecialTokens: true, wordTimestamps: false)
        var found: [(start: Float, end: Float, speaker: Speaker, text: String)] = []
        for (speaker, samples) in [(Speaker.me, window.mic), (.others, window.system)]
        where Self.peak(samples) >= TranscriptionPipeline.silencePeak {
            guard let results = try? await whisper.transcribe(audioArray: samples, decodeOptions: options) else {
                continue
            }
            for segment in results.flatMap(\.segments) {
                let text = WhisperKitEngine.speechText(segment.text)
                guard !text.isEmpty, !WhisperKitEngine.isSilent(samples, from: segment.start, to: segment.end) else {
                    continue
                }
                found.append((segment.start, segment.end, speaker, text))
            }
        }
        found.sort { $0.start < $1.start }
        let rate = Float(WhisperKit.sampleRate)
        let seconds = Float(window.mic.count) / rate
        let cut = Self.cut(found.map { (start: $0.start, end: $0.end) }, window: seconds)
        return Decoded(
            lines: found.filter { $0.start < cut }.map { ($0.speaker, $0.text) },
            held: found.filter { $0.start >= cut }.map { ($0.speaker, $0.text) },
            keep: Int(((seconds - cut) * rate).rounded()))
    }

    /// Where a window's shown text ends (seconds): `overlapSeconds` before its end, moved back to the start of any
    /// span still running there, unless that holds back more than `maxKeepSeconds` (non-stop speech).
    static func cut(_ spans: [(start: Float, end: Float)], window: Float) -> Float {
        let overlap = window - Float(overlapSeconds)
        var cut = overlap
        while let span = spans.first(where: { $0.start < cut && cut < $0.end }) { cut = span.start }
        return cut < window - Float(maxKeepSeconds) ? overlap : cut
    }

    /// Text of each non-silent channel's stream, mic first.
    private nonisolated func transcribe(
        _ window: Window, with engine: NemotronStreamingEngine
    ) async -> [(Speaker, String)] {
        let language = language()
        var found: [(Speaker, String)] = []
        let channels = [
            (RecordingSession.Channel.mic, Speaker.me, window.mic),
            (RecordingSession.Channel.system, .others, window.system),
        ]
        for (channel, speaker, samples) in channels where Self.peak(samples) >= TranscriptionPipeline.silencePeak {
            guard let text = try? await engine.transcribe(samples, channel: channel, language: language),
                  !text.isEmpty else { continue }
            found.append((speaker, text))
        }
        return found
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

    /// The selected engine (a WhisperKit entry or Nemotron streaming) if installed, else the preferred model if
    /// installed, else the smallest installed WhisperKit entry usable in any language, else Nemotron if installed.
    static func model(in store: ModelStore, engineID: String) -> ModelEntry? {
        if let selected = ModelCatalog.entry(id: engineID), selected.kind == .asr,
           selected.runtime == .whisperKit || selected.id == NemotronStreamingEngine.entryID,
           store.isInstalled(selected.id) {
            return selected
        }
        if let preferred = ModelCatalog.entry(id: preferredModelID), store.isInstalled(preferred.id) { return preferred }
        return ModelCatalog.asr
            .filter { $0.runtime == .whisperKit && !$0.vietnameseOnly && !$0.liveOnly && store.isInstalled($0.id) }
            .min { $0.diskMB < $1.diskMB }
            ?? ModelCatalog.entry(id: NemotronStreamingEngine.entryID).flatMap { store.isInstalled($0.id) ? $0 : nil }
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

    /// Lines to show now, lines held back (decoded again from the kept tail), and the samples kept from the end.
    private struct Decoded {
        var lines: [(Speaker, String)]
        var held: [(Speaker, String)] = []
        var keep = 0
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

        /// Keeps only the newest `windowSamples` already buffered.
        mutating func resize(windowSamples: Int) {
            self.windowSamples = windowSamples
            mic.removeFirst(max(0, mic.count - windowSamples))
            system.removeFirst(max(0, system.count - windowSamples))
        }

        /// The full window, leaving nothing: `restore` puts back the tail to decode again once it is decoded.
        mutating func take() -> Window? {
            guard !closed, mic.count >= windowSamples else { return nil }
            let window = Window(mic: mic, system: system)
            mic = []
            system = []
            return window
        }

        /// Puts the last `keep` samples of `window` back in front of what arrived while it was decoded. False when
        /// they no longer fit (`append` would drop them first): then only its last `overlap` samples, or what fits.
        /// Also false once closed, so held lines are still shown when `stop()` lands mid-decode.
        mutating func restore(_ window: Window, keep: Int, overlap: Int) -> Bool {
            guard !closed else { return false }
            let room = max(0, windowSamples - mic.count)
            let fits = keep <= room
            let count = fits ? keep : min(overlap, room)
            mic = window.mic.suffix(count) + mic
            system = window.system.suffix(count) + system
            return fits
        }

        mutating func close() {
            closed = true
            mic = []
            system = []
        }
    }
}
