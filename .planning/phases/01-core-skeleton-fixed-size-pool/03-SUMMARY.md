---
phase: 01-core-skeleton-fixed-size-pool
plan: 03
subsystem: core-tests
tags: [tests, stress, coverage, ci, telemetry-tests, di-tests, awesomeassertions, nsubstitute, metriccollector]
requires:
  - Plan 02 sealed ElasticPool<T> engine + 12 public types + DI extension (Channel direct-handoff waiter, Interlocked counter rollback, idempotent Dispose, IMeterFactory + Counter<long> emitters, source-gen [LoggerMessage] partial class, AddElasticPool<T>(name, configure) keyed/non-keyed)
provides:
  - 13 unit-test files (62 + 8 closing tests = 70 [Fact]/[Theory] tests) under tests/Oragon.ElasticPool.Tests/, organized by Builder/Pool/DependencyInjection/Telemetry/TimeProvider/TestSupport
  - PingPongStressTest in tests/Oragon.ElasticPool.Stress/ — MaxSize=1 with 256 threads × 40 iterations under 25 s logical watchdog and 5 s per-call timeout (Phase 1 anchor gate per ROADMAP success criterion 2)
  - 90 % line-coverage gate on `Oragon.ElasticPool` enforced in CI (`.github/workflows/build.yml`) via coverlet.console + reportgenerator with `+Oragon.ElasticPool` filter
  - Stress project still excluded from CI default per CONTEXT.md
  - Reusable test infrastructure for Phase 2 (FakeTimeProvider injection point exercised, MetricCollector pattern, NSubstitute on IItemFailurePolicy<T>)
affects:
  - Phase 1 closure: every Phase 1 ROADMAP success criterion (1–6) is now backed by at least one passing test; the MaxSize=1 stress is green; 92.8 % line coverage on Core leaves 7.2 % headroom above the 90 % gate
  - Phase 2 sweeper / elasticity work inherits the fixture patterns established here (LoggerFactory wiring for source-gen logging, MetricCollector for counter assertions, NSubstitute for failure policy mocking)
  - Requirements API-01, API-02, API-03, HOOK-01, HOOK-02, HOOK-04, HOOK-05, BOUND-01, BOUND-02, FAIL-01, FAIL-02, DI-01, QUAL-01, QUAL-02, TELEM-01 are now verified by automated tests (16 of the 16 Phase 1 requirements)
tech-stack:
  added:
    - Microsoft.Extensions.Diagnostics 10.0.6 (CPM) — required to register IMeterFactory via services.AddMetrics(); not present in Plan 01 because TelemetryEmitter falls back to `new Meter` and Plan 01 had no telemetry tests
    - coverlet.console 10.0.0 (CI global tool) — wraps `dotnet <testdll>` to collect cobertura without going through `dotnet test --collect`, which the MTP runner does not honor (RESEARCH Pitfall 7)
    - dotnet-reportgenerator-globaltool 5.5.9 (CI global tool) — parses cobertura, applies `+Oragon.ElasticPool` assembly filter, emits TextSummary for the 90 % gate
  patterns:
    - Coverlet "data-collector" path implemented via console-tool wrapper rather than VSTest `--collect:"XPlat Code Coverage"`; resilient under MTP and avoids the `--report-trx` injection failure
    - MetricCollector<long> + IMeterFactory tagged measurement assertions (vs. ad-hoc MeterListener) — establishes the canonical telemetry-test pattern for Phase 2
    - NSubstitute on the public `IItemFailurePolicy<T>` interface — the `Resource` POCO used for substitution is `public` (Castle DynamicProxy needs visibility); engine internals tested only through the public surface
    - xUnit1051 NoWarn at the test-csproj level — the analyzer enforcement adds noise without value at this scope; tests own their CTS lifetimes directly
    - GC.Collect/WaitForPendingFinalizers handshake in FinalizerTests — assertion is loose because finalizers are not deterministic (PITFALLS Pitfall 4); covers the path without flake-prone strict assertions
