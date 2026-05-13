namespace Oragon.ElasticPool.Core.Internals;

/// <summary>
/// Mutable in <see cref="LastReturnedAt"/> only — every other field is readonly. Mutability is required
/// because the engine writes <c>LastReturnedAt</c> on every return without producing a new entry
/// (record-with churn was the alternative; Phase 2 sweeper reads it on every tick, hot path).
/// </summary>
internal sealed class PoolEntry<T> where T : notnull
{
    public T Item { get; }
    public DateTimeOffset CreatedAt { get; }

    // WR-01 fix: DateTimeOffset is a 16-byte struct (long ticks + short offset). Plain reads/writes
    // are not atomic under the CLR memory model, so concurrent writers (ReturnSync, sweeper marking
    // unhealthy) and readers (sweeper TryPeek age check, snapshot iteration) can observe torn reads
    // on weakly-ordered architectures (ARM64). Replace with a Volatile.Read/Write-protected long
    // (UTC ticks) — atomic on all .NET-supported architectures, paired memory ordering.
    private long _lastReturnedAtUtcTicks;
    public DateTimeOffset LastReturnedAt
    {
        get => new DateTimeOffset(Volatile.Read(ref _lastReturnedAtUtcTicks), TimeSpan.Zero);
        set => Volatile.Write(ref _lastReturnedAtUtcTicks, value.UtcTicks);
    }

    public PoolEntry(T item, DateTimeOffset createdAt, DateTimeOffset? lastReturnedAt = null)
    {
        Item = item;
        CreatedAt = createdAt;
        // Use the property setter so the volatile-write semantics apply uniformly.
        LastReturnedAt = lastReturnedAt ?? createdAt;
    }

    /// <summary>Preserves any existing <c>(item, createdAt)</c> deconstruction usages from Phase 1.</summary>
    public void Deconstruct(out T item, out DateTimeOffset createdAt)
    { item = Item; createdAt = CreatedAt; }
}
