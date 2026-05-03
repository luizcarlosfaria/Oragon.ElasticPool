---
phase: 01-core-skeleton-fixed-size-pool
plan: 03
type: execute
wave: 3
depends_on: [01, 02]
files_modified:
  - tests/Oragon.AdaptivePool.Core.Tests/Builder/BuilderValidationTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/AcquireAndReturnTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/PoolItemDisposeTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/FactoryFailureTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/BeforeUseUnhealthyTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/WaitBehaviorTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/WarmupAndBoundsTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/DisposeDrainTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Telemetry/MeterAndCounterTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/TimeProvider/TimeProviderInjectionTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/PlaceholderSmokeTest.cs
  - tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs
  - tests/Oragon.AdaptivePool.Core.Stress/PlaceholderStressFact.cs
  - .github/workflows/build.yml
autonomous: true
requirements:
  - API-01
  - API-02
  - API-03
  - HOOK-01
  - HOOK-02
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
    - "Every Phase 1 success criterion (1–6 per ROADMAP) has at least one passing automated test"
    - "Builder validation rejects: null Factory, MinSize<0, MaxSize<1, MinSize>MaxSize, InitialSize<MinSize, InitialSize>MaxSize"
    - "Sync Acquire() returns immediately when an idle item exists; throws PoolExhaustedException when none"
    - "Async AcquireAsync wakes a waiter via direct-handoff when an in-flight item returns (no spin, no lost wake-up)"
    - "Double Dispose() and double DisposeAsync() on IPoolItem<T> are no-ops (no double-return — Available stays at 1, not 2)"
    - "Calling IPoolItem<T>.Value after dispose throws ObjectDisposedException"
    - "When Factory throws, _total is correctly rolled back: subsequent AcquireAsync can fill up to MaxSize successfully"
    - "When BeforeUse returns Unhealthy, DiscardAndReplaceFailurePolicy<T> discards the item and a fresh item is produced (verified by sequential Acquire calls returning healthy items)"
    - "WaitBehavior.Throw raises PoolExhaustedException immediately under exhaustion (no wait)"
    - "WaitBehavior.Wait respects the caller's CancellationToken: a cancelled token causes the await to throw OperationCanceledException, the in-flight TCS is removed from the channel"
    - "Eager warm-up to InitialSize is awaitable via pool.ReadyAsync(); after await, Available == InitialSize"
    - "DisposeAsync invokes Release hook on every idle entry; subsequent Acquire throws ObjectDisposedException; double-dispose is idempotent"
    - "services.AddAdaptivePool<T>(name, configure) registers under name; multiple pools of same T coexist by name; default name (string.Empty) is also resolvable as non-keyed"
    - "Meter `Oragon.AdaptivePool` is created via IMeterFactory when registered; emits pool.acquire.count + pool.factory.failures with pool.name tag (verified by MetricCollector<long> snapshot)"
    - "TimeProvider injection point works: pool with FakeTimeProvider stamps PoolEntry.CreatedAt from fake clock (smoke test confirming injection is wired)"
    - "MaxSize=1 ping-pong stress test: 256 concurrent threads × ~40 iterations each (10,000 total) completes within 25s watchdog; every AcquireAsync resolves within 5s per-call timeout; no deadlocks; final InUse=0, Available=1"
    - "Coverage of `Oragon.AdaptivePool.Core` ≥ 90% line coverage (gated in CI for Core.Tests run only — Stress remains opt-in)"
    - "CI workflow .github/workflows/build.yml additionally runs the coverage gate; build fails if Core line coverage < 90%"
  artifacts:
    - path: tests/Oragon.AdaptivePool.Core.Tests/Builder/BuilderValidationTests.cs
      provides: "Tests for Builder.Build() validation: missing Factory, invalid bounds matrix"
      contains: "[Fact]"
    - path: tests/Oragon.AdaptivePool.Core.Tests/Pool/AcquireAndReturnTests.cs
      provides: "Tests for sync Acquire fast-path, async AcquireAsync wait+return, PoolItem.Dispose returns to pool"
      contains: "AcquireAsync"
    - path: tests/Oragon.AdaptivePool.Core.Tests/Pool/PoolItemDisposeTests.cs
      provides: "Tests for double-dispose idempotency, sync vs async dispose paths, Value-after-dispose throws"
      contains: "ObjectDisposedException"
    - path: tests/Oragon.AdaptivePool.Core.Tests/Pool/FactoryFailureTests.cs
      provides: "Tests for Factory throw → _total rollback → subsequent AcquireAsync succeeds up to MaxSize; failure policy invoked with FailureKind.FactoryThrew"
      contains: "FailureKind.FactoryThrew"
    - path: tests/Oragon.AdaptivePool.Core.Tests/Pool/BeforeUseUnhealthyTests.cs
      provides: "Tests for BeforeUse Unhealthy → IItemFailurePolicy.HandleAsync invoked with FailureKind.BeforeUseUnhealthy → item replaced"
      contains: "FailureKind.BeforeUseUnhealthy"
    - path: tests/Oragon.AdaptivePool.Core.Tests/Pool/WaitBehaviorTests.cs
      provides: "Tests for WaitBehavior.Throw immediate exception + WaitBehavior.Wait honors cancellation"
      contains: "WaitBehavior.Throw"
    - path: tests/Oragon.AdaptivePool.Core.Tests/Pool/WarmupAndBoundsTests.cs
      provides: "Tests for ReadyAsync awaitability, Available == InitialSize after warmup, bounds enforcement at runtime"
      contains: "ReadyAsync"
    - path: tests/Oragon.AdaptivePool.Core.Tests/Pool/DisposeDrainTests.cs
      provides: "Tests for DisposeAsync drain semantics: Release invoked on each idle, Acquire throws after dispose, double-dispose idempotent"
      contains: "DisposeAsync"
    - path: tests/Oragon.AdaptivePool.Core.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs
      provides: "Tests for AddAdaptivePool<T>(name, configure): keyed resolution, multiple-pool coexistence, default-name (string.Empty) non-keyed fallback"
      contains: "GetRequiredKeyedService"
    - path: tests/Oragon.AdaptivePool.Core.Tests/Telemetry/MeterAndCounterTests.cs
      provides: "Tests using MetricCollector<long> verifying pool.acquire.count + pool.factory.failures fire with pool.name tag; OTel listener consumability"
      contains: "MetricCollector"
    - path: tests/Oragon.AdaptivePool.Core.Tests/TimeProvider/TimeProviderInjectionTests.cs
      provides: "Test that FakeTimeProvider injection works (pool accepts custom TimeProvider via builder)"
      contains: "FakeTimeProvider"
    - path: tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs
      provides: "MaxSize=1 ping-pong: 256 threads × ~40 iterations, 25s watchdog, 5s per-call timeout"
      min_lines: 50
      contains: "MaxSize=1"
    - path: .github/workflows/build.yml
      provides: "Updated CI: adds coverage collection on Core.Tests, fails build if line coverage < 90%; Stress still excluded"
      contains: "XPlat Code Coverage"
  key_links:
    - from: "tests/Oragon.AdaptivePool.Core.Tests/Telemetry/MeterAndCounterTests.cs"
      to: "src/Oragon.AdaptivePool.Core/Telemetry/PoolMeterNames.cs"
      via: "MetricCollector<long>(meterFactory, \"Oragon.AdaptivePool\", \"pool.acquire.count\")"
      pattern: "Oragon\\.AdaptivePool"
    - from: "tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs"
      to: "src/Oragon.AdaptivePool.Core/Internals/AdaptivePool.cs"
      via: "256 concurrent AcquireAsync calls against MaxSize=1 prove waiter direct-handoff correctness"
      pattern: "AcquireAsync"
    - from: ".github/workflows/build.yml"
      to: "tests/Oragon.AdaptivePool.Core.Tests"
      via: "dotnet test --collect:'XPlat Code Coverage' + reportgenerator threshold check (90% line)"
      pattern: "XPlat Code Coverage"
