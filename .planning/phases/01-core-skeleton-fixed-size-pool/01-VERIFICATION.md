---
phase: 01-core-skeleton-fixed-size-pool
verified: 2026-05-02T04:40:00Z
status: passed
score: 6/6 success criteria verified; 16/16 requirements verified
overrides_applied: 0
gaps: []
human_verification: []
---

# Phase 1: Core Skeleton — Fixed-Size Pool Verification Report

**Phase Goal:** Deliver a working, fully-tested fixed-size pool with all foundational architecture decisions (hook signatures, ValueTask contract, dispose semantics, waiter-queue design, counter rollback) locked in correctly — these are non-retrofittable without breaking changes.

**Verified:** 2026-05-02T04:40:00Z
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths (ROADMAP Success Criteria 1–6)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Consumer can call `services.AddElasticPool<T>(name, b => b.Factory(...).BeforeUse(...).Release(...))` and receive an `IElasticPool<T>` from DI that survives sync `Acquire()`, `await using` of `IPoolItem<T>`, and idempotent double-dispose | VERIFIED | `ServiceCollectionExtensions.AddElasticPool<T>` wired in `DependencyInjection/ServiceCollectionExtensions.cs`; tested by `ServiceCollectionExtensionsTests`, `AcquireAndReturnTests`, `PoolItemDisposeTests`, `PlaceholderSmokeTest.cs` (`DI_Build_Acquire_Dispose_Roundtrip_Works`). Tests pass: 210/210 on net8/net9/net10. |
| 2 | Pool with `MaxSize=1` survives 10,000-cycle ping-pong stress test with 256 concurrent threads — no deadlocks, every `AcquireAsync` completes within 5s watchdog or honors its `CancellationToken` | VERIFIED | `PingPongStressTest.MaxSize1_HundredsOfThreads_TenThousandIterations_NoDeadlock` exists in `tests/Oragon.ElasticPool.Stress/PingPongStressTest.cs` (61 lines, `[Fact(Timeout=60_000)]`). Live run: 3/3 TFMs pass in ~300ms, final invariants `InUse==0`, `Available==1` confirmed. |
| 3 | Factory throws → `_total` counter decremented (subsequent `Acquire` can reach `MaxSize`); `BeforeUse` returning `Unhealthy` invokes `IItemFailurePolicy<T>` and `DiscardAndReplaceFailurePolicy<T>` discards + replaces | VERIFIED | Counter rollback: `Interlocked.Decrement(ref _total)` at `ElasticPool.cs:129` (factory exception path) and `:191` (BeforeUse unhealthy path). Tested by `FactoryFailureTests.FactoryThrows_DecrementsTotal_AllowsSubsequentAcquireToReachMaxSize` and `BeforeUseUnhealthyTests.BeforeUseUnhealthy_DiscardAndReplace_DefaultPolicy_ProducesFreshItem`. `FailureKind.FactoryThrew` and `FailureKind.BeforeUseUnhealthy` both have test coverage. |
| 4 | Pool exposes `Meter` named `"Oragon.ElasticPool"` via `IMeterFactory` with counters `pool.acquire.count`, `pool.factory.failures` consumable by OTel listener | VERIFIED | `TelemetryEmitter.cs`: `services.GetService<IMeterFactory>()` with `new Meter` fallback; constants in `PoolMeterNames.cs` (`MeterName="Oragon.ElasticPool"`, `AcquireCount="pool.acquire.count"`, `FactoryFailures="pool.factory.failures"`, `PoolNameTag="pool.name"`). Tested by `MeterAndCounterTests` with `MetricCollector<long>` AND a raw `MeterListener` proving OTel-listenability. Fallback path (no `AddMetrics()`) also tested. |
| 5 | Pool implements both `IDisposable` and `IAsyncDisposable` with drain semantics: stops accepting new `Acquire`, waits for in-flight items, then releases all pooled items via `Release` hook | VERIFIED | `IElasticPool<T>` declares `: IDisposable, IAsyncDisposable`. `ElasticPool.DisposeAsync()` flips lifecycle via `Interlocked.Exchange`, cancels lifetime CTS, drains waiters, drains idle queue calling `Release` on each. Sync `Dispose()` blocks on `DisposeAsync().AsTask().GetAwaiter().GetResult()`. Tested by `DisposeDrainTests` (5 tests) and `AfterUseAndExceptionTests` (closing tests). |
| 6 | Eager warm-up to `InitialSize` is awaitable and cancellable; configuration `0 ≤ Min ≤ Initial ≤ Max` is validated at `.Build()` and throws on invalid bounds | VERIFIED | `ElasticPool.ReadyAsync()` returns `WarmupTask` (a `Task` set in constructor). `ElasticPoolBuilder.Build()` validates all four bound conditions at lines 47–55. Tested by `WarmupAndBoundsTests` (`ReadyAsync_CompletesWhenWarmedUp`, `PoolDispose_DuringWarmup_CancelsWarmupTask`) and `BuilderValidationTests` (`Build_WithInvalidBounds_Throws` theory + `Build_WithValidBounds_Succeeds` theory). |

