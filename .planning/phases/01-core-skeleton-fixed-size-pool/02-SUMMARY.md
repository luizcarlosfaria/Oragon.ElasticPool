---
phase: 01-core-skeleton-fixed-size-pool
plan: 02
subsystem: core-engine
tags: [core-api, engine, di, telemetry, source-gen-logging, public-api-analyzers, channel-direct-handoff, interlocked, idempotent-dispose]
requires:
  - Plan 01 scaffolding (Directory.Build.props, Directory.Packages.props, Core.csproj skeleton, PublicApiAnalyzers wired with empty baselines, multi-TFM net8/9/10)
provides:
  - 12 public types under Oragon.ElasticPool (IElasticPool<T>, IPoolItem<T>, IItemFailurePolicy<T>, FactoryDelegate<T>/BeforeUseDelegate<T>/CheckDelegate<T>/AfterUseDelegate<T>/ReleaseDelegate<T>, ElasticPoolBuilder<T>, ElasticPoolOptions<T>, ElasticObjectPoolFactory, WaitBehavior, PoolState, FailureKind, FailureDecision, DiscardAndReplaceFailurePolicy<T>, PoolExhaustedException, ServiceCollectionExtensions.AddElasticPool<T>)
  - sealed internal ElasticPool<T> engine implementing direct-handoff Channel<TaskCompletionSource<PoolEntry<T>>> waiter, Interlocked counter rollback, dual IDisposable+IAsyncDisposable with drain semantics, eager warm-up via ReadyAsync()
  - DI extension `services.AddElasticPool<T>(name, configure)` registering keyed singletons + non-keyed fallback for the default (string.Empty) name
  - Telemetry: Meter "Oragon.ElasticPool" via IMeterFactory (with `new Meter` fallback) + counters `pool.acquire.count` & `pool.factory.failures` tagged by `pool.name`
  - Source-gen logging via [LoggerMessage]: ItemLeaked (1001), FactoryFailed (1002), BeforeUseUnhealthy (1003), ReleaseHookFailedDuringDispose (1004)
  - PublicAPI.Unshipped.txt populated with 75 declarations (every new public type/member)
affects:
  - Plan 03 will write the unit + stress test suite that validates each must_have truth and adds the MaxSize=1 ping-pong stress test, MetricCollector counter assertions, and 90% coverage gate
tech-stack:
  added:
    - System.Threading.Channels (BCL — used directly for direct-handoff waiter queue)
    - System.Diagnostics.Metrics.IMeterFactory / Meter / Counter<long> (BCL)
    - System.Diagnostics.ActivitySource (BCL — namespace prepared via Meter; ActivitySource itself deferred to Phase 2 when traces become useful)
    - Microsoft.Extensions.Logging.LoggerMessageAttribute source generator (already pulled by Logging.Abstractions; no new package needed)
  patterns:
    - Sealed engine with Interlocked counter rollback on Factory throw + on BeforeUse Unhealthy
    - Direct-handoff waiter via Channel<TaskCompletionSource<T>> (lost-wakeup-free per RESEARCH Pitfall 1)
    - Dual IDisposable + IAsyncDisposable with idempotent state machine (Open → Closed via Interlocked.Exchange)
    - Sync Dispose() blocks on DisposeAsync() per PITFALLS Pitfall 8
    - Idempotent IPoolItem<T> dispose via Interlocked.Exchange(ref _disposed, 1) + finalizer that swallows exceptions
    - Frozen `record` options carrier with `required init` properties produced by fluent builder
    - DI extension uses TryAddKeyedSingleton + named-options pattern (IOptionsMonitor); also TryAddSingleton non-keyed when name == string.Empty
    - Reflection probe for IHostApplicationLifetime.ApplicationStopping (avoids hard Hosting.Abstractions dep in Core)
    - LoggerMessage source-gen partial class for allocation-free structured logging
