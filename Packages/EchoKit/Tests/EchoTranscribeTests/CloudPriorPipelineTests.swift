@testable import EchoTranscribe
import AVFoundation
import EchoAI
import EchoCore
import Foundation
import Synchronization
import Testing

/// Guards the cloud realtime prior as the transcript (`job.prior.engineID == cloud.engineID`): only its gaps are
/// uploaded (padded, by range, split under the cap), their speaker labels are bridged onto the stream's, and a
/// diarizer runs only when speakers are on and the stream labelled none. Recordings are 16 kHz stereo CAFs with a
/// 440 Hz tone; the fake transcriber answers by its `diarize` flag or call order, relative to the upload start.
struct CloudPriorPipelineTests {
    /// (a) A mic gap is one upload of about the gap (±0.5 s padding) whose segment inside the gap joins the prior's;
    /// a system gap goes up with ±30 s of padding and its own label for the stream's voice is mapped back onto the
    /// stream's. The transcript records `cloud:fake` and the realtime model, not the batch one.
    @Test func gapsAreUploadedPaddedAndJoinTheStream() async throws {
        let recording = try Self.makeRecording(seconds: 100)
        defer { recording.remove() }
        let prior = PriorTranscription(
            engineID: "cloud:fake",
            mic: [RawSegment(start: 10, end: 12, text: "hello from me"),
                  RawSegment(start: 60, end: 62, text: "later from me")],
            system: Self.raw(Self.pieces("A", 0...50) + Self.pieces("A", 52...100)),
            micGaps: [Span(start: 40, end: 42)], systemGaps: [Span(start: 50, end: 52)], duration: 100,
            modelID: "rt-model")
        let asr = FakeTranscriber { _, diarize, seconds in
            diarize ? Self.pieces("X", 0...seconds, text: "upload")
                : [CloudSegment(start: 0, end: 0.4, text: "padding word"),
                   CloudSegment(start: 0.6, end: 2.4, text: "words in the gap")]
        }

        let transcript = try await recording.transcribe(prior: prior, diarize: true, asr: asr)

        #expect(asr.calls.map(\.diarize) == [false, true])
        #expect(abs(asr.calls[0].seconds - 3) < 0.3)
        #expect(abs(asr.calls[1].seconds - 62) < 0.3)
        #expect(transcript.engineID == "cloud:fake")
        #expect(transcript.modelID == "rt-model")
        let mic = transcript.segments.filter { $0.channel == .mic }
        #expect(mic.map(\.text) == ["hello from me", "words in the gap", "later from me"])
        let system = transcript.segments.filter { $0.channel == .system }
        #expect(system.count == prior.system.count + 1)
        #expect(system.filter { $0.text == "upload" }.map(\.start) == [50])
        #expect(Set(system.map(\.speaker)) == [Transcript.Speaker.system(1)])
    }

    /// (b) A prior with no gaps whose stream labelled its speakers: nothing is uploaded, no diarizer runs.
    @Test func noGapsUploadNothing() async throws {
        let recording = try Self.makeRecording(seconds: 4)
        defer { recording.remove() }
        let prior = PriorTranscription(
            engineID: "cloud:fake", mic: [RawSegment(start: 0, end: 1, text: "hello from me")],
            system: [RawSegment(start: 2, end: 3, text: "hi there", speaker: "A")], micGaps: [], systemGaps: [],
            duration: 4, modelID: "rt-model")
        let asr = FakeTranscriber { _, _, _ in [] }
        let diarizer = FakeTranscriber { _, _, _ in [] }

        let transcript = try await recording.transcribe(prior: prior, diarize: true, asr: asr, diarizer: diarizer)

        #expect(asr.calls.isEmpty)
        #expect(diarizer.calls.isEmpty)
        #expect(transcript.segments.map(\.text) == ["hello from me", "hi there"])
    }

    /// (b2) A system gap whose padded upload is over the fake's `maxUploadBytes` goes up as several parts inside the
    /// range, each labelling the one voice differently; the parts' labels and the stream's stay one speaker.
    @Test func aLongGapIsSplitInsideItsRangeAndKeepsOneSpeaker() async throws {
        let recording = try Self.makeRecording(seconds: 140)
        defer { recording.remove() }
        let prior = PriorTranscription(
            engineID: "cloud:fake", mic: [],
            system: Self.raw(Self.pieces("A", 0...40) + Self.pieces("A", 100...140)),
            micGaps: [], systemGaps: [Span(start: 40, end: 100)], duration: 140, modelID: "rt-model")
        let asr = FakeTranscriber(maxUploadBytes: 200_000) { call, _, seconds in
            Self.pieces("L\(call)", 0...seconds, text: "upload")
        }

        let transcript = try await recording.transcribe(prior: prior, diarize: true, asr: asr)

        #expect(asr.calls.count >= 2)
        #expect(asr.calls.allSatisfy { $0.diarize && $0.seconds < 70 })
        let system = transcript.segments.filter { $0.channel == .system }
        let uploaded = system.filter { $0.text == "upload" }
        #expect(uploaded.count >= 25)
        #expect(uploaded.allSatisfy { $0.start >= 39 && $0.end <= 101 })
        #expect(Set(system.map(\.speaker)) == [Transcript.Speaker.system(1)])
    }

