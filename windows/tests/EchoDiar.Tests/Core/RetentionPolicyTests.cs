using EchoDiar.Core;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.Core;

// Port of macOS RetentionPolicyTests: guards auto-delete against data loss. The policy cases are pure (no file is read
// or deleted); the last one runs LibraryStore.ApplyRetentionAsync on real folders.
public sealed class RetentionPolicyTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));

    public RetentionPolicyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SelectsOnlyConversationsOlderThanTheAge()
    {
        var old = Make(ageDays: 31);
        var recent = Make(ageDays: 29);

        var deletions = RetentionPolicy.Select(
            [old, recent], Now, Settings(RetentionMode.WholeConversation, 30), Audio, _ => true);

        var deletion = Assert.Single(deletions);
        Assert.Equal(old.Id, deletion.ConversationId);
        Assert.Equal([old.Folder], deletion.Items);
    }

    // Audio only trashes just what audioFiles lists (tested on a real folder in LibraryStoreTests), never the folder;
    // a conversation with no audio left is skipped.
    [Fact]
    public void AudioOnlyTrashesOnlyTheListedAudioAndSkipsConversationsWithout()
    {
        var withAudio = Make(ageDays: 120);
        var withoutAudio = Make(ageDays: 120);

        var deletions = RetentionPolicy.Select(
            [withAudio, withoutAudio], Now, Settings(RetentionMode.AudioOnly, 90),
            c => c.Id == withAudio.Id ? Audio(c) : [], _ => true);

        var deletion = Assert.Single(deletions);
        Assert.Equal(withAudio.Id, deletion.ConversationId);
        Assert.Equal(RetentionMode.AudioOnly, deletion.Mode);
        Assert.Equal(Audio(withAudio), deletion.Items);
    }

    [Theory]
    [InlineData(ConversationStatus.Recording)]
    [InlineData(ConversationStatus.Compressing)]
    [InlineData(ConversationStatus.Transcribing)]
    [InlineData(ConversationStatus.Ready)]
    [InlineData(ConversationStatus.Recovered)]
    [InlineData(ConversationStatus.CompressionFailed)]
    [InlineData(ConversationStatus.TranscriptionFailed)]
    [InlineData(ConversationStatus.Failed)]
    [InlineData(ConversationStatus.Unknown)]
    public void BusyOrUntranscribedConversationsAreNeverSelected(ConversationStatus status)
    {
        var old = Make(ageDays: 400, status: status);
        foreach (var mode in new[] { RetentionMode.AudioOnly, RetentionMode.WholeConversation })
        {
            Assert.Empty(RetentionPolicy.Select([old], Now, Settings(mode, 1), Audio));
        }
    }

    [Fact]
    public void TranscribedStatusWithoutTranscriptIsNeverSelected()
    {
        var old = Make(ageDays: 400, hasTranscript: false);
        Assert.Empty(RetentionPolicy.Select([old], Now, Settings(RetentionMode.WholeConversation, 1), Audio));
    }

    // conversation.json may come from another device (sync): marked transcribed but without transcript.json on disk
    // (a partial sync, a forged file), the conversation is kept; with the file, it goes.
    [Theory]
    [InlineData(RetentionMode.WholeConversation)]
    [InlineData(RetentionMode.AudioOnly)]
    public void TranscribedWithoutTranscriptFileOnDiskIsNeverSelected(RetentionMode mode)
    {
        var old = Make(ageDays: 400);
        Directory.CreateDirectory(old.Folder);

        Assert.Empty(RetentionPolicy.Select([old], Now, Settings(mode, 1), Audio));

        File.WriteAllText(Path.Combine(old.Folder, Transcript.FileName), "{}");
        Assert.Equal([old.Id], RetentionPolicy.Select([old], Now, Settings(mode, 1), Audio).Select(d => d.ConversationId));
    }

    // macOS applyRetention: a conversation with an active job (isBusy) keeps its files, the others go.
    [Fact]
    public async Task ApplyRetentionSkipsBusyConversations()
    {
        var busy = Make(ageDays: 120);
        var idle = Make(ageDays: 120);
        foreach (var conversation in new[] { busy, idle })
        {
            Directory.CreateDirectory(conversation.Folder);
            conversation.Write();
            File.WriteAllText(Path.Combine(conversation.Folder, "audio.m4a"), "x");
            File.WriteAllText(Path.Combine(conversation.Folder, Transcript.FileName), "{}");
        }
        var store = new LibraryStore(_dir, new DeletingTrash());

        var done = await store.ApplyRetentionAsync(
            Settings(RetentionMode.AudioOnly, 90), id => Task.FromResult(id == busy.Id), Now);

        Assert.Equal([idle.Id], done.Select(d => d.ConversationId));
        Assert.True(File.Exists(Path.Combine(busy.Folder, "audio.m4a")));
        Assert.False(File.Exists(Path.Combine(idle.Folder, "audio.m4a")));
        Assert.False(store.Conversations.Single(c => c.Id == idle.Id).HasAudio);
    }

    private static AppSettings Settings(RetentionMode mode, int days) =>
        new() { AutoDeleteEnabled = true, AutoDeleteAfterDays = days, AutoDeleteMode = mode };

    private Conversation Make(double ageDays, ConversationStatus status = ConversationStatus.Transcribed, bool hasTranscript = true)
    {
        var id = Guid.NewGuid();
        return new Conversation
        {
            Id = id, CreatedAt = Now.AddDays(-ageDays), Status = status, HasTranscript = hasTranscript, HasSummary = true,
            Folder = Path.Combine(_dir, id.ToString()),
        };
    }

    /// <summary>The audio files the policy is told the folder holds.</summary>
    private static IReadOnlyList<string> Audio(Conversation conversation) =>
        [Path.Combine(conversation.Folder, "audio.m4a"), Path.Combine(conversation.Folder, "audio.caf")];

    private sealed class DeletingTrash : IFileTrash
    {
        public void MoveToTrash(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
    }
}