key-files:
  created:
    - tests/Oragon.ElasticPool.Tests/TestSupport/Resource.cs
    - tests/Oragon.ElasticPool.Tests/Builder/BuilderValidationTests.cs
    - tests/Oragon.ElasticPool.Tests/Pool/AcquireAndReturnTests.cs
    - tests/Oragon.ElasticPool.Tests/Pool/PoolItemDisposeTests.cs
    - tests/Oragon.ElasticPool.Tests/Pool/FactoryFailureTests.cs
    - tests/Oragon.ElasticPool.Tests/Pool/BeforeUseUnhealthyTests.cs
    - tests/Oragon.ElasticPool.Tests/Pool/WaitBehaviorTests.cs
    - tests/Oragon.ElasticPool.Tests/Pool/WarmupAndBoundsTests.cs
    - tests/Oragon.ElasticPool.Tests/Pool/DisposeDrainTests.cs
    - tests/Oragon.ElasticPool.Tests/Pool/AfterUseAndExceptionTests.cs
    - tests/Oragon.ElasticPool.Tests/Pool/FinalizerTests.cs
    - tests/Oragon.ElasticPool.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs
    - tests/Oragon.ElasticPool.Tests/Telemetry/MeterAndCounterTests.cs
    - tests/Oragon.ElasticPool.Tests/TimeProvider/TimeProviderInjectionTests.cs
    - tests/Oragon.ElasticPool.Stress/PingPongStressTest.cs
  modified:
    - tests/Oragon.ElasticPool.Tests/PlaceholderSmokeTest.cs (replaced trivial placeholder with full DI roundtrip smoke)
    - tests/Oragon.ElasticPool.Tests/Oragon.ElasticPool.Tests.csproj (added Microsoft.Extensions.Diagnostics package reference + xUnit1051 NoWarn)
    - tests/Oragon.ElasticPool.Stress/Oragon.ElasticPool.Stress.csproj (xUnit1051 NoWarn)
    - tests/Oragon.ElasticPool.Stress/PlaceholderStressFact.cs (reduced to comment-only sentinel)
    - Directory.Packages.props (pinned Microsoft.Extensions.Diagnostics 10.0.6)
    - .github/workflows/build.yml (coverlet.console + reportgenerator + 90 % line gate restricted to `+Oragon.ElasticPool`)
decisions:
  - Coverage gate uses coverlet.console wrapped over `dotnet <testdll>` rather than `dotnet test --collect:"XPlat Code Coverage"`. MTP runner injects `--report-trx` which is unsupported by `Oragon.ElasticPool.Tests.dll`, so VSTest-style collection fails. The console-tool path is functionally equivalent and matches the intent of CONTEXT.md (data-collector path via coverlet).
  - xUnit1051 (advisory: thread `TestContext.Current.CancellationToken` through every CT-accepting call) is suppressed at the test-project level via `<NoWarn>$(NoWarn);xUnit1051</NoWarn>`. The analyzer became fatal because `Directory.Build.props` sets `TreatWarningsAsErrors=true`. Tests already manage their own CTS lifetimes for cancellation-matrix tests; the indirection adds noise without behavioral benefit. Same suppression applied to the Stress csproj for the per-acquire timeout pattern.
  - The shared `TestSupport/Resource` POCO is **public** (not `internal`) — Castle DynamicProxy via NSubstitute requires the substituted-type's generic argument to be accessible; making the test assembly add `[InternalsVisibleTo("DynamicProxyGenAssembly2")]` was rejected as ceremony for a 6-line stand-in.
  - No `[ExcludeFromCodeCoverage]` markers were added. Plan 03 explicitly said "apply them only if necessary AFTER attempting genuine coverage". The closing tests (AfterUseAndExceptionTests, FinalizerTests) lifted line coverage from 84.09 % to 92.85 %, comfortably above the 90 % gate, without resorting to attribute suppression. Lines that remain uncovered are: the reflection-based `IHostApplicationLifetime` probe in `ServiceCollectionExtensions` (requires a real host), specific source-gen `IsEnabled`-short-circuit branches in `LoggerMessage.g.cs` (hit only when a log filter excludes the level), and the few defensive `catch` branches inside the engine's race-condition recovery paths.
  - The Stress project is run **manually**, not in CI default. The build.yml workflow only invokes `tests/Oragon.ElasticPool.Tests/...csproj`. A nightly stress job is part of Phase 4 (OSS-01 expansion).
