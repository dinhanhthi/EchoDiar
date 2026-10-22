#if DEBUG
using System.Buffers.Binary;
using System.Text;
using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using Microsoft.UI.Dispatching;
using NAudio.Dsp;

namespace EchoDiar.App;

/// <summary>
/// DEBUG headless harness <c>--seed-library [wav path]</c>, the port of macOS <c>DebugLibrarySeed.swift</c>: adds three
/// transcribed conversations to the real library (VM layout, tab and search checks), then exits. Entries have fixed ids;
/// one whose id is already in the library is skipped, and nothing is deleted or overwritten. The wav (16-bit PCM, mono
/// or stereo, any rate) becomes each entry's <c>audio.caf</c> system channel, the mic channel silent; when it is missing
/// the entries are written without audio. Exit code 0, or 1 when a write failed.
/// </summary>
internal static class DebugLibrarySeed
{
    private const string Flag = "--seed-library";
    private const string DefaultWav = @"Z:\Downloads\EchoDiarQA\two-voice.wav";
    private const string AudioFileName = "audio.caf";

    private sealed record Entry(
        Guid Id,
        string Title,
        double HoursAgo,
        double Duration,
        RecordingSession.SourceApp? SourceApp,
        Transcript.Segment[] Segments,
        Dictionary<string, string>? SpeakerNames = null,
        string? Summary = null,
        (string Question, string Answer)? Chat = null);

