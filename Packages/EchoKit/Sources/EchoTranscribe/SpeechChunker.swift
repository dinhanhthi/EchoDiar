import Accelerate

/// Splits a 16 kHz mono stream into speech chunks for transcribe-while-recording, with an energy VAD over 20 ms
/// frames. A chunk opens on speech (with pre-roll), closes at a pause (with post-roll) and is cut at its quietest
/// frame when it reaches `maxChunkSeconds`, so every chunk fits the model window. The length a chunk needs before a
/// short pause closes it is per instance (`shortPauseMinSeconds`): the batch gap fill keeps the default, the live path
/// passes a shorter one. Silence between chunks is never emitted: it counts as covered. The clock is the sample index
/// of the stream, so chunk starts are file time.
///
/// Pure and bounded: only the open chunk and the pre-roll are retained.
struct SpeechChunker {
    /// Contiguous samples of the stream starting at `startSample`.
    struct Chunk: Equatable, Sendable {
        var startSample: Int
        var samples: [Float]
        /// The language of the chunk's first frame (nil = detect).
        var language: String?
    }

    // MARK: - Tuning (initial values, see the go/no-go measurement)

    static let sampleRate = 16_000
    static let frameSamples = 320  // 20 ms
    static let framesPerSecond = sampleRate / frameSamples
    /// Where the noise floor starts; it then follows the quietest frame.
    static let initialFloorDB: Float = -70
    /// How fast the noise floor climbs back after a quiet frame (it falls instantly).
    static let floorRiseDBPerSecond: Float = 2
    /// A frame is speech when its RMS is at least this far above the floor...
    static let speechAboveFloorDB: Float = 10
    /// ...and at least this loud.
    static let speechMinDB: Float = -55
    /// The level of digital silence, so zeros do not drag the floor to minus infinity.
    static let silenceDB: Float = -100
    /// Consecutive speech frames that open a chunk.
    static let openFrames = 2
    static let preRollFrames = 15  // 300 ms
    static let postRollFrames = 10  // 200 ms
    /// A pause this long closes a chunk of at least `shortPauseMinFrames`.
    static let shortPauseFrames = 30  // 0.6 s
    /// A pause this long closes a chunk of any length.
    static let longPauseFrames = 75  // 1.5 s
    /// The default length a chunk needs before a short pause closes it, capped at half the window.
    static let shortPauseMinSeconds = 8
    /// A chunk reaching the window is cut at its quietest frame in the last third.
    static let cutSearchFraction = (numerator: 2, denominator: 3)

    private struct Frame {
        var db: Float
        var isSpeech: Bool
        var language: String?
    }

    private let maxFrames: Int
    private let shortPauseMinFrames: Int
    private let floorRisePerFrame = floorRiseDBPerSecond / Float(framesPerSecond)

    private var floorDB = initialFloorDB
    /// Whole frames: the open chunk, or the pre-roll while none is open. `samples` starts at `startSample`.
    private var samples: [Float] = []
    private var frames: [Frame] = []
    private var startSample = 0
    private var isOpen = false
    /// Non-speech frames at the end of `frames`.
    private var trailingSilence = 0
    /// The start of a frame not yet complete, with the language of its first sample.
    private var partial: [Float] = []
    private var partialLanguage: String?

    init(maxChunkSeconds: Int, shortPauseMinSeconds: Int = SpeechChunker.shortPauseMinSeconds) {
        maxFrames = max(1, maxChunkSeconds) * Self.framesPerSecond
        shortPauseMinFrames = min(shortPauseMinSeconds * Self.framesPerSecond, maxFrames / 2)
    }

    /// Feeds the next samples of the stream; returns the chunks they closed.
    mutating func append(_ samples: [Float], language: String?) -> [Chunk] {
        var chunks: [Chunk] = []
        var index = samples.startIndex
        while index < samples.endIndex {
            if partial.isEmpty { partialLanguage = language }
            let take = min(Self.frameSamples - partial.count, samples.endIndex - index)
            partial += samples[index..<index + take]
            index += take
            if partial.count == Self.frameSamples {
                if let chunk = process(partial, language: partialLanguage) { chunks.append(chunk) }
                partial.removeAll(keepingCapacity: true)
            }
        }
        return chunks
    }

