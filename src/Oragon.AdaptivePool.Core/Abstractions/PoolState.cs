namespace Oragon.AdaptivePool.Core.Abstractions;

/// <summary>Health verdict returned by lifecycle hooks (BeforeUse, Check, AfterUse).</summary>
public enum PoolState
{
    /// <summary>Item is usable. Engine returns/keeps it.</summary>
    Healthy = 0,
    /// <summary>Item is broken. Engine invokes <see cref="IItemFailurePolicy{T}"/>.</summary>
    Unhealthy = 1,
}
