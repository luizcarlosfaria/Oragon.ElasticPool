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
}
