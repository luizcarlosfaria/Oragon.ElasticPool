using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.DependencyInjection;
using Oragon.AdaptivePool.RabbitMQ.Builder;
using Oragon.AdaptivePool.RabbitMQ.Internals;
using RabbitMQ.Client;

namespace Oragon.AdaptivePool.RabbitMQ.DependencyInjection;

/// <summary>
/// DI extension methods for registering an adaptive RabbitMQ connection pool.
/// </summary>
public static class AdaptiveConnectionPoolServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IAdaptivePool{IConnection}"/> in DI under the given
    /// <paramref name="name"/>. The pool produces and recycles RabbitMQ
    /// <see cref="IConnection"/> instances under load using
    /// <see cref="Oragon.AdaptivePool.Core"/>'s elastic engine.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">
    /// The pool name. Use <c>string.Empty</c> for single-pool apps (also resolvable as
    /// non-keyed <see cref="IAdaptivePool{IConnection}"/>); use distinct names to register
    /// multiple connection pools side by side.
    /// </param>
    /// <param name="configureFactory">
    /// Optional callback that configures the underlying <see cref="ConnectionFactory"/>
    /// (host, port, credentials, etc.). May be <c>null</c> when the consumer instead
    /// registers a keyed <see cref="IConnectionFactory"/> singleton or binds
    /// <see cref="Options.AdaptiveConnectionPoolOptions"/> via <c>IOptions</c>.
    /// </param>
    /// <param name="configurePool">
    /// Required callback that configures pool-shape knobs
    /// (<see cref="AdaptiveConnectionPoolBuilder.WithBounds"/>,
    /// <see cref="AdaptiveConnectionPoolBuilder.WithIdleTimeout"/>).
    /// </param>
    /// <returns>The service collection (for chaining).</returns>
    /// <remarks>
    /// <para>Sister-library DI conventions: aligned with <c>Oragon.RabbitMQ</c> per RMQ-04.</para>
    /// <para>The adapter resolves the <see cref="IConnectionFactory"/> in this priority order
    /// (3-mode probe):</para>
    /// <list type="number">
    ///   <item><description>Keyed singleton — <c>sp.GetKeyedService&lt;IConnectionFactory&gt;(name)</c>.</description></item>
    ///   <item><description>Closure — <paramref name="configureFactory"/> runs against a fresh
    ///     <see cref="ConnectionFactory"/>.</description></item>
    ///   <item><description>IOptions — bound from
    ///     <see cref="Options.AdaptiveConnectionPoolOptions"/> by name.</description></item>
    /// </list>
    /// <para><b>AutomaticRecoveryEnabled override (RESEARCH Pitfall A):</b> after the consumer's
    /// <paramref name="configureFactory"/> runs, the adapter forces
    /// <see cref="ConnectionFactory.AutomaticRecoveryEnabled"/> to <c>false</c> at every
    /// connection creation — the pool owns lifecycle and client-side auto-recovery would race
    /// the pool's own discard/replace path. EventId 2001 Warning is emitted on override.</para>
    /// </remarks>
    public static IServiceCollection AddAdaptiveConnectionPool(
        this IServiceCollection services,
        string name,
        Action<ConnectionFactory>? configureFactory,
        Action<AdaptiveConnectionPoolBuilder> configurePool)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configurePool);

        // Build the adapter-side builder once at registration to capture pool-shape settings.
        var poolBuilder = new AdaptiveConnectionPoolBuilder();
        configurePool(poolBuilder);

        services.AddAdaptivePool<IConnection>(name, builder =>
        {
            builder
                .Factory(async (sp, ct) =>
                {
                    var factory = ConnectionFactoryResolver.Resolve(sp, name, configureFactory);
                    var loggerFactory = sp.GetService<ILoggerFactory>();
                    var logger = loggerFactory?.CreateLogger("Oragon.AdaptivePool.RabbitMQ")
                                 ?? (ILogger)NullLogger.Instance;
                    ConnectionFactoryResolver.ForceAutomaticRecoveryDisabled(factory, logger, name);
                    return await factory.CreateConnectionAsync(ct).ConfigureAwait(false);
                })
                .BeforeUse((conn, _) => ValueTask.FromResult(conn.IsOpen ? PoolState.Healthy : PoolState.Unhealthy))
                .Check((conn, _) => ValueTask.FromResult(conn.IsOpen ? PoolState.Healthy : PoolState.Unhealthy))
                .Release(async (conn, ct) =>
                {
                    try
                    {
                        await conn.CloseAsync(ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // RESEARCH Pitfall A: swallow close errors; the pool already considers
                        // the item discarded. The DisposeAsync below releases unmanaged state.
                    }
                    await conn.DisposeAsync().ConfigureAwait(false);
                })
                .WithBounds(poolBuilder.MinSize, poolBuilder.MaxSize, poolBuilder.InitialSize)
                .IdleTimeout(poolBuilder.IdleTimeout);
        });

        return services;
    }
}
