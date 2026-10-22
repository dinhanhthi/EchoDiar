using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Json;
using EchoDiar.Core.Model;

namespace EchoDiar.Tests.Core;

// File-format contract with macOS: the JSON files of a conversation folder and the version/archive rules.
public sealed class FileFormatTests : IDisposable
{
    private const string WriteFixturesVariable = "ECHODIAR_WRITE_FIXTURES";
    private const string Vietnamese = "Xin chào, cuộc họp";

    // Whole seconds plus 0.25 s: fractional, and exact as a double.
    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 10, 21, 9, 30, 15, TimeSpan.Zero).AddMilliseconds(250);
    private static readonly Guid ConversationId = Guid.Parse("6F1C2A9E-3B4D-4E5F-8A7B-1C2D3E4F5A6B");
    private static readonly Guid ChatId = Guid.Parse("0A1B2C3D-4E5F-4061-8273-94A5B6C7D8E9");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));

    public FileFormatTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void EveryFileKindRoundTripsSortedWithoutNulls()
    {
        AssertRoundTrips(FullConversation());
        AssertRoundTrips(FullTranscript());
        AssertRoundTrips(FullSession());
        AssertRoundTrips(FullChat());
        AssertRoundTrips(FullChatIndex());
        AssertRoundTrips(FullSummaryIndex());

        // Sparse: null optionals must be omitted, not written as null.
        AssertRoundTrips(new Conversation { Id = ConversationId, CreatedAt = At, Status = ConversationStatus.Ready });
        AssertRoundTrips(new Transcript
        {
            Segments = [new Transcript.Segment { Id = ConversationId, Start = 1, End = 2, Channel = TranscriptChannel.System, Text = "Hi" }],
            CreatedAt = At,
        });
        AssertRoundTrips(new RecordingSession
        {
            Id = ConversationId, Pid = 1, StartedAt = At, State = SessionState.Paused, TapMode = TapMode.Global,
            Pauses = [new RecordingSession.PauseInterval { FileTime = 3, Start = At }],
        });
        AssertRoundTrips(new ChatLog { Messages = [new ChatLog.Message { Role = ChatRole.User, Text = "Hi" }] });
        AssertRoundTrips(new SummaryIndex { Summaries = [new SummaryVersion { Number = 1 }] });
    }

    [Fact]
    public void UnknownEnumValuesFallBackToUnknown()
    {
        var conversation = SwiftJson.Deserialize<Conversation>(Encoding.UTF8.GetBytes(
            $$"""{"id":"{{ConversationId}}","createdAt":1.5,"status":"foo"}"""));
        var session = SwiftJson.Deserialize<RecordingSession>(Encoding.UTF8.GetBytes(
            $$"""{"id":"{{ConversationId}}","pid":1,"startedAt":0,"state":"bar"}"""));

        Assert.Equal(ConversationStatus.Unknown, conversation.Status);
        Assert.Equal(SessionState.Unknown, session.State);
        Assert.Equal("unknown", JsonNode.Parse(SwiftJson.Serialize(conversation))!["status"]!.GetValue<string>());
        Assert.Equal("unknown", JsonNode.Parse(SwiftJson.Serialize(session))!["state"]!.GetValue<string>());
    }

    [Fact]
    public void AppleDatesAreNumbersAndIsoDatesHaveNoFraction()
    {
        var json = JsonNode.Parse(SwiftJson.Serialize(Fixtures()))!;
        var appleSeconds = (At - new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
        var iso = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");

        foreach (var apple in new[]
                 {
                     json[Conversation.FileName]!["createdAt"], json[Transcript.FileName]!["createdAt"],
                     json[RecordingSession.FileName]!["startedAt"], json[RecordingSession.FileName]!["pauses"]![0]!["end"],
                 })
        {
            Assert.Equal(JsonValueKind.Number, apple!.GetValueKind());
            Assert.Equal(appleSeconds, apple.GetValue<double>());
        }
        foreach (var isoDate in new[]
                 {
                     json[ChatLog.FileName]!["messages"]![0]!["timestamp"], json[ChatArchive.IndexFileName]!["chats"]![0]!["archivedAt"],
                     json[Conversation.SummaryIndexFileName]!["summaries"]![0]!["createdAt"],
                 })
        {
            Assert.Matches(iso, isoDate!.GetValue<string>());
            Assert.Equal("2026-10-21T09:30:15Z", isoDate.GetValue<string>());
        }
    }

    [Fact]
    public void RetranscribingKeepsTheReplacedTranscript()
    {
        TranscriptOf("First.", "whisper").Write(_dir);
        TranscriptOf("Second.", "parakeet").Write(_dir, keepingPrevious: true);

        Assert.True(File.Exists(Path.Combine(_dir, Transcript.VersionsFolderName, "v1.json")));
        Assert.Equal(["Second."], Transcript.Read(_dir).Segments.Select(s => s.Text));
        var versions = Transcript.ReadVersions(_dir);
        Assert.Equal([1, 2], versions.Select(v => v.Number));
        Assert.Equal(["First.", "Second."], versions.Select(v => v.Transcript.Segments.Single().Text));
        Assert.Equal(["whisper", "parakeet"], versions.Select(v => v.Transcript.EngineID));

        var renamed = Transcript.Read(_dir);
        renamed.SpeakerNames[Transcript.Speaker.Me] = "Thi";
        renamed.Write(_dir);

        Assert.Equal([1, 2], Transcript.ReadVersions(_dir).Select(v => v.Number));
    }

    [Fact]
    public void FirstResummaryKeepsTheSummaryFromBeforeVersions()
    {
        var conversation = new Conversation
        {
            Id = ConversationId, Title = "Weekly sync", CreatedAt = At, Status = ConversationStatus.Transcribed, Folder = _dir,
        };
        conversation.Write();
        File.WriteAllText(Path.Combine(_dir, Conversation.SummaryFileName), "Old.");

        conversation.WriteSummary("New.", provider: "anthropic", model: "claude-sonnet");

        var versions = Conversation.ReadSummaryVersions(_dir);
        Assert.Equal([1, 2], versions.Select(v => v.Number));
        Assert.Equal(["Old.", "New."], versions.Select(v => v.Text));
        Assert.Null(versions[0].Model);
        Assert.Equal("claude-sonnet", versions[1].Model);
        Assert.Equal("Old.", File.ReadAllText(Path.Combine(_dir, Conversation.SummariesFolderName, "v1.md")));
        Assert.Equal("New.", Conversation.ReadSummary(_dir));
        Assert.True(Conversation.Read(_dir).HasSummary);
    }

    [Fact]
    public void ResumeRestoresTheArchivedChatAndArchivesTheOpenOne()
    {
        var reference = new ChatLog.Reference { Number = 1, ConversationID = ConversationId, Title = "Planning" };
        new ChatLog
        {
            Messages =
            [
                new ChatLog.Message { Role = ChatRole.User, Text = "What was decided?" },
                new ChatLog.Message { Role = ChatRole.Assistant, Text = "Ship on Friday.", References = [reference] },
            ],
        }.Write(_dir);
        var first = ChatArchive.Archive(_dir);
        Assert.NotNull(first);
        Assert.True(first.NeedsTitle);
        Assert.Equal("What was decided?", first.Entry.Title);
        Assert.Empty(ChatLog.Read(_dir).Messages);
        WriteChat("Who owns the budget?");

        var open = ChatArchive.Resume(first.Entry.Id, _dir);

        Assert.NotNull(open);
        var resumed = ChatLog.Read(_dir).Messages;
        Assert.Equal(["What was decided?", "Ship on Friday."], resumed.Select(m => m.Text));
        Assert.Null(resumed[0].References);
        Assert.Equal([reference], resumed[1].References!);
        Assert.Equal([open.Entry], ChatArchive.Entries(_dir));
        Assert.Equal("Who owns the budget?", open.Entry.Title);
        Assert.False(File.Exists(Path.Combine(_dir, ChatArchive.FolderName, $"{first.Entry.Id.ToString("D").ToUpperInvariant()}.json")));
    }

    [Fact]
    public void TitledChatKeepsItsTitleAndEmptyChatIsNotArchived()
    {
        WriteChat("What was decided?", "Ship on Friday.");
        var first = ChatArchive.Archive(_dir);
        Assert.NotNull(first);
        ChatArchive.SetTitle("Release date", first.Entry.Id, _dir);

        Assert.Null(ChatArchive.Resume(first.Entry.Id, _dir));
        var again = ChatArchive.Archive(_dir);

        Assert.NotNull(again);
        Assert.False(again.NeedsTitle);
        Assert.Equal("Release date", again.Entry.Title);
        Assert.Null(ChatArchive.Archive(_dir));
        Assert.Equal(["Release date"], ChatArchive.Entries(_dir).Select(e => e.Title));
    }

    // Writes the fixture 3.4's Swift test decodes with the EchoCore types.
    [Fact]
    public void WritesWindowsJsonFixture()
    {
        SkipUnlessWritingFixtures();
        var path = Path.Combine(SourceFixtures(), "windows-written.json");

        SwiftJson.Write(path, Fixtures());

        Assert.Contains(Vietnamese, File.ReadAllText(path, Encoding.UTF8));
    }

    // Writes the CAF 3.4's Swift test opens with AVAudioFile: ch0 a 440 Hz sine at amplitude 0.5, ch1 silence, 1 s.
    [Fact]
    public void WritesWindowsSineCafFixture()
    {
        SkipUnlessWritingFixtures();
        const int rate = 16_000;
        var path = Path.Combine(SourceFixtures(), "windows-sine.caf");
        var samples = new short[rate * 2];
        for (var i = 0; i < rate; i++)
            samples[i * 2] = (short)Math.Round(0.5 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / rate));

        using (var writer = new CafFile.Writer(path, rate, 2, CafFile.SampleFormat.Int16))
        {
            writer.Append(samples);
        }

        using var reader = new CafFile.Reader(path);
        Assert.Equal(rate, reader.Frames);
    }

    private static void AssertRoundTrips<T>(T value)
    {
        var first = SwiftJson.Serialize(value);
        var second = SwiftJson.Serialize(SwiftJson.Deserialize<T>(first));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(first), JsonNode.Parse(second)),
            $"{typeof(T).Name} changed on re-encode:\n{Encoding.UTF8.GetString(first)}\n{Encoding.UTF8.GetString(second)}");
        using var document = JsonDocument.Parse(first);
        AssertSortedWithoutNulls(document.RootElement, typeof(T).Name);
    }

    private static void AssertSortedWithoutNulls(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                Assert.Fail($"null at {path}");
                break;
            case JsonValueKind.Object:
                var names = element.EnumerateObject().Select(p => p.Name).ToList();
                Assert.Equal(names.Order(StringComparer.Ordinal), names);
                foreach (var property in element.EnumerateObject()) AssertSortedWithoutNulls(property.Value, $"{path}.{property.Name}");
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray()) AssertSortedWithoutNulls(item, $"{path}[{index++}]");
                break;
        }
    }

    /// <summary>One value per file kind, by file name, every optional set.</summary>
    private static Dictionary<string, object> Fixtures() => new()
    {
        [Conversation.FileName] = FullConversation(),
        [Transcript.FileName] = FullTranscript(),
        [RecordingSession.FileName] = FullSession(),
        [ChatLog.FileName] = FullChat(),
        [ChatArchive.IndexFileName] = FullChatIndex(),
        [Conversation.SummaryIndexFileName] = FullSummaryIndex(),
    };

    private static Conversation FullConversation() => new()
    {
        Id = ConversationId, Title = Vietnamese, CustomTitle = true, CreatedAt = At, Duration = 61.5,
        SourceAppName = "Zoom", SourceBundleID = "Zoom.exe", Status = ConversationStatus.Transcribed,
        HasTranscript = true, HasSummary = true, SizeBytes = 1_234_567,
    };

    private static Transcript FullTranscript() => new()
    {
        Segments =
        [
            new Transcript.Segment
            {
                Id = Guid.Parse("11111111-2222-4333-8444-555555555555"), Start = 0.5, End = 2.75,
                Channel = TranscriptChannel.Mic, Speaker = Transcript.Speaker.Me, Text = Vietnamese, Language = "vi",
            },
            new Transcript.Segment
            {
                Id = Guid.Parse("66666666-7777-4888-9999-AAAAAAAAAAAA"), Start = 3, End = 4.25,
                Channel = TranscriptChannel.System, Speaker = Transcript.Speaker.System(2), Text = "Hello", Language = "en",
            },
        ],
        SpeakerNames = { ["s2"] = "Lan" },
        EngineID = "whisper", ModelID = "openai_whisper-small", EngineFallback = "parakeet",
        Notes = [Transcript.DiarizationMissingNote],
        CreatedAt = At,
    };

    private static RecordingSession FullSession() => new()
    {
        Id = ConversationId, Pid = 4242, State = SessionState.Stopped, StartedAt = At,
        SourceApps = [new RecordingSession.SourceApp { Name = "Zoom", BundleIDs = ["Zoom.exe"], Pids = [1234, 5678] }],
        TapMode = TapMode.PerApp, GlobalTapFallback = true,
        MicDeviceName = "Microphone (Realtek)", OutputDeviceName = "Speakers (Realtek)",
        Pauses = [new RecordingSession.PauseInterval { FileTime = 12.5, Start = At.AddSeconds(-30), End = At }],
        LanguageMarkers = [new RecordingSession.LanguageMarker { FileTime = 0, Language = "vi" }],
        EndReason = "user", AudioFileName = "audio.caf", Probe = true,
    };

    private static ChatLog FullChat() => new()
    {
        Title = Vietnamese,
        Messages =
        [
            new ChatLog.Message { Role = ChatRole.User, Text = Vietnamese, Timestamp = At },
            new ChatLog.Message
            {
                Role = ChatRole.Assistant, Text = "Ship on Friday [00:05].", Provider = "anthropic", Model = "claude-sonnet-5-5",
                Timestamp = At.AddSeconds(5),
                References = [new ChatLog.Reference { Number = 1, ConversationID = ConversationId, Title = Vietnamese }],
            },
        ],
    };

    private static ChatArchive.Index FullChatIndex() => new()
    {
        Chats = [new ChatArchive.Entry { Id = ChatId, Title = Vietnamese, ArchivedAt = At, MessageCount = 2 }],
    };

    private static SummaryIndex FullSummaryIndex() => new()
    {
        Summaries = [new SummaryVersion { Number = 1, CreatedAt = At, Provider = "anthropic", Model = "claude-sonnet-5-5", Stopped = true }],
    };

    private static Transcript TranscriptOf(string text, string engineID) => new()
    {
        Segments = [new Transcript.Segment { Start = 0, End = 1, Channel = TranscriptChannel.Mic, Speaker = Transcript.Speaker.Me, Text = text }],
        EngineID = engineID,
    };

    private void WriteChat(params string[] texts) =>
        new ChatLog
        {
            Messages = texts.Select((text, i) => new ChatLog.Message { Role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, Text = text }).ToList(),
        }.Write(_dir);

    private static void SkipUnlessWritingFixtures()
    {
        if (Environment.GetEnvironmentVariable(WriteFixturesVariable) != "1")
            Assert.Skip($"Set {WriteFixturesVariable}=1 to write the fixture into the source Fixtures folder.");
    }

    /// <summary>The source <c>Fixtures</c> folder (not bin), so a written fixture can be committed.</summary>
    private static string SourceFixtures([CallerFilePath] string file = "")
    {
        var folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "Fixtures"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
