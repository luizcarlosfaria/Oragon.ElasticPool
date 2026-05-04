using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.Hooks;

namespace Oragon.AdaptivePool.Core.Builder;

/// <summary>Frozen, init-only configuration produced by <see cref="AdaptivePoolBuilder{T}.Build"/>.</summary>
public sealed record AdaptivePoolOptions<T>
    where T : notnull
{
    public required FactoryDelegate<T> Factory { get; init; }
    public BeforeUseDelegate<T>? BeforeUse { get; init; }
    public CheckDelegate<T>? Check { get; init; }
    public AfterUseDelegate<T>? AfterUse { get; init; }
    public ReleaseDelegate<T>? Release { get; init; }
    public required int MinSize { get; init; }
    public required int MaxSize { get; init; }
    public required int InitialSize { get; init; }
    public WaitBehavior WhenExhausted { get; init; } = WaitBehavior.Wait;
    public required IItemFailurePolicy<T> FailurePolicy { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public string PoolName { get; init; } = string.Empty;

    // Phase 2 elasticity tunables — locked defaults per CONTEXT.md D-01..D-08.
    /// <summary>Number of parked waiters that triggers an adaptive grow tick. Default: 1.</summary>
    public int GrowOnWaiterCount { get; init; } = 1;
    /// <summary>Sustained-utilization threshold (in-use / total) that triggers an adaptive grow tick. Default: 0.80.</summary>
    public double GrowOnUtilizationPercent { get; init; } = 0.80;
    /// <summary>Rolling window over which utilization is averaged. Default: 30 seconds.</summary>
    public TimeSpan UtilizationWindow { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>p95 acquire-wait threshold that triggers an adaptive grow tick. Default: 100 ms.</summary>
    public TimeSpan GrowOnWaitTimeP95 { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>An idle item older than this is eligible for shrink. Default: 60 seconds.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Utilization (in-use / total) at or below which sustained low pressure may shrink the pool. Default: 0.50.</summary>
    public double ShrinkOnUtilizationPercent { get; init; } = 0.50;
    /// <summary>Target utilization used to compute the post-shrink pool size. Default: 0.75.</summary>
    public double ShrinkTargetUtilizationPercent { get; init; } = 0.75;
    /// <summary>Maximum number of available items the sweeper may evict per shrink tick. Default: 1.</summary>
    public int ShrinkBatchSize { get; init; } = 1;
    /// <summary>Number of sweep windows after a grow during which shrink is suppressed. Default: 3.</summary>
    public int ShrinkCooldownWindows { get; init; } = 3;
    /// <summary>Background sweep tick interval. Default: 30 seconds.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum sweep interval after exponential backoff on consecutive failure windows. Default: 5 minutes.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);
}
