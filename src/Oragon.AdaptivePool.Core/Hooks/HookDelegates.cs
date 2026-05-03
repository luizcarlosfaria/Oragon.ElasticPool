using Oragon.AdaptivePool.Core.Abstractions;

namespace Oragon.AdaptivePool.Core.Hooks;

/// <summary>Creates a new pooled instance. Required hook. Runs OUTSIDE pool locks — slow factory work never blocks the acquire fast path.</summary>
public delegate ValueTask<T> FactoryDelegate<T>(IServiceProvider services, CancellationToken cancellationToken);

/// <summary>Cheap on-borrow validation. MUST complete in &lt; 1 ms p99 (no server round-trips). Heavy probes belong in the Check hook (Phase 2 sweep).</summary>
public delegate ValueTask<PoolState> BeforeUseDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>
/// Background health probe consumed by the Phase 2 sweeper.
/// </summary>
/// <remarks>
/// In Phase 1 this hook signature exists for API stability but is NEVER invoked by the engine —
/// configuring it via <c>AdaptivePoolBuilder&lt;T&gt;.Check(...)</c> has no effect until the
/// Phase 2 sweeper ships. Use <c>BeforeUse</c> for on-borrow validation in Phase 1.
/// </remarks>
public delegate ValueTask<PoolState> CheckDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>On-return validation. Default no-op in v1; activated via FAIL-V2 / HOOK-V2.</summary>
public delegate ValueTask<PoolState> AfterUseDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>Cleanup hook for evicted/discarded items (e.g., connection.CloseAsync()).</summary>
public delegate ValueTask ReleaseDelegate<T>(T item, CancellationToken cancellationToken);
