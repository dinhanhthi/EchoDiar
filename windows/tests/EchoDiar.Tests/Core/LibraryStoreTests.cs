using EchoDiar.Core;
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

    private sealed class DeletingTrash : IFileTrash
    {
        public void MoveToTrash(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
    }
}
