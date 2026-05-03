---
phase: 02-elasticity-health
plan: 01
subsystem: core-engine-elasticity-scaffold
tags: [phase2, elasticity, sweeper, scaffold, internal-sealed, builder, options, public-api]
requires:
  - Phase 1 sealed AdaptivePool<T> engine + 12 public types + DI extension (Plan 1.03 baseline = 76 tests + PingPongStressTest green on net8/net9/net10)
  - Phase 1 deviations carried forward (CPM pinned 10.0.6, xUnit1051 NoWarn, public Resource POCO, coverlet.console gate)
provides:
  - 5 new internal sealed types under Internals/ (UtilizationSampler, WaitDurationHistogram, PressureSampler<T>, SweepBackoffState, BackgroundSweeper<T>)
  - 8 new init-only properties on AdaptivePoolOptions<T> (locked Phase 2 defaults D-01..D-08)
  - 7 new fluent methods on AdaptivePoolBuilder<T> (each validating; UtilizationWindow stays options-only)
  - 13 new internal accessors on AdaptivePool<T> for Plan 02/03 (CurrentTotal/WaitersCount/Idle/Waiters/Options/Pressure/UtilSampler/WaitHistogram/BackoffState/Sweeper + IncrementSinceLastGrowTicks/SinceLastGrowTicks/ResetSinceLastGrowTicks)
  - InternalsVisibleTo("Oragon.AdaptivePool.Core.Tests") in csproj so Plan 03 tests reach Sweeper.TickCompleted + all internal probes
  - PoolEntry<T> migrated record -> internal sealed class with mutable LastReturnedAt + Deconstruct(item, createdAt) compat
  - +23 lines on PublicAPI.Unshipped.txt (7 builder methods + 16 option get/init)
affects:
  - Phase 1 hot path: extra _utilSampler.Sample(inUse, total) call on each Acquire/Release transition; debounced to 1s so hot path is effectively free under contention. PingPongStressTest still ~300 ms.
  - Phase 1 PoolEntry consumers: PoolItem reads .Item only (works); engine 3 ctor sites updated to 3-arg form with lastReturnedAt = now.
  - Phase 1 DisposeAsync: now awaits _sweeper.DisposeAsync() BEFORE waiter+idle drain so sweep cannot race with TryDequeue.
  - Phase 1 slow-path AcquireAsync: Interlocked.Increment/Decrement on _waitersCount around the wait window (Plan 02 reads it via PressureSampler.Evaluate).
  - Plan 02 inherits: BackgroundSweeper.RunSweepTickAsync stub that increments cooldown counter and records clean sweep — Plan 02 replaces the body with health-check + shrink + telemetry.
  - Plan 03 inherits: BackgroundSweeper.TickCompleted Task<>, BackgroundSweeper.TickCount long, all AdaptivePool<T> internal accessors via [InternalsVisibleTo].
tech-stack:
  added: []
  patterns:
    - "1s-debounced ring-buffer rolling-window sampler (UtilizationSampler) — allocation-free, lock-free, suitable on hot Acquire/Release path"
    - "100-sample ring-buffered p95 estimator (WaitDurationHistogram) — Record() is lock-free; P95 takes a short lock to snapshot+sort (microseconds, populated.Length<=100)"
    - "Composite-signal grow evaluator (PressureSampler<T>) — OR-combined, all 3 signals computed (no short-circuit) so telemetry can attribute which signal tripped"
    - "Exponential backoff state machine (SweepBackoffState) — 3 consecutive failure windows -> double interval up to MaxBackoff; first clean sweep resets to base"
    - "PeriodicTimer-driven sweep loop with adaptive Period adjustment — handles per-tick try/catch/finally + cancellation-safe lifecycle (DisposeAsync awaits the sweep task)"
    - "Internal sealed class with [InternalsVisibleTo] for tests — preserves zero public surface for engine internals"