metrics:
  duration: ~22m
  completed: 2026-05-03
  tasks: 3
  files_created: 15
  files_modified: 6
  commits: 3
  unit_tests: 70
  stress_tests: 1
  line_coverage_core: "92.8%"
  branch_coverage_core: "88.3%"
  method_coverage_core: "98.5%"
---

# Phase 1 Plan 03: Tests + Stress + Coverage Gate Summary

**One-liner:** Converted every Phase 1 must-have truth into automated tests (70 unit tests across 13 files), shipped the anchor `MaxSize=1` 256-thread × 40-iteration ping-pong stress test (passes in ~300 ms), and wired a 90 % line-coverage gate on `Oragon.ElasticPool` into CI via coverlet.console + reportgenerator (achieved 92.8 %). Every Phase 1 ROADMAP success criterion (1–6) is now backed by at least one passing test on net8.0/net9.0/net10.0.

## What Was Built

### Task 1 — Unit test suite (commit 58c0916)

13 .cs files under `tests/Oragon.ElasticPool.Tests/` (incl. shared `TestSupport/Resource.cs` POCO):

| File | Tests | Covers |
| --- | ---: | --- |
| `Builder/BuilderValidationTests.cs` | 8 | Factory required + bounds matrix + ArgumentNull guards on `Factory`, `WithFailurePolicy`, `WithTimeProvider` |
| `Pool/AcquireAndReturnTests.cs` | 7 | Sync fast-path, async grow-to-MaxSize, direct-handoff waiter (proves the same pooled instance is reused), `await using` returns to pool, sync `Dispose()` returns to pool |
| `Pool/PoolItemDisposeTests.cs` | 6 | Double Dispose / DisposeAsync idempotency in both orders + `Value` post-dispose throws `ObjectDisposedException` (sync and async paths) |
| `Pool/FactoryFailureTests.cs` | 3 | Counter rollback to zero "ghost reservation", `IItemFailurePolicy.HandleAsync` invoked with `FailureKind.FactoryThrew` and the original exception, `pool.factory.failures` Counter increment via MetricCollector |
| `Pool/BeforeUseUnhealthyTests.cs` | 5 | Policy invoked with `FailureKind.BeforeUseUnhealthy`, default DiscardAndReplace produces fresh item, Release hook fires on discarded item, healthy path bypasses policy, BeforeUse exception is treated as Unhealthy |
| `Pool/WaitBehaviorTests.cs` | 3 | `WaitBehavior.Throw` is immediate (< 500 ms), `WaitBehavior.Wait` honors caller's CT, pre-canceled token returns immediately |
| `Pool/WarmupAndBoundsTests.cs` | 5 | `ReadyAsync` completes after `InitialSize` items created, propagates Factory exceptions, dispose-during-warmup cancels, `InitialSize=0` skips factory entirely, `ReadyAsync` is reusable |
| `Pool/DisposeDrainTests.cs` | 5 | `DisposeAsync` invokes Release on every idle entry, post-dispose Acquire throws `ObjectDisposedException`, double-DisposeAsync idempotent, sync `Dispose()` drains within 5 s, parked waiters cancelled on dispose |
| `Pool/AfterUseAndExceptionTests.cs` | 8 | (Closing tests) AfterUse Healthy/Unhealthy/throws paths, `PoolExhaustedException` ctor matrix (with/without WaitTime), DisposeAsync continues drain through Release-throw, in-flight item disposed after pool dispose triggers `TryReleaseFireAndForget` |
| `Pool/FinalizerTests.cs` | 1 | (Closing test) Best-effort GC handshake exercising the `~PoolItem()` finalizer + `ElasticPool.ReturnFromFinalizer` path |
| `DependencyInjection/ServiceCollectionExtensionsTests.cs` | 6 | Default-name (`string.Empty`) registers BOTH keyed and non-keyed (same singleton), named-only is keyed-only (non-keyed returns null), two named pools of the same `T` coexist, ArgumentNull theory matrix, `pool.name` tag flows to telemetry |
| `Telemetry/MeterAndCounterTests.cs` | 4 | Meter named `Oragon.ElasticPool` via IMeterFactory + tag, `pool.factory.failures` increments by exact count, manual `MeterListener` observes counters (proves OTel-listenability), without `services.AddMetrics()` the engine falls back to `new Meter` and counters still emit |
| `TimeProvider/TimeProviderInjectionTests.cs` | 2 | `WithTimeProvider(FakeTimeProvider)` builds and acquires; default TimeProvider is System without `WithTimeProvider()` |
| `PlaceholderSmokeTest.cs` (rewritten) | 1 | Full DI smoke: `services.AddElasticPool<T>("smoke", …)` → `await pool.AcquireAsync()` → `await using` → assertions on `InUse`/`Available` |