    /// A copy of the open chunk so far (nil when none is open): every retained sample, with its trailing silence and
    /// the incomplete frame, for the live transcriber's tentative text and its "others speaking now" check. Changes
    /// nothing, so the chunks emitted later are the same.
    var openChunk: Chunk? {
        guard isOpen else { return nil }
        return Chunk(startSample: startSample, samples: samples + partial, language: frames.first?.language)
    }

    /// The open chunk's start and length so far (`openChunk` without copying its samples), nil when none is open.
    var openSpan: (startSample: Int, count: Int)? {
        isOpen ? (startSample, samples.count + partial.count) : nil
    }

    /// Emits the open chunk, trimmed to its post-roll (which may take in the incomplete frame), and drops the rest;
    /// the clock and the floor carry on, so the stream can continue (as after a pause).
    mutating func flush() -> [Chunk] {
        let speechEnd = frames.count - trailingSilence
        var chunk: Chunk?
        if isOpen, speechEnd > 0 {
            let end = min((speechEnd + Self.postRollFrames) * Self.frameSamples, samples.count + partial.count)
            let all = samples + partial
            chunk = Chunk(startSample: startSample, samples: Array(all[0..<end]), language: frames[0].language)
        }
        startSample += samples.count + partial.count
        samples.removeAll()
        frames.removeAll()
        partial.removeAll()
        isOpen = false
        trailingSilence = 0
        return chunk.map { [$0] } ?? []
    }

    // MARK: - Frames

    private mutating func process(_ frame: [Float], language: String?) -> Chunk? {
        let db = Self.rmsDB(frame)
        let isSpeech = db >= max(floorDB + Self.speechAboveFloorDB, Self.speechMinDB)
        floorDB = min(db, floorDB + floorRisePerFrame)
        samples += frame
        frames.append(Frame(db: db, isSpeech: isSpeech, language: language))
        trailingSilence = isSpeech ? 0 : trailingSilence + 1

        guard isOpen else {
            let openRun = frames.suffix(Self.openFrames).filter(\.isSpeech).count
            if openRun == Self.openFrames {
                isOpen = true
                dropFrames(frames.count - Self.preRollFrames - Self.openFrames)
            } else {
                dropFrames(frames.count - Self.preRollFrames - Self.openFrames + 1)
            }
            return nil
        }
        let speechEnd = frames.count - trailingSilence
        let end = min(speechEnd + Self.postRollFrames, frames.count)
        if trailingSilence >= Self.longPauseFrames
            || (trailingSilence >= Self.shortPauseFrames && end >= shortPauseMinFrames)
        {
            return close(at: end, hasSpeech: speechEnd > 0)
        }
        if frames.count >= maxFrames { return cut() }
        return nil
    }

    /// Emits the first `end` frames; the rest becomes the pre-roll of the next chunk.
    private mutating func close(at end: Int, hasSpeech: Bool) -> Chunk? {
        let chunk = hasSpeech ? takeChunk(frames: end) : nil
        if !hasSpeech { dropFrames(end) }
        isOpen = false
        dropFrames(frames.count - Self.preRollFrames - Self.openFrames + 1)
        return chunk
    }

    /// Emits the chunk up to its quietest frame in the last third of the window; the rest stays open.
    private mutating func cut() -> Chunk? {
        let lower = maxFrames * Self.cutSearchFraction.numerator / Self.cutSearchFraction.denominator
        var quietest = lower
        for index in lower..<frames.count where frames[index].db < frames[quietest].db { quietest = index }
        let chunk = takeChunk(frames: quietest)
        trailingSilence = frames.reversed().prefix { !$0.isSpeech }.count
        return chunk
    }

    /// Removes and returns the first `count` frames as a chunk.
    private mutating func takeChunk(frames count: Int) -> Chunk {
        let chunk = Chunk(
            startSample: startSample,
            samples: Array(samples[0..<count * Self.frameSamples]),
            language: frames[0].language
        )
        dropFrames(count)
        return chunk
    }

    private mutating func dropFrames(_ count: Int) {
        guard count > 0 else { return }
        samples.removeFirst(count * Self.frameSamples)
        frames.removeFirst(count)
        startSample += count * Self.frameSamples
    }

    private static func rmsDB(_ frame: [Float]) -> Float {
        var rms: Float = 0
        frame.withUnsafeBufferPointer { vDSP_rmsqv($0.baseAddress!, 1, &rms, vDSP_Length($0.count)) }
        return rms > 0 ? max(silenceDB, 20 * log10(rms)) : silenceDB
    }
}
