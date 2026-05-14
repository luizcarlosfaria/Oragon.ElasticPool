---
phase: 01-core-skeleton-fixed-size-pool
plan: 02
type: execute
wave: 2
depends_on: [01]
files_modified:
  - src/Oragon.ElasticPool/Abstractions/IElasticPool.cs
  - src/Oragon.ElasticPool/Abstractions/IPoolItem.cs
  - src/Oragon.ElasticPool/Abstractions/IItemFailurePolicy.cs
  - src/Oragon.ElasticPool/Abstractions/PoolState.cs
  - src/Oragon.ElasticPool/Abstractions/FailureKind.cs
  - src/Oragon.ElasticPool/Abstractions/FailureDecision.cs
  - src/Oragon.ElasticPool/Hooks/HookDelegates.cs
  - src/Oragon.ElasticPool/Builder/ElasticObjectPoolFactory.cs
  - src/Oragon.ElasticPool/Builder/ElasticPoolBuilder.cs
  - src/Oragon.ElasticPool/Builder/ElasticPoolOptions.cs
  - src/Oragon.ElasticPool/Builder/WaitBehavior.cs
  - src/Oragon.ElasticPool/Policies/DiscardAndReplaceFailurePolicy.cs
  - src/Oragon.ElasticPool/Exceptions/PoolExhaustedException.cs
  - src/Oragon.ElasticPool/Internals/PoolEntry.cs
  - src/Oragon.ElasticPool/Internals/PoolItem.cs
  - src/Oragon.ElasticPool/Internals/PoolLifecycle.cs
  - src/Oragon.ElasticPool/Internals/ElasticPool.cs
  - src/Oragon.ElasticPool/Telemetry/PoolMeterNames.cs
  - src/Oragon.ElasticPool/Telemetry/TelemetryEmitter.cs
  - src/Oragon.ElasticPool/Telemetry/PoolDiagnosticsLog.cs
  - src/Oragon.ElasticPool/DependencyInjection/ServiceCollectionExtensions.cs
  - src/Oragon.ElasticPool/PublicAPI.Unshipped.txt
autonomous: true
requirements:
  - API-01
  - API-02
  - API-03
  - HOOK-01
  - HOOK-02
  - HOOK-03
  - HOOK-04
  - HOOK-05
  - BOUND-01
  - BOUND-02
  - FAIL-01
  - FAIL-02
  - DI-01
  - QUAL-01
  - QUAL-02
  - TELEM-01
user_setup: []

must_haves:
  truths:
    - "`IElasticPool<T>` exposes sync `Acquire()`, async `AcquireAsync(CancellationToken)` returning `ValueTask<IPoolItem<T>>`, and read-only `MaxSize`/`MinSize`/`Available`/`InUse` properties (per RESEARCH Pitfall 5)"
    - "`IPoolItem<T>` is a disposable wrapper exposing `.Value`; double-Dispose() and double-DisposeAsync() are no-ops via `Interlocked.Exchange`"
    - "Calling `IPoolItem<T>.Value` after dispose throws `ObjectDisposedException`"
    - "`ElasticObjectPoolFactory.Build<T>(IServiceProvider, CancellationToken)` returns a fluent builder; `Build()` validates `0 ≤ Min ≤ Initial ≤ Max`, requires `Factory`, and throws on violation"
    - "All five hook delegates (Factory, BeforeUse, Check, AfterUse, Release) accept `CancellationToken` and return `ValueTask` (or `ValueTask<PoolState>`); only Factory is required"
    - "`Factory` runs OUTSIDE any pool lock (engine increments counter atomically, then awaits factory)"
    - "When `Factory` throws, `_total` is decremented (counter rollback) and `IItemFailurePolicy<T>.HandleAsync` is invoked with `FailureKind.FactoryThrew` and the exception"
    - "When `BeforeUse` returns `Unhealthy`, `IItemFailurePolicy<T>.HandleAsync` is invoked with `FailureKind.BeforeUseUnhealthy`; `DiscardAndReplaceFailurePolicy<T>` returns `FailureDecision.Discard` and engine discards + replaces"
    - "Pool with `MaxSize=N` running at full utilization queues additional `AcquireAsync` callers via `Channel<TaskCompletionSource<PoolEntry<T>>>` and wakes them on `Return` (direct-handoff)"
    - "`WaitBehavior.Throw` raises `PoolExhaustedException` immediately when no free item exists; `WaitBehavior.Wait` (default) waits respecting the caller's `CancellationToken`"
    - "Sync `Acquire()` returns immediately if a free item exists; otherwise throws `PoolExhaustedException` (never blocks per RESEARCH Open Question 3 recommendation)"
    - "Eager warm-up to `InitialSize` runs in parallel; `Task ReadyAsync()` returns the warm-up task so consumers can `await pool.ReadyAsync()` for readiness"
    - "Pool implements both `IDisposable` and `IAsyncDisposable`; `DisposeAsync` cancels lifetime CTS, drains idle queue invoking `Release` hook on each entry, and transitions to Closed"
    - "After dispose, new `AcquireAsync` / `Acquire` throw `ObjectDisposedException`"
    - "`services.AddElasticPool<T>(name, configure)` registers the pool as a keyed singleton; consumers resolve via `GetRequiredKeyedService<IElasticPool<T>>(name)`; default name `string.Empty` is also resolvable as non-keyed `GetRequiredService<IElasticPool<T>>()`"
    - "`Meter` named `\"Oragon.ElasticPool\"` is created via `IMeterFactory` if registered, else via `new Meter(...)` fallback; emits at minimum `pool.acquire.count` and `pool.factory.failures` counters tagged with `pool.name`"
  artifacts:
    - path: src/Oragon.ElasticPool/Abstractions/IElasticPool.cs
      provides: "Public IElasticPool<T> contract"
      contains: "ValueTask<IPoolItem<T>> AcquireAsync"
    - path: src/Oragon.ElasticPool/Abstractions/IPoolItem.cs
      provides: "Public IPoolItem<out T> : IDisposable, IAsyncDisposable wrapper"
      contains: "T Value"
    - path: src/Oragon.ElasticPool/Abstractions/IItemFailurePolicy.cs
      provides: "Public failure-policy extension point"
      contains: "ValueTask<FailureDecision> HandleAsync"
    - path: src/Oragon.ElasticPool/Abstractions/PoolState.cs
      provides: "Healthy/Unhealthy enum"
      contains: "Healthy"
    - path: src/Oragon.ElasticPool/Hooks/HookDelegates.cs
      provides: "Five public hook delegate types — all accept CancellationToken, return ValueTask<...>"
      contains: "FactoryDelegate"
    - path: src/Oragon.ElasticPool/Builder/ElasticObjectPoolFactory.cs
      provides: "Public static entry point Build<T>(IServiceProvider, CancellationToken)"
      contains: "ElasticPoolBuilder<T>"
    - path: src/Oragon.ElasticPool/Builder/ElasticPoolBuilder.cs
      provides: "Fluent builder with Factory/BeforeUse/Check/AfterUse/Release/WithBounds/WhenExhausted/WithFailurePolicy/WithTimeProvider, .Build() validates and constructs"
      contains: "public IElasticPool<T> Build()"
    - path: src/Oragon.ElasticPool/Builder/ElasticPoolOptions.cs
      provides: "Frozen init-only record carrying configured values into the engine"
      contains: "public required FactoryDelegate<T> Factory"
    - path: src/Oragon.ElasticPool/Builder/WaitBehavior.cs
      provides: "Wait | Throw enum for exhaustion behavior"
      contains: "Wait"
    - path: src/Oragon.ElasticPool/Policies/DiscardAndReplaceFailurePolicy.cs
      provides: "Default failure policy: always returns Discard"
      contains: "FailureDecision.Discard"
    - path: src/Oragon.ElasticPool/Exceptions/PoolExhaustedException.cs
      provides: "Thrown by sync Acquire() when no item free, or by WaitBehavior.Throw"
      contains: "MaxSize"
    - path: src/Oragon.ElasticPool/Internals/ElasticPool.cs
      provides: "Sealed engine: ConcurrentQueue idle store, Channel waiter queue, Interlocked counters, lifecycle state machine, acquire/release paths with rollback, drain on dispose"
      min_lines: 200
      contains: "Channel<TaskCompletionSource"
    - path: src/Oragon.ElasticPool/Internals/PoolItem.cs
      provides: "Sealed wrapper: Interlocked _disposed flag, idempotent Dispose/DisposeAsync, finalizer for leak detection"
      contains: "Interlocked.Exchange"
    - path: src/Oragon.ElasticPool/Telemetry/TelemetryEmitter.cs
      provides: "Owns Meter (via IMeterFactory or fallback); exposes OnAcquire/OnFactoryFailure"
      contains: "IMeterFactory"
    - path: src/Oragon.ElasticPool/Telemetry/PoolMeterNames.cs
      provides: "Meter name 'Oragon.ElasticPool' + counter name constants + pool.name tag key"
      contains: "Oragon.ElasticPool"
    - path: src/Oragon.ElasticPool/Telemetry/PoolDiagnosticsLog.cs
      provides: "[LoggerMessage] source-gen logging entries for ItemLeaked, FactoryFailed, BeforeUseUnhealthy, ReleaseHookFailedDuringDispose"
      contains: "LoggerMessage"
    - path: src/Oragon.ElasticPool/DependencyInjection/ServiceCollectionExtensions.cs
      provides: "AddElasticPool<T>(name, configure) DI extension with named options + keyed singleton registration; default-name (string.Empty) also registered non-keyed"
      contains: "AddKeyedSingleton"
    - path: src/Oragon.ElasticPool/PublicAPI.Unshipped.txt
      provides: "Updated public API surface for new Phase 1 types"
      min_lines: 20
  key_links:
    - from: "src/Oragon.ElasticPool/Internals/ElasticPool.cs"
      to: "src/Oragon.ElasticPool/Telemetry/TelemetryEmitter.cs"
      via: "private readonly TelemetryEmitter _telemetry — emits OnAcquire / OnFactoryFailure on hot paths"
      pattern: "_telemetry\\.OnAcquire|_telemetry\\.OnFactoryFailure"
    - from: "src/Oragon.ElasticPool/Internals/ElasticPool.cs"
      to: "src/Oragon.ElasticPool/Internals/PoolItem.cs"
      via: "AcquireAsync wraps the dequeued PoolEntry in a new PoolItem<T>(this, entry)"
      pattern: "new PoolItem<T>"
    - from: "src/Oragon.ElasticPool/Internals/PoolItem.cs"
      to: "src/Oragon.ElasticPool/Internals/ElasticPool.cs"
      via: "Dispose calls _owner.ReturnSync(_entry); DisposeAsync calls _owner.ReturnAsync(_entry); finalizer calls _owner.ReturnFromFinalizer(_entry)"
      pattern: "_owner\\.Return"
    - from: "src/Oragon.ElasticPool/Internals/ElasticPool.cs"
      to: "src/Oragon.ElasticPool/Abstractions/IItemFailurePolicy.cs"
      via: "On factory throw: rollback counter, then await _options.FailurePolicy.HandleAsync(default, FailureKind.FactoryThrew, ex, ct). On BeforeUse Unhealthy: same with FailureKind.BeforeUseUnhealthy."
      pattern: "FailurePolicy\\.HandleAsync"
    - from: "src/Oragon.ElasticPool/DependencyInjection/ServiceCollectionExtensions.cs"
      to: "src/Oragon.ElasticPool/Builder/ElasticObjectPoolFactory.cs"
      via: "AddKeyedSingleton<IElasticPool<T>>(name, factory) calls ElasticObjectPoolFactory.Build<T>(sp, ct), runs the user-configured Action<ElasticPoolBuilder<T>>, then .Build()"
      pattern: "ElasticObjectPoolFactory\\.Build"