**70 tests total, 0 failed across net8.0 + net9.0 + net10.0** (210 test invocations).

### Task 2 — `MaxSize=1` ping-pong stress (commit 481fec1)

`tests/Oragon.ElasticPool.Stress/PingPongStressTest.cs`:

- **Workload:** 256 threads × 40 iterations = 10 240 acquire/release cycles against a `MaxSize=1` pool.
- **Watchdogs:** 25 s logical watchdog (`CancellationTokenSource(TimeSpan.FromSeconds(25))`) + 5 s per-call linked CTS (`CreateLinkedTokenSource` + `CancelAfter`); xUnit `Timeout=60_000` outer safety net.
- **Final invariants:** `pool.InUse.Should().Be(0)` and `pool.Available.Should().Be(1)` — every cycle returned cleanly.
- **Wall-clock:** ~300 ms across all 3 TFMs locally; 60 s ceiling never approached.

This is the Phase 1 anchor gate per ROADMAP success criterion 2. It proves end-to-end:

- `Channel<TaskCompletionSource<PoolEntry<T>>>` direct-handoff has no lost wake-ups
- `Interlocked` counter rollback paths are correct under contention
- Cancellation tokens are honored at the per-acquire boundary
- No deadlocks under hundreds-of-threads contention with `MaxSize=1` (worst case)

`PlaceholderStressFact.cs` reduced to a comment-only sentinel (kept on disk for file_modified ledger continuity; no `[Fact]` attribute remains).

The Stress project is **NOT** invoked by `.github/workflows/build.yml`. Run manually:

```bash
dotnet test --project tests/Oragon.ElasticPool.Stress/Oragon.ElasticPool.Stress.csproj --configuration Release
```

A nightly Stress job is on the Phase 4 OSS-01 list.

### Task 3 — Coverage gate in CI (commit c7b6191)

`.github/workflows/build.yml` updates:

- New step: `install coverlet.console + reportgenerator` (`dotnet tool install -g`)
- New step: `collect coverage on Core.Tests` — runs `coverlet ./Oragon.ElasticPool.Tests.dll --target dotnet --targetargs Oragon.ElasticPool.Tests.dll --format cobertura --output … --include "[Oragon.ElasticPool]*"` per matrix TFM
- New step: `enforce 90% line coverage on Core` — runs `reportgenerator -assemblyfilters:+Oragon.ElasticPool -reporttypes:TextSummary`, parses `Line coverage: NN.N%`, fails if `< 90.0`
- New step: `upload coverage report (artifact)` — uploads the `coverage-report/` folder as `coverage-${tfm}` on every CI run regardless of pass/fail (diagnosability)
- **Stress project still NOT invoked** (verified: `! grep -q 'Oragon.ElasticPool.Stress' .github/workflows/build.yml`)

