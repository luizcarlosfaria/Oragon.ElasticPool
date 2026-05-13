namespace Oragon.ElasticPool.Core.Abstractions;

/// <summary>
/// Disposable wrapper around a pooled item. Disposing returns the item to the pool.
/// Idempotent: repeat Dispose / DisposeAsync calls are no-ops.
/// </summary>
public interface IPoolItem<out T> : IDisposable, IAsyncDisposable
{
    /// <summary>The pooled instance. Throws <see cref="ObjectDisposedException"/> if already disposed.</summary>
    T Value { get; }
}
