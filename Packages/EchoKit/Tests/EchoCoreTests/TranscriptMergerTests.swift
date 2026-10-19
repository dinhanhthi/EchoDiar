import EchoCore
import Foundation
import Testing

/// Guards the merge rules the transcript relies on and the transcript.json v1 format contract.
struct TranscriptMergerTests {
    @Test func systemSegmentsTakeTurnWithLargestOverlapAndMicIsMe() {
        // Diarizer indices are deliberately not in order of appearance: labels must follow the turns, not the index.
        let turnA = DiarizationTurn(start: 0, end: 10, speakerIndex: 4)
        let turnB = DiarizationTurn(start: 10, end: 20, speakerIndex: 1)
        let mic = [RawSegment(start: 0, end: 1, text: "Good morning")]
        let system = [
            RawSegment(start: 2, end: 11, text: "Mostly in A"),   // A: 8 s, B: 1 s
            RawSegment(start: 8, end: 13, text: "Mostly in B"),   // A: 2 s, B: 3 s
        ]

        let segments = TranscriptMerger.merge(mic: mic, system: system, turns: [turnA, turnB], languageMarkers: [])

        #expect(segments.map(\.text) == ["Good morning", "Mostly in A", "Mostly in B"])
        #expect(segments.map(\.speaker) == ["me", "s1", "s2"])
        #expect(segments.map(\.channel) == [.mic, .system, .system])
    }

    @Test func providerSpeakerLabelsBecomeSystemSpeakersInOrderOfFirstAppearance() {
        let mic = [RawSegment(start: 0, end: 1, text: "Hi", speaker: "A")]
        // Out of order, and B overlaps A: the label decides, not time overlap.
        let system = [
            RawSegment(start: 6, end: 8, text: "Third", speaker: "A"),
            RawSegment(start: 2, end: 9, text: "First", speaker: "B"),
            RawSegment(start: 3, end: 4, text: "Second", speaker: "A"),
        ]

        let segments = TranscriptMerger.merge(mic: mic, system: system, turns: [], languageMarkers: [])

        #expect(segments.map(\.text) == ["Hi", "First", "Second", "Third"])
        #expect(segments.map(\.speaker) == ["me", "s1", "s2", "s2"])
    }

    @Test func languageMarkersBecomeSegmentLanguagesWithAutoFallback() {
        let markers = [
            RecordingSession.LanguageMarker(fileTime: 0, language: "auto"),
            RecordingSession.LanguageMarker(fileTime: 10, language: "fr"),
        ]
        let system = [
            RawSegment(start: 3, end: 5, text: "Hello", detectedLanguage: "en"),
            RawSegment(start: 12, end: 14, text: "Bonjour", detectedLanguage: "en"),
        ]

        let segments = TranscriptMerger.merge(mic: [], system: system, turns: [], languageMarkers: markers)

        #expect(segments.map(\.language) == ["en", "fr"])
    }

    @Test func handWrittenV1TranscriptDecodes() throws {
        let json = #"""
        {
          "version": 1,
          "segments": [
            {
              "id": "6F1D2C3B-0A4E-4B5F-9C8D-7E6F5A4B3C2D",
              "start": 0.5,
              "end": 2.25,
              "channel": "mic",
              "speaker": "me",
              "text": "Hi everyone",
              "language": "en"
            },
            {
              "id": "1A2B3C4D-5E6F-4A7B-8C9D-0E1F2A3B4C5D",
              "start": 2.5,
              "end": 4,
              "channel": "system",
              "speaker": "s1",
              "text": "Bonjour",
              "language": "fr"
            },
            {
              "id": "9F8E7D6C-5B4A-4F3E-8D2C-1B0A9F8E7D6C",
              "start": 4.5,
              "end": 6,
              "channel": "system",
              "speaker": "s2",
              "text": "Xin chào",
              "language": "vi"
            }
          ],
          "speakerNames": { "s1": "Alice" },
          "engineID": "whisperkit-small",
          "modelID": "openai_whisper-small_216MB",
          "engineFallback": "parakeet-v3",
          "notes": ["diarization model not installed"],
          "createdAt": 782000000
        }
        """#

        let transcript = try JSONDecoder().decode(Transcript.self, from: Data(json.utf8))

        #expect(transcript.version == 1)
        #expect(transcript.segments.map(\.speaker) == ["me", "s1", "s2"])
        #expect(transcript.segments.map(\.channel) == [.mic, .system, .system])
        #expect(transcript.segments.map(\.language) == ["en", "fr", "vi"])
        #expect(transcript.segments[0].start == 0.5)
        #expect(transcript.segments[0].end == 2.25)
        #expect(transcript.segments[2].text == "Xin chào")
        #expect(transcript.engineID == "whisperkit-small")
        #expect(transcript.modelID == "openai_whisper-small_216MB")
        #expect(transcript.engineFallback == "parakeet-v3")
        #expect(transcript.notes == [Transcript.diarizationMissingNote])
        #expect(transcript.createdAt == Date(timeIntervalSinceReferenceDate: 782_000_000))
        #expect(transcript.displayName(for: "me") == "Me")
        #expect(transcript.displayName(for: "s2") == "Speaker 2")
        #expect(transcript.displayName(for: "s1") == "Alice")
    }
}