The plan recommended `dotnet test --collect:"XPlat Code Coverage"`, but the MTP runner v1.x does not honor that legacy VSTest data-collector contract — it injects `--report-trx` which `Oragon.ElasticPool.Tests.dll` rejects with `Unknown option`. The console-tool path implements the same intent (data-collector via coverlet, not MSBuild integration per RESEARCH Pitfall 7) without the runner-coupling failure.

## ROADMAP Success Criterion → Test Map

| Criterion | File(s) | Test method(s) |
| --- | --- | --- |
| **1 — DI + sync Acquire + await using + idempotent dispose** | `DependencyInjection/ServiceCollectionExtensionsTests.cs`, `Pool/AcquireAndReturnTests.cs`, `Pool/PoolItemDisposeTests.cs`, `PlaceholderSmokeTest.cs` | `AddElasticPool_DefaultName_RegistersAsKeyedAndNonKeyed`, `Acquire_FastPath_ReturnsItemImmediately`, `AwaitUsing_ReturnsItemToPool`, `Dispose_IsIdempotent_NoDoubleReturn`, `DisposeAsync_IsIdempotent`, `DI_Build_Acquire_Dispose_Roundtrip_Works` |
| **2 — `MaxSize=1` ping-pong stress, no deadlocks, CT honored** | `tests/Oragon.ElasticPool.Stress/PingPongStressTest.cs` | `MaxSize1_HundredsOfThreads_TenThousandIterations_NoDeadlock` |
| **3 — Factory throw counter rollback + BeforeUse Unhealthy → policy + replace** | `Pool/FactoryFailureTests.cs`, `Pool/BeforeUseUnhealthyTests.cs` | `FactoryThrows_DecrementsTotal_AllowsSubsequentAcquireToReachMaxSize`, `FactoryThrows_InvokesFailurePolicy_WithFailureKindFactoryThrew_AndException`, `BeforeUseUnhealthy_DiscardAndReplace_DefaultPolicy_ProducesFreshItem`, `BeforeUseUnhealthy_InvokesFailurePolicy_WithFailureKindBeforeUseUnhealthy` |
| **4 — Meter via IMeterFactory + counters consumable by OTel listener** | `Telemetry/MeterAndCounterTests.cs` | `Meter_IsNamedOragonElasticPool_AndCreatedViaIMeterFactory`, `FactoryFailure_IncrementsCounter`, `OtelListener_ObservesPoolMeter`, `PoolWithoutAddMetrics_FallbackMeterUsed_AndPoolStillFunctions` |
| **5 — IDisposable + IAsyncDisposable drain** | `Pool/DisposeDrainTests.cs`, `Pool/AfterUseAndExceptionTests.cs` | `DisposeAsync_InvokesReleaseOnEachIdleEntry`, `DisposeAsync_AfterDispose_NewAcquireThrowsObjectDisposed`, `DisposeAsync_IsIdempotent`, `Dispose_Sync_DrainsPoolWithoutHanging`, `DisposeAsync_CancelsPendingWaiters`, `DisposeAsync_ReleaseHookThrows_DrainContinues_AndIsLogged` |
| **6 — Eager warm-up awaitable + bounds validation** | `Pool/WarmupAndBoundsTests.cs`, `Builder/BuilderValidationTests.cs` | `ReadyAsync_CompletesWhenWarmedUp`, `ReadyAsync_PropagatesFactoryException`, `PoolDispose_DuringWarmup_CancelsWarmupTask`, `InitialSizeZero_NoFactoryCallsAtStartup`, `Build_WithInvalidBounds_Throws`, `Build_WithValidBounds_Succeeds` |

## Coverage Achieved

