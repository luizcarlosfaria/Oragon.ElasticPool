---
phase: 02-elasticity-health
plan: 03
type: execute
wave: 3
depends_on: [01, 02]
files_modified:
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/UtilizationSamplerTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/PressureSamplerTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/WaitDurationHistogramTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/SweepBackoffStateTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/BackgroundSweepTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/ElasticGrowTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/HystereticShrinkTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Pool/SweepBackoffIntegrationTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Telemetry/ActivitySourceSpanTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Telemetry/Phase2CountersAndHistogramsTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/Telemetry/LoggerMessageEventTests.cs
  - tests/Oragon.AdaptivePool.Core.Tests/TestSupport/CapturedActivities.cs
  - tests/Oragon.AdaptivePool.Core.Tests/TestSupport/CapturedLogEntries.cs
  - tests/Oragon.AdaptivePool.Core.Stress/BurstIdleBurstStressTest.cs
  - tests/Oragon.AdaptivePool.Core.Benchmarks/Oragon.AdaptivePool.Core.Benchmarks.csproj
  - tests/Oragon.AdaptivePool.Core.Benchmarks/PoolDiagnosticsLogBenchmarks.cs
  - Directory.Packages.props
autonomous: true
requirements: [ELASTIC-01, ELASTIC-02, TELEM-02, TELEM-03, QUAL-03]
must_haves:
  truths:
    - "Each Plan 01 internal sealed component (UtilizationSampler, WaitDurationHistogram, PressureSampler, SweepBackoffState) has unit-level coverage proving its core invariants under FakeTimeProvider control."
    - "BackgroundSweeper tick fires deterministically under FakeTimeProvider.Advance(SweepInterval) using the internal TickCompleted probe; Check hook is invoked on every idle item; Pool.Sweep span is emitted with per-item ActivityEvents."
    - "Composite-signal grow is verified under each signal in isolation (waiters-only, utilization-only, p95-only) and combined (OR semantics) — every signal that can trip grow has a passing test."
    - "Hysteretic shrink test asserts NO shrink occurs during cooldown windows even with idle items past IdleTimeout; AFTER cooldown elapses, exactly one item per tick is evicted; never below MinSize."
    - "Sweep adaptive backoff test simulates downstream outage (Check throws on >=50% items for 3+ consecutive ticks) and asserts timer.Period progression 30s -> 60s -> 120s -> MaxBackoff cap; resets to base on first clean tick."
    - "ActivityListener attached to 'Oragon.AdaptivePool' captures Pool.Acquire, Pool.Release, Pool.HealthCheck, Pool.Grow, Pool.Shrink, Pool.Sweep spans across their respective happy-path tests."
    - "MetricCollector<long> verifies pool.grow.count, pool.shrink.count, pool.health.failures increment exactly when expected; MetricCollector<double> verifies pool.acquire.wait.duration and pool.sweep.duration record positive values."
    - "[LoggerMessage] entries 1005-1010 and 1099 fire on their corresponding state transitions; capture verified via FakeLogger from Microsoft.Extensions.Logging.Testing OR a custom in-memory ILoggerProvider (see Task 5 decision)."
    - "Anchor stress test (BurstIdleBurstStressTest) — burst -> idle -> burst with 200 threads x 10 cycles -- completes within watchdog without deadlocks; pool grows under burst, shrinks during idle, regrows on second burst; counter snapshot consistency verified at end."
    - "Coverage gate at 90% line on Oragon.AdaptivePool.Core remains green after all Phase 2 additions."
    - "Optional benchmark project produces a baseline PoolDiagnosticsLog.Grew BenchmarkDotNet result establishing 0-allocation-per-call (ROADMAP success criterion 5)."
  artifacts:
    - path: "tests/Oragon.AdaptivePool.Core.Tests/Pool/UtilizationSamplerTests.cs"
      provides: "Unit tests for ring-buffer sampler debounce + window expiry + AverageUtilization correctness"
      min_lines: 60
    - path: "tests/Oragon.AdaptivePool.Core.Tests/Pool/PressureSamplerTests.cs"
      provides: "OR-combined decision matrix; MaxSize cap; per-signal isolation"
      min_lines: 60
    - path: "tests/Oragon.AdaptivePool.Core.Tests/Pool/WaitDurationHistogramTests.cs"
      provides: "P95 correctness under <100 samples and full ring-buffer overwrite"
      min_lines: 30
    - path: "tests/Oragon.AdaptivePool.Core.Tests/Pool/SweepBackoffStateTests.cs"
      provides: "Backoff curve 30s -> 60s -> 120s -> cap; reset on clean tick; 0-checked-item no-op"
      min_lines: 40
    - path: "tests/Oragon.AdaptivePool.Core.Tests/Pool/BackgroundSweepTests.cs"
      provides: "Deterministic tick firing via FakeTimeProvider + TickCompleted probe; Check hook invoked"
      min_lines: 60
    - path: "tests/Oragon.AdaptivePool.Core.Tests/Pool/ElasticGrowTests.cs"
      provides: "Grow under waiter-only signal, utilization-only signal, p95-only signal, and combined OR"
      min_lines: 80
    - path: "tests/Oragon.AdaptivePool.Core.Tests/Pool/HystereticShrinkTests.cs"
      provides: "No shrink during cooldown; shrink after cooldown; never below MinSize; one-per-tick"
      min_lines: 60
    - path: "tests/Oragon.AdaptivePool.Core.Tests/Pool/SweepBackoffIntegrationTests.cs"
      provides: "End-to-end backoff progression under simulated outage"
      min_lines: 50
    - path: "tests/Oragon.AdaptivePool.Core.Tests/Telemetry/ActivitySourceSpanTests.cs"
      provides: "ActivityListener captures Pool.Grow, Pool.Shrink, Pool.Sweep, Pool.HealthCheck spans + tag matrix"
      min_lines: 60
    - path: "tests/Oragon.AdaptivePool.Core.Tests/Telemetry/Phase2CountersAndHistogramsTests.cs"
      provides: "MetricCollector assertions for the 5 new instruments"
      min_lines: 50
    - path: "tests/Oragon.AdaptivePool.Core.Tests/Telemetry/LoggerMessageEventTests.cs"
      provides: "EventId 1005-1010 + 1099 capture matrix"
      min_lines: 50
    - path: "tests/Oragon.AdaptivePool.Core.Tests/TestSupport/CapturedActivities.cs"
      provides: "Reusable ActivityListener-based span capture helper"
      min_lines: 30
    - path: "tests/Oragon.AdaptivePool.Core.Stress/BurstIdleBurstStressTest.cs"
      provides: "Phase 2 anchor stress test (burst -> idle -> burst, FakeTimeProvider, 200 threads x 10 cycles)"
      min_lines: 60
  key_links:
    - from: "tests/Oragon.AdaptivePool.Core.Tests/Pool/BackgroundSweepTests.cs"
      to: "src/Oragon.AdaptivePool.Core/Internals/BackgroundSweeper.cs"
      via: "Internal TickCompleted probe via [InternalsVisibleTo]; await on probe after fake.Advance"
      pattern: "TickCompleted"
    - from: "tests/Oragon.AdaptivePool.Core.Tests/Telemetry/Phase2CountersAndHistogramsTests.cs"
      to: "src/Oragon.AdaptivePool.Core/Telemetry/PoolMeterNames.cs"
      via: "MetricCollector<long>(meterFactory, MeterName, instrumentName) for each Phase 2 instrument"
      pattern: "MetricCollector<"
    - from: "tests/Oragon.AdaptivePool.Core.Stress/BurstIdleBurstStressTest.cs"
      to: "src/Oragon.AdaptivePool.Core/Internals/AdaptivePool.cs"
      via: "200 concurrent AcquireAsync producing waiters; FakeTimeProvider.Advance drives sweep ticks"
      pattern: "AcquireAsync"