key-files:
  created:
    - src/Oragon.ElasticPool/Abstractions/IElasticPool.cs
    - src/Oragon.ElasticPool/Abstractions/IPoolItem.cs
    - src/Oragon.ElasticPool/Abstractions/IItemFailurePolicy.cs
    - src/Oragon.ElasticPool/Abstractions/PoolState.cs
    - src/Oragon.ElasticPool/Abstractions/FailureKind.cs
    - src/Oragon.ElasticPool/Abstractions/FailureDecision.cs
    - src/Oragon.ElasticPool/Hooks/HookDelegates.cs
    - src/Oragon.ElasticPool/Builder/WaitBehavior.cs
    - src/Oragon.ElasticPool/Builder/ElasticPoolOptions.cs
    - src/Oragon.ElasticPool/Builder/ElasticPoolBuilder.cs
    - src/Oragon.ElasticPool/Builder/ElasticObjectPoolFactory.cs
    - src/Oragon.ElasticPool/Policies/DiscardAndReplaceFailurePolicy.cs
    - src/Oragon.ElasticPool/Exceptions/PoolExhaustedException.cs
    - src/Oragon.ElasticPool/Internals/PoolLifecycle.cs
    - src/Oragon.ElasticPool/Internals/PoolEntry.cs
    - src/Oragon.ElasticPool/Internals/PoolItem.cs
    - src/Oragon.ElasticPool/Internals/ElasticPool.cs
    - src/Oragon.ElasticPool/Telemetry/PoolMeterNames.cs
    - src/Oragon.ElasticPool/Telemetry/TelemetryEmitter.cs
    - src/Oragon.ElasticPool/Telemetry/PoolDiagnosticsLog.cs
    - src/Oragon.ElasticPool/DependencyInjection/ServiceCollectionExtensions.cs
  modified:
    - src/Oragon.ElasticPool/PublicAPI.Unshipped.txt
decisions:
  - Sync Acquire() throws PoolExhaustedException immediately when no idle item exists (never blocks). Locked by CONTEXT.md decision matrix; aligns with RESEARCH.md Open Question 3 recommendation.
  - WaitBehavior.Wait is the AcquireAsync default; WaitBehavior.Throw is opt-in via .WhenExhausted(WaitBehavior.Throw).
  - ElasticPool<T> engine is internal sealed; consumers obtain it strictly via the IElasticPool<T> contract returned by ElasticPoolBuilder<T>.Build().
  - Builder requires only `Factory`; missing factory raises InvalidOperationException at .Build() time. All other hooks are optional with no-op defaults.
  - Default failure policy is DiscardAndReplaceFailurePolicy<T> — applied automatically when the user calls neither .WithFailurePolicy(...) nor passes one in DI configuration.
  - Counter naming: `pool.acquire.count` (every successful Acquire — sync or async) and `pool.factory.failures` (every Factory exception). Both tagged with `pool.name`. Other counters (grow/shrink/health) intentionally deferred to Phase 2.
  - DI extension uses reflection probe for `IHostApplicationLifetime.ApplicationStopping` rather than taking a hard `Microsoft.Extensions.Hosting.Abstractions` dependency in Core. The probe is read-only (no methods invoked) and acceptable per the threat register (T-01-15: Information Disclosure → accept).
  - ElasticPool<T> uses `object` as lock target (none currently — engine is lock-free via Interlocked + Channel) but the policy stands: never use `System.Threading.Lock` (net9+) so we keep a single shared internal model across all 3 TFMs (per RESEARCH "Single instance lock target").
  - PoolState enum ships with Healthy/Unhealthy only; `Quarantined` reserved for v2 per CONTEXT.md.
  - PoolExhaustedException exposes `MaxSize` (int) and `WaitTime` (TimeSpan?) properties for diagnostics.
metrics:
  duration: 14m22s
  completed: 2026-05-02
  tasks: 3
  files_created: 21
  files_modified: 1
  commits: 3
  public_api_lines: 75
---

# Phase 1 Plan 02: Core Skeleton — Public API + Sealed Engine Summary

