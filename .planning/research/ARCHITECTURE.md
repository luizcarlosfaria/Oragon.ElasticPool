# Architecture Research — Oragon.ElasticPool

**Domain:** Multi-target .NET OSS NuGet pooling library + RabbitMQ adapter
**Researched:** 2026-05-03
**Confidence:** HIGH (synthesized from HikariCP source, Microsoft.Extensions.ObjectPool source, Reactor Pool, generic-pool, OpenTelemetry semantic conventions, and verified BCL API availability across net8/net9/net10)

## Executive Summary

The architecture is two NuGet packages with a thin, deliberately small public surface and an internal core built on three primitives that are in-box on **every** target framework (`net8.0`/`net9.0`/`net10.0`):

1. **`ConcurrentQueue<PoolEntry<T>>`** — lock-free idle-item bag (validated by `Microsoft.Extensions.ObjectPool` and HikariCP-equivalents).
2. **`Channel<TaskCompletionSource<PoolEntry<T>>>`** — bounded FIFO **waiter queue** with built-in cancellation and back-pressure (replaces hand-rolled `SemaphoreSlim`+queue dance).
3. **`PeriodicTimer`** — drift-free background sweep loop for shrink + health-check passes (in-box since .NET 6, single-consumer model fits perfectly).

A **single sealed `ElasticPool<T>` class** owns all internal state. It is constructed only by an immutable `ElasticPoolOptions<T>` config record (frozen by the fluent builder's `Build()`). Adapters (RabbitMQ) are pure consumers of the public API — they call `ElasticObjectPoolFactory.Build<T>(...)` with their own factory/hooks. Layered pools are composed at the **adapter** layer (channel pool's `Factory` hook acquires from the connection pool); Core has no concept of "layered pool" — keeping the abstraction clean.

Telemetry follows OpenTelemetry semantic conventions (`db.client.connection.*` model adapted to `pool.*` namespace) with a single `Meter` named `"Oragon.ElasticPool"` and a single `ActivitySource` of the same name.

Build order: ship the **fixed-size pool with hooks + telemetry first** (Phase 1 proves the API surface and DX); add **elasticity (grow + shrink + sweep)** as the marquee differentiator (Phase 2); add **RabbitMQ adapter** (Phase 3) — the layered composition exercises both inner and outer failure-policy paths and validates the abstraction.

## Standard Architecture

### System Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                       Consumer Application                           │
│           (publisher, worker, ASP.NET Core, etc.)                    │
└────────────────────────┬────────────────────────────────────────────┘
                         │  IServiceCollection.AddElastic*
                         │  Inject IElasticPool<T> / IPoolItem<T>
                         ▼
┌─────────────────────────────────────────────────────────────────────┐
│              Oragon.ElasticPool.RabbitMQ (adapter)                 │
│  ┌─────────────────────────┐    ┌─────────────────────────────┐     │
│  │ AddElastic             │    │ AddElastic                 │     │
│  │  ConnectionPool(...)    │    │  ChannelPool(...)           │     │
│  └────────────┬────────────┘    └─────────────┬───────────────┘     │
│               │ builds                        │ builds              │
│               │ IElasticPool<IConnection>    │ IElasticPool<IChannel> │
│               │ via Core builder              │ whose Factory pulls │
│               │                               │ from the connection │
│               │                               │ pool above          │
└───────────────┼───────────────────────────────┼─────────────────────┘
                │                               │
                ▼                               ▼ (depends on inner pool at runtime)
┌─────────────────────────────────────────────────────────────────────┐
│                  Oragon.ElasticPool (public API)              │
│  ┌─────────────────────┐  ┌─────────────────────┐                   │
│  │ IElasticPool<T>    │  │ IPoolItem<T>        │                   │
│  │  Acquire / Async    │  │  .Object  IDisposable│                  │
│  └─────────────────────┘  └─────────────────────┘                   │
│  ┌─────────────────────┐  ┌─────────────────────┐                   │
│  │ AdaptiveObjectPool  │  │ ElasticPoolOptions │                   │
│  │  Factory.Build<T>() │  │  <T>  (record)      │                   │
│  └─────────────────────┘  └─────────────────────┘                   │
│  ┌─────────────────────┐  ┌─────────────────────┐                   │
│  │ IItemFailurePolicy<T>│ │ DI extensions       │                   │
│  │  + Discard default  │  │ AddElasticPool<T>  │                   │
│  └─────────────────────┘  └─────────────────────┘                   │
├─────────────────────────────────────────────────────────────────────┤
│              Oragon.ElasticPool (internal engine)             │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │              sealed ElasticPool<T> : IElasticPool<T>      │    │
│  │  ┌─────────────────────┐  ┌──────────────────────────────┐  │    │
│  │  │ ConcurrentQueue     │  │ Channel<TCS<PoolEntry<T>>>   │  │    │
│  │  │ <PoolEntry<T>>      │  │   (waiter queue, bounded)    │  │    │
│  │  │   _idle             │  │                              │  │    │
│  │  └─────────────────────┘  └──────────────────────────────┘  │    │
│  │  ┌─────────────────────┐  ┌──────────────────────────────┐  │    │
│  │  │ Counters (long):    │  │ PeriodicTimer (sweep loop)   │  │    │
│  │  │ _total / _inUse /   │  │ → shrink + health check pass │  │    │
│  │  │ _waiters / _highWM  │  │                              │  │    │
│  │  └─────────────────────┘  └──────────────────────────────┘  │    │
│  │  ┌─────────────────────┐  ┌──────────────────────────────┐  │    │
│  │  │ PressureSampler     │  │ TelemetryEmitter             │  │    │
│  │  │ (utilization window)│  │ (Meter + ActivitySource)     │  │    │
│  │  └─────────────────────┘  └──────────────────────────────┘  │    │
│  │  ┌─────────────────────────────────────────────────────────┐│    │
│  │  │ Hooks struct: Factory / BeforeUse / Check / AfterUse /  ││    │
│  │  │ Release   (frozen at Build())                           ││    │
│  │  └─────────────────────────────────────────────────────────┘│    │
│  └─────────────────────────────────────────────────────────────┘    │
├─────────────────────────────────────────────────────────────────────┤
│                BCL primitives (in-box net8/9/10)                    │
│  ConcurrentQueue · Channel<T> · PeriodicTimer · TimeProvider ·      │
│  Meter / ActivitySource · ValueTask · IAsyncDisposable              │
└─────────────────────────────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | Responsibility | Visibility | Sealed/Abstract |
|---|---|---|---|
| `IElasticPool<T>` | Public contract: `Acquire`, `AcquireAsync`, counters, `IAsyncDisposable` | **public** | interface |
| `IPoolItem<T>` | Disposable wrapper: `.Object` + `Dispose`/`DisposeAsync` returns to pool | **public** | interface |
| `ElasticPool<T>` | The one and only concrete pool engine | **internal** | **sealed** |
| `PoolItem<T>` | Internal struct/class that holds `PoolEntry<T>` + back-reference to pool | **internal** | **sealed** |
| `PoolEntry<T>` | Internal record: `T Item`, `DateTimeOffset CreatedAt`, `DateTimeOffset LastReturnedAt`, `long UseCount`, `bool QuarantineFlag` | **internal** | **sealed record** |
| `ElasticObjectPoolFactory` | Static entry point — `.Build<T>(IServiceProvider, CancellationToken)` returns the fluent builder | **public** | static |
| `ElasticPoolBuilder<T>` | Fluent builder: `.Factory()`, `.BeforeUse()`, `.Check()`, `.AfterUse()`, `.Release()`, `.WithBounds()`, `.WithFailurePolicy()`, `.Build()` | **public** | **sealed** |
| `ElasticPoolOptions<T>` | Frozen immutable config record produced by `Build()` | **public** | **sealed record** (init-only) |
| `IItemFailurePolicy<T>` | Pluggable: receives broken `PoolEntry<T>`, decides discard vs. quarantine vs. custom | **public** | interface |
| `DiscardAndReplaceFailurePolicy<T>` | Default policy — always discard | **public** | **sealed** |
| `WaiterQueue<T>` (alias) | `Channel<TaskCompletionSource<PoolEntry<T>>>` wrapper | **internal** | **sealed** |
| `PressureSampler` | Sliding window of utilization% + waiter-queue depth + acquire-wait latency | **internal** | **sealed** |
| `BackgroundSweeper` | `PeriodicTimer`-driven loop performing shrink + health check passes | **internal** | **sealed** |
| `TelemetryEmitter` | Owns `Meter` + `ActivitySource`, exposes typed methods (`OnAcquire`, `OnGrow`, etc.) | **internal** | **sealed** |
| `PoolDiagnosticsLog` | `[LoggerMessage]` source-gen logging (allocation-free) | **internal** | static partial |
| `ServiceCollectionExtensions` (Core) | `AddElasticPool<T>(...)`, `AddKeyedElasticPool<T>(string)` | **public** | static |
| `ServiceCollectionExtensions` (RabbitMQ) | `AddElasticConnectionPool(...)`, `AddElasticChannelPool(...)` | **public** | static |

