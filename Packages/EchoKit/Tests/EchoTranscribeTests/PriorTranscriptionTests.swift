import AVFoundation
import EchoCore
@testable import EchoTranscribe
import Foundation
import Testing

/// Guards "failures degrade, never lose text": what the recording transcriber did not transcribe becomes a gap the
/// pipeline transcribes at Stop, and a prior that no longer matches the run is not used (full batch instead).
///
/// Contract for 3.5 (in TranscriptionEngine.swift):
/// - `PriorTranscription.ChunkOutcome`: `transcribed`, `silence` (peak below `TranscriptionPipeline.silencePeak`),
///   `dropped` (over the queue cap), `failed` (ASR threw), `pending` (not decoded by Stop, e.g. while constrained).
/// - `static func gaps(_ chunks: [(span: Span, outcome: ChunkOutcome)]) -> [Span]`: one channel's chunks in file
///   order (the recording transcriber records each chunk's span and outcome per channel). Returns the spans of the
///   dropped, failed and pending chunks, touching or overlapping ones merged. Transcribed and silence chunks, and
///   the pauses between chunks, are covered.
/// - `func isUsable(engineID:engineFallback:languageOverride:audioDuration:) -> Bool` (instance: it compares the
///   run against the prior's own `engineID` and `duration`).
struct PriorTranscriptionTests {
    @Test func gapsAreTheChunksNotTranscribedWithNeighborsMerged() {
        let gaps = PriorTranscription.gaps([
            (span: Span(start: 0, end: 10), outcome: .transcribed),
            (span: Span(start: 10, end: 20), outcome: .failed),
            (span: Span(start: 20, end: 30), outcome: .dropped),
            (span: Span(start: 30, end: 40), outcome: .transcribed),
            (span: Span(start: 42, end: 50), outcome: .silence),
            (span: Span(start: 55, end: 60), outcome: .pending),
        ])
        #expect(gaps == [Span(start: 10, end: 30), Span(start: 55, end: 60)])
        #expect(PriorTranscription.gaps([]).isEmpty)
    }