---

<objective>
Implement the entire Phase 1 product surface in `Oragon.ElasticPool`: 12 public types + the sealed internal engine + DI extension + basic telemetry. The result is a fully-functional fixed-size pool that consumers can register via DI, acquire from sync or async, dispose with drain semantics, and observe via OpenTelemetry-compatible counters.

Purpose: Lock in every non-retrofittable architectural decision identified in RESEARCH.md and PITFALLS.md — `ValueTask` returns, `CancellationToken` in every signature, `Channel<TCS>` direct-handoff waiter, `Interlocked` counter rollback, dual `IDisposable`+`IAsyncDisposable`, `IPoolItem<T>` wrapper with finalizer for leak detection, `TimeProvider` injection point, `IMeterFactory` resolution with fallback. Once shipped, none of these can change without a major version bump.

Output: A buildable Core assembly whose public API enables every success criterion of Phase 1. Plan 03 then proves correctness through unit and stress tests.
</objective>

<execution_context>
@/mnt/p/dynamic-pool/.claude/get-shit-done/workflows/execute-plan.md
@/mnt/p/dynamic-pool/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/REQUIREMENTS.md
@.planning/ROADMAP.md
@.planning/research/STACK.md
@.planning/phases/01-core-skeleton-fixed-size-pool/01-CONTEXT.md
@.planning/phases/01-core-skeleton-fixed-size-pool/01-RESEARCH.md
@.planning/phases/01-core-skeleton-fixed-size-pool/01-01-SUMMARY.md

<interfaces>
<!-- Plan 01 produced the following inputs that this plan consumes: -->

Project structure (from Plan 01):
- src/Oragon.ElasticPool/ — empty C# project, multi-target net8/9/10, Nullable=enable, ImplicitUsings=enable, TreatWarningsAsErrors=true
- PublicApiAnalyzers active with empty Shipped.txt + Unshipped.txt baselines (this plan WILL update Unshipped.txt as new public types land)
- Direct PackageReferences available: Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Options
- Versions pinned in Directory.Packages.props

Public-contract code patterns to follow VERBATIM (from RESEARCH.md):
- Pattern 1 (Hook Delegates): `public delegate ValueTask<T> FactoryDelegate<T>(IServiceProvider, CancellationToken)`, etc.
- Pattern 2 (PoolItem wrapper): `Interlocked.Exchange(ref _disposed, 1)` idempotency, finalizer that swallows exceptions, `Value` getter throws ObjectDisposedException
- Pattern 3 (Builder): `Factory` required, validation rules `0 ≤ Min ≤ Initial ≤ Max`, frozen options record
- Pattern 4 (Failure Policy): `IItemFailurePolicy<T>.HandleAsync(T?, FailureKind, Exception?, CancellationToken)` returning `ValueTask<FailureDecision>`; `DiscardAndReplaceFailurePolicy<T>` always returns `Discard`
- Pattern 5 (Warm-up): parallel `Task.Run` over `InitialSize`, expose via `Task ReadyAsync()`
- Pattern 6 (TelemetryEmitter): try `IMeterFactory` from DI, fallback to `new Meter`, dispose only the fallback
- Pattern 7 (DI extension): `services.AddOptions<ElasticPoolBuilderConfigurator<T>>(name).Configure(...)` + `TryAddKeyedSingleton<IElasticPool<T>>(name, factory)` — keyed for multi-pool, also non-keyed registration when name == string.Empty
- Pattern 8 (CancellationToken linking): `using var linked = CTS.CreateLinkedTokenSource(callerCt, _lifetimeCts.Token)`, never skip the using
- Pattern 9 (Dispose state machine): `Open → Closed` via `Interlocked.Exchange`, sync Dispose calls `DisposeAsync().AsTask().GetAwaiter().GetResult()`

Key API decisions LOCKED by CONTEXT.md (do not deviate):
- `IPoolItem<T>.Value` (NOT `.Item` or `.Object`)
- `WaitBehavior` enum: `Wait` (default) | `Throw`; `Throw` raises `PoolExhaustedException`
- Builder: only `Factory` is required; `Build()` throws `InvalidOperationException` if missing
- DI: `AddElasticPool<T>(name, configure)` requires `name` explicit; `string.Empty` for default; named-options pattern internally
- TimeProvider injection point present in builder + engine ctor (Phase 2 sweeper consumes it; Phase 1 only stamps `PoolEntry.CreatedAt`)
- Telemetry: minimum counters `pool.acquire.count` + `pool.factory.failures`, tagged with `pool.name`
- Sync `Acquire()` is fast-path only — RESEARCH Open Question 3 recommendation: throw `PoolExhaustedException` immediately if no free item (never block)