    /// <summary>Starts the seed when <paramref name="commandLine"/> has <c>--seed-library</c>, and calls <paramref name="exit"/> on this thread when it ends.</summary>
    public static bool TryRun(string[] commandLine, Action exit)
    {
        var index = Array.IndexOf(commandLine, Flag);
        if (index < 0) return false;

        var wav = index + 1 < commandLine.Length && !commandLine[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? commandLine[index + 1]
            : DefaultWav;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(() =>
        {
            var code = 1;
            try
            {
                code = Run(App.Paths.Conversations, wav);
            }
            catch (Exception ex)
            {
                Log($"seed failed: {ex}");
            }
            finally
            {
                dispatcher.TryEnqueue(() =>
                {
                    Environment.ExitCode = code;
                    exit();
                });
            }
        });
        return true;
    }

    private static int Run(string root, string wav)
    {
        short[]? audio = null;
        if (File.Exists(wav))
        {
            try
            {
                audio = ReadWav(wav);
                Log($"audio: {wav} ({audio.Length / SampleRate:0.0} s)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log($"audio: cannot read {wav}: {ex.Message}");
                return 1;
            }
        }
        else
        {
            Log($"no audio: {wav} is missing");
        }

        Directory.CreateDirectory(root);
        var existing = ExistingIds(root);
        var failed = false;
        foreach (var entry in Entries)
        {
            if (existing.Contains(entry.Id))
            {
                Log($"skipped (id exists): {entry.Title}");
                continue;
            }
            try
            {
                if (Write(entry, root, audio) is { } folder) Log($"seeded: {entry.Title} -> {folder}");
                else Log($"skipped (folder exists): {entry.Title}");
            }
            catch (Exception ex)
            {
                failed = true;
                Log($"failed: {entry.Title}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Log(failed ? "seed done with failures" : "seed done");
        return failed ? 1 : 0;
    }

    /// <summary>Ids of the library's folders, from <c>conversation.json</c> else <c>session.json</c>; unreadable folders are ignored.</summary>
    private static HashSet<Guid> ExistingIds(string root)
    {
        var ids = new HashSet<Guid>();
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        foreach (var folder in Directory.EnumerateDirectories(root, "*", options))
        {
            try
            {
                ids.Add(Conversation.Read(folder).Id);
                continue;
            }
            catch (Exception) { }
            try
            {
                ids.Add(RecordingSession.Read(folder).Id);
            }
            catch (Exception) { }
        }
        return ids;
    }

    /// <summary>Writes one entry into a new folder; null when that folder already exists.</summary>
    private static string? Write(Entry entry, string root, short[]? audio)
    {
        var session = new RecordingSession
        {
            Id = entry.Id,
            State = SessionState.Stopped,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-entry.HoursAgo),
            SourceApps = entry.SourceApp is null ? [] : [entry.SourceApp],
            TapMode = entry.SourceApp is null ? TapMode.Global : TapMode.PerApp,
            AudioFileName = AudioFileName,
        };
        var folder = session.Folder(root);
        if (Directory.Exists(folder)) return null;
        session.Write(folder);
        if (audio is not null) WriteCaf(Path.Combine(folder, AudioFileName), audio);
        new Transcript { Segments = [.. entry.Segments], SpeakerNames = entry.SpeakerNames ?? [] }.Write(folder);
        var conversation = new Conversation
        {
            Id = session.Id,
            Title = entry.Title,
            CustomTitle = true,
            CreatedAt = session.StartedAt,
            Duration = audio is null ? entry.Duration : audio.Length / SampleRate,
            SourceAppName = entry.SourceApp?.Name,
            SourceBundleID = entry.SourceApp?.BundleIDs.FirstOrDefault(),
            Status = ConversationStatus.Transcribed,
            HasTranscript = true,
            HasSummary = entry.Summary is not null,
            Folder = folder,
        };
        conversation.Write();
        if (entry.Summary is { } summary) conversation.WriteSummary(summary);
        if (entry.Chat is { } chat)
        {
            var now = DateTimeOffset.UtcNow;
            new ChatLog
            {
                Messages =
                [
                    new() { Role = ChatRole.User, Text = chat.Question, Timestamp = now },
                    new() { Role = ChatRole.Assistant, Text = chat.Answer, Timestamp = now },
                ],
            }.Write(folder);
        }
        conversation = Conversation.Read(folder);
        conversation.SizeBytes = LibraryStore.SizeOnDisk(folder);
        conversation.Write();
        return folder;
    }

    // Audio

    private const double SampleRate = ChannelWriter.SampleRate;

    /// <summary>A 2-channel Int16 CAF: ch0 (mic) silent, ch1 (system) <paramref name="system"/>.</summary>
    private static void WriteCaf(string path, short[] system)
    {
        using var writer = new CafFile.Writer(path, SampleRate, ChannelWriter.ChannelCount, CafFile.SampleFormat.Int16);
        var block = new short[8_192 * 2];
        for (var done = 0; done < system.Length;)
        {
            var n = Math.Min(block.Length / 2, system.Length - done);
            for (var i = 0; i < n; i++)
            {
                block[2 * i] = 0;
                block[2 * i + 1] = system[done + i];
            }
            writer.Append(block.AsSpan(0, 2 * n));
            done += n;
        }
    }

    /// <summary>A RIFF WAVE file of 16-bit PCM (mono or stereo, any rate) as 16 kHz mono; stereo is averaged.</summary>
    private static short[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("not a RIFF WAVE file");
        }
        int channels = 0, rate = 0, bits = 0;
        ReadOnlySpan<byte> data = default;
        var hasData = false;
        for (var offset = 12; offset + 8 <= bytes.Length;)
        {
            var id = Encoding.ASCII.GetString(bytes, offset, 4);
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            var body = offset + 8;
            var available = (int)Math.Min(size, bytes.Length - body);
            if (id == "fmt " && available >= 16)
            {
                var fmt = bytes.AsSpan(body, available);
                var format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                // WAVE_FORMAT_EXTENSIBLE: the sub-format GUID starts with the format tag.
                if (format == 0xFFFE && available >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
                if (format != 1) throw new InvalidDataException($"format {format} is not PCM");
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
            }
            else if (id == "data")
            {
                data = bytes.AsSpan(body, available);
                hasData = true;
                break;
            }
            offset = (int)Math.Min(bytes.Length, body + size + (size & 1));
        }
        if (!hasData || channels == 0) throw new InvalidDataException("no fmt or data chunk");
        if (bits != 16 || channels is not (1 or 2) || rate <= 0)
        {
            throw new InvalidDataException($"{bits}-bit, {channels} channels, {rate} Hz is not 16-bit mono or stereo PCM");
        }

        var frames = data.Length / (2 * channels);
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += BinaryPrimitives.ReadInt16LittleEndian(data[(2 * (i * channels + c))..]);
            mono[i] = sum / channels / 32768f;
        }
        var resampled = rate == (int)SampleRate ? mono : Resample(mono, rate);
        var samples = new short[resampled.Length];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)Math.Clamp(MathF.Round(resampled[i] * 32768f), short.MinValue, short.MaxValue);
        return samples;
    }

    /// <summary>To 16 kHz with the windowed-sinc resampler <see cref="ChannelWriter"/> uses, in the same mode.</summary>
    private static float[] Resample(float[] samples, int rate)
    {
        const int chunk = 8_192;
        var resampler = new WdlResampler();
        resampler.SetMode(false, 0, true);
        resampler.SetFeedMode(true);
        resampler.SetRates(rate, SampleRate);
        var output = new float[(int)Math.Ceiling(chunk * SampleRate / rate) + 64];
        var result = new List<float>((int)(samples.Length * SampleRate / rate) + 64);
        for (var done = 0; done < samples.Length;)
        {
            var n = Math.Min(chunk, samples.Length - done);
            resampler.ResamplePrepare(n, 1, out var input);
            samples.AsSpan(done, n).CopyTo(input);
            var produced = resampler.ResampleOut(output, n, output.Length, 1);
            result.AddRange(output.AsSpan(0, produced));
            done += n;
        }
        resampler.ResamplePrepare(1, 1, out _);
        result.AddRange(output.AsSpan(0, resampler.ResampleOut(output, 0, output.Length, 1)));
        return [.. result];
    }

    private static void Log(string message)
    {
        Console.WriteLine($"EchoDiar seed-library: {message}");
        System.Diagnostics.Debug.WriteLine($"EchoDiar seed-library: {message}");
    }

    // Fixtures (data, not UI: they stay as the macOS seed has them)

    private static Transcript.Segment Me(double start, double end, string text, string language = "en") => new()
    {
        Start = start, End = end, Channel = TranscriptChannel.Mic, Speaker = Transcript.Speaker.Me, Text = text, Language = language,
    };

    private static Transcript.Segment Other(double start, double end, string text, string language = "en") => new()
    {
        Start = start, End = end, Channel = TranscriptChannel.System, Speaker = Transcript.Speaker.System(1), Text = text, Language = language,
    };

    // Windows records the exe name where macOS records the bundle id (RecordingEngine, AudioProcessGrouping).
    private static RecordingSession.SourceApp Source(string name, string exe) => new() { Name = name, BundleIDs = [exe] };

    private static readonly Entry[] Entries =
    [
        new(
            Id: new Guid("5eed0001-0000-4000-8000-000000000001"),
            Title: "Weekly sync with Lan",
            HoursAgo: 3,
            Duration: 1_712,
            SourceApp: Source("Zoom", "zoom.exe"),
            Segments:
            [
                Other(0, 6.5, "Hi, can you hear me? I just joined from the other room."),
                Me(7, 14, "Yes, loud and clear. Let's go through the release checklist first."),
                Other(15, 31, "Sure. The build is green, the notarization passed and the updater feed is ready."),
                Me(32, 45, "Great. What about the translations, are they all in?"),
                Other(46, 62, "Mình nghĩ bản tiếng Việt nên ra trước cuối tháng, phần còn lại đã xong.", "vi"),
                Me(63, 80, "Okay, then we ship English first and add Vietnamese in the next update."),
                Other(81, 95, "Agreed. I'll send the notes to the team after the call."),
            ],
            SpeakerNames: new() { ["s1"] = "Lan" },
            Summary: "## Summary\n"
                + "Release checklist review: build, notarization and update feed are ready.\n"
                + "\n"
                + "## Decisions\n"
                + "- Ship English first; the Vietnamese translation follows in the next update.\n"
                + "\n"
                + "## Action items\n"
                + "- Lan sends the meeting notes to the team.",
            Chat: (
                "When does the Vietnamese translation ship?",
                "In the next update, before the end of the month. English ships first.")),
        new(
            Id: new Guid("5eed0002-0000-4000-8000-000000000002"),
            Title: "Quarterly planning review with the product, design and engineering leads about the roadmap, "
                + "hiring plan, budget for the next two quarters and the migration of the legacy sync service",
            HoursAgo: 27,
            Duration: 3_245,
            SourceApp: Source("Microsoft Teams", "ms-teams.exe"),
            Segments:
            [
                Me(0, 12, "Thanks for joining. We have a lot to cover, so let's start with the roadmap."),
                Other(13, 40, "The sync service migration is the biggest item. We need two more engineers for it."),
                Me(41, 58, "Can we split it into phases so the first part lands this quarter?"),
                Other(59, 85, "Yes, phase one moves the read path, phase two the writes. Each takes about six weeks."),
                Me(86, 101, "Then let's plan phase one now and revisit the budget for phase two in a month."),
            ],
            Summary: "## Summary\n"
                + "Roadmap and hiring review. The legacy sync service migration is split in two phases.\n"
                + "\n"
                + "## Decisions\n"
                + "- Phase one (read path) starts this quarter.\n"
                + "- The phase two budget is reviewed in a month.",
            Chat: (
                "How long does each migration phase take?",
                "About six weeks each: phase one moves the read path, phase two the writes.")),
        new(
            Id: new Guid("5eed0003-0000-4000-8000-000000000003"),
            Title: "Quick call about the invoice",
            HoursAgo: 75,
            Duration: 214,
            SourceApp: null,
            Segments:
            [
                Other(0, 9, "Hello, I'm calling about the invoice from last week."),
                Me(10, 21, "Yes, I saw it. The amount looks right, I'll pay it tomorrow."),
                Other(22, 30, "Perfect, thank you. Have a good day."),
            ]),
    ];
}
#endif