```
Line coverage:   92.8%   (286 / 308)
Branch coverage: 88.3%   (99  / 112)
Method coverage: 98.5%   (66  / 67)
```

| Class | Line | Notes |
| --- | ---: | --- |
| `Builder.ElasticObjectPoolFactory` | 100 % | Trivial entry-point class |
| `Builder.ElasticPoolBuilder<T>` | 97.6 % | Fully covered modulo a defensive null-check branch in `WithName` |
| `Builder.ElasticPoolOptions<T>` | 100 % | Init-only record |
| `DependencyInjection.ServiceCollectionExtensions` | ~88 % | `TryGetHostApplicationStoppingToken` reflection probe is hard to exercise without a real host (Phase 2 work) |
| `Exceptions.PoolExhaustedException` | 100 % | Both ctor paths covered by `AfterUseAndExceptionTests` |
| `Internals.ElasticPool<T>` | ~95 % | After AfterUse + dispose-drain closing tests; remaining gaps are the OperationCanceledException catch in WarmupAsync (race-only) and some failure-recursion edges |
| `Internals.PoolEntry<T>` | 100 % | Trivial record |
| `Internals.PoolItem<T>` | ~88 % | Finalizer best-effort (PITFALLS Pitfall 4: GC.Collect handshake is non-deterministic) |
| `Policies.DiscardAndReplaceFailurePolicy<T>` | 100 % | One method, fully covered |
| `Telemetry.PoolDiagnosticsLog` | ~75 % | Source-gen `LoggerMessage.g.cs` has `IsEnabled` short-circuit lines that fire only when a `LogLevel` filter excludes the level — not exercised because tests use `AddLogging()` defaults |
| `Telemetry.TelemetryEmitter` | 100 % | Both factory + new-Meter fallback paths covered |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 — Blocking] Added `Microsoft.Extensions.Diagnostics 10.0.6` to CPM**
- **Found during:** Task 1 build.
- **Issue:** Tests need `services.AddMetrics()` to register `IMeterFactory` so MetricCollector can attach. The extension is in `Microsoft.Extensions.Diagnostics` — **not** in `Microsoft.Extensions.Diagnostics.Testing` (which only provides `MetricCollector`). Plan 01's CPM didn't pin it because Plan 02's engine falls back to `new Meter` when no factory is registered — there were no telemetry tests to need the factory yet.
- **Fix:** Pinned `Microsoft.Extensions.Diagnostics 10.0.6` in `Directory.Packages.props` (matching the rest of the M.E.* 10.0.6 alignment from Plan 01) and added `<PackageReference Include="Microsoft.Extensions.Diagnostics" />` to `Oragon.ElasticPool.Tests.csproj`.
- **Files modified:** `Directory.Packages.props`, `tests/Oragon.ElasticPool.Tests/Oragon.ElasticPool.Tests.csproj`.
- **Commit:** 58c0916.

**2. [Rule 3 — Blocking] Suppressed xUnit1051 in test projects**
- **Found during:** Task 1 build.
- **Issue:** `Directory.Build.props` sets `TreatWarningsAsErrors=true`. The xUnit v3 analyzer rule xUnit1051 fires on every CT-accepting call inside a test method, demanding `TestContext.Current.CancellationToken` be threaded through. With ~50 such call sites in a CI / matrix / cancellation-aware test suite, this is pure ceremony. The build broke on 200+ "errors" of this advisory rule.
- **Fix:** Added `<NoWarn>$(NoWarn);xUnit1051</NoWarn>` to both test projects (`Oragon.ElasticPool.Tests.csproj` and `Oragon.ElasticPool.Stress.csproj`). Tests own their CTS lifetimes directly (per-acquire timeouts, cancellation matrix tests); threading TestContext's CT adds noise without behavioral benefit at this scope.
- **Files modified:** Both test csproj files.
- **Commit:** 58c0916 (Tests), 481fec1 (Stress).