Internal naming (Claude's discretion per CONTEXT.md):
- Namespace `Oragon.ElasticPool` (root); sub-namespaces per folder (Abstractions, Builder, Hooks, Internals, Policies, Telemetry, DependencyInjection, Exceptions)
- `PoolState` enum: `Healthy`, `Unhealthy` only (no `Quarantined` — reserved for v2)
- `PoolExhaustedException` exposes `int MaxSize` and `TimeSpan? WaitTime` properties
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Public API surface — abstractions, hooks, builder, options, exceptions, default failure policy</name>
  <files>
    src/Oragon.ElasticPool/Abstractions/IElasticPool.cs,
    src/Oragon.ElasticPool/Abstractions/IPoolItem.cs,
    src/Oragon.ElasticPool/Abstractions/IItemFailurePolicy.cs,
    src/Oragon.ElasticPool/Abstractions/PoolState.cs,
    src/Oragon.ElasticPool/Abstractions/FailureKind.cs,
    src/Oragon.ElasticPool/Abstractions/FailureDecision.cs,
    src/Oragon.ElasticPool/Hooks/HookDelegates.cs,
    src/Oragon.ElasticPool/Builder/WaitBehavior.cs,
    src/Oragon.ElasticPool/Builder/ElasticPoolOptions.cs,
    src/Oragon.ElasticPool/Builder/ElasticPoolBuilder.cs,
    src/Oragon.ElasticPool/Builder/ElasticObjectPoolFactory.cs,
    src/Oragon.ElasticPool/Policies/DiscardAndReplaceFailurePolicy.cs,
    src/Oragon.ElasticPool/Exceptions/PoolExhaustedException.cs
  </files>
  <action>
This task creates ONLY public-surface code (no engine yet). The engine in Task 2 implements `IElasticPool<T>` and references this surface. By writing the contracts first, Task 2 has nothing left to invent.

Reference: RESEARCH.md Patterns 1–4 (verbatim where shown). Use those code shapes exactly. Where types are referenced but not yet defined (e.g. `ElasticPool<T>` in Pattern 3 — that goes in Task 2), forward-declare via class scaffolding only.

1. `Abstractions/PoolState.cs`:
```csharp
namespace Oragon.ElasticPool.Abstractions;

/// <summary>Health verdict returned by lifecycle hooks (BeforeUse, Check, AfterUse).</summary>
public enum PoolState
{
    /// <summary>Item is usable. Engine returns/keeps it.</summary>
    Healthy = 0,
    /// <summary>Item is broken. Engine invokes <see cref="IItemFailurePolicy{T}"/>.</summary>
    Unhealthy = 1,
}
```

2. `Abstractions/FailureKind.cs`:
```csharp
namespace Oragon.ElasticPool.Abstractions;

/// <summary>Reason the failure policy was invoked.</summary>
public enum FailureKind
{
    /// <summary>The Factory hook threw an exception.</summary>
    FactoryThrew = 0,
    /// <summary>The BeforeUse hook returned <see cref="PoolState.Unhealthy"/>.</summary>
    BeforeUseUnhealthy = 1,
    /// <summary>The AfterUse hook returned <see cref="PoolState.Unhealthy"/>.</summary>
    AfterUseUnhealthy = 2,
}
```

3. `Abstractions/FailureDecision.cs`:
```csharp
namespace Oragon.ElasticPool.Abstractions;

/// <summary>Action the engine should take based on the failure policy.</summary>
public enum FailureDecision
{
    /// <summary>Discard the item; engine triggers replacement if below MinSize. Default behavior in v1.</summary>
    Discard = 0,
    // Quarantine = 1,  // reserved for v2 (FAIL-V2-01)
}
```

4. `Abstractions/IItemFailurePolicy.cs` — verbatim from RESEARCH Pattern 4 (failure policy contract):
```csharp
namespace Oragon.ElasticPool.Abstractions;

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
```

5. `Abstractions/IPoolItem.cs` — covariant disposable wrapper per RESEARCH Pattern 2:
```csharp
namespace Oragon.ElasticPool.Abstractions;

/// <summary>
/// Disposable wrapper around a pooled item. Disposing returns the item to the pool.
/// Idempotent: repeat Dispose / DisposeAsync calls are no-ops.
/// </summary>
public interface IPoolItem<out T> : IDisposable, IAsyncDisposable
{
    /// <summary>The pooled instance. Throws <see cref="ObjectDisposedException"/> if already disposed.</summary>
    T Value { get; }
}
```

6. `Abstractions/IElasticPool.cs` — public contract for the engine. Per RESEARCH Pitfall 5, expose read-only `MaxSize`/`MinSize`/`Available`/`InUse` for tests AND telemetry consumers. Per RESEARCH Pattern 5, expose `ReadyAsync()`. Per CONTEXT.md, sync `Acquire()` is fast-path only.
```csharp
using Oragon.ElasticPool.Abstractions;

namespace Oragon.ElasticPool.Abstractions;

/// <summary>
/// Generic, elastic pool of T. Phase 1 ships fixed-size behavior; elasticity arrives in Phase 2.
/// Disposing the pool drains in-flight items and invokes the Release hook on each remaining entry.
/// </summary>
public interface IElasticPool<T> : IDisposable, IAsyncDisposable
    where T : notnull
{
    /// <summary>Configured maximum pool size.</summary>
    int MaxSize { get; }
    /// <summary>Configured minimum pool size (floor maintained by Phase 2 sweep).</summary>
    int MinSize { get; }
    /// <summary>Items currently idle in the pool, available for immediate Acquire.</summary>
    int Available { get; }
    /// <summary>Items currently checked out by consumers.</summary>
    int InUse { get; }

    /// <summary>
    /// Synchronous fast-path: returns immediately if a free item exists.
    /// If no free item is available, throws <see cref="Exceptions.PoolExhaustedException"/>.
    /// NEVER blocks. Use <see cref="AcquireAsync"/> if you want to wait.
    /// </summary>
    IPoolItem<T> Acquire();

    /// <summary>
    /// Asynchronous acquire. Returns immediately if a free item exists; otherwise:
    ///   - WaitBehavior.Wait (default): waits until an item returns to the pool, respecting <paramref name="cancellationToken"/>.
    ///   - WaitBehavior.Throw: throws <see cref="Exceptions.PoolExhaustedException"/> immediately.
    /// Throws <see cref="ObjectDisposedException"/> if the pool has been disposed.
    /// </summary>
    ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the warm-up task. Awaitable for readiness probes; completes when the InitialSize
    /// items have all been created (or when warm-up is canceled by pool dispose).
    /// </summary>
    Task ReadyAsync();
}
```

7. `Hooks/HookDelegates.cs` — verbatim from RESEARCH Pattern 1:
```csharp
using Oragon.ElasticPool.Abstractions;

namespace Oragon.ElasticPool.Hooks;

/// <summary>Creates a new pooled instance. Required hook. Runs OUTSIDE pool locks — slow factory work never blocks the acquire fast path.</summary>
public delegate ValueTask<T> FactoryDelegate<T>(IServiceProvider services, CancellationToken cancellationToken);

/// <summary>Cheap on-borrow validation. MUST complete in &lt; 1 ms p99 (no server round-trips). Heavy probes belong in the Check hook (Phase 2 sweep).</summary>
public delegate ValueTask<PoolState> BeforeUseDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>Background health probe. Engine signature only in Phase 1; sweeper consumes in Phase 2.</summary>
public delegate ValueTask<PoolState> CheckDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>On-return validation. Default no-op in v1; activated via FAIL-V2 / HOOK-V2.</summary>
public delegate ValueTask<PoolState> AfterUseDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>Cleanup hook for evicted/discarded items (e.g., connection.CloseAsync()).</summary>
public delegate ValueTask ReleaseDelegate<T>(T item, CancellationToken cancellationToken);
```

8. `Builder/WaitBehavior.cs`:
```csharp
namespace Oragon.ElasticPool.Builder;

/// <summary>Strategy when AcquireAsync is called and the pool is exhausted.</summary>
public enum WaitBehavior
{
    /// <summary>Wait for an item to return; respects the caller's CancellationToken. (Default.)</summary>
    Wait = 0,
    /// <summary>Throw <see cref="Exceptions.PoolExhaustedException"/> immediately.</summary>
    Throw = 1,
}
```

9. `Builder/ElasticPoolOptions.cs` — frozen record per RESEARCH Pattern 3:
```csharp
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.Hooks;

namespace Oragon.ElasticPool.Builder;

/// <summary>Frozen, init-only configuration produced by <see cref="ElasticPoolBuilder{T}.Build"/>.</summary>
public sealed record ElasticPoolOptions<T>
    where T : notnull
{
    public required FactoryDelegate<T> Factory { get; init; }
    public BeforeUseDelegate<T>? BeforeUse { get; init; }
    public CheckDelegate<T>? Check { get; init; }
    public AfterUseDelegate<T>? AfterUse { get; init; }
    public ReleaseDelegate<T>? Release { get; init; }
    public required int MinSize { get; init; }
    public required int MaxSize { get; init; }
    public required int InitialSize { get; init; }
    public WaitBehavior WhenExhausted { get; init; } = WaitBehavior.Wait;
    public required IItemFailurePolicy<T> FailurePolicy { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public string PoolName { get; init; } = string.Empty;
}
```

10. `Builder/ElasticPoolBuilder.cs` — per RESEARCH Pattern 3, with one addition: `WithName(string)` so the DI extension can stamp the pool name into options for telemetry tagging:
```csharp
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.Hooks;
using Oragon.ElasticPool.Internals;
using Oragon.ElasticPool.Policies;

namespace Oragon.ElasticPool.Builder;

public sealed class ElasticPoolBuilder<T> where T : notnull
{
    private readonly IServiceProvider _services;
    private readonly CancellationToken _ct;
    private FactoryDelegate<T>? _factory;
    private BeforeUseDelegate<T>? _beforeUse;
    private CheckDelegate<T>? _check;
    private AfterUseDelegate<T>? _afterUse;
    private ReleaseDelegate<T>? _release;
    private int _minSize = 0;
    private int _maxSize = 1;
    private int _initialSize = 0;
    private WaitBehavior _whenExhausted = WaitBehavior.Wait;
    private IItemFailurePolicy<T>? _failurePolicy;
    private TimeProvider _timeProvider = TimeProvider.System;
    private string _name = string.Empty;

    internal ElasticPoolBuilder(IServiceProvider services, CancellationToken ct)
    { _services = services; _ct = ct; }

    public ElasticPoolBuilder<T> Factory(FactoryDelegate<T> factory)
    { _factory = factory ?? throw new ArgumentNullException(nameof(factory)); return this; }
    public ElasticPoolBuilder<T> BeforeUse(BeforeUseDelegate<T> hook) { _beforeUse = hook; return this; }
    public ElasticPoolBuilder<T> Check(CheckDelegate<T> hook) { _check = hook; return this; }
    public ElasticPoolBuilder<T> AfterUse(AfterUseDelegate<T> hook) { _afterUse = hook; return this; }
    public ElasticPoolBuilder<T> Release(ReleaseDelegate<T> hook) { _release = hook; return this; }
    public ElasticPoolBuilder<T> WithBounds(int minSize, int maxSize, int initialSize)
    { _minSize = minSize; _maxSize = maxSize; _initialSize = initialSize; return this; }
    public ElasticPoolBuilder<T> WhenExhausted(WaitBehavior behavior) { _whenExhausted = behavior; return this; }
    public ElasticPoolBuilder<T> WithFailurePolicy(IItemFailurePolicy<T> policy)
    { _failurePolicy = policy ?? throw new ArgumentNullException(nameof(policy)); return this; }
    public ElasticPoolBuilder<T> WithTimeProvider(TimeProvider timeProvider)
    { _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)); return this; }
    internal ElasticPoolBuilder<T> WithName(string name) { _name = name ?? string.Empty; return this; }

    public IElasticPool<T> Build()
    {
        if (_factory is null)
            throw new InvalidOperationException("Factory hook is required. Call .Factory(...) before .Build().");
        if (_minSize < 0)
            throw new ArgumentOutOfRangeException(nameof(_minSize), "MinSize must be >= 0.");
        if (_maxSize < 1)
            throw new ArgumentOutOfRangeException(nameof(_maxSize), "MaxSize must be >= 1.");
        if (_minSize > _maxSize)
            throw new InvalidOperationException($"MinSize ({_minSize}) cannot exceed MaxSize ({_maxSize}).");
        if (_initialSize < _minSize || _initialSize > _maxSize)
            throw new InvalidOperationException(
                $"InitialSize ({_initialSize}) must satisfy MinSize ({_minSize}) <= InitialSize <= MaxSize ({_maxSize}).");

        var options = new ElasticPoolOptions<T>
        {
            Factory = _factory,
            BeforeUse = _beforeUse,
            Check = _check,
            AfterUse = _afterUse,
            Release = _release,
            MinSize = _minSize,
            MaxSize = _maxSize,
            InitialSize = _initialSize,
            WhenExhausted = _whenExhausted,
            FailurePolicy = _failurePolicy ?? new DiscardAndReplaceFailurePolicy<T>(),
            TimeProvider = _timeProvider,
            PoolName = _name,
        };

        return new ElasticPool<T>(options, _services, _ct);
    }
}
```

11. `Builder/ElasticObjectPoolFactory.cs`:
```csharp
namespace Oragon.ElasticPool.Builder;

public static class ElasticObjectPoolFactory
{
    public static ElasticPoolBuilder<T> Build<T>(IServiceProvider services, CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        return new ElasticPoolBuilder<T>(services, cancellationToken);
    }
}
```

12. `Policies/DiscardAndReplaceFailurePolicy.cs`:
```csharp
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
```

13. `Exceptions/PoolExhaustedException.cs`:
```csharp
namespace Oragon.ElasticPool.Exceptions;

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
```

NOTE: At end of this task, build will FAIL because ElasticPool<T> (referenced by Builder) does not yet exist. That is expected — Task 2 implements the engine and unblocks the build. Do not stub `ElasticPool` in this task; doing so would create a partial type that must be merged later.

PUBLIC API surface tracking: do NOT update PublicAPI.Unshipped.txt yet — wait until Task 3 (after engine + DI complete). Build with `/p:RunAnalyzersDuringBuild=false` if needed during this task to bypass PublicApiAnalyzers transient errors.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && find src/Oragon.ElasticPool -name '*.cs' -type f | wc -l | awk '{ if ($1 < 13) { print "MISSING-FILES:" $1; exit 1 } else print "FILES-OK:" $1 }' && grep -r 'public delegate ValueTask<T> FactoryDelegate' src/Oragon.ElasticPool/Hooks/ && grep -r 'public sealed record ElasticPoolOptions' src/Oragon.ElasticPool/Builder/ && grep -r 'public IPoolItem<T> Acquire()' src/Oragon.ElasticPool/Abstractions/ && grep -r 'ValueTask<IPoolItem<T>> AcquireAsync' src/Oragon.ElasticPool/Abstractions/ && grep -r 'public sealed class PoolExhaustedException' src/Oragon.ElasticPool/Exceptions/ && grep -r 'public sealed class DiscardAndReplaceFailurePolicy' src/Oragon.ElasticPool/Policies/ && echo SURFACE-OK</automated>
  </verify>
  <done>13 source files exist with the verbatim shapes from RESEARCH.md Patterns 1–4. All public types declared; only `ElasticPool<T>` (engine, Task 2) is referenced but not yet created — build will be intentionally red until Task 2 lands. No stubs left behind.</done>
</task>

<task type="auto">
  <name>Task 2: Engine internals — PoolEntry, PoolItem wrapper, ElasticPool sealed engine, lifecycle state machine, telemetry, logging</name>
  <files>
    src/Oragon.ElasticPool/Internals/PoolEntry.cs,
    src/Oragon.ElasticPool/Internals/PoolLifecycle.cs,
    src/Oragon.ElasticPool/Internals/PoolItem.cs,
    src/Oragon.ElasticPool/Internals/ElasticPool.cs,
    src/Oragon.ElasticPool/Telemetry/PoolMeterNames.cs,
    src/Oragon.ElasticPool/Telemetry/TelemetryEmitter.cs,
    src/Oragon.ElasticPool/Telemetry/PoolDiagnosticsLog.cs
  </files>
  <action>
This task implements the sealed engine `ElasticPool<T>` plus all internal supporting types. After this task, the Core assembly compiles cleanly. Reference: RESEARCH Patterns 2 (PoolItem), 5 (warm-up), 6 (telemetry), 8 (CT linking), 9 (dispose state machine), and PITFALLS Pitfall 1/2/3/4.

1. `Internals/PoolLifecycle.cs`:
```csharp
namespace Oragon.ElasticPool.Internals;

internal enum PoolLifecycle { Open = 0, Draining = 1, Closed = 2 }
```

2. `Internals/PoolEntry.cs` — internal record stamping creation time (TimeProvider injection point per RESEARCH "TimeProvider injection point present"):
```csharp
namespace Oragon.ElasticPool.Internals;

internal sealed record PoolEntry<T>(T Item, DateTimeOffset CreatedAt) where T : notnull;
```

3. `Internals/PoolItem.cs` — verbatim from RESEARCH Pattern 2 (idempotent dispose, finalizer). Adapt namespace + add `_log` injection for leak warning (per RESEARCH Pitfall 4 and Example 3 LoggerMessage):
```csharp
using Microsoft.Extensions.Logging;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.Telemetry;

namespace Oragon.ElasticPool.Internals;

internal sealed class PoolItem<T> : IPoolItem<T>
    where T : notnull
{
    private readonly ElasticPool<T> _owner;
    private readonly PoolEntry<T> _entry;
    private int _disposed;

    public PoolItem(ElasticPool<T> owner, PoolEntry<T> entry)
    {
        _owner = owner;
        _entry = entry;
    }

    public T Value
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(PoolItem<T>));
            return _entry.Item;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        GC.SuppressFinalize(this);
        _owner.ReturnSync(_entry);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        GC.SuppressFinalize(this);
        return _owner.ReturnAsync(_entry);
    }

    ~PoolItem()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _owner.ReturnFromFinalizer(_entry);
        }
        catch
        {
            // Finalizers MUST NOT throw. Swallow per PITFALLS Pitfall 4.
        }
    }
}
```

4. `Telemetry/PoolMeterNames.cs` — verbatim from RESEARCH Pattern 6:
```csharp
namespace Oragon.ElasticPool.Telemetry;

internal static class PoolMeterNames
{
    public const string MeterName = "Oragon.ElasticPool";
    public const string AcquireCount = "pool.acquire.count";
    public const string FactoryFailures = "pool.factory.failures";
    public const string PoolNameTag = "pool.name";
}
```

5. `Telemetry/TelemetryEmitter.cs` — verbatim from RESEARCH Pattern 6 (with namespace adjustment + `Microsoft.Extensions.DependencyInjection.GetService` extension import):
```csharp
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace Oragon.ElasticPool.Telemetry;

internal sealed class TelemetryEmitter : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _acquireCount;
    private readonly Counter<long> _factoryFailures;
    private readonly KeyValuePair<string, object?> _poolNameTag;
    private readonly bool _ownsMeter;

    public TelemetryEmitter(IServiceProvider services, string poolName)
    {
        var factory = services.GetService<IMeterFactory>();
        if (factory is not null)
        {
            _meter = factory.Create(PoolMeterNames.MeterName);
            _ownsMeter = false;
        }
        else
        {
            _meter = new Meter(PoolMeterNames.MeterName);
            _ownsMeter = true;
        }

        _acquireCount = _meter.CreateCounter<long>(
            PoolMeterNames.AcquireCount,
            unit: "{acquires}",
            description: "Total number of successful Acquire calls.");
        _factoryFailures = _meter.CreateCounter<long>(
            PoolMeterNames.FactoryFailures,
            unit: "{failures}",
            description: "Total number of times the Factory hook threw an exception.");
        _poolNameTag = new KeyValuePair<string, object?>(PoolMeterNames.PoolNameTag, poolName);
    }

    public void OnAcquire() => _acquireCount.Add(1, _poolNameTag);
    public void OnFactoryFailure() => _factoryFailures.Add(1, _poolNameTag);

    public void Dispose()
    {
        if (_ownsMeter) _meter.Dispose();
    }
}
```

6. `Telemetry/PoolDiagnosticsLog.cs` — extension-method LoggerMessage source-gen per RESEARCH Example 3:
```csharp
using Microsoft.Extensions.Logging;

namespace Oragon.ElasticPool.Telemetry;

internal static partial class PoolDiagnosticsLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "Pool item leaked (consumer forgot to dispose). Returning defensively to pool '{PoolName}'.")]
    public static partial void ItemLeaked(this ILogger logger, string poolName);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Factory hook threw for pool '{PoolName}'. Counter rolled back.")]
    public static partial void FactoryFailed(this ILogger logger, string poolName, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "BeforeUse hook reported Unhealthy for pool '{PoolName}'. Invoking failure policy.")]
    public static partial void BeforeUseUnhealthy(this ILogger logger, string poolName);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
        Message = "Release hook threw during pool dispose for pool '{PoolName}'. Continuing drain.")]
    public static partial void ReleaseHookFailedDuringDispose(this ILogger logger, string poolName, Exception exception);
}
```

7. `Internals/ElasticPool.cs` — the sealed engine. Combine RESEARCH Pattern 5 (warm-up), Pattern 8 (CT linking), Pattern 9 (dispose), Pitfall 3 (counter rollback), Pitfall 1 (waiter queue lost-wakeup avoidance via Channel<TCS> direct-handoff). This is the largest file; aim ~250–350 lines.

```csharp
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.Builder;
using Oragon.ElasticPool.Exceptions;
using Oragon.ElasticPool.Telemetry;

namespace Oragon.ElasticPool.Internals;

internal sealed class ElasticPool<T> : IElasticPool<T>
    where T : notnull
{
    private readonly ElasticPoolOptions<T> _options;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ConcurrentQueue<PoolEntry<T>> _idle = new();
    // Direct-handoff waiter queue per RESEARCH Pitfall 1.
    private readonly Channel<TaskCompletionSource<PoolEntry<T>>> _waiters;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly TelemetryEmitter _telemetry;
    private readonly ILogger _log;

    private int _total;       // total live items (idle + in-use + being-created)
    private int _inUse;       // items currently checked out
    private int _lifecycle = (int)PoolLifecycle.Open;

    public Task WarmupTask { get; }

    public int MaxSize => _options.MaxSize;
    public int MinSize => _options.MinSize;
    public int Available => _idle.Count;
    public int InUse => Volatile.Read(ref _inUse);

    internal ElasticPool(ElasticPoolOptions<T> options, IServiceProvider services, CancellationToken ct)
    {
        _options = options;
        _services = services;
        _time = options.TimeProvider;
        _waiters = Channel.CreateUnbounded<TaskCompletionSource<PoolEntry<T>>>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _telemetry = new TelemetryEmitter(services, options.PoolName);

        // Best-effort logger; if Logging is not registered, use NullLogger.
        var loggerFactory = services.GetService<ILoggerFactory>();
        _log = loggerFactory?.CreateLogger($"Oragon.ElasticPool.{typeof(T).Name}") ?? NullLogger.Instance;

        WarmupTask = WarmupAsync(_lifetimeCts.Token);
    }

    public Task ReadyAsync() => WarmupTask;

    private async Task WarmupAsync(CancellationToken ct)
    {
        if (_options.InitialSize == 0) return;

        var tasks = new Task[_options.InitialSize];
        for (int i = 0; i < _options.InitialSize; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                Interlocked.Increment(ref _total);
                try
                {
                    var item = await _options.Factory(_services, ct).ConfigureAwait(false);
                    _idle.Enqueue(new PoolEntry<T>(item, _time.GetUtcNow()));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Interlocked.Decrement(ref _total);
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Decrement(ref _total);
                    _telemetry.OnFactoryFailure();
                    _log.FactoryFailed(_options.PoolName, ex);
                    throw;
                }
            }, ct);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public IPoolItem<T> Acquire()
    {
        ThrowIfDisposed();
        if (_idle.TryDequeue(out var entry))
        {
            Interlocked.Increment(ref _inUse);
            _telemetry.OnAcquire();
            return new PoolItem<T>(this, entry);
        }
        // Sync NEVER blocks (per CONTEXT.md + RESEARCH Open Question 3).
        throw new PoolExhaustedException(_options.MaxSize);
    }

    public async ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        var ct = linked.Token;

        // Fast path: free item available.
        if (_idle.TryDequeue(out var entry))
        {
            return await PrepareForUseAsync(entry, ct).ConfigureAwait(false);
        }

        // Try to grow up to MaxSize (Phase 1 = on-demand creation up to MaxSize, no elastic signal).
        while (true)
        {
            int currentTotal = Volatile.Read(ref _total);
            if (currentTotal >= _options.MaxSize) break;
            if (Interlocked.CompareExchange(ref _total, currentTotal + 1, currentTotal) == currentTotal)
            {
                // Reservation succeeded. Build new item OUTSIDE any lock per HOOK-01.
                T newItem;
                try
                {
                    newItem = await _options.Factory(_services, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Decrement(ref _total); // counter rollback per PITFALLS Pitfall 3
                    _telemetry.OnFactoryFailure();
                    _log.FactoryFailed(_options.PoolName, ex);
                    await _options.FailurePolicy.HandleAsync(default, FailureKind.FactoryThrew, ex, ct).ConfigureAwait(false);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Decrement(ref _total);
                    throw;
                }
                var fresh = new PoolEntry<T>(newItem, _time.GetUtcNow());
                return await PrepareForUseAsync(fresh, ct).ConfigureAwait(false);
            }
            // CAS failed → retry (another thread created an item or grew).
        }

        // Pool is at MaxSize and exhausted.
        if (_options.WhenExhausted == WaitBehavior.Throw)
            throw new PoolExhaustedException(_options.MaxSize);

        // WaitBehavior.Wait — direct-handoff waiter.
        var tcs = new TaskCompletionSource<PoolEntry<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _waiters.Writer.WriteAsync(tcs, ct).ConfigureAwait(false);

        using var registration = ct.Register(static state =>
        {
            var t = (TaskCompletionSource<PoolEntry<T>>)state!;
            t.TrySetCanceled();
        }, tcs);

        try
        {
            var awaited = await tcs.Task.ConfigureAwait(false);
            return await PrepareForUseAsync(awaited, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Re-throw caller's CT semantics per RESEARCH Pattern 8.
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new ObjectDisposedException(nameof(ElasticPool<T>), "Pool was disposed during AcquireAsync.");
        }
    }

    private async ValueTask<IPoolItem<T>> PrepareForUseAsync(PoolEntry<T> entry, CancellationToken ct)
    {
        if (_options.BeforeUse is { } beforeUse)
        {
            PoolState state;
            try
            {
                state = await beforeUse(entry.Item, ct).ConfigureAwait(false);
            }
            catch
            {
                // BeforeUse threw — treat as Unhealthy.
                state = PoolState.Unhealthy;
            }
            if (state == PoolState.Unhealthy)
            {
                _log.BeforeUseUnhealthy(_options.PoolName);
                Interlocked.Decrement(ref _total); // discarding broken item
                await _options.FailurePolicy.HandleAsync(entry.Item, FailureKind.BeforeUseUnhealthy, null, ct).ConfigureAwait(false);
                if (_options.Release is { } release)
                {
                    try { await release(entry.Item, ct).ConfigureAwait(false); } catch { /* swallow */ }
                }
                // Replace by recursing the acquire path (will create a new item up to MaxSize).
                return await AcquireAsync(ct).ConfigureAwait(false);
            }
        }
        Interlocked.Increment(ref _inUse);
        _telemetry.OnAcquire();
        return new PoolItem<T>(this, entry);
    }

    // Called from PoolItem.Dispose() — synchronous return path.
    internal void ReturnSync(PoolEntry<T> entry)
    {
        Interlocked.Decrement(ref _inUse);
        if (Volatile.Read(ref _lifecycle) != (int)PoolLifecycle.Open)
        {
            // Pool is being disposed — invoke Release best-effort, do not requeue.
            TryReleaseFireAndForget(entry);
            return;
        }
        if (TryHandoff(entry)) return;
        _idle.Enqueue(entry);
    }

    // Called from PoolItem.DisposeAsync() — async return path (AfterUse can run async; Phase 1 default no-op).
    internal async ValueTask ReturnAsync(PoolEntry<T> entry)
    {
        if (_options.AfterUse is { } afterUse)
        {
            try
            {
                var state = await afterUse(entry.Item, CancellationToken.None).ConfigureAwait(false);
                if (state == PoolState.Unhealthy)
                {
                    Interlocked.Decrement(ref _inUse);
                    Interlocked.Decrement(ref _total);
                    if (_options.Release is { } release)
                    {
                        try { await release(entry.Item, CancellationToken.None).ConfigureAwait(false); } catch { /* swallow */ }
                    }
                    return;
                }
            }
            catch { /* AfterUse failure → still return defensively */ }
        }
        ReturnSync(entry);
    }

    // Called from PoolItem finalizer — sync only, never throws, no Release hook (per PITFALLS Pitfall 4).
    internal void ReturnFromFinalizer(PoolEntry<T> entry)
    {
        _log.ItemLeaked(_options.PoolName);
        ReturnSync(entry);
    }

    private bool TryHandoff(PoolEntry<T> entry)
    {
        // Try to wake exactly one waiter.
        while (_waiters.Reader.TryRead(out var tcs))
        {
            if (tcs.TrySetResult(entry)) return true;
            // Waiter was canceled — drop it, try next.
        }
        return false;
    }

    private void TryReleaseFireAndForget(PoolEntry<T> entry)
    {
        if (_options.Release is { } release)
        {
            _ = Task.Run(async () =>
            {
                try { await release(entry.Item, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _log.ReleaseHookFailedDuringDispose(_options.PoolName, ex); }
            });
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _lifecycle) != (int)PoolLifecycle.Open)
            throw new ObjectDisposedException(nameof(ElasticPool<T>));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _lifecycle, (int)PoolLifecycle.Closed) == (int)PoolLifecycle.Closed)
            return;

        // Cancel pending waiters and warm-up.
        try { _lifetimeCts.Cancel(); } catch { /* swallow */ }

        // Complete waiter channel so any in-flight WriteAsync throws.
        _waiters.Writer.TryComplete();

        // Drain remaining waiters → cancel them.
        while (_waiters.Reader.TryRead(out var tcs))
        {
            tcs.TrySetCanceled();
        }

        // Drain idle queue → invoke Release on each.
        while (_idle.TryDequeue(out var entry))
        {
            if (_options.Release is { } release)
            {
                try { await release(entry.Item, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _log.ReleaseHookFailedDuringDispose(_options.PoolName, ex); }
            }
        }

        try { _lifetimeCts.Dispose(); } catch { /* swallow */ }
        _telemetry.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        // Standard non-host DI may only call Dispose. Block-with-fallback per PITFALLS Pitfall 8.
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
```

Internal correctness notes (validated by Plan 03 tests):
- `Channel<TCS>` direct-handoff: only one waiter receives any returned entry, eliminating split free-list/waiter races.
- Counter rollback: every `Interlocked.Increment(ref _total)` has a paired `Decrement` on the failure path.
- Cancellation: `linked` CTS is `using` (no leak per PITFALLS Pitfall 2); registration on the TCS is also `using`.
- Dispose: idempotent via `Interlocked.Exchange`. Drain order: waiters → idle (Release each) → telemetry.
- `BeforeUse` recursion: at most `MaxSize` deep, bounded because `_total` only increments via CAS on capacity slots; in practice the path is tail-call-shaped.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && find src/Oragon.ElasticPool/Internals -name '*.cs' | xargs grep -l 'sealed class ElasticPool' && grep -q 'Channel<TaskCompletionSource' src/Oragon.ElasticPool/Internals/ElasticPool.cs && grep -q 'Interlocked.Decrement(ref _total)' src/Oragon.ElasticPool/Internals/ElasticPool.cs && grep -q 'Interlocked.Exchange(ref _disposed' src/Oragon.ElasticPool/Internals/PoolItem.cs && grep -q '\[LoggerMessage(EventId = 1001' src/Oragon.ElasticPool/Telemetry/PoolDiagnosticsLog.cs && grep -q 'IMeterFactory' src/Oragon.ElasticPool/Telemetry/TelemetryEmitter.cs && dotnet build src/Oragon.ElasticPool/Oragon.ElasticPool.csproj -c Release -p:RunAnalyzersDuringBuild=false 2>&1 | tee /tmp/build2.log && grep -qE 'Build succeeded' /tmp/build2.log && echo ENGINE-BUILD-OK</automated>
  </verify>
  <done>All 7 internal/telemetry files exist. `ElasticPool<T>` implements `IElasticPool<T>` with Channel direct-handoff waiter queue, Interlocked counter rollback on factory failure, idempotent Dispose, drain semantics on dispose. PoolItem has Interlocked dispose flag, finalizer that swallows exceptions. TelemetryEmitter resolves IMeterFactory or falls back to `new Meter`. PoolDiagnosticsLog uses [LoggerMessage] source-gen. `dotnet build` of Core project succeeds (PublicApiAnalyzers may still emit warnings — addressed in Task 3).</done>
</task>

<task type="auto">
  <name>Task 3: DI extension + PublicApiAnalyzers Unshipped baseline + final build verification</name>
  <files>
    src/Oragon.ElasticPool/DependencyInjection/ServiceCollectionExtensions.cs,
    src/Oragon.ElasticPool/PublicAPI.Unshipped.txt
  </files>
  <action>
This task closes the loop: wire DI registration and bring PublicApiAnalyzers green.

1. `DependencyInjection/ServiceCollectionExtensions.cs` — per RESEARCH Pattern 7. Important: do NOT take a hard dependency on `Microsoft.Extensions.Hosting.Abstractions`; the optional `IHostApplicationLifetime` resolution is via `GetService<>` (nullable). The `ElasticPoolBuilderConfigurator<T>` carries the user-supplied `Action<ElasticPoolBuilder<T>>` through Options.
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.Builder;

namespace Oragon.ElasticPool.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IElasticPool{T}"/> in DI under the given <paramref name="name"/>.
    /// Use <c>string.Empty</c> for single-pool apps (also resolvable as non-keyed <c>IElasticPool&lt;T&gt;</c>).
    /// Multiple pools of the same T can coexist by name.
    /// </summary>
    public static IServiceCollection AddElasticPool<T>(
        this IServiceCollection services,
        string name,
        Action<ElasticPoolBuilder<T>> configure)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<ElasticPoolBuilderConfigurator<T>>(name)
                .Configure(c => c.Configure = configure);

        services.TryAddKeyedSingleton<IElasticPool<T>>(name, (sp, key) =>
        {
            var keyName = (string)key!;
            var configurator = sp.GetRequiredService<IOptionsMonitor<ElasticPoolBuilderConfigurator<T>>>().Get(keyName);
            // Optional: pull application-stopping CT if the host registers it (no hard dep).
            var lifetimeCt = TryGetHostApplicationStoppingToken(sp);
            var builder = ElasticObjectPoolFactory.Build<T>(sp, lifetimeCt);
            // Internal — see Builder/ElasticPoolBuilder.cs WithName(...).
            BuilderInternals.SetName(builder, keyName);
            configurator.Configure(builder);
            return builder.Build();
        });

        if (name.Length == 0)
        {
            services.TryAddSingleton<IElasticPool<T>>(sp => sp.GetRequiredKeyedService<IElasticPool<T>>(string.Empty));
        }

        return services;
    }

    private static CancellationToken TryGetHostApplicationStoppingToken(IServiceProvider sp)
    {
        // Resolve IHostApplicationLifetime by name without a Hosting.Abstractions reference.
        // We probe for any registered service exposing an "ApplicationStopping" CancellationToken property.
        var candidate = sp.GetServices<object>()
            .FirstOrDefault(s => s?.GetType().Name == "IHostApplicationLifetime");
        if (candidate is null) return CancellationToken.None;
        var prop = candidate.GetType().GetProperty("ApplicationStopping");
        if (prop?.GetValue(candidate) is CancellationToken ct) return ct;
        return CancellationToken.None;
    }

    private sealed class ElasticPoolBuilderConfigurator<T> where T : notnull
    {
        public Action<ElasticPoolBuilder<T>> Configure { get; set; } = static _ => { };
    }

    // Internal sidedoor to call ElasticPoolBuilder<T>.WithName(...) from the same assembly.
    internal static class BuilderInternals
    {
        public static void SetName<T>(ElasticPoolBuilder<T> builder, string name) where T : notnull
        {
            // Method exists on the builder as `internal ElasticPoolBuilder<T> WithName(string)`.
            builder.GetType().GetMethod("WithName",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(builder, new object[] { name });
        }
    }
}
```

NOTE on the reflection sidedoor: The cleaner alternative is to mark `ElasticPoolBuilder<T>.WithName` as `internal` and add `[InternalsVisibleTo("Oragon.ElasticPool")]` is unnecessary because both files are in the same assembly. Replace the reflection call with a direct `builder.WithName(keyName)` call (since `WithName` is `internal` in the same assembly, this works).

REVISED Task 3 simplification — replace `BuilderInternals` block with a direct call:
```csharp
            var builder = ElasticObjectPoolFactory.Build<T>(sp, lifetimeCt);
            builder.WithName(keyName);  // internal method, same assembly
            configurator.Configure(builder);
            return builder.Build();
