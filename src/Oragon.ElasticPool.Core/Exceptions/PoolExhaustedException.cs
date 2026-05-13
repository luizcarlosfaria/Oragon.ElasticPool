namespace Oragon.ElasticPool.Core.Exceptions;

/// <summary>
/// Thrown when:
///   - Sync Acquire() finds no free item (sync NEVER blocks).
///   - WaitBehavior.Throw is configured and AcquireAsync finds no free item.
/// </summary>
/// <remarks>
/// IR-03: this exception represents a recoverable, transient condition — the pool is operating
/// correctly, it is simply at capacity. The <see cref="IsTransient"/> property gives consumers
/// building retry logic a stable signal without coupling to the exception hierarchy
/// (changes to which would be binary-breaking post-1.0).
/// </remarks>
public sealed class PoolExhaustedException : Exception
{
    public int MaxSize { get; }
    public TimeSpan? WaitTime { get; }

    /// <summary>
    /// Always <c>true</c>. Indicates that this failure is transient and a retry is appropriate
    /// (subject to the caller's retry budget). Distinguishes pool exhaustion from programmer
    /// errors thrown as <see cref="InvalidOperationException"/> by the same library.
    /// </summary>
    public bool IsTransient => true;

    public PoolExhaustedException(int maxSize, TimeSpan? waitTime = null)
        : base($"Pool is exhausted. MaxSize={maxSize}{(waitTime is { } w ? $", waited={w}" : "")}")
    {
        MaxSize = maxSize;
        WaitTime = waitTime;
    }
}
