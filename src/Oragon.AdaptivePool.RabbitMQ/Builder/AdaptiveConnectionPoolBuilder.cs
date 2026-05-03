namespace Oragon.AdaptivePool.RabbitMQ.Builder;

/// <summary>
/// Adapter-side fluent builder for an <c>IAdaptivePool&lt;IConnection&gt;</c>. Exposes the
/// pool-shape knobs most relevant to RabbitMQ connection pools (size bounds and idle timeout).
/// Other Core knobs (<c>GrowOnWaiterCount</c>, <c>GrowOnUtilizationPercent</c>,
/// <c>GrowOnWaitTimeP95</c>, <c>ShrinkCooldownWindows</c>, <c>SweepInterval</c>,
/// <c>MaxBackoff</c>, <c>WhenExhausted</c>, custom <c>IItemFailurePolicy</c>, custom
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
}
