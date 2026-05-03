---
phase: 02-elasticity-health
plan: 02
subsystem: core-engine-elasticity-behavior-and-telemetry
tags: [phase2, elasticity, sweeper, telemetry, activitysource, loggermessage, composite-signal-grow, hysteretic-shrink, backoff]
requires:
  - Plan 02-01 scaffold (5 components + 13 internal accessors + sweeper stub) — commit d825757
  - Phase 1 anchor stress (PingPongStressTest) green on net8/net9/net10
provides:
  - Composite-signal grow path in AcquireAsyncCore (TryGrowAsync helper, gated by PressureSampler.Evaluate OR `total < MinSize`)
  - Wait-duration recording on every parked slow-path wait (both _waitHistogram + pool.acquire.wait.duration histogram)
  - Acquire/Release ActivitySource spans on the public AcquireAsync/ReturnAsync paths
  - Sweep tick body: health-check pass + shrink pass (1 item/tick gentle decay) + cooldown gate + backoff state update
  - 5 new Meter instruments (3 counters + 2 histograms) on TelemetryEmitter
  - 1 ActivitySource (assembly-static) emitting 6 span types (Acquire, Release, Grow, Shrink, Sweep, HealthCheck)
  - 7 new [LoggerMessage] entries (1005 Grew, 1006 Shrunk, 1007 SweepStarted, 1008 SweepCompleted, 1009 SweepFailureBackoff, 1010 CheckUnhealthy, 1099 SweepFailed)
  - 3 new internal accessors on AdaptivePool<T>: Telemetry, Log, DecrementTotal()
affects:
  - Phase 1 hot path: AcquireAsync now wraps in a Pool.Acquire span (null-no-op when no listener); slow path now consults PressureSampler before parking, with `waiters + 1` semantics so default GrowOnWaiterCount=1 preserves Phase 1 grow-on-demand behavior.
  - PingPongStressTest still 300 ms — hot path unchanged when items are available.
  - GrowAndHandoffAsync (Phase 1 method) now emits OnGrow + Grew log on every replacement-grow; counter accuracy verified (zero new test failures).
  - WaitBehavior.Throw still throws synchronously; PressureSampler short-circuits at MaxSize so no grow attempt is made when the pool is full.
tech-stack:
  added: []
  patterns:
    - "Composite-signal grow gate (3 OR-combined signals + MinSize-floor warmup) replacing Phase 1's unconditional CAS-grow loop"
    - "TryGrowAsync helper with using-block ActivitySource span scoped to the Factory call; counter rollback on cancellation/exception preserved from Phase 1"
    - "Wait duration captured in finally block — covers normal completion, OperationCanceledException, ObjectDisposedException re-throw paths uniformly"
    - "Snapshot-iterate-mark-evict pattern for sweep health checks: `ConcurrentQueue.ToArray` snapshot + `LastReturnedAt = MinValue` sentinel so shrink pass owns dequeue+decrement (T-02-02-05 mitigation)"
    - "Sweep span carries per-item ActivityEvents (CONTEXT decision: 1 span/tick, bounded cardinality) — HasListeners() guard to skip ActivityTagsCollection allocations when no listener attached"
    - "Telemetry-first failure logging: per-item Check failure logs only `ex.GetType().Name` as reason; full Exception goes through 1099 SweepFailed at Error level only on catastrophic per-tick failures (avoids log volume explosion under outage)"
key-files:
  created: []
  modified:
    - src/Oragon.AdaptivePool.Core/Telemetry/PoolMeterNames.cs
    - src/Oragon.AdaptivePool.Core/Telemetry/TelemetryEmitter.cs
    - src/Oragon.AdaptivePool.Core/Telemetry/PoolDiagnosticsLog.cs
    - src/Oragon.AdaptivePool.Core/Internals/AdaptivePool.cs
    - src/Oragon.AdaptivePool.Core/Internals/BackgroundSweeper.cs
