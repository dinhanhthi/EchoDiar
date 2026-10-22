using System.Diagnostics;
using System.Text.Json;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;

namespace EchoDiar.Core;

/// <summary>Moves a file or a folder to the Recycle Bin. Implemented by Platform (ShellFiles).</summary>
public interface IFileTrash
{
    void MoveToTrash(string path);
}

/// <summary>An operation named a conversation that is not in the library.</summary>
public sealed class LibraryNotFoundException(Guid id) : Exception($"Conversation {id} is not in the library.")
{
    public Guid Id { get; } = id;
}

/// <summary>
/// The conversation library under the conversations root (<see cref="AppPaths.Conversations"/>). Calls are serialized
/// and run off the caller's thread; nothing watches the disk, so callers <see cref="RefreshAsync"/> after writing a
/// folder themselves.
/// </summary>
public sealed class LibraryStore(string root, IFileTrash trash)
{
    /// <summary>Bytes on disk by kind. <see cref="Models"/> comes from the caller (the model store).</summary>
    public sealed record Usage(long Audio, long TranscriptsAndAI, long Models, IReadOnlyList<ConversationUsage> Conversations)
    {
        public long Total => Audio + TranscriptsAndAI + Models;
    }

    public sealed record ConversationUsage(Guid Id, string Title, long Audio, long Total);

