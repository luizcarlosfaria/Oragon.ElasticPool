using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oragon.AdaptivePool.RabbitMQ.Options;
using RabbitMQ.Client;

namespace Oragon.AdaptivePool.RabbitMQ.Internals;

/// <summary>
/// 3-mode probe to obtain an <see cref="IConnectionFactory"/> for an adaptive RabbitMQ
/// connection pool. Probe order:
/// <list type="number">
///   <item><description>Keyed singleton: <c>sp.GetKeyedService&lt;IConnectionFactory&gt;(name)</c>.</description></item>
///   <item><description>Closure: when <paramref name="configureFactory"/> is non-null, instantiate
///     <see cref="ConnectionFactory"/> and run the closure on it.</description></item>
///   <item><description>IOptions: read <see cref="AdaptiveConnectionPoolOptions"/> via
///     <c>IOptionsMonitor</c> (named per pool); requires <c>HostName</c> non-empty.</description></item>
/// </list>
/// Throws <see cref="InvalidOperationException"/> when all probes fail.
/// </summary>
internal static class ConnectionFactoryResolver
{
    public static IConnectionFactory Resolve(
        IServiceProvider sp,
        string name,
        Action<ConnectionFactory>? configureFactory)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(name);

        // Mode 1: keyed singleton.
        var keyed = sp.GetKeyedService<IConnectionFactory>(name);
        if (keyed is not null)
        {
            return keyed;
        }

        // Mode 2: closure callback.
        if (configureFactory is not null)
        {
            var factory = new ConnectionFactory();
            configureFactory(factory);
            return factory;
        }

        // Mode 3: IOptions binding (named).
        var monitor = sp.GetService<IOptionsMonitor<AdaptiveConnectionPoolOptions>>();
        var options = monitor?.Get(name);
        if (options is not null && !string.IsNullOrEmpty(options.HostName))
        {
            var factory = new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                UserName = options.UserName ?? ConnectionFactory.DefaultUser,
                Password = options.Password ?? ConnectionFactory.DefaultPass,
                VirtualHost = options.VirtualHost ?? ConnectionFactory.DefaultVHost,
            };
            if (options.RequestedHeartbeat is { } hb)
            {
                factory.RequestedHeartbeat = hb;
            }
            return factory;
        }

        throw new InvalidOperationException(
            $"AddAdaptiveConnectionPool: no IConnectionFactory found for pool '{name}'. " +
            "Provide one via keyed singleton, configureFactory closure, or IOptions binding.");
    }

    /// <summary>
    /// Forces <see cref="ConnectionFactory.AutomaticRecoveryEnabled"/> to <c>false</c> when the
    /// resolved factory is a concrete <see cref="ConnectionFactory"/> with recovery enabled,
    /// emitting EventId=2001 Warning log. The pool owns connection lifecycle, so client-side
    /// auto-recovery would race the pool's own discard/replace path (RESEARCH Pitfall A).
    /// </summary>
    public static void ForceAutomaticRecoveryDisabled(IConnectionFactory factory, ILogger logger, string poolName)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(poolName);

        if (factory is ConnectionFactory cf && cf.AutomaticRecoveryEnabled)
        {
            logger.AutomaticRecoveryOverridden(poolName);
            cf.AutomaticRecoveryEnabled = false;
        }
    }
}
