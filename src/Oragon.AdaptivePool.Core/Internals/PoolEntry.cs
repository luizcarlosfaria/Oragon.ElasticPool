namespace Oragon.AdaptivePool.Core.Internals;

/// <summary>
/// Mutable in <see cref="LastReturnedAt"/> only — every other field is readonly. Mutability is required
/// because the engine writes <c>LastReturnedAt</c> on every return without producing a new entry
/// (record-with churn was the alternative; Phase 2 sweeper reads it on every tick, hot path).
/// </summary>
internal sealed class PoolEntry<T> where T : notnull
{
    public T Item { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastReturnedAt { get; set; }

    public PoolEntry(T item, DateTimeOffset createdAt, DateTimeOffset? lastReturnedAt = null)
    {
        Item = item;
        CreatedAt = createdAt;
        LastReturnedAt = lastReturnedAt ?? createdAt;
    }

    /// <summary>Preserves any existing <c>(item, createdAt)</c> deconstruction usages from Phase 1.</summary>
    public void Deconstruct(out T item, out DateTimeOffset createdAt)
    { item = Item; createdAt = CreatedAt; }
}