**Score: 6/6 success criteria VERIFIED**

---

## Required Artifacts

| Artifact | Status | Evidence |
|----------|--------|----------|
| `src/Oragon.ElasticPool/Abstractions/IElasticPool.cs` | VERIFIED | Exists, 39 lines, declares `IElasticPool<T>: IDisposable, IAsyncDisposable` with `Acquire()`, `AcquireAsync(CancellationToken)`, `ReadyAsync()`, `MaxSize`, `MinSize`, `Available`, `InUse` |
| `src/Oragon.ElasticPool/Abstractions/IPoolItem.cs` | VERIFIED | Exists, `IPoolItem<out T>: IDisposable, IAsyncDisposable` with `.Value` (NOT `.Object` — CONTEXT.md decision honored) |
| `src/Oragon.ElasticPool/Hooks/HookDelegates.cs` | VERIFIED | All 5 delegates present: `FactoryDelegate<T>`, `BeforeUseDelegate<T>`, `CheckDelegate<T>`, `AfterUseDelegate<T>`, `ReleaseDelegate<T>` — all accept `CancellationToken`, all return `ValueTask<T>`/`ValueTask<PoolState>`/`ValueTask` |
| `src/Oragon.ElasticPool/Internals/ElasticPool.cs` | VERIFIED | 317 lines, sealed internal engine with `Channel<TaskCompletionSource<PoolEntry<T>>>` direct-handoff waiter, CAS-grow loop, `Interlocked` counter rollback, `DisposeAsync` drain, `Dispose()` blocks on `DisposeAsync()` |
| `src/Oragon.ElasticPool/Internals/PoolItem.cs` | VERIFIED | `Interlocked.Exchange(ref _disposed, 1)` idempotency, finalizer present, `Value` throws `ObjectDisposedException` after dispose |
| `src/Oragon.ElasticPool/Builder/ElasticPoolBuilder.cs` | VERIFIED | Only `Factory` is required; `.Build()` validates `0≤Min≤Initial≤Max`; `WhenExhausted` configurable; defaults to `DiscardAndReplaceFailurePolicy<T>` |
| `src/Oragon.ElasticPool/Telemetry/TelemetryEmitter.cs` | VERIFIED | `IMeterFactory` via DI with `new Meter` fallback; counters `pool.acquire.count` + `pool.factory.failures` tagged with `pool.name` |
| `src/Oragon.ElasticPool/Telemetry/PoolDiagnosticsLog.cs` | VERIFIED | `[LoggerMessage]` source-gen partial class with 4 events (1001–1004) |
| `src/Oragon.ElasticPool/DependencyInjection/ServiceCollectionExtensions.cs` | VERIFIED | `AddElasticPool<T>(name, configure)` with `TryAddKeyedSingleton` + non-keyed fallback for `string.Empty`; reflection probe for `IHostApplicationLifetime.ApplicationStopping` (no hard Hosting dep) |
| `src/Oragon.ElasticPool/Policies/DiscardAndReplaceFailurePolicy<T>.cs` | VERIFIED | Always returns `FailureDecision.Discard`; used as default when no policy is configured |
| `src/Oragon.ElasticPool/PublicAPI.Shipped.txt` | VERIFIED | Contains `#nullable enable` (canonical empty baseline) |
| `src/Oragon.ElasticPool/PublicAPI.Unshipped.txt` | VERIFIED | 78 lines: 75 public API declarations under `#nullable enable` — all public types and members recorded; PublicApiAnalyzers RS0016 silent |
| `tests/Oragon.ElasticPool.Tests/Builder/BuilderValidationTests.cs` | VERIFIED | 7 `[Fact]`/`[Theory]` tests; covers missing Factory, invalid bounds matrix, valid bounds |
| `tests/Oragon.ElasticPool.Tests/Pool/AcquireAndReturnTests.cs` | VERIFIED | 7 tests; covers sync fast-path, async grow, direct-handoff waiter, `await using` |
| `tests/Oragon.ElasticPool.Tests/Pool/PoolItemDisposeTests.cs` | VERIFIED | 6 tests; covers double-dispose idempotency, `Value` throws after dispose |
| `tests/Oragon.ElasticPool.Tests/Pool/FactoryFailureTests.cs` | VERIFIED | 3 tests; `FailureKind.FactoryThrew` used; counter rollback + MetricCollector |
| `tests/Oragon.ElasticPool.Tests/Pool/BeforeUseUnhealthyTests.cs` | VERIFIED | 5 tests; `FailureKind.BeforeUseUnhealthy` used; NSubstitute policy verification |
| `tests/Oragon.ElasticPool.Tests/Pool/WaitBehaviorTests.cs` | VERIFIED | 3 tests; `WaitBehavior.Throw` + `WaitBehavior.Wait` + pre-canceled CT |
| `tests/Oragon.ElasticPool.Tests/Pool/WarmupAndBoundsTests.cs` | VERIFIED | 5 tests; `ReadyAsync` completeness + cancel during warmup + InitialSize=0 |
| `tests/Oragon.ElasticPool.Tests/Pool/DisposeDrainTests.cs` | VERIFIED | 5 tests; Release invoked per idle entry, `ObjectDisposedException` after dispose |
| `tests/Oragon.ElasticPool.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs` | VERIFIED | 6 tests; `GetRequiredKeyedService` + multi-pool coexistence + `pool.name` tag |
| `tests/Oragon.ElasticPool.Tests/Telemetry/MeterAndCounterTests.cs` | VERIFIED | 4 tests; `MetricCollector<long>` + raw `MeterListener` OTel-listenability + fallback without `AddMetrics()` |
| `tests/Oragon.ElasticPool.Tests/TimeProvider/TimeProviderInjectionTests.cs` | VERIFIED | 2 tests; `FakeTimeProvider` injection via `.WithTimeProvider()` |
| `tests/Oragon.ElasticPool.Stress/PingPongStressTest.cs` | VERIFIED | 61 lines; 256 threads × 40 iterations; 25s logical watchdog + 5s per-call timeout; `[Fact(Timeout=60_000)]`; passes on all 3 TFMs in ~300ms |
| `.github/workflows/build.yml` | VERIFIED | Multi-TFM matrix (net8/net9/net10); `Core.Tests`-only (no Stress reference); coverlet.console + reportgenerator + 90% line gate on `+Oragon.ElasticPool`; coverage artifact upload |
| `global.json` | VERIFIED | SDK 10.0.100 / `latestFeature`; `"runner": "Microsoft.Testing.Platform"` |
| `Directory.Build.props` | VERIFIED | `TreatWarningsAsErrors=true`, `ContinuousIntegrationBuild`, `Nullable=enable`, SourceLink, snupkg, deterministic build |
| `Directory.Packages.props` | VERIFIED | `ManagePackageVersionsCentrally=true`; AwesomeAssertions 9.4.0 (NOT FluentAssertions); xunit.v3 3.2.2; NSubstitute 5.3.0; FakeTimeProvider 10.5.0 |