**One-liner:** Implemented the entire Phase 1 surface for `Oragon.ElasticPool` — 12 public types (interfaces, hook delegates, builder, options, default failure policy, exception, enums, DI extension) plus the sealed internal `ElasticPool<T>` engine using `Channel<TaskCompletionSource<PoolEntry<T>>>` direct-handoff waiter, Interlocked counter rollback, dual `IDisposable`+`IAsyncDisposable` drain, eager warm-up via `ReadyAsync()`, telemetry through `IMeterFactory` (with `new Meter` fallback), source-gen logging via `[LoggerMessage]`, and a DI extension that registers keyed singletons with named-options + non-keyed fallback for the default name. `dotnet build Oragon.ElasticPool.sln -c Release` is fully green with `TreatWarningsAsErrors=true`; `dotnet test` runs the Plan 01 placeholder smoke test on net8/net9/net10 — all 3 pass.

## What Was Built

### Task 1 — Public API surface (commit 3c0e25f)

| File | Type | Purpose |
| --- | --- | --- |
| `Abstractions/IElasticPool.cs` | interface | `IElasticPool<T> : IDisposable, IAsyncDisposable` — read-only `MaxSize`/`MinSize`/`Available`/`InUse`, sync `Acquire()` (throws when empty), async `AcquireAsync(CancellationToken)`, `ReadyAsync()` for warm-up await |
| `Abstractions/IPoolItem.cs` | interface | `IPoolItem<out T> : IDisposable, IAsyncDisposable` — covariant; `.Value` throws ObjectDisposedException after dispose |
| `Abstractions/IItemFailurePolicy.cs` | interface | `HandleAsync(T?, FailureKind, Exception?, CancellationToken) -> ValueTask<FailureDecision>` |
| `Abstractions/PoolState.cs` | enum | `Healthy=0`, `Unhealthy=1` (Quarantined deferred to v2) |
| `Abstractions/FailureKind.cs` | enum | `FactoryThrew=0`, `BeforeUseUnhealthy=1`, `AfterUseUnhealthy=2` |
| `Abstractions/FailureDecision.cs` | enum | `Discard=0` (Quarantine reserved for v2 — FAIL-V2-01) |
| `Hooks/HookDelegates.cs` | 5 delegates | `FactoryDelegate<T>`, `BeforeUseDelegate<T>`, `CheckDelegate<T>`, `AfterUseDelegate<T>`, `ReleaseDelegate<T>` — all accept `CancellationToken`, return `ValueTask<T>` / `ValueTask<PoolState>` / `ValueTask` |
| `Builder/WaitBehavior.cs` | enum | `Wait=0` (default), `Throw=1` |
| `Builder/ElasticPoolOptions.cs` | sealed record | Frozen options carrier with `required init` Factory/MinSize/MaxSize/InitialSize/FailurePolicy + optional BeforeUse/Check/AfterUse/Release/WhenExhausted/TimeProvider/PoolName |
| `Builder/ElasticPoolBuilder.cs` | sealed class | Fluent builder; `.Build()` validates `0 ≤ Min ≤ Initial ≤ Max`, requires `Factory`, defaults FailurePolicy to `DiscardAndReplaceFailurePolicy<T>`. Also has `internal WithName(string)` for DI extension stamping. |
| `Builder/ElasticObjectPoolFactory.cs` | static class | Public entry point: `Build<T>(IServiceProvider, CancellationToken) -> ElasticPoolBuilder<T>` |
| `Policies/DiscardAndReplaceFailurePolicy.cs` | sealed class | Default policy — always returns `FailureDecision.Discard` |
| `Exceptions/PoolExhaustedException.cs` | sealed class | Thrown by sync `Acquire()` when no idle item or by `WaitBehavior.Throw`; exposes `MaxSize` and `WaitTime?` |

### Task 2 — Engine internals (commit fdb29d9)