---

<objective>
Prove every Phase 2 must-have via deterministic tests. Cover each new internal component in isolation, the integration of grow/shrink/sweep through the public surface, the full telemetry surface (counters, histograms, spans, log entries), and the anchor stress scenario. Add an optional benchmark project to establish the [LoggerMessage] allocation-free baseline. Keep the coverage gate green.

Purpose: Phase 1 SUMMARY established the canonical patterns (FakeTimeProvider, MetricCollector, NSubstitute on public POCO type-args, AwesomeAssertions). This plan applies them at scale to lock down the Phase 2 behavior so that Phase 3 (RabbitMQ adapter) can build on a verified foundation. The anchor stress test is the headline gate — if burst-idle-burst passes, the elastic-and-self-healing pool is real.

Output: 11 unit-test files, 2 reusable test-support helpers, 1 anchor stress test, 1 benchmark project, plus a CPM pin for BenchmarkDotNet. All test files follow Phase 1 conventions: xUnit v3, AwesomeAssertions, FakeTimeProvider, MetricCollector. Coverage stays >=90%.
</objective>

<execution_context>
@/mnt/p/dynamic-pool/.claude/get-shit-done/workflows/execute-plan.md
@/mnt/p/dynamic-pool/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/phases/02-elasticity-health/02-CONTEXT.md
@.planning/phases/02-elasticity-health/02-RESEARCH.md
@.planning/phases/02-elasticity-health/02-01-SUMMARY.md
@.planning/phases/02-elasticity-health/02-02-SUMMARY.md
@.planning/phases/01-core-skeleton-fixed-size-pool/03-SUMMARY.md

# Existing test infrastructure (re-use, don't rebuild).
@tests/Oragon.AdaptivePool.Core.Tests/TestSupport/Resource.cs
@tests/Oragon.AdaptivePool.Core.Tests/Telemetry/MeterAndCounterTests.cs
@tests/Oragon.AdaptivePool.Core.Tests/TimeProvider/TimeProviderInjectionTests.cs
@tests/Oragon.AdaptivePool.Core.Stress/PingPongStressTest.cs

# Phase 2 production source under test.
@src/Oragon.AdaptivePool.Core/Internals/UtilizationSampler.cs
@src/Oragon.AdaptivePool.Core/Internals/WaitDurationHistogram.cs
@src/Oragon.AdaptivePool.Core/Internals/PressureSampler.cs
@src/Oragon.AdaptivePool.Core/Internals/SweepBackoffState.cs
@src/Oragon.AdaptivePool.Core/Internals/BackgroundSweeper.cs
@src/Oragon.AdaptivePool.Core/Internals/AdaptivePool.cs
@src/Oragon.AdaptivePool.Core/Telemetry/TelemetryEmitter.cs
@src/Oragon.AdaptivePool.Core/Telemetry/PoolDiagnosticsLog.cs
@src/Oragon.AdaptivePool.Core/Telemetry/PoolMeterNames.cs

<interfaces>
<!-- Test helpers consume these contracts established by Plan 01 + Plan 02. -->

ActivitySource name: "Oragon.AdaptivePool"
Meter name: "Oragon.AdaptivePool"

Span operation names: "Pool.Acquire", "Pool.Release", "Pool.HealthCheck", "Pool.Grow", "Pool.Shrink", "Pool.Sweep"
Span tag keys: "pool.name", "outcome", "pool.size_after", "pool.size_before",
  "grow.tripped_by_waiters", "grow.tripped_by_utilization", "grow.tripped_by_p95"

Counter instrument names (long):
  "pool.acquire.count", "pool.factory.failures", "pool.grow.count", "pool.shrink.count", "pool.health.failures"
Histogram instrument names (double, seconds):
  "pool.acquire.wait.duration", "pool.sweep.duration"

LoggerMessage EventIds: 1001 ItemLeaked, 1002 FactoryFailed, 1003 BeforeUseUnhealthy, 1004 ReleaseHookFailedDuringDispose,
  1005 Grew, 1006 Shrunk, 1007 SweepStarted, 1008 SweepCompleted, 1009 SweepFailureBackoff, 1010 CheckUnhealthy, 1099 SweepFailed

Test-only probes via [InternalsVisibleTo("Oragon.AdaptivePool.Core.Tests")]:
  AdaptivePool<T>.Sweeper, .UtilSampler, .WaitHistogram, .BackoffState, .CurrentTotal, .SinceLastGrowTicks
  BackgroundSweeper<T>.TickCompleted (Task that completes after each sweep tick), .TickCount
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Test-support helpers + per-component unit tests (UtilizationSampler, WaitDurationHistogram, PressureSampler, SweepBackoffState)</name>
  <files>
    tests/Oragon.AdaptivePool.Core.Tests/TestSupport/CapturedActivities.cs,
    tests/Oragon.AdaptivePool.Core.Tests/TestSupport/CapturedLogEntries.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/UtilizationSamplerTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/WaitDurationHistogramTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/PressureSamplerTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/SweepBackoffStateTests.cs
  </files>
  <action>