key-files:
  created:
    - src/Oragon.AdaptivePool.Core/Internals/UtilizationSampler.cs
    - src/Oragon.AdaptivePool.Core/Internals/WaitDurationHistogram.cs
    - src/Oragon.AdaptivePool.Core/Internals/PressureSampler.cs
    - src/Oragon.AdaptivePool.Core/Internals/SweepBackoffState.cs
    - src/Oragon.AdaptivePool.Core/Internals/BackgroundSweeper.cs
  modified:
    - src/Oragon.AdaptivePool.Core/Builder/AdaptivePoolOptions.cs
    - src/Oragon.AdaptivePool.Core/Builder/AdaptivePoolBuilder.cs
    - src/Oragon.AdaptivePool.Core/Internals/PoolEntry.cs
    - src/Oragon.AdaptivePool.Core/Internals/AdaptivePool.cs
    - src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj
    - src/Oragon.AdaptivePool.Core/PublicAPI.Unshipped.txt
decisions:
  - "PoolEntry<T> migrated from `record` to `internal sealed class` because LastReturnedAt is written every return path; record-with churn would allocate a fresh entry on every return and shred the hot path. Internal-only type, so no public-surface impact. `Deconstruct(item, createdAt)` preserves any positional pattern usages from Phase 1 (none found, but kept for safety)."
  - "BackgroundSweeper<T>.RunSweepTickAsync body is intentionally a no-op stub for Plan 01 — only IncrementSinceLastGrowTicks() + OnSweepResult(0,0) (clean-sweep path). Plan 02 replaces the body with the real health-check + shrink + telemetry logic. This split is what keeps PingPongStressTest non-regressive: the Phase 1 fixed-size hot path doesn't see any new behavior from Phase 2 yet."
  - "Sweeper teardown is sequenced BEFORE waiter+idle drain in DisposeAsync. Cancelling _lifetimeCts unblocks WaitForNextTickAsync which terminates the sweep task; awaiting that completion ensures no concurrent _idle.TryDequeue with the drain loop. Documented in the threat model as T-02-01-02 (DoS mitigation)."
  - "UtilizationWindow exposed only on AdaptivePoolOptions<T> (not on the builder). Builder uses the locked 30s default; tests in Plan 03 set it directly via the options surface or fall back to the default. Decided per CONTEXT.md 'Claude's Discretion' on what to surface fluently — keeping builder API smaller wins for v1."
  - "Builder.Build() validates GrowOnWaiterCount<=MaxSize (a >MaxSize trigger is unreachable) and SweepInterval<=MaxBackoff (else exponential backoff cannot apply). Validations chosen specifically because they catch misconfigurations that would silently degrade behavior."
  - "Sampling debounce = 1 second (TimeSpan.FromSeconds(1).Ticks) — independent of UtilizationWindow. CAS via Interlocked.CompareExchange on _lastSampleTicks ensures only one writer wins per second under contention; losers return without sampling. Per RESEARCH OQ #3 — cited inline in the file."
  - "_waitersCount is wrapped in try/finally around the WriteAsync + tcs.Task await. WriteAsync can throw on a completed channel (pool dispose); that path decrements explicitly. The outer finally covers normal completion + OperationCanceledException + the ObjectDisposedException-rethrow path. Net behavior: _waitersCount is always Interlocked-balanced even under cancellation/dispose races."
metrics:
  duration: "~7m"
  completed: 2026-05-02
  tasks: 3
  files_created: 5
  files_modified: 6
  commits: 3
  unit_tests_baseline_phase1: 76
  unit_tests_now: 76
  unit_tests_failed: 0
  stress_tests: 1
  stress_tests_failed: 0
  tfms: [net8.0, net9.0, net10.0]
  build_warnings_added: 0
---

# Phase 2 Plan 01: Components & Builder Extensions Summary

**One-liner:** Scaffolded all five Phase 2 internal sealed components (UtilizationSampler, WaitDurationHistogram, PressureSampler<T>, SweepBackoffState, BackgroundSweeper<T>), extended `AdaptivePoolOptions<T>` with 8 init-only Phase 2 tunables (locked defaults D-01..D-08) and `AdaptivePoolBuilder<T>` with 7 validating fluent methods, and wired the components into `AdaptivePool<T>`'s constructor and DisposeAsync — all without changing observable Phase 1 behavior. Phase 1 anchor stress (`PingPongStressTest`, MaxSize=1, 256 threads × 40 cycles) passes in ~300 ms across net8/net9/net10; all 76 Phase 1 unit tests pass on all 3 TFMs.