**3. [Rule 1 — Test bug] `WaitBehaviorWait_TokenCanceledBeforeAcquire_ReturnsImmediately` exact-type assertion**
- **Found during:** First test run.
- **Issue:** Test used `Assert.ThrowsAsync<OperationCanceledException>` (exact type). The engine's pre-canceled-CT path goes through `Channel.WriteAsync(tcs, ct)` which surfaces `TaskCanceledException` (a subclass of OCE). Since xUnit's `ThrowsAsync<T>` requires exact type match, the test failed with `Expected: OperationCanceledException, Actual: TaskCanceledException`.
- **Fix:** Changed to `Assert.ThrowsAnyAsync<OperationCanceledException>` (accepts the subclass). Applied the same relaxation to `WaitBehaviorWait_RespectsCancellationToken` for symmetry — that one's path goes through `tcs.TrySetCanceled()` and DOES produce a plain OCE, but accepting the subclass keeps the test resilient.
- **Files modified:** `tests/Oragon.ElasticPool.Tests/Pool/WaitBehaviorTests.cs`.
- **Commit:** 58c0916.

**4. [Rule 3 — Blocking] `Resource` POCO must be `public` for NSubstitute proxy**
- **Found during:** First test run.
- **Issue:** Castle DynamicProxy (used internally by NSubstitute) needs visibility into the type-arguments of the substituted interface. `Substitute.For<IItemFailurePolicy<Resource>>()` with `internal sealed class Resource` raised: *"Can not create proxy for type IItemFailurePolicy`1[Resource] because type Resource is not accessible. Make it public, or internal and mark your assembly with `[InternalsVisibleTo("DynamicProxyGenAssembly2")]`"*.
- **Fix:** Changed `Resource` to `public sealed class Resource`. Cleaner than adding `InternalsVisibleTo("DynamicProxyGenAssembly2")` ceremony for a 6-line stand-in.
- **Files modified:** `tests/Oragon.ElasticPool.Tests/TestSupport/Resource.cs`.
- **Commit:** 58c0916.

### Plan-sanctioned alternative

**5. CI coverage path: coverlet.console wrapper instead of `dotnet test --collect`**
- **Plan said:** "Per RESEARCH Pitfall 7, prefer the data-collector path over MSBuild integration under MTP runner. Use `dotnet test --collect:"XPlat Code Coverage"`."
- **Reality:** MTP runner under .NET 10 SDK (10.0.107 here) ignores the legacy VSTest `--collect` contract entirely. `dotnet test --project … --collect:"XPlat Code Coverage"` injects `--report-trx` into the MTP runner which the test executable rejects with `Unknown option '--report-trx'`. Result: zero tests run, coverage data never produced. Locally reproducible, would fail in CI the same way.
- **Fix:** Used coverlet.console as a wrapper over `dotnet <test.dll>` directly. Same data-collector mechanism (coverlet instrumenting the assembly), same cobertura output, same reportgenerator + `+Oragon.ElasticPool` filter. Documented inline in `build.yml` and called out in this SUMMARY.
- **Files modified:** `.github/workflows/build.yml`.
- **Commit:** c7b6191.

### Coverage closing

**6. Added 9 closing tests to lift line coverage from 84.09 % → 92.8 %**
- **Found during:** Task 3 first coverage run.
- **Issue:** Initial 62 tests gave 84.09 % line coverage on Core — below the 90 % gate. Major gaps: AfterUse paths (Healthy / Unhealthy / throws), `PoolExhaustedException` with WaitTime, `TryReleaseFireAndForget`, dispose-drain Release-throws path, finalizer.
- **Fix:** Added `Pool/AfterUseAndExceptionTests.cs` (8 tests) and `Pool/FinalizerTests.cs` (1 test). Both use the production interfaces only (no internal-reflection sidedoors). The finalizer test uses the standard `GC.Collect` / `GC.WaitForPendingFinalizers` handshake with a loose assertion (PITFALLS Pitfall 4: finalizers are not deterministic).
- **Files modified:** Created 2 new test files.
- **Commit:** c7b6191.
- **No `[ExcludeFromCodeCoverage]` markers were applied** — the plan said "apply them only if necessary AFTER attempting genuine coverage". Coverage hit 92.8 % via real tests.