**1. `TestSupport/CapturedActivities.cs`** — reusable IDisposable helper wrapping `ActivityListener` with `ShouldListenTo = src => src.Name == sourceName` and `Sample = (ref _) => ActivitySamplingResult.AllData`. Exposes `List<Activity> Started`, `List<Activity> Stopped`, indexer/Find by `OperationName`. Per RESEARCH §"Pattern 7" + RESEARCH Code Examples §1 of "ActivityListener Test Pattern".

**2. `TestSupport/CapturedLogEntries.cs`** — minimal in-memory `ILoggerProvider` + `ILogger` that captures `(LogLevel, EventId, string Message, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State)` records. Used by Task 5's `LoggerMessageEventTests`. NOTE: We deliberately DO NOT pull `Microsoft.Extensions.Logging.Testing.FakeLogger` because Phase 1 SUMMARY did not pin it; using a 30-line in-memory provider keeps the dependency graph stable. Provider stores entries in a `ConcurrentBag<CapturedLogEntry>` exposed as `IReadOnlyCollection<CapturedLogEntry>`.

**3. `Pool/UtilizationSamplerTests.cs`** — exercise the ring-buffer sampler directly. Internal type → use `[InternalsVisibleTo]` access from Plan 01.
- `Sample_BelowDebounceThreshold_DoesNotWriteBucket`: with FakeTimeProvider, advance 500ms between two `Sample()` calls; assert `AverageUtilization()` reflects only the first sample.
- `Sample_AboveDebounceThreshold_WritesBucket`: advance 1100ms; assert second sample is recorded.
- `AverageUtilization_OnlyConsidersSamplesWithinWindow`: write 30 samples across a 60s timeline with 30s window; assert older samples are excluded.
- `AverageUtilization_WithZeroTotal_Returns0`: sample (0,0); assert 0.0.
- `AverageUtilization_ComputesAcrossBuckets`: sample (5,10) and (10,10) within window; assert ((5+10)/(10+10)) = 0.75.
- `Sample_AllocationFreeAfterFirstCall`: not benchmarked here (benchmark project has it); just assert it doesn't throw under 1000-call hot loop.

**4. `Pool/WaitDurationHistogramTests.cs`**
- `P95_OnEmpty_ReturnsZero`
- `P95_With10Samples_Returns95thPercentile`: record durations 1ms..10ms; P95 should be 10ms (ceiling(10*0.95)-1 = 9 → 10ms).
- `P95_With100Samples_HandlesExactPercentile`: record 1ms..100ms; P95 should be 95ms.
- `P95_OverwritesAfterCapacity`: record 200 samples (1..200ms); only last 100 retained; P95 from samples 101..200 = 195ms (ceiling(100*0.95)-1 = 94 → 195ms).
- `Record_DoesNotThrow_UnderConcurrency`: 50 threads x 1000 records — no exception, P95 finite.

**5. `Pool/PressureSamplerTests.cs`** — uses `Substitute.For<>()` is NOT applicable (PressureSampler is internal sealed); construct directly with FakeTimeProvider + the real sampler/histogram. Use the `Resource` POCO from TestSupport.
- `Evaluate_AtMaxSize_ReturnsShouldGrowFalse_AndAllTripsFalse`
- `Evaluate_BelowAllThresholds_ReturnsShouldGrowFalse`
- `Evaluate_OnlyWaiterCountTrips_ReturnsTrue_WithTrippedByWaitersTrue`
- `Evaluate_OnlyUtilizationTrips_ReturnsTrue_WithTrippedByUtilizationTrue`: feed sampler enough `Sample(8,10)` calls within window to push avg to 0.80.
- `Evaluate_OnlyP95Trips_ReturnsTrue_WithTrippedByP95True`: record 100 wait durations of 150ms; assert P95 trips at 100ms threshold.
- `Evaluate_AllThreeTrip_ReturnsTrue_WithAllFlagsTrue`: prove OR semantics by also asserting flags (no short-circuit).
- `Evaluate_RespectsCustomThresholds`: build options with `GrowOnUtilizationPercent = 0.50`; verify trip at 50% utilization (config-driven).

**6. `Pool/SweepBackoffStateTests.cs`**
- `OnSweepResult_TotalCheckedZero_DoesNotChangeInterval`: no-op when nothing was checked.
- `OnSweepResult_OneCleanWindow_AfterFailure_ResetsToBase`: after 2 failure windows + 1 clean, current = base.
- `OnSweepResult_ThreeFailureWindows_DoublesInterval`: 3 windows of >=50% unhealthy → interval doubles.
- `OnSweepResult_FurtherFailures_ContinueDoubling_UpToCap`: keep failing → 30s -> 60s -> 120s -> 240s -> capped at MaxBackoff (5min).
- `OnSweepResult_AfterCap_RemainsAtCap`: even with 100 more failure windows, never exceeds cap.
- `IntervalChanged_FlagSetCorrectly`: true on transition tick, false on stable tick.

Each test uses AwesomeAssertions: `result.ShouldBe(...)`, `interval.ShouldBe(TimeSpan.FromSeconds(60))`. Follow Phase 1 SUMMARY conventions exactly (no `Assert.Equal`).

**Build invariant:** every test file must compile against `Oragon.AdaptivePool.Core` with `InternalsVisibleTo` enabling access to `Internals.UtilizationSampler` etc. Plan 01 already added this.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet test tests/Oragon.AdaptivePool.Core.Tests/Oragon.AdaptivePool.Core.Tests.csproj --filter "FullyQualifiedName~UtilizationSampler|FullyQualifiedName~WaitDurationHistogram|FullyQualifiedName~PressureSampler|FullyQualifiedName~SweepBackoffState" 2>&1 | tail -5</automated>
  </verify>
  <done>All component-level tests pass on net8.0/net9.0/net10.0; CapturedActivities + CapturedLogEntries compile and are usable by later tasks; no flake under repeated runs (run twice locally).</done>