**Sealing rationale:** every concrete class is `sealed` to prevent inheritance-based extension. Extension is via **hooks and policy interfaces** — not subclassing. This is HikariCP's discipline (no extension by subclass) and `Microsoft.Extensions.ObjectPool`'s mistake to avoid (people do subclass `DefaultObjectPool` and break things).

## Recommended Project Structure

```
src/
├── Oragon.ElasticPool/
│   ├── Abstractions/
│   │   ├── IElasticPool.cs              # public contract
│   │   ├── IPoolItem.cs                  # public disposable wrapper
│   │   ├── IItemFailurePolicy.cs         # pluggable policy
│   │   └── HealthCheckResult.cs          # enum: Healthy | Unhealthy
│   ├── Builder/
│   │   ├── ElasticObjectPoolFactory.cs  # static entry: Build<T>(sp, ct)
│   │   ├── ElasticPoolBuilder.cs        # fluent builder, sealed
│   │   └── ElasticPoolOptions.cs        # frozen options record
│   ├── Hooks/
│   │   ├── PoolItemContext.cs            # state bag passed to all hooks
│   │   └── HookDelegates.cs              # FactoryDelegate<T>, BeforeUseDelegate<T>, etc.
│   ├── Internals/
│   │   ├── ElasticPool.cs               # sealed engine — internal
│   │   ├── PoolEntry.cs                  # internal sealed record
│   │   ├── PoolItem.cs                   # internal sealed wrapper
│   │   ├── WaiterQueue.cs                # Channel<TCS> abstraction
│   │   ├── PressureSampler.cs            # composite-signal logic
│   │   └── BackgroundSweeper.cs          # PeriodicTimer loop
│   ├── Policies/
│   │   └── DiscardAndReplaceFailurePolicy.cs  # default impl
│   ├── Telemetry/
│   │   ├── TelemetryEmitter.cs           # Meter + ActivitySource owner
│   │   ├── PoolMeterNames.cs             # const strings (semantic conv)
│   │   └── PoolDiagnosticsLog.cs         # [LoggerMessage] source-gen
│   ├── DependencyInjection/
│   │   └── ServiceCollectionExtensions.cs  # AddElasticPool<T>(...)
│   ├── PublicAPI.Shipped.txt
│   ├── PublicAPI.Unshipped.txt
│   └── Oragon.ElasticPool.csproj
├── Oragon.ElasticPool.RabbitMQ/
│   ├── ElasticConnectionPoolBuilder.cs  # ergonomic wrapper over Core builder
│   ├── ElasticChannelPoolBuilder.cs     # layered: Factory hook acquires from connection pool
│   ├── DependencyInjection/
│   │   └── ServiceCollectionExtensions.cs  # AddElasticConnectionPool / AddElasticChannelPool
│   ├── PublicAPI.Shipped.txt
│   ├── PublicAPI.Unshipped.txt
│   └── Oragon.ElasticPool.RabbitMQ.csproj
tests/
├── Oragon.ElasticPool.Tests/
├── Oragon.ElasticPool.IntegrationTests/      # stress, concurrency
├── Oragon.ElasticPool.RabbitMQ.Tests/
└── Oragon.ElasticPool.RabbitMQ.IntegrationTests/   # Testcontainers
bench/
└── Oragon.ElasticPool.Benchmarks/
samples/
└── PublisherSample/
```

### Structure Rationale

- **`Abstractions/` separated from `Internals/`:** consumers reference only abstractions; internals are implementation detail and may evolve without API breaks.
- **`Builder/` separated from `Internals/`:** builder is part of the public surface; engine is not. The builder produces a frozen `ElasticPoolOptions<T>` record — the engine constructor takes that record. Builder and engine never share mutable state.
- **`Hooks/` is its own folder:** hook delegate types (`FactoryDelegate<T>`, `BeforeUseDelegate<T>`, etc.) are public — but they're a small, related cluster that benefits from co-location for discoverability.
- **`Policies/` separated:** failure policies are public extension points; ship one default, leave room for `QuarantineWithBackoffFailurePolicy<T>` (P2).
- **`Telemetry/` self-contained:** `Meter`/`ActivitySource` ownership is centralized in one class so naming conventions cannot drift.
- **`PublicAPI.*.txt`** files (from `Microsoft.CodeAnalysis.PublicApiAnalyzers`): track every public symbol explicitly to prevent accidental ABI breaks. Critical for OSS.

## Internal Data Structures (Decision Tree)

### Idle Item Storage: `ConcurrentQueue<PoolEntry<T>>` (NOT `ConcurrentBag`, NOT `ConcurrentStack`, NOT custom ring buffer)

**Decision:** `ConcurrentQueue<PoolEntry<T>>` — FIFO, lock-free, in-box.

| Option | Why considered | Why rejected (or chosen) |
|---|---|---|
| **`ConcurrentQueue<T>`** | Lock-free FIFO; what `Microsoft.Extensions.ObjectPool` uses internally | **Chosen.** Validated battle-tested choice; FIFO ordering aids fair item rotation (oldest idle item used next → exercises all items, surfaces health issues earlier than LIFO). |
| `ConcurrentStack<T>` | LIFO — better cache locality (recently-used item still hot) | Rejected. For heavy resources (RabbitMQ connections), cache locality is irrelevant; FIFO rotation surfaces stale items faster, which matches the "auto-cure" promise. |
| `ConcurrentBag<T>` | Thread-local optimization (HikariCP-style) | Rejected for v1. Wins only when the same thread repeatedly acquires+releases. RabbitMQ publisher workloads acquire on one thread, return on another (continuation thread). Adds complexity for negligible gain in our target scenarios. Reconsider in v2 if benchmarks justify. |
| Custom ring buffer | Theoretical perf for fixed-size scenarios | Rejected. We're elastic — bounds change. Maintenance burden + correctness risk dominates the perceived perf win. |
| `Channel<PoolEntry<T>>` (as item store) | Async dequeue API would let `Acquire` block on the channel | Rejected. Conflates "I have an idle item" with "I'm waiting for one." Two concerns → two structures: queue for idle items, separate waiter queue for waiters. |

### Waiter Queue: `Channel<TaskCompletionSource<PoolEntry<T>>>` (bounded, single-consumer-per-write)

**Decision:** `Channel.CreateBounded<TaskCompletionSource<PoolEntry<T>>>(maxWaiters)` with `SingleReader = false, SingleWriter = false, FullMode = Wait`.

**Mechanism:**
1. `AcquireAsync` first tries `_idle.TryDequeue()` (fast path, no allocation).
2. If empty AND `_total < MaxSize`, attempt to **grow** (create new entry via `Factory` hook); CAS-increment `_total`. If CAS fails (race lost), retry.
3. If empty AND at max, create a `TaskCompletionSource<PoolEntry<T>>(TaskCreationOptions.RunContinuationsAsynchronously)`, write to the channel, increment `_waiters`. Register cancellation: on cancel, set TCS canceled — `Release` path checks `Task.IsCompleted` before handing off.
4. On `Release`, the pool path FIRST checks if there's a waiter (`channel.Reader.TryRead()`); if so, sets the TCS result directly with the entry (**direct handoff** — never touches `_idle`). Otherwise enqueues to `_idle`.

**Why `Channel<T>` over `SemaphoreSlim` + `ConcurrentQueue<TCS>`:**
- Channel is in-box, lock-free, async-aware, with cancellation built in.
- `SemaphoreSlim.WaitAsync(ct)` + manual TCS dance has historically been a source of bugs (cancellation cleanup races, lost signals).
- Channel's `WaitToReadAsync`/`TryRead` API is the right shape for the direct-handoff pattern.