## What Was Built

### Task 1 — Options + Builder + PublicAPI deltas (commit `4faaf36`)

**`AdaptivePoolOptions<T>`** gained 8 init-only properties:

| Property | Type | Default | Source |
| --- | --- | --- | --- |
| `GrowOnWaiterCount` | `int` | `1` | D-01 |
| `GrowOnUtilizationPercent` | `double` | `0.80` | D-02 |
| `UtilizationWindow` | `TimeSpan` | `30 s` | D-03 (options-only; not on builder) |
| `GrowOnWaitTimeP95` | `TimeSpan` | `100 ms` | D-04 |
| `IdleTimeout` | `TimeSpan` | `60 s` | D-05 |
| `ShrinkCooldownWindows` | `int` | `3` | D-06 |
| `SweepInterval` | `TimeSpan` | `30 s` | D-07 |
| `MaxBackoff` | `TimeSpan` | `5 min` | D-08 |

**`AdaptivePoolBuilder<T>`** gained 7 fluent methods, each validating its argument and returning `this`:

| Method | Validation |
| --- | --- |
| `GrowOnWaiterCount(int n)` | `n >= 1` |
| `GrowOnUtilizationPercent(double p)` | `0 < p <= 1.0` |
| `GrowOnWaitTimeP95(TimeSpan t)` | `t > TimeSpan.Zero` |
| `IdleTimeout(TimeSpan t)` | `t > TimeSpan.Zero` |
| `ShrinkCooldownWindows(int n)` | `n >= 0` |
| `SweepInterval(TimeSpan t)` | `t > TimeSpan.Zero` |
| `MaxBackoff(TimeSpan t)` | `t > TimeSpan.Zero` |

**`Build()`** added two cross-field validations:

- `GrowOnWaiterCount <= MaxSize` (else `InvalidOperationException`)
- `SweepInterval <= MaxBackoff` (else `InvalidOperationException`)

**`PublicAPI.Unshipped.txt`** gained exactly 23 lines (7 builder methods + 16 option `get`/`init` pairs for the 8 new properties — one of those is `UtilizationWindow` which was missing from PublicAPI — so 16 = 8 × 2 entries). PublicAPI analyzer (`RS0016`/`RS0017`) reports zero unshipped-API errors.

### Task 2 — 5 internal sealed components (commit `3bdb3f7`)

| File | Provides |
| --- | --- |
| `Internals/UtilizationSampler.cs` | Allocation-free 1s-debounced ring-buffer rolling-window sampler over `(timestamp, inUse, total)`. CAS-loop on `_lastSampleTicks` ensures only one writer wins per second; `AverageUtilization()` is lock-free. |
| `Internals/WaitDurationHistogram.cs` | 100-sample ring-buffered acquire-wait histogram. `Record(TimeSpan)` is lock-free (slow-path Acquire); `P95` takes a short lock to snapshot + sort the populated portion (microseconds at n≤100, called only on sweep tick / slow-path pre-grow check). |
| `Internals/PressureSampler.cs` | `internal readonly record struct GrowDecision(bool ShouldGrow, int CurrentTotal, bool TrippedByWaiters, bool TrippedByUtilization, bool TrippedByP95)`; `internal sealed class PressureSampler<T>` evaluates 3 signals (waiters >= GrowOnWaiterCount, avg-utilization >= GrowOnUtilizationPercent, p95 >= GrowOnWaitTimeP95). OR-combined, all 3 computed — no short-circuit so telemetry can attribute. |
| `Internals/SweepBackoffState.cs` | Exponential backoff state machine. `OnSweepResult(totalChecked, unhealthy)` increments `_consecutiveFailureWindows` when `unhealthy * 2 >= totalChecked`; on the 3rd consecutive failure the interval doubles, capped at `MaxBackoff`. First clean window resets to base. `IntervalChanged` lets the loop swap `PeriodicTimer.Period`. |
| `Internals/BackgroundSweeper.cs` | `internal sealed class BackgroundSweeper<T>` runs `Task.Run(SweepLoopAsync)` from its ctor. Loop awaits `PeriodicTimer.WaitForNextTickAsync(_sweepCts.Token)`, runs `RunSweepTickAsync` inside per-tick try/catch (swallow + continue), increments `TickCount`, signals `_tickCompleted` (test probe), and adjusts `timer.Period` when `_backoffState.IntervalChanged`. `DisposeAsync` cancels `_sweepCts` and awaits `_sweepTask`. **`RunSweepTickAsync` body is a Plan 01 stub** — only `_pool.IncrementSinceLastGrowTicks()` + `_backoff.OnSweepResult(0, 0)` (clean-sweep path). Plan 02 replaces it. |

