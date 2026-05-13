using Oragon.ElasticPool.Core.Abstractions;

namespace Oragon.ElasticPool.Core.Hooks;

/// <summary>Creates a new pooled instance. Required hook. Runs OUTSIDE pool locks — slow factory work never blocks the acquire fast path.</summary>
public delegate ValueTask<T> FactoryDelegate<T>(IServiceProvider services, CancellationToken cancellationToken);

/// <summary>Cheap on-borrow validation. MUST complete in &lt; 1 ms p99 (no server round-trips). Heavy probes belong in the Check hook (Phase 2 sweep).</summary>
public delegate ValueTask<PoolState> BeforeUseDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>
/// Background health probe consumed by the Phase 2 sweeper.
/// </summary>
/// <remarks>
/// The engine invokes this hook from the background sweeper for idle entries. Use
/// <c>BeforeUse</c> for cheap on-borrow validation and <c>Check</c> for periodic probes.
/// </remarks>
public delegate ValueTask<PoolState> CheckDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>On-return validation. Default no-op in v1; activated via FAIL-V2 / HOOK-V2.</summary>
public delegate ValueTask<PoolState> AfterUseDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>Cleanup hook for evicted/discarded items (e.g., connection.CloseAsync()).</summary>
public delegate ValueTask ReleaseDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>
/// Synchronous variant of <see cref="FactoryDelegate{T}"/>. Use when creating a new instance is
/// purely in-memory (no I/O). The builder wraps the result in a completed <see cref="ValueTask{T}"/>
/// before storing — zero allocation on the fast path.
/// </summary>
public delegate T FactorySyncDelegate<T>(IServiceProvider services, CancellationToken cancellationToken);

/// <summary>
/// Synchronous variant of <see cref="BeforeUseDelegate{T}"/>. Use when the on-borrow validation
/// is a cheap in-memory check (e.g., <c>connection.IsOpen</c>) and no <c>await</c> is needed.
/// The builder wraps the result in a completed <see cref="ValueTask{PoolState}"/>.
/// </summary>
public delegate PoolState BeforeUseSyncDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>
/// Synchronous variant of <see cref="CheckDelegate{T}"/>. Use when the background sweep probe
/// is a cheap in-memory check that doesn't need <c>await</c>.
/// </summary>
public delegate PoolState CheckSyncDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>
/// Synchronous variant of <see cref="AfterUseDelegate{T}"/>. Use when the on-return validation
/// is a cheap in-memory check that doesn't need <c>await</c>.
/// </summary>
public delegate PoolState AfterUseSyncDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>
/// Synchronous variant of <see cref="ReleaseDelegate{T}"/>. Use when cleanup is purely synchronous
/// (e.g., <c>IDisposable.Dispose()</c>). The builder wraps the call in a completed
/// <see cref="ValueTask"/> before storing.
/// </summary>
public delegate void ReleaseSyncDelegate<T>(T item, CancellationToken cancellationToken);