**Why direct handoff (not "release to queue, waiter dequeues"):**
- Eliminates the TOCTOU race where waiter checks empty queue, releaser enqueues, waiter waits forever.
- Mirrors HikariCP's `SynchronousQueue` pattern (validated at scale).

### Counters: `Interlocked` on `long` fields (NOT `ConcurrentDictionary`, NOT locks)

| Counter | Type | Purpose |
|---|---|---|
| `_total` | `long` (Interlocked) | Items the pool owns (idle + in-use). Bounded by `MaxSize`. |
| `_inUse` | `long` (Interlocked) | Items currently checked out. |
| `_waiters` | `long` (Interlocked) | Pending `AcquireAsync` calls. Drives grow signal. |
| `_highWaterMark` | `long` (Interlocked, monotonic max via CAS loop) | Peak `_total` since last sweep — used to decide if shrink is appropriate. |
| `_acquireCount` / `_releaseCount` / `_growCount` / `_shrinkCount` / `_failCount` | `long` (Interlocked) | Total event counters surfaced via `Counter<long>` instruments. |

**Derived (not stored):** `Available = _total - _inUse`. Computed on demand in metric callbacks.

### Locks: avoid in hot path; one short lock for grow contention only

**Decision:** lock-free hot path (acquire/release). One short critical section (`object _growLock = new()`; or `Lock` on net9+) **only** around the "decide whether to grow + invoke Factory" block — to ensure we don't blow past `MaxSize` under contention. The Factory invocation itself happens **outside** the lock; we reserve a "grow ticket" inside the lock (CAS increment of `_total`) and call `Factory` after releasing it.

**On net9+:** can use `System.Threading.Lock` for slightly nicer syntax — guard with `#if NET9_0_OR_GREATER`. On net8.0 use plain `object`. Behavior identical.

## Concurrency Strategy

| Path | Strategy | Rationale |
|---|---|---|
| **Acquire (fast path: idle item available)** | `_idle.TryDequeue()` → `Interlocked.Increment(ref _inUse)` → return wrapper | Zero locks, zero allocations beyond the `IPoolItem<T>` wrapper. |
| **Acquire (slow path: must grow)** | CAS-loop on `_total` to reserve slot up to `MaxSize`; outside any lock, invoke `Factory` async; if factory throws, decrement `_total` back; if succeeds, return wrapper | Lock-free reservation; Factory may take seconds (RabbitMQ TCP handshake) — never hold lock across that. |
| **Acquire (slow path: must wait)** | Create TCS; `channel.Writer.WriteAsync(tcs, ct)`; `await tcs.Task.WaitAsync(ct)` | Channel handles back-pressure, ordering, cancellation. |
| **Release** | Run `AfterUse` hook (if configured); if healthy, check `channel.Reader.TryRead(out var tcs)` for waiter direct handoff; else `_idle.Enqueue(entry)`; `Interlocked.Decrement(ref _inUse)` | Handoff prefers waiters over idle queue → fairness + lower latency. |
| **Sweep (background)** | Single `PeriodicTimer` loop; not concurrent with itself; can run concurrent with acquire/release | Single-consumer of timer → no synchronization needed within sweep code; touches shared state via Interlocked + `TryDequeue` |
| **Shrink** | Drain idle entries whose `LastReturnedAt < now - IdleTimeout`, but stop at `MinSize`. For each evicted entry: invoke `Release` hook, decrement `_total` | Shrink is opportunistic and bounded; never blocks acquire path. |
| **Health check (background)** | Sample N idle entries (parameterized); for each, dequeue, run `Check` hook async, on Healthy re-enqueue, on Unhealthy invoke failure policy | Bounds CPU; never validates an in-use entry (would require acquiring it back); under heavy load, in-use validation comes from `BeforeUse` hook on next borrow. |
| **Dispose** | State machine: `Open → Draining → Closed`; refuses new acquires; sweep timer disposed; existing in-use items still allowed to return; on grace timeout, force-release all idle | Reactor Pool's `GracefulShutdownInstrumentedPool` model. |

**Tradeoff summary:** lock-free everywhere lock-free is correct; one tiny lock only for the grow-decision moment. Acquire-fast-path is allocation-free (after warmup) which matters for high-throughput publisher workloads.

## Background Sweep Mechanism

**Decision:** `PeriodicTimer` (in-box net6+, available on all our TFMs) inside a long-running `Task` started by the pool constructor.

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **`PeriodicTimer.WaitForNextTickAsync(ct)`** | Drift-free (next tick scheduled from previous tick, not from when work finishes); cancellation built in; one allocation per pool; in-box on all TFMs | Single-consumer (only one `WaitForNextTickAsync` in flight) — fits sweep perfectly | **Chosen** |
| `Task.Delay(interval, ct)` loop | Trivial | Drifts: if work takes 1.5s and delay is 1s, real interval is 2.5s. Bad for telemetry interpretation. | Rejected |
| `System.Threading.Timer` | Old, callback-based | Sync callback in thread-pool thread; awkward for `async` health-check work; risk of overlapping callbacks | Rejected |
| `Channel<DateTimeOffset>`-based scheduler | Composable | Over-engineered for a single periodic task | Rejected |
| `IHostedService` integration | Lifetime-aware | Couples Core to `Microsoft.Extensions.Hosting` (forbidden by stack rules) | Rejected for Core; can be done in consumer code |

**Implementation sketch (multi-target safe):**
```csharp
// Inside ElasticPool<T> ctor:
_sweepCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
_sweepTask = Task.Run(SweepLoopAsync);

private async Task SweepLoopAsync()
{
    using var timer = new PeriodicTimer(_options.SweepInterval, _options.TimeProvider);
    while (await timer.WaitForNextTickAsync(_sweepCts.Token).ConfigureAwait(false))
    {
        try
        {
            await ShrinkPassAsync(_sweepCts.Token).ConfigureAwait(false);
            await HealthCheckPassAsync(_sweepCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            _log.SweepFailed(ex);  // [LoggerMessage] source-gen
            // never propagate — sweep failure must not kill the pool
        }
    }
}
```

**`TimeProvider` injection:** `PeriodicTimer(TimeSpan, TimeProvider)` overload exists on net8+ — use it. Tests inject `FakeTimeProvider` (from `Microsoft.Extensions.TimeProvider.Testing`) to advance time deterministically. **No multi-target `#if` needed** for `TimeProvider` itself (in-box net8+).

## Builder Pattern Shape

**Shape:** mutable `ElasticPoolBuilder<T>` → frozen `ElasticPoolOptions<T>` record → sealed `ElasticPool<T>` engine.

```csharp
// public static entry
public static class ElasticObjectPoolFactory
{
    public static ElasticPoolBuilder<T> Build<T>(IServiceProvider sp, CancellationToken ct = default)
        => new(sp, ct);
}

// public sealed builder (mutable; not thread-safe)
public sealed class ElasticPoolBuilder<T>
{
    // configured via fluent methods
    private FactoryDelegate<T>? _factory;
    private BeforeUseDelegate<T>? _beforeUse;
    private CheckDelegate<T>? _check;
    private AfterUseDelegate<T>? _afterUse;
    private ReleaseDelegate<T>? _release;
    private int _minSize, _maxSize, _initialSize;
    private TimeSpan _idleTimeout = TimeSpan.FromMinutes(5);
    private TimeSpan _sweepInterval = TimeSpan.FromSeconds(30);
    private IItemFailurePolicy<T>? _failurePolicy;
    // ...

    public ElasticPoolBuilder<T> Factory(FactoryDelegate<T> factory) { _factory = factory; return this; }
    public ElasticPoolBuilder<T> BeforeUse(BeforeUseDelegate<T> hook) { _beforeUse = hook; return this; }
    // etc.

    public IElasticPool<T> Build()
    {
        // Validation (throws ArgumentException with clear messages)
        if (_factory is null)
            throw new InvalidOperationException("Factory hook is required. Call .Factory(...).");
        if (_minSize < 0) throw new ArgumentOutOfRangeException(nameof(_minSize));
        if (_maxSize < 1) throw new ArgumentOutOfRangeException(nameof(_maxSize));
        if (_minSize > _maxSize) throw new InvalidOperationException("MinSize cannot exceed MaxSize.");
        if (_initialSize < _minSize || _initialSize > _maxSize)
            throw new InvalidOperationException("InitialSize must be between MinSize and MaxSize.");
        if (_idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(_idleTimeout));
        // ...

        // Freeze into immutable options
        var options = new ElasticPoolOptions<T>
        {
            Factory = _factory,
            BeforeUse = _beforeUse,
            // ...
            FailurePolicy = _failurePolicy ?? new DiscardAndReplaceFailurePolicy<T>(),
            // ...
        };

        // Construct engine, kick off warm-up + sweep
        return new ElasticPool<T>(options, _serviceProvider, _ct);
    }
}

// public frozen options (init-only)
public sealed record ElasticPoolOptions<T>
{
    public required FactoryDelegate<T> Factory { get; init; }
    public BeforeUseDelegate<T>? BeforeUse { get; init; }
    public CheckDelegate<T>? Check { get; init; }
    public AfterUseDelegate<T>? AfterUse { get; init; }
    public ReleaseDelegate<T>? Release { get; init; }
    public int MinSize { get; init; }
    public int MaxSize { get; init; }
    public int InitialSize { get; init; }
    public TimeSpan IdleTimeout { get; init; }
    public TimeSpan SweepInterval { get; init; }
    public IItemFailurePolicy<T> FailurePolicy { get; init; } = new DiscardAndReplaceFailurePolicy<T>();
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public string? PoolName { get; init; }  // for telemetry tagging
}
```