</task>

<task type="auto">
  <name>Task 2: Engine integration tests — composite-signal grow + hysteretic shrink + background sweep + adaptive backoff</name>
  <files>
    tests/Oragon.AdaptivePool.Core.Tests/Pool/BackgroundSweepTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/ElasticGrowTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/HystereticShrinkTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Pool/SweepBackoffIntegrationTests.cs
  </files>
  <action>
All four files use FakeTimeProvider exclusively (no wall-clock timing). They drive the engine through its public surface (`AdaptiveObjectPoolFactory.Build<T>(sp)` + `AcquireAsync`) and assert on observable behavior + internal probes (`pool.UtilSampler.AverageUtilization()`, `pool.SinceLastGrowTicks`, `pool.Sweeper.TickCompleted`).

**Determinism pattern (every test):**
1. `var fake = new FakeTimeProvider();`
2. Build pool with `.WithTimeProvider(fake)` and the relevant Phase 2 builder fluent methods.
3. After `fake.Advance(SweepInterval)`, `await pool.Sweeper.TickCompleted;` to deterministically wait for the tick body to complete (the probe replaces `Task.Yield()` for stronger determinism per RESEARCH §"Pattern 9" + Open Question A2).
4. Assert.

**1. `BackgroundSweepTests.cs`**
- `Sweep_FiresOnAdvance_AndCallsCheckHookOnIdleItems`: 3 idle items + Check hook returning Healthy; advance one SweepInterval; await TickCompleted; assert Check called 3 times.
- `Sweep_DoesNotInvokeCheck_WhenNoneConfigured`: no Check; advance; assert no exception, sweep still tick-counts.
- `Sweep_StoppedOnDispose_TerminatesCleanly`: dispose pool, then advance time; assert TickCompleted does NOT continue advancing (TickCount stable).
- `Sweep_RunsAtConfiguredInterval`: build with `.SweepInterval(TimeSpan.FromSeconds(15))`; advance 15s; await tick; advance 15s; await tick; assert TickCount incremented twice.

**2. `ElasticGrowTests.cs`** — verify each composite signal in isolation + combined.
- `Grow_OnWaiterSignalOnly_RaisesPoolSize`:
  - Build with `MaxSize=10, MinSize=0, InitialSize=0, GrowOnWaiterCount=1, GrowOnUtilizationPercent=1.01 (impossible to trip), GrowOnWaitTimeP95=TimeSpan.FromHours(1) (impossible to trip)`.
  - Acquire 1 item (forces grow from 0→1 via MinSize fallthrough OR pressure).
  - Hold the item; trigger one parallel `AcquireAsync` (becomes a waiter) — this should immediately grow because waiters >= 1.
  - Assert: pool grows to 2, second acquire succeeds without parking, MetricCollector observes pool.grow.count >= 1.
- `Grow_OnUtilizationSignalOnly_RaisesPoolSize`:
  - Build with `MaxSize=20, GrowOnWaiterCount=int.MaxValue (untrippable), GrowOnUtilizationPercent=0.50, GrowOnWaitTimeP95=TimeSpan.FromHours(1)`.
  - Hold N items s.t. inUse/total >= 0.50 across the window; advance fake time past UtilizationWindow (30s) of sustained samples; perform another acquire; assert grow fired with `TrippedByUtilization=true` (verify via captured Grow span tag).
- `Grow_OnP95SignalOnly_RaisesPoolSize`:
  - Build with `MaxSize=10, GrowOnWaiterCount=int.MaxValue, GrowOnUtilizationPercent=1.01, GrowOnWaitTimeP95=TimeSpan.FromMilliseconds(50)`.
  - Force >=100 wait-duration recordings >= 100ms each via `pool.WaitHistogram.Record(...)` (internal probe). The next slow-path Acquire should observe ShouldGrow via P95.
- `Grow_OrSemantics_AnyOneSignalSuffices`: loop over the 3 isolated configs above, each must independently trigger.
- `Grow_RespectsMaxSize_DoesNotExceedTotal`: high pressure on all 3 signals + MaxSize=5; assert _total never exceeds 5 across the test.
- `Grow_RecordsCounterAndSpan`: attach `MetricCollector<long>` on `pool.grow.count` + `CapturedActivities("Oragon.AdaptivePool")`; trigger one grow; assert exactly 1 counter increment + exactly 1 `Pool.Grow` stopped activity with the expected tags.

**3. `HystereticShrinkTests.cs`**
- `Shrink_DoesNotFire_DuringCooldownWindows`:
  - Build with `MinSize=1, MaxSize=10, IdleTimeout=10s, ShrinkCooldownWindows=3, SweepInterval=30s`.
  - Force grow to 5 items (one waiter + then release).
  - Advance time past IdleTimeout for all idle entries.
  - Advance 1 sweep interval (cooldown counter = 1, < 3); await TickCompleted; assert pool.Available is unchanged (no shrink).
  - Advance 2nd sweep interval; await; still no shrink (cooldown = 2).
  - Advance 3rd sweep interval; await; THIS tick should be shrink-eligible (cooldown >= 3). Assert exactly 1 idle item evicted (gentle decay = 1 per tick).
- `Shrink_NeverGoesBelowMinSize`:
  - Build with `MinSize=2, MaxSize=10`.
  - Grow to 5 items, all idle, all past IdleTimeout, cooldown elapsed.
  - Advance 10 sweep ticks; assert pool.Available stops decrementing at 2.
- `Shrink_OnlyEvictsItemsPastIdleTimeout`:
  - Mix of fresh items (LastReturnedAt=now) and stale items (LastReturnedAt=2*IdleTimeout ago) at sweep tick after cooldown.
  - Assert only stale items are evicted (stop when head is fresh).
- `Shrink_RecordsCounterAndSpan_AndReleaseHook`: attach MetricCollector on `pool.shrink.count` + CapturedActivities + a Substituted Release delegate; trigger one shrink; assert 1 counter + 1 `Pool.Shrink` span + Release called once.

