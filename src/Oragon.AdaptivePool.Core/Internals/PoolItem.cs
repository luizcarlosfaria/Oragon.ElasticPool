using Oragon.AdaptivePool.Core.Abstractions;

namespace Oragon.AdaptivePool.Core.Internals;

internal sealed class PoolItem<T> : IPoolItem<T>
    where T : notnull
{
    private readonly AdaptivePool<T> _owner;
    private readonly PoolEntry<T> _entry;
    private int _disposed;

    public PoolItem(AdaptivePool<T> owner, PoolEntry<T> entry)
    {
        _owner = owner;
        _entry = entry;
    }

    public T Value
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(PoolItem<T>));
            return _entry.Item;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        GC.SuppressFinalize(this);
        _owner.ReturnSync(_entry);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        GC.SuppressFinalize(this);
        return _owner.ReturnAsync(_entry);
    }

    ~PoolItem()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _owner.ReturnFromFinalizer(_entry);
        }
        catch
        {
            // Finalizers MUST NOT throw. Swallow per PITFALLS Pitfall 4.
        }
    }
}