**Key principles:**
1. **Builder is single-use:** `Build()` doesn't enforce one-shot, but options are frozen on extraction — calling `Build()` twice is allowed and returns two pools.
2. **Validation in `Build()`:** all config errors throw at construction, never at `AcquireAsync` time. Fail fast and loud.
3. **Required vs. optional hooks:** only `Factory` is required (enforced at runtime); other hooks default to no-op.
4. **`required` keyword on options:** C# 12+ `required` on `ElasticPoolOptions<T>.Factory` provides compile-time enforcement if someone ever constructs options directly.

## Adapter Composition (How RabbitMQ Adapter Plugs In Without Leaking)

**Core knows nothing about `IConnection`/`IChannel`.** The adapter is a pure consumer of `ElasticObjectPoolFactory.Build<T>(...)`.

### Connection Pool (single-layer, straightforward)

```csharp
// In Oragon.ElasticPool.RabbitMQ
public static class RabbitMqServiceCollectionExtensions
{
    public static IServiceCollection AddElasticConnectionPool(
        this IServiceCollection services,
        Action<ElasticConnectionPoolBuilder> configure)
    {
        var poolBuilder = new ElasticConnectionPoolBuilder();
        configure(poolBuilder);

        services.AddSingleton<IElasticPool<IConnection>>(sp =>
        {
            var connFactory = poolBuilder.BuildConnectionFactory(sp);
            return ElasticObjectPoolFactory.Build<IConnection>(sp)
                .Factory(async ct => await connFactory.CreateConnectionAsync(ct).ConfigureAwait(false))
                .BeforeUse(c => c.IsOpen ? HealthCheckResult.Healthy : HealthCheckResult.Unhealthy)
                .Check(async (c, ct) => c.IsOpen ? HealthCheckResult.Healthy : HealthCheckResult.Unhealthy)
                .Release(async (c, ct) => { try { await c.CloseAsync(ct); } catch { } await c.DisposeAsync(); })
                .WithBounds(poolBuilder.MinConnections, poolBuilder.MaxConnections, poolBuilder.InitialConnections)
                .WithIdleTimeout(poolBuilder.IdleTimeout)
                .WithPoolName("rabbitmq.connection")  // shows up as tag in telemetry
                .Build();
        });
        return services;
    }
}
```

### Channel Pool (layered — channel pool's Factory pulls from connection pool)

This is the subtle part. The `Factory` hook for `IChannel` acquires a connection from the `IConnection` pool, creates a channel on it, but **does not return the connection to the pool until the channel is released**. Otherwise the connection could be evicted while a channel is using it.

**Solution: the channel pool's `PoolEntry<IChannel>` carries its borrowed `IPoolItem<IConnection>` alongside.** This requires an internal wrapper type — exposed via the adapter, not Core.

```csharp
// In Oragon.ElasticPool.RabbitMQ — INTERNAL
internal sealed class ChannelLease : IAsyncDisposable
{
    public IChannel Channel { get; }
    private readonly IPoolItem<IConnection> _connectionLease;

    public ChannelLease(IChannel ch, IPoolItem<IConnection> conn)
    { Channel = ch; _connectionLease = conn; }

    public async ValueTask DisposeAsync()
    {
        try { await Channel.CloseAsync().ConfigureAwait(false); } catch { }
        await Channel.DisposeAsync().ConfigureAwait(false);
        await _connectionLease.DisposeAsync().ConfigureAwait(false); // returns connection to inner pool
    }
}

public static IServiceCollection AddElasticChannelPool(
    this IServiceCollection services,
    Action<ElasticChannelPoolBuilder> configure)
{
    services.AddSingleton<IElasticPool<IChannel>>(sp =>
    {
        var connectionPool = sp.GetRequiredService<IElasticPool<IConnection>>();
        // Cache: when the channel pool acquires a channel, it stashes the connection lease in a ConditionalWeakTable<IChannel, IPoolItem<IConnection>>
        // so that the Release hook can dispose the connection lease AFTER closing the channel.
        var leaseMap = new ConditionalWeakTable<IChannel, IPoolItem<IConnection>>();

        return ElasticObjectPoolFactory.Build<IChannel>(sp)
            .Factory(async ct =>
            {
                var connLease = await connectionPool.AcquireAsync(ct).ConfigureAwait(false);
                try
                {
                    var ch = await connLease.Object.CreateChannelAsync(/* options */, ct).ConfigureAwait(false);
                    leaseMap.Add(ch, connLease);  // pair channel ↔ connection lease
                    return ch;
                }
                catch
                {
                    await connLease.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            })
            .BeforeUse(ch =>
            {
                if (!ch.IsOpen) return HealthCheckResult.Unhealthy;
                // also probe underlying connection
                if (leaseMap.TryGetValue(ch, out var connLease) && !connLease.Object.IsOpen)
                    return HealthCheckResult.Unhealthy;
                return HealthCheckResult.Healthy;
            })
            .Release(async (ch, ct) =>
            {
                try { await ch.CloseAsync(ct).ConfigureAwait(false); } catch { }
                await ch.DisposeAsync().ConfigureAwait(false);
                if (leaseMap.TryGetValue(ch, out var connLease))
                {
                    leaseMap.Remove(ch);
                    await connLease.DisposeAsync().ConfigureAwait(false);  // returns connection to pool
                }
            })
            .WithBounds(...)
            .WithPoolName("rabbitmq.channel")
            .Build();
    });
    return services;
}
```

**Lifecycle propagation rules:**
1. **Inner item (connection) dies while outer item (channel) is checked out:** outer pool's `BeforeUse` detects via `connLease.Object.IsOpen` → returns Unhealthy → failure policy discards → Release path closes channel and disposes connection lease (which goes back to inner pool, where inner pool's `BeforeUse` will catch the dead connection on next inner borrow).
2. **Outer item (channel) is recycled/discarded:** Release hook unconditionally disposes the connection lease, returning the connection to the inner pool. Inner pool's health policy decides what to do with it.
3. **Inner pool is shrinking:** the inner pool **cannot shrink a connection that's currently leased** by an outer-pool channel — the lease still exists in `_inUse`. Only after the channel is released does the connection go back to the inner idle queue, where it becomes a shrink candidate. **No special coordination needed** — the lease counter naturally protects in-use connections.

**Why `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>`:** keys off identity, doesn't prevent GC, and the channel pool's Release hook always runs before GC could matter. We could also stash the lease in a wrapper type and store that, but keeping `IChannel` as the pool's `T` keeps the public API clean (`IElasticPool<IChannel>` is what consumers expect to inject).

**Key abstraction-leak guard:** Core's public types never reference RabbitMQ types. The layered behavior is implemented entirely with Core's public hooks. If a future adapter (HTTP, gRPC) wants the same pattern, it implements it the same way — no new Core API needed.

## Telemetry Surface Design

### Meter & ActivitySource Names

