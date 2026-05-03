namespace Oragon.AdaptivePool.RabbitMQ.Options;

/// <summary>
/// IOptions-bindable settings for an adaptive RabbitMQ connection pool. Used as the third
/// (lowest priority) probe by <c>ConnectionFactoryResolver</c>: when no keyed
/// <c>IConnectionFactory</c> is registered and no <c>configureFactory</c> closure is supplied,
/// these options are read from <c>IOptionsMonitor&lt;AdaptiveConnectionPoolOptions&gt;</c>
/// (named per pool) and used to build a <c>ConnectionFactory</c>.
/// </summary>
/// <remarks>
/// SECURITY: <see cref="Password"/> is sensitive. Supply via secret stores (User Secrets,
/// Azure Key Vault, environment variables, etc.) — never hardcode in source or commit to
/// configuration files. The adapter never logs the password value.
/// </remarks>
public sealed class AdaptiveConnectionPoolOptions
{
    /// <summary>RabbitMQ broker host name. When null/empty, the IOptions probe is skipped.</summary>
    public string? HostName { get; set; }

    /// <summary>RabbitMQ broker port. Defaults to 5672 (the standard AMQP port).</summary>
    public int Port { get; set; } = 5672;

    /// <summary>RabbitMQ user name. When null, <c>ConnectionFactory.DefaultUser</c> ("guest") is used.</summary>
    public string? UserName { get; set; }

    /// <summary>
    /// RabbitMQ password. SECURITY: supply via secret stores. When null,
    /// <c>ConnectionFactory.DefaultPass</c> ("guest") is used. Never logged by the adapter.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>RabbitMQ virtual host. When null, <c>ConnectionFactory.DefaultVHost</c> ("/") is used.</summary>
    public string? VirtualHost { get; set; }

    /// <summary>
    /// Heartbeat interval. When null (default), the underlying <c>ConnectionFactory</c>
    /// default (60 seconds) is used unchanged.
    /// </summary>
    public TimeSpan? RequestedHeartbeat { get; set; }
}
