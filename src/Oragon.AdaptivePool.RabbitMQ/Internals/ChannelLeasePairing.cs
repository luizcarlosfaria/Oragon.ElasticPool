using System.Runtime.CompilerServices;
using Oragon.AdaptivePool.Core.Abstractions;
using RabbitMQ.Client;

namespace Oragon.AdaptivePool.RabbitMQ.Internals;

/// <summary>
/// Pairs each <see cref="IChannel"/> handed out by the channel pool with the
/// <see cref="IPoolItem{IConnection}"/> lease it was created from. Backed by
/// <see cref="ConditionalWeakTable{TKey, TValue}"/> so a channel that is GC-collected
/// without being released does not pin its connection lease in memory.
/// </summary>
/// <remarks>
/// CWT key uniqueness — <see cref="ConditionalWeakTable{TKey, TValue}.Add"/> throws
/// <see cref="ArgumentException"/> on duplicate keys. The Factory hook is the SINGLE add
/// site and uses an ownership-transfer pattern to avoid double-add.
/// </remarks>
internal sealed class ChannelLeasePairing
{
    private readonly ConditionalWeakTable<IChannel, IPoolItem<IConnection>> _table = new();

    /// <summary>Registers the pairing. Throws on duplicate keys (CWT contract).</summary>
    public void Add(IChannel channel, IPoolItem<IConnection> lease)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(lease);
        _table.Add(channel, lease);
    }

    /// <summary>Looks up the lease paired to <paramref name="channel"/>.</summary>
    public bool TryGet(IChannel channel, out IPoolItem<IConnection>? lease)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (_table.TryGetValue(channel, out var found))
        {
            lease = found;
            return true;
        }
        lease = null;
        return false;
    }

    /// <summary>Removes the pairing. Returns true if present, false otherwise.</summary>
    public bool TryRemove(IChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return _table.Remove(channel);
    }
}
