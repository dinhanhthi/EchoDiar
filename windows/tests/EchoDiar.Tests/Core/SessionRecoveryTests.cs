using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;

namespace EchoDiar.Tests.Core;

// Guards crash recovery. Every test uses its own temporary root, never the real library.
public sealed class SessionRecoveryTests : IDisposable
{
    private const int DeadPid = 999_999;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));

    public SessionRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A real crash cannot be simulated in-process, so recovery runs while the writer still holds the file open
    // (CAF data size still -1) after a flush to disk; the writer is disposed only afterwards.
    [Fact]
    public void UnclosedFlushedCafIsRecovered()
    {
        var session = new RecordingSession { Pid = DeadPid, State = SessionState.Recording };
        var folder = session.Folder(_root);
        session.Write(folder);
        var audioPath = Path.Combine(folder, session.AudioFileName);

        const int frames = 8_000;
        using var writer = new CafFile.Writer(audioPath, 16_000, 2, CafFile.SampleFormat.Int16);
        var samples = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var value = (short)(Math.Sin(i * 2 * Math.PI * 440 / 16_000) * 0.5 * short.MaxValue);
            samples[2 * i] = value;
            samples[2 * i + 1] = value;
        }
        writer.Append(samples);
        writer.Flush();

        var results = SessionRecovery.Recover(_root, (_, _) => false, Environment.ProcessId);

        using (var reader = new CafFile.Reader(audioPath))
        {
            Assert.Equal(frames, reader.Frames);
            Assert.Equal(2, reader.Channels);
        }
        var result = Assert.Single(results);
        Assert.Equal(SessionState.Recovered, result.State);
        Assert.True(result.Duration > 0);
        Assert.Equal(SessionState.Recovered, RecordingSession.Read(folder).State);
    }

    [Fact]
    public void MissingCafFails()
    {
        var session = new RecordingSession { Pid = DeadPid, State = SessionState.Recording };
        var folder = session.Folder(_root);
        session.Write(folder);

        var results = SessionRecovery.Recover(_root, (_, _) => false, Environment.ProcessId);

        Assert.Equal([SessionState.Failed], results.Select(r => r.State));
        Assert.Equal(0, results[0].Duration);
        Assert.Equal(SessionState.Failed, RecordingSession.Read(folder).State);
    }

    [Fact]
    public void LiveOwnerIsUntouched()
    {
        var session = new RecordingSession { Pid = DeadPid, State = SessionState.Recording };
        var folder = session.Folder(_root);
        session.Write(folder);
        var before = File.ReadAllBytes(Path.Combine(folder, RecordingSession.FileName));

        var results = SessionRecovery.Recover(_root, (_, _) => true, Environment.ProcessId);

        Assert.Empty(results);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(folder, RecordingSession.FileName)));
    }

    // Recovery runs in a task nothing awaits, so it can meet a session this process has just started.
    [Fact]
    public void SessionOfThisProcessIsUntouched()
    {
        var session = new RecordingSession { Pid = Environment.ProcessId, State = SessionState.Recording };
        var folder = session.Folder(_root);
        session.Write(folder);
        var before = File.ReadAllBytes(Path.Combine(folder, RecordingSession.FileName));

        var results = SessionRecovery.Recover(_root, (_, _) => false, Environment.ProcessId);

        Assert.Empty(results);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(folder, RecordingSession.FileName)));
    }
}
