namespace Oragon.AdaptivePool.Core.Abstractions;

/// <summary>Reason the failure policy was invoked.</summary>
public enum FailureKind
{
    /// <summary>The Factory hook threw an exception.</summary>
    FactoryThrew = 0,
    /// <summary>The BeforeUse hook returned <see cref="PoolState.Unhealthy"/>.</summary>
    BeforeUseUnhealthy = 1,
    /// <summary>The AfterUse hook returned <see cref="PoolState.Unhealthy"/>.</summary>
    AfterUseUnhealthy = 2,
}