---

## Key Link Verification

| From | To | Via | Status | Evidence |
|------|----|-----|--------|----------|
| `ElasticPool.cs` | `TelemetryEmitter.cs` | `_telemetry.OnAcquire()` / `_telemetry.OnFactoryFailure()` | WIRED | Lines 94, 130 in `ElasticPool.cs` call telemetry on every Acquire and factory failure |
| `ElasticPool.cs` | `Channel<TCS>` waiter | `TryHandoff()` in `ReturnSync` | WIRED | Lines 216, 251–259 — direct-handoff waiter is the return path |
| `ElasticPool.cs` | `_options.FailurePolicy.HandleAsync(...)` | On factory throw + BeforeUse unhealthy | WIRED | Lines 132 (factory throw) and 192 (BeforeUse unhealthy) |
| `ServiceCollectionExtensions.cs` | `ElasticObjectPoolFactory.Build<T>(sp, lifetimeCt)` | DI factory lambda | WIRED | Line 35 |
| `TelemetryTests` | `PoolMeterNames.MeterName` | `MetricCollector<long>(meterFactory, "Oragon.ElasticPool", ...)` | WIRED | `MeterAndCounterTests.cs` line 22 |
| `PingPongStressTest` | `IElasticPool<Resource>` via DI | `services.AddElasticPool<Resource>("stress", ...)` + `GetRequiredKeyedService` | WIRED | Lines 29–33 of `PingPongStressTest.cs` |
| `build.yml` | `Core.Tests` only | `dotnet test --project tests/Oragon.ElasticPool.Tests/...csproj` | WIRED | Line 47 of `build.yml`; negative grep for `Oragon.ElasticPool.Stress` returns empty |
| `build.yml` | coverage gate 90% | `coverlet.console` + `reportgenerator` + `awk` compare | WIRED | Lines 59–101 of `build.yml` |

