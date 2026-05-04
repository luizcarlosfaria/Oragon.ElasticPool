using RabbitMQ.Client;

namespace Oragon.AdaptivePool.RabbitMQ.Builder;

/// <summary>
/// Adapter-side fluent builder for an <c>IAdaptivePool&lt;IChannel&gt;</c>. Exposes the
/// pool-shape knobs most relevant to RabbitMQ channel pools (size bounds, idle timeout,
/// sweep interval, shrink pressure, shrink cooldown, channels-per-connection ceiling, and the
/// <see cref="CreateChannelOptions"/> applied when channels are created).
/// </summary>
/// <remarks>
/// <para>Defaults: MinSize=0, MaxSize=32, InitialSize=0, IdleTimeout=60s,
/// SweepInterval=30s, ShrinkOnUtilizationPercent=0.50,
/// ShrinkTargetUtilizationPercent=0.75, ShrinkBatchSize=1, ShrinkCooldownWindows=3,
/// MaxChannelsPerConnection=100, and a <see cref="CreateChannelOptions"/> with
/// publisher-confirmations + tracking ENABLED (per Pitfall E).</para>
/// <para>The 100-default for <see cref="MaxChannelsPerConnection"/> sits well below the
/// broker's 2047 default (Pitfall 11), avoiding single-connection bottlenecks. The 2047
/// hard ceiling on <see cref="WithMaxChannelsPerConnection"/> matches the broker default
/// channel_max — operators may lower it but never raise above the protocol limit.</para>
/// </remarks>
public sealed class AdaptiveChannelPoolBuilder
{
    /// <summary>Minimum pool size (steady-state floor). Default: 0.</summary>
    public int MinSize { get; private set; } = 0;

    /// <summary>Maximum pool size (hard ceiling). Default: 32.</summary>
    public int MaxSize { get; private set; } = 32;

    /// <summary>Initial pool size (eagerly-created instances at startup). Default: 0.</summary>
    public int InitialSize { get; private set; } = 0;

    /// <summary>Idle-item discard threshold for the background sweeper. Default: 60 seconds.</summary>
    public TimeSpan IdleTimeout { get; private set; } = TimeSpan.FromSeconds(60);
    /// <summary>Background sweep tick interval. Default: 30 seconds.</summary>
    public TimeSpan SweepInterval { get; private set; } = TimeSpan.FromSeconds(30);
    /// <summary>Utilization at or below which sustained low pressure may shrink the pool. Default: 0.50.</summary>
    public double ShrinkOnUtilizationPercent { get; private set; } = 0.50;
    /// <summary>Target utilization used to compute post-shrink size. Default: 0.75.</summary>
    public double ShrinkTargetUtilizationPercent { get; private set; } = 0.75;
    /// <summary>Maximum number of available items evicted per shrink tick. Default: 1.</summary>
    public int ShrinkBatchSize { get; private set; } = 1;
    /// <summary>Number of sweep windows after a grow during which shrink is suppressed. Default: 3.</summary>
    public int ShrinkCooldownWindows { get; private set; } = 3;

    /// <summary>
    /// Maximum number of channels multiplexed over a single <see cref="IConnection"/>
    /// before the channel pool's Factory acquires a different connection (eager spread,
    /// per RESEARCH Q2). Default: 100. Valid range: [1, 2047].
    /// </summary>
    public int MaxChannelsPerConnection { get; private set; } = 100;