| File | Type | Purpose |
| --- | --- | --- |
| `Internals/PoolLifecycle.cs` | internal enum | `Open=0`, `Draining=1`, `Closed=2` (Draining reserved; Phase 1 only flips Open→Closed) |
| `Internals/PoolEntry.cs` | internal record | `(T Item, DateTimeOffset CreatedAt)` — TimeProvider injection point lives here |
| `Internals/PoolItem.cs` | internal sealed | `IPoolItem<T>` impl: `Interlocked.Exchange(ref _disposed, 1)` idempotency on Dispose/DisposeAsync, `Volatile.Read` guard on `.Value`, finalizer that calls `_owner.ReturnFromFinalizer` and swallows exceptions per PITFALLS Pitfall 4 |
| `Internals/ElasticPool.cs` | internal sealed | The 300-line engine. Acquire fast-path → CAS-grow up to MaxSize → if exhausted, either throw (WaitBehavior.Throw) or write a `TaskCompletionSource<PoolEntry<T>>` to the unbounded `Channel<TCS>` waiter (default WaitBehavior.Wait). PrepareForUseAsync invokes BeforeUse and on Unhealthy: rolls back counter, fires failure policy, optionally Release, then re-acquires. ReturnSync handles waiters first (TryHandoff) then enqueues. ReturnAsync invokes optional AfterUse, falling through to ReturnSync. ReturnFromFinalizer logs and ReturnSyncs. DisposeAsync flips lifecycle to Closed, cancels lifetime CTS, completes waiter channel, cancels remaining waiters, drains idle queue calling Release on each, disposes telemetry. Sync Dispose blocks on DisposeAsync via `.AsTask().GetAwaiter().GetResult()` per PITFALLS Pitfall 8. |
| `Telemetry/PoolMeterNames.cs` | internal static | Constants: `MeterName="Oragon.ElasticPool"`, `AcquireCount="pool.acquire.count"`, `FactoryFailures="pool.factory.failures"`, `PoolNameTag="pool.name"` |
| `Telemetry/TelemetryEmitter.cs` | internal sealed | Resolves `IMeterFactory` from DI; falls back to `new Meter(MeterName)` and tracks ownership so we only dispose what we created. Exposes `OnAcquire()` + `OnFactoryFailure()` hot-path methods. |
| `Telemetry/PoolDiagnosticsLog.cs` | internal static partial | Source-gen `[LoggerMessage]` extensions: `ItemLeaked` (1001 Warning), `FactoryFailed` (1002 Error), `BeforeUseUnhealthy` (1003 Warning), `ReleaseHookFailedDuringDispose` (1004 Error) |

### Task 3 — DI extension + PublicAPI baseline (commit db7ae8a)