---

## Data-Flow Trace (Level 4)

Not applicable for this phase. No UI components or data-fetching artifacts — the codebase is a library with in-process state. Observability flows are verified via MetricCollector tests (live counter increments verified against real pool operations).

---

## Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Unit tests: 70 tests × 3 TFMs = 210 invocations, 0 failed | `dotnet test --project tests/Oragon.ElasticPool.Tests/... --configuration Release --no-build` | `total: 210, failed: 0, succeeded: 210` | PASS |
| Stress test: 256 threads × 40 iterations, 3 TFMs, ~300ms each | `dotnet test --project tests/Oragon.ElasticPool.Stress/... --configuration Release --no-build` | `total: 3, failed: 0, succeeded: 3` | PASS |
| Build: 0 errors, 0 compile warnings | `dotnet build Oragon.ElasticPool.sln -c Release` | `4 projects, 0 errors, 6 warnings` (all SourceLink "no remote" — expected locally, resolved in CI) | PASS |
| Coverage >= 90% on `Oragon.ElasticPool` | `reportgenerator` on stored `TestResults/final/coverage.cobertura.xml` | `Line coverage: 92.8% (286/308)`, `Branch: 88.3%`, `Method: 98.5%` | PASS |
| No FluentAssertions imports in tests | `grep -r 'using FluentAssertions' tests/ --include="*.cs"` | No output | PASS |
| Stress project NOT in CI | `grep 'Oragon.ElasticPool.Stress' .github/workflows/build.yml` | No output (exit 1 = not found) | PASS |

---

## Requirements Coverage