```
And delete `BuilderInternals` static class. This is the executor's preferred path. Reflection is the fallback only if file-organization concerns block the direct call.

The `TryGetHostApplicationStoppingToken` reflection probe IS legitimate — it avoids pulling `Microsoft.Extensions.Hosting.Abstractions` into Core. Per RESEARCH Pattern 7 note: "IHostApplicationLifetime reference is optional — pulled from IServiceProvider if available, else CancellationToken.None. This avoids forcing a Microsoft.Extensions.Hosting.Abstractions dependency on Core."

2. `src/Oragon.ElasticPool/PublicAPI.Unshipped.txt` — Populate with all NEW public API surface introduced in Phase 1. Format: one declaration per line, alphabetically sorted, prefixed with `#nullable enable`. Run a build first; PublicApiAnalyzers (RS0016) will list every missing declaration. Copy each into Unshipped.txt.

Expected entries (alphabetically by type, then by member). Generate from compiler output rather than handwriting:
```
#nullable enable
Oragon.ElasticPool.Abstractions.FailureDecision
Oragon.ElasticPool.Abstractions.FailureDecision.Discard = 0 -> Oragon.ElasticPool.Abstractions.FailureDecision
Oragon.ElasticPool.Abstractions.FailureKind
Oragon.ElasticPool.Abstractions.FailureKind.AfterUseUnhealthy = 2 -> Oragon.ElasticPool.Abstractions.FailureKind
Oragon.ElasticPool.Abstractions.FailureKind.BeforeUseUnhealthy = 1 -> Oragon.ElasticPool.Abstractions.FailureKind
Oragon.ElasticPool.Abstractions.FailureKind.FactoryThrew = 0 -> Oragon.ElasticPool.Abstractions.FailureKind
Oragon.ElasticPool.Abstractions.IElasticPool<T>
Oragon.ElasticPool.Abstractions.IElasticPool<T>.Acquire() -> Oragon.ElasticPool.Abstractions.IPoolItem<T>!
Oragon.ElasticPool.Abstractions.IElasticPool<T>.AcquireAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.ValueTask<Oragon.ElasticPool.Abstractions.IPoolItem<T>!>
Oragon.ElasticPool.Abstractions.IElasticPool<T>.Available.get -> int
Oragon.ElasticPool.Abstractions.IElasticPool<T>.InUse.get -> int
Oragon.ElasticPool.Abstractions.IElasticPool<T>.MaxSize.get -> int
Oragon.ElasticPool.Abstractions.IElasticPool<T>.MinSize.get -> int
Oragon.ElasticPool.Abstractions.IElasticPool<T>.ReadyAsync() -> System.Threading.Tasks.Task!
Oragon.ElasticPool.Abstractions.IItemFailurePolicy<T>
Oragon.ElasticPool.Abstractions.IItemFailurePolicy<T>.HandleAsync(T? failedItem, Oragon.ElasticPool.Abstractions.FailureKind failureKind, System.Exception? exception, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.ValueTask<Oragon.ElasticPool.Abstractions.FailureDecision>
Oragon.ElasticPool.Abstractions.IPoolItem<T>
Oragon.ElasticPool.Abstractions.IPoolItem<T>.Value.get -> T
Oragon.ElasticPool.Abstractions.PoolState
Oragon.ElasticPool.Abstractions.PoolState.Healthy = 0 -> Oragon.ElasticPool.Abstractions.PoolState
Oragon.ElasticPool.Abstractions.PoolState.Unhealthy = 1 -> Oragon.ElasticPool.Abstractions.PoolState
Oragon.ElasticPool.Builder.ElasticObjectPoolFactory
static Oragon.ElasticPool.Builder.ElasticObjectPoolFactory.Build<T>(System.IServiceProvider! services, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.AfterUse(Oragon.ElasticPool.Hooks.AfterUseDelegate<T>! hook) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.BeforeUse(Oragon.ElasticPool.Hooks.BeforeUseDelegate<T>! hook) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.Build() -> Oragon.ElasticPool.Abstractions.IElasticPool<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.Check(Oragon.ElasticPool.Hooks.CheckDelegate<T>! hook) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.Factory(Oragon.ElasticPool.Hooks.FactoryDelegate<T>! factory) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.Release(Oragon.ElasticPool.Hooks.ReleaseDelegate<T>! hook) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.WhenExhausted(Oragon.ElasticPool.Builder.WaitBehavior behavior) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.WithBounds(int minSize, int maxSize, int initialSize) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.WithFailurePolicy(Oragon.ElasticPool.Abstractions.IItemFailurePolicy<T>! policy) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.WithTimeProvider(System.TimeProvider! timeProvider) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.ElasticPoolOptions() -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.AfterUse.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.AfterUse.get -> Oragon.ElasticPool.Hooks.AfterUseDelegate<T>?
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.BeforeUse.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.BeforeUse.get -> Oragon.ElasticPool.Hooks.BeforeUseDelegate<T>?
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.Check.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.Check.get -> Oragon.ElasticPool.Hooks.CheckDelegate<T>?
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.FailurePolicy.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.FailurePolicy.get -> Oragon.ElasticPool.Abstractions.IItemFailurePolicy<T>!
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.Factory.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.Factory.get -> Oragon.ElasticPool.Hooks.FactoryDelegate<T>!
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.InitialSize.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.InitialSize.get -> int
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.MaxSize.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.MaxSize.get -> int
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.MinSize.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.MinSize.get -> int
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.PoolName.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.PoolName.get -> string!
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.Release.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.Release.get -> Oragon.ElasticPool.Hooks.ReleaseDelegate<T>?
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.TimeProvider.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.TimeProvider.get -> System.TimeProvider!
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.WhenExhausted.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.WhenExhausted.get -> Oragon.ElasticPool.Builder.WaitBehavior
Oragon.ElasticPool.Builder.WaitBehavior
Oragon.ElasticPool.Builder.WaitBehavior.Throw = 1 -> Oragon.ElasticPool.Builder.WaitBehavior
Oragon.ElasticPool.Builder.WaitBehavior.Wait = 0 -> Oragon.ElasticPool.Builder.WaitBehavior
Oragon.ElasticPool.DependencyInjection.ServiceCollectionExtensions
static Oragon.ElasticPool.DependencyInjection.ServiceCollectionExtensions.AddElasticPool<T>(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, string! name, System.Action<Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!>! configure) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
Oragon.ElasticPool.Exceptions.PoolExhaustedException
Oragon.ElasticPool.Exceptions.PoolExhaustedException.MaxSize.get -> int
Oragon.ElasticPool.Exceptions.PoolExhaustedException.PoolExhaustedException(int maxSize, System.TimeSpan? waitTime = null) -> void
Oragon.ElasticPool.Exceptions.PoolExhaustedException.WaitTime.get -> System.TimeSpan?
Oragon.ElasticPool.Hooks.AfterUseDelegate<T>
Oragon.ElasticPool.Hooks.BeforeUseDelegate<T>
Oragon.ElasticPool.Hooks.CheckDelegate<T>
Oragon.ElasticPool.Hooks.FactoryDelegate<T>
Oragon.ElasticPool.Hooks.ReleaseDelegate<T>
Oragon.ElasticPool.Policies.DiscardAndReplaceFailurePolicy<T>
Oragon.ElasticPool.Policies.DiscardAndReplaceFailurePolicy<T>.DiscardAndReplaceFailurePolicy() -> void
Oragon.ElasticPool.Policies.DiscardAndReplaceFailurePolicy<T>.HandleAsync(T? failedItem, Oragon.ElasticPool.Abstractions.FailureKind failureKind, System.Exception? exception, System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.ValueTask<Oragon.ElasticPool.Abstractions.FailureDecision>
```
Workflow: do an initial `dotnet build` of Core, capture every RS0016 line ("Symbol ... is not part of the declared API"), and append the suggested public-API line to PublicAPI.Unshipped.txt. Most IDEs offer a "Add to public API" code-fix that does this automatically — use it where available.