```csharp
internal static class PoolMeterNames
{
    // Single Meter, single ActivitySource for the whole library
    public const string MeterName = "Oragon.ElasticPool";
    public const string ActivitySourceName = "Oragon.ElasticPool";

    // Instruments — adapted from OTel db.client.connection.* semantic conventions
    // Using "pool.*" prefix (since we're not strictly db) — namespaced to avoid collision.
    public const string PoolSize = "oragon.pool.size";                // ObservableGauge<long>  (= _total)
    public const string PoolAvailable = "oragon.pool.available";      // ObservableGauge<long>  (= _total - _inUse)
    public const string PoolInUse = "oragon.pool.in_use";             // ObservableGauge<long>  (= _inUse)
    public const string PoolWaiters = "oragon.pool.pending_requests"; // ObservableGauge<long>  (= _waiters)
    public const string PoolMin = "oragon.pool.min";                  // ObservableGauge<long>  (= MinSize)
    public const string PoolMax = "oragon.pool.max";                  // ObservableGauge<long>  (= MaxSize)
    public const string AcquireDuration = "oragon.pool.acquire.duration";  // Histogram<double> (seconds)
    public const string CreateDuration = "oragon.pool.create.duration";    // Histogram<double> (seconds)
    public const string UseDuration = "oragon.pool.use.duration";          // Histogram<double> (seconds)
    public const string AcquireTotal = "oragon.pool.acquire.count";        // Counter<long>
    public const string ReleaseTotal = "oragon.pool.release.count";        // Counter<long>
    public const string GrowTotal = "oragon.pool.grow.count";              // Counter<long>
    public const string ShrinkTotal = "oragon.pool.shrink.count";          // Counter<long>
    public const string FailureTotal = "oragon.pool.failure.count";        // Counter<long>
    public const string TimeoutTotal = "oragon.pool.timeout.count";        // Counter<long>
}
```

### Tag Conventions

Every instrument carries:
- **`pool.name`** — string, the `PoolName` from options (e.g., `"rabbitmq.connection"`, `"rabbitmq.channel"`); falls back to `typeof(T).Name` if unset.
- **`pool.item_type`** — string, `typeof(T).FullName` (cached).

`oragon.pool.failure.count` additionally carries:
- **`failure.reason`** — `"unhealthy_borrow"` | `"unhealthy_check"` | `"factory_failed"` | `"max_lifetime"` | `"explicit_discard"`.

`oragon.pool.acquire.count` additionally carries:
- **`acquire.outcome`** — `"fast_path"` (idle item) | `"grow"` | `"wait"` | `"timeout"` | `"canceled"`.

### ActivitySource Spans

| Activity name | Started by | Tags |
|---|---|---|
| `Pool.Acquire` | `Acquire`/`AcquireAsync` entry | `pool.name`, on success: `acquire.outcome`, `pool.size_after` |
| `Pool.Grow` | grow path (when `Factory` is invoked) | `pool.name`, `pool.size_after` |
| `Pool.HealthCheck` | sweep's check pass per-entry | `pool.name`, `health.result` (`healthy`/`unhealthy`) |
| `Pool.Release` | `IPoolItem.Dispose` path | `pool.name`, `release.action` (`returned`/`discarded`) |

### IMeterFactory Integration

Constructor takes optional `IMeterFactory` from DI; if present, uses `meterFactory.Create(MeterName)` (testable, scoped per consumer); if absent, uses `new Meter(MeterName)` (works in non-DI scenarios). This pattern is in-box on net8+.

### Source-Generated Logging (`[LoggerMessage]`)

```csharp
internal static partial class PoolDiagnosticsLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Pool '{PoolName}' grew from {Old} to {New} items")]
    public static partial void Grew(this ILogger logger, string poolName, int old, int @new);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Pool '{PoolName}' shrunk from {Old} to {New} items (idle timeout)")]
    public static partial void Shrunk(this ILogger logger, string poolName, int old, int @new);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning, Message = "Pool '{PoolName}' item failed health check ({Reason}); applying failure policy")]
    public static partial void HealthCheckFailed(this ILogger logger, string poolName, string reason);

    [LoggerMessage(EventId = 1099, Level = LogLevel.Error, Message = "Pool '{PoolName}' background sweep iteration failed")]
    public static partial void SweepFailed(this ILogger logger, string poolName, Exception ex);
}
```

**Allocation-free:** source-gen avoids the boxing/format-string interpretation cost of `_logger.LogXxx(...)` — critical when sweep runs every 30s for years.

## DI Integration Shape

```csharp
// Core
public static class ElasticPoolServiceCollectionExtensions
{
    // Registers IElasticPool<T> as a singleton; the configure delegate has access to IServiceProvider
    public static IServiceCollection AddElasticPool<T>(
        this IServiceCollection services,
        Action<ElasticPoolBuilder<T>> configure)
        where T : class
    {
        services.AddSingleton<IElasticPool<T>>(sp =>
        {
            var builder = ElasticObjectPoolFactory.Build<T>(sp);
            configure(builder);
            return builder.Build();
        });
        return services;
    }

    // Named pools — for cases where you need multiple pools of the same T
    public static IServiceCollection AddKeyedElasticPool<T>(
        this IServiceCollection services,
        string name,
        Action<ElasticPoolBuilder<T>> configure)
        where T : class
    {
        services.AddKeyedSingleton<IElasticPool<T>>(name, (sp, key) =>
        {
            var builder = ElasticObjectPoolFactory.Build<T>(sp);
            configure(builder);
            return builder.Build();
        });
        return services;
    }
}
```

**Why singleton:** the pool owns expensive resources and a background sweep loop. Scoped/transient would re-create the pool per request — meaningless.

**Why no `IOptions<T>` for pool config:** the pool's "config" is hooks (delegates) — not just data. `IOptions<>` is for serializable, change-aware data; doesn't fit. Consumers who want config-driven bounds should pull `IOptions<MyConfig>` inside their `configure` callback and read it themselves.

**Why `IPoolItem<T>` is not registered in DI:** lifetime is acquire-scoped, not container-scoped. Consumers do `await using var item = await pool.AcquireAsync(ct);` — never inject `IPoolItem<T>`.

**Keyed services:** available on net8+ via `AddKeyedSingleton` (in `Microsoft.Extensions.DependencyInjection.Abstractions` 8.0+). No multi-target guard needed.

## Component Boundaries (public/internal/sealed/abstract)

| Type | Visibility | Modifier | Rationale |
|---|---|---|---|
| `IElasticPool<T>` | public | interface | Core contract; consumers code to this |
| `IPoolItem<T>` | public | interface | Disposable wrapper contract |
| `IItemFailurePolicy<T>` | public | interface | Pluggable extension point |
| `HealthCheckResult` | public | enum | Hook return type |
| `PoolItemContext` | public | sealed class | State bag passed to hooks; sealed prevents extension |
| `ElasticObjectPoolFactory` | public | static class | Single entry point — discoverable via `Build<T>` |
| `ElasticPoolBuilder<T>` | public | sealed class | Fluent builder; sealed prevents subclass-based extension |
| `ElasticPoolOptions<T>` | public | sealed record | Frozen config; sealed because adding fields is an additive evolution, not a subclass concern |
| `DiscardAndReplaceFailurePolicy<T>` | public | sealed class | Default policy; sealed — extend by writing a different `IItemFailurePolicy<T>` |
| `FactoryDelegate<T>`, `BeforeUseDelegate<T>`, etc. | public | delegate types | Hook signatures |
| `ElasticPoolServiceCollectionExtensions` | public | static class | DI extension methods |
| `ElasticPool<T>` | internal | sealed class | The engine; consumers never see the concrete type |
| `PoolEntry<T>` | internal | sealed record | Internal item wrapper with bookkeeping |
| `PoolItem<T>` | internal | sealed class | Concrete `IPoolItem<T>` impl |
| `WaiterQueue<T>` | internal | sealed class | Channel<TCS> abstraction |
| `PressureSampler` | internal | sealed class | Composite-signal logic |
| `BackgroundSweeper` | internal | sealed class | PeriodicTimer loop |
| `TelemetryEmitter` | internal | sealed class | Meter/ActivitySource owner |
| `PoolMeterNames` | internal | static class | Const strings |
| `PoolDiagnosticsLog` | internal | static partial class | `[LoggerMessage]` source-gen |

**Public surface ≈ 12 types.** Everything else is internal. Track via `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`.

## Multi-Target API Design Considerations

