import EchoCore
@testable import EchoTranscribe
import Testing

/// Guards "one live line per chunk": the segments Whisper cuts a decoded chunk into are joined into one line (trimmed,
/// single spaces, blank segments skipped), so the live transcript does not show a burst of short lines per chunk; a
/// chunk with no text adds no line.
struct RecordingTranscriberLineTests {
    @Test func aChunksSegmentsMakeOneTrimmedLine() {
        let segments = [
            " parce qu'il y avait les deux", " \n", "il y avait les contraintes techniques ", "donc les clients",
        ]

        #expect(RecordingTranscriber.lineText(Self.raw(segments))
            == "parce qu'il y avait les deux il y avait les contraintes techniques donc les clients")
    }

    @Test func aChunkWithoutTextMakesNoLine() {
        #expect(RecordingTranscriber.lineText([]) == nil)
        #expect(RecordingTranscriber.lineText(Self.raw(["", " ", "\n"])) == nil)
    }

    /// Guards the live Merci regression: a mic line that is a lone "Merci." while others speak is the residual of their
    /// voices on a near-silent mic and is not shown; alone, on the system channel, or with more words it is kept, and
    /// "others speak" is only asked for a mic lone Merci.
    @Test func aLoneMicMerciWhileOthersSpeakIsNotShown() {
        let merci = Self.raw([" Merci. "])

        #expect(RecordingTranscriber.liveLine(merci, mic: true, othersSpeak: { true }) == nil)
        #expect(RecordingTranscriber.liveLine(merci, mic: true, othersSpeak: { false }) == "Merci.")
        #expect(RecordingTranscriber.liveLine(merci, mic: false, othersSpeak: { true }) == "Merci.")
        #expect(RecordingTranscriber.liveLine(Self.raw(["Merci beaucoup."]), mic: true, othersSpeak: { true })
            == "Merci beaucoup.")
        var asked = false
        #expect(RecordingTranscriber.liveLine(Self.raw(["Bonjour."]), mic: true, othersSpeak: {
            asked = true
            return true
        }) == "Bonjour.")
        #expect(!asked)
    }

    /// Guards the tentative text of the open chunks: at most one per speaker, ordered by start; a newer partial replaces
    /// the speaker's text, a partial with no text removes it, and a final clears it only when it is the same chunk.
    @Test func aTentativePerSpeakerIsReplacedThenClearedByItsFinal() {
        typealias Tentative = RecordingTranscriber.Tentative
        let me = Tentative(speaker: .me, text: "On commence la réunion", startSample: 48_000)
        let others = Tentative(speaker: .others, text: "Bonjour", startSample: 32_000)
        var tentative = RecordingTranscriber.tentative([], setting: .others, text: "Bonjour", startSample: 32_000)
        tentative = RecordingTranscriber.tentative(tentative, setting: .me, text: "On commence", startSample: 16_000)
        #expect(tentative == [Tentative(speaker: .me, text: "On commence", startSample: 16_000), others])
        tentative = RecordingTranscriber.tentative(tentative, setting: .me, text: me.text, startSample: 48_000)
        #expect(tentative == [others, me])
        #expect(RecordingTranscriber.tentative(tentative, setting: .others, text: nil, startSample: 64_000) == [me])

        #expect(RecordingTranscriber.tentative(tentative, clearing: .me, startSample: 16_000) == tentative)
        #expect(RecordingTranscriber.tentative(tentative, clearing: .me, startSample: 48_000) == [others])
    }

    private static func raw(_ texts: [String]) -> [RawSegment] {
        texts.enumerated().map { RawSegment(start: Double($0.offset), end: Double($0.offset + 1), text: $0.element) }
    }
}