---

<objective>
Convert every Phase 1 must-have truth into automated tests; add the anchor stress test (MaxSize=1 ping-pong) that proves waiter-queue correctness; verify telemetry via MetricCollector; gate Core line coverage at 90% in CI.

Purpose: Phase 1 success criteria are not "complete" until each truth has a test that fails when the truth is violated. Without this plan, criteria 1–6 are claims, not verified facts. The MaxSize=1 ping-pong stress test specifically is the anchor gate — RESEARCH.md and CONTEXT.md both call it out as the test that proves the engine's foundational concurrency correctness.

Output: A green `dotnet test` for unit tests with ≥90% Core coverage; a green `dotnet test` for the Stress project (invoked manually); a CI workflow that enforces both build success and the coverage gate.
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
@.planning/phases/01-core-skeleton-fixed-size-pool/01-02-SUMMARY.md

<interfaces>
<!-- Plan 02 produced the following test targets: -->

Public types this plan tests against (already exist in Core after Plan 02):
- IAdaptivePool<T>: Acquire(), AcquireAsync(ct), ReadyAsync(), MaxSize, MinSize, Available, InUse, Dispose(), DisposeAsync()
- IPoolItem<T>: Value, Dispose(), DisposeAsync()
- IItemFailurePolicy<T>: HandleAsync(T?, FailureKind, Exception?, CancellationToken)
- AdaptiveObjectPoolFactory.Build<T>(IServiceProvider, CancellationToken)
- AdaptivePoolBuilder<T>: Factory, BeforeUse, Check, AfterUse, Release, WithBounds, WhenExhausted, WithFailurePolicy, WithTimeProvider, Build
- WaitBehavior: Wait, Throw
- PoolExhaustedException: MaxSize, WaitTime
- DiscardAndReplaceFailurePolicy<T>
- ServiceCollectionExtensions.AddAdaptivePool<T>(name, configure)
- PoolMeterNames: MeterName="Oragon.AdaptivePool", AcquireCount="pool.acquire.count", FactoryFailures="pool.factory.failures", PoolNameTag="pool.name"
- All five hook delegates (FactoryDelegate, BeforeUseDelegate, CheckDelegate, AfterUseDelegate, ReleaseDelegate) — each taking CancellationToken and returning ValueTask<...>

Test stack (from Plan 01 Directory.Packages.props):
- xUnit v3 + Microsoft.Testing.Platform — `[Fact]` and `[Theory]`/`[InlineData]`; xUnit v3 `Assert.ThrowsAsync<T>(...)` etc.
- AwesomeAssertions 9.4.0 — `using AwesomeAssertions;` (NOT `FluentAssertions`); same FA v7 API
- NSubstitute 5.3.0 — `Substitute.For<IItemFailurePolicy<MyResource>>()`; `.Received(1).HandleAsync(...)`
- Microsoft.Extensions.Diagnostics.Testing — `MetricCollector<T>` for asserting counter snapshots
- Microsoft.Extensions.TimeProvider.Testing — `FakeTimeProvider`
- Microsoft.Extensions.DependencyInjection — real `ServiceCollection` for DI integration tests

Test classes target the 6 ROADMAP success criteria explicitly:

| Criterion | Tests file | Specific tests |
|-----------|------------|----------------|
| 1: DI + sync Acquire + await using + idempotent dispose | DI tests + AcquireAndReturnTests + PoolItemDisposeTests | `AddAdaptivePool_RegistersAndResolves`, `Acquire_FastPath_ReturnsItem`, `AwaitUsing_ReturnsItemToPool`, `DoubleDispose_IsNoOp` |
| 2: MaxSize=1 ping-pong stress, no deadlocks | PingPongStressTest (Stress project) | `MaxSize1_HundredsOfThreads_TenThousandIterations_NoDeadlock` |
| 3: Factory throw counter rollback + BeforeUse Unhealthy → policy + replace | FactoryFailureTests + BeforeUseUnhealthyTests | `FactoryThrows_DecrementsTotal_PoolStillReachesMaxSize`, `BeforeUseUnhealthy_InvokesPolicy_AndReplacesItem` |
| 4: Meter via IMeterFactory + counters consumable by OTel listener | MeterAndCounterTests | `Acquire_IncrementsCounter_ViaMetricCollector`, `FactoryFailure_IncrementsFailureCounter`, `OtelListener_ObservesMeter` |
| 5: IDisposable + IAsyncDisposable drain | DisposeDrainTests | `DisposeAsync_InvokesReleaseOnIdleEntries`, `DisposeAsync_RejectsSubsequentAcquire`, `Dispose_IsIdempotent` |
| 6: Eager warm-up awaitable, bounds validation | WarmupAndBoundsTests + BuilderValidationTests | `ReadyAsync_CompletesWhenWarmedUp_InitialSizeReached`, `Build_RejectsInvalidBounds_Theory` |