    private static readonly string[] AudioExtensions = [".caf", ".m4a"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<Conversation> _conversations = [];

    public string Root { get; } = root;

    /// <summary>Newest first. A new list after every change, so reading it needs no lock.</summary>
    public IReadOnlyList<Conversation> Conversations => Volatile.Read(ref _conversations);

    /// <summary>
    /// The full list (newest first), raised after every refresh, reload, rename and delete, on a background thread
    /// while the store is busy: handlers must not block on the store (dispatch, then call it).
    /// </summary>
    public event Action<IReadOnlyList<Conversation>>? Changed;

    /// <summary>
    /// Rescans the root. Reads only metadata: <c>conversation.json</c> when present, else derived from
    /// <c>session.json</c>. Never writes. Hidden folders, links, probe sessions, folders where neither file decodes, and
    /// folders that otherwise fail to load, are skipped.
    /// </summary>
    public Task<IReadOnlyList<Conversation>> RefreshAsync() => Run(() =>
    {
        RefreshLocked();
        return Conversations;
    });

    /// <summary>
    /// Re-reads one conversation (a job wrote its folder), without rescanning the root: a folder that is gone or no
    /// longer loads leaves the list. An id not in the list yet falls back to a refresh.
    /// </summary>
    public Task ReloadAsync(Guid id) => Run(() =>
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            RefreshLocked();
            return;
        }
        var list = Conversations.ToList();
        if (TryLoad(list[index].Folder) is { } conversation) list[index] = conversation;
        else list.RemoveAt(index);
        Publish(list);
    });

    /// <summary>
    /// Sets a custom title and writes <c>conversation.json</c>. A blank title is ignored. An existing file is read
    /// fresh and only its title changes, so a status written meanwhile (e.g. by the compressor) survives.
    /// </summary>
    public Task RenameAsync(Guid id, string title) => Run(() =>
    {
        title = title.Trim();
        if (title.Length == 0) return;
        var index = RequireIndex(id);
        var current = Conversations[index];
        var conversation = HasMetadataFile(current.Folder) ? Conversation.Read(current.Folder) : current with { };
        conversation.Title = title;
        conversation.CustomTitle = true;
        conversation.Write();
        conversation.SizeBytes = current.SizeBytes;
        conversation.HasAudio = current.HasAudio;
        Normalize(conversation);
        Replace(index, conversation);
    });

    /// <summary>Moves the conversation folder to the Recycle Bin.</summary>
    public Task DeleteAsync(Guid id) => Run(() => DeleteLocked(id));

    /// <summary>Moves the conversation's audio files to the Recycle Bin and updates its size; transcript, summary and chat stay.</summary>
    public Task DeleteAudioAsync(Guid id) => Run(() =>
    {
        var index = RequireIndex(id);
        var current = Conversations[index];
        foreach (var path in AudioFiles(current.Folder)) trash.MoveToTrash(path);
        var conversation = TryRead(current.Folder) ?? current with { };
        conversation.SizeBytes = SizeOnDisk(current.Folder);
        conversation.HasAudio = false;
        if (HasMetadataFile(current.Folder)) conversation.Write();
        Normalize(conversation);
        Replace(index, conversation);
    });

    /// <summary>
    /// Moves the transcript and its kept versions, summary and its versions, chat and archived chats to the Recycle
    /// Bin; the conversation is ready to transcribe again. Without audio nothing would be left, so the whole
    /// conversation goes instead.
    /// </summary>
    public Task DeleteTranscriptsAndAIAsync(Guid id) => Run(() =>
    {
        var index = RequireIndex(id);
        var current = Conversations[index];
        if (!current.HasAudio)
        {
            DeleteLocked(id);
            return;
        }
        string[] names = [Transcript.FileName, Transcript.VersionsFolderName, Conversation.SummaryFileName,
            Conversation.SummariesFolderName, ChatLog.FileName, ChatArchive.FolderName];
        foreach (var name in names)
        {
            var path = Path.Combine(current.Folder, name);
            if (File.Exists(path) || Directory.Exists(path)) trash.MoveToTrash(path);
        }
        var conversation = TryRead(current.Folder) ?? current with { };
        conversation.HasTranscript = false;
        conversation.HasSummary = false;
        if (conversation.Status is ConversationStatus.Transcribed or ConversationStatus.TranscriptionFailed)
        {
            conversation.Status = ConversationStatus.Ready;
        }
        conversation.SizeBytes = SizeOnDisk(current.Folder);
        conversation.HasAudio = current.HasAudio;
        if (HasMetadataFile(current.Folder)) conversation.Write();
        Replace(index, conversation);
    });

    /// <summary>
    /// Rescans the root, then moves what <see cref="RetentionPolicy.Select"/> picks to the Recycle Bin (the folder, or
    /// the audio files listed again at deletion time); returns the deletions done. <paramref name="isBusy"/> is asked
    /// right before each one (recording, compressing, transcribing or an AI job), which is then skipped; it is awaited
    /// without holding the store, so it may dispatch to the UI thread. A failure is logged and the rest continue.
    /// </summary>
    public async Task<IReadOnlyList<RetentionPolicy.Deletion>> ApplyRetentionAsync(
        AppSettings settings, Func<Guid, Task<bool>> isBusy, DateTimeOffset? now = null)
    {
        var conversations = await RefreshAsync().ConfigureAwait(false);
        var deletions = RetentionPolicy.Select(conversations, now ?? DateTimeOffset.UtcNow, settings);
        List<RetentionPolicy.Deletion> done = [];
        foreach (var deletion in deletions)
        {
            if (await isBusy(deletion.ConversationId).ConfigureAwait(false)) continue;
            try
            {
                if (deletion.Mode == RetentionMode.WholeConversation) await DeleteAsync(deletion.ConversationId).ConfigureAwait(false);
                else await DeleteAudioAsync(deletion.ConversationId).ConfigureAwait(false);
                done.Add(deletion);
            }
            // OperationCanceledException: the user cancelled a Recycle Bin error dialog (ShellFiles).
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or LibraryNotFoundException
                or OperationCanceledException)
            {
                Debug.WriteLine($"EchoDiar: auto-delete failed for {deletion.ConversationId}: {e}");
            }
        }
        return done;
    }

    /// <summary>Measures every conversation folder of the last refresh now. <paramref name="modelBytes"/> sizes the models.</summary>
    public Task<Usage> UsageAsync(Func<long> modelBytes) => Run(() =>
    {
        var sizes = Conversations.Select(c =>
        {
            var audio = AudioFiles(c.Folder).Sum(FileLength);
            return new ConversationUsage(c.Id, c.Title, audio, SizeOnDisk(c.Folder));
        }).ToList();
        var audio = sizes.Sum(s => s.Audio);
        return new Usage(audio, sizes.Sum(s => s.Total) - audio, modelBytes(), sizes.OrderByDescending(s => s.Total).ToList());
    });

    /// <summary>
    /// Audio files of a conversation folder, sorted by name: regular files (not links or other reparse points, not
    /// hidden) with an audio extension, as written by the recorder (CAF) and the compressor (m4a). Not recursive.
    /// </summary>
    public static IReadOnlyList<string> AudioFiles(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(folder, "*", options)
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => IsPlain(new FileInfo(path)))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The conversation folder holds its transcript as a regular file (not a link or other reparse point, not hidden).</summary>
    public static bool HasTranscriptFile(string folder)
    {
        var info = new FileInfo(Path.Combine(folder, Transcript.FileName));
        return info.Exists && IsPlain(info);
    }

    /// <summary>Bytes of every regular file in <paramref name="folder"/>, recursively; links are not followed or counted.</summary>
    public static long SizeOnDisk(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true,
        };
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", options).Where(f => f.LinkTarget is null).Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task<T> Run<T>(Func<T> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(work).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task Run(Action work) => Run(() =>
    {
        work();
        return true;
    });

    private void RefreshLocked()
    {
        List<Conversation> list = [];
        if (Directory.Exists(Root))
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
            foreach (var folder in Directory.EnumerateDirectories(Root, "*", options))
            {
                if (TryLoad(folder) is { } conversation) list.Add(conversation);
            }
        }
        Publish(list.OrderByDescending(c => c.CreatedAt).ToList());
    }

    private void DeleteLocked(Guid id)
    {
        var index = RequireIndex(id);
        trash.MoveToTrash(Conversations[index].Folder);
        var list = Conversations.ToList();
        list.RemoveAt(index);
        Publish(list);
    }

    private int IndexOf(Guid id)
    {
        var list = Conversations;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == id) return i;
        }
        return -1;
    }

    private int RequireIndex(Guid id) => IndexOf(id) is var index and >= 0 ? index : throw new LibraryNotFoundException(id);

    private void Replace(int index, Conversation conversation)
    {
        var list = Conversations.ToList();
        list[index] = conversation;
        Publish(list);
    }

    private void Publish(IReadOnlyList<Conversation> list)
    {
        Volatile.Write(ref _conversations, list);
        Changed?.Invoke(list);
    }

    // One folder that fails to load in an unforeseen way (session.json is untrusted) is skipped, not the whole library.
    private static Conversation? TryLoad(string folder)
    {
        try
        {
            return Load(folder);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException))
        {
            return null;
        }
    }

    private static Conversation? Load(string folder)
    {
        if (!IsPlain(new DirectoryInfo(folder))) return null;
        RecordingSession? session;
        try
        {
            session = RecordingSession.Read(folder);
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            session = null;
        }
        if (session?.Probe == true) return null;
        Conversation conversation;
        if (TryRead(folder) is { } stored)
        {
            conversation = stored;
        }
        else if (session is not null)
        {
            var isRecording = session.State is SessionState.Recording or SessionState.Paused;
            var audio = ConversationFiles.AudioPath(folder, session.AudioFileName);
            var duration = isRecording || audio is null ? 0 : AudioDuration(audio);
            conversation = Conversation.FromSession(session, folder, duration);
        }
        else
        {
            return null;
        }
        conversation.SizeBytes = SizeOnDisk(folder);
        conversation.HasAudio = AudioFiles(folder).Count > 0;
        Normalize(conversation);
        return conversation;
    }

    /// <summary>
    /// <c>conversation.json</c> may come from another device (sync): no transcript unless its file is in the folder.
    /// In memory only, after any write, so the file keeps the synced flag.
    /// </summary>
    private static void Normalize(Conversation conversation) =>
        conversation.HasTranscript &= HasTranscriptFile(conversation.Folder);

    private static Conversation? TryRead(string folder)
    {
        try
        {
            return Conversation.Read(folder);
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return null;
        }
    }

    /// <summary>
    /// Seconds of audio in a CAF file; 0 when it is missing, not a regular file, empty or unreadable. Other formats
    /// (m4a) read as 0 until Core has a decoder (Phase 14); the compressor writes <c>conversation.json</c> with the
    /// duration anyway.
    /// </summary>
    private static double AudioDuration(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".caf", StringComparison.OrdinalIgnoreCase)) return 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return 0;
            using var reader = new CafFile.Reader(path);
            return reader.Frames > 0 && reader.SampleRate > 0 ? reader.Frames / reader.SampleRate : 0;
        }
        // NotSupportedException, ArgumentException: a path FileStream rejects (a device, say).
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
            or NotSupportedException or ArgumentException)
        {
            return 0;
        }
    }

    private static bool HasMetadataFile(string folder) => File.Exists(Path.Combine(folder, Conversation.FileName));

    private static bool IsReadFailure(Exception e) => e is IOException or UnauthorizedAccessException or JsonException;

    /// <summary>Not hidden (attribute or a leading dot, as on macOS), not a link or other reparse point.</summary>
    private static bool IsPlain(FileSystemInfo info) =>
        !info.Name.StartsWith('.')
        && (info.Attributes & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) == 0
        && info.LinkTarget is null;

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