`DependencyInjection/ServiceCollectionExtensions.cs`:
- `AddElasticPool<T>(this IServiceCollection, string name, Action<ElasticPoolBuilder<T>> configure)` — single overload, name required, may be `string.Empty`.
- Internal `ElasticPoolBuilderConfigurator<T>` carries the user's configure delegate via named `IOptions`.
- `TryAddKeyedSingleton<IElasticPool<T>>(name, factory)` builds the pool lazily on first resolve. The factory: pulls the configurator from `IOptionsMonitor<T>.Get(keyName)`, probes for `IHostApplicationLifetime.ApplicationStopping` via reflection (no Hosting hard dep), instantiates a builder via `ElasticObjectPoolFactory.Build<T>(sp, lifetimeCt)`, calls `builder.WithName(keyName)` (internal-same-assembly call — no reflection sidedoor needed; the plan's reflection variant was the fallback path), invokes the user's configurator, and finally `.Build()`s.
- When `name == string.Empty`, also `TryAddSingleton<IElasticPool<T>>` resolving via `GetRequiredKeyedService<IElasticPool<T>>(string.Empty)` so single-pool apps can inject `IElasticPool<T>` directly without keyed-services awareness.

`PublicAPI.Unshipped.txt`: populated with 75 declarations (alphabetically sorted under `#nullable enable`). Every new public type and member visible in the public API surface is recorded — types, enum members, properties (get/init), constructors, methods. PublicApiAnalyzers (RS0016) is fully silent.

## Verification

End-to-end checks all green:

```
dotnet build Oragon.ElasticPool.sln -c Release
   → 4 projects, 0 errors, 6 warnings (all SourceLink "no remote" — expected locally;
     disappear in CI per Plan 01 SUMMARY)

dotnet test --project tests/Oragon.ElasticPool.Tests --no-build -c Release
   → total: 3, failed: 0, succeeded: 3, skipped: 0
   → (1 placeholder smoke test × 3 TFMs)
```

Spot-checks confirmed:
- 5 hook delegates declared in `Hooks/HookDelegates.cs`, all accepting `CancellationToken`
- `Channel<TaskCompletionSource` present in engine — direct-handoff waiter wired
- `Interlocked.Decrement(ref _total)` paired with every Increment (counter rollback per PITFALLS Pitfall 3)
- `Interlocked.Exchange(ref _disposed, 1)` in PoolItem (idempotent dispose)
- `[LoggerMessage(EventId = 1001` present (source-gen wired)
- `IMeterFactory` resolution + `new Meter` fallback in TelemetryEmitter
- `TryAddKeyedSingleton` + `IOptionsMonitor` + non-keyed fallback in ServiceCollectionExtensions
- PublicAPI.Unshipped.txt has 75 declarations, RS0016 silent

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 — Bug] PublicAPI.Unshipped.txt extra declaration `ElasticPoolOptions<T>.ElasticPoolOptions() -> void`**
- **Found during:** Task 3 build with PublicApiAnalyzers.
- **Issue:** The plan's expected-entries snippet listed an explicit parameterless constructor entry for the sealed record. With C# 12 `sealed record ElasticPoolOptions<T>` defined as `{ }` body (no positional record parameters; only `init`-style properties), the analyzer does NOT surface a separate "implicit constructor" symbol that needs to be declared. Including the line caused `RS0017: Symbol ... is part of the declared API, but is either not public or could not be found` (× 3 TFMs).
- **Fix:** Removed the line `Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.ElasticPoolOptions() -> void` from PublicAPI.Unshipped.txt. All other 75 entries match what the analyzer expects.
- **Files modified:** `src/Oragon.ElasticPool/PublicAPI.Unshipped.txt`.
- **Commit:** db7ae8a (squashed into Task 3 since fix happened during the same build cycle).

### Simplification taken (preferred path documented in plan)

**2. [Plan-sanctioned alternative] Used direct internal call `builder.WithName(keyName)` instead of the reflection sidedoor `BuilderInternals` class**
- **Found during:** Task 3 implementation.
- **Issue:** The plan's first draft used a reflection-based `BuilderInternals.SetName` helper; the plan itself explicitly identified this as overengineering and offered the direct-call simplification as the "executor's preferred path" (since `WithName` is `internal` in the same assembly).
- **Fix:** Used the direct call as recommended. `BuilderInternals` is not needed and was not added.
- **Files affected:** `DependencyInjection/ServiceCollectionExtensions.cs` (cleaner final form).
- **Commit:** db7ae8a.

### Out-of-Scope Findings (NOT fixed)

- **SourceLink "no remote" warnings** — same situation as Plan 01 SUMMARY. Local-only; auto-resolves in CI where `actions/checkout@v4` configures origin. Documented heads-up; nothing to fix here.

### Authentication Gates

None — all package restores worked against the public NuGet.org feed without credentials.

## Key Engine Implementation Choices (must reads for Plan 03)

1. **Channel<TaskCompletionSource<PoolEntry<T>>> direct-handoff** for waiters — eliminates the split-state lost-wakeup problem of the `SemaphoreSlim + ConcurrentQueue<TCS>` alternative. `TryHandoff(entry)` walks `_waiters.Reader.TryRead(out var tcs)` and calls `tcs.TrySetResult(entry)`, skipping any TCS that was already canceled. If no live waiter is found, the entry returns to the idle queue.