CI integration (Plan 01's `build.yml` is updated):
- Add `--collect:"XPlat Code Coverage"` to the test step
- Add a coverage check step using `reportgenerator` to compute line coverage on Core, fail if < 90%
- Stress remains explicitly excluded from CI default

CONTEXT.md decision: 90% line coverage gate on Core in CI. Per RESEARCH Pitfall 7, prefer the data-collector path over MSBuild integration under MTP runner.
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Unit test suite — every must-have truth covered + delete placeholder</name>
  <files>
    tests/Oragon.AdaptivePool.Core.Tests/Builder/BuilderValidationTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/AcquireAndReturnTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/PoolItemDisposeTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/FactoryFailureTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/BeforeUseUnhealthyTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/WaitBehaviorTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/WarmupAndBoundsTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/DisposeDrainTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Telemetry/MeterAndCounterTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/TimeProvider/TimeProviderInjectionTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/PlaceholderSmokeTest.cs
  </files>
  <action>
Create 11 test files covering every must-have truth listed in this plan. Delete the placeholder smoke test from Plan 01 (or replace its content with one minimal sanity check — the choice below replaces it with `pool builds via DI` to keep at least one always-on smoke test).

Each test file uses `using AwesomeAssertions;` (NOT FluentAssertions). Each test method is `[Fact]` (or `[Theory]` with `[InlineData]`). All async tests are `async Task` returning. Test class names match file names.

Common test helper: define a small reusable `Resource` POCO inside each test file (or move to a shared file `tests/Oragon.AdaptivePool.Core.Tests/TestSupport/Resource.cs` if duplication becomes annoying — the executor can decide, but keep tests self-contained for clarity).

Below: minimum required test methods per file. Add additional edge cases as needed to hit the 90% coverage gate enforced in Task 3.

### 1. `Builder/BuilderValidationTests.cs`
Tests:
- `[Fact] Build_WithoutFactory_ThrowsInvalidOperationException` — builder lacking `.Factory(...)` → assert `Should().Throw<InvalidOperationException>().WithMessage("*Factory*")`
- `[Theory]` `[InlineData(-1, 1, 0)]` `[InlineData(0, 0, 0)]` `[InlineData(2, 1, 1)]` `[InlineData(0, 5, 6)]` `[InlineData(2, 5, 1)]` — `Build_WithInvalidBounds_Throws(int min, int max, int initial)` — should throw either `ArgumentOutOfRangeException` or `InvalidOperationException`
- `[Theory]` `[InlineData(0, 1, 0)]` `[InlineData(1, 1, 1)]` `[InlineData(0, 5, 3)]` `[InlineData(1, 10, 5)]` — `Build_WithValidBounds_Succeeds(int min, int max, int initial)` — pool builds; assert `pool.MaxSize == max`, `pool.MinSize == min`

### 2. `Pool/AcquireAndReturnTests.cs`
Tests:
- `[Fact] Acquire_FastPath_ReturnsItemImmediately` — InitialSize=1; sync `pool.Acquire()` returns IPoolItem with non-null Value; `pool.InUse == 1`, `pool.Available == 0`
- `[Fact] Acquire_NoFreeItem_ThrowsPoolExhausted` — MaxSize=1, take one item, second sync `Acquire()` throws `PoolExhaustedException` (sync NEVER blocks)
- `[Fact] AcquireAsync_FastPath_ReturnsItemImmediately` — InitialSize=1; `await pool.AcquireAsync()` returns wrapper; assert telemetry counter incremented (deferred to MeterAndCounterTests)
- `[Fact] AcquireAsync_GrowsToMaxSize_OnDemand` — MaxSize=3, InitialSize=0; three concurrent AcquireAsync's all complete; pool factory invoked exactly 3 times
- `[Fact] AcquireAsync_WaiterWokenByReturn_DirectHandoff` — MaxSize=1, item taken; second AcquireAsync started; first item disposed; second AcquireAsync completes with the same Resource instance (proves direct-handoff)
- `[Fact] AwaitUsing_ReturnsItemToPool` — `await using (var i = await pool.AcquireAsync()) {}`; after block, Available==1, InUse==0

### 3. `Pool/PoolItemDisposeTests.cs`
Tests:
- `[Fact] Dispose_IsIdempotent_NoDoubleReturn` — acquire one item from MaxSize=1 pool; call `Dispose()` twice; Available stays at 1, NOT 2
- `[Fact] DisposeAsync_IsIdempotent` — same as above with async dispose
- `[Fact] DisposeAsyncAfterDispose_IsNoOp` — call sync Dispose then async DisposeAsync; second call returns ValueTask.CompletedTask without acting
- `[Fact] Value_AfterDispose_ThrowsObjectDisposedException` — acquire, dispose, then access `.Value` → throws

### 4. `Pool/FactoryFailureTests.cs`
Tests:
- `[Fact] FactoryThrows_DecrementsTotal_AllowsSubsequentAcquireToReachMaxSize` — Factory throws on first invocation; assert `await pool.AcquireAsync()` throws; second AcquireAsync (Factory now succeeds via toggle) returns successfully; over MaxSize iterations the pool fills correctly (no "ghost" reservation)
- `[Fact] FactoryThrows_InvokesFailurePolicy_WithFailureKindFactoryThrew_AndException` — pass NSubstitute substitute as failure policy; trigger Factory throw; assert `policy.Received(1).HandleAsync(default, FailureKind.FactoryThrew, Arg.Any<Exception>(), Arg.Any<CancellationToken>())`
- `[Fact] FactoryThrows_IncrementsFactoryFailuresCounter` — verify via MetricCollector that `pool.factory.failures` incremented by exactly 1 per throw

### 5. `Pool/BeforeUseUnhealthyTests.cs`
Tests:
- `[Fact] BeforeUseUnhealthy_InvokesFailurePolicy_WithFailureKindBeforeUseUnhealthy` — NSubstitute policy; first BeforeUse returns Unhealthy then Healthy; assert policy received once with `FailureKind.BeforeUseUnhealthy`
- `[Fact] BeforeUseUnhealthy_DiscardAndReplace_DefaultPolicy_ProducesFreshItem` — use `DiscardAndReplaceFailurePolicy` (default); first item returned by Factory has flag set; BeforeUse returns Unhealthy if flag set; AcquireAsync returns a healthy item from a second factory invocation; verify Factory called twice
- `[Fact] BeforeUseUnhealthy_InvokesReleaseHookOnDiscardedItem` — provide Release hook; trigger Unhealthy; assert Release was invoked on the discarded item

### 6. `Pool/WaitBehaviorTests.cs`
Tests:
- `[Fact] WaitBehaviorThrow_ExhaustedPool_ThrowsPoolExhaustedImmediately` — MaxSize=1 + WhenExhausted=Throw; first acquire OK; second AcquireAsync throws `PoolExhaustedException` immediately (assert via `Stopwatch` < 100ms)
- `[Fact] WaitBehaviorWait_RespectsCancellationToken` — MaxSize=1 + WhenExhausted=Wait (default); first acquire OK; second AcquireAsync with `CancellationToken.WaitForCancellation` token canceled after 100ms throws `OperationCanceledException` whose CancellationToken matches the caller's
- `[Fact] WaitBehaviorWait_TokenCanceledBeforeAcquire_ReturnsImmediately` — pre-canceled token causes immediate `OperationCanceledException`

### 7. `Pool/WarmupAndBoundsTests.cs`
Tests:
- `[Fact] ReadyAsync_CompletesWhenWarmedUp` — InitialSize=3; await `pool.ReadyAsync()`; assert `pool.Available == 3` (factory called 3 times)
- `[Fact] ReadyAsync_PropagatesFactoryException` — InitialSize=2 with Factory always throwing; await ReadyAsync → throws (or its first failing inner task throws)
- `[Fact] PoolDispose_DuringWarmup_CancelsWarmupTask` — InitialSize=10 with slow Factory (await Task.Delay(1000)); dispose pool 50ms in; ReadyAsync throws `OperationCanceledException` or `TaskCanceledException`
- `[Fact] InitialSizeZero_NoFactoryCallsAtStartup` — InitialSize=0, MaxSize=5; assert `pool.Available == 0` and Factory invocation counter == 0

### 8. `Pool/DisposeDrainTests.cs`
Tests:
- `[Fact] DisposeAsync_InvokesReleaseOnEachIdleEntry` — InitialSize=3, configured with Release hook; await ReadyAsync; await DisposeAsync; assert Release invoked exactly 3 times (use NSubstitute or counter)
- `[Fact] DisposeAsync_AfterDispose_NewAcquireThrowsObjectDisposed` — dispose pool; sync Acquire() and async AcquireAsync both throw `ObjectDisposedException`
- `[Fact] DisposeAsync_IsIdempotent` — call DisposeAsync twice; second call is a no-op (no second Release invocation)
- `[Fact] Dispose_Sync_DrainsPoolWithoutHanging` — sync Dispose() on a pool with InitialSize=2 + Release hook; completes within 5s; Release invoked 2x

### 9. `DependencyInjection/ServiceCollectionExtensionsTests.cs`
Tests:
- `[Fact] AddAdaptivePool_DefaultName_RegistersAsKeyedAndNonKeyed` — register with `name: string.Empty`; assert `sp.GetRequiredKeyedService<IAdaptivePool<MyResource>>(string.Empty)` AND `sp.GetRequiredService<IAdaptivePool<MyResource>>()` both return the SAME singleton
- `[Fact] AddAdaptivePool_WithName_RegistersAsKeyedOnly` — register with `name: "primary"`; assert keyed resolution works; non-keyed `GetService<>()` returns null
- `[Fact] AddAdaptivePool_TwoNamedPoolsSameType_BothResolve` — register `"primary"` and `"secondary"` for same T; both resolvable; pools are distinct instances; configurations independent
- `[Fact] AddAdaptivePool_NullArguments_ThrowArgumentNullException` — `[Theory]` covering null `services`, null `name`, null `configure`
- `[Fact] AddAdaptivePool_PoolNameTagFlowsToTelemetry` — register pool name "metrics-test"; trigger Acquire; assert pool.acquire.count snapshot has tag `pool.name=metrics-test`

### 10. `Telemetry/MeterAndCounterTests.cs`
Tests using `MetricCollector<long>` from `Microsoft.Extensions.Diagnostics.Testing`:
- `[Fact] Meter_IsNamedOragonAdaptivePool_AndCreatedViaIMeterFactory` — register `services.AddMetrics()`; build pool; resolve `IMeterFactory`; create `MetricCollector<long>(meterFactory, "Oragon.AdaptivePool", "pool.acquire.count")`; await one AcquireAsync; assert exactly 1 measurement with value 1 and tag `pool.name`
- `[Fact] FactoryFailure_IncrementsCounter` — collector on `pool.factory.failures`; trigger 3 Factory throws; assert 3 measurements
- `[Fact] OtelListener_ObservesPoolMeter` — wire a `MeterListener` directly (not via DI); listener observes `Counter<long>` named `pool.acquire.count` from meter `Oragon.AdaptivePool` — confirms the Meter is OTel-listenable per ROADMAP success criterion 4
- `[Fact] PoolWithoutAddMetrics_FallbackMeterUsed_AndPoolStillFunctions` — bare `ServiceCollection` (no `AddMetrics`); pool builds; AcquireAsync succeeds; counters still emit (TelemetryEmitter falls back to `new Meter`); assert via a manually-attached `MeterListener` filtered by name `Oragon.AdaptivePool`

### 11. `TimeProvider/TimeProviderInjectionTests.cs`
Tests:
- `[Fact] WithTimeProvider_FakeTimeProvider_BuilderAcceptsAndPoolBuilds` — pass `new FakeTimeProvider(...)` via builder.WithTimeProvider; pool builds without error; AcquireAsync returns successfully (smoke that the injection point compiles and is wired all the way through; deeper time-sensitive behavior is Phase 2)

### 12. `PlaceholderSmokeTest.cs` (REPLACE the Plan-01 placeholder content)
Replace the existing trivial test with one always-on integration smoke that exercises the full DI + Acquire + Dispose path:
```csharp
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.DependencyInjection;
using Xunit;

namespace Oragon.AdaptivePool.Core.Tests;

public class PoolSmokeTests
{
    private sealed class Resource { }

    [Fact]
    public async Task DI_Build_Acquire_Dispose_Roundtrip_Works()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddAdaptivePool<Resource>("smoke", b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(minSize: 0, maxSize: 1, initialSize: 0));
        var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<Resource>>("smoke");

        await using (var item = await pool.AcquireAsync())
        {
            item.Value.Should().NotBeNull();
            pool.InUse.Should().Be(1);
        }
        pool.InUse.Should().Be(0);
        pool.Available.Should().Be(1);
    }
}
```
Rename the file to keep the existing path (`PlaceholderSmokeTest.cs`) so Plan 01's file_modified ledger remains correct, OR delete and replace with a new file — executor's choice; either keeps the smoke coverage active.

Build hygiene reminder: every test must `await` returned ValueTask exactly once (PITFALLS Pitfall 1).
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && find tests/Oragon.AdaptivePool.Core.Tests -name '*.cs' -type f | wc -l | awk '{ if ($1 < 12) { print "MISSING-TESTS:" $1; exit 1 } else print "TEST-FILES-OK:" $1 }' && grep -r 'using AwesomeAssertions' tests/Oragon.AdaptivePool.Core.Tests/ | wc -l | awk '{ if ($1 < 11) { print "MISSING-AWESOMEASSERTIONS-IMPORTS:" $1; exit 1 } else print "AA-IMPORTS-OK:" $1 }' && ! grep -rE 'using FluentAssertions' tests/Oragon.AdaptivePool.Core.Tests/ && grep -r 'MetricCollector' tests/Oragon.AdaptivePool.Core.Tests/Telemetry/ && grep -r 'FakeTimeProvider' tests/Oragon.AdaptivePool.Core.Tests/TimeProvider/ && grep -r 'FailureKind.FactoryThrew' tests/Oragon.AdaptivePool.Core.Tests/Pool/FactoryFailureTests.cs && grep -r 'FailureKind.BeforeUseUnhealthy' tests/Oragon.AdaptivePool.Core.Tests/Pool/BeforeUseUnhealthyTests.cs && dotnet test tests/Oragon.AdaptivePool.Core.Tests -c Release 2>&1 | tee /tmp/test-task1.log && grep -qE '(Failed:[ ]*0|Failed!:[ ]*0|Passed!? -)' /tmp/test-task1.log && ! grep -qE 'Failed:[ ]*[1-9]' /tmp/test-task1.log && echo TASK1-DONE</automated>
  </verify>
  <done>11 unit test files exist (plus updated placeholder). All use AwesomeAssertions (no FluentAssertions imports). Every must-have truth has at least one corresponding test. `dotnet test tests/Oragon.AdaptivePool.Core.Tests -c Release` runs and all tests pass under MTP runner. Failing tests trigger CI failure.</done>
</task>

<task type="auto">
  <name>Task 2: Stress test — MaxSize=1 ping-pong (anchor gate per ROADMAP success criterion 2)</name>
  <files>
    tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs,
    tests/Oragon.AdaptivePool.Core.Stress/PlaceholderStressFact.cs
  </files>
  <action>
This test is the anchor gate for waiter-queue + counter-rollback + cancellation correctness. It is RESEARCH.md Example 4 verbatim with minor refinements: 256 threads × ~40 iterations to land at exactly 10,000 cycles; 25s overall watchdog + 5s per-call timeout; final invariants `InUse==0`, `Available==1`.

Replace `PlaceholderStressFact.cs` content with the actual stress test, OR add `PingPongStressTest.cs` as a new file and delete the placeholder (to satisfy file_modified ledger, modify both: replace placeholder content with a one-line skip comment; add the new ping-pong test as the real workload).

`tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs`:
```csharp
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.DependencyInjection;
using Xunit;

namespace Oragon.AdaptivePool.Core.Stress;

public class PingPongStressTest
{
    private sealed class Resource { }

    /// <summary>
    /// Phase 1 anchor gate (ROADMAP success criterion 2 + RESEARCH.md MaxSize=1 ping-pong recipe).
    ///
    /// Proves correctness of:
    ///   - Channel<TaskCompletionSource> direct-handoff waiter queue (no lost wake-ups)
    ///   - Interlocked counter rollback paths
    ///   - CancellationToken honored end-to-end (per-acquire 5s watchdog)
    ///   - No deadlocks under hundreds-of-threads contention with MaxSize=1 (worst case)
    ///
    /// 256 threads × 40 iterations = 10,240 acquire/release cycles. Hard 25s overall ceiling.
    /// </summary>
    [Fact(Timeout = 60_000)] // hard ceiling for the test runner; logical watchdog below is 25s
    public async Task MaxSize1_HundredsOfThreads_TenThousandIterations_NoDeadlock()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddAdaptivePool<Resource>("stress", b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(minSize: 1, maxSize: 1, initialSize: 1));
        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<Resource>>("stress");

        await pool.ReadyAsync();

        const int threads = 256;
        const int iterationsPerThread = 40; // 256 * 40 = 10,240 cycles

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < iterationsPerThread; i++)
            {
                using var perAcquireTimeout = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
                perAcquireTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var item = await pool.AcquireAsync(perAcquireTimeout.Token);
                // brief simulated work — yield to let other threads contend
                await Task.Yield();
            }
        }, watchdog.Token)).ToArray();

        await Task.WhenAll(tasks);

        // Final invariants — every cycle returned cleanly.
        pool.InUse.Should().Be(0, "no item should be checked out after all threads finish");
        pool.Available.Should().Be(1, "the single MaxSize=1 item must be back in the idle queue");
    }
}
```

Replace `PlaceholderStressFact.cs` with a no-op deletion sentinel so the file_modified ledger entry stays valid (or simply delete the file). Recommended: keep the file but reduce to:
```csharp
// Placeholder retired — replaced by PingPongStressTest.cs (Phase 1 Plan 03).
```

Run the stress test locally and confirm green. Note: This test is NOT in the CI default sweep (Plan 01 build.yml does not invoke the Stress project). It is a manual-execution / nightly-job (Phase 4) gate.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && test -f tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs && grep -q 'MaxSize1_HundredsOfThreads_TenThousandIterations_NoDeadlock' tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs && grep -q 'Channel<TaskCompletionSource\|direct-handoff' tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs && grep -q 'WithBounds(minSize: 1, maxSize: 1, initialSize: 1)' tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs && dotnet test tests/Oragon.AdaptivePool.Core.Stress -c Release 2>&1 | tee /tmp/stress-test.log && grep -qE '(Failed:[ ]*0|Passed!? -.*Failed:[ ]*0)' /tmp/stress-test.log && ! grep -qE 'Failed:[ ]*[1-9]' /tmp/stress-test.log && echo STRESS-DONE</antomated>
    <automated>cd /mnt/p/dynamic-pool && test -f tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs && grep -q 'MaxSize1_HundredsOfThreads_TenThousandIterations_NoDeadlock' tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs && grep -q 'WithBounds(minSize: 1, maxSize: 1, initialSize: 1)' tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs && dotnet test tests/Oragon.AdaptivePool.Core.Stress -c Release 2>&1 | tee /tmp/stress-test.log && ! grep -qE 'Failed:[ ]*[1-9]' /tmp/stress-test.log && echo STRESS-DONE</automated>
  </verify>
  <done>PingPongStressTest.cs exists with the 256-thread × 40-iteration MaxSize=1 ping-pong workload; 25s logical watchdog + 5s per-call timeout. Final invariants check `InUse==0` and `Available==1`. `dotnet test tests/Oragon.AdaptivePool.Core.Stress` passes within 60s. Placeholder is reduced to comment-only or deleted. Stress is still NOT in the CI default sweep.</done>
</task>

<task type="auto">
  <name>Task 3: Coverage gate — wire coverlet + reportgenerator into CI; enforce 90% line coverage on Core</name>
  <files>.github/workflows/build.yml</files>
  <action>
Update Plan 01's build.yml to enforce the 90% line-coverage gate on Core (per CONTEXT.md decision). Per RESEARCH Pitfall 7, use the data-collector path (`dotnet test --collect:"XPlat Code Coverage"`) rather than MSBuild integration, because MTP runner's MSBuild integration is brittle in the transition.

Replace `.github/workflows/build.yml` with the expanded version:

```yaml
name: build

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

env:
  DOTNET_NOLOGO: 'true'
  DOTNET_CLI_TELEMETRY_OPTOUT: 'true'
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: 'true'

jobs:
  build:
    name: build-${{ matrix.os }}-${{ matrix.tfm }}
    runs-on: ${{ matrix.os }}
    strategy:
      fail-fast: false
      matrix:
        os: [ ubuntu-latest ]
        tfm: [ net8.0, net9.0, net10.0 ]
    steps:
      - uses: actions/checkout@v4

      - name: setup .NET (8.0.x, 9.0.x, 10.0.x)
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            8.0.x
            9.0.x
            10.0.x

      - name: dotnet restore
        run: dotnet restore Oragon.AdaptivePool.sln

      - name: dotnet build (Release, ${{ matrix.tfm }})
        run: dotnet build Oragon.AdaptivePool.sln -c Release --no-restore -f ${{ matrix.tfm }}

      - name: dotnet test (Core.Tests with coverage; Stress excluded)
        run: >
          dotnet test tests/Oragon.AdaptivePool.Core.Tests/Oragon.AdaptivePool.Core.Tests.csproj
          -c Release --no-build -f ${{ matrix.tfm }}
          --collect:"XPlat Code Coverage"
          --results-directory ./TestResults/${{ matrix.tfm }}
          --logger "console;verbosity=normal"

      - name: install reportgenerator
        run: dotnet tool install -g dotnet-reportgenerator-globaltool

      - name: enforce 90% line coverage on Core
        shell: bash
        run: |
          set -euo pipefail
          # Collect cobertura outputs from the test run
          REPORTS=$(find ./TestResults/${{ matrix.tfm }} -name 'coverage.cobertura.xml' -print | tr '\n' ';' | sed 's/;$//')
          if [ -z "$REPORTS" ]; then
            echo "::error::No coverage reports found under ./TestResults/${{ matrix.tfm }}"
            exit 1
          fi
          # Generate text summary, restricted to Oragon.AdaptivePool.Core assembly
          reportgenerator \
            -reports:"$REPORTS" \
            -targetdir:"./TestResults/${{ matrix.tfm }}/coverage-report" \
            -reporttypes:TextSummary \
            -assemblyfilters:"+Oragon.AdaptivePool.Core"
          cat ./TestResults/${{ matrix.tfm }}/coverage-report/Summary.txt
          # Extract "Line coverage:" percentage and compare to 90.0
          LINE_PCT=$(grep -E '^[[:space:]]*Line coverage:' ./TestResults/${{ matrix.tfm }}/coverage-report/Summary.txt | grep -oE '[0-9]+(\.[0-9]+)?' | head -1)
          if [ -z "$LINE_PCT" ]; then
            echo "::error::Could not parse line coverage from summary"
            exit 1
          fi
          echo "Line coverage on Oragon.AdaptivePool.Core: $LINE_PCT%"
          # Bash float compare via awk
          PASS=$(awk -v cov="$LINE_PCT" -v threshold=90.0 'BEGIN { print (cov+0 >= threshold+0) ? 1 : 0 }')
          if [ "$PASS" -ne 1 ]; then
            echo "::error::Line coverage $LINE_PCT% is below the required 90% gate"
            exit 1
          fi
          echo "Coverage gate satisfied (>= 90%)."

      - name: upload coverage report (artifact)
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: coverage-${{ matrix.tfm }}
          path: ./TestResults/${{ matrix.tfm }}/coverage-report
          if-no-files-found: ignore
```

Key invariants:
- Stress project is STILL not invoked here (only Core.Tests). Per CONTEXT.md.
- `coverlet.collector` (already in Directory.Packages.props from Plan 01, already a PackageReference in Core.Tests.csproj) produces the cobertura XML when `--collect:"XPlat Code Coverage"` is passed.
- `assemblyfilters:"+Oragon.AdaptivePool.Core"` restricts measurement to the production library — test code is excluded from the % calculation.
- 90% gate fails the job; uploads the report as an artifact regardless for diagnosability.
- `dotnet-reportgenerator-globaltool` is installed inside the runner — no checkin into the repo.

If local coverage is below 90%, add additional test cases to `BuilderValidationTests`, `WarmupAndBoundsTests`, or `DisposeDrainTests` (the most likely under-tested paths) until ≥90%. The reflection-based `TryGetHostApplicationStoppingToken` probe in ServiceCollectionExtensions and the finalizer path in PoolItem are difficult to cover deterministically and may need `[ExcludeFromCodeCoverage]` markers — apply them only if necessary AFTER attempting genuine coverage. Document any `[ExcludeFromCodeCoverage]` decisions in the SUMMARY.

Local verification before pushing:
```bash
dotnet test tests/Oragon.AdaptivePool.Core.Tests --collect:"XPlat Code Coverage" --results-directory ./TestResults
reportgenerator -reports:./TestResults/**/coverage.cobertura.xml -targetdir:./TestResults/coverage -reporttypes:TextSummary -assemblyfilters:"+Oragon.AdaptivePool.Core"
cat ./TestResults/coverage/Summary.txt
```
Confirm the "Line coverage" line shows ≥ 90.0%.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && grep -q 'XPlat Code Coverage' .github/workflows/build.yml && grep -q 'reportgenerator' .github/workflows/build.yml && grep -q '+Oragon.AdaptivePool.Core' .github/workflows/build.yml && grep -q '90' .github/workflows/build.yml && ! grep -q 'Oragon.AdaptivePool.Core.Stress' .github/workflows/build.yml && dotnet test tests/Oragon.AdaptivePool.Core.Tests --collect:"XPlat Code Coverage" --results-directory ./TestResults -c Release 2>&1 | tee /tmp/cov-test.log && (which reportgenerator > /dev/null 2>&1 || dotnet tool install -g dotnet-reportgenerator-globaltool) && export PATH="$PATH:$HOME/.dotnet/tools" && REPORTS=$(find ./TestResults -name 'coverage.cobertura.xml' -print | tr '\n' ';' | sed 's/;$//') && reportgenerator -reports:"$REPORTS" -targetdir:./TestResults/coverage-report -reporttypes:TextSummary -assemblyfilters:"+Oragon.AdaptivePool.Core" && LINE_PCT=$(grep -E '^[[:space:]]*Line coverage:' ./TestResults/coverage-report/Summary.txt | grep -oE '[0-9]+(\.[0-9]+)?' | head -1) && awk -v cov="$LINE_PCT" 'BEGIN { exit (cov+0 >= 90.0 ? 0 : 1) }' && echo "COVERAGE-OK ${LINE_PCT}%"</automated>
  </verify>
  <done>build.yml updated with coverage collection step + reportgenerator install + 90% line-coverage gate restricted to `+Oragon.AdaptivePool.Core`. Stress project still not invoked. Local `dotnet test --collect:"XPlat Code Coverage"` followed by reportgenerator confirms ≥90% line coverage on Core. Test artifacts uploaded on every CI run for diagnosability.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| Test code → engine internals | Tests must reach correctness invariants without using `internal` reflection (use public surface only) |
| CI runner → coverlet output | Coverage XML format must match what reportgenerator parses |
| Stress test → CI default sweep | Stress test must NOT inadvertently land in CI default per CONTEXT.md |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-01-16 | Repudiation | Tests pass but engine is broken | mitigate | Each must-have truth converts directly into a `[Fact]`; test names mirror the truth so audits trace 1:1 |
| T-01-17 | Denial of Service | Stress test in default CI bloats per-PR runtime | mitigate | build.yml explicitly invokes Core.Tests path; Stress project not referenced (verified by negative grep gate in Task 2 + Task 3 verify) |
| T-01-18 | Tampering | Coverage gate is silently bypassed by missing cobertura file | mitigate | Coverage step explicitly fails ("No coverage reports found") if the find returns empty |
| T-01-19 | Information Disclosure | Coverage report leaks source paths in CI logs | accept | Repo is intended for OSS publication; source paths are non-secret |
| T-01-20 | Elevation of Privilege | Tests use NSubstitute on internal types | mitigate | Tests substitute only PUBLIC interfaces (`IItemFailurePolicy<T>`); engine internals tested via public surface invariants |
</threat_model>

<verification>
After all 3 tasks complete:

```bash
cd /mnt/p/dynamic-pool

# Default CI-equivalent run (no stress)
dotnet build Oragon.AdaptivePool.sln -c Release                                    # green
dotnet test tests/Oragon.AdaptivePool.Core.Tests -c Release --no-build             # all pass
dotnet test tests/Oragon.AdaptivePool.Core.Tests --collect:"XPlat Code Coverage" \
  --results-directory ./TestResults -c Release --no-build
reportgenerator -reports:./TestResults/**/coverage.cobertura.xml \
  -targetdir:./TestResults/coverage -reporttypes:TextSummary \
  -assemblyfilters:"+Oragon.AdaptivePool.Core"
cat ./TestResults/coverage/Summary.txt   # Line coverage >= 90%

# Manual stress run
dotnet test tests/Oragon.AdaptivePool.Core.Stress -c Release --no-build            # passes within 60s
```

Spot-check:
- `grep -r 'using FluentAssertions' tests/` returns nothing
- `find tests/Oragon.AdaptivePool.Core.Tests -name '*.cs' | wc -l` >= 12
- `grep -c '\[Fact\]\|\[Theory\]' tests/Oragon.AdaptivePool.Core.Tests/**/*.cs` >= 30 (well above the success-criterion-coverage minimum)
- All Phase 1 success criteria 1–6 (ROADMAP) traceable to a passing test
</verification>

<success_criteria>
This plan is complete when:
- [ ] All 11 unit test files exist; placeholder smoke updated to a real DI roundtrip
- [ ] Each Phase 1 ROADMAP success criterion (1–6) has at least one corresponding passing test
- [ ] `dotnet test tests/Oragon.AdaptivePool.Core.Tests` reports 0 failed
- [ ] No test file imports `FluentAssertions` (only `AwesomeAssertions`)
- [ ] PingPongStressTest.cs exists in the Stress project; runs MaxSize=1 with 256 threads × 40 iterations under a 25s logical watchdog and 5s per-call timeout; final invariants Available==1, InUse==0
- [ ] `dotnet test tests/Oragon.AdaptivePool.Core.Stress` reports 0 failed and completes within 60s
- [ ] Coverage of `+Oragon.AdaptivePool.Core` ≥ 90.0% line coverage (verified locally via reportgenerator)
- [ ] CI workflow `.github/workflows/build.yml` runs `--collect:"XPlat Code Coverage"`, installs reportgenerator, and fails the build if coverage < 90%
- [ ] Stress project is still NOT invoked in build.yml (negative grep check)
- [ ] Coverage report uploaded as a CI artifact named `coverage-${tfm}` for diagnosability
- [ ] Telemetry tests verify counter `pool.acquire.count` AND `pool.factory.failures` via `MetricCollector<long>`, AND a manually-attached `MeterListener` proves the meter is OTel-listenable
- [ ] DI tests verify keyed resolution + multi-pool coexistence + default-name (string.Empty) non-keyed fallback
- [ ] Builder validation tests cover the full `0 ≤ Min ≤ Initial ≤ Max` matrix
- [ ] Dispose tests verify drain semantics (Release per idle entry), idempotency, and ObjectDisposedException after dispose
</success_criteria>

<source_coverage_audit>
## Phase 1 Sources → This Plan

**Phase 1 success criteria (from ROADMAP) verified by tests in this plan:**
- Criterion 1 (DI + sync Acquire + await using + idempotent dispose) → ServiceCollectionExtensionsTests + AcquireAndReturnTests + PoolItemDisposeTests
- Criterion 2 (MaxSize=1 ping-pong, no deadlocks, 5s watchdog, CT honored) → PingPongStressTest in the Stress project
- Criterion 3 (Factory throw counter rollback + BeforeUse Unhealthy → policy + replace) → FactoryFailureTests + BeforeUseUnhealthyTests
- Criterion 4 (Meter via IMeterFactory + counters consumable by OTel listener) → MeterAndCounterTests (MetricCollector + manual MeterListener)
- Criterion 5 (IDisposable + IAsyncDisposable drain) → DisposeDrainTests
- Criterion 6 (Eager warm-up awaitable + bounds validation) → WarmupAndBoundsTests + BuilderValidationTests

**Requirements (REQ) verified:**
- API-01: AcquireAndReturnTests (sync + async)
- API-02: PoolItemDisposeTests (idempotency, post-dispose throw)
- API-03: BuilderValidationTests
- HOOK-01: AcquireAndReturnTests `AcquireAsync_GrowsToMaxSize_OnDemand` (Factory invocation count)
- HOOK-02: BeforeUseUnhealthyTests
- HOOK-04: WarmupAndBoundsTests covers AfterUse signature; behavior trivially no-op (per CONTEXT.md "default no-op")
- HOOK-05: DisposeDrainTests `DisposeAsync_InvokesReleaseOnEachIdleEntry`
- BOUND-01: BuilderValidationTests `Build_WithInvalidBounds_Throws`
- BOUND-02: WarmupAndBoundsTests `ReadyAsync_CompletesWhenWarmedUp`
- FAIL-01: FactoryFailureTests + BeforeUseUnhealthyTests (NSubstitute policy verification)
- FAIL-02: BeforeUseUnhealthyTests `BeforeUseUnhealthy_DiscardAndReplace_DefaultPolicy_ProducesFreshItem`
- DI-01: ServiceCollectionExtensionsTests (all four cases)
- QUAL-01: WaitBehaviorTests `WaitBehaviorWait_RespectsCancellationToken` + ping-pong stress (per-acquire 5s CT)
- QUAL-02: DisposeDrainTests
- TELEM-01: MeterAndCounterTests

**Requirements NOT covered here that should NOT be expected (out of Phase 1 scope):**
- HOOK-03 (Check hook BACKGROUND consumption — Phase 2 sweeper)
- ELASTIC-01, ELASTIC-02, TELEM-02, TELEM-03, QUAL-03 — all Phase 2
- RMQ-* — Phase 3
- OSS-* — Phase 4 (note: OSS-01 partial — Phase 1 ships a minimal CI workflow; full OSS-01 includes integration/stress matrix in Phase 4)

**CONTEXT.md decisions verified:**
- AwesomeAssertions used throughout (negative grep on FluentAssertions)
- xUnit v3 + MTP runner (test csproj from Plan 01)
- FakeTimeProvider injection point tested
- Stress project excluded from CI default
- 90% line coverage gate active in CI

**Audit verdict:** No unplanned items, no scope creep. Every Phase 1 ROADMAP success criterion is tested. Every must-have truth maps to at least one [Fact]. PITFALLS.md anchor test (MaxSize=1 ping-pong) is the dedicated stress workload. Coverage gate enforces the CONTEXT.md 90% bar.
</source_coverage_audit>

<output>
After completion, create `.planning/phases/01-core-skeleton-fixed-size-pool/01-03-SUMMARY.md` documenting:
- Map of every Phase 1 ROADMAP success criterion → test class(es) + test method(s) that verify it
- The MaxSize=1 ping-pong stress test parameters (256 threads, 40 iter, 25s watchdog, 5s per-call) and where it lives
- The CI coverage workflow design (XPlat collector → reportgenerator → 90% gate restricted to `+Oragon.AdaptivePool.Core`)
- Final coverage % achieved on Core (capture from reportgenerator Summary.txt)
- Any `[ExcludeFromCodeCoverage]` markers added (with justification — likely the IHostApplicationLifetime probe and PoolItem finalizer paths)
- Confirmation that the Stress project remains EXCLUDED from CI default (per CONTEXT.md) — it is run manually until Phase 4 adds the nightly job
- Heads-up to Phase 2: the test infrastructure (FakeTimeProvider injection, MetricCollector, NSubstitute on IItemFailurePolicy) is now reusable for sweeper / elasticity tests
</output>