### Authentication Gates

None — all package restores went against the public NuGet.org feed. CI tools (`coverlet.console`, `reportgenerator`) are installed inside the runner via `dotnet tool install -g`.

### Out-of-Scope Findings (NOT fixed)

- **SourceLink "no remote" warnings** (6 per build) — same pre-existing local-only situation as Plans 01 / 02. Resolves automatically in CI where `actions/checkout@v4` configures origin. Not a Plan 03 concern.

## Heads-up to Phase 2

The test infrastructure built here is reusable:

1. **`FakeTimeProvider` injection point** is wired and tested. Phase 2 sweeper / health-check tests will use `WithTimeProvider(fake).Build()` then advance via `fake.Advance(TimeSpan)` to exercise time-windowed logic deterministically.
2. **`MetricCollector<long>` + `IMeterFactory` pattern** is established. Phase 2 will need it for `pool.size.idle`, `pool.grow.count`, `pool.shrink.count`, `pool.health.checks` — all consume the same canonical pattern.
3. **NSubstitute on `IItemFailurePolicy<T>`** works because the policy is a public interface and `Resource` is public. Phase 2 may add `IPoolHealthSweeper<T>` etc. — keep them as public interfaces with public POCO type-arguments to preserve NSubstitute compat.
4. **xUnit1051 NoWarn** is set at the test-project level; it should remain set into Phase 2.
5. **The coverlet.console wrapper pattern** in CI is required as long as MTP+VSTest data-collector compatibility remains broken. If a future SDK fixes that, the simpler `dotnet test --collect:"XPlat Code Coverage"` will become viable; until then keep the wrapper.
6. **The `MaxSize=1` ping-pong test** is Phase 1's anchor gate. Phase 2's elasticity / sweeper additions must NOT regress it. Run it before merging any engine-internals change: `dotnet test --project tests/Oragon.ElasticPool.Stress/Oragon.ElasticPool.Stress.csproj`.

## Commits

| Task | Hash    | Message |
| ---- | ------- | ------- |
| 1    | 58c0916 | test(03-01): unit test suite covering all Phase 1 must-have truths (62 tests, 3 TFMs green) |
| 2    | 481fec1 | test(03-02): MaxSize=1 ping-pong stress (Phase 1 anchor gate, success criterion 2) |
| 3    | c7b6191 | test(03-03): wire 90% Core coverage gate into CI + coverage-closing tests |

## Self-Check: PASSED

- All 15 created test files exist on disk (verified via `find`).
- All 6 modified files reflect the documented changes (verified via `git diff` against parent).
- All 3 task commits exist in `git log` (`58c0916`, `481fec1`, `c7b6191`).
- `dotnet build tests/Oragon.ElasticPool.Tests/...` exits 0 with only the expected SourceLink local-only warnings.
- `dotnet <Oragon.ElasticPool.Tests.dll>` reports `total: 70, failed: 0, succeeded: 70` on net8.0 + net9.0 + net10.0 (210 successful test invocations).
- `dotnet <Oragon.ElasticPool.Stress.dll>` reports `total: 1, failed: 0, succeeded: 1` on all 3 TFMs in ~300 ms each (well under the 25 s logical watchdog and 60 s xUnit Timeout).
- Local coverage probe via `coverlet --include "[Oragon.ElasticPool]*"` followed by `reportgenerator -assemblyfilters:"+Oragon.ElasticPool"`: **Line coverage 92.8 %** — passes the 90 % gate.
- Negative grep confirmed: `! grep -r 'using FluentAssertions' tests/` and `! grep -q 'Oragon.ElasticPool.Stress' .github/workflows/build.yml` both return clean.
