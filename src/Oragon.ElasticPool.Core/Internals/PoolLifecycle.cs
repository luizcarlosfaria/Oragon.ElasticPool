namespace Oragon.ElasticPool.Core.Internals;

/// <summary>
/// Pool lifecycle state machine.
/// </summary>
/// <remarks>
/// Phase 1 only uses <see cref="Open"/> and <see cref="Closed"/>; <see cref="Draining"/> is a
/// reserved Phase 2 placeholder for graceful drain (allowing in-flight checkouts to complete
/// before full shutdown without accepting new acquires). Removing it now would risk a churn in
/// the engine's CAS-based lifecycle transitions when the sweeper lands; documenting the intent
/// keeps the values stable across phase boundaries.
/// </remarks>
internal enum PoolLifecycle { Open = 0, Draining = 1, Closed = 2 }
