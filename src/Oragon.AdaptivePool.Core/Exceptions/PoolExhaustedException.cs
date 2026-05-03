namespace Oragon.AdaptivePool.Core.Exceptions;

/// <summary>
/// Thrown when:
///   - Sync Acquire() finds no free item (sync NEVER blocks).
///   - WaitBehavior.Throw is configured and AcquireAsync finds no free item.
/// </summary>
public sealed class PoolExhaustedException : Exception
{
    public int MaxSize { get; }
    public TimeSpan? WaitTime { get; }

    public PoolExhaustedException(int maxSize, TimeSpan? waitTime = null)
        : base($"Pool is exhausted. MaxSize={maxSize}{(waitTime is { } w ? $", waited={w}" : "")}")
    {
        MaxSize = maxSize;
        WaitTime = waitTime;
    }
}