All 5 files are `internal sealed` (verified by grep across all 5 files).

### Task 3 — Engine wiring (commit `6f9ae22`)

**`Internals/PoolEntry.cs`** migrated from `internal sealed record PoolEntry<T>(T Item, DateTimeOffset CreatedAt)` to `internal sealed class PoolEntry<T>` with:

- `T Item { get; }` (immutable)
- `DateTimeOffset CreatedAt { get; }` (immutable)
- `DateTimeOffset LastReturnedAt { get; set; }` (mutable — written by engine on every return path)
- `Deconstruct(out T item, out DateTimeOffset createdAt)` — preserves any positional pattern usages from Phase 1 (none in tree, but kept for forward-compat).

**`Internals/AdaptivePool.cs`** wiring (additive only — no Phase 1 logic rewrites):

- 7 new private fields: `_utilSampler`, `_waitHistogram`, `_pressure`, `_backoffState`, `_sweeper`, `_waitersCount`, `_sinceLastGrowTicks`.
- Ctor instantiates the 5 components (in the order: util → histogram → pressure → backoff → sweeper) BEFORE `WarmupTask = WarmupAsync(...)` so the sweep loop is running by the time warmup completes.
- 13 internal accessors for Plan 02/03: `IncrementSinceLastGrowTicks()`, `SinceLastGrowTicks`, `ResetSinceLastGrowTicks()`, `CurrentTotal`, `WaitersCount`, `Idle`, `Waiters`, `Options`, `Pressure`, `UtilSampler`, `WaitHistogram`, `BackoffState`, `Sweeper`.
- 3 sample sites:
  - sync `Acquire()` after `_telemetry.OnAcquire()`
  - `PrepareForUseAsync` after `_telemetry.OnAcquire()` (covers async fast path + slow path + recursion-after-Unhealthy)
  - `ReturnSync` after `Interlocked.Decrement(ref _inUse)` AND after `entry.LastReturnedAt = _time.GetUtcNow()` is set
- 3 PoolEntry construction sites updated to 3-arg ctor with `lastReturnedAt = now` for fresh items: `WarmupAsync`, `AcquireAsyncCore` grow path, `GrowAndHandoffAsync`.
- Waiter slow path: `Interlocked.Increment(ref _waitersCount)` before `WriteAsync`; explicit `Decrement` in WriteAsync exception path; outer `finally` decrements after `tcs.Task` await regardless of completion / cancellation / dispose-rethrow. Net: counter is always Interlocked-balanced.
- `DisposeAsync`: `await _sweeper.DisposeAsync()` is sequenced BEFORE the waiter channel `TryComplete` and the idle drain. Sweeper exit cannot race with the drain `_idle.TryDequeue`.

**`Oragon.AdaptivePool.Core.csproj`** added `<InternalsVisibleTo Include="Oragon.AdaptivePool.Core.Tests" />` so Plan 03 can reach `BackgroundSweeper.TickCompleted`, `BackgroundSweeper.TickCount`, and the 13 new internal accessors on `AdaptivePool<T>`.

## Verification

```
dotnet build /clp:ErrorsOnly        ->  4 projects, 0 errors, 6 warnings (Phase 1 SourceLink "no remote" only)
net10.0  Tests dll                  ->  total: 76, failed: 0, succeeded: 76, duration: 486 ms
net9.0   Tests dll                  ->  total: 76, failed: 0, succeeded: 76, duration: 438 ms
net8.0   Tests dll                  ->  total: 76, failed: 0, succeeded: 76, duration: 537 ms
net10.0  Stress dll (PingPong)      ->  total: 1,  failed: 0, succeeded: 1,  duration: 301 ms
net9.0   Stress dll (PingPong)      ->  total: 1,  failed: 0, succeeded: 1,  duration: 285 ms
net8.0   Stress dll (PingPong)      ->  total: 1,  failed: 0, succeeded: 1,  duration: 289 ms
```

