using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.DependencyInjection;
using Oragon.ElasticPool.RabbitMQ.Builder;
using Oragon.ElasticPool.RabbitMQ.Internals;
using RabbitMQ.Client;

namespace Oragon.ElasticPool.RabbitMQ.DependencyInjection;

/// <summary>
/// DI extension methods for registering an adaptive RabbitMQ channel pool layered over
/// a previously-registered adaptive connection pool.
/// </summary>
public static class ElasticChannelPoolServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IElasticPool{IChannel}"/> in DI under the given
    /// <paramref name="name"/>. The pool's Factory acquires connections from the inner
    /// pool resolved via <paramref name="connectionPoolName"/> and creates channels on
    /// retained shared connection leases via <see cref="IConnection.CreateChannelAsync"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">
    /// The pool name. Use <c>string.Empty</c> for single-pool apps; use distinct names
    /// to register multiple channel pools side by side.
    /// </param>
    /// <param name="connectionPoolName">
    /// Name of the connection pool registered via
    /// <see cref="ElasticConnectionPoolServiceCollectionExtensions.AddElasticConnectionPool"/>.
    /// Resolved via <c>sp.GetRequiredKeyedService&lt;IElasticPool&lt;IConnection&gt;&gt;(connectionPoolName)</c>.
    /// </param>
    /// <param name="configurePool">
    /// Required callback that configures pool-shape and channel knobs
    /// (<see cref="ElasticChannelPoolBuilder.WithBounds"/>,
    /// <see cref="ElasticChannelPoolBuilder.WithIdleTimeout"/>,
    /// <see cref="ElasticChannelPoolBuilder.WithMaxChannelsPerConnection"/>,
    /// <see cref="ElasticChannelPoolBuilder.WithChannelOptions"/>).
    /// </param>
    /// <returns>The service collection (for chaining).</returns>
    /// <remarks>
    /// <para>Sister-library DI conventions: aligned with <c>Oragon.RabbitMQ</c> per RMQ-04.</para>
    /// <para><b>Connection sharing.</b> Each Factory acquire selects a retained connection
    /// whose live channel count is below
    /// <see cref="ElasticChannelPoolBuilder.MaxChannelsPerConnection"/>. If all retained
    /// connections are saturated, the Factory borrows one more connection lease from the
    /// inner pool. Default ceiling is 100 channels/connection — well below the broker's
    /// 2047 default channel_max (Pitfall 11).</para>
    /// <para><b>Lazy cross-pool invalidation (RESEARCH Q1 — ship lazy, validate empirically).</b>
    /// The <c>BeforeUse</c> hook returns Unhealthy when EITHER the channel itself has
    /// closed (<c>IChannel.IsOpen=false</c>) OR the paired shared connection has closed
    /// (<c>IConnection.IsOpen=false</c>). No eager event/callback
    /// hook is exposed in v1; integration tests in Plan 03 will validate whether the
    /// lazy probe latency is acceptable, and Phase 4 may revisit if defects surface.</para>
    /// <para><b>Release order (Pitfall E + lifecycle ownership).</b>
    /// <c>IChannel.CloseAsync</c> (close errors swallowed), then <c>IChannel.DisposeAsync</c>,
    /// then <c>pairing.TryRemove</c>, finally decrements the shared connection lease.
    /// The retained connection lease returns to the inner pool only after the last
    /// associated channel is discarded.</para>
    /// </remarks>
    public static IServiceCollection AddElasticChannelPool(
        this IServiceCollection services,
        string name,
        string connectionPoolName,
        Action<ElasticChannelPoolBuilder> configurePool)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(connectionPoolName);
        ArgumentNullException.ThrowIfNull(configurePool);

        // IN-02: fail-fast on double-registration. See sister extension method
        // AddElasticConnectionPool for rationale (Core's TryAddKeyedSingleton
        // + additive Configure produces a silently-inconsistent registration).
        if (services.Any(d => d.ServiceType == typeof(IElasticPool<IChannel>)
                              && d.ServiceKey is string k && k == name))
        {
            throw new InvalidOperationException(
                $"An adaptive channel pool named '{name}' is already registered. " +
                "Call AddElasticChannelPool exactly once per name.");
        }

        // Build the adapter-side builder once at registration to capture pool-shape settings.
        var chBuilder = new ElasticChannelPoolBuilder();
        configurePool(chBuilder);

        // Single instance per channel-pool registration: pairing + shared connection
        // registry live alongside the pool.
        var pairing = new ChannelLeasePairing();
        var sharedConnections = new SharedConnectionLeaseRegistry();

        // Logger is resolved on the first Factory call (which receives sp) and cached for
        // subsequent Release calls (which do not). Volatile read on a reference type is
        // safe — worst case is a redundant resolve from a second concurrent Factory call.
        ILogger? cachedLogger = null;

        services.AddElasticPool<IChannel>(name, builder =>
        {
            builder
                .Factory((sp, ct) =>
                {
                    if (cachedLogger is null)
                    {
                        cachedLogger = sp.GetService<ILoggerFactory>()
                                          ?.CreateLogger("Oragon.ElasticPool.RabbitMQ")
                                       ?? (ILogger)NullLogger.Instance;
                    }
                    return CreateChannelWithSpreadAsync(sp, connectionPoolName, chBuilder, pairing, sharedConnections, ct);
                })
                .BeforeUse((ch, _) =>
                {
                    if (!ch.IsOpen)
                        return PoolState.Unhealthy;
                    if (pairing.TryGet(ch, out var connLease) && connLease is not null && !connLease.Value.IsOpen)
                        return PoolState.Unhealthy;
                    return PoolState.Healthy;
                })
                .Check((ch, _) => ch.IsOpen ? PoolState.Healthy : PoolState.Unhealthy)
                .Release(async (ch, ct) =>
                {
                    // Release runs after at least one Factory call (an item must exist to be
                    // released). cachedLogger is therefore initialised; fall back to NullLogger
                    // defensively to keep this hook non-throwing.
                    var logger = cachedLogger ?? NullLogger.Instance;

                    try
                    {
                        await ch.CloseAsync(ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Swallow close errors — channel is being discarded regardless.
                    }

                    // CR-01: DisposeAsync was unguarded — a throw silently leaked the
                    // shared lease count, the pairing entry, AND the connection lease (because
                    // Core's Release call sites swallow all hook exceptions, no diagnostic
                    // ever fired). Guard it so the pairing/shared-lease cleanup below
                    // ALWAYS runs.
                    try
                    {
                        await ch.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.ChannelDisposeFailed(name, ex);
                    }

                    if (pairing.TryGet(ch, out var connLease) && connLease is not null)
                    {
                        pairing.TryRemove(ch);
                        try
                        {
                            await connLease.DisposeAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            // Don't mask earlier exceptions — the lease's own dispose path
                            // already logs via Core's diagnostics.
                        }
                    }
                    else
                    {
                        // IN-01: pairing missing on Release — invariant violation. The
                        // shared lease count for the underlying connection cannot be decremented
                        // and the connection lease (if any) is leaked. Should never happen
                        // under normal flow; if it does, log Debug for operator visibility.
                        logger.UnpairedChannelRelease(name);
                    }
                })
                .WithBounds(chBuilder.MinSize, chBuilder.MaxSize, chBuilder.InitialSize)
                .IdleTimeout(chBuilder.IdleTimeout)
                .SweepInterval(chBuilder.SweepInterval)
                .ShrinkOnUtilizationPercent(chBuilder.ShrinkOnUtilizationPercent)
                .ShrinkTargetUtilizationPercent(chBuilder.ShrinkTargetUtilizationPercent)
                .ShrinkBatchSize(chBuilder.ShrinkBatchSize)
                .ShrinkCooldownWindows(chBuilder.ShrinkCooldownWindows);
        });

        return services;
    }

    /// <summary>
    /// Factory helper. Acquires a shared connection lease with a free channel slot, then
    /// creates a channel on it and pairs the channel to that shared lease.
    /// </summary>
    private static async ValueTask<IChannel> CreateChannelWithSpreadAsync(
        IServiceProvider sp,
        string connectionPoolName,
        ElasticChannelPoolBuilder chBuilder,
        ChannelLeasePairing pairing,
        SharedConnectionLeaseRegistry sharedConnections,
        CancellationToken ct)
    {
        var connectionPool = sp.GetRequiredKeyedService<IElasticPool<IConnection>>(connectionPoolName);
        SharedConnectionLease? selected = null;
        try
        {
            selected = await sharedConnections
                .AcquireAsync(connectionPool, chBuilder.MaxChannelsPerConnection, ct)
                .ConfigureAwait(false);

            IChannel channel;
            try
            {
                channel = await selected.Value.CreateChannelAsync(chBuilder.ChannelOptions, ct).ConfigureAwait(false);
            }
            catch
            {
                await selected.DisposeAsync().ConfigureAwait(false);
                selected = null;
                throw;
            }

            pairing.Add(channel, selected);
            // Ownership transferred — the pairing table now holds the shared lease.
            selected = null;
            return channel;
        }
        finally
        {
            if (selected is not null)
            {
                try { await selected.DisposeAsync().ConfigureAwait(false); }
                catch { /* don't mask primary exception */ }
            }
        }
    }
}