    /// (g) Speakers off: the stream's labels are dropped, the system gap is uploaded without diarization and its
    /// labels dropped too, the system channel is one speaker, and the cloud diarizer is never called.
    @Test func speakersOffDropTheStreamLabelsAndRunNoDiarizer() async throws {
        let recording = try Self.makeRecording(seconds: 10)
        defer { recording.remove() }
        let prior = PriorTranscription(
            engineID: "cloud:fake", mic: [],
            system: [RawSegment(start: 0, end: 2, text: "first voice", speaker: "A"),
                     RawSegment(start: 6, end: 8, text: "second voice", speaker: "B")],
            micGaps: [], systemGaps: [Span(start: 3, end: 5)], duration: 10, modelID: "rt-model")
        let asr = FakeTranscriber { _, _, _ in [CloudSegment(start: 0.5, end: 2.5, text: "gap words", speaker: "Q")] }
        let diarizer = FakeTranscriber { _, _, _ in [CloudSegment(start: 0, end: 2, text: "x", speaker: "D")] }

        let transcript = try await recording.transcribe(prior: prior, diarize: false, asr: asr, diarizer: diarizer)

        #expect(asr.calls.map(\.diarize) == [false])
        #expect(diarizer.calls.isEmpty)
        let system = transcript.segments.filter { $0.channel == .system }
        #expect(system.map(\.text) == ["first voice", "gap words", "second voice"])
        #expect(Set(system.map(\.speaker)) == [Transcript.Speaker.system(1)])
    }

    /// (f) Speakers on and a stream without labels: the picked cloud diarizer runs once on the whole system channel
    /// and labels it; nothing is transcribed again.
    @Test func speakersOnWithoutStreamLabelsRunTheCloudDiarizerOnce() async throws {
        let recording = try Self.makeRecording(seconds: 6)
        defer { recording.remove() }
        let prior = PriorTranscription(
            engineID: "cloud:fake", mic: [],
            system: [RawSegment(start: 0, end: 2, text: "first voice"),
                     RawSegment(start: 3, end: 5, text: "second voice")],
            micGaps: [], systemGaps: [], duration: 6, modelID: "rt-model")
        let asr = FakeTranscriber { _, _, _ in [] }
        let diarizer = FakeTranscriber { _, _, _ in
            [CloudSegment(start: 0, end: 2, text: "x", speaker: "A"), CloudSegment(start: 3, end: 5, text: "y", speaker: "B")]
        }

        let transcript = try await recording.transcribe(prior: prior, diarize: true, asr: asr, diarizer: diarizer)

        #expect(asr.calls.isEmpty)
        #expect(diarizer.calls.map(\.diarize) == [true])
        #expect(abs((diarizer.calls.first?.seconds ?? 0) - 6) < 0.3)
        #expect(transcript.segments.map(\.speaker) == [Transcript.Speaker.system(1), Transcript.Speaker.system(2)])
    }

    /// (e) Two sessions around an unplanned disconnect both call their speaker "A": the second's labels are scoped
    /// (`r1:A`). When the gap upload hears nobody in its padding the two stay two speakers; when it hears the same
    /// voice through the left padding, the gap and the right padding, the second session's "A" joins the first's.
    @Test func scopedSessionLabelsJoinOnlyThroughTheGapsPadding() {
        var coverage = RealtimeCoverage()
        for (offset, sent) in [(0.0, 40.0), (60.0, 100.0)] {
            let session = coverage.open(at: offset, bridged: false)
            coverage.sent(session, through: sent)
            for final in Self.pieces("A", 0...40) { coverage.final(session, final) }
            coverage.close(session, drained: false)
        }
        let gap = Span(start: 40, end: 60)
        #expect(coverage.gaps(through: 100) == [gap])
        let stream = Self.raw(coverage.segments())
        #expect(Set(stream.compactMap(\.speaker)) == ["A", "r1:A"])

        let cases: [(response: [CloudSegment], speakers: Set<String>)] = [
            ([], ["A", "r1:A"]),
            (Self.pieces("X", 10...90), ["A"]),
        ]
        for (response, speakers) in cases {
            let joined = TranscriptionPipeline.joinGap(
                response, gap: gap, upload: 10...90, into: stream, labels: true, partIndex: 1)
            #expect(Set(joined.compactMap(\.speaker)) == speakers)
        }
    }