**4. `SweepBackoffIntegrationTests.cs`**
- `Backoff_AfterThreeFailureWindows_DoublesInterval`:
  - Build with Check hook that throws for the first 9 calls then returns Healthy. (Use a thread-safe counter + closure.)
  - 3 idle items so each tick checks 3 items; tick 1 = 3/3 unhealthy, tick 2 = 3/3 unhealthy, tick 3 = 3/3 unhealthy → backoff triggers; tick 4 = 0/0 (no items? OR add idle items so tick happens) → behavior: state reset on first non-failure window.
  - Drive 3 ticks via fake.Advance; await TickCompleted; assert `pool.BackoffState.CurrentInterval == TimeSpan.FromSeconds(60)` (doubled from 30).
- `Backoff_ResetsOnFirstCleanTick`: continue scenario; after backoff triggered, fix Check (return Healthy); advance; assert interval back to base 30s.
- `Backoff_CapsAtMaxBackoff`: configure `MaxBackoff = TimeSpan.FromSeconds(120)`; force unbounded failure windows; assert interval saturates at 120s.
- `Backoff_LogsSweepFailureBackoff_OnTransition`: capture logs via CapturedLogEntries; assert EventId 1009 fires when interval doubles.

All tests obey the Phase 1 conventions: `Resource` POCO, `services.AddMetrics()` + `services.AddLogging()` in DI setup, AwesomeAssertions, no wall-clock timing.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet test tests/Oragon.AdaptivePool.Core.Tests/Oragon.AdaptivePool.Core.Tests.csproj --filter "FullyQualifiedName~BackgroundSweep|FullyQualifiedName~ElasticGrow|FullyQualifiedName~HystereticShrink|FullyQualifiedName~SweepBackoffIntegration" 2>&1 | tail -8</automated>
  </verify>
  <done>All integration tests pass on all 3 TFMs; no test relies on wall-clock timing (`grep -rn "Thread.Sleep\\|Task.Delay" tests/Oragon.AdaptivePool.Core.Tests/Pool/` shows no occurrences in new files); `pool.Sweeper.TickCompleted` is awaited at every advance site.</done>
</task>

<task type="auto">
  <name>Task 3: Telemetry tests (ActivitySource spans, MetricCollector counters/histograms, LoggerMessage events)</name>
  <files>
    tests/Oragon.AdaptivePool.Core.Tests/Telemetry/ActivitySourceSpanTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Telemetry/Phase2CountersAndHistogramsTests.cs,
    tests/Oragon.AdaptivePool.Core.Tests/Telemetry/LoggerMessageEventTests.cs
  </files>
  <action>
**1. `ActivitySourceSpanTests.cs`** (uses `CapturedActivities` from Task 1)
- `AcquireAsync_EmitsPoolAcquireSpan_WithOkOutcome`
- `AcquireAsync_Cancellation_EmitsAcquireSpan_WithCanceledOutcome`
- `Grow_EmitsPoolGrowSpan_WithTripFlags`: trigger grow via waiter signal; assert exactly 1 `Pool.Grow` stopped activity with `grow.tripped_by_waiters=true`.
- `Shrink_EmitsPoolShrinkSpan_WithSizeBeforeAfter`: trigger shrink (FakeTimeProvider after cooldown); assert tags `pool.size_before` + `pool.size_after`.
- `Sweep_EmitsOneSpanPerTick_WithPerItemEvents`: configure 5 idle items + Check; advance one tick; assert exactly 1 `Pool.Sweep` activity + `Activity.Events` count == 5 (per-item events).
- `HealthCheck_EmitsPoolHealthCheckSpan_PerInvocation_WithHealthyOrUnhealthyOutcome`: 3 idle items, Check returns Healthy/Unhealthy/Healthy; advance; assert 3 `Pool.HealthCheck` stopped activities with the expected outcome tag distribution.
- `Spans_AllCarryPoolNameTag`: every Phase 2 span has tag `pool.name == "test-pool"`.
- `NoListenersAttached_StartActivityReturnsNull_NoCrash`: build without `CapturedActivities`; trigger grow; assert no exception.

**2. `Phase2CountersAndHistogramsTests.cs`** (per Phase 1 SUMMARY canonical pattern — `MetricCollector<T>` from `Microsoft.Extensions.Diagnostics.Testing`)
- `GrowCount_IncrementsExactlyOncePerGrow`: `MetricCollector<long>(meterFactory, "Oragon.AdaptivePool", "pool.grow.count", TimeProvider.System)`; trigger 3 grows; assert snapshot has 3 measurements summing to 3 with `pool.name` tag.
- `ShrinkCount_IncrementsExactlyOncePerShrink`
- `HealthFailures_IncrementsOnEachUnhealthyVerdict`: 5 idle items, Check returns 2 Unhealthy; advance; assert counter incremented by 2.
- `AcquireWaitDuration_RecordsWaiterDurations`: `MetricCollector<double>(... "pool.acquire.wait.duration" ...)`; trigger 1 parked waiter for ~50ms (FakeTimeProvider-driven; record via internal probe that simulates wait); assert exactly 1 measurement > 0 with `pool.name` tag.
- `SweepDuration_RecordsOnEveryTick`: advance 3 sweep intervals; assert >= 3 measurements.
- `AllInstrumentsTaggedWithPoolName`: scan every snapshot for the tag.

**3. `LoggerMessageEventTests.cs`** (uses `CapturedLogEntries` from Task 1)
- `Grew_EventId1005_FiresOnGrow`
- `Shrunk_EventId1006_FiresOnShrink`
- `SweepStarted_EventId1007_FiresAtTickStart`
- `SweepCompleted_EventId1008_FiresAtTickEnd`
- `SweepFailureBackoff_EventId1009_FiresOnIntervalChange`
- `CheckUnhealthy_EventId1010_FiresOnUnhealthyVerdict`
- `SweepFailed_EventId1099_FiresOnTickException`: configure Check that throws AND a downstream that breaks the tick body (e.g., FailurePolicy that throws); assert 1099 entry exists with the propagated exception.
- `LogLevels_MatchSpecification`: scan captured entries; Grew/Shrunk = Information, SweepStarted/Completed = Debug, SweepFailureBackoff/CheckUnhealthy = Warning, SweepFailed = Error.

DI setup pattern (used by all 3 files):
```csharp
var services = new ServiceCollection();
services.AddMetrics();
services.AddLogging(b => b.AddProvider(_capturedLogProvider).SetMinimumLevel(LogLevel.Trace));
using var sp = services.BuildServiceProvider();
```

