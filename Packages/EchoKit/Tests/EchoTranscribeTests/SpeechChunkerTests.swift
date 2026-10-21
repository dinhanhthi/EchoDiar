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

    // MARK: - Helpers

    private static let rate = 16_000
    private static let frame = 320

    /// A chunk as sample range + content, so the tests do not depend on the chunk type's name.
    private struct Piece {
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
