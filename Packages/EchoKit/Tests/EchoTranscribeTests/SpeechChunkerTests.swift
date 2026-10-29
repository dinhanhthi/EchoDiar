@testable import EchoTranscribe
import Foundation
import Testing

/// Guards the chunk boundaries of transcribe-while-recording: chunks close at pauses, silence is never chunked, and
/// speech longer than the model window is cut at its quietest frame into contiguous chunks. Signals are 16 kHz sines:
/// speech at −20 dBFS with a 100 ms −45 dBFS dip each second (like the gaps between words, so the noise floor never
/// climbs to the speech level), a −70 dBFS floor and a −65 dBFS cut marker. Fed in 1000-sample blocks (not a whole
/// number of 20 ms frames), as the recording sink's blocks vary. Tolerances allow the 300 ms pre-roll and 200 ms
/// post-roll.
struct SpeechChunkerTests {
    @Test func aPauseClosesTheChunkAndSilenceIsNotChunked() {
        let signal = Self.noiseFloor(1) + Self.speech(3) + Self.noiseFloor(2) + Self.speech(3) + Self.noiseFloor(1)
        var chunker = SpeechChunker(maxChunkSeconds: 30)

        let closed = Self.feed(signal, to: &chunker) { $0 < 5 * Self.rate ? "en" : "fr" }
        let flushed = Self.flush(&chunker)

        #expect(closed.count == 1)
        #expect(flushed.count == 1)
        let (first, second) = (closed[0], flushed[0])
        #expect((Self.at(1) - Self.at(0.3) - 2 * Self.frame...Self.at(1)).contains(first.start))
        #expect((Self.at(4)...Self.at(4.2) + 2 * Self.frame).contains(first.end))
        #expect((Self.at(6) - Self.at(0.3) - 2 * Self.frame...Self.at(6)).contains(second.start))
        #expect((Self.at(9)...signal.count).contains(second.end))
        #expect(first.language == "en")
        #expect(second.language == "fr")
        for chunk in closed + flushed {
            #expect(chunk.samples == Array(signal[chunk.start..<chunk.end]))
        }
    }

    @Test func speechLongerThanTheWindowIsCutAtTheQuietestFrame() {
        let signal = Self.noiseFloor(1) + Self.speech(11) + Self.dip(0.1) + Self.speech(18.9)
        var chunker = SpeechChunker(maxChunkSeconds: 15)

        let closed = Self.feed(signal, to: &chunker)
        let flushed = Self.flush(&chunker)

        #expect(closed.count == 2)
        #expect(flushed.count == 1)
        let chunks = closed + flushed
        #expect((Self.at(12) - Self.frame...Self.at(12.1) + Self.frame).contains(chunks[0].end))
        for chunk in chunks {
            #expect(chunk.samples.count <= Self.at(15))
            #expect(chunk.samples == Array(signal[chunk.start..<chunk.end]))
        }
        for chunk in closed {
            #expect(chunk.samples.count >= Self.at(10) - Self.frame)
        }
        for (chunk, next) in zip(chunks, chunks.dropFirst()) {
            #expect(next.start == chunk.end)
        }
        #expect(chunks.last?.end == signal.count)
    }

    /// The live tuning bounds the time to a live line: a 0.5 s pause closes a chunk from the live minimum (the batch
    /// tuning waits for a 0.6 s pause and 8 s), and continuous speech is cut into contiguous chunks of at most 12 s, in
    /// the last third (8–12 s).
    @Test func liveChunksCloseAtAHalfSecondPauseFromTheLiveMinimumAndNeverExceed12() throws {
        let speechSeconds = Double(RecordingTranscriber.liveShortPauseMinSeconds) + 1
        let paused = Self.noiseFloor(1) + Self.speech(speechSeconds) + Self.noiseFloor(0.55)
        var live = Self.liveChunker()
        var batch = SpeechChunker(maxChunkSeconds: 30)

        let closed = Self.feed(paused, to: &live)

        #expect(closed.count == 1)
        #expect(Self.feed(paused, to: &batch).isEmpty)
        let chunk = try #require(closed.first)
        let speechEnd = 1 + speechSeconds
        #expect((Self.at(speechEnd)...Self.at(speechEnd + 0.2) + 2 * Self.frame).contains(chunk.end))
        #expect(chunk.samples == Array(paused[chunk.start..<chunk.end]))

        let continuous = Self.noiseFloor(1) + Self.speech(20)
        live = Self.liveChunker()
        let cut = Self.feed(continuous, to: &live)
        let chunks = cut + Self.flush(&live)

        #expect(!cut.isEmpty)
        for chunk in chunks {
            #expect(chunk.samples.count <= Self.at(12))
            #expect(chunk.samples == Array(continuous[chunk.start..<chunk.end]))
        }
        for chunk in cut {
            #expect(chunk.samples.count >= Self.at(8) - Self.frame)
        }
        for (chunk, next) in zip(chunks, chunks.dropFirst()) {
            #expect(next.start == chunk.end)
        }
        #expect(chunks.last?.end == continuous.count)
    }

