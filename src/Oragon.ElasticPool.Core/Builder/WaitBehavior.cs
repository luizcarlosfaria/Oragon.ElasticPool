namespace Oragon.ElasticPool.Core.Builder;

/// <summary>Strategy when AcquireAsync is called and the pool is exhausted.</summary>
public enum WaitBehavior
{
    /// <summary>Wait for an item to return; respects the caller's CancellationToken. (Default.)</summary>
    Wait = 0,
    /// <summary>Throw <see cref="Exceptions.PoolExhaustedException"/> immediately.</summary>
    Throw = 1,
}
