using Oragon.ElasticPool.Abstractions;

namespace Oragon.ElasticPool.Policies;

/// <summary>
/// Default failure policy. On any failure (factory throw or BeforeUse Unhealthy),
/// discards the broken item; engine triggers replacement to maintain MinSize.
/// </summary>
public sealed class DiscardAndReplaceFailurePolicy<T> : IItemFailurePolicy<T>
{
    public ValueTask<FailureDecision> HandleAsync(
        T? failedItem,
        FailureKind failureKind,
        Exception? exception,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(FailureDecision.Discard);
}