| Concern | Strategy |
|---|---|
| **`PeriodicTimer` constructor with `TimeProvider`** | Available net8+ — use unconditionally |
| **`System.Threading.Lock` (net9+)** | Optional; if used, guard with `#if NET9_0_OR_GREATER` and fall back to `object` lock target on net8. Behavior identical |
| **`TimeProvider`** | In-box net8+; no guard. Default `TimeProvider.System` |
| **`ConfigureAwaitOptions.SuppressThrowing`** | Available net8+ — use everywhere we need post-cancellation cleanup |
| **`AddKeyedSingleton`** | net8+ in DI.Abstractions 8.0+ — use unconditionally |
| **Collection expressions `[..items]`** | Language feature (C# 12), works on all TFMs — fine to use |
| **`Channel.CreateBounded` `FullMode = BoundedChannelFullMode.Wait`** | Available net8+; default behavior. No guard |
| **`required` modifier on options** | C# 11 language feature, works on all our TFMs |
| **`IMeterFactory`** | net8+ in `Microsoft.Extensions.Diagnostics` (impl) and BCL (abstraction). Take as constructor dependency, optional. Works without it |
| **`LoggerMessageAttribute` source generator** | Roslyn analyzer ships with `Microsoft.Extensions.Logging.Abstractions`; works on all TFMs |
| **`TaskCreationOptions.RunContinuationsAsynchronously`** | Available since .NET Framework 4.6 — universal |
| **`IAsyncDisposable`** | In-box net8+ — use unconditionally |

**Guidance for public API design:**
- Never expose a type from `Microsoft.Extensions.Hosting` — that pulls hosting into Core deps.
- Never expose a `Lock` (net9+) on the public surface — even if used internally, guard it.
- Take `TimeProvider` in `ElasticPoolOptions<T>` (defaults to `TimeProvider.System`) — enables `FakeTimeProvider` in tests without dependency injection.

**Verdict:** zero polyfills, at most one `#if NET9_0_OR_GREATER` if we adopt `Lock`. Public API surface is uniform across TFMs.

## Data Flow

### Acquire Path (Fast: idle item available)

```
Consumer calls pool.AcquireAsync(ct)
        ↓
ActivitySource.StartActivity("Pool.Acquire")
        ↓
_idle.TryDequeue(out var entry) ? YES
        ↓
Run BeforeUse hook (if configured)
        ↓
  Healthy? YES → wrap in PoolItem<T> wrapper
        ↓
Interlocked.Increment(ref _inUse)
        ↓
Counter "acquire.count" += 1, tag acquire.outcome="fast_path"
Histogram "acquire.duration" record stopwatch elapsed
        ↓
Return IPoolItem<T> to consumer
```

### Acquire Path (Slow: must grow)

```
_idle.TryDequeue → NO
        ↓
CAS-loop on _total:
  current = _total
  if current >= MaxSize → fall through to wait path
  if Interlocked.CompareExchange(ref _total, current+1, current) == current → won
        ↓
Outside lock: invoke Factory hook async (with ct from acquire + lifetime token)
        ↓
  Throws? → Interlocked.Decrement(ref _total); rethrow to caller
        ↓
Wrap entry; increment _inUse; tag acquire.outcome="grow"
Counter "grow.count" += 1
Log: PoolDiagnosticsLog.Grew(name, old, new)
ActivitySource event: "Pool.Grow"
        ↓
Return wrapper
```

### Acquire Path (Slow: must wait)

```
At max → cannot grow
        ↓
Create TaskCompletionSource<PoolEntry<T>>(RunContinuationsAsynchronously)
Interlocked.Increment(ref _waiters)
        ↓
channel.Writer.TryWrite(tcs) (always succeeds — we sized it generously; if at cap, WriteAsync)
        ↓
Register: ct.UnsafeRegister(s => ((TCS)s).TrySetCanceled(), tcs)
        ↓
await tcs.Task.WaitAsync(ct)
        ↓
On completion (handoff from Release path):
  PressureSampler.RecordWait(stopwatch.Elapsed)
  Wrap entry, increment _inUse
  Counter "acquire.count" tag acquire.outcome="wait"
        ↓
Return wrapper
```

### Release Path (via IPoolItem.Dispose / DisposeAsync)

```
Consumer disposes PoolItem<T>
        ↓
ActivitySource.StartActivity("Pool.Release")
        ↓
Run AfterUse hook if configured
        ↓
  Returned Unhealthy? → invoke FailurePolicy.OnReleaseUnhealthy(entry)
                      → typically: invoke Release hook, decrement _total, GoTo "trigger replacement"
        ↓ (healthy)
Try direct handoff:
  if channel.Reader.TryRead(out var waiterTcs) AND waiterTcs.TrySetResult(entry) → done
        ↓ (no waiter)
entry.LastReturnedAt = TimeProvider.GetUtcNow()
_idle.Enqueue(entry)
        ↓
Interlocked.Decrement(ref _inUse)
Counter "release.count" += 1, tag release.action="returned" or "discarded"
```

### Sweep Path (background, every SweepInterval)

```
PeriodicTimer.WaitForNextTickAsync(sweepCt) → true
        ↓
ShrinkPass:
  now = TimeProvider.GetUtcNow()
  while _total > MinSize:
    peek next idle entry; if entry.LastReturnedAt > now - IdleTimeout → break
    if _idle.TryDequeue(out entry):
      invoke Release hook (best-effort)
      Interlocked.Decrement(ref _total)
      Counter "shrink.count" += 1; Log.Shrunk
        ↓
HealthCheckPass (if Check hook configured):
  sample up to N idle entries (parameterized; default = MinSize)
  for each:
    TryDequeue → if got it:
      run Check hook async
      if Healthy → re-enqueue
      if Unhealthy → invoke FailurePolicy → trigger replacement (CAS_inc _total + Factory)
        ↓
Update _highWaterMark snapshot for next interval
```

### Grow Pressure Path (composite-signal)

```
PressureSampler exposes:
  - WaitersCount (instantaneous)
  - UtilizationPercent (rolling avg over window, e.g., 10s)
  - AcquireWaitP95 (rolling P95 wait time)
        ↓
On every AcquireAsync slow path:
  if (_waiters > GrowThresholdWaiters)
   OR (utilization > GrowThresholdUtilization for sustained > 5s)
   OR (waitP95 > GrowThresholdWait):
     → AND _total < MaxSize → trigger grow (CAS + Factory)
        ↓
This is "demand-side grow" — happens INLINE during acquire.
Sweep pass does NOT grow (only shrinks); shrink and grow are decoupled timing-wise.
```

### Disposal Path

```
pool.DisposeAsync()
        ↓
State: Open → Draining (CAS)
        ↓
Refuse new AcquireAsync (throw ObjectDisposedException)
        ↓
Cancel sweepCts → background task exits
        ↓
Wait for _inUse == 0 OR drainTimeout
        ↓
Drain idle queue: invoke Release hook on each
        ↓
Cancel any pending waiters' TCS (set canceled)
        ↓
Dispose Meter, ActivitySource (no-op for Activity, real dispose for Meter)
        ↓
State: Draining → Closed
```

## Suggested Build Order

Mapped to v1.0 MVP scope (`P1` items in `FEATURES.md`).

### Phase 1: "Skeleton" — fixed-size pool with hooks (proves the API)

**Ships:**
- Public surface: `IElasticPool<T>`, `IPoolItem<T>`, `ElasticObjectPoolFactory`, `ElasticPoolBuilder<T>`, `ElasticPoolOptions<T>`, hook delegate types
- `ElasticPool<T>` engine with **just** `MaxSize` (no Min, no elasticity, no sweep)
- `ConcurrentQueue<PoolEntry<T>>` + lock-free counters
- Direct-handoff waiter queue via `Channel<TCS>`
- Factory + BeforeUse + Release hooks (the others can be present in API but no-op)
- DI extension `AddElasticPool<T>(...)`
- Basic logging + `Meter` instruments (counters only — no observable gauges yet)
- `IAsyncDisposable` (basic — drain idle + invoke Release; no graceful timeout yet)

**Why first:** validates the public API shape end-to-end with a working pool. Catches DX issues early. Anything broken in the API surface is cheap to fix here, expensive after Phase 2.

**Test:** stress test under contention; verify no leaks, no deadlocks, fast-path is allocation-free after warmup.

### Phase 2: "Elasticity" — the marquee differentiator

**Ships on top of Phase 1:**
- `MinSize`, `InitialSize` + eager warm-up
- `BackgroundSweeper` with `PeriodicTimer`
- Idle eviction (shrink to `MinSize` after `IdleTimeout`)
- `Check` hook + background health-check pass
- `IItemFailurePolicy<T>` interface + `DiscardAndReplaceFailurePolicy<T>`
- `PressureSampler` — composite-signal grow logic
- `AfterUse` hook + on-return validation
- Observable gauges (`pool.size`, `pool.available`, `pool.in_use`, `pool.pending_requests`)
- `ActivitySource` spans (`Pool.Acquire`, `Pool.Grow`, `Pool.HealthCheck`, `Pool.Release`)

**Why second:** depends on Phase 1's data structures; adding sweep/grow/shrink to a working pool is incremental. Shipping it as Phase 2 (vs. all-at-once) lets us validate Phase 1 in isolation.

**Test:** burst → idle → burst cycle (the motivating scenario); verify telemetry numbers match observable behavior; deterministic shrink with `FakeTimeProvider`.

### Phase 3: "RabbitMQ Adapter" — proves the abstraction

**Ships:**
- `Oragon.ElasticPool.RabbitMQ` package
- `AddElasticConnectionPool` (single-layer)
- `AddElasticChannelPool` (layered)
- `ChannelLease` internal helper + `ConditionalWeakTable` lifecycle
- Bursty publisher sample
- Testcontainers integration tests

**Why third:** validates that Core's hooks/policy abstraction is sufficient for a real, layered, lifecycle-sensitive scenario. If anything is missing in Core, Phase 3 surfaces it. If Phase 3 needs new Core API, that's a signal to refactor — and it's much cheaper to refactor at this point than after community adoption.

### Phase 4: "Polish for v1.0 release"

- Graceful drain `DisposeAsync(TimeSpan)`
- README quickstart + OTel example
- CI matrix (net8/net9/net10)
- SemVer + MinVer + SourceLink + symbol packages
- `PublicAPI.Shipped.txt` baselined
- NuGet publish

### Post-v1 (P2/P3, deferred)

- `QuarantineWithBackoffFailurePolicy<T>`
- Per-item `MaxLifetime` / `MaxUses` rotation
- `Oragon.ElasticPool.Polly` glue (if community asks)
- Additional adapters (HttpClient, Npgsql, gRPC) — only if RabbitMQ adapter proves the abstraction in real use

## Architectural Patterns

### Pattern 1: Direct Handoff (waiter queue)

**What:** When releasing a pool item, prefer giving it directly to a waiting consumer instead of returning to the idle queue and forcing the waiter to dequeue.

**When to use:** any pool with a wait queue.

**Trade-offs:**
- (+) Eliminates TOCTOU races (waiter might miss enqueue+notify ordering)
- (+) Lower latency for waiters (no extra dequeue hop)
- (+) Mirrors HikariCP's `SynchronousQueue` design (validated at scale)
- (-) Requires bookkeeping: waiter cancellation must remove TCS from queue

**Example:**
```csharp
// On release
if (_waiters > 0 && _waiterChannel.Reader.TryRead(out var waiterTcs))
{
    if (waiterTcs.TrySetResult(entry))
    {
        Interlocked.Decrement(ref _waiters);
        return; // direct handoff — never touch _idle
    }
    // TCS was canceled; loop to next waiter or fall through
}
_idle.Enqueue(entry);
```

### Pattern 2: Frozen Options Record + Sealed Engine

**What:** Mutable builder produces an immutable options record; engine takes the record in constructor; engine cannot be reconfigured after construction.

**When to use:** any library where misconfiguration after start would cause subtle bugs.

**Trade-offs:**
- (+) Thread-safe by construction (no need to synchronize config reads)
- (+) Clear lifecycle: configure → freeze → run
- (+) Testable: pass options directly in tests, skip builder
- (-) Reconfiguring requires creating a new pool (correct behavior — pool state is tied to its config)

### Pattern 3: Hooks Over Subclasses

**What:** Customize behavior via delegate hooks (`Factory`, `BeforeUse`, etc.) rather than `protected virtual` overrides.

**When to use:** any framework primitive where consumers customize lifecycle stages.

**Trade-offs:**
- (+) Composable (multiple hooks combinable; subclasses can't multiple-inherit)
- (+) DI-friendly (hooks close over `IServiceProvider`)
- (+) Allows engine to be `sealed` — no fragile-base-class problems
- (-) Slightly more verbose at the call site than `class MyPool : Pool<T> { override ... }`
- (-) Stack traces less informative than overridden methods (mitigated with good hook naming)

### Pattern 4: Layered Composition via Adapter

**What:** "Pool of B where B is built from a pool of A" is implemented in the **adapter** layer using Core's public hooks; Core has no concept of layering.

**When to use:** when domain-specific composition exists (channels-on-connections); when generalizing to Core would force an abstraction not yet validated.

**Trade-offs:**
- (+) Core stays simple; one less concept in public API
- (+) Each adapter chooses its own coupling strategy (lease pairing here; could be different for HTTP)
- (-) If multiple adapters need similar lifecycle pairing, may eventually warrant a Core helper

### Pattern 5: Background Sweep (One Loop, Two Concerns)

**What:** Single `PeriodicTimer` task performs BOTH shrink and health-check passes per tick.

**When to use:** when concerns have similar cadence and don't conflict.

**Trade-offs:**
- (+) One task, one allocation, one cancellation token
- (+) Predictable interleaving (shrink first, then check) is easier to reason about than two timers
- (-) If health checks become long, they delay shrink. Mitigation: time-bound check pass; defer to next sweep if exceeded.

## Anti-Patterns

### Anti-Pattern 1: Holding Locks Across Hook Invocations

**What people do:** lock a mutex around the entire acquire-or-grow path, including the `Factory` invocation.

**Why it's wrong:** Factory may take seconds (RabbitMQ TCP handshake). All other acquires block — pool throughput collapses to factory latency.

**Do this instead:** Use CAS to **reserve** the slot inside the (very short) lock or atomic op; invoke Factory **outside** the lock; on factory failure, unreserve the slot.

### Anti-Pattern 2: `ConcurrentQueue` for Both Items and Waiters

**What people do:** put both `T` items and `TaskCompletionSource<T>` waiters into the same data structure with discriminator.

**Why it's wrong:** complicates reasoning, adds allocation per wait, defeats `Channel<T>`'s built-in cancellation handling.

**Do this instead:** two structures — `ConcurrentQueue<PoolEntry<T>>` for idle items, `Channel<TCS<PoolEntry<T>>>` for waiters. Direct handoff bridges them.

### Anti-Pattern 3: Sweeping with `Task.Delay(interval)` Loop

**What people do:**
```csharp
while (!ct.IsCancellationRequested)
{
    await Task.Delay(interval, ct);
    await DoSweepAsync();
}
```

**Why it's wrong:** drift. If sweep work takes 1.5s and interval is 30s, real cadence is 31.5s — telemetry interpretation breaks.

**Do this instead:** `PeriodicTimer.WaitForNextTickAsync(ct)` — drift-free.

### Anti-Pattern 4: Subclass-Based Extension

**What people do:** make `ElasticPool<T>` non-sealed with `protected virtual OnAcquire`, etc.

**Why it's wrong:** fragile base class — every method becomes a stable extension point; one internal refactor can break consumers. Also encourages misuse (consumers think subclassing is the right way; it's not).

**Do this instead:** `sealed class ElasticPool<T>`; extension via hooks and policies (`IItemFailurePolicy<T>`).

### Anti-Pattern 5: Async-Over-Sync Factory in Sync Code Paths

**What people do:** call `factory.GetResult()` or `.Wait()` inside acquire to "support sync acquire."

**Why it's wrong:** RabbitMQ.Client v7 is async-only; blocking on async = deadlock surface in any sync context (sync-over-async).

**Do this instead:** sync `Acquire()` ONLY when an idle item is available without waiting; for any path that requires creation/wait, force the consumer to use `AcquireAsync`. PROJECT.md already commits to this rule.

### Anti-Pattern 6: Returning Broken Items "Just in Case"

**What people do:** when health check fails, return the item to the queue anyway "in case it recovers."

**Why it's wrong:** defeats the auto-cure value proposition; the next consumer hits the same broken item.

**Do this instead:** delegate to `IItemFailurePolicy<T>` — discard (default) or quarantine with backoff; never silently re-enqueue.

### Anti-Pattern 7: Exposing the Engine Class

**What people do:** make `ElasticPool<T>` public so consumers can `new ElasticPool<T>(options)`.

**Why it's wrong:** bypasses the builder's validation; couples consumers to internal constructor signature; breaks API evolution.

**Do this instead:** keep `ElasticPool<T>` internal; public path is `ElasticObjectPoolFactory.Build<T>(...)...Build()`.

## Integration Points

### External Services / Libraries

| Service | Integration Pattern | Notes |
|---|---|---|
| `RabbitMQ.Client` v7.x | Adapter package only; Core doesn't depend on it | `IConnection.IsOpen` / `IChannel.IsOpen` for health probes; `CreateChannelAsync(options)` for channel creation; `BasicProperties` is now a value type |
| `Microsoft.Extensions.Logging` | `ILogger<ElasticPool<T>>` resolved from `IServiceProvider`; falls back to `NullLogger<T>` if absent | Source-gen logging via `[LoggerMessage]` |
| `Microsoft.Extensions.DependencyInjection` (impl) | Only consumer-side; Core depends on Abstractions only | `AddElasticPool<T>`/`AddKeyedElasticPool<T>` extension methods |
| `IMeterFactory` | Optional dependency; if registered, used for `Meter` creation; otherwise `new Meter("Oragon.ElasticPool")` | net8+ in-box |
| `OpenTelemetry.Extensions.Hosting` | Consumer-side; consumer adds `.AddMeter("Oragon.ElasticPool").AddSource("Oragon.ElasticPool")` to their OTel pipeline | We document the names as part of the API contract |

### Internal Boundaries

| Boundary | Communication | Notes |
|---|---|---|
| `Builder ↔ Engine` | Builder produces `ElasticPoolOptions<T>`; engine constructor takes it | One-way; no callbacks |
| `Engine ↔ Sweeper` | Sweeper is owned by engine; calls private engine methods (`TryEvictIdle`, `RunHealthCheck`) | Direct method call inside same assembly; sweeper has same lifetime as engine |
| `Engine ↔ TelemetryEmitter` | Engine instantiates emitter in ctor; calls typed methods (`emitter.OnGrow(name, newSize)`) | Decouples Meter API from engine business logic |
| `Engine ↔ FailurePolicy` | Engine invokes `policy.OnFailure(entry, reason, ct)`; policy returns disposition | Policy may discard (caller decrements _total) or quarantine (caller re-enqueues with flag) |
| `RabbitMQ Adapter ↔ Core` | Adapter calls `ElasticObjectPoolFactory.Build<T>(...)`; uses public hooks; never references internal types | Strict — adapter compiles against Core's NuGet, not its source |
| `RabbitMQ Channel Pool ↔ RabbitMQ Connection Pool` | Channel pool's `Factory` hook holds an `IElasticPool<IConnection>` reference (closed over in delegate) | Pure runtime composition; no Core changes needed |

## Cross-References to Mature Pool Implementations

| Implementation | What we adopt | What we reject |
|---|---|---|
| **HikariCP** ([source](https://github.com/brettwooldridge/HikariCP)) | Direct handoff via SynchronousQueue (we use `Channel<TCS>`); strict sealing/no-extension; "Prime Directive — only block on the pool"; dedicated waiter counter; `keepalive` ≈ our background `Check` pass | Three-tier ConcurrentBag with ThreadLocal — overkill for our acquire-on-A/release-on-B publisher pattern; JMX (we use OTel-native instead) |
| **`Microsoft.Extensions.ObjectPool`** ([source](https://github.com/dotnet/aspnetcore/blob/main/src/ObjectPool/src/DefaultObjectPool.cs)) | `_fastItem` + `ConcurrentQueue<T>` two-tier idle storage (we keep the queue; skip the `_fastItem` field — it helps for cheap StringBuilder-style objects, not for heavy resources); minimal-allocation philosophy | Fixed-only, no-health design — we replace |
| **Apache commons-pool2** ([docs](https://commons.apache.org/proper/commons-pool/)) | Five-stage lifecycle (`make/activate/validate/passivate/destroy`) → our `Factory/BeforeUse/Check/AfterUse/Release`; idle eviction sweeper concept; abandoned-object detection (deferred to v2) | JMX-only metrics; verbose Java factory class API — we use delegates |
| **node generic-pool** ([repo](https://github.com/coopernurse/node-pool)) | `acquireTimeoutMillis`/`idleTimeoutMillis` semantics (we use `CancellationToken` + `IdleTimeout`); `testOnBorrow`/`testWhileIdle` (our `BeforeUse` and `Check`); eager-min-creation behavior | Promise-based API (we use `ValueTask`) |
| **Reactor Pool** ([javadoc](https://projectreactor.io/docs/pool/snapshot/api/reactor/pool/)) | `GracefulShutdownInstrumentedPool` decorator → our drain semantics with grace timeout; explicit pool state machine (Open/Draining/Closed) | Reactive Streams API — we're imperative async |
| **OTel database-pool semantic conventions** ([spec](https://opentelemetry.io/docs/specs/semconv/database/database-metrics/)) | Metric names (`db.client.connection.count`, `pending_requests`, `wait_time`, `use_time`, `create_time`) → our `oragon.pool.*` adaptation; `pool.name` tag; `state` attribute (`idle`/`used`) | Strict `db.client.*` namespace (we're not strictly db) |

## Sources

- [Microsoft.Extensions.ObjectPool DefaultObjectPool source (dotnet/aspnetcore)](https://github.com/dotnet/aspnetcore/blob/main/src/ObjectPool/src/DefaultObjectPool.cs) — `_fastItem` + `ConcurrentQueue<T>` two-tier storage (HIGH)
- [Channels - .NET (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels) — bounded channel back-pressure semantics, cancellation (HIGH)
- [An Introduction to System.Threading.Channels (.NET Blog)](https://devblogs.microsoft.com/dotnet/an-introduction-to-system-threading-channels/) — producer/consumer patterns, handoff (HIGH)
- [HikariCP ConcurrentBag source](https://github.com/brettwooldridge/HikariCP/blob/dev/src/main/java/com/zaxxer/hikari/util/ConcurrentBag.java) — direct-handoff and waiter design (HIGH)
- [HikariCP Key Features and Design Philosophy (DeepWiki)](https://deepwiki.com/brettwooldridge/HikariCP/1.1-key-features-and-design-philosophy) — Prime Directive, design rationale (HIGH)
- [Apache commons-pool2 GenericObjectPool API](https://commons.apache.org/proper/commons-pool/apidocs/org/apache/commons/pool2/impl/GenericObjectPool.html) — five-stage lifecycle reference (HIGH)
- [node-pool / generic-pool](https://github.com/coopernurse/node-pool) — small focused pool API reference (HIGH)
- [Reactor Pool GracefulShutdownInstrumentedPool javadoc](https://projectreactor.io/docs/pool/snapshot/api/reactor/pool/decorators/GracefulShutdownInstrumentedPool.html) — drain semantics (HIGH)
- [OTel database connection pool semantic conventions](https://opentelemetry.io/docs/specs/semconv/database/database-metrics/) — metric names and tag conventions (HIGH)
- [PeriodicTimer (Microsoft Learn / Q&A)](https://learn.microsoft.com/en-us/answers/questions/1357004/how-do-i-use-periodictimer-for-short-time-interval) — drift-free single-consumer timer (HIGH)
- [PeriodicTimer real-world usage (Always Developing)](https://www.alwaysdeveloping.net/dailydrop/2022/08/09-periodic-timer/) — accurate periodic scheduling pattern (MEDIUM)
- [.NET Observability with OpenTelemetry (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel) — `IMeterFactory`, `ActivitySource` BCL surface (HIGH)
- [High-performance logging (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/high-performance-logging) — `[LoggerMessage]` source generator (HIGH)
- [DefaultObjectPool docs (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.objectpool.defaultobjectpool-1) — public surface conventions (HIGH)
- [RabbitMQ .NET Client v7 docs](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) — `IConnection`/`IChannel` async-first API (HIGH)

---
*Architecture research for: Oragon.ElasticPool — adaptive in-process pool + RabbitMQ adapter*
*Researched: 2026-05-03*
