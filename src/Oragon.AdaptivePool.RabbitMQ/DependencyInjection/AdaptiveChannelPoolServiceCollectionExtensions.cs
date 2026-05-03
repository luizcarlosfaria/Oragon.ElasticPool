using Microsoft.Extensions.DependencyInjection;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.DependencyInjection;
using Oragon.AdaptivePool.RabbitMQ.Builder;
using Oragon.AdaptivePool.RabbitMQ.Internals;
using RabbitMQ.Client;

namespace Oragon.AdaptivePool.RabbitMQ.DependencyInjection;

/// <summary>
/// DI extension methods for registering an adaptive RabbitMQ channel pool layered over
/// a previously-registered adaptive connection pool.
/// </summary>
public static class AdaptiveChannelPoolServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IAdaptivePool{IChannel}"/> in DI under the given
    /// <paramref name="name"/>. The pool's Factory acquires connections from the inner
    /// pool resolved via <paramref name="connectionPoolName"/> and creates channels on
    /// them via <see cref="IConnection.CreateChannelAsync"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">
    /// The pool name. Use <c>string.Empty</c> for single-pool apps; use distinct names
    /// to register multiple channel pools side by side.
    /// </param>
    /// <param name="connectionPoolName">
    /// Name of the connection pool registered via
    /// <see cref="AdaptiveConnectionPoolServiceCollectionExtensions.AddAdaptiveConnectionPool"/>.
    /// Resolved via <c>sp.GetRequiredKeyedService&lt;IAdaptivePool&lt;IConnection&gt;&gt;(connectionPoolName)</c>.
    /// </param>
    /// <param name="configurePool">
    /// Required callback that configures pool-shape and channel knobs
    /// (<see cref="AdaptiveChannelPoolBuilder.WithBounds"/>,
    /// <see cref="AdaptiveChannelPoolBuilder.WithIdleTimeout"/>,
    /// <see cref="AdaptiveChannelPoolBuilder.WithMaxChannelsPerConnection"/>,
    /// <see cref="AdaptiveChannelPoolBuilder.WithChannelOptions"/>).
    /// </param>
    /// <returns>The service collection (for chaining).</returns>
    /// <remarks>
    /// <para>Sister-library DI conventions: aligned with <c>Oragon.RabbitMQ</c> per RMQ-04.</para>
    /// <para><b>Eager spread (RESEARCH Q2).</b> Each Factory acquire selects a connection
    /// whose live channel count is below
    /// <see cref="AdaptiveChannelPoolBuilder.MaxChannelsPerConnection"/>. If the first
    /// connection acquired from the connection pool is saturated, the Factory keeps
    /// drawing fresh connections from the pool (releasing the saturated leases) until it
    /// finds one with a free slot, up to a safety net of 16 attempts. Default ceiling is
    /// 100 channels/connection — well below the broker's 2047 default channel_max
    /// (Pitfall 11).</para>
    /// <para><b>Lazy cross-pool invalidation (RESEARCH Q1 — ship lazy, validate empirically).</b>
    /// The <c>BeforeUse</c> hook returns Unhealthy when EITHER the channel itself has
    /// closed (<c>IChannel.IsOpen=false</c>) OR the paired connection lease has closed
    /// (<c>IPoolItem&lt;IConnection&gt;.Value.IsOpen=false</c>). No eager event/callback
    /// hook is exposed in v1; integration tests in Plan 03 will validate whether the
    /// lazy probe latency is acceptable, and Phase 4 may revisit if defects surface.</para>
    /// <para><b>Release order (Pitfall E + lifecycle ownership).</b>
    /// <c>IChannel.CloseAsync</c> (close errors swallowed), then <c>IChannel.DisposeAsync</c>,
    /// then <c>tracker.ReleaseSlot</c>, then <c>pairing.TryRemove</c>, finally
    /// <c>IPoolItem&lt;IConnection&gt;.DisposeAsync</c> (returns the connection lease to
    /// the inner pool). The tracker decrement happens BEFORE returning the lease to
    /// avoid a race where a concurrent Factory call sees the connection as still
    /// saturated.</para>
    /// </remarks>
    public static IServiceCollection AddAdaptiveChannelPool(
        this IServiceCollection services,
        string name,
        string connectionPoolName,
        Action<AdaptiveChannelPoolBuilder> configurePool)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(connectionPoolName);
        ArgumentNullException.ThrowIfNull(configurePool);

        // Build the adapter-side builder once at registration to capture pool-shape settings.
        var chBuilder = new AdaptiveChannelPoolBuilder();
        configurePool(chBuilder);

        // Single instance per channel-pool registration: pairing + tracker live alongside the pool.
        var pairing = new ChannelLeasePairing();
        var tracker = new ConnectionChannelTracker();

        services.AddAdaptivePool<IChannel>(name, builder =>
        {
            builder
                .Factory((sp, ct) => CreateChannelWithSpreadAsync(sp, connectionPoolName, chBuilder, pairing, tracker, ct))
                .BeforeUse((ch, _) =>
                {
                    if (!ch.IsOpen)
                        return ValueTask.FromResult(PoolState.Unhealthy);
                    if (pairing.TryGet(ch, out var connLease) && connLease is not null && !connLease.Value.IsOpen)
                        return ValueTask.FromResult(PoolState.Unhealthy);
                    return ValueTask.FromResult(PoolState.Healthy);
                })
                .Check((ch, _) =>
                    ValueTask.FromResult(ch.IsOpen ? PoolState.Healthy : PoolState.Unhealthy))
                .Release(async (ch, ct) =>
                {
                    try
                    {
                        await ch.CloseAsync(ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Swallow close errors — channel is being discarded regardless.
                    }
                    await ch.DisposeAsync().ConfigureAwait(false);

                    if (pairing.TryGet(ch, out var connLease) && connLease is not null)
                    {
                        // Decrement BEFORE returning the lease so a concurrent Factory call
                        // does not see the connection as still-saturated.
                        tracker.ReleaseSlot(connLease.Value);
                        pairing.TryRemove(ch);
                        await connLease.DisposeAsync().ConfigureAwait(false);
                    }
                })
                .WithBounds(chBuilder.MinSize, chBuilder.MaxSize, chBuilder.InitialSize)
                .IdleTimeout(chBuilder.IdleTimeout);
        });

        return services;
    }

    /// <summary>
    /// Eager-spread Factory helper. Acquires connections from the inner pool until one
    /// with a free channel slot is found (up to 16 attempts), then creates a channel on
    /// it, pairs the channel to the lease, and transfers ownership to the pairing table.
    /// Saturated leases are returned to the pool in the finally block.
    /// </summary>
    private static async ValueTask<IChannel> CreateChannelWithSpreadAsync(
        IServiceProvider sp,
        string connectionPoolName,
        AdaptiveChannelPoolBuilder chBuilder,
        ChannelLeasePairing pairing,
        ConnectionChannelTracker tracker,
        CancellationToken ct)
    {
        var connectionPool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(connectionPoolName);

        // T-03-07: bound the retry loop to prevent runaway acquisition if every connection
        // is saturated. In practice 1–2 attempts suffice; the pool's elasticity provides a
        // fresh connection (or surfaces PoolExhaustedException) before we hit the bound.
        const int MaxAttempts = 16;
        List<IPoolItem<IConnection>>? rejected = null;
        IPoolItem<IConnection>? selected = null;
        try
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var lease = await connectionPool.AcquireAsync(ct).ConfigureAwait(false);
                if (tracker.TryAcquireSlot(lease.Value, chBuilder.MaxChannelsPerConnection))
                {
                    selected = lease;
                    break;
                }
                // Saturated — keep it referenced so we don't immediately re-acquire the same one;
                // dispose all rejected leases in the finally block.
                (rejected ??= new()).Add(lease);
            }

            if (selected is null)
                throw new InvalidOperationException(
                    $"AddAdaptiveChannelPool: could not find a connection with a free channel slot in pool '{connectionPoolName}' after {MaxAttempts} attempts. " +
                    "Increase MaxChannelsPerConnection or the connection pool's MaxSize.");

            IChannel channel;
            try
            {
                channel = await selected.Value.CreateChannelAsync(chBuilder.ChannelOptions, ct).ConfigureAwait(false);
            }
            catch
            {
                // Channel creation failed — release the slot and let the finally block return the lease.
                tracker.ReleaseSlot(selected.Value);
                throw;
            }

            pairing.Add(channel, selected);
            // Ownership transferred — the pairing table now holds the lease; do NOT dispose it in the finally.
            selected = null;
            return channel;
        }
        finally
        {
            if (rejected is not null)
            {
                foreach (var r in rejected)
                {
                    try { await r.DisposeAsync().ConfigureAwait(false); }
                    catch { /* don't mask primary exception */ }
                }
            }
            if (selected is not null)
            {
                try { await selected.DisposeAsync().ConfigureAwait(false); }
                catch { /* don't mask primary exception */ }
            }
        }
    }
}