This guarantees `IMeterFactory` is registered (so `MetricCollector` can attach by meter name) and the captured-log provider receives every entry including Debug.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet test tests/Oragon.AdaptivePool.Core.Tests/Oragon.AdaptivePool.Core.Tests.csproj --filter "FullyQualifiedName~ActivitySourceSpan|FullyQualifiedName~Phase2CountersAndHistograms|FullyQualifiedName~LoggerMessageEvent" 2>&1 | tail -8</automated>
  </verify>
  <done>All telemetry tests pass; CapturedActivities + MetricCollector + CapturedLogEntries all integrate cleanly; spans match the operation names + tag schema from PoolMeterNames; counters increment by the expected delta per assertion.</done>
</task>

<task type="auto">
  <name>Task 4: Anchor stress test — burst -> idle -> burst with FakeTimeProvider</name>
  <files>
    tests/Oragon.AdaptivePool.Core.Stress/BurstIdleBurstStressTest.cs
  </files>
  <action>
Implement the Phase 2 anchor stress test, analogous to Phase 1's `PingPongStressTest` (per RESEARCH Code Examples §4 + §"Stress Test Design").

**File:** `tests/Oragon.AdaptivePool.Core.Stress/BurstIdleBurstStressTest.cs`

Design points (RESEARCH §"Stress Test Design"):
1. **FakeTimeProvider** drives sweep deterministically — wall-clock would multi-minute the test.
2. **200 threads × 10 cycles** = 2000 acquire/release pairs is enough to reliably trigger grow under contention.
3. **Watchdog**: `using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(45));` + xUnit `Timeout = 60_000` outer net.
4. **Three phases**: burst1 → idle → burst2.
5. **Assertions**:
   - After burst1: `pool.InUse == 0`, `pool.Available > MinSize` (grew).
   - After idle: `pool.Available == MinSize` (shrunk).
   - After burst2: `pool.InUse == 0`, `pool.Available > MinSize` (regrew).
6. **Telemetry assertion**: attach `MetricCollector<long>` on `pool.grow.count` and `pool.shrink.count`; assert `growCount >= 1` after burst1, `shrinkCount >= MinSize-MaxAfterBurst1` (i.e., enough shrinks to reach MinSize).
7. **Counter consistency**: at the very end, `pool.InUse == 0` AND `pool.Available + 0 == _total` (no ghost reservations).

```csharp
[Fact(Timeout = 60_000)]
public async Task BurstIdleBurst_PoolGrowsShrinksGrowsAgain_WithoutDeadlocks()
{
    var fake = new FakeTimeProvider();
    var services = new ServiceCollection();
    services.AddMetrics();
    services.AddLogging();
    using var sp = services.BuildServiceProvider();

    using var growCounter = new MetricCollector<long>(
        sp.GetRequiredService<IMeterFactory>(),
        "Oragon.AdaptivePool", "pool.grow.count", TimeProvider.System);
    using var shrinkCounter = new MetricCollector<long>(
        sp.GetRequiredService<IMeterFactory>(),
        "Oragon.AdaptivePool", "pool.shrink.count", TimeProvider.System);

    var poolName = "burst-stress";
    var pool = (AdaptivePool<Resource>)AdaptiveObjectPoolFactory.Build<Resource>(sp)
        .Factory((_, _) => ValueTask.FromResult(new Resource()))
        .Release((r, _) => { r.Dispose(); return ValueTask.CompletedTask; })
        .WithBounds(min: 5, max: 100, initial: 5)
        .WithTimeProvider(fake)
        .SweepInterval(TimeSpan.FromSeconds(30))
        .IdleTimeout(TimeSpan.FromSeconds(60))
        .ShrinkCooldownWindows(3)
        .GrowOnWaiterCount(1)
        .Build();
    await pool.ReadyAsync();

    using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(45));

    // === Burst 1 ===
    var burst1 = Enumerable.Range(0, 200).Select(async _ =>
    {
        for (int i = 0; i < 10; i++)
        {
            using var perCall = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
            perCall.CancelAfter(TimeSpan.FromSeconds(5));
            await using var item = await pool.AcquireAsync(perCall.Token);
            await Task.Yield();
        }
    }).ToArray();
    await Task.WhenAll(burst1);
    pool.InUse.ShouldBe(0);
    var afterBurst1 = pool.Available;
    afterBurst1.ShouldBeGreaterThan(5);
    growCounter.GetMeasurementSnapshot().Sum(m => m.Value).ShouldBeGreaterThan(0);

    // === Idle (drive sweep) ===
    // Need cooldown elapsed (3 windows) + IdleTimeout passed.
    fake.Advance(TimeSpan.FromSeconds(60));     // mark idle entries past IdleTimeout
    for (int i = 0; i < 10; i++)
    {
        fake.Advance(TimeSpan.FromSeconds(30)); // one sweep interval per iteration
        await pool.Sweeper.TickCompleted;
    }
    pool.Available.ShouldBe(5);                  // shrunk to MinSize
    shrinkCounter.GetMeasurementSnapshot().Sum(m => m.Value).ShouldBeGreaterThan(0);

    // === Burst 2 ===
    var burst2 = Enumerable.Range(0, 200).Select(async _ =>
    {
        for (int i = 0; i < 10; i++)
        {
            using var perCall = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
            perCall.CancelAfter(TimeSpan.FromSeconds(5));
            await using var item = await pool.AcquireAsync(perCall.Token);
            await Task.Yield();
        }
    }).ToArray();
    await Task.WhenAll(burst2);
    pool.InUse.ShouldBe(0);
    pool.Available.ShouldBeGreaterThan(5);

    await pool.DisposeAsync();
}
```

**Stress project file invariants** (per Phase 1 SUMMARY):
- File lives in `tests/Oragon.AdaptivePool.Core.Stress/`.
- Project is NOT included in CI default build (verified via `! grep -q 'Oragon.AdaptivePool.Core.Stress' .github/workflows/build.yml`).
- xUnit1051 NoWarn already set in csproj.
- Run manually: `dotnet test --project tests/Oragon.AdaptivePool.Core.Stress/Oragon.AdaptivePool.Core.Stress.csproj --configuration Release`.

