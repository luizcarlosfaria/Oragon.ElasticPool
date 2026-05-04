namespace Oragon.AdaptivePool.RabbitMQ.Builder;

/// <summary>
/// Adapter-side fluent builder for an <c>IAdaptivePool&lt;IConnection&gt;</c>. Exposes the
/// pool-shape knobs most relevant to RabbitMQ connection pools (size bounds, idle timeout,
/// sweep interval, shrink pressure, and shrink cooldown).
/// Other Core knobs (<c>GrowOnWaiterCount</c>, <c>GrowOnUtilizationPercent</c>,
/// <c>GrowOnWaitTimeP95</c>, <c>MaxBackoff</c>, <c>WhenExhausted</c>, custom <c>IItemFailurePolicy</c>, custom
/// <c>TimeProvider</c>) keep their Core defaults; if a consumer needs them, they can register
/// the pool directly with <c>services.AddAdaptivePool&lt;IConnection&gt;(...)</c> instead of
/// the adapter extension. Defaults match Core's defaults so the public DX is self-contained.
/// </summary>
public sealed class AdaptiveConnectionPoolBuilder
{
    /// <summary>Minimum pool size (steady-state floor). Default: 0.</summary>
    public int MinSize { get; private set; } = 0;

    /// <summary>Maximum pool size (hard ceiling). Default: 8.</summary>
    public int MaxSize { get; private set; } = 8;

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
    /// Configures the pool size bounds. Validates <c>0 &lt;= min &lt;= initial &lt;= max</c>
    /// and <c>max &gt;= 1</c>.
    /// </summary>
    public AdaptiveConnectionPoolBuilder WithBounds(int min, int max, int initial)
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
    public AdaptiveConnectionPoolBuilder WithIdleTimeout(TimeSpan t)
    {
        if (t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(t), "IdleTimeout must be > TimeSpan.Zero.");
        IdleTimeout = t;
        return this;
    }

    /// <summary>
    /// Configures the background sweep tick interval. Validates <c>t &gt; TimeSpan.Zero</c>.
    /// </summary>
    public AdaptiveConnectionPoolBuilder WithSweepInterval(TimeSpan t)
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
    public AdaptiveConnectionPoolBuilder WithShrinkOnUtilizationPercent(double p)
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
    public AdaptiveConnectionPoolBuilder WithShrinkTargetUtilizationPercent(double p)
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
    public AdaptiveConnectionPoolBuilder WithShrinkBatchSize(int n)
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
    public AdaptiveConnectionPoolBuilder WithShrinkCooldownWindows(int n)
    {
        if (n < 0)
            throw new ArgumentOutOfRangeException(nameof(n), "ShrinkCooldownWindows must be >= 0.");
        ShrinkCooldownWindows = n;
        return this;
    }
}