3. Final verification: build Core + Tests projects together, ensuring no analyzer or compiler errors remain. Confirm `dotnet build Oragon.ElasticPool.sln -c Release` is fully green with `TreatWarningsAsErrors=true`. Run the placeholder smoke test to confirm the assembly loads.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && test -f src/Oragon.ElasticPool/DependencyInjection/ServiceCollectionExtensions.cs && grep -q 'AddKeyedSingleton' src/Oragon.ElasticPool/DependencyInjection/ServiceCollectionExtensions.cs && grep -q 'IOptionsMonitor' src/Oragon.ElasticPool/DependencyInjection/ServiceCollectionExtensions.cs && grep -q '#nullable enable' src/Oragon.ElasticPool/PublicAPI.Unshipped.txt && grep -c 'Oragon.ElasticPool' src/Oragon.ElasticPool/PublicAPI.Unshipped.txt | awk '{ if ($1 < 30) { print "TOO-FEW-API-LINES:" $1; exit 1 } else print "API-LINES-OK:" $1 }' && dotnet build Oragon.ElasticPool.sln -c Release 2>&1 | tee /tmp/build3.log && grep -qE 'Build succeeded' /tmp/build3.log && ! grep -qE '\b(error|Error) (CS|RS)[0-9]' /tmp/build3.log && dotnet test tests/Oragon.ElasticPool.Tests --no-build -c Release 2>&1 | tee /tmp/test3.log && grep -qE '(Passed:.*1|Passed!.*1)' /tmp/test3.log && echo PLAN-02-DONE</automated>
  </verify>
  <done>DI extension `services.AddElasticPool<T>(name, configure)` registered with named-options + keyed singleton + non-keyed fallback for default name. PublicAPI.Unshipped.txt populated with all new public types/members (>= 30 API lines). `dotnet build Oragon.ElasticPool.sln -c Release` is fully green with `TreatWarningsAsErrors=true`. Smoke test from Plan 01 still passes (Core assembly loads under the test runner).</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| Consumer code → IElasticPool<T> | Untrusted hook delegates supplied by consumer; engine must not assume well-behavior |