decisions:
  - "Pass `waiters + 1` (caller counted as if parked) to PressureSampler.Evaluate. With default GrowOnWaiterCount=1, this preserves Phase 1's grow-on-demand semantics: any caller hitting the slow path triggers grow. Higher GrowOnWaiterCount values delay grow until a real queue forms (CONTEXT D-01: tolerance to spikes). Without this adjustment, single-thread acquire on an empty pool with MinSize=0 would deadlock — Phase 1 tests covering WithBounds(0,N,0) would all break."
  - "MinSize-respecting warmup grow clause (`total < MinSize`) is OR'd into the grow gate. Cold-start with InitialSize=0 + MinSize>0 still climbs to MinSize on first acquire even when pressure says no-grow. Critical for Phase 1 BeforeUseUnhealthy-replacement tests."
  - "PoolEntry.LastReturnedAt = DateTimeOffset.MinValue is the sentinel marking 'unhealthy item awaiting eviction'. Chosen because ConcurrentQueue<T> can't remove a specific element. The shrink pass's `head.LastReturnedAt + IdleTimeout <= now` check trivially evicts MinValue-sentinels (MinValue + 60s is still in the past). DecrementTotal is owned by the shrink pass to keep _total balanced — never decremented by health-check pass."
  - "WaitBehavior.Throw branch now fires whenever pressure says no-grow (not just when at MaxSize). This is technically a Phase 1 behavior change — Phase 1 would always try to grow up to MaxSize before throwing. Phase 2 says: pressure decides. With default GrowOnWaiterCount=1 + waiters+1 semantics, single-thread Throw callers still get an item (decision.ShouldGrow=true via byWaiters), so Phase 1 tests pass. Higher GrowOnWaiterCount values intentionally throw earlier when pressure says no-grow — that's the elastic contract."
  - "Sweep span uses HasListeners() guard explicitly (per RESEARCH §Pattern 6). All other span helpers rely on the cheap `StartActivity` null-return path. Per-item HealthCheck spans ARE allowed (one per Check invocation) because each Check is a discrete user-visible operation; cardinality stays bounded by the snapshot size, which is bounded by MaxSize."
  - "Sweep failure backoff log (1009) fires only when the interval ACTUALLY increased (`IntervalChanged && CurrentInterval > prevInterval`). Reset-to-base events are silent — no log noise on normal recovery."
metrics:
  duration: "~6m"
  completed: 2026-05-02
  tasks: 3
  files_created: 0
  files_modified: 5
  commits: 3
  unit_tests_baseline_phase1: 76
  unit_tests_now: 76
  unit_tests_failed: 0
  stress_tests: 1
  stress_tests_failed: 0
  tfms: [net8.0, net9.0, net10.0]
  build_warnings_added: 0
---

# Phase 2 Plan 02: Elasticity & Telemetry Behavior Summary

**One-liner:** Implemented the headline Phase 2 differentiators — composite-signal grow in `AcquireAsyncCore` (gated by `PressureSampler.Evaluate` with `waiters+1` caller-aware semantics + MinSize floor), hysteretic shrink in `BackgroundSweeper.RunSweepTickAsync` (cooldown gate + 1-item-per-tick decay + IdleTimeout floor), full Check-hook health-check pass with adaptive failure backoff (1009 SweepFailureBackoff log on interval bumps), plus the complete observability surface (1 ActivitySource emitting 6 span types, 5 new Meter instruments, 7 new [LoggerMessage] entries 1005-1010+1099). All 76 Phase 1 unit tests × 3 TFMs (228 invocations) still green; PingPongStressTest still ~300 ms across all TFMs.

## What Was Built

### Task 1 — Telemetry surface expansion (commit `d9deac5`)

**`PoolMeterNames.cs`** (+5 instrument constants, +2 framework constants):

| Constant | Value | Use |
| --- | --- | --- |
| `ActivitySourceName` | `"Oragon.AdaptivePool"` | Same string as `MeterName` but separate constant — different types. |
| `OutcomeTag` | `"outcome"` | Bounded-cardinality tag; values: grew, shrunk, healthy, unhealthy, skipped, ok, canceled, factory_failed. |
| `GrowCount` | `"pool.grow.count"` | Counter. |
| `ShrinkCount` | `"pool.shrink.count"` | Counter. |
| `HealthFailures` | `"pool.health.failures"` | Counter. |
| `AcquireWaitDuration` | `"pool.acquire.wait.duration"` | Histogram, seconds. |
| `SweepDuration` | `"pool.sweep.duration"` | Histogram, seconds. |

**`TelemetryEmitter.cs`** additions:

- `internal static readonly ActivitySource ActivitySource = new("Oragon.AdaptivePool")` — process-lifetime singleton, never disposed.
- 3 new `Counter<long>` fields: `_growCount`, `_shrinkCount`, `_healthFailures`.
- 2 new `Histogram<double>` fields: `_acquireWaitDuration`, `_sweepDuration` (both unit `"s"`).
- 5 emission methods: `OnGrow()`, `OnShrink()`, `OnHealthFailure()`, `OnAcquireWait(TimeSpan)`, `OnSweepDuration(TimeSpan)`.
- 6 span helpers, each tagging `pool.name`:
  - `StartAcquireSpan()` → `Pool.Acquire`
  - `StartReleaseSpan()` → `Pool.Release`
  - `StartGrowSpan(poolName, GrowDecision)` → `Pool.Grow` (+ `pool.size_after`, `grow.tripped_by_*` × 3)
  - `StartShrinkSpan(poolName, oldTotal, newTotal)` → `Pool.Shrink` (+ `pool.size_before`, `pool.size_after`)
  - `StartSweepSpan(poolName)` → `Pool.Sweep` (with `HasListeners()` guard — only site doing per-item ActivityEvents)
  - `StartHealthCheckSpan(poolName)` → `Pool.HealthCheck`

**`PoolDiagnosticsLog.cs`** appended 7 `[LoggerMessage]` entries (all primitive args — boxing-free per Assumption A6):

| EventId | Level | Method | Args |
| --- | --- | --- | --- |
| 1005 | Information | `Grew` | poolName, old, new, tripWaiters, tripUtilization, tripP95 |
| 1006 | Information | `Shrunk` | poolName, oldTotal, newTotal |
| 1007 | Debug | `SweepStarted` | poolName, intervalSeconds |
| 1008 | Debug | `SweepCompleted` | poolName, durationMs, checked, unhealthy, shrunk |
| 1009 | Warning | `SweepFailureBackoff` | poolName, oldSec, newSec, consecutiveWindows |
| 1010 | Warning | `CheckUnhealthy` | poolName, reason |
| 1099 | Error | `SweepFailed` | poolName, Exception |

Grep gate: `grep -c "EventId = " PoolDiagnosticsLog.cs` → 11 (4 Phase 1 + 7 new), exactly as specified.

### Task 2 — Composite-signal grow + wait histogram + Acquire/Release spans (commit `d638ea1`)

**Span wrapping.** The public `AcquireAsync` now wraps the core in a `Pool.Acquire` span via the `AcquireAsyncCoreWithSpan` helper:

```csharp
public ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken ct = default)
{
    var span = _telemetry.StartAcquireSpan();
    return AcquireAsyncCoreWithSpan(span, ct, retryCount: 0);
}
```

Outcome tagging: `"ok"` on success, `"canceled"` on `OperationCanceledException`. The mirror `ReturnAsync` is now split into `ReturnAsync` (span wrapper) → `ReturnAsyncCore` (existing logic).

**Composite-signal grow integration.** The Phase 1 unconditional CAS-grow loop in `AcquireAsyncCore` was replaced with a single pressure consultation:

```csharp
var waiters = Volatile.Read(ref _waitersCount) + 1;   // count caller as if parked
var decision = _pressure.Evaluate(Volatile.Read(ref _total), waiters);

if (decision.ShouldGrow || Volatile.Read(ref _total) < _options.MinSize)
{
    var grew = await TryGrowAsync(decision, ct, cancellationToken).ConfigureAwait(false);
    if (grew is not null)
        return await PrepareForUseAsync(grew, ct, cancellationToken, retryCount).ConfigureAwait(false);
}

if (_options.WhenExhausted == WaitBehavior.Throw)
    throw new PoolExhaustedException(_options.MaxSize);
// ... else WaitBehavior.Wait branch
```

The `waiters + 1` semantics (caller counted as if parked) is the **non-trivial decision** of this plan: with default `GrowOnWaiterCount = 1`, every slow-path entry trips `byWaiters=true` → ShouldGrow=true → grow attempted. This preserves Phase 1's grow-on-demand for tests using `WithBounds(0, N, 0)` (which would otherwise deadlock — see Decisions section).

**`TryGrowAsync` helper.** New private method with the existing CAS-reservation pattern and a `using var span = StartGrowSpan(...)`:

```csharp
private async ValueTask<PoolEntry<T>?> TryGrowAsync(GrowDecision d, CancellationToken ct, CancellationToken callerCt)
{
    int currentTotal = Volatile.Read(ref _total);
    if (currentTotal >= _options.MaxSize) return null;
    if (Interlocked.CompareExchange(ref _total, currentTotal + 1, currentTotal) != currentTotal) return null;

    using var span = _telemetry.StartGrowSpan(_options.PoolName, d);
    try
    {
        var newItem = await _options.Factory(_services, ct).ConfigureAwait(false);
        var entry = new PoolEntry<T>(newItem, _time.GetUtcNow(), _time.GetUtcNow());
        Interlocked.Exchange(ref _sinceLastGrowTicks, 0);   // <-- cooldown reset on every grow
        _telemetry.OnGrow();
        _log.Grew(_options.PoolName, currentTotal, currentTotal + 1, d.TrippedByWaiters, d.TrippedByUtilization, d.TrippedByP95);
        span?.SetTag(PoolMeterNames.OutcomeTag, "grew");
        return entry;
    }
    catch (OperationCanceledException) { Interlocked.Decrement(ref _total); /* ... */ throw; }
    catch (Exception ex) { Interlocked.Decrement(ref _total); /* ... + FailurePolicy + */ throw; }
}
```

Phase 1 invariants preserved: counter rollback on Factory failure, OperationCanceledException semantics, FailurePolicy invocation on FactoryThrew.

**Wait-duration recording.** The slow-path waiter park now records elapsed time uniformly via the `finally` block:

```csharp
var waitStart = _time.GetTimestamp();
Interlocked.Increment(ref _waitersCount);
try { await _waiters.Writer.WriteAsync(tcs, ct).ConfigureAwait(false); }
catch { /* decrement + record + throw */ }
// ... await tcs.Task ...
finally
{
    Interlocked.Decrement(ref _waitersCount);
    var elapsed = _time.GetElapsedTime(waitStart);
    _waitHistogram.Record(elapsed);          // feeds PressureSampler.P95
    _telemetry.OnAcquireWait(elapsed);       // feeds pool.acquire.wait.duration histogram
}
```

The `WriteAsync` exception path also records the wait duration (covers pool-disposed-during-write race).

**`GrowAndHandoffAsync` (Phase 1 replacement-grow path).** Now also calls `_telemetry.OnGrow()` and `_log.Grew(..., false, false, false)` (all trip-flags false — replacement grow, not pressure-driven) so counter accuracy is preserved.

**New internal accessors** added on `AdaptivePool<T>`:

| Member | Use |
| --- | --- |
| `internal TelemetryEmitter Telemetry => _telemetry` | Sweep tick reads via `_pool.Telemetry.OnHealthFailure()` etc. |
| `internal ILogger Log => _log` | Sweep tick reads via `_pool.Log.SweepStarted(...)` etc. |
| `internal void DecrementTotal()` | Sweep shrink pass owns dequeue+decrement (T-02-02-05 mitigation). |

Grep gates: `TryGrowAsync` count = 2 (decl + call); `_waitHistogram.Record` count = 2 (success path + WriteAsync error path — exceeds spec of 1); `_telemetry.OnGrow` count = 2 (TryGrowAsync + GrowAndHandoffAsync). All gates met.

### Task 3 — Sweep tick body (commit `26aebab`)

`BackgroundSweeper<T>.RunSweepTickAsync` was promoted from a 2-line stub to the full sequence. The signature changed: it now takes the `PeriodicTimer` as a parameter (so the loop body can adjust `timer.Period` after `_backoff.OnSweepResult`). The 8-step sequence:

1. **Capture sweep start.** `var sweepStart = _options.TimeProvider.GetTimestamp();`
2. **Open `Pool.Sweep` span** + log `SweepStarted` at Debug.
3. **Health-check pass.** Iterate `_pool.Idle.ToArray()` (snapshot — `ConcurrentQueue.ToArray` is stable). For each entry:
   - Increment `totalChecked++`.
   - Open `Pool.HealthCheck` span (one per item).
   - Try `await check(entry.Item, ct)`. On exception OR `state == Unhealthy`:
     - `unhealthy++; _pool.Telemetry.OnHealthFailure(); _pool.Log.CheckUnhealthy(poolName, ex?.GetType().Name ?? "Unhealthy")`.
     - Apply failure policy via `_pool.Options.FailurePolicy.HandleAsync(item, FailureKind.AfterUseUnhealthy, ex, ct)` (using `AfterUseUnhealthy` — no `CheckUnhealthy` kind exists in Phase 1 PublicAPI).
     - Mark via `entry.LastReturnedAt = DateTimeOffset.MinValue` so the shrink pass evicts on this/next tick.
     - Tag `outcome=unhealthy`.
   - On Healthy: tag `outcome=healthy`.
   - Add `ActivityEvent("item-checked", { result: healthy|unhealthy })` to the parent sweep span.
4. **Shrink pass.** Gates: `_pool.SinceLastGrowTicks >= ShrinkCooldownWindows && _pool.CurrentTotal > MinSize`. Walk: `Idle.TryPeek` head → if `LastReturnedAt + IdleTimeout <= now` → `TryDequeue` → `DecrementTotal` → open `Pool.Shrink` span → invoke `Release` hook (try/swallow) → `OnShrink()` + `Shrunk` log → tag `outcome=shrunk`. **At most 1 item evicted per tick** (CONTEXT lock D-06: gentle decay).
5. **Cooldown bookkeeping.** `_pool.IncrementSinceLastGrowTicks()`.
6. **Backoff state update.** `_backoff.OnSweepResult(totalChecked, unhealthy)`. If `IntervalChanged && CurrentInterval > prevInterval`, log `SweepFailureBackoff` at Warning. Caller (`SweepLoopAsync`) reads `_backoff.IntervalChanged` after the tick and swaps `timer.Period`.
7. **Tick close.** Compute elapsed via `GetElapsedTime(sweepStart)` → `OnSweepDuration(elapsed)` + `SweepCompleted(...)` log → tag sweep-span outcome (`unhealthy` if `unhealthy > 0` else `healthy`).

**Catastrophic-failure resilience** (`SweepLoopAsync`): the existing per-tick `try/catch (Exception)` was retained but now logs `SweepFailed` (1099) instead of swallowing silently. The loop continues — a single tick failure cannot kill the sweeper.

**Cancellation safety.** Every `await` inside the tick accepts the linked sweep CT. `OperationCanceledException` propagates out and is caught by the existing `catch (OperationCanceledException) { break; }` in the loop.

Grep gates: `RunSweepTickAsync` count = 2 (decl + call); `OnSweepResult` count = 2 (one in body — counted with state ref); `SweepCompleted` count = 2 (declared + called). All gates met.

## Verification

```
dotnet build /clp:ErrorsOnly       ->  4 projects, 0 errors, 6 warnings (carry-forward Phase 1 SourceLink "no remote")
net10.0  Tests dll                 ->  total: 76, failed: 0, succeeded: 76, duration: 499 ms
net9.0   Tests dll                 ->  total: 76, failed: 0, succeeded: 76, duration: 427 ms
net8.0   Tests dll                 ->  total: 76, failed: 0, succeeded: 76, duration: 520 ms
net10.0  Stress dll (PingPong)     ->  total: 1,  failed: 0, succeeded: 1,  duration: 317 ms
net9.0   Stress dll (PingPong)     ->  total: 1,  failed: 0, succeeded: 1,  duration: 300 ms
net8.0   Stress dll (PingPong)     ->  total: 1,  failed: 0, succeeded: 1,  duration: 299 ms
```

228 unit-test invocations + 3 stress invocations across net8/net9/net10. Zero failures. Phase 1 anchor gate (`PingPongStressTest`) is non-regressive; engine elastic behavior is now active without breaking Phase 1's fixed-size hot path.

`grep -c "EventId = " PoolDiagnosticsLog.cs` = 11 (4 Phase 1 + 7 new). PublicAPI.Unshipped.txt unchanged from Plan 02-01 — all telemetry types remain `internal sealed`, ActivitySource is `internal static readonly`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 — Bug] WaitBehavior.Throw fork after pressure no-grow could deadlock under default config**