| Requirement | Description | Status | Evidence |
|-------------|-------------|--------|----------|
| API-01 | `IElasticPool<T>` with sync `Acquire()` + async `AcquireAsync(CancellationToken)` returning `ValueTask<IPoolItem<T>>` | SATISFIED | `IElasticPool.cs`; tested by `AcquireAndReturnTests` |
| API-02 | `IPoolItem<T>` disposable with `.Value` (NOT `.Object`), idempotent dispose, double-dispose detection | SATISFIED | `IPoolItem.cs` exposes `.Value`; `PoolItem.cs` uses `Interlocked.Exchange(ref _disposed, 1)`; tested by `PoolItemDisposeTests` |
| API-03 | Builder `.Build()` validates config, throws on missing `Factory` or invalid bounds | SATISFIED | `ElasticPoolBuilder.Build()` lines 45–55; tested by `BuilderValidationTests` |
| HOOK-01 | `Factory((IServiceProvider, CancellationToken) → ValueTask<T>)` required, runs outside locks | SATISFIED | `FactoryDelegate<T>` in `HookDelegates.cs`; factory called outside CAS lock in `AcquireAsync` |
| HOOK-02 | `BeforeUse((T, CancellationToken) → ValueTask<PoolState>)` optional; failure triggers policy | SATISFIED | `BeforeUseDelegate<T>` in `HookDelegates.cs`; `PrepareForUseAsync` invokes it; tested by `BeforeUseUnhealthyTests` |
| HOOK-03 | `Check((T, CancellationToken) → ValueTask<PoolState>)` signature present (consumed by Phase 2 sweeper) | SATISFIED (signature locked) | `CheckDelegate<T>` in `HookDelegates.cs`; builder exposes `.Check(...)`. Background sweeper consumption is Phase 2 scope — deferred correctly. |
| HOOK-04 | `AfterUse((T, CancellationToken) → ValueTask<PoolState>)` optional, default no-op | SATISFIED | `AfterUseDelegate<T>` in `HookDelegates.cs`; `ReturnAsync` invokes it when configured; default is no-op (`_options.AfterUse is null`). Per CONTEXT.md decision, activation is v2. |
| HOOK-05 | `Release((T, CancellationToken) → ValueTask)` optional, for cleanup | SATISFIED | `ReleaseDelegate<T>` in `HookDelegates.cs`; invoked in `DisposeAsync` drain + `TryReleaseFireAndForget`; tested by `DisposeDrainTests.DisposeAsync_InvokesReleaseOnEachIdleEntry` |
| BOUND-01 | `MinSize`/`MaxSize`/`InitialSize` with `0 ≤ Min ≤ Initial ≤ Max` validation | SATISFIED | Builder validates all four conditions; tested by `BuilderValidationTests.Build_WithInvalidBounds_Throws` theory matrix |
| BOUND-02 | Eager warm-up awaitable to `InitialSize`, cancellable | SATISFIED | `WarmupAsync` + `ReadyAsync()` returns `WarmupTask`; tested by `WarmupAndBoundsTests` |
| FAIL-01 | `IItemFailurePolicy<T>` invoked on unhealthy hook or factory failure | SATISFIED | `IItemFailurePolicy<T>` interface in `Abstractions`; called at lines 132 and 192 of `ElasticPool.cs`; tested with NSubstitute in `FactoryFailureTests` and `BeforeUseUnhealthyTests` |
| FAIL-02 | `DiscardAndReplaceFailurePolicy<T>` built-in default | SATISFIED | `Policies/DiscardAndReplaceFailurePolicy.cs`; always returns `FailureDecision.Discard`; default in builder when `_failurePolicy` is null |
| TELEM-01 | `Meter "Oragon.ElasticPool"` via `IMeterFactory` with counters + `pool.name` tag | SATISFIED | `TelemetryEmitter.cs`; `PoolMeterNames` constants; tested by `MeterAndCounterTests` with `MetricCollector<long>` and manual `MeterListener` |
| DI-01 | `services.AddElasticPool<T>(name, configure)` with named options, keyed singletons, non-keyed fallback | SATISFIED | `ServiceCollectionExtensions.AddElasticPool<T>`; `TryAddKeyedSingleton` + `TryAddSingleton` for `string.Empty`; tested by `ServiceCollectionExtensionsTests` (6 tests) |
| QUAL-01 | `CancellationToken` propagated end-to-end in all hook async paths, waiter cancellation | SATISFIED | All 5 hook delegates accept `CancellationToken`; `AcquireAsync` links caller CT with `_lifetimeCts`; waiter canceled via `ct.Register`; tested by `WaitBehaviorTests` |
| QUAL-02 | `IDisposable` + `IAsyncDisposable` with drain semantics | SATISFIED | Both interfaces implemented; sync `Dispose()` blocks on `DisposeAsync()`; drain verified by `DisposeDrainTests` |

**16/16 Phase 1 requirements SATISFIED**

---

## CONTEXT.md Locked Decisions Verification

