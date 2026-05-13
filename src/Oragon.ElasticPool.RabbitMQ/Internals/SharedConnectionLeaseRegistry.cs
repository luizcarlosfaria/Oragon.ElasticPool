using Oragon.ElasticPool.Core.Abstractions;
using RabbitMQ.Client;

namespace Oragon.ElasticPool.RabbitMQ.Internals;

/// <summary>
/// Shares retained connection-pool leases across multiple channels created by one
/// channel-pool registration.
/// </summary>
internal sealed class SharedConnectionLeaseRegistry
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private readonly List<Entry> _entries = [];

    public async ValueTask<SharedConnectionLease> AcquireAsync(
        IElasticPool<IConnection> connectionPool,
        int maxChannelsPerConnection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionPool);
        if (maxChannelsPerConnection < 1)
            throw new ArgumentOutOfRangeException(nameof(maxChannelsPerConnection), "maxChannelsPerConnection must be >= 1.");

        while (true)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (TryAcquireExisting_NoLock(maxChannelsPerConnection, out var existing))
                    return existing;
            }
            finally
            {
                _gate.Release();
            }

            await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (TryAcquireExisting_NoLock(maxChannelsPerConnection, out var existing))
                        return existing;
                }
                finally
                {
                    _gate.Release();
                }

                var lease = await connectionPool.AcquireAsync(cancellationToken).ConfigureAwait(false);

                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var newEntry = new Entry(lease) { ChannelCount = 1 };
                    _entries.Add(newEntry);
                    return new SharedConnectionLease(this, newEntry);
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                _createGate.Release();
            }
        }
    }

    internal IConnection ConnectionFor(Entry entry) => entry.Lease.Value;

    internal async ValueTask ReleaseAsync(Entry entry)
    {
        IPoolItem<IConnection>? leaseToReturn = null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_entries.Contains(entry))
                return;

            if (entry.ChannelCount > 0)
                entry.ChannelCount--;

            if (entry.ChannelCount == 0 || !entry.Lease.Value.IsOpen)
            {
                _entries.Remove(entry);
                leaseToReturn = entry.Lease;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (leaseToReturn is not null)
        {
            try
            {
                await leaseToReturn.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Core's lease-return path owns its own diagnostics; never mask channel cleanup.
            }
        }
    }

    private bool TryAcquireExisting_NoLock(int maxChannelsPerConnection, out SharedConnectionLease lease)
    {
        foreach (var entry in _entries)
        {
            if (entry.ChannelCount < maxChannelsPerConnection && entry.Lease.Value.IsOpen)
            {
                entry.ChannelCount++;
                lease = new SharedConnectionLease(this, entry);
                return true;
            }
        }

        lease = null!;
        return false;
    }

    internal sealed class Entry(IPoolItem<IConnection> lease)
    {
        public IPoolItem<IConnection> Lease { get; } = lease;
        public int ChannelCount { get; set; }
    }
}

internal sealed class SharedConnectionLease : IAsyncDisposable
{
    private readonly SharedConnectionLeaseRegistry _registry;
    private readonly SharedConnectionLeaseRegistry.Entry _entry;
    private int _disposed;

    public SharedConnectionLease(SharedConnectionLeaseRegistry registry, SharedConnectionLeaseRegistry.Entry entry)
    {
        _registry = registry;
        _entry = entry;
    }

    public IConnection Value => _registry.ConnectionFor(_entry);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _registry.ReleaseAsync(_entry).ConfigureAwait(false);
    }
}