- **Found during:** Task 2 design (writing AcquireAsyncCore composite-signal gate).
- **Issue:** The plan's snippet read `decision.ShouldGrow || Volatile.Read(ref _total) < _options.MinSize` to gate grow. With Phase 1 tests that use `WithBounds(0, N, 0)` (MinSize=0), single-thread acquire on an empty pool would: (a) fail fast-path (no idle), (b) `_pressure.Evaluate(0, waitersCount=0)` → `byWaiters = (0 >= 1) = false` → `ShouldGrow=false`, (c) `0 < 0` → false → fall through to wait branch, (d) park forever (nobody to grow on its behalf). All Phase 1 BeforeUseUnhealthy + DI + AfterUse tests would deadlock.
- **Fix:** Pass `waiters + 1` (caller counted as if parked) to PressureSampler.Evaluate. Default GrowOnWaiterCount=1 then makes ANY slow-path caller trip byWaiters=true → grow. Higher values delay grow until queue depth ≥ threshold (CONTEXT D-01: tolerance to spikes). Net effect: Phase 1 tests pass identically; Phase 2 callers can opt into latency-tolerant behavior by raising the threshold.
- **Files modified:** `src/Oragon.AdaptivePool.Core/Internals/AdaptivePool.cs` only.
- **Commit:** `d638ea1` (the same Task 2 commit; this was a design refinement during the task, not a separate fix).

**2. [Rule 2 — Missing critical functionality] WaitBehavior.Throw must throw immediately when pressure says no-grow**

- **Found during:** Task 2 design.
- **Issue:** Phase 1 logic was "loop CAS until MaxSize, then throw or wait." Phase 2 short-circuits via PressureSampler — but if pressure says no-grow AND WaitBehavior=Throw, we must throw, not park (Phase 1 contract: Throw means "never block"). My initial draft had a redundant double-check; cleaned up to a single fork.
- **Fix:** After the grow-attempt block, an unconditional `if (WhenExhausted == Throw) throw new PoolExhaustedException(MaxSize);` precedes the wait branch. Behavior change vs Phase 1: with high `GrowOnWaiterCount` (>1) + `WhenExhausted=Throw`, callers throw earlier than they would have in Phase 1. This is the elastic contract (CONTEXT D-01: configurable tolerance). Phase 1 tests pass because default GrowOnWaiterCount=1 + waiters+1 means ShouldGrow always trips for the first slow-path caller.
- **Files modified:** none beyond Task 2's planned set.
- **Commit:** `d638ea1`.

**3. [Rule 3 — Blocking] `dotnet test --project` carry-forward (already documented in Phase 1 + Plan 02-01)**