| Decision | Status | Evidence |
|----------|--------|----------|
| `IPoolItem<T>.Value` (not `.Object`) | HONORED | `IPoolItem.cs` line 10: `T Value { get; }` |
| `WaitBehavior` configurable via `.WhenExhausted(...)` | HONORED | `WaitBehavior.cs` enum; `ElasticPoolBuilder.WhenExhausted()`; tested by `WaitBehaviorTests` |
| Builder requires only `Factory`; `.Build()` throws if absent | HONORED | `ElasticPoolBuilder.Build()` line 45–46 |
| xUnit v3 + AwesomeAssertions + NSubstitute (NOT FluentAssertions/Shouldly) | HONORED | `Directory.Packages.props` pins `AwesomeAssertions 9.4.0`; grep for `using FluentAssertions` returns nothing |
| Stress in separate project, excluded from CI default | HONORED | `Oragon.ElasticPool.Stress` is in the solution but NOT in `build.yml`; verified by negative grep |
| 90% Core coverage gate in CI | HONORED | `build.yml` lines 74–101: coverlet.console + reportgenerator + awk 90% check restricted to `+Oragon.ElasticPool` |
| `PublicApiAnalyzers` active with baselines | HONORED | `Core.csproj` references `Microsoft.CodeAnalysis.PublicApiAnalyzers` (PrivateAssets=all); `PublicAPI.Shipped.txt` = `#nullable enable`; `PublicAPI.Unshipped.txt` = 75 declarations; RS0016 silent |
| `CancellationToken` in ALL hook signatures | HONORED | All 5 delegates in `HookDelegates.cs` accept `CancellationToken` |
| `ValueTask<T>` returns on all hooks | HONORED | Factory→`ValueTask<T>`, BeforeUse/Check/AfterUse→`ValueTask<PoolState>`, Release→`ValueTask` |
| `IDisposable` + `IAsyncDisposable` both | HONORED | `IElasticPool<T>` declares both; `ElasticPool<T>` implements both; `IPoolItem<T>` declares both |
| `Meter` via `IMeterFactory` with fallback | HONORED | `TelemetryEmitter.cs` lines 16–25: `GetService<IMeterFactory>()` with `new Meter` fallback; `_ownsMeter` flag tracks ownership |
| Single `object`-based lock (no `System.Threading.Lock`) | HONORED | Engine is lock-free (Interlocked + Channel); no `lock` statements present; no `System.Threading.Lock` usage in source |

---

## Anti-Patterns Found

No blockers. Notes:

| File | Pattern | Severity | Impact |
|------|---------|---------|--------|
| `ServiceCollectionExtensions.cs` | `TryGetHostApplicationStoppingToken` reflection probe uses `sp.GetServices<object>().FirstOrDefault(...)` — fragile heuristic that could fail to find a host lifetime service | INFO | Acknowledged in CONTEXT.md (T-01-15 Accept). Pool falls back to `CancellationToken.None` (safe). Low risk in Phase 1 scope. |
| `Telemetry/PoolDiagnosticsLog` | `LoggerMessage.g.cs` `IsEnabled` short-circuit branches are uncovered (40% line coverage on that class) | INFO | Source-gen artifact; these branches fire only under log-level filtering. Not a stub. No `[ExcludeFromCodeCoverage]` needed — this is a generator limitation, not a logic gap. Total assembly coverage remains 92.8%, above the 90% gate. |
| `Internals/PoolItem.cs` | Finalizer path tested loosely via `GC.Collect/WaitForPendingFinalizers` handshake (non-deterministic per PITFALLS Pitfall 4) | INFO | Accepted pattern for finalizer testing. Not a stub — the finalizer calls `ReturnFromFinalizer` which is the legitimate recovery path. |

---

## Human Verification Required

None. All must-haves are verifiable programmatically and were verified above.

---

## Gaps Summary

No gaps. All 6 ROADMAP success criteria are achieved, all 16 Phase 1 requirements are satisfied, all CONTEXT.md locked decisions are honored in code, all tests pass (210 unit + 3 stress across 3 TFMs), and the 92.8% line coverage is confirmed by the stored coverage artifact.

**Phase 1 goal is fully achieved. The fixed-size pool engine with all non-retrofittable architectural decisions is locked in correctly and verified by automated tests.**

---

_Verified: 2026-05-02T04:40:00Z_
_Verifier: Claude (gsd-verifier)_