| Hook delegate → engine internals | Hook may throw, hang, return null, or attempt to escape via captured state |
| Pool engine → CancellationToken sources | Linked CTSes leak registrations if not disposed |
| Pool dispose → in-flight consumers | Consumers holding IPoolItem<T> after pool dispose must not corrupt engine state |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-01-07 | Tampering | Counter rollback path on Factory throw | mitigate | `try { Factory } catch { Interlocked.Decrement(_total); throw }` — always paired; Plan 03 stress test verifies counter reaches MaxSize after N factory throws |
| T-01-08 | Denial of Service | Waiter-queue lost wakeup under burst→drain | mitigate | `Channel<TaskCompletionSource>` direct-handoff (RESEARCH Pitfall 1); `MaxSize=1` ping-pong stress test in Plan 03 with 5s watchdog enforces |
| T-01-09 | Information Disclosure | Linked CTS registration leak on _lifetimeCts | mitigate | Every `CreateLinkedTokenSource` is wrapped in `using` per RESEARCH Pattern 8 / Pitfall 2; Plan 03 may add a memory-stable test if needed |
| T-01-10 | Tampering | Double-dispose of IPoolItem<T> double-returns to pool, corrupting Available | mitigate | `Interlocked.Exchange(ref _disposed, 1)` flag, guard at top of Dispose/DisposeAsync; Plan 03 test "double-dispose is idempotent" |
| T-01-11 | Elevation of Privilege | Finalizer thread runs Release hook with potentially unsafe context | mitigate | Finalizer NEVER invokes Release hook; only logs leak + sync-returns entry per PITFALLS Pitfall 4 |
| T-01-12 | Repudiation | No telemetry signal when Factory fails | mitigate | `pool.factory.failures` counter incremented on every catch; Plan 03 test asserts via MetricCollector |
| T-01-13 | Denial of Service | BeforeUse-Unhealthy recursion in PrepareForUseAsync could exhaust stack on repeated unhealthy items | accept | Each iteration decrements _total then re-acquires under MaxSize bound; pathological case (every factory call returns Unhealthy item) is a consumer logic bug. Plan 03 verifies behavior with bounded test (3 unhealthy then healthy). v1.x can add iteration-count guard if reports surface. |
| T-01-14 | Tampering | Pool consumer mutates returned T outside lifetime → returns broken item | accept | Pool semantics inherit from consumer's contract; mitigated by BeforeUse hook, which is consumer's responsibility |
| T-01-15 | Information Disclosure | Reflection sidedoor for IHostApplicationLifetime probes any registered service | accept | Probe is read-only; only observes service whose type-name matches; never invokes side-effectful methods. Acceptable trade vs. forcing Hosting.Abstractions on Core. |
</threat_model>