    @Test func flushEmitsTheOpenChunk() {
        let signal = Self.noiseFloor(0.5) + Self.speech(2)
        var chunker = SpeechChunker(maxChunkSeconds: 30)

        #expect(Self.feed(signal, to: &chunker).isEmpty)
        let flushed = Self.flush(&chunker)

        #expect(flushed.count == 1)
        #expect((Self.at(0.5) - Self.at(0.3) - 2 * Self.frame...Self.at(0.5)).contains(flushed[0].start))
        #expect(flushed[0].end == signal.count)
        #expect(flushed[0].language == "en")
    }

    /// Guards the open-chunk peek the live transcriber reads ("others speaking now", tentative text): nil while no
    /// chunk is open, every sample fed since the chunk's start while one is, the start and a prefix of the chunk
    /// `flush()` later emits, and pure, so a twin chunker that is never peeked emits the same chunks.
    @Test func openChunkIsAPurePeekAtTheChunkFlushWillEmit() throws {
        let parts = [Self.noiseFloor(1), Self.speech(3) + Self.noiseFloor(2) + Self.speech(2), Self.noiseFloor(0.3)]
        var chunker = SpeechChunker(maxChunkSeconds: 30)
        var twin = SpeechChunker(maxChunkSeconds: 30)

        var closed = Self.feed(parts[0], to: &chunker)
        #expect(chunker.openChunk == nil)
        closed += Self.feed(parts[1], to: &chunker)
        let open = try #require(chunker.openChunk)
        #expect(chunker.openChunk == open)
        closed += Self.feed(parts[2], to: &chunker)
        let flushed = Self.flush(&chunker)
        #expect(chunker.openChunk == nil)

        #expect(closed.count == 1)
        let last = try #require(flushed.first)
        #expect(open.startSample == last.start)
        #expect(open.startSample + open.samples.count == Self.at(8))
        #expect(open.samples.count < last.samples.count)
        #expect(Array(last.samples.prefix(open.samples.count)) == open.samples)
        #expect(open.language == "en")
        #expect(closed == parts.flatMap { Self.feed($0, to: &twin) })
        #expect(flushed == Self.flush(&twin))
    }

    // MARK: - Helpers

    private static let rate = 16_000
    private static let frame = 320

    /// A chunk as sample range + content, so the tests do not depend on the chunk type's name.
    private struct Piece: Equatable {
        var start: Int
        var samples: [Float]
        var language: String?
        var end: Int { start + samples.count }
    }

    private static func feed(
        _ signal: [Float], to chunker: inout SpeechChunker, language: (Int) -> String? = { _ in "en" }
    ) -> [Piece] {
        var pieces: [Piece] = []
        for offset in stride(from: 0, to: signal.count, by: 1_000) {
            let block = Array(signal[offset..<min(offset + 1_000, signal.count)])
            pieces += chunker.append(block, language: language(offset)).map {
                Piece(start: $0.startSample, samples: $0.samples, language: $0.language)
            }
        }
        return pieces
    }

    /// The chunker of one live channel (`RecordingTranscriber.Buffer`).
    private static func liveChunker() -> SpeechChunker {
        SpeechChunker(maxChunkSeconds: RecordingTranscriber.liveMaxChunkSeconds,
                      shortPauseMinSeconds: RecordingTranscriber.liveShortPauseMinSeconds,
                      shortPauseFrames: RecordingTranscriber.liveShortPauseFrames)
    }

    private static func flush(_ chunker: inout SpeechChunker) -> [Piece] {
        chunker.flush().map { Piece(start: $0.startSample, samples: $0.samples, language: $0.language) }
    }

    private static func at(_ seconds: Double) -> Int { Int((seconds * Double(rate)).rounded()) }

    /// RMS −20 dBFS, starting each second with 100 ms at −45 dBFS.
    private static func speech(_ seconds: Double) -> [Float] {
        tone(seconds) { $0 % rate < at(0.1) ? 0.007_95 : 0.141 }
    }
    /// RMS −70 dBFS.
    private static func noiseFloor(_ seconds: Double) -> [Float] { tone(seconds) { _ in 0.000_447 } }
    /// RMS −65 dBFS: the quietest stretch of a monologue, too short to close a chunk.
    private static func dip(_ seconds: Double) -> [Float] { tone(seconds) { _ in 0.000_795 } }

    private static func tone(_ seconds: Double, amplitude: (Int) -> Float) -> [Float] {
        (0..<at(seconds)).map { amplitude($0) * sin(2 * .pi * 220 * Float($0) / Float(rate)) }
    }
}
