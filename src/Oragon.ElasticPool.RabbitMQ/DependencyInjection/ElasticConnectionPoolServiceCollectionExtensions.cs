using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.Core.DependencyInjection;
using Oragon.ElasticPool.RabbitMQ.Builder;
using Oragon.ElasticPool.RabbitMQ.Internals;
using RabbitMQ.Client;

namespace Oragon.ElasticPool.RabbitMQ.DependencyInjection;

/// <summary>
/// DI extension methods for registering an adaptive RabbitMQ connection pool.
/// </summary>
public static class ElasticConnectionPoolServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IElasticPool{IConnection}"/> in DI under the given
    /// <paramref name="name"/>. The pool produces and recycles RabbitMQ
    /// <see cref="IConnection"/> instances under load using
    /// <see cref="Oragon.ElasticPool.Core"/>'s elastic engine.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">
    /// The pool name. Use <c>string.Empty</c> for single-pool apps (also resolvable as
    /// non-keyed <see cref="IElasticPool{IConnection}"/>); use distinct names to register
    /// multiple connection pools side by side.
    /// </param>
    /// <param name="configureFactory">
    /// Optional callback that configures the underlying <see cref="ConnectionFactory"/>
    /// (host, port, credentials, etc.). May be <c>null</c> when the consumer instead
    /// registers a keyed <see cref="IConnectionFactory"/> singleton or binds
    /// <see cref="Options.ElasticConnectionPoolOptions"/> via <c>IOptions</c>.
    /// </param>
    /// <param name="configurePool">
    /// Required callback that configures pool-shape knobs
    /// (<see cref="ElasticConnectionPoolBuilder.WithBounds"/>,
    /// <see cref="ElasticConnectionPoolBuilder.WithIdleTimeout"/>).
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
    ///     <see cref="Options.ElasticConnectionPoolOptions"/> by name.</description></item>
    /// </list>
    /// <para><b>AutomaticRecoveryEnabled override (RESEARCH Pitfall A):</b> after the consumer's
    /// <paramref name="configureFactory"/> runs, the adapter forces
    /// <see cref="ConnectionFactory.AutomaticRecoveryEnabled"/> to <c>false</c> at every
    /// connection creation — the pool owns lifecycle and client-side auto-recovery would race
    /// the pool's own discard/replace path. EventId 2001 Warning is emitted on override.</para>
    /// </remarks>
    public static IServiceCollection AddElasticConnectionPool(
        this IServiceCollection services,
        string name,
        Action<ConnectionFactory>? configureFactory,
        Action<ElasticConnectionPoolBuilder> configurePool)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configurePool);

        // IN-02: fail-fast on double-registration. Core's AddElasticPool uses
        // TryAddKeyedSingleton (first-wins) for the pool but additive Configure
        // (last-wins) for the builder, so a second AddElasticConnectionPool with
        // the same name silently produces an inconsistent configuration: the
        // outer DI singleton from the first call but the builder lambda from
        // the second. Throw before any registration happens to make the bug
        // loud rather than subtle.
        if (services.Any(d => d.ServiceType == typeof(IElasticPool<IConnection>)
                              && d.ServiceKey is string k && k == name))
        {
            throw new InvalidOperationException(
                $"An adaptive connection pool named '{name}' is already registered. " +
                "Call AddElasticConnectionPool exactly once per name.");
        }

        // Build the adapter-side builder once at registration to capture pool-shape settings.
        var poolBuilder = new ElasticConnectionPoolBuilder();
        configurePool(poolBuilder);

        // CR-01 mirror: cache a logger from the first Factory call so the Release hook
        // can surface DisposeAsync failures even though it doesn't receive an IServiceProvider.
        ILogger? cachedLogger = null;

        services.AddElasticPool<IConnection>(name, builder =>
        {
            builder
                .Factory(async (sp, ct) =>
                {
                    var factory = ConnectionFactoryResolver.Resolve(sp, name, configureFactory);
                    var loggerFactory = sp.GetService<ILoggerFactory>();
                    var logger = loggerFactory?.CreateLogger("Oragon.ElasticPool.RabbitMQ")
                                 ?? (ILogger)NullLogger.Instance;
                    cachedLogger ??= logger;
                    // WR-02: returns a clone (not the shared instance) when override fires,
                    // so the consumer's factory is never mutated.
                    var safeFactory = ConnectionFactoryResolver.ApplyAutomaticRecoveryOverride(factory, logger, name);
                    return await safeFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                })
                .BeforeUse((conn, _) => conn.IsOpen ? PoolState.Healthy : PoolState.Unhealthy)
                .Check((conn, _) => conn.IsOpen ? PoolState.Healthy : PoolState.Unhealthy)
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
                    // CR-01 (lower-impact mirror): guard DisposeAsync so a throw cannot
                    // escape the Release hook. No tracker/pairing to corrupt here, but a
                    // raised exception is still wrapped + swallowed by Core's call sites
                    // with zero observability — log via EventId 2004 for parity.
                    try
                    {
                        await conn.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        var disposeLogger = cachedLogger ?? NullLogger.Instance;
                        disposeLogger.ConnectionDisposeFailed(name, ex);
                    }
                })
                .WithBounds(poolBuilder.MinSize, poolBuilder.MaxSize, poolBuilder.InitialSize)
                .IdleTimeout(poolBuilder.IdleTimeout)
                .SweepInterval(poolBuilder.SweepInterval)
                .ShrinkOnUtilizationPercent(poolBuilder.ShrinkOnUtilizationPercent)
                .ShrinkTargetUtilizationPercent(poolBuilder.ShrinkTargetUtilizationPercent)
                .ShrinkBatchSize(poolBuilder.ShrinkBatchSize)
                .ShrinkCooldownWindows(poolBuilder.ShrinkCooldownWindows);
        });

        return services;
    }
}