<verification>
After all 3 tasks complete:

```bash
cd /mnt/p/dynamic-pool
dotnet build Oragon.ElasticPool.sln -c Release   # GREEN, no warnings
dotnet test tests/Oragon.ElasticPool.Tests --no-build -c Release  # 1 passed (Plan 01 smoke; Plan 03 adds real tests)
```

Spot-check public surface:
- `grep -r "public delegate ValueTask" src/Oragon.ElasticPool/Hooks/` — 5 delegates, every one accepts CancellationToken
- `grep -r "Interlocked\." src/Oragon.ElasticPool/Internals/` — counters use atomic ops; dispose flag uses Exchange
- `grep "Channel<TaskCompletionSource" src/Oragon.ElasticPool/Internals/ElasticPool.cs` — direct-handoff waiter
- `grep "IMeterFactory" src/Oragon.ElasticPool/Telemetry/TelemetryEmitter.cs` — DI resolution + fallback
- `grep "AddKeyedSingleton" src/Oragon.ElasticPool/DependencyInjection/ServiceCollectionExtensions.cs` — keyed registration

Confirm PublicApiAnalyzers is silent:
- No RS0016 ("not part of declared API") errors in build output
- No RS0017 ("removed from declared API") — none removed in this plan
</verification>

<success_criteria>
This plan is complete when:
- [ ] All 22 source files exist and contain the specified content
- [ ] `dotnet build Oragon.ElasticPool.sln -c Release` is green with TreatWarningsAsErrors=true
- [ ] PublicApiAnalyzers reports no errors (Unshipped.txt fully reflects new surface)
- [ ] `IElasticPool<T>` exposes Acquire / AcquireAsync / ReadyAsync / MaxSize / MinSize / Available / InUse
- [ ] `IPoolItem<T>` is covariant (`out T`), exposes `Value`, implements both Dispose and DisposeAsync idempotently
- [ ] All five hook delegates accept CancellationToken and return ValueTask<...>
- [ ] Builder validates `Factory != null`, `0 ≤ Min ≤ Initial ≤ Max`; throws on violation
- [ ] Engine uses `Channel<TaskCompletionSource<PoolEntry<T>>>` for waiter direct-handoff (no `SemaphoreSlim`+`ConcurrentQueue` split)
- [ ] Engine increments `_total` BEFORE invoking Factory; decrements in catch (counter rollback)
- [ ] On `BeforeUse → Unhealthy`, engine invokes failure policy with `FailureKind.BeforeUseUnhealthy` and replaces item
- [ ] `DiscardAndReplaceFailurePolicy<T>` always returns `FailureDecision.Discard`
- [ ] `WaitBehavior.Throw` raises `PoolExhaustedException` immediately when at MaxSize
- [ ] Sync `Acquire()` throws `PoolExhaustedException` when no idle item (NEVER blocks)
- [ ] `DisposeAsync` cancels lifetime CTS, drains waiters (cancels them), drains idle queue (calls Release on each), is idempotent
- [ ] Sync `Dispose()` calls `DisposeAsync().AsTask().GetAwaiter().GetResult()`
- [ ] `services.AddElasticPool<T>(name, configure)` registers as keyed singleton; default name `string.Empty` also resolvable as non-keyed
- [ ] `Meter` named `"Oragon.ElasticPool"` resolved via `IMeterFactory` if available, else via `new Meter`
- [ ] At minimum `pool.acquire.count` and `pool.factory.failures` counters exist, both tagged with `pool.name`
- [ ] `[LoggerMessage]` source-gen used for ItemLeaked, FactoryFailed, BeforeUseUnhealthy, ReleaseHookFailedDuringDispose
- [ ] Plan 01 smoke test still passes (assembly loads under test runner)
</success_criteria>

