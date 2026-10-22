using System.Text;
using EchoDiar.Core;
using EchoDiar.Core.Json;
using EchoDiar.Core.Model;

namespace EchoDiar.Tests.Core;

// Guards the merge rules the transcript relies on and the transcript.json v1 format contract.
public sealed class TranscriptMergerTests
{
    [Fact]
    public void SystemSegmentsTakeTurnWithLargestOverlapAndMicIsMe()
    {
        // Diarizer indices are deliberately not in order of appearance: labels must follow the turns, not the index.
        var turnA = new DiarizationTurn(Start: 0, End: 10, SpeakerIndex: 4);
        var turnB = new DiarizationTurn(Start: 10, End: 20, SpeakerIndex: 1);
        RawSegment[] mic = [new(Start: 0, End: 1, Text: "Good morning")];
        RawSegment[] system =
        [
            new(Start: 2, End: 11, Text: "Mostly in A"), // A: 8 s, B: 1 s
            new(Start: 8, End: 13, Text: "Mostly in B"), // A: 2 s, B: 3 s
        ];

        var segments = TranscriptMerger.Merge(mic, system, [turnA, turnB], []);

        Assert.Equal(["Good morning", "Mostly in A", "Mostly in B"], segments.Select(s => s.Text));
        Assert.Equal(["me", "s1", "s2"], segments.Select(s => s.Speaker));
        Assert.Equal([TranscriptChannel.Mic, TranscriptChannel.System, TranscriptChannel.System], segments.Select(s => s.Channel));
    }

    [Fact]
    public void ProviderSpeakerLabelsBecomeSystemSpeakersInOrderOfFirstAppearance()
    {
        RawSegment[] mic = [new(Start: 0, End: 1, Text: "Hi", Speaker: "A")];
        // Out of order, and B overlaps A: the label decides, not time overlap.
        RawSegment[] system =
        [
            new(Start: 6, End: 8, Text: "Third", Speaker: "A"),
            new(Start: 2, End: 9, Text: "First", Speaker: "B"),
            new(Start: 3, End: 4, Text: "Second", Speaker: "A"),
        ];

        var segments = TranscriptMerger.Merge(mic, system, [], []);

        Assert.Equal(["Hi", "First", "Second", "Third"], segments.Select(s => s.Text));
        Assert.Equal(["me", "s1", "s2", "s2"], segments.Select(s => s.Speaker));
    }

    [Fact]
    public void LanguageMarkersBecomeSegmentLanguagesWithAutoFallback()
    {
        RecordingSession.LanguageMarker[] markers =
        [
            new() { FileTime = 0, Language = "auto" },
            new() { FileTime = 10, Language = "fr" },
        ];
        RawSegment[] system =
        [
            new(Start: 3, End: 5, Text: "Hello", DetectedLanguage: "en"),
            new(Start: 12, End: 14, Text: "Bonjour", DetectedLanguage: "en"),
        ];

        var segments = TranscriptMerger.Merge([], system, [], markers);

        Assert.Equal(["en", "fr"], segments.Select(s => s.Language));
    }

    [Fact]
    public void HandWrittenV1TranscriptDecodes()
    {
        const string json = """
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
        """;

        var transcript = SwiftJson.Deserialize<Transcript>(Encoding.UTF8.GetBytes(json));

        Assert.Equal(1, transcript.Version);
        Assert.Equal(["me", "s1", "s2"], transcript.Segments.Select(s => s.Speaker));
        Assert.Equal([TranscriptChannel.Mic, TranscriptChannel.System, TranscriptChannel.System], transcript.Segments.Select(s => s.Channel));
        Assert.Equal(["en", "fr", "vi"], transcript.Segments.Select(s => s.Language));
        Assert.Equal(0.5, transcript.Segments[0].Start);
        Assert.Equal(2.25, transcript.Segments[0].End);
        Assert.Equal("Xin chào", transcript.Segments[2].Text);
        Assert.Equal("whisperkit-small", transcript.EngineID);
        Assert.Equal("openai_whisper-small_216MB", transcript.ModelID);
        Assert.Equal("parakeet-v3", transcript.EngineFallback);
        Assert.Equal([Transcript.DiarizationMissingNote], transcript.Notes);
        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(782_000_000), transcript.CreatedAt);
        Assert.Equal("Me", transcript.DisplayName("me"));
        Assert.Equal("Speaker 2", transcript.DisplayName("s2"));
        Assert.Equal("Alice", transcript.DisplayName("s1"));
    }
}