    /// A resume after a pause with no gap (`systemBridges`): the stream scoped the resumed session's labels
    /// (`r1:A`); the audio ±30 s around the resume goes up once with labels, hears one voice there, and maps `r1:A`
    /// back onto `A`. The upload adds no segment.
    @Test func aResumeBridgeMapsTheScopedLabelsBackAndAddsNoSegment() async throws {
        let recording = try Self.makeRecording(seconds: 100)
        defer { recording.remove() }
        let prior = PriorTranscription(
            engineID: "cloud:fake", mic: [],
            system: Self.raw(Self.pieces("A", 0...50) + Self.pieces("r1:A", 50...100)),
            micGaps: [], systemGaps: [], duration: 100, modelID: "rt-model", systemBridges: [50])
        let asr = FakeTranscriber { _, _, seconds in Self.pieces("X", 0...seconds, text: "upload") }

        let transcript = try await recording.transcribe(prior: prior, diarize: true, asr: asr)

        #expect(asr.calls.map(\.diarize) == [true])
        #expect(abs((asr.calls.first?.seconds ?? 0) - 60) < 0.3)
        let system = transcript.segments.filter { $0.channel == .system }
        #expect(system.count == prior.system.count)
        #expect(!system.contains { $0.text == "upload" })
        #expect(Set(system.map(\.speaker)) == [Transcript.Speaker.system(1)])
    }

    // MARK: - Harness

    /// Records each call's `diarize` and upload length (read from the file it gets), and answers with `script`
    /// (call index from 0, `diarize`, upload seconds), relative to the upload start.
    private final class FakeTranscriber: CloudTranscriber {
        struct Call {
            var diarize: Bool
            var seconds: Double
        }

        let maxUploadBytes: Int?
        private let script: @Sendable (Int, Bool, Double) -> [CloudSegment]
        private let log = Mutex<[Call]>([])
        var calls: [Call] { log.withLock { $0 } }

        init(maxUploadBytes: Int? = nil, script: @escaping @Sendable (Int, Bool, Double) -> [CloudSegment]) {
            self.maxUploadBytes = maxUploadBytes
            self.script = script
        }

        func transcribe(file: URL, language: String?, diarize: Bool) async throws -> [CloudSegment] {
            let audio = try AVAudioFile(forReading: file)
            let seconds = Double(audio.length) / audio.processingFormat.sampleRate
            let index = log.withLock { calls in
                calls.append(Call(diarize: diarize, seconds: seconds))
                return calls.count - 1
            }
            return script(index, diarize, seconds)
        }
    }

    /// A stopped recording folder with a stereo CAF of `seconds`, a 440 Hz tone on both channels.
    private struct Recording {
        let root: URL
        let session: RecordingSession

        func remove() { try? FileManager.default.removeItem(at: root) }

        /// Runs the job with `prior` through `cloud:fake` (batch model "batch-model") and returns its transcript.
        func transcribe(
            prior: PriorTranscription, diarize: Bool, asr: FakeTranscriber, diarizer: FakeTranscriber? = nil
        ) async throws -> Transcript {
            let folder = session.folder(in: root)
            let pipeline = TranscriptionPipeline(
                modelStore: ModelStore(root: root.appendingPathComponent("Models")), gate: ModelLoadGate(),
                deferWhenConstrained: false)
            let job = TranscriptionJob(
                conversationID: session.id, folder: folder, processNow: true, diarize: diarize, prior: prior)
            _ = try await pipeline.transcribe(
                job, cloud: CloudEngine(engineID: "cloud:fake", name: "Fake", model: "batch-model", transcriber: asr),
                cloudDiarizer: diarizer.map {
                    CloudEngine(engineID: "cloud:fake-diarizer", name: "Fake", model: "", transcriber: $0)
                })
            return try Transcript.read(from: folder)
        }
    }

    private static func makeRecording(seconds: Int) throws -> Recording {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let session = RecordingSession(state: .stopped, audioFileName: "audio.caf")
        let folder = session.folder(in: root)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try session.write(to: folder)
        let frames = seconds * 16_000
        let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 2, interleaved: false)!
        let file = try AVAudioFile(
            forWriting: folder.appendingPathComponent(session.audioFileName), settings: format.settings,
            commonFormat: .pcmFormatFloat32, interleaved: false)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames))!
        buffer.frameLength = buffer.frameCapacity
        for index in 0..<frames {
            let sample = Float(sin(Double(index) * 2 * .pi * 440 / 16_000) * 0.5)
            buffer.floatChannelData![0][index] = sample
            buffer.floatChannelData![1][index] = sample
        }
        try file.write(from: buffer)
        return Recording(root: root, session: session)
    }

    /// 2 s segments of `label` over `span` (the last one shorter), text `text`.
    private static func pieces(_ label: String, _ span: ClosedRange<Double>, text: String = "stream") -> [CloudSegment] {
        stride(from: span.lowerBound, to: span.upperBound, by: 2).map {
            CloudSegment(start: $0, end: min($0 + 2, span.upperBound), text: text, speaker: label)
        }
    }

    private static func raw(_ segments: [CloudSegment]) -> [RawSegment] {
        segments.map { RawSegment(start: $0.start, end: $0.end, text: $0.text, speaker: $0.speaker) }
    }
}