<source_coverage_audit>
## Phase 1 Sources → This Plan

**Phase 1 success criteria (from ROADMAP) addressed by this plan:**
- Criterion 1 (DI registration + sync Acquire + await using + idempotent dispose): ENABLED here (verified by tests in Plan 03)
- Criterion 2 (MaxSize=1 ping-pong, no deadlocks/lost wake-ups): ENABLED here via Channel direct-handoff (verified by stress test in Plan 03)
- Criterion 3 (Factory throw counter rollback + BeforeUse Unhealthy → policy): IMPLEMENTED here (verified by tests in Plan 03)
- Criterion 4 (Meter "Oragon.ElasticPool" via IMeterFactory + counters): IMPLEMENTED here (verified by MetricCollector test in Plan 03)
- Criterion 5 (IDisposable + IAsyncDisposable with drain): IMPLEMENTED here (verified by tests in Plan 03)
- Criterion 6 (Eager warm-up awaitable + bounds validation): IMPLEMENTED here (verified by tests in Plan 03)

**Requirements (REQ) addressed:**
- API-01: `IElasticPool<T>` with sync + async — Task 1
- API-02: `IPoolItem<T>` wrapper, idempotent — Task 1 (interface) + Task 2 (impl)
- API-03: `ElasticObjectPoolFactory.Build<T>(...)` builder + validation — Task 1
- HOOK-01: Factory required, runs outside locks — Task 1 (delegate) + Task 2 (engine awaits factory after Interlocked CAS, before adding entry to idle)
- HOOK-02: BeforeUse optional, fires in Acquire, failure → policy — Task 1 (delegate) + Task 2 (PrepareForUseAsync)
- HOOK-03: Check optional, signature only in Phase 1 — Task 1 (delegate)
- HOOK-04: AfterUse optional, default no-op — Task 1 (delegate) + Task 2 (ReturnAsync invokes if configured)
- HOOK-05: Release optional cleanup — Task 1 (delegate) + Task 2 (DisposeAsync invokes for each idle entry)
- BOUND-01: Min/Max/Initial config, validated `0 ≤ Min ≤ Initial ≤ Max` — Task 1 (Builder.Build())
- BOUND-02: Eager warm-up awaitable, cancellable — Task 2 (WarmupAsync + ReadyAsync)
- FAIL-01: `IItemFailurePolicy<T>` invoked on Unhealthy or Factory throw — Task 1 (interface) + Task 2 (engine invokes in catch + on BeforeUse Unhealthy)
- FAIL-02: `DiscardAndReplaceFailurePolicy<T>` default — Task 1
- TELEM-01: `Meter "Oragon.ElasticPool"` via `IMeterFactory` + `pool.acquire.count` + `pool.factory.failures` — Task 2 (TelemetryEmitter)
- DI-01: `services.AddElasticPool<T>(name, configure)` named options — Task 3
- QUAL-01: CancellationToken end-to-end — Task 1 (every hook signature) + Task 2 (linked CTS in AcquireAsync, propagated to all hook calls)
- QUAL-02: IDisposable + IAsyncDisposable drain — Task 2 (lifecycle state machine + DisposeAsync drain)

**CONTEXT.md decisions implemented (D-equivalents):**
- `IPoolItem<T>.Value` (not .Item / .Object) — Task 1
- `WaitBehavior` enum + `PoolExhaustedException` — Task 1
- Builder requires Factory; throws on missing — Task 1
- `services.AddElasticPool<T>(name, ...)` requires explicit name; string.Empty for default — Task 3
- IMeterFactory resolution — Task 2

**RESEARCH.md patterns implemented:** Patterns 1, 2, 3, 4, 5, 6, 7, 8, 9 (all of them)

**RESEARCH.md items deferred:**
- Coverage gate (90%) — Plan 03
- All tests — Plan 03

**Audit verdict:** No unplanned items. Every Phase 1 requirement is covered. Every locked CONTEXT.md decision is honored. The single discretionary choice (sync `Acquire()` throws PoolExhaustedException rather than blocking) follows RESEARCH.md Open Question 3 recommendation, consistent with CONTEXT.md "retorno imediato".
</source_coverage_audit>

<output>
After completion, create `.planning/phases/01-core-skeleton-fixed-size-pool/01-02-SUMMARY.md` documenting:
- All public API types created (with one-line purpose each)
- Key engine implementation choices (direct-handoff Channel, counter rollback in CAS loop, dispose state machine)
- How the DI extension wires named-options + keyed singleton + default-name fallback
- The decision to throw `PoolExhaustedException` in sync `Acquire()` (never block) per RESEARCH Open Question 3
- The reflection-based optional `IHostApplicationLifetime` probe (avoiding hard Hosting dependency)
- Heads-up to Plan 03: every must_have truth in this plan is testable; Plan 03 must convert each truth into at least one test, then add the MaxSize=1 ping-pong stress test, MetricCollector counter assertions, and 90% coverage gate
</output>