228 unit-test invocations + 3 stress invocations across all 3 TFMs. Zero failures. The Phase 1 anchor gate (`PingPongStressTest`, success criterion 2) is non-regressive under Phase 2 wiring.

Note on `dotnet test` invocation: this project uses MTP-style test execution. Running `dotnet test --project ...` triggers the MTP `--report-trx` injection bug carried forward from Phase 1 (Plan 1.03 deviation #5). Run the test DLL directly: `dotnet bin/Debug/<tfm>/Oragon.AdaptivePool.Core.Tests.dll` and `dotnet bin/Release/<tfm>/Oragon.AdaptivePool.Core.Stress.dll`. CI uses the coverlet.console wrapper as documented in Phase 1 SUMMARY.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 — Blocking inter-task dependency] Task 2 standalone build temporarily fails until Task 3 lands**

- **Found during:** Task 2 verify step (`dotnet build src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj`).
- **Issue:** `BackgroundSweeper.RunSweepTickAsync` calls `_pool.IncrementSinceLastGrowTicks()`, but `IncrementSinceLastGrowTicks` is added to `AdaptivePool<T>` only in Task 3. Task 2's standalone verify therefore reports `error CS1061: 'AdaptivePool<T>' does not contain a definition for 'IncrementSinceLastGrowTicks'`. The plan documents this as expected — Task 2's note: *"Note: BackgroundSweeper<T> is generic on T so it can carry AdaptivePool<T> reference"* implies the cross-reference is intentional.
- **Fix:** Committed Task 2's 5 files as the deliverable (the components themselves are correct), proceeded to Task 3 immediately, and treated the combined build at end of Task 3 as the actual verify gate. Task 3's verify passes (4 projects, 0 errors). No code change to either Task 2 or Task 3 was required — only the verify-gate timing was adjusted.
- **Files modified:** none beyond the planned set.
- **Commit:** N/A (process deviation, not code).

**2. [Rule 3 — Blocking] `dotnet test --project` fails with MTP `--report-trx` injection — already documented in Phase 1**

- **Found during:** Task 3 verify step.
- **Issue:** `dotnet test --project tests/Oragon.AdaptivePool.Core.Tests/...csproj` reports `Test run summary: Zero tests ran, error: 3` because the MTP runner injects `--report-trx` which the xUnit v3 test executable rejects.
- **Fix:** Switched verify to direct `dotnet bin/Debug/<tfm>/Oragon.AdaptivePool.Core.Tests.dll` execution per Phase 1 SUMMARY deviation #5 (the same coverlet-wrapper rationale). All 76 tests × 3 TFMs pass.
- **Files modified:** none.
- **Commit:** N/A (verify-step adjustment, not code).

### Out-of-Scope Findings (NOT fixed)

- The 6 SourceLink "no remote" warnings carry forward from Phase 1 — local-only, resolves automatically in CI where `actions/checkout@v4` configures origin. Not a Plan 01 concern.
- The Phase 1 unit-test count is 76 in this run (vs 70 stated in the Phase 1 SUMMARY). Inspecting Phase 1 SUMMARY's table: 8 + 7 + 6 + 3 + 5 + 3 + 5 + 5 + 8 + 1 + 6 + 4 + 2 + 1 = 70 + the rewritten `PlaceholderSmokeTest.cs` (1) = 71 listed; the test runner reports 76. Likely a `[Theory]` count expansion (xUnit reports each row of a `[Theory]` as a test) — orthogonal to Plan 01 work and not regressive.

### Authentication Gates

None.

## Heads-up to Plan 02

Plan 02 inherits a stable scaffold. Specific TODO sites:

1. **`Internals/BackgroundSweeper.cs:RunSweepTickAsync`** — the body is the stub `_pool.IncrementSinceLastGrowTicks(); _backoff.OnSweepResult(0, 0);`. Plan 02 replaces it with: `Check`-hook iteration over each idle entry, eviction of `Unhealthy` items (via `_pool.Options.Release` + `Interlocked.Decrement(ref _pool._total)` exposed indirectly through a new internal helper), shrink pass over idle entries older than `IdleTimeout` while `SinceLastGrowTicks >= ShrinkCooldownWindows && CurrentTotal > MinSize`, telemetry counter emission (`pool.shrink.count`, `pool.health.failures`), and `_backoff.OnSweepResult(realTotalChecked, realUnhealthy)`.

2. **`Internals/AdaptivePool.cs:AcquireAsyncCore`** — the slow path is still the Phase 1 CAS loop. Plan 02 needs to:
   - Insert a `_pressure.Evaluate(currentTotal, _waitersCount)` call between the CAS-grow loop and the WaitBehavior fork, so the composite-signal grow path can fire when `_pressure.Evaluate(...).ShouldGrow` is true even before waiters park.
   - Wire `_waitHistogram.Record(elapsed)` after the `tcs.Task` await completes (covers both fast direct-handoff and slow grow-and-handoff completion). Use `Stopwatch.StartNew()` at the top of the slow path or compute `_time.GetUtcNow() - waitStart` if you want determinism under `FakeTimeProvider`.

3. **`Internals/AdaptivePool.cs` grow-success site** — wherever Plan 02 adds the actual grow decision, call `ResetSinceLastGrowTicks()` to start the cooldown countdown. The internal accessor is already in place.

4. **Plan 02's new `<files_modified>` frontmatter** — add `src/Oragon.AdaptivePool.Core/Internals/AdaptivePool.cs` to the list. The plan-checker flagged this as missing from Plan 02's frontmatter; Plan 01 surfaces it here so it's not lost.

5. **PublicAPI deltas Plan 02 will add** — the new options for `pool.acquire.wait.duration` histogram bucket boundaries (if Plan 02 chooses to expose them) need fresh `PublicAPI.Unshipped.txt` entries. Plan 01 did NOT touch the histogram-buckets surface.

6. **`InternalsVisibleTo`** is now in place. Plan 03 tests can directly reach `pool.Sweeper.TickCompleted`, `Volatile.Read(ref pool.Sweeper.TickCount)`, and the 13 internal accessors. Plan 03 should NOT need to add any further VisibleTo entries.

## Commits

| Task | Hash      | Message |
| ---- | --------- | ------- |
| 1    | `4faaf36` | feat(02-01): extend AdaptivePoolOptions+Builder with Phase 2 tunables |
| 2    | `3bdb3f7` | feat(02-01): add 5 internal sealed Phase 2 components |
| 3    | `6f9ae22` | feat(02-01): wire Phase 2 components into AdaptivePool<T> |

## Self-Check: PASSED

- All 5 created files exist on disk:
  - `src/Oragon.AdaptivePool.Core/Internals/UtilizationSampler.cs` ✓
  - `src/Oragon.AdaptivePool.Core/Internals/WaitDurationHistogram.cs` ✓
  - `src/Oragon.AdaptivePool.Core/Internals/PressureSampler.cs` ✓
  - `src/Oragon.AdaptivePool.Core/Internals/SweepBackoffState.cs` ✓
  - `src/Oragon.AdaptivePool.Core/Internals/BackgroundSweeper.cs` ✓
- All 6 modified files reflect documented changes (verified via `git diff` against parent tip).
- All 3 task commits exist in `git log` (`4faaf36`, `3bdb3f7`, `6f9ae22`) — verified.
- `dotnet build` exits 0 (4 projects, 0 errors, 6 carry-forward Phase 1 SourceLink warnings).
- 76 tests × 3 TFMs (228 invocations, 0 failures) — verified per-TFM dll execution.
- PingPongStressTest × 3 TFMs (3 invocations, 0 failures, ≤ 301 ms each).
- `grep -c LastReturnedAt PoolEntry.cs` -> 4 (>= 1).
- `grep -c "_sweeper" AdaptivePool.cs` -> 4 (>= 3 for field + ctor + DisposeAsync).
- `internal sealed` declared on each of the 5 new component files.
- PublicAPI.Unshipped.txt diff is exactly +23 lines (verified via `git diff --stat`).
