using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;

namespace EchoDiar.Tests.Core;

// Ports of macOS LibraryStoreAudioFilesTests and LibraryStoreReloadTests.
public sealed class LibraryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));

    public LibraryStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // AudioFiles decides what audio-only deletion moves to the Recycle Bin: only the folder's own audio files.
    [Fact]
    public void AudioFilesListsOnlyTheRealAudioFilesOfTheFolder()
    {
        var folder = Path.Combine(_dir, "conversation");
        var outside = Path.Combine(_dir, "outside.m4a");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        foreach (var name in new[] { "audio.m4a", "audio.caf", "transcript.json", "summary.md", "chat.json", ".x.m4a", "sub/y.m4a" })
        {
            File.WriteAllText(Path.Combine(folder, name), "x");
        }
        File.WriteAllText(outside, "x");
        File.CreateSymbolicLink(Path.Combine(folder, "z.m4a"), outside);

        Assert.Equal(["audio.caf", "audio.m4a"], LibraryStore.AudioFiles(folder).Select(Path.GetFileName));
    }

    // ReloadAsync is how a job's step change reaches the library without a rescan: it picks up the folder's new
    // metadata, and drops a folder that is gone.
    [Fact]
    public async Task ReloadsOneConversationAndDropsAGoneFolder()
    {
        Conversation Make(string name, double minutesAgo)
        {
            var folder = Path.Combine(_dir, name);
            Directory.CreateDirectory(folder);
            var conversation = new Conversation
            {
                Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
                Status = ConversationStatus.Ready, Folder = folder,
            };
            conversation.Write();
            return conversation;
        }
        var older = Make("a", 2);
        var newer = Make("b", 1);
        var store = new LibraryStore(_dir, new DeletingTrash());
        await store.RefreshAsync();

        older.Status = ConversationStatus.Transcribing;
        older.Write();
        await store.ReloadAsync(older.Id);
        Assert.Equal([ConversationStatus.Ready, ConversationStatus.Transcribing], store.Conversations.Select(c => c.Status));

        Directory.Delete(newer.Folder, recursive: true);
        await store.ReloadAsync(newer.Id);
        Assert.Equal([older.Id], store.Conversations.Select(c => c.Id));
    }

    // session.json is untrusted: an audio name that Windows resolves to a DOS device (CON, NUL, COM1, even with an
    // extension, inside a folder) or alters (a trailing dot) is not a plain name, and one such folder must not fault
    // the refresh and hide the whole library.
    [Fact]
    public async Task HostileSessionDoesNotHideTheLibrary()
    {
        string[] hostileNames = ["CON.caf", "nul.m4a", "COM1.caf", "audio.caf."];
        foreach (var name in hostileNames.Concat(["PRN", "aux.caf", "LPT¹.caf", "com9.x.caf", "con .caf", "audio.caf "]))
        {
            Assert.Null(ConversationFiles.AudioPath(_dir, name));
        }
        var hostileIds = hostileNames.Select(name =>
        {
            var session = new RecordingSession { State = SessionState.Stopped, AudioFileName = name };
            session.Write(session.Folder(_dir));
            return session.Id;
        }).ToList();
        var normal = new RecordingSession { State = SessionState.Stopped };
        var normalFolder = normal.Folder(_dir);
        normal.Write(normalFolder);
        using (var writer = new CafFile.Writer(Path.Combine(normalFolder, normal.AudioFileName), 16_000, 2, CafFile.SampleFormat.Int16))
        {
            writer.Append(new short[1_600 * 2]);
        }

        var conversations = await new LibraryStore(_dir, new DeletingTrash()).RefreshAsync();

        var listed = Assert.Single(conversations, c => c.Id == normal.Id);
        Assert.Equal(0.1, listed.Duration, precision: 6);
        Assert.True(listed.HasAudio);
        foreach (var id in hostileIds)
        {
            var hostile = Assert.Single(conversations, c => c.Id == id);
            Assert.Equal(0, hostile.Duration);
            Assert.False(hostile.HasAudio);
        }
    }

    private sealed class DeletingTrash : IFileTrash
    {
        public void MoveToTrash(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
    }
}