    /// <summary>
    /// Options applied when the Factory calls <see cref="IConnection.CreateChannelAsync"/>.
    /// Default: publisher-confirmations + tracking ENABLED, no rate limiter,
    /// consumerDispatchConcurrency=1 (per Pitfall E mitigation).
    /// </summary>
    public CreateChannelOptions ChannelOptions { get; private set; } =
        new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true,
            outstandingPublisherConfirmationsRateLimiter: null,
            consumerDispatchConcurrency: 1);

    /// <summary>
    /// Configures the pool size bounds. Validates <c>0 &lt;= min &lt;= initial &lt;= max</c>
    /// and <c>max &gt;= 1</c>.
    /// </summary>
    public AdaptiveChannelPoolBuilder WithBounds(int min, int max, int initial)
    {
        if (min < 0)
            throw new ArgumentOutOfRangeException(nameof(min), "MinSize must be >= 0.");
        if (max < 1)
            throw new ArgumentOutOfRangeException(nameof(max), "MaxSize must be >= 1.");
        if (min > max)
            throw new ArgumentOutOfRangeException(nameof(min), $"MinSize ({min}) cannot exceed MaxSize ({max}).");
        if (initial < min || initial > max)
            throw new ArgumentOutOfRangeException(nameof(initial), $"InitialSize ({initial}) must satisfy MinSize ({min}) <= InitialSize <= MaxSize ({max}).");

        MinSize = min;
        MaxSize = max;
        InitialSize = initial;
        return this;
    }

    /// <summary>
    /// Configures the idle-item discard threshold used by the background sweeper.
    /// Validates <c>t &gt; TimeSpan.Zero</c>.
    /// </summary>
    public AdaptiveChannelPoolBuilder WithIdleTimeout(TimeSpan t)
    {
        if (t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(t), "IdleTimeout must be > TimeSpan.Zero.");
        IdleTimeout = t;
        return this;
    }

    /// <summary>
    /// Configures the background sweep tick interval. Validates <c>t &gt; TimeSpan.Zero</c>.
    /// </summary>
    public AdaptiveChannelPoolBuilder WithSweepInterval(TimeSpan t)
    {
        if (t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(t), "SweepInterval must be > TimeSpan.Zero.");
        SweepInterval = t;
        return this;
    }

    /// <summary>
    /// Configures the utilization threshold at or below which sustained low pressure may shrink.
    /// Validates <c>p in [0, 1]</c>.
    /// </summary>
    public AdaptiveChannelPoolBuilder WithShrinkOnUtilizationPercent(double p)
    {
        if (p < 0.0 || p > 1.0)
            throw new ArgumentOutOfRangeException(nameof(p), "ShrinkOnUtilizationPercent must be in [0, 1].");
        ShrinkOnUtilizationPercent = p;
        return this;
    }

    /// <summary>
    /// Configures the target utilization used to compute post-shrink size.
    /// Validates <c>p in (0, 1]</c>.
    /// </summary>
    public AdaptiveChannelPoolBuilder WithShrinkTargetUtilizationPercent(double p)
    {
        if (p <= 0.0 || p > 1.0)
            throw new ArgumentOutOfRangeException(nameof(p), "ShrinkTargetUtilizationPercent must be in (0, 1].");
        ShrinkTargetUtilizationPercent = p;
        return this;
    }

    /// <summary>
    /// Configures the maximum number of available items evicted per shrink tick.
    /// Validates <c>n &gt;= 1</c>.
    /// </summary>
    public AdaptiveChannelPoolBuilder WithShrinkBatchSize(int n)
    {
        if (n < 1)
            throw new ArgumentOutOfRangeException(nameof(n), "ShrinkBatchSize must be >= 1.");
        ShrinkBatchSize = n;
        return this;
    }

    /// <summary>
    /// Configures the number of sweep windows after grow during which shrink is suppressed.
    /// Validates <c>n &gt;= 0</c>.
    /// </summary>
    public AdaptiveChannelPoolBuilder WithShrinkCooldownWindows(int n)
    {
        if (n < 0)
            throw new ArgumentOutOfRangeException(nameof(n), "ShrinkCooldownWindows must be >= 0.");
        ShrinkCooldownWindows = n;
        return this;
    }

    /// <summary>
    /// Configures the maximum number of channels multiplexed over a single connection
    /// before eager spread acquires another. Validates <c>n in [1, 2047]</c> — the upper
    /// bound matches the broker's default <c>channel_max</c> (Pitfall 11).
    /// </summary>
    public AdaptiveChannelPoolBuilder WithMaxChannelsPerConnection(int n)
    {
        if (n < 1 || n > 2047)
            throw new ArgumentOutOfRangeException(nameof(n),
                "MaxChannelsPerConnection must be in [1, 2047] — broker default channel_max is 2047.");
        MaxChannelsPerConnection = n;
        return this;
    }

    /// <summary>
    /// Replaces the <see cref="CreateChannelOptions"/> used when creating channels.
    /// Throws <see cref="ArgumentNullException"/> on null.
    /// </summary>
    public AdaptiveChannelPoolBuilder WithChannelOptions(CreateChannelOptions options)
    {
        ChannelOptions = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }
}