- **Found during:** Task 2/3 verify steps.
- **Issue:** MTP `--report-trx` injection bug (Phase 1 deviation #5).
- **Fix:** Direct DLL execution: `dotnet tests/Oragon.AdaptivePool.Core.Tests/bin/Debug/<tfm>/Oragon.AdaptivePool.Core.Tests.dll`. Same workaround as prior plans.
- **Files modified:** none.
- **Commit:** N/A.

### Out-of-Scope Findings (NOT fixed)

- The 6 SourceLink "no remote" warnings carry forward — local-only, resolves automatically in CI where `actions/checkout@v4` configures origin. Same as Phase 1 + Plan 02-01.
- Phase 1 SUMMARY listed 70 tests; runner reports 76. Already explained in Plan 02-01 SUMMARY (xUnit `[Theory]` row expansion). Orthogonal to Phase 2 work.

### Authentication Gates

None.

## Heads-up to Plan 03

Plan 03 inherits a fully-wired engine. Specific probes available:

1. **Pressure-grow telemetry assertions.** Plan 03 tests can assert against:
   - `pool.grow.count` counter increments with `pool.name` tag (via `MetricCollector<long>`).
   - `Pool.Grow` ActivitySource span with `pool.size_after`, `grow.tripped_by_waiters`, `grow.tripped_by_utilization`, `grow.tripped_by_p95` tags + `outcome=grew` (via `ActivityListener` on source `"Oragon.AdaptivePool"`).
   - `Grew` log entry (EventId=1005) at Information level with primitive args.
   - `pool.acquire.wait.duration` histogram receiving the parked wait duration on every slow-path entry.

2. **Shrink telemetry assertions.** After driving `FakeTimeProvider` past `IdleTimeout + ShrinkCooldownWindows × SweepInterval`:
   - `pool.shrink.count` counter increment with `pool.name` tag.
   - `Pool.Shrink` ActivitySource span with `pool.size_before`, `pool.size_after`, `outcome=shrunk`.
   - `Shrunk` log (EventId=1006) at Information with `(poolName, oldTotal, newTotal)`.
   - At most 1 shrink per sweep tick (gentle decay invariant).

3. **Sweep + backoff state machine.** Plan 03 tests use `pool.Sweeper.TickCompleted` to await a tick, then assert:
   - `pool.sweep.duration` histogram populated with positive seconds.
   - `Pool.Sweep` ActivitySource span with `outcome=healthy|unhealthy` + child `item-checked` ActivityEvents.
   - `_backoff.CurrentInterval` doubles after 3 consecutive >=50% Unhealthy ticks; resets to base on first clean tick.
   - `SweepFailureBackoff` log (EventId=1009) fires exactly when interval ACTUALLY increases (not on resets).

4. **Anchor stress test for Phase 2.** The CONTEXT.md "burst → idle → burst cycle" anchor test is now buildable: drive a burst of 256+ concurrent acquires (forces `byWaiters` trip), advance `FakeTimeProvider` by `60s + 90s` to elapse `IdleTimeout` + `ShrinkCooldownWindows × SweepInterval`, verify pool size returned to MinSize via `pool.CurrentTotal`, then re-burst and verify the pool grows back without losing waiters.

5. **Health-check failure storm test.** Wire a `Check` hook that throws on every invocation. After `pool.Sweeper.TickCompleted × 3`, verify `_backoff.CurrentInterval` doubled (60s → 60s → 60s → 120s after the 3rd window), then make Check return Healthy and verify `_backoff.CurrentInterval == _backoff.BaseInterval` after the next clean tick. `pool.health.failures` counter must equal `Idle.Count × 3` over the failure window.

6. **PublicAPI is untouched.** Plan 03 should not need to add any new PublicAPI entries — all Phase 2 behavior lives in `internal` types reached via `[InternalsVisibleTo]`.

## Commits

| Task | Hash      | Message |
| ---- | --------- | ------- |
| 1    | `d9deac5` | feat(02-02): extend telemetry surface with ActivitySource + 5 instruments + 7 LoggerMessage entries |
| 2    | `d638ea1` | feat(02-02): composite-signal grow path + wait-duration histogram + Acquire/Release spans |
| 3    | `26aebab` | feat(02-02): replace BackgroundSweeper stub with health-check + shrink + backoff body |

## Self-Check: PASSED

- All 5 modified files reflect documented changes (verified via `git diff`):
  - `src/Oragon.AdaptivePool.Core/Telemetry/PoolMeterNames.cs` ✓
  - `src/Oragon.AdaptivePool.Core/Telemetry/TelemetryEmitter.cs` ✓
  - `src/Oragon.AdaptivePool.Core/Telemetry/PoolDiagnosticsLog.cs` ✓
  - `src/Oragon.AdaptivePool.Core/Internals/AdaptivePool.cs` ✓
  - `src/Oragon.AdaptivePool.Core/Internals/BackgroundSweeper.cs` ✓
- All 3 task commits exist in `git log` (`d9deac5`, `d638ea1`, `26aebab`) — verified.
- `dotnet build` exits 0 (4 projects, 0 errors, 6 carry-forward Phase 1 SourceLink warnings).
- 76 tests × 3 TFMs (228 invocations, 0 failures).
- PingPongStressTest × 3 TFMs (3 invocations, 0 failures, ≤ 317 ms each).
- `grep -c "EventId = " PoolDiagnosticsLog.cs` = 11 (4 Phase 1 + 7 new) — exact match.
- `grep -c TryGrowAsync AdaptivePool.cs` = 2 (decl + call) ✓
- `grep -c "_waitHistogram.Record" AdaptivePool.cs` = 2 (success + error path) ✓
- `grep -c "_telemetry.OnGrow" AdaptivePool.cs` = 2 (TryGrowAsync + GrowAndHandoffAsync) ✓
- `grep -c RunSweepTickAsync BackgroundSweeper.cs` = 2 (decl + call) ✓
- `grep -c OnSweepResult BackgroundSweeper.cs` = 2 ✓
- `grep -c SweepCompleted BackgroundSweeper.cs` = 2 ✓