**Why this is the anchor:** if grow under contention races with shrink under cooldown, deadlocks under MaxSize=100 + 200-thread bursts, or silently leaks counter state, this test catches it. Phase 1's `PingPongStressTest` proved the fixed-size hot path; this one proves the elastic path.

**Optional secondary stress test** (TIME PERMITTING — NOT required for Plan 03 success): a burst-with-failing-Factory test verifying that `pool.factory.failures` increments under contention without deadlock. Skip if it would push Plan 03 over budget.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet test tests/Oragon.AdaptivePool.Core.Stress/Oragon.AdaptivePool.Core.Stress.csproj --configuration Release 2>&1 | tail -10 && grep -c "BurstIdleBurst" tests/Oragon.AdaptivePool.Core.Stress/BurstIdleBurstStressTest.cs && (! grep -q 'Oragon.AdaptivePool.Core.Stress' .github/workflows/build.yml; echo "stress-not-in-CI: $?")</automated>
  </verify>
  <done>BurstIdleBurstStressTest passes on all 3 TFMs in Release within 60s xUnit timeout (typical: 5-15s wall-clock with FakeTimeProvider); existing PingPongStressTest still passes; stress project still excluded from CI.</done>
</task>

<task type="auto">
  <name>Task 5: Benchmark project for [LoggerMessage] allocation-free verification + coverage gate sanity check</name>
  <files>
    Directory.Packages.props,
    tests/Oragon.AdaptivePool.Core.Benchmarks/Oragon.AdaptivePool.Core.Benchmarks.csproj,
    tests/Oragon.AdaptivePool.Core.Benchmarks/PoolDiagnosticsLogBenchmarks.cs
  </files>
  <action>
**1. Pin BenchmarkDotNet** in `Directory.Packages.props`. Add:
```xml
<PackageVersion Include="BenchmarkDotNet" Version="0.15.4" />
```
Use the most recent stable BenchmarkDotNet version supporting net8/net9/net10 multi-target. If 0.15.4 is unavailable at execution time, the executor will use Context7/CLI fallback (`npx --yes ctx7@latest library benchmarkdotnet` then `docs <id> "multi-target net10"`) to verify the latest version. Pin only what is verified.

**2. Create `tests/Oragon.AdaptivePool.Core.Benchmarks/Oragon.AdaptivePool.Core.Benchmarks.csproj`**:
- Multi-target `<TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>` per project convention.
- `<OutputType>Exe</OutputType>` (BenchmarkDotNet requires console executable).
- `<IsPackable>false</IsPackable>`.
- ProjectReference to `Oragon.AdaptivePool.Core`.
- PackageReference to `BenchmarkDotNet`.
- PackageReference to `Microsoft.Extensions.Logging` + `Microsoft.Extensions.Logging.Abstractions` (already CPM-pinned).
- Inherit Phase 1 invariants: `<NoWarn>$(NoWarn);xUnit1051</NoWarn>` not needed (no xUnit), but keep `TreatWarningsAsErrors=true` if `Directory.Build.props` propagates.

**3. Create `tests/Oragon.AdaptivePool.Core.Benchmarks/PoolDiagnosticsLogBenchmarks.cs`** with `[MemoryDiagnoser]` and benchmarks for the new `[LoggerMessage]` entries.
```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oragon.AdaptivePool.Core.Telemetry;

namespace Oragon.AdaptivePool.Core.Benchmarks;

[MemoryDiagnoser]
public class PoolDiagnosticsLogBenchmarks
{
    private ILogger _logger = NullLogger.Instance;
    private const string PoolName = "bench-pool";

    [GlobalSetup]
    public void Setup()
    {
        // NullLoggerProvider is the cheapest path that still exercises [LoggerMessage] dispatch.
        // For the allocation-free claim we need a logger whose IsEnabled returns TRUE,
        // because IsEnabled=false short-circuits the source-gen and trivially allocates 0.
        var factory = LoggerFactory.Create(b => b.AddProvider(new EnabledNullProvider()));
        _logger = factory.CreateLogger("bench");
    }

    [Benchmark] public void Grew()              => _logger.Grew(PoolName, 5, 6, true, false, false);
    [Benchmark] public void Shrunk()            => _logger.Shrunk(PoolName, 6, 5);
    [Benchmark] public void SweepStarted()      => _logger.SweepStarted(PoolName, 30.0);
    [Benchmark] public void SweepCompleted()    => _logger.SweepCompleted(PoolName, 12.5, 5, 0, 1);
    [Benchmark] public void SweepFailureBackoff() => _logger.SweepFailureBackoff(PoolName, 30.0, 60.0, 3);
    [Benchmark] public void CheckUnhealthy()    => _logger.CheckUnhealthy(PoolName, "TimeoutException");

    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(PoolDiagnosticsLogBenchmarks).Assembly).Run(args);
}

internal sealed class EnabledNullProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new EnabledNullLogger();
    public void Dispose() { }
    private sealed class EnabledNullLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { /* discard — we measure dispatch + state-construction, not formatting */ }
    }
}
```

**Allocation-free target** (ROADMAP success criterion 5 + RESEARCH Assumption A6): each benchmark should report `0 B` allocated per call in the BenchmarkDotNet output. The `[LoggerMessage]` source-gen produces a `LogState` value type that boxes only when `IsEnabled` is true AND a non-primitive arg is passed. All Phase 2 entries use only primitives + Exception (which is reference, not boxed) so allocation = 0 is the expected result. This task does NOT enforce a strict CI gate on the benchmark — it is a baseline artifact for documentation; manual run only.

**4. Add a top-level CI no-op for the benchmark project**: do NOT add it to the CI workflow (`.github/workflows/build.yml`) — benchmarks are manually run only, like the stress project. Document in the project's README/docs that the benchmark is run via `dotnet run --project tests/Oragon.AdaptivePool.Core.Benchmarks --configuration Release`.