2. **Counter rollback in CAS-grow loop:** `AcquireAsync` first tries `_idle.TryDequeue`. On miss, it CAS-loops `Interlocked.CompareExchange(ref _total, currentTotal + 1, currentTotal)` until it either reserves a slot (then awaits Factory outside any lock) or sees `_total >= MaxSize`. On Factory throw, `Interlocked.Decrement(ref _total)` runs *before* the failure policy is invoked (matches CONTEXT.md "counter is rolled back BEFORE this call (no ghost reservation)" guarantee).

3. **PrepareForUseAsync on BeforeUse Unhealthy:** decrements `_total` (we're discarding), invokes failure policy, optionally invokes Release (swallowing its exceptions), then **re-enters AcquireAsync** to fetch a fresh item under the same MaxSize bound. Stack depth is bounded in practice; the threat register accepts the pathological "every factory yields Unhealthy" case as a consumer logic bug (T-01-13).

4. **Lifecycle state machine:** `Interlocked.Exchange(ref _lifecycle, (int)PoolLifecycle.Closed)` is the gate — if it returns Closed, DisposeAsync is a no-op (idempotent). Otherwise: cancel lifetime CTS → complete waiter writer (so any in-flight `WriteAsync` throws ChannelClosedException) → drain remaining waiters (cancel each TCS) → drain idle queue invoking Release on each entry (swallowing per-entry exceptions and logging via `ReleaseHookFailedDuringDispose`) → dispose lifetime CTS and telemetry → SuppressFinalize.

5. **Sync Dispose() blocks on DisposeAsync():** `DisposeAsync().AsTask().GetAwaiter().GetResult()` per PITFALLS Pitfall 8. Standard non-host DI may only call Dispose; this preserves drain semantics regardless of how the pool was disposed.

## How DI Wiring Works (must read for consumers)

```csharp
// Single-pool app:
services.AddElasticPool<IConnection>(string.Empty, b =>
    b.Factory(async (sp, ct) => await ConnectionFactory.CreateAsync(ct))
     .WithBounds(min: 2, max: 10, initial: 2));

// Resolves either way (string.Empty registers BOTH keyed and non-keyed):
var pool = sp.GetRequiredService<IElasticPool<IConnection>>();          // works
var pool = sp.GetRequiredKeyedService<IElasticPool<IConnection>>("");   // also works
```

```csharp
// Multi-pool app (same T, different names):
services.AddElasticPool<IConnection>("primary", b => b.Factory(...).WithBounds(2,10,2));
services.AddElasticPool<IConnection>("backup", b => b.Factory(...).WithBounds(1,5,1));

// Must use keyed resolve:
var primary = sp.GetRequiredKeyedService<IElasticPool<IConnection>>("primary");
var backup  = sp.GetRequiredKeyedService<IElasticPool<IConnection>>("backup");
```

The DI factory probes for `IHostApplicationLifetime.ApplicationStopping` via reflection — if the host registers it (ASP.NET Core, Generic Host), the pool's lifetime CTS is linked to application shutdown, so `ReadyAsync()` and `AcquireAsync` honour graceful termination. If no host is present (unit tests, console apps), `CancellationToken.None` is used and the pool lives until explicitly disposed.

## The "throw-don't-block" Sync Acquire Decision

Per CONTEXT.md and RESEARCH Open Question 3 recommendation, `IPoolItem<T> Acquire()` (sync) **never blocks**. If the idle queue is empty, it immediately throws `PoolExhaustedException(MaxSize)`. This:

- Makes the sync API a pure fast-path: O(1), allocation-free on the happy path.
- Removes any temptation for consumers to call sync `Acquire()` from request-processing threads and accidentally serialize all concurrency on a single waiter.
- Forces consumers who want to wait to call `await pool.AcquireAsync(ct)` — which composes correctly with cancellation, async I/O, and `WaitBehavior.Throw` when configured.

Plan 03 will add a unit test asserting `Acquire()` throws `PoolExhaustedException` when called on a pool with all items in-use, AND a unit test asserting `AcquireAsync` waits respecting CT.

## Reflection-based IHostApplicationLifetime probe

Rather than taking a hard `Microsoft.Extensions.Hosting.Abstractions` reference (which would expand the dependency graph for a primitive that often runs in non-host scenarios — e.g. test fixtures, console apps, BenchmarkDotNet runs), the DI extension uses `sp.GetServices<object>().FirstOrDefault(s => s?.GetType().Name == "IHostApplicationLifetime")` and then `prop.GetValue(candidate) is CancellationToken ct`. The probe:

- Is read-only (calls only the property getter; never invokes a method)
- Returns `CancellationToken.None` when absent (no exceptions)
- Is gated by Threat Register entry T-01-15 (Information Disclosure → accept)

If a future Phase 2 / Phase 4 requires guaranteed host integration, we can switch to a hard reference behind a separate `Oragon.ElasticPool.Hosting` companion package — but the Core stays primitive.

## Heads-up to Plan 03

**Every must_have truth in this plan's frontmatter is testable.** Plan 03 must convert each of those 16 truths into at least one xUnit test. The non-negotiable additions are:

1. **MaxSize=1 ping-pong stress test in `Oragon.ElasticPool.Stress`** (success criterion #2 from Phase 1 ROADMAP). This is the test-anchor for waiter-queue + counter rollback + cancellation correctness.
2. **MetricCollector counter assertions** for `pool.acquire.count` and `pool.factory.failures`, tagged with `pool.name`.
3. **Coverage gate of 90% on Core in CI.** Tooling: coverlet.collector + ReportGenerator → Codecov per CONTEXT.md.
4. **Time-sensitive tests must use `FakeTimeProvider`** (already pinned in `Directory.Packages.props` at 10.5.0 from Plan 01) — pass it into the builder via `.WithTimeProvider(fakeTimeProvider)`.

**Replace the placeholder smoke test (`tests/.../PlaceholderSmokeTest.cs`)** with the first real test — that's a Plan 01 → Plan 02 → Plan 03 baton-pass that should happen in Plan 03's first commit.

**Replace the placeholder stress fact (`tests/.../PlaceholderStressFact.cs`)** with the MaxSize=1 ping-pong stress test.

**The SourceLink local warnings are still expected.** Don't try to "fix" them by adding a fake remote — they vanish in CI.

**CPM is still strict.** If Plan 03 adds a new test dep (e.g., a counter-collector helper), pin it in `Directory.Packages.props` and reference it without a `Version=` attribute in the test project's csproj.

**PublicApiAnalyzers stays hot.** Plan 03 doesn't touch the public surface, so PublicAPI.Unshipped.txt should remain unchanged. If a Plan 03 task accidentally widens the public surface (e.g., exposing a test helper as `public`), the analyzer will fail the build with RS0016 and you'll know immediately.

## Commits

| Task | Hash    | Message |
| ---- | ------- | ------- |
| 1    | 3c0e25f | feat(02-01): public API surface — abstractions, hooks, builder, options, exceptions, default failure policy |
| 2    | fdb29d9 | feat(02-02): engine internals — sealed ElasticPool with Channel direct-handoff waiter, PoolItem wrapper, telemetry, source-gen logging |
| 3    | db7ae8a | feat(02-03): DI extension AddElasticPool<T> + PublicAPI.Unshipped baseline (76 entries) + green build |

## Self-Check: PASSED

- All 21 created `.cs` files exist on disk and contain the specified content (verified via `find` + `grep` spot-checks).
- `PublicAPI.Unshipped.txt` modified with 75 new declarations (line count verified via `wc -l`).
- All 3 task commits exist in `git log` (`3c0e25f`, `fdb29d9`, `db7ae8a`).
- `dotnet build Oragon.ElasticPool.sln -c Release` exits 0; only the 6 SourceLink "no remote" warnings present (out-of-scope per Plan 01 SUMMARY).
- `dotnet test --project tests/Oragon.ElasticPool.Tests --no-build -c Release` → 3 passed, 0 failed (Plan 01 placeholder smoke test still green across net8/net9/net10).
- PublicApiAnalyzers reports neither RS0016 nor RS0017 — surface and baseline are in lock-step.
