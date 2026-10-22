namespace EchoDiar.Core.Models;

/// <summary>
/// The app-wide "one heavy model at a time" lock, shared by every module that loads an ML model. A holder loads,
/// uses and unloads its model while it holds a <see cref="Lease"/> (or inside <see cref="WithExclusiveModelAsync"/>).
/// </summary>
/// <remarks>
/// Policy: the recording transcriber has priority. A waiting recording transcriber (owner
/// <see cref="Owner.LiveTranscript"/>) is served before any waiting batch job, and a batch job waits for the gate
/// while the recording transcriber holds it (the whole recording). A holder is never interrupted: a batch job
/// already running finishes first, so a recording transcriber started during it waits for its end.
/// Waiters of the same priority are served in FIFO order.
/// </remarks>
public sealed class ModelLoadGate
{
    public enum Owner
    {
        LiveTranscript, TranscriptionJob, Llm,
        /// <summary>A local text embedder building or querying the library index, held per batch.</summary>
        Embedder,
    }

    /// <summary>Holds the gate until disposed; disposing more than once releases once.</summary>
    public sealed class Lease : IDisposable
    {
        private ModelLoadGate? _gate;

        internal Lease(ModelLoadGate gate, Owner owner)
        {
            _gate = gate;
            Owner = owner;
        }

        public Owner Owner { get; }

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private sealed class Waiter(Owner owner)
    {
        public Owner Owner { get; } = owner;
        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Lock _lock = new();
    private readonly List<Waiter> _waiters = [];
    private Owner? _holder;

    /// <summary>
    /// Waits until no other owner holds the gate, then returns its lease. Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled while waiting,
    /// or by the time the gate is granted (the gate is then released at once).
    /// </summary>
    public async Task<Lease> AcquireAsync(Owner owner, CancellationToken cancellationToken = default)
    {
        Waiter? waiter = null;
        lock (_lock)
        {
            if (_holder is null)
            {
                _holder = owner;
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                waiter = new Waiter(owner);
                var first = owner == Owner.LiveTranscript
                    ? _waiters.FindIndex(w => w.Owner != Owner.LiveTranscript)
                    : -1;
                if (first >= 0) _waiters.Insert(first, waiter);
                else _waiters.Add(waiter);
            }
        }

        if (waiter is not null)
        {
            using (cancellationToken.Register(() => CancelWaiter(waiter, cancellationToken)))
            {
                await waiter.Granted.Task.ConfigureAwait(false);
            }
        }

        var lease = new Lease(this, owner);
        if (cancellationToken.IsCancellationRequested)
        {
            lease.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return lease;
    }

    /// <summary>Runs <paramref name="body"/> while holding the gate (Swift <c>withExclusiveModel</c>).</summary>
    public async Task<T> WithExclusiveModelAsync<T>(
        Owner owner, Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(owner, cancellationToken).ConfigureAwait(false);
        return await body(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Hands the gate to the next waiter, if any.</summary>
    /// <remarks>macOS also trims the malloc zones once no model is left; Windows has no such step.</remarks>
    private void Release()
    {
        Waiter next;
        lock (_lock)
        {
            if (_waiters.Count == 0)
            {
                _holder = null;
                return;
            }
            next = _waiters[0];
            _waiters.RemoveAt(0);
            _holder = next.Owner;
        }
        next.Granted.SetResult();
    }

    /// <summary>Drops a waiter that has not been granted the gate yet; a granted one sees the token after its await.</summary>
    private void CancelWaiter(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_waiters.Remove(waiter)) return;
        }
        waiter.Granted.SetCanceled(cancellationToken);
    }
}
