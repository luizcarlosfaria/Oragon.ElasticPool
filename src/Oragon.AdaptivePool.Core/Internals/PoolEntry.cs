namespace Oragon.AdaptivePool.Core.Internals;

internal sealed record PoolEntry<T>(T Item, DateTimeOffset CreatedAt) where T : notnull;