    @Test func isUsableForTheSameEngineAndAudio() {
        #expect(Self.prior.isUsable(
            engineID: Self.prior.engineID, engineFallback: nil, languageOverride: nil, audioDuration: 600.05))
    }

    /// Another engine, a language fallback, a re-transcribe language or audio of another length: full batch instead.
    @Test(arguments: [
        ("parakeet-tdt-0.6b-v3", nil, nil, 600),
        ("whisperkit-large-v3-turbo", "parakeet-tdt-0.6b-v3", nil, 600),
        ("whisperkit-large-v3-turbo", nil, "fr", 600),
        ("whisperkit-large-v3-turbo", nil, nil, 600.2),
    ] as [(String, String?, String?, Double)])
    func isNotUsableWhenTheRunDiffers(engineID: String, engineFallback: String?, languageOverride: String?,
                                      audioDuration: Double) {
        #expect(!Self.prior.isUsable(
            engineID: engineID, engineFallback: engineFallback, languageOverride: languageOverride,
            audioDuration: audioDuration))
    }

    private static let prior = PriorTranscription(
        engineID: "whisperkit-large-v3-turbo", mic: [], system: [], micGaps: [], systemGaps: [], duration: 600)

    // MARK: - Pipeline

    /// No gaps and speakers off: the prior's segments are the transcript, written without any model (the Models root
    /// is empty, so a batch run would stop at "Needs model").
    @Test func usablePriorWithoutGapsIsTheTranscriptWithoutAModel() async throws {
        let (pipeline, job, root) = try Self.pipelineJob(prior: Self.recorded(duration: 2))
        defer { try? FileManager.default.removeItem(at: root) }
        _ = try await pipeline.transcribe(job)

        let transcript = try Transcript.read(from: job.folder)
        #expect(transcript.engineID == "parakeet-v3")
        #expect(transcript.segments.map(\.text) == ["hello there", "general kenobi"])
        #expect(transcript.segments.map(\.speaker) == [Transcript.Speaker.me, Transcript.Speaker.system(1)])
        let conversation = try Conversation.read(from: job.folder)
        #expect(conversation.status == .transcribed)
        #expect(conversation.hasTranscript)
    }

    /// A gap needs the model: with none installed the job stops at "Needs model".
    @Test func usablePriorWithAGapNeedsTheModel() async throws {
        var prior = Self.recorded(duration: 2)
        prior.systemGaps = [Span(start: 1, end: 2)]
        let (pipeline, job, root) = try Self.pipelineJob(prior: prior)
        defer { try? FileManager.default.removeItem(at: root) }
        #expect(await Self.neededModel(pipeline, job) == "parakeet-v3")
    }

    /// A prior that does not match the audio is ignored: the full batch runs, and needs the model even without gaps.
    @Test func priorOfAnotherLengthRunsTheBatch() async throws {
        let (pipeline, job, root) = try Self.pipelineJob(prior: Self.recorded(duration: 2.5))
        defer { try? FileManager.default.removeItem(at: root) }
        #expect(await Self.neededModel(pipeline, job) == "parakeet-v3")
        #expect((try? Transcript.read(from: job.folder)) == nil)
    }

    /// A gap is cut into speech chunks: silence is not decoded (nor the model loaded for it), each chunk gets the
    /// marker language at its start, and segments come back in file time.
    @Test func gapFillDecodesSpeechChunksInFileTime() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        // 1 s of tone, 2 s of silence, 1 s of tone.
        let audio = root.appendingPathComponent("system.caf")
        try Self.writeMono([0..<16_000, 48_000..<64_000], frames: 64_000, to: audio)
        let entry = ModelCatalog.entry(id: "parakeet-v3")!
        let ranges = LanguageRange.ranges(from: [.init(fileTime: 0, language: "auto"), .init(fileTime: 2, language: "fr")])

        var silent: (any TranscriptionSession)?
        let none = try await TranscriptionPipeline.fillGaps(
            [Span(start: 1.5, end: 2.5)], of: audio, entry: entry, folder: root, ranges: ranges, session: &silent,
            progress: { _ in })
        #expect(none.isEmpty)
        #expect(silent == nil)

        let fake = FakeSession()
        var session: (any TranscriptionSession)? = fake
        let segments = try await TranscriptionPipeline.fillGaps(
            [Span(start: 0, end: 4)], of: audio, entry: entry, folder: root, ranges: ranges, session: &session,
            progress: { _ in })
        #expect(fake.languages == [nil, "fr"])
        #expect(segments.count == 2)
        #expect(abs(segments[0].start) < 0.05)
        #expect((2.6...3.0).contains(segments[1].start))
    }

    /// Records each chunk's language; returns one segment spanning the chunk.
    private final class FakeSession: TranscriptionSession {
        var languages: [String?] = []

        func transcribe(_ samples: [Float], language: String?) async throws -> [RawSegment] {
            languages.append(language)
            return [RawSegment(start: 0, end: Double(samples.count) / 16_000, text: "x")]
        }

        func unload() async {}
    }

    /// The catalog id of the job's "Needs model" error; nil when it succeeded or failed otherwise.
    private static func neededModel(_ pipeline: TranscriptionPipeline, _ job: TranscriptionJob) async -> String? {
        do {
            _ = try await pipeline.transcribe(job)
        } catch TranscriptionError.needsModel(let id) {
            return id
        } catch {}
        return nil
    }

    /// Parakeet segments of a 2 s recording: one on the mic, one on the system channel, no gaps.
    private static func recorded(duration: Double) -> PriorTranscription {
        PriorTranscription(
            engineID: "parakeet-v3", mic: [RawSegment(start: 0, end: 0.8, text: "hello there")],
            system: [RawSegment(start: 1, end: 1.8, text: "general kenobi")], micGaps: [], systemGaps: [],
            duration: duration)
    }

    /// A 2 s stereo recording, a pipeline with an empty Models root, a speakers-off job carrying `prior` with its
    /// engine, and the temporary root holding both.
    private static func pipelineJob(
        prior: PriorTranscription
    ) throws -> (TranscriptionPipeline, TranscriptionJob, URL) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let session = RecordingSession(state: .stopped, audioFileName: "audio.caf")
        let folder = session.folder(in: root)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try session.write(to: folder)
        try writeStereo(seconds: 2, to: folder.appendingPathComponent(session.audioFileName))
        let pipeline = TranscriptionPipeline(
            modelStore: ModelStore(root: root.appendingPathComponent("Models")), gate: ModelLoadGate(),
            deferWhenConstrained: false)
        let job = TranscriptionJob(
            conversationID: session.id, folder: folder, engineID: prior.engineID, processNow: true, diarize: false,
            prior: prior)
        return (pipeline, job, root)
    }

    /// A 16 kHz mono Float32 CAF of `frames` samples: a 440 Hz tone over `tones`, zeros elsewhere.
    private static func writeMono(_ tones: [Range<Int>], frames: Int, to url: URL) throws {
        let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false)!
        let file = try AVAudioFile(forWriting: url, settings: format.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames))!
        buffer.frameLength = buffer.frameCapacity
        for index in 0..<frames {
            let tone = tones.contains { $0.contains(index) }
            buffer.floatChannelData![0][index] = tone ? Float(sin(Double(index) * 2 * .pi * 440 / 16_000) * 0.5) : 0
        }
        try file.write(from: buffer)
    }

    /// A 16 kHz stereo Float32 CAF with a 440 Hz tone on both channels.
    private static func writeStereo(seconds: Int, to url: URL) throws {
        let frames = seconds * 16_000
        let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 2, interleaved: false)!
        let file = try AVAudioFile(forWriting: url, settings: format.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames))!
        buffer.frameLength = buffer.frameCapacity
        for index in 0..<frames {
            let sample = Float(sin(Double(index) * 2 * .pi * 440 / 16_000) * 0.5)
            buffer.floatChannelData![0][index] = sample
            buffer.floatChannelData![1][index] = sample
        }
        try file.write(from: buffer)
    }
}
