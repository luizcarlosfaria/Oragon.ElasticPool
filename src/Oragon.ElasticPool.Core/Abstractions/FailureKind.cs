namespace Oragon.ElasticPool.Core.Abstractions;

/// <summary>Reason the failure policy was invoked.</summary>
public enum FailureKind
{
    /// <summary>The Factory hook threw an exception.</summary>
    FactoryThrew = 0,
    /// <summary>The BeforeUse hook returned <see cref="PoolState.Unhealthy"/>.</summary>
    BeforeUseUnhealthy = 1,
    /// <summary>The AfterUse hook returned <see cref="PoolState.Unhealthy"/>.</summary>
    AfterUseUnhealthy = 2,
    /// <summary>The background <c>Check</c> hook reported the item <see cref="PoolState.Unhealthy"/>
    /// (or threw an exception). Distinguished from <see cref="AfterUseUnhealthy"/> so failure
    /// policies can apply different remediation per source (e.g., reconnect vs. log+discard).</summary>
    CheckUnhealthy = 3,
}
