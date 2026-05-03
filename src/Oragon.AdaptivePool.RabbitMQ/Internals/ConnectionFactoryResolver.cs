using System.Reflection;
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
    /// Returns an <see cref="IConnectionFactory"/> safe for connection creation with
    /// <see cref="ConnectionFactory.AutomaticRecoveryEnabled"/> forced to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// <para>WR-02 fix: when the resolved factory is a concrete <see cref="ConnectionFactory"/>
    /// with recovery enabled, we no longer mutate the shared singleton in-place — instead
    /// we return a per-acquire clone with all writable public properties copied and only
    /// <c>AutomaticRecoveryEnabled</c> overridden. This preserves any other code path
    /// holding a reference to the same registered factory (side connections, multi-pool
    /// scenarios, test helpers). EventId=2001 Warning is emitted on EVERY acquire that
    /// overrides (not just the first), so the issue stays observable and the consumer
    /// is encouraged to set <c>AutomaticRecoveryEnabled = false</c> in their factory
    /// configuration up front.</para>
    /// <para>Mode 2 (closure) creates a fresh ConnectionFactory per Resolve call so this
    /// path is unnecessary there; the override still applies to keep behaviour uniform.</para>
    /// <para>For non-<see cref="ConnectionFactory"/> implementations (a custom
    /// <see cref="IConnectionFactory"/>) this method is a no-op — the consumer owns the
    /// recovery semantics and we have no contract to override them.</para>
    /// </remarks>
    public static IConnectionFactory ApplyAutomaticRecoveryOverride(IConnectionFactory factory, ILogger logger, string poolName)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(poolName);

        if (factory is not ConnectionFactory cf)
        {
            // Custom IConnectionFactory implementation — cannot inspect or override.
            return factory;
        }

        if (!cf.AutomaticRecoveryEnabled)
        {
            // Already configured correctly — no clone, no log.
            return cf;
        }

        // EventId 2001 fires on every override (per WR-02), not just the first acquire,
        // so a misconfigured factory keeps producing diagnostic output until corrected.
        logger.AutomaticRecoveryOverridden(poolName);

        // Reflection-based clone: copy every writable instance property. Robust against
        // future ConnectionFactory additions (we cannot enumerate hardcoded fields without
        // risking silent loss of new configuration on RabbitMQ.Client upgrades).
        var clone = new ConnectionFactory();
        foreach (var prop in typeof(ConnectionFactory)
                                 .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            // Skip indexers.
            if (prop.GetIndexParameters().Length > 0) continue;
            try
            {
                var value = prop.GetValue(cf);
                prop.SetValue(clone, value);
            }
            catch
            {
                // Property may throw on get/set under certain configurations (e.g. Uri
                // with no HostName). Best-effort copy: skip and continue.
            }
        }
        clone.AutomaticRecoveryEnabled = false;
        return clone;
    }

    /// <summary>
    /// Legacy API kept for source compatibility. Prefer
    /// <see cref="ApplyAutomaticRecoveryOverride"/> which returns a clone instead of
    /// mutating the shared instance (WR-02). This method now delegates to the clone path
    /// AND mirrors the recovery flag onto the input for callers that still expect the
    /// observable side effect — but production code should use the new API.
    /// </summary>
    [Obsolete("Use ApplyAutomaticRecoveryOverride which returns a clone (WR-02). This method mutates the shared factory.")]
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
