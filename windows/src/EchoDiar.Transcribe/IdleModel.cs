namespace EchoDiar.Transcribe;

/// <summary>
/// The recording transcriber's model, loaded on demand (Swift <c>IdleModel</c>): <see cref="ReleaseIfIdleAsync"/> frees
/// it once it went unused for the idle time (no chunk comes while a recording is paused; the app passes
/// <c>settings.modelIdleMinutes</c>), and the next <see cref="UseAsync{TResult}"/> loads it again.
/// </summary>
/// <remarks>
/// Thread-safe: one lock serializes use, load and release, so the model is never freed while in use.
/// <see cref="DisposeAsync"/> frees it for good (app shutdown).
/// </remarks>
public sealed class IdleModel<TModel> : IAsyncDisposable where TModel : class
{
    private readonly TimeSpan? _idle;
    private readonly Func<CancellationToken, Task<TModel>> _load;
    private readonly Func<TModel, ValueTask> _unload;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TModel? _model;
    private long _lastUse;
    private bool _disposed;

    /// <summary><paramref name="model"/> is already loaded; <paramref name="idle"/> null keeps it until released.</summary>
    public IdleModel(
        TModel model, TimeSpan? idle, Func<CancellationToken, Task<TModel>> load, Func<TModel, ValueTask> unload,
        TimeProvider? time = null)
    {
        _model = model;
        _idle = idle;
        _load = load;
        _unload = unload;
        _time = time ?? TimeProvider.System;
        _lastUse = _time.GetTimestamp();
    }

    /// <summary>
    /// Runs <paramref name="use"/> with the model, loaded again when it was freed. Counts as a use when it starts and
    /// when it ends, so a long decode is not followed by an immediate release.
    /// </summary>
    public async Task<TResult> UseAsync<TResult>(
        Func<TModel, CancellationToken, Task<TResult>> use, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lastUse = _time.GetTimestamp();
            _model ??= await _load(cancellationToken).ConfigureAwait(false);
            try
            {
                return await use(_model, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lastUse = _time.GetTimestamp();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Frees the model once the idle time passed since its last use; true when it did.</summary>
    public async Task<bool> ReleaseIfIdleAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_idle is not { } idle || _model is not { } model || _time.GetElapsedTime(_lastUse) < idle) return false;
            _model = null;
            await _unload(model).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Frees the model now; the next use loads it again.</summary>
    public async Task ReleaseAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReleaseLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Frees the model for good: a later use throws <see cref="ObjectDisposedException"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await ReleaseLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async ValueTask ReleaseLockedAsync()
    {
        if (_model is not { } model) return;
        _model = null;
        await _unload(model).ConfigureAwait(false);
    }
}
