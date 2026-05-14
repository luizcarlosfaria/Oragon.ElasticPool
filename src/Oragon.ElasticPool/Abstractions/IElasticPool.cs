namespace Oragon.ElasticPool.Abstractions;

/// <summary>
/// Generic, elastic pool of T. Disposing the pool drains in-flight items and invokes
/// the Release hook on each remaining entry.
/// </summary>
public interface IElasticPool<T> : IDisposable, IAsyncDisposable
    where T : notnull
{
    /// <summary>Configured maximum pool size.</summary>
    int MaxSize { get; }
    /// <summary>Configured minimum pool size (floor maintained by Phase 2 sweep).</summary>
    int MinSize { get; }
    /// <summary>Total live items the pool currently owns (idle + in-use + being-created).</summary>
    int Total { get; }
    /// <summary>Items currently idle in the pool, available for immediate Acquire.</summary>
    int Available { get; }
    /// <summary>Items currently checked out by consumers.</summary>
    int InUse { get; }
    /// <summary>AcquireAsync callers currently waiting for a returned item.</summary>
    int Waiting { get; }

    /// <summary>
    /// Synchronous fast-path: returns immediately if a free item exists.
    /// If no free item is available, throws <see cref="Exceptions.PoolExhaustedException"/>.
    /// NEVER blocks. Use <see cref="AcquireAsync"/> if you want to wait.
    /// </summary>
    IPoolItem<T> Acquire();

    /// <summary>
    /// Asynchronous acquire. Returns immediately if a free item exists; otherwise:
    ///   - WaitBehavior.Wait (default): waits until an item returns to the pool, respecting <paramref name="cancellationToken"/>.
    ///   - WaitBehavior.Throw: throws <see cref="Exceptions.PoolExhaustedException"/> immediately.
    /// Throws <see cref="ObjectDisposedException"/> if the pool has been disposed.
    /// </summary>
    ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the warm-up task. Awaitable for readiness probes; completes when the InitialSize
    /// items have all been created (or when warm-up is canceled by pool dispose).
    /// </summary>
    Task ReadyAsync();
}
