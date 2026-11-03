namespace EchoDiar.AI;

/// <summary>The kinds and statuses of an <see cref="AIJobTable{TPayload}"/> entry, outside the generic type.</summary>
public static class AIJobTable
{
    /// <summary>What an AI job does.</summary>
    public enum Kind { Summary, Question, LibraryQuestion, Title }

    /// <summary>Where an AI job is: started and waiting for its first text, or streaming text.</summary>
    public enum Status { Running, Streaming }
}

/// <summary>
/// The running AI jobs, at most one per key (Swift <c>AIJobTable</c>): a key is a conversation id, <c>null</c> is the
/// library chat. Cloud, local-server and CLI jobs run in parallel; in-app model jobs run one at a time through
/// <c>ModelLoadGate</c>, so an in-app job started after another one still in the table waits and reads as queued.
/// Pure state with no I/O and no locking: use it from one thread (the UI thread).
/// </summary>
/// <typeparam name="TPayload">What the app keeps with each job (its text, status line, ...).</typeparam>
public sealed class AIJobTable<TPayload>
{
    /// <summary>One running job.</summary>
    /// <param name="Key">The conversation id, or <c>null</c> for the library chat.</param>
    /// <param name="Kind">What the job does.</param>
    /// <param name="UsesInAppModel">The job runs an in-app model, which waits for <c>ModelLoadGate</c>.</param>
    /// <param name="Order">The start order: a later start has a larger value.</param>
    /// <param name="Status">Whether the job streams text yet.</param>
    /// <param name="Payload">What the app keeps with the job.</param>
    public sealed record Entry(
        Guid? Key, AIJobTable.Kind Kind, bool UsesInAppModel, int Order, AIJobTable.Status Status, TPayload Payload);

    // A handful of entries at most, in start order; a list also takes the null key, which a Dictionary can't.
    private readonly List<Entry> _entries = [];
    private int _nextOrder;

    /// <summary>
    /// Starts a job on <paramref name="key"/>. Returns <c>false</c>, and changes nothing, when that key already has a
    /// job (one job per conversation).
    /// </summary>
    public bool Start(Guid? key, AIJobTable.Kind kind, bool usesInAppModel, TPayload payload)
    {
        if (IndexOf(key) >= 0) return false;
        _entries.Add(new Entry(key, kind, usesInAppModel, _nextOrder++, AIJobTable.Status.Running, payload));
        return true;
    }

    /// <summary>Removes the job on <paramref name="key"/>, if any.</summary>
    public void Finish(Guid? key)
    {
        var index = IndexOf(key);
        if (index >= 0) _entries.RemoveAt(index);
    }

    /// <summary>The job on <paramref name="key"/>, or <c>null</c> when that key is free.</summary>
    public Entry? EntryFor(Guid? key)
    {
        var index = IndexOf(key);
        return index >= 0 ? _entries[index] : null;
    }

    /// <summary>The conversations with a job; the library chat (the <c>null</c> key) is not in it.</summary>
    public IReadOnlySet<Guid> BusyIds => _entries.Where(e => e.Key.HasValue).Select(e => e.Key!.Value).ToHashSet();

    /// <summary>No job is running.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>A job runs, or waits for, an in-app model.</summary>
    public bool HasInAppJob => _entries.Exists(e => e.UsesInAppModel);

    /// <summary>
    /// The job on <paramref name="key"/> waits for the in-app model: it uses one, has no text yet, and an in-app job
    /// started before it is still in the table.
    /// </summary>
    public bool IsQueued(Guid? key) =>
        EntryFor(key) is { UsesInAppModel: true, Status: AIJobTable.Status.Running } entry
        && _entries.Exists(e => e.UsesInAppModel && e.Order < entry.Order);

    /// <summary>The job on <paramref name="key"/>, if any, has its first text.</summary>
    public void MarkStreaming(Guid? key)
    {
        var index = IndexOf(key);
        if (index >= 0) _entries[index] = _entries[index] with { Status = AIJobTable.Status.Streaming };
    }

    private int IndexOf(Guid? key) => _entries.FindIndex(e => e.Key == key);
}