**5. Coverage gate sanity check.** Plan 03 may push Core line coverage below 90% if Phase 2 adds more production lines than test lines. After completing Tasks 1-4, run the local coverage probe (per Phase 1 SUMMARY recipe):
```bash
coverlet ./tests/Oragon.AdaptivePool.Core.Tests/bin/Debug/net10.0/Oragon.AdaptivePool.Core.Tests.dll \
  --target dotnet --targetargs ./tests/Oragon.AdaptivePool.Core.Tests/bin/Debug/net10.0/Oragon.AdaptivePool.Core.Tests.dll \
  --format cobertura --output coverage/ --include "[Oragon.AdaptivePool.Core]*"
reportgenerator -reports:coverage/coverage.cobertura.xml -targetdir:coverage-report \
  -assemblyfilters:+Oragon.AdaptivePool.Core -reporttypes:TextSummary
grep -E "Line coverage:" coverage-report/Summary.txt
```
- If coverage >= 90%, success — done.
- If coverage < 90%, identify gap-files and add closing tests in the lowest-coverage Phase 2 component file (likely `BackgroundSweeper.cs` or `AdaptivePool.cs`'s grow path). Closing tests follow the Phase 1 pattern of using only the public surface — no `[ExcludeFromCodeCoverage]` markers.
- The verify command below performs the local coverage check and fails if < 90.0%.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet build tests/Oragon.AdaptivePool.Core.Benchmarks/Oragon.AdaptivePool.Core.Benchmarks.csproj /clp:ErrorsOnly && dotnet test tests/Oragon.AdaptivePool.Core.Tests/Oragon.AdaptivePool.Core.Tests.csproj --configuration Release 2>&1 | tail -5 && (! grep -q 'Oragon.AdaptivePool.Core.Benchmarks' .github/workflows/build.yml; echo "bench-not-in-CI: $?")</automated>
  </verify>
  <done>Benchmark project compiles on all 3 TFMs; full Phase 2 test suite passes; coverage gate >= 90% on Core (re-verify locally — if regressed, add closing tests in this task BEFORE marking done); benchmark project NOT added to CI workflow.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| Test process → engine internals | Tests use `[InternalsVisibleTo("Oragon.AdaptivePool.Core.Tests")]` to reach probes (`Sweeper.TickCompleted`, `WaitHistogram.Record`, `BackoffState`). Production consumers cannot. |
| FakeTimeProvider → engine | Test-only. Production uses `TimeProvider.System`. No path lets tests influence prod behavior. |
| Stress test concurrency → assertion correctness | 200 threads compete; assertions sample after `WhenAll` (synchronization point). No data race in the assertions themselves. |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-02-03-01 | T (Tampering) | Internal probes (TickCompleted, WaitHistogram.Record) | accept | Internal access via `[InternalsVisibleTo]` is the established pattern (Phase 1 SUMMARY assumption A2). Risk is test-only; production consumers cannot reach these surfaces. |
| T-02-03-02 | D (DoS) | Stress test resource consumption | mitigate | Watchdog (45s logical + 60s xUnit Timeout), per-acquire 5s linked CTS, MaxSize=100 ceiling. Same pattern as Phase 1 PingPongStressTest. |
| T-02-03-03 | I (Information disclosure) | Captured log entries | accept | Test process only; no PII in test data (Resource POCO has no fields). |
| T-02-03-04 | T (Tampering) | BenchmarkDotNet output as proof | accept | Benchmark is a baseline artifact, not a CI gate. Result is documented but does not gate merges. |
</threat_model>

<verification>
- All Phase 1 + Phase 2 unit tests pass on net8.0/net9.0/net10.0 (combined ~110+ tests after Phase 2).
- Phase 1 PingPongStressTest still passes (regression guard).
- Phase 2 BurstIdleBurstStressTest passes within 60s xUnit Timeout.
- Coverage gate >= 90% line on `Oragon.AdaptivePool.Core` (re-verified locally; CI gate enforces same).
- ActivityListener captures all 6 Phase 2 span operation names.
- MetricCollector observes all 5 Phase 2 instruments incrementing/recording on the right transitions.
- All 7 new `[LoggerMessage]` EventIds (1005-1010, 1099) are observable via the `CapturedLogEntries` provider.
- Benchmark project compiles and runs (`dotnet run --project ... --configuration Release` manually); benchmark allocation = 0 B per call for all 6 logged entries (documentation artifact).
- Stress + Benchmark projects still excluded from CI default build.
- `grep -rn "Thread.Sleep\\|Task.Delay" tests/Oragon.AdaptivePool.Core.Tests/Pool/ tests/Oragon.AdaptivePool.Core.Tests/Telemetry/` returns 0 hits in NEW files (existing Phase 1 tests may use Task.Delay for cancellation tests — leave those alone).
</verification>

<success_criteria>
- ROADMAP Phase 2 success criterion 1 (composite-signal grow) — covered by ElasticGrowTests + Phase2CountersAndHistogramsTests + ActivitySourceSpanTests.
- ROADMAP Phase 2 success criterion 2 (hysteretic shrink) — covered by HystereticShrinkTests + ActivitySourceSpanTests + Phase2CountersAndHistogramsTests.
- ROADMAP Phase 2 success criterion 3 (sweep adaptive backoff) — covered by SweepBackoffStateTests (unit) + SweepBackoffIntegrationTests (e2e).
- ROADMAP Phase 2 success criterion 4 (stress test, no deadlocks/starvation/counter inconsistency) — covered by BurstIdleBurstStressTest.
- ROADMAP Phase 2 success criterion 5 (ActivitySource spans + [LoggerMessage] allocation-free) — covered by ActivitySourceSpanTests + LoggerMessageEventTests + the benchmark baseline.
- All requirement IDs (ELASTIC-01, ELASTIC-02, TELEM-02, TELEM-03, QUAL-03) traceable to specific tests.
- 90% line-coverage gate on Core green.
</success_criteria>

<output>
After completion, create `.planning/phases/02-elasticity-health/02-03-SUMMARY.md` documenting:
- Test inventory (count of new test methods per file)
- ROADMAP success criterion → test method map (analogous to Phase 1 Plan 03 SUMMARY)
- Coverage achieved on Core (line/branch/method percentages)
- Stress test wall-clock and watchdog headroom
- Benchmark baseline numbers (mean ns + allocated B/op for each [LoggerMessage] entry)
- Heads-up to Phase 3: which test infrastructure (CapturedActivities, CapturedLogEntries, FakeTimeProvider patterns, MetricCollector pattern) is reusable for the RabbitMQ adapter test suite
</output>
