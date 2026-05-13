namespace Oragon.ElasticPool.Core.Abstractions;

public interface IItemFailurePolicy<T>
{
    /// <summary>
    /// Decide what to do with a broken item or a failed factory call.
    /// Engine guarantees: counter is rolled back BEFORE this call (no ghost reservation).
    /// </summary>
    /// <param name="failedItem">The broken item — may be default(T) when factory itself failed.</param>
    /// <param name="failureKind">Why the policy was invoked.</param>
    /// <param name="exception">The exception, if any (factory failure case).</param>
    /// <returns>Action the engine should take.</returns>
    ValueTask<FailureDecision> HandleAsync(
        T? failedItem,
        FailureKind failureKind,
        Exception? exception,
        CancellationToken cancellationToken);
}
