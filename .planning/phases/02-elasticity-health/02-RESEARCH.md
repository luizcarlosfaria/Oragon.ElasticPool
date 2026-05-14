# Phase 2: Elasticity & Health — Research

**Researched:** 2026-05-02
**Domain:** .NET adaptive object pool — composite-signal elasticity, hysteretic shrink, background health sweep, full ActivitySource + counters + structured logging telemetry, deterministic testing
**Confidence:** HIGH

## Summary

Phase 2 layers four cooperating subsystems onto the proven Phase 1 fixed-size engine: a **utilization sampler** (ring-buffer over time-bucketed samples), a **`PeriodicTimer`-driven sweep loop** (`TimeProvider`-injected for tests, with adaptive backoff under failure storms), a **composite-signal grow evaluator** (waiter-queue depth OR sustained utilization OR p95 acquire-wait, OR-combined per CONTEXT decisions), and a **hysteretic shrink** (cooldown counter decremented per tick since last grow, gentle 1-item-per-tick decay). All four hang off the existing `ElasticPool<T>` engine without modifying its public surface. Telemetry expands from 2 counters in Phase 1 to a full set: 5 ActivitySource spans (`Acquire`, `Release`, `HealthCheck`, `Grow`, `Shrink`), 4 new counters + 1 histogram (`pool.acquire.wait.duration`), and 6 new `[LoggerMessage]` source-generated entries.

The architectural shape is **deliberately conservative**: every new internal type (`UtilizationSampler`, `BackgroundSweeper`, `PressureSampler`) is `internal sealed`, exposes a small surface, takes `TimeProvider` and the existing `ElasticPoolOptions<T>` record, and is constructed once from the pool ctor. The `Channel<TCS>` direct-handoff waiter queue from Phase 1 stays — grow decisions reserve slots via the existing `Interlocked.CompareExchange(ref _total, ...)` CAS pattern. The sweep loop runs as a single long-running `Task.Run(SweepLoopAsync)` started from the ctor; `DisposeAsync` already cancels `_lifetimeCts`, which terminates the sweep loop naturally.

Determinism is the single most important Phase 2 design constraint. **Every** test that exercises grow/shrink/sweep MUST use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` — no wall-clock-based timing is permitted. The well-known `FakeTimeProvider`+`PeriodicTimer` race (continuation runs on the threadpool, not the test thread) is mitigated by the `await Task.Yield()` pattern between `Advance(...)` calls, and where stronger determinism is needed, by an internal `TaskCompletionSource`-based "sweep tick fired" probe wired only in DEBUG/test builds [VERIFIED: dotnet/runtime#125077].

**Primary recommendation:** Implement four independent `internal sealed` components (`UtilizationSampler`, `PressureSampler`, `BackgroundSweeper`, plus a small `SweepBackoffState`) wired through `ElasticPoolOptions<T>` (extended with the new tunables) and constructed once in the `ElasticPool<T>` ctor. Reuse the Phase 1 CAS-on-`_total` slot reservation for grow; add a `_sinceLastGrowTicks` counter for hysteresis. Test every time-dependent path with `FakeTimeProvider` + `Task.Yield()`; assert telemetry via `MetricCollector<long>` and an `ActivityListener` configured with `Sample = (ref _) => ActivitySamplingResult.AllData`.

## User Constraints (from CONTEXT.md)

### Locked Decisions

**Composite-Signal Grow Thresholds:**
- Waiter queue threshold: default `1` (any wait triggers adaptive grow), configurable via builder fluent: `.GrowOnWaiterCount(int n)`
- Sustained utilization: default ≥ `80%` (HikariCP-style equilibrated), configurable via builder fluent: `.GrowOnUtilizationPercent(double p)`
- Utilization sampling window: `30s` rolling window (balance reactivity vs noise)
- p95 wait time threshold: `100ms` (reasonable latency target for resource pools); configurable via builder fluent `.GrowOnWaitTimeP95(TimeSpan)`
- The three signals combine with **OR** semantics (not AND): grow fires if **any one** threshold is exceeded, respecting `MaxSize` as absolute ceiling

**Shrink Hysteresis & Sweep Cadence:**
- `IdleTimeout` default = `60s` (HikariCP default; idle item discarded after this period down to `MinSize`)
- Cooldown windows after grow before shrinking = `3` windows (with 30s window → ~90s cooldown total — prevents thrashing)
- Shrink batch size = `1 item per sweep tick` (gentle decay; gradual without abrupt oscillation)
- Sweep interval default = `30s` (aligned with utilization window — one grow/shrink decision per window)
- Configurable via builder: `.IdleTimeout(TimeSpan)`, `.ShrinkCooldownWindows(int)`, `.SweepInterval(TimeSpan)`

**Telemetry Tag Schema & Logging:**
- ActivitySource spans on `Acquire`, `Release`, `HealthCheck`, `Grow`, `Shrink` — always with tags `pool.name` + `outcome` (grew/shrunk/healthy/unhealthy/skipped); cardinality bounded (no detailed reason codes that would explode in prod)
- Log level for grow/shrink events = `Information` (rare but operationally significant; visible in prod without noise)
- Span granularity for sweep = 1 span per sweep tick with `events` per item processed (not 1 span per item — cardinality explosion)
- Separate counters: `pool.factory.failures` (already in Phase 1) vs `pool.health.failures` new (sweep + BeforeUse/AfterUse Unhealthy decisions)
- Add counters: `pool.grow.count`, `pool.shrink.count`, `pool.sweep.duration`, `pool.acquire.wait.duration` (histogram)

### Claude's Discretion

- Exact algorithm for composite-signal evaluation (evaluation order of the 3 signals, short-circuit or compute all for telemetry)
- Internal structure of the "utilization sampler" (rolling window via `Channel<sample>` or `Stopwatch`-based?)
- Histogram bucket boundaries for `pool.acquire.wait.duration` (default OTel or custom?)
- Exact cooldown tracking strategy (count-based vs timestamp-based)
- How sweep coordinates with active grow: may skip sweep if grow ran recently OR always run but skip shrink during cooldown
- Exact backoff curve for sweep failures: exponential 30s → 60s → 120s → 5min (cap) per RESEARCH Pitfall, but exact curve may be tuned

### Deferred Ideas (OUT OF SCOPE)

- Quarantine with backoff (v2) — REQUIREMENTS marks as FAIL-V2-01
- AfterUse hook active (v2) — HOOK-V2-01 — in Phase 2 the signature stays, but failure-policy integration is deferred
- Per-item MaxLifetime/MaxUses rotation — LIFECYCLE-V2-01
- Explicit `DrainAsync(TimeSpan)` — LIFECYCLE-V2-02 — Phase 2 keeps only Phase 1's `DisposeAsync()`
- `Microsoft.Extensions.Diagnostics.HealthChecks` integration (auto-register pool as health check) — defer, demand-real-first
- Empirical threshold tuning (benchmark-driven calibration) — defer to Phase 4 or v1.x; v1.0 ships sensible defaults above and exposes configurable APIs

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ELASTIC-01 | Composite-signal grow (waiters + utilization% + p95 wait) respecting `MaxSize` | §"Composite-Signal Grow Decision Algorithm", §"Code Examples" |
| ELASTIC-02 | Hysteretic shrink past `IdleTimeout`, never below `MinSize`, cooldown since last grow | §"Hysteretic Shrink with Cooldown Counter" |
| TELEM-02 | `ActivitySource` spans on `Acquire`/`Release`/`HealthCheck`/`Grow`/`Shrink` with `HasListeners()` guard | §"ActivitySource Spans + HasListeners Guard", §"Code Examples" |
| TELEM-03 | `[LoggerMessage]`-source-generated logging on every state transition | §"Allocation-free [LoggerMessage] Patterns" |
| QUAL-03 | Thread-safety verified via stress tests (hundreds of threads, thousands of cycles) covering grow/shrink/sweep paths | §"Stress Test Design (Burst→Idle→Burst)" |

## Project Constraints (from CLAUDE.md)

No project-local CLAUDE.md was found at the repository root — only the user's global RTK directives apply, which are tooling-level and have no bearing on Phase 2 implementation. The binding project constraints come from PROJECT.md (multi-target net8/net9/net10, async-first, BCL-only Core deps, three-pillar trifecta non-negotiable) and Phase 1 SUMMARY decisions (xUnit1051 NoWarn at test-csproj level; coverlet.console wrapper for coverage; `FakeTimeProvider` is the established time-injection point; `MetricCollector<long>` is the canonical telemetry-test pattern; NSubstitute requires public type-arguments).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Composite-signal grow decision | API/Backend (engine internal) | — | Lives in `ElasticPool<T>` slow path — the engine already owns `_total`/`_inUse`/`_waiters` state; signal evaluation is a pure function over those + samples |
| Utilization rolling window | API/Backend (engine internal) | — | Updated on every Acquire/Release transition; sampled by the grow evaluator and the shrink decider |
| Background sweep loop | API/Backend (engine internal) | — | Owns its own long-running `Task` started from the engine ctor and cancelled by `_lifetimeCts`; never crosses public surface |
| Hysteretic shrink | API/Backend (engine internal) | — | Reads idle queue + cooldown counter set by grow; runs on sweep tick |
| Telemetry emission | API/Backend (engine internal) | — | `TelemetryEmitter` is internal sealed; consumers observe via `Meter` name + `ActivitySource` name only |
| Test orchestration | Test framework | — | xUnit v3 + `FakeTimeProvider`; deterministic time injection — no wall-clock dependencies |

**No client/UI tier exists for this library.** It is a single-package backend primitive consumed by .NET applications. The "tier" question reduces to "engine internal vs public API surface", and every Phase 2 addition is internal. Public surface change is limited to additive `ElasticPoolBuilder<T>` fluent methods (the locked decisions section above).

## Standard Stack

### Core (already pinned in Phase 1 — verified against Directory.Packages.props)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `System.Threading.PeriodicTimer` | BCL net8+ in-box | Drift-free sweep loop | Single-consumer model; `(TimeSpan, TimeProvider)` ctor on net8+ enables `FakeTimeProvider` injection [CITED: learn.microsoft.com/dotnet/api/system.threading.periodictimer] |
| `System.TimeProvider` | BCL net8+ in-box | Time abstraction for tests | Wired through `ElasticPoolOptions<T>.TimeProvider` in Phase 1; `FakeTimeProvider` plugs in directly |
| `System.Diagnostics.ActivitySource` | BCL in-box (net5+) | Distributed tracing spans | Single `internal static readonly` per assembly, never disposed (process lifetime); `HasListeners()` guard skips work when no listener attached [CITED: learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs] |
| `System.Diagnostics.Metrics.Meter` + `IMeterFactory` | BCL net8+ in-box | Counters, histograms, gauges | Phase 1 already wires `IMeterFactory`-or-fallback in `TelemetryEmitter`; Phase 2 just adds instruments |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.6 (CPM) | `ILogger` + `[LoggerMessage]` source-gen | Already pinned; source-gen is allocation-free hot-path logging [CITED: learn.microsoft.com/dotnet/core/extensions/logging/high-performance-logging] |

### Test (already pinned in Phase 1)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `Microsoft.Extensions.TimeProvider.Testing` | 10.5.0 (CPM) | `FakeTimeProvider` — synthetic time | The canonical pattern for testing time-dependent code in .NET 8+ [VERIFIED: nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing — 10.5.0 published 2026-04-15] |
| `Microsoft.Extensions.Diagnostics.Testing` | 10.5.0 (CPM) | `MetricCollector<T>` — counter/histogram capture | The canonical pattern for asserting `Counter<T>`/`Histogram<T>` measurements with tags [VERIFIED: nuget.org/packages/Microsoft.Extensions.Diagnostics.Testing — 10.5.0 published 2026-04-15] |
| `xunit.v3` | 3.2.2 (CPM) | Test framework | Phase 1 standard |
| `AwesomeAssertions` | 9.4.0 (CPM) | Assertions | Phase 1 chose AwesomeAssertions (FluentAssertions v8 commercial license disqualified per project research) |
| `NSubstitute` | 5.3.0 (CPM) | Mocking | Phase 1 standard; requires public POCO type-arguments per Phase 1 SUMMARY |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `Microsoft.Extensions.Diagnostics` | 10.0.6 (CPM, test-side) | `services.AddMetrics()` registers `IMeterFactory` | Already in test csproj per Phase 1 Plan 03; needed for `MetricCollector` + `IMeterFactory` interop |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `PeriodicTimer` | `Task.Delay` loop | Drift: if work takes 1.5s and interval is 30s, real interval is 31.5s → telemetry interpretation breaks. Rejected per ARCHITECTURE.md anti-pattern §3 |
| `PeriodicTimer` | `System.Threading.Timer` | Sync callback in thread-pool thread; awkward for `async` health-check work; risk of overlapping callbacks. Rejected per ARCHITECTURE.md |
| `MetricCollector<T>` | Hand-rolled `MeterListener` | Phase 1 Plan 03 SUMMARY: "MetricCollector<long> + IMeterFactory tagged measurement assertions (vs. ad-hoc MeterListener) — establishes the canonical telemetry-test pattern for Phase 2." `MetricCollector` provides built-in `WaitForMeasurementsAsync(int, TimeSpan)`, snapshot semantics, and tag matching |
| Ring-buffer utilization sampler | `Channel<sample>` rolling window | Channels are FIFO + heap-allocated; ring buffer is allocation-free per sample and the cheapest way to compute "X% over last 30s" |
| Ring-buffer utilization sampler | Exponential-decay scalar | Simpler (one float, one update) but harder to reason about for the "≥80% sustained over 30s" predicate; ring buffer keeps the bucket window literal |
| Cooldown counter | Cooldown timestamp | CONTEXT decision: counter is more deterministic for tests because it advances on a discrete sweep-tick basis, not wall-clock |

**Installation:** No new packages required. All dependencies are already pinned in `Directory.Packages.props` from Phase 1.

**Version verification:**
- `Microsoft.Extensions.TimeProvider.Testing 10.5.0` published 2026-04-15 [VERIFIED: nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing]
- `Microsoft.Extensions.Diagnostics.Testing 10.5.0` published 2026-04-15 [VERIFIED: nuget.org/packages/Microsoft.Extensions.Diagnostics.Testing]
- `Microsoft.Extensions.Logging.Abstractions 10.0.6` already pinned [VERIFIED: Directory.Packages.props]

## Architecture Patterns

### System Architecture (Phase 2 deltas over Phase 1)

```
                        ┌─────────────────────────────────────────────┐
   AcquireAsync ───────▶│             ElasticPool<T>  (sealed)        │
                        │                                              │
                        │   _idle (ConcurrentQueue<PoolEntry>)         │
                        │   _waiters (Channel<TCS>) — direct handoff   │
                        │   _total / _inUse / _sinceLastGrowTicks      │
                        │                                              │
                        │   ┌────────────────────────────────────────┐ │
   sample tick ────────▶│   │   UtilizationSampler  (new, internal) │ │
                        │   │   ring buffer, 30s window, AddSample  │ │
                        │   └─────────────────┬──────────────────────┘ │
                        │                     │                        │
                        │                     ▼                        │
                        │   ┌────────────────────────────────────────┐ │
   Acquire slow path ──▶│   │   PressureSampler  (new, internal)    │ │
   (tcs ready/timeout)  │   │   evaluates 3 signals, OR-combined    │ │
                        │   │   waitDurationHistogram (p95)          │ │
                        │   └─────────────────┬──────────────────────┘ │
                        │                     │ "should grow?"          │
                        │                     ▼                        │
                        │              CAS reserve _total              │
                        │              factory + handoff               │
                        │              record _sinceLastGrowTicks=0    │
                        │                                              │
                        │   ┌────────────────────────────────────────┐ │
                        │   │   BackgroundSweeper  (new, internal)  │ │
                        │   │   PeriodicTimer(SweepInterval, _time) │ │
                        │   │                                        │ │
                        │   │   while (await timer.WaitForNextTickA-│ │
                        │   │     sync(_lifetimeCts.Token)):        │ │
                        │   │     1. health-check pass (Check hook) │ │
                        │   │     2. shrink pass (if cooldown==0)   │ │
                        │   │     3. _sinceLastGrowTicks++          │ │
                        │   │     4. update SweepBackoffState        │ │
                        │   └────────────────────────────────────────┘ │
                        │                                              │
                        │   ┌────────────────────────────────────────┐ │
   span+counter+log ◀───┤   │   TelemetryEmitter  (extended)        │ │
                        │   │   ActivitySource "Oragon.ElasticPool"│ │
                        │   │   Counter: grow/shrink/health.fails   │ │
                        │   │   Histogram: acquire.wait.duration    │ │
                        │   │   Histogram: sweep.duration            │ │
                        │   └────────────────────────────────────────┘ │
                        └─────────────────────────────────────────────┘
                                          ▲
                                          │
                                  PoolDiagnosticsLog (extended,
                                  [LoggerMessage] source-gen)
```

Data flow primary path: a caller's `AcquireAsync` either succeeds fast (idle item) or enters the slow path; the slow path consults `PressureSampler.ShouldGrow()`; on grow, it CAS-reserves a `_total` slot, invokes Factory outside any lock, hands off to a waiter or enqueues to idle, resets the cooldown counter, and emits `Pool.Grow` span + `pool.grow.count` increment + `Grew` log entry. Independently, the sweep loop fires every `SweepInterval` (default 30s) on a `TimeProvider`-driven `PeriodicTimer`; it runs the health-check pass first (invokes `Check` hook on idle items, applies failure policy on Unhealthy, tracks rolling failure rate), then the shrink pass (only if `_sinceLastGrowTicks >= ShrinkCooldownWindows`), then increments the cooldown counter, and emits a single `Pool.Sweep` span with per-item events.

### Recommended Project Structure (additions)

```
src/Oragon.ElasticPool/
├── Internals/
│   ├── ElasticPool.cs              [MODIFIED — wire new components]
│   ├── PoolEntry.cs                 [MODIFIED — add LastReturnedAt]
│   ├── PoolItem.cs                  [unchanged]
│   ├── PoolLifecycle.cs             [unchanged]
│   ├── UtilizationSampler.cs        [NEW — internal sealed]
│   ├── PressureSampler.cs           [NEW — internal sealed]
│   ├── BackgroundSweeper.cs         [NEW — internal sealed]
│   └── SweepBackoffState.cs         [NEW — internal sealed]
├── Builder/
│   ├── ElasticPoolBuilder.cs       [MODIFIED — add 7 fluent methods]
│   └── ElasticPoolOptions.cs       [MODIFIED — add 7 init-only props]
├── Telemetry/
│   ├── TelemetryEmitter.cs          [MODIFIED — add ActivitySource + new instruments]
│   ├── PoolDiagnosticsLog.cs        [MODIFIED — add 6 [LoggerMessage] entries]
│   └── PoolMeterNames.cs            [MODIFIED — add new instrument name constants]
└── Hooks/
    └── HookDelegates.cs             [unchanged — Check signature already exists]

tests/Oragon.ElasticPool.Tests/
└── Pool/
    ├── ElasticGrowTests.cs          [NEW — composite-signal grow]
    ├── HystereticShrinkTests.cs     [NEW — cooldown + IdleTimeout]
    ├── BackgroundSweepTests.cs      [NEW — sweep tick, Check hook invocation]
    ├── SweepBackoffTests.cs         [NEW — adaptive backoff under failure storms]
    ├── UtilizationSamplerTests.cs   [NEW — ring buffer correctness]
    └── ActivitySourceSpanTests.cs   [NEW — span emission per outcome]

tests/Oragon.ElasticPool.Stress/
└── BurstIdleBurstStressTest.cs      [NEW — Phase 2 anchor stress test]
```

### Pattern 1: Drift-Free Sweep Loop with `TimeProvider`-Injected `PeriodicTimer`

**What:** Long-running `Task.Run(SweepLoopAsync)` started in the pool ctor, awaiting `PeriodicTimer.WaitForNextTickAsync(ct)` in a loop. The timer is constructed with `(SweepInterval, _options.TimeProvider)` so that `FakeTimeProvider.Advance(SweepInterval)` triggers the next tick deterministically in tests.

**When to use:** any periodic, single-consumer background task that needs deterministic test coverage.

**Example (verified pattern):**

```csharp
// Source: ARCHITECTURE.md §"Background Sweep Mechanism" + learn.microsoft.com/dotnet/api/system.threading.periodictimer
internal sealed class BackgroundSweeper
{
    private readonly ElasticPool<T> _pool;
    private readonly ElasticPoolOptions<T> _options;
    private readonly CancellationTokenSource _sweepCts;
    private readonly Task _sweepTask;
    private readonly SweepBackoffState _backoff;

    public BackgroundSweeper(ElasticPool<T> pool, ElasticPoolOptions<T> options, CancellationToken lifetimeToken)
    {
        _pool = pool;
        _options = options;
        _sweepCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _backoff = new SweepBackoffState(options.SweepInterval, options.MaxBackoff);
        _sweepTask = Task.Run(SweepLoopAsync);
    }

    private async Task SweepLoopAsync()
    {
        // Use TimeProvider overload (net8+, in-box) so FakeTimeProvider plugs in.
        using var timer = new PeriodicTimer(_backoff.CurrentInterval, _options.TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(_sweepCts.Token).ConfigureAwait(false))
            {
                try
                {
                    var sweepStart = _options.TimeProvider.GetTimestamp();
                    var healthOutcome = await RunHealthCheckPassAsync(_sweepCts.Token).ConfigureAwait(false);
                    if (_pool.SinceLastGrowTicks >= _options.ShrinkCooldownWindows)
                    {
                        await RunShrinkPassAsync(_sweepCts.Token).ConfigureAwait(false);
                    }
                    _pool.IncrementSinceLastGrowTicks();
                    _backoff.OnSuccessOrFailure(healthOutcome);
                    if (_backoff.IntervalChanged)
                    {
                        timer.Period = _backoff.CurrentInterval;  // adjust on backoff/recovery
                    }
                    _pool.RecordSweepDuration(sweepStart);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _pool.Log.SweepFailed(_options.PoolName, ex);
                    // never propagate — sweep failure must not kill the pool
                }
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
    }

    public Task DisposeAsync() { _sweepCts.Cancel(); return _sweepTask; }
}
```

Notes:
- `PeriodicTimer.Period` is **mutable** at runtime [CITED: learn.microsoft.com/dotnet/api/system.threading.periodictimer.period] — this is how we implement adaptive backoff without recreating the timer.
- `WaitForNextTickAsync(ct)` returns `false` when the timer is disposed (or ct cancels with OCE); the loop terminates naturally [CITED: learn.microsoft.com/dotnet/api/system.threading.periodictimer.waitfornexttickasync].
- Single-consumer is enforced: only one `WaitForNextTickAsync` may be in flight per timer [CITED: same].

### Pattern 2: Allocation-Free Ring-Buffer Utilization Sampler

**What:** Time-bucketed circular buffer of `(timestamp, inUse, total)` samples. Each Acquire/Release writes one sample; the grow evaluator computes `mean(sample.inUse / sample.total) over [now - 30s, now]` from the buffer.

**When to use:** rolling window summary statistics with a known fixed time horizon.

**Why this over alternatives:**
- **vs `Channel<sample>`:** Channels allocate per-write and don't support random-access reads. Ring buffer is one heap allocation up front, zero per-sample allocation.
- **vs exponential-decay scalar:** A single decaying float is cheaper but cannot answer "≥80% sustained for 30s" — the question requires examining individual buckets.
- **vs time-bucketed `Dictionary<long, ...>`:** dictionary churn under concurrent writes; ring buffer with `Interlocked.Exchange`-based write head is contention-free for single-writer-per-tick.

Sample shape: with sample interval = 1s and window = 30s, the buffer holds 30 entries. Writer advances a `_writeIndex` (Interlocked); reader walks the buffer skipping entries older than `now - 30s`.

```csharp
// Source: design — verified against ARCHITECTURE.md §"PressureSampler" responsibility
internal sealed class UtilizationSampler
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly long[] _bucketTimestampsTicks;  // -1 = empty
    private readonly int[] _bucketInUse;
    private readonly int[] _bucketTotal;
    private int _writeIndex;
    private readonly TimeSpan _bucketSize;

    public UtilizationSampler(TimeProvider time, TimeSpan window, TimeSpan bucketSize)
    {
        _time = time;
        _window = window;
        _bucketSize = bucketSize;
        var n = (int)Math.Ceiling(window.TotalSeconds / bucketSize.TotalSeconds);
        _bucketTimestampsTicks = new long[n];
        Array.Fill(_bucketTimestampsTicks, -1L);
        _bucketInUse = new int[n];
        _bucketTotal = new int[n];
    }

    public void Sample(int inUse, int total)
    {
        var nowTicks = _time.GetUtcNow().UtcTicks;
        var idx = (int)((uint)Interlocked.Increment(ref _writeIndex) - 1) % _bucketTimestampsTicks.Length;
        Volatile.Write(ref _bucketTimestampsTicks[idx], nowTicks);
        _bucketInUse[idx] = inUse;
        _bucketTotal[idx] = total;
    }

    /// <summary>Average utilization (inUse/total) across all buckets within the window.</summary>
    public double AverageUtilization()
    {
        var nowTicks = _time.GetUtcNow().UtcTicks;
        var cutoffTicks = nowTicks - _window.Ticks;
        long sumInUse = 0, sumTotal = 0;
        for (int i = 0; i < _bucketTimestampsTicks.Length; i++)
        {
            var ts = Volatile.Read(ref _bucketTimestampsTicks[i]);
            if (ts < 0 || ts < cutoffTicks) continue;
            sumInUse += _bucketInUse[i];
            sumTotal += _bucketTotal[i];
        }
        return sumTotal == 0 ? 0.0 : (double)sumInUse / sumTotal;
    }
}
```

Sample-rate decision: the recommendation is to sample on every Acquire/Release transition (so the engine already has the current `_inUse`/`_total` in hand) but cap to 1 sample/sec via `_lastSampleTicks` to keep the bucket count bounded under high churn.

### Pattern 3: OR-Combined Composite-Signal Grow Evaluator

**What:** A pure function `ShouldGrow(state) → GrowDecision` that evaluates the three locked signals (waiter count, utilization%, p95 wait time) and returns a decision tagged with which signal(s) tripped — for telemetry attribution.

**Decision per CONTEXT (locked):** OR semantics. Any one signal exceeding threshold triggers grow. Per CONTEXT discretion, evaluate all three (no short-circuit) and report all triggering signals as span tags / log properties — costs us 3 trivial comparisons per slow-path call but gives operators causal data ("which signal saved them today").

**p95 wait time computation:** maintain a small `Histogram<double>`-style ring buffer of the last N (e.g., 100) acquire-wait durations. p95 from a small sample isn't strictly correct, but it's cheap and matches HikariCP's approach. For test determinism, expose a method that returns the current p95 reading directly so tests can assert deterministic decisions.

```csharp
// Source: design — verified against PITFALLS Pitfall 8 "composite-signal grow" recommendation
internal readonly record struct GrowDecision(
    bool ShouldGrow,
    int CurrentTotal,
    bool TrippedByWaiters,
    bool TrippedByUtilization,
    bool TrippedByP95);

internal sealed class PressureSampler
{
    private readonly ElasticPoolOptions<T> _options;
    private readonly UtilizationSampler _util;
    private readonly WaitDurationHistogram _waitHistogram;

    public GrowDecision Evaluate(int currentTotal, int currentWaiters)
    {
        if (currentTotal >= _options.MaxSize)
            return new GrowDecision(false, currentTotal, false, false, false);

        var byWaiters = currentWaiters >= _options.GrowOnWaiterCount;
        var byUtilization = _util.AverageUtilization() >= _options.GrowOnUtilizationPercent;
        var byP95 = _waitHistogram.P95 >= _options.GrowOnWaitTimeP95;

        return new GrowDecision(
            ShouldGrow: byWaiters | byUtilization | byP95,
            CurrentTotal: currentTotal,
            TrippedByWaiters: byWaiters,
            TrippedByUtilization: byUtilization,
            TrippedByP95: byP95);
    }
}
```

The grow path then performs the existing Phase 1 CAS-on-`_total` reservation, the Factory call outside any lock, the handoff or enqueue, **then** resets `_sinceLastGrowTicks = 0` (Interlocked.Exchange) and emits telemetry.

### Pattern 4: Hysteretic Shrink with Cooldown Counter

**What:** Tracks "windows since last grow" via a single `int` counter `_sinceLastGrowTicks` — incremented at the END of each sweep tick, reset to 0 on every grow. Shrink pass runs ONLY when `_sinceLastGrowTicks >= ShrinkCooldownWindows`. Within shrink, evict at most 1 idle entry per tick, only if its `LastReturnedAt < now - IdleTimeout` AND `_total > MinSize`.

**Why counter not timestamp** (locked CONTEXT decision): counter advances on discrete sweep ticks, so test determinism is obvious — `Advance(SweepInterval)` 3 times → cooldown elapsed. With timestamps, you'd compare `now - lastGrowAt`, but `now` only advances when the test explicitly advances it, AND `lastGrowAt` was captured at grow time → equivalent in the simple case but more brittle when the test advances by less than a full window.

```csharp
// Source: design — verified against PITFALLS Pitfall 7 "elastic algorithm oscillation"
private async ValueTask RunShrinkPassAsync(CancellationToken ct)
{
    if (Volatile.Read(ref _sinceLastGrowTicks) < _options.ShrinkCooldownWindows)
        return;  // still in cooldown — skip silently

    if (Volatile.Read(ref _total) <= _options.MinSize)
        return;  // already at floor

    if (!_idle.TryPeek(out var head)) return;
    var nowUtc = _options.TimeProvider.GetUtcNow();
    if (nowUtc - head.LastReturnedAt < _options.IdleTimeout)
        return;  // head is too fresh to evict — don't bother walking the queue

    // Evict at most ONE entry per tick (gentle decay per CONTEXT).
    if (_idle.TryDequeue(out var entry))
    {
        Interlocked.Decrement(ref _total);
        if (_options.Release is { } release)
        {
            try { await release(entry.Item, ct).ConfigureAwait(false); } catch { /* swallow */ }
        }
        _telemetry.OnShrink();
        _log.Shrunk(_options.PoolName, oldTotal: _total + 1, newTotal: _total);
    }
}
```

Note `PoolEntry<T>` needs `LastReturnedAt` added. `CreatedAt` already exists from Phase 1 but doesn't capture re-entries to the idle queue — a hot item with churn shouldn't be evicted just because it was created 5 minutes ago.

### Pattern 5: Adaptive Sweep Backoff State Machine

**What:** Stateful object that tracks consecutive sweep-pass failures and adjusts the next sweep interval.

**Curve (locked CONTEXT, with discretion on exact shape):** 30s → 60s → 120s → 5min cap. A single failure does NOT trigger backoff (one bad item is normal). Threshold: 3 consecutive sweep passes where ≥50% of `Check` invocations returned Unhealthy or threw. Reset to 30s on the first sweep pass with 0 unhealthy items.

```csharp
// Source: design — verified against PITFALLS Pitfall 5 "health-check sweep amplifies load"
internal sealed class SweepBackoffState
{
    private readonly TimeSpan _baseInterval;
    private readonly TimeSpan _maxBackoff;
    private int _consecutiveFailureWindows;
    private TimeSpan _currentInterval;
    public TimeSpan CurrentInterval => _currentInterval;
    public bool IntervalChanged { get; private set; }

    public SweepBackoffState(TimeSpan baseInterval, TimeSpan maxBackoff)
    {
        _baseInterval = baseInterval;
        _maxBackoff = maxBackoff;
        _currentInterval = baseInterval;
    }

    public void OnSweepResult(int totalChecked, int unhealthy)
    {
        var prev = _currentInterval;
        if (totalChecked == 0)
        {
            // No work done this tick — don't change anything.
            IntervalChanged = false;
            return;
        }
        if (unhealthy * 2 >= totalChecked)  // ≥50% unhealthy
        {
            _consecutiveFailureWindows++;
            if (_consecutiveFailureWindows >= 3)
            {
                // 30s → 60s → 120s → ... up to MaxBackoff cap
                var doubled = TimeSpan.FromTicks(_currentInterval.Ticks * 2);
                _currentInterval = doubled > _maxBackoff ? _maxBackoff : doubled;
            }
        }
        else
        {
            _consecutiveFailureWindows = 0;
            _currentInterval = _baseInterval;
        }
        IntervalChanged = _currentInterval != prev;
    }
}
```

Default `MaxBackoff` = 5 min (per ROADMAP success criterion 3); exposed via `.MaxBackoff(TimeSpan)` builder method.

### Pattern 6: ActivitySource Spans with `HasListeners()` Guard

**What:** `internal static readonly ActivitySource _activitySource = new("Oragon.ElasticPool")` declared once per assembly, never disposed. Every span site is preceded by either a `HasListeners()` check (when there's preparatory work to skip) OR uses the standard `using var activity = _activitySource.StartActivity(...)` pattern (which itself returns null when no listener is registered, so the null-conditional `?.` operator handles the no-listener case at zero cost).

**Why both:** `StartActivity` returns null cheaply when there's no listener [CITED: learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs §"Notes" — "If there are no registered listeners or there are listeners that are not interested, StartActivity() will return null and avoid creating the Activity object. This is a performance optimization so that the code pattern can still be used in functions that are called frequently."]. `HasListeners()` adds value only when you have preparatory work that's expensive (e.g., constructing tag dictionaries, computing fingerprint strings) and you want to skip it entirely.

```csharp
// Source: ARCHITECTURE.md + learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs
internal sealed class TelemetryEmitter
{
    internal static readonly ActivitySource ActivitySource = new("Oragon.ElasticPool");

    public Activity? StartGrowSpan(string poolName, GrowDecision decision)
    {
        // StartActivity returns null when no listener — zero-allocation no-op.
        var activity = ActivitySource.StartActivity("Pool.Grow", ActivityKind.Internal);
        if (activity is null) return null;
        activity.SetTag("pool.name", poolName);
        activity.SetTag("pool.size_after", decision.CurrentTotal + 1);
        activity.SetTag("grow.tripped_by_waiters", decision.TrippedByWaiters);
        activity.SetTag("grow.tripped_by_utilization", decision.TrippedByUtilization);
        activity.SetTag("grow.tripped_by_p95", decision.TrippedByP95);
        return activity;
    }

    public Activity? StartSweepSpan(string poolName)
    {
        if (!ActivitySource.HasListeners()) return null;  // skip even tag prep
        var activity = ActivitySource.StartActivity("Pool.Sweep", ActivityKind.Internal);
        activity?.SetTag("pool.name", poolName);
        return activity;
    }
}
```

For `Pool.Sweep`, attach per-item processing as `ActivityEvent` rather than nested spans (per CONTEXT decision: 1 span per tick, events for items — bounded cardinality):

```csharp
sweepSpan?.AddEvent(new ActivityEvent("item-checked", default,
    new ActivityTagsCollection
    {
        { "result", "unhealthy" }, { "policy", "discard_and_replace" }
    }));
```

### Pattern 7: ActivityListener Test Pattern

**What:** Tests register an `ActivityListener` that captures every started/stopped Activity for the `Oragon.ElasticPool` source. Lifetime is scoped to the test via `using var listener = ...`.

**Source for pattern:** dotnet/runtime test code [VERIFIED: github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.DiagnosticSource/tests/ActivitySourceTests.cs] + Jimmy Bogard "A Lap Around ActivitySource and ActivityListener" [CITED].

```csharp
// Source: standard pattern from dotnet/runtime test code + Jimmy Bogard blog
private sealed class CapturedActivities : IDisposable
{
    public List<Activity> Started { get; } = new();
    public List<Activity> Stopped { get; } = new();
    private readonly ActivityListener _listener;

    public CapturedActivities(string sourceName)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => Started.Add(a),
            ActivityStopped = a => Stopped.Add(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();
}

[Fact]
public async Task GrowEmitsActivitySpan()
{
    using var captured = new CapturedActivities("Oragon.ElasticPool");
    // ... drive the pool to grow ...
    captured.Stopped.ShouldContain(a => a.OperationName == "Pool.Grow");
    var grow = captured.Stopped.Single(a => a.OperationName == "Pool.Grow");
    grow.GetTagItem("grow.tripped_by_waiters").ShouldBe(true);
}
```

### Pattern 8: `MetricCollector<T>` Test Pattern

**What:** Attach a `MetricCollector<long>` to a specific named instrument or to a `Meter` by name; assert measurement count, value, and tag set. `WaitForMeasurementsAsync(int, TimeSpan)` is the canonical way to handle async emission.

**Source:** [CITED: learn.microsoft.com/dotnet/api/microsoft.extensions.diagnostics.metrics.testing.metriccollector-1] — constructors include `MetricCollector<T>(Meter, string instrumentName, TimeProvider)` and `MetricCollector<T>(IMeterFactory, string meterName, string instrumentName, TimeProvider)`.

```csharp
// Source: learn.microsoft.com/dotnet/api/microsoft.extensions.diagnostics.metrics.testing.metriccollector-1
[Fact]
public async Task GrowIncrementsCounter()
{
    var services = new ServiceCollection();
    services.AddMetrics();  // registers IMeterFactory
    using var sp = services.BuildServiceProvider();
    var meterFactory = sp.GetRequiredService<IMeterFactory>();

    using var growCounter = new MetricCollector<long>(
        meterFactory,
        meterName: "Oragon.ElasticPool",
        instrumentName: "pool.grow.count",
        timeProvider: TimeProvider.System);  // counter doesn't need fake time

    var fake = new FakeTimeProvider();
    var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
        .Factory((_, _) => ValueTask.FromResult(new Resource()))
        .WithBounds(0, 10, 0)
        .WithTimeProvider(fake)
        .Build();

    // Drive pool to grow (e.g., parallel acquires, force waiter)
    // ...

    await growCounter.WaitForMeasurementsAsync(minCount: 1, timeout: TimeSpan.FromSeconds(5));
    var snapshot = growCounter.GetMeasurementSnapshot();
    snapshot.ShouldHaveSingleItem();
    snapshot[0].Value.ShouldBe(1);
    snapshot[0].Tags.ShouldContain(kvp => kvp.Key == "pool.name");
}
```

For histogram bucket distribution assertions, use `MetricCollector<double>` and walk `GetMeasurementSnapshot()` — each measurement is a single observation, not a bucket. Standard approach: assert count, sum, and that p-percentile-equivalent measurements exist (e.g., "at least one measurement > 100ms").

### Pattern 9: FakeTimeProvider + PeriodicTimer Test Pattern (with race mitigation)

**What:** Standard pattern is `Advance(SweepInterval)` to fire the timer; the well-known pitfall is that the timer's `WaitForNextTickAsync` continuation runs on the threadpool, so the test must yield to let it execute before asserting.

**Source:** [VERIFIED: dotnet/runtime#125077] — recommended workaround: `await Task.Yield()` between `Advance` calls. For stronger guarantees, expose an internal "sweep tick fired" probe via `TaskCompletionSource`.

```csharp
// Source: github.com/dotnet/runtime/discussions/125077 + andrewlock.net "Avoiding flaky tests with TimeProvider"
[Fact]
public async Task SweepFiresOnAdvance()
{
    var fake = new FakeTimeProvider();
    var pool = ElasticObjectPoolFactory.Build<Resource>(sp)
        .Factory(...)
        .WithBounds(0, 5, 0)
        .WithTimeProvider(fake)
        .SweepInterval(TimeSpan.FromSeconds(30))
        .Build();
    await Task.Yield();  // give the sweep loop a chance to enter WaitForNextTickAsync

    fake.Advance(TimeSpan.FromSeconds(30));
    await Task.Yield();  // let the timer continuation run

    // Assert: one sweep span emitted, ...
}
```

For high-determinism tests (sweep failure backoff, multi-tick scenarios), expose internal `Task SweepTickCompleted` (a `TaskCompletionSource` reset each tick) via `[InternalsVisibleTo]` or test-only public probe — Phase 1 already uses `[InternalsVisibleTo]` for `DynamicProxyGenAssembly2`; add Test assembly to that list. Wait on this TCS instead of `Task.Yield()` for tighter assertion.

### Anti-Patterns to Avoid

- **Wall-clock-based timing in tests:** flake-prone, slow, and impossible under CI parallel test execution. Per Phase 1 SUMMARY pattern + CONTEXT specifics: every time-dependent test MUST use `FakeTimeProvider`.
- **Sweeping with `Task.Delay(interval)` loop:** drift (per ARCHITECTURE.md §"Background Sweep Mechanism"). Use `PeriodicTimer.WaitForNextTickAsync` only.
- **Calling Factory inside a lock during grow:** Factory may take seconds (per Phase 1 design); existing CAS-on-`_total` pattern reserves the slot lock-free, then calls Factory outside. Phase 2 must NOT introduce a lock here.
- **One ActivitySource per pool instance:** wrong lifetime — should be `internal static readonly` per assembly. ActivitySource has process lifetime; many short-lived pools sharing one source is the documented pattern.
- **One Meter per pool instance via `new Meter(...)`:** Phase 1 already uses `IMeterFactory` with fallback. Phase 2 keeps that pattern. Tests use `MetricCollector` against the meter from `IMeterFactory`.
- **Per-item span in sweep:** explodes cardinality. Per CONTEXT: 1 span per sweep tick + `ActivityEvent` per item.
- **Sync sample emission in hot path:** the utilization sampler's `Sample(int, int)` MUST be `void` and allocation-free. Don't `await` anything inside; don't allocate `KeyValuePair[]`; don't lock.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Periodic background loop | `Task.Delay`-based timer | `PeriodicTimer(TimeSpan, TimeProvider)` | Drift-free; in-box net8+; `TimeProvider`-injectable for tests; cancellation built in |
| Synthetic time for tests | Custom `IClock` interface | `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing 10.5.0` | Already pinned; integrates with `PeriodicTimer`, `Task.Delay`, `CancellationTokenSource` timeouts via `TimeProvider` extension methods |
| Counter assertions in tests | Hand-rolled `MeterListener` | `MetricCollector<long>` from `Microsoft.Extensions.Diagnostics.Testing 10.5.0` | Phase 1 SUMMARY established this as canonical pattern; provides `WaitForMeasurementsAsync`, snapshot, tag matching |
| Span capture in tests | Custom `Activity.Current` polling | `ActivityListener` with `Sample = (ref _) => AllData` | Standard pattern from dotnet/runtime tests; deterministic; supports per-source filtering |
| Allocation-free logging | `_logger.LogInformation(...)` with format strings | `[LoggerMessage]` source-gen partial methods | Phase 1 already uses this; avoids boxing + format-string interpretation cost |
| Histogram for p95 wait time | `Stopwatch[]` array with manual percentile compute | `Counter<long>`/`Histogram<double>` instrument + small ring buffer for in-engine p95 query | The `Histogram<double>` instrument is for OTel export; for the in-engine grow decision, a small dedicated p95 ring buffer is the right primitive |
| Failure-rate tracking | Time-bucketed exponential decay | Plain int counter `_consecutiveFailureWindows` reset on success | Sweep is discrete-tick already; counter aligns naturally |
| Concurrent waiter coordination | Hand-rolled `SemaphoreSlim` + `ConcurrentQueue` | `Channel<TaskCompletionSource<PoolEntry<T>>>` (Phase 1) | Already in Phase 1; Phase 2 reuses unchanged |

**Key insight:** Every Phase 2 component aligns with an established BCL or `Microsoft.Extensions.*` primitive. There is no scenario in Phase 2 where a hand-rolled custom solution is correct. The temptation areas (rolling-window stats, percentile tracking, time-bucketed failure rate) all have small, focused internal classes that wrap BCL primitives — they are not "hand-rolled" in the bad sense (no novel concurrency primitives, no custom CAS loops) but rather thin domain logic over `int[]`, `double[]`, and `Interlocked`.

## Common Pitfalls

### Pitfall A: Sweep amplifies load during downstream outage
**What goes wrong:** `Check` hook returns Unhealthy on every item during a broker outage; failure policy discards + recreates; Factory fails; sweep runs again 30s later, same pattern. Outbound traffic INCREASES during outage.
**Why it happens:** Sweep cadence + factory retry + consumer retry compose multiplicatively. "Heal aggressively" feels right in isolation; in partial-outage it's wrong.
**How to avoid:** Adaptive backoff (`SweepBackoffState`), 30s → 60s → 120s → 5min cap. Reset on first clean sweep. Cap concurrent factory invocations during recovery (already partially mitigated by single-consumer sweep loop).
**Warning signs:** `pool.health.failures` counter shows synchronized spikes at sweep-interval cadence. `pool.size` does not recover after outage ends.
**Source:** [CITED: PITFALLS.md Pitfall 5]

### Pitfall B: Elastic algorithm oscillation (grow/shrink thrash)
**What goes wrong:** Pool grows to 100 under burst; idle 60s; shrinks to MinSize=10; next burst arrives → grow from 10 to 100 again. Cycle repeats every ~60s.
**Why it happens:** No hysteresis between grow and shrink thresholds; aggressive `IdleTimeout`.
**How to avoid:** Cooldown counter (3 windows since last grow). Shrink one item per tick. Per CONTEXT decisions, all four mitigations are locked in.
**Warning signs:** `pool.size` gauge shows sawtooth pattern. Broker shows repeated open/close at fixed period. App p99 latency has periodic spikes.
**Source:** [CITED: PITFALLS.md Pitfall 7]

### Pitfall C: Slow growth fails the burst
**What goes wrong:** Single-signal "grow on wait > 100ms" fires too late; pool grows by 1 per second while 200 acquires pile up.
**Why it happens:** Single-signal triggers; conservative grow-by-one.
**How to avoid:** OR-combined composite signal (per CONTEXT). Pre-emptive grow on first wait (waiter count threshold = 1 default). The CAS reservation pattern from Phase 1 already supports parallel grow under contention.
**Warning signs:** `pool.acquire.wait.duration` p99 spikes during burst onset; `pool.size` rises slowly while `pool.pending_requests` rises fast.
**Source:** [CITED: PITFALLS.md Pitfall 8]

### Pitfall D: Cascading failure — pool exhaustion masks dependency failure
**What goes wrong:** Factory takes 25s before timing out; pool grows to MaxSize; all acquires block 25s+; app threads exhaust; pool amplifies partial outage to full one.
**Why it happens:** Pool's job is to provide items; doesn't reason about dependency health.
**How to avoid:** Surface failure rate as a counter (`pool.health.failures`); document Polly composition recipe; for v1 we do NOT add a "degraded mode" — that's deferred per CONTEXT (and per PITFALLS.md Pitfall 22 Phase mapping which assigns part to v1.x hardening).
**Warning signs:** Thread-pool exhaustion correlated with downstream slowness; `pool.acquire.duration` p99 = full timeout for ALL calls.
**Source:** [CITED: PITFALLS.md Pitfall 22]

### Pitfall E: FakeTimeProvider+PeriodicTimer test races
**What goes wrong:** Test calls `fake.Advance(SweepInterval)`, then immediately asserts on sweep effects; assertion fails because the timer continuation is queued on the threadpool but hasn't run yet.
**Why it happens:** `WaitForNextTickAsync` continuation resumes on the threadpool; `Advance` is a synchronous setter, doesn't pump.
**How to avoid:** `await Task.Yield()` after `Advance`. For multi-tick determinism, expose an internal `Task SweepTickCompleted` probe via `[InternalsVisibleTo]`.
**Warning signs:** Tests pass locally, fail in CI; tests pass with `Thread.Sleep(50)` "fix" injected.
**Source:** [VERIFIED: dotnet/runtime#125077]

### Pitfall F: ActivitySource lifetime mismatch
**What goes wrong:** `new ActivitySource(...)` per pool instance, then `Dispose()` on pool dispose. Listeners attached to the source name receive no events from later pools; tests interfere with each other.
**Why it happens:** Treating ActivitySource like Meter.
**How to avoid:** `internal static readonly ActivitySource _activitySource = new("Oragon.ElasticPool")` per assembly. Never disposed. Meter is per-pool (already Phase 1); ActivitySource is per-assembly.
**Source:** [CITED: PITFALLS.md Pitfall 16/17 + ARCHITECTURE.md "Meter & ActivitySource Names"]

### Pitfall G: BeforeUse / Check hook pulled into hot Acquire path
**What goes wrong:** Sweep's `Check` hook is implemented as a server round-trip (e.g., AMQP no-op). Engine reuses the same hook in BeforeUse. Acquire latency = round-trip.
**Why it happens:** Symmetric API design feels right.
**How to avoid:** Document the hook contracts: `BeforeUse` cheap (<1ms p99, in-process only); `Check` may be expensive. They are SEPARATE delegates — Phase 1 already separates them. Phase 2 must NOT collapse them.
**Source:** [CITED: PITFALLS.md Pitfall 6]

## Code Examples

### Example 1: AcquireAsync slow path with composite-signal grow (Phase 2 modification)

```csharp
// Source: design — extends Phase 1 ElasticPool<T>.AcquireAsyncCore
private async ValueTask<IPoolItem<T>> AcquireAsyncCore(CancellationToken cancellationToken, int retryCount)
{
    ThrowIfDisposed();
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
    var ct = linked.Token;

    // Fast path (unchanged from Phase 1)
    if (_idle.TryDequeue(out var entry))
        return await PrepareForUseAsync(entry, ct, cancellationToken, retryCount).ConfigureAwait(false);

    // PHASE 2: composite-signal grow decision
    var waiters = Volatile.Read(ref _waitersCount);
    var decision = _pressure.Evaluate(currentTotal: Volatile.Read(ref _total), currentWaiters: waiters);
    if (decision.ShouldGrow)
    {
        var newEntry = await TryGrowAsync(decision, ct).ConfigureAwait(false);
        if (newEntry is not null)
            return await PrepareForUseAsync(newEntry, ct, cancellationToken, retryCount).ConfigureAwait(false);
    }

    // Wait or throw (per WaitBehavior — unchanged from Phase 1)
    if (_options.WhenExhausted == WaitBehavior.Throw)
        throw new PoolExhaustedException(_options.MaxSize);

    return await ParkAndWaitAsync(ct, cancellationToken, retryCount).ConfigureAwait(false);
}

private async ValueTask<PoolEntry<T>?> TryGrowAsync(GrowDecision decision, CancellationToken ct)
{
    // Reuse Phase 1 CAS pattern.
    int currentTotal = Volatile.Read(ref _total);
    if (currentTotal >= _options.MaxSize) return null;
    if (Interlocked.CompareExchange(ref _total, currentTotal + 1, currentTotal) != currentTotal) return null;

    using var span = _telemetry.StartGrowSpan(_options.PoolName, decision);
    try
    {
        var newItem = await _options.Factory(_services, ct).ConfigureAwait(false);
        var entry = new PoolEntry<T>(newItem, _options.TimeProvider.GetUtcNow(), _options.TimeProvider.GetUtcNow());
        Interlocked.Exchange(ref _sinceLastGrowTicks, 0);  // reset cooldown
        _telemetry.OnGrow();
        _log.Grew(_options.PoolName, oldTotal: currentTotal, newTotal: currentTotal + 1,
            tripWaiters: decision.TrippedByWaiters,
            tripUtilization: decision.TrippedByUtilization,
            tripP95: decision.TrippedByP95);
        span?.SetStatus(ActivityStatusCode.Ok);
        return entry;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Interlocked.Decrement(ref _total);  // counter rollback (Phase 1 invariant)
        _telemetry.OnFactoryFailure();
        _log.FactoryFailed(_options.PoolName, ex);
        span?.SetStatus(ActivityStatusCode.Error, ex.Message);
        await _options.FailurePolicy.HandleAsync(default, FailureKind.FactoryThrew, ex, ct).ConfigureAwait(false);
        throw;
    }
}
```

### Example 2: `[LoggerMessage]` extensions (Phase 2 additions)

```csharp
// Source: ARCHITECTURE.md §"Source-Generated Logging" + Phase 1 PoolDiagnosticsLog.cs
internal static partial class PoolDiagnosticsLog
{
    // Phase 1 entries 1001-1004 already exist.

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information,
        Message = "Pool '{PoolName}' grew from {Old} to {New} items (waiters={TripWaiters} util={TripUtilization} p95={TripP95}).")]
    public static partial void Grew(this ILogger logger, string poolName, int old, int @new,
        bool tripWaiters, bool tripUtilization, bool tripP95);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information,
        Message = "Pool '{PoolName}' shrunk from {OldTotal} to {NewTotal} idle items past IdleTimeout.")]
    public static partial void Shrunk(this ILogger logger, string poolName, int oldTotal, int newTotal);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Debug,
        Message = "Pool '{PoolName}' sweep started (interval={IntervalSeconds}s).")]
    public static partial void SweepStarted(this ILogger logger, string poolName, double intervalSeconds);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Debug,
        Message = "Pool '{PoolName}' sweep completed in {DurationMs}ms (checked={Checked} unhealthy={Unhealthy} shrunk={Shrunk}).")]
    public static partial void SweepCompleted(this ILogger logger, string poolName,
        double durationMs, int @checked, int unhealthy, int shrunk);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning,
        Message = "Pool '{PoolName}' sweep entering backoff: interval {OldSec}s → {NewSec}s after {ConsecutiveWindows} unhealthy windows.")]
    public static partial void SweepFailureBackoff(this ILogger logger, string poolName,
        double oldSec, double newSec, int consecutiveWindows);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning,
        Message = "Pool '{PoolName}' Check hook reported Unhealthy in sweep ({Reason}); applying failure policy.")]
    public static partial void CheckUnhealthy(this ILogger logger, string poolName, string reason);

    [LoggerMessage(EventId = 1099, Level = LogLevel.Error,
        Message = "Pool '{PoolName}' background sweep iteration failed.")]
    public static partial void SweepFailed(this ILogger logger, string poolName, Exception ex);
}
```

### Example 3: Builder fluent additions (Phase 2)

```csharp
// Source: design — extends Phase 1 ElasticPoolBuilder<T>
public sealed class ElasticPoolBuilder<T> where T : notnull
{
    // ... existing Phase 1 fields ...
    private int _growOnWaiterCount = 1;
    private double _growOnUtilizationPercent = 0.80;
    private TimeSpan _growOnWaitTimeP95 = TimeSpan.FromMilliseconds(100);
    private TimeSpan _idleTimeout = TimeSpan.FromSeconds(60);
    private int _shrinkCooldownWindows = 3;
    private TimeSpan _sweepInterval = TimeSpan.FromSeconds(30);
    private TimeSpan _maxBackoff = TimeSpan.FromMinutes(5);

    public ElasticPoolBuilder<T> GrowOnWaiterCount(int n)
    { if (n < 1) throw new ArgumentOutOfRangeException(nameof(n)); _growOnWaiterCount = n; return this; }

    public ElasticPoolBuilder<T> GrowOnUtilizationPercent(double p)
    { if (p <= 0 || p > 1.0) throw new ArgumentOutOfRangeException(nameof(p), "0 < p <= 1"); _growOnUtilizationPercent = p; return this; }

    public ElasticPoolBuilder<T> GrowOnWaitTimeP95(TimeSpan t)
    { if (t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(t)); _growOnWaitTimeP95 = t; return this; }

    public ElasticPoolBuilder<T> IdleTimeout(TimeSpan t)
    { if (t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(t)); _idleTimeout = t; return this; }

    public ElasticPoolBuilder<T> ShrinkCooldownWindows(int n)
    { if (n < 0) throw new ArgumentOutOfRangeException(nameof(n)); _shrinkCooldownWindows = n; return this; }

    public ElasticPoolBuilder<T> SweepInterval(TimeSpan t)
    { if (t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(t)); _sweepInterval = t; return this; }

    public ElasticPoolBuilder<T> MaxBackoff(TimeSpan t)
    { if (t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(t)); _maxBackoff = t; return this; }

    public IElasticPool<T> Build()
    {
        // ... existing Phase 1 validation ...
        if (_growOnWaiterCount > _maxSize)
            throw new InvalidOperationException("GrowOnWaiterCount must not exceed MaxSize.");
        if (_sweepInterval > _maxBackoff)
            throw new InvalidOperationException("SweepInterval must not exceed MaxBackoff.");
        // ... build options + new ElasticPool<T> ...
    }
}
```

### Example 4: Stress test scaffold (burst → idle → burst)

```csharp
// Source: design — Phase 2 anchor stress test, analog of Phase 1 PingPongStressTest
[Fact(Timeout = 60_000)]
public async Task BurstIdleBurst_PoolGrowsShrinksGrowsAgain_WithoutDeadlocks()
{
    var fake = new FakeTimeProvider();
    var services = new ServiceCollection();
    services.AddMetrics();
    services.AddLogging();
    using var sp = services.BuildServiceProvider();

    using var pool = (ElasticPool<Resource>)ElasticObjectPoolFactory.Build<Resource>(sp)
        .Factory((_, _) => ValueTask.FromResult(new Resource()))
        .Release((r, _) => { r.Dispose(); return ValueTask.CompletedTask; })
        .WithBounds(min: 5, max: 100, initial: 5)
        .WithTimeProvider(fake)
        .SweepInterval(TimeSpan.FromSeconds(30))
        .IdleTimeout(TimeSpan.FromSeconds(60))
        .ShrinkCooldownWindows(3)
        .Build();
    await pool.ReadyAsync();

    using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(45));

    // Burst 1: 200 concurrent Acquires
    var burst1 = Enumerable.Range(0, 200).Select(async _ =>
    {
        for (int i = 0; i < 10; i++)
        {
            await using var item = await pool.AcquireAsync(watchdog.Token);
            await Task.Yield();
        }
    }).ToArray();
    await Task.WhenAll(burst1);
    pool.InUse.ShouldBe(0);
    pool.Available.ShouldBeGreaterThan(5);  // grew during burst

    // Idle: advance time past cooldown + idle timeout
    for (int i = 0; i < 5; i++)  // 5 sweep ticks → cooldown elapsed at tick 3, then shrinks
    {
        fake.Advance(TimeSpan.FromSeconds(30));
        await Task.Yield();
    }
    pool.Available.ShouldBe(5);  // shrunk back to MinSize

    // Burst 2: same shape — pool must regrow
    var burst2 = Enumerable.Range(0, 200).Select(async _ =>
    {
        for (int i = 0; i < 10; i++)
        {
            await using var item = await pool.AcquireAsync(watchdog.Token);
            await Task.Yield();
        }
    }).ToArray();
    await Task.WhenAll(burst2);
    pool.InUse.ShouldBe(0);
    pool.Available.ShouldBeGreaterThan(5);  // regrew
}
```

## Stress Test Design (Burst→Idle→Burst)

The Phase 2 anchor stress test (analogous to Phase 1's MaxSize=1 ping-pong) is the burst-idle-burst cycle described above. Key design points:

1. **FakeTimeProvider**: only way to drive the sweep loop deterministically; wall-clock would make the test multi-minute.
2. **Realistic concurrency**: 200 threads × 10 cycles each (2000 total Acquire/Release) is enough to exercise the slow path under contention; smaller counts may not trigger grow.
3. **Watchdog**: 45s logical, 60s xUnit `Timeout` — same pattern as Phase 1.
4. **Assertions**: `InUse == 0` and `Available > MinSize` after burst1 (grew); `Available == MinSize` after idle (shrank); `Available > MinSize` after burst2 (regrew).
5. **Stress project NOT in CI default**: per Phase 1 SUMMARY decision; runs manually until Phase 4 nightly stress job.
6. **Concurrent grow correctness**: the CAS-on-`_total` pattern from Phase 1 is reused; under 200-way contention, only `MaxSize` reservations succeed, others fall through to wait. No new race surface.
7. **Telemetry assertions**: optionally attach a `MetricCollector<long>` for `pool.grow.count` and `pool.shrink.count` during the test; assert `growCount >= 1` after burst1 and `shrinkCount >= 1` after idle.
8. **Sweep-failure scenario**: a separate stress test makes Factory throw 50% of the time during burst; asserts adaptive backoff kicks in (sweep interval doubles after 3 failure windows) and recovers (interval resets to 30s) when Factory becomes healthy again.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 10 SDK | build/test | ✓ | per Phase 1 (SDK 10.0.107+) | — |
| `Microsoft.Extensions.TimeProvider.Testing` 10.5.0 | tests | ✓ | 10.5.0 (CPM-pinned) | — |
| `Microsoft.Extensions.Diagnostics.Testing` 10.5.0 | tests | ✓ | 10.5.0 (CPM-pinned) | — |
| `xunit.v3` 3.2.2 | tests | ✓ | 3.2.2 (CPM-pinned) | — |
| `AwesomeAssertions` 9.4.0 | tests | ✓ | 9.4.0 (CPM-pinned) | — |
| `NSubstitute` 5.3.0 | tests | ✓ | 5.3.0 (CPM-pinned) | — |
| `coverlet.console` (CI tool) | CI coverage | ✓ | 10.0.0 (CI-installed per Phase 1) | — |
| `dotnet-reportgenerator-globaltool` (CI tool) | CI coverage | ✓ | 5.5.9 (CI-installed per Phase 1) | — |

**Missing dependencies with no fallback:** none.
**Missing dependencies with fallback:** none.

All dependencies needed for Phase 2 are already pinned and verified by Phase 1 Plan 03.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Wall-clock-driven `Task.Delay` for periodic work | `PeriodicTimer` + `TimeProvider` for testable, drift-free periodic work | net6 (PeriodicTimer) + net8 (TimeProvider overload) | Tests become deterministic; no flake from timing |
| `IClock` custom abstractions | `System.TimeProvider` (in-box) | net8 GA | One less library dependency; integrates with all BCL timer APIs |
| `Task<T>`-based background loops with manual `CancellationTokenSource` | `await PeriodicTimer.WaitForNextTickAsync(ct)` | net6+ | Eliminates drift; cancellation token handled correctly |
| Hand-rolled `MeterListener` for tests | `MetricCollector<T>` from `Microsoft.Extensions.Diagnostics.Testing` | Microsoft.Extensions 8+ | Built-in `WaitForMeasurementsAsync`, snapshots, tag matching |
| `Activity.Current` polling for span tests | `ActivityListener` with `Sample = AllData` | net5+ | Standard, deterministic, source-filterable |
| Custom rolling-window allocations per sample | Pre-allocated ring buffer | timeless pattern | Allocation-free hot path |
| HikariCP-style `SynchronousQueue` | `Channel<TaskCompletionSource>` direct handoff (Phase 1) | inherited | Async-aware, cancellation built in |

**Deprecated/outdated:**
- `System.Threading.Timer` for periodic async work: callback-based, sync-style, awkward for async health checks. Use `PeriodicTimer`.
- `ConcurrentBag<T>` for free-list: ABA risk per PITFALLS Pitfall 4. Use `ConcurrentQueue<T>` (Phase 1 already does).
- Per-pool `new ActivitySource(...)`: lifetime mismatch. Use `internal static readonly` per assembly.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Histogram bucket boundaries for `pool.acquire.wait.duration` should be OTel default (no custom buckets) | §"Telemetry" | Telemetry consumers may want different buckets; trivially overridable later via `Histogram.AdvanceBuckets` API or OTel View. Low risk |
| A2 | `[InternalsVisibleTo]` is acceptable to add for a test-only `Task SweepTickCompleted` probe | §"Pattern 9" | If team prefers no test-side internal exposure, fall back to pure `Task.Yield()` patterns; more flake risk but feasible. Low-medium risk |
| A3 | Sample interval for `UtilizationSampler` = 1 second (cap on hot-path sampling) | §"Pattern 2" | Sub-second bursts under-represented in window. Configurable later if metric users complain. Low risk |
| A4 | p95 wait-time computation from a 100-sample ring buffer is "accurate enough" | §"Pattern 3" | Statistical p95 from 100 samples has ~10% confidence interval; sufficient for grow decisions but not for SLO reporting. Document as "in-engine p95 estimate". Low risk |
| A5 | `MaxBackoff` default of 5 minutes matches ROADMAP success criterion | §"Pattern 5" | Verified against ROADMAP §"Phase 2" SC #3 — explicitly says "30s → 60s → 120s, capped at 5 min". Confirmed |
| A6 | Telemetry allocation-free claim verified by benchmark (per ROADMAP SC #5) | §"Code Examples" | If `[LoggerMessage]` or `Counter.Add` allocates per call (e.g., due to KVP[] tag boxing), benchmark will catch — Phase 2 must include the benchmark. Medium risk if not actually benchmarked |

**If this table looks light:** All locked decisions in CONTEXT.md remove most assumptions. Remaining items are tactical defaults (bucket sizes, sample rates) that are configurable post-hoc without breaking changes.

## Open Questions

1. **`MaxBackoff` builder method naming.**
   - What we know: ROADMAP locks the curve (30s → 60s → 120s → 5min) and CONTEXT discretion mentions the cap is tunable.
   - What's unclear: should it be `.MaxBackoff(TimeSpan)` (consistent with `.MaxSize(int)`) or `.SweepFailureBackoffCap(TimeSpan)` (more descriptive)?
   - Recommendation: ship `.MaxBackoff(TimeSpan)` (shorter, consistent). Add XML doc explaining it caps the exponential backoff under sweep failures.

2. **`pool.acquire.wait.duration` histogram unit.**
   - What we know: BCL `Histogram<double>` is unit-agnostic; OTel convention is seconds for `*.duration`.
   - What's unclear: do we record seconds (per OTel convention) or milliseconds (more readable in dashboards)?
   - Recommendation: seconds (per OTel) — Aspire Dashboard, Prometheus, Grafana all interpret correctly.

3. **Should the engine sample utilization on every Acquire/Release, or only on a timer?**
   - What we know: every-event sampling captures bursts but is hot-path overhead; timer sampling is cheaper but loses sub-tick resolution.
   - What's unclear: which is the right tradeoff for v1.
   - Recommendation: every-event with 1-second `_lastSampleTicks` debounce. Allocates nothing in the debounced case (just a `Volatile.Read` + compare); fully captures sub-second bursts when they happen.

4. **Should `SweepBackoffState` be exposed for inspection (e.g., a `pool.sweep.interval` gauge)?**
   - What we know: operators benefit from seeing "is the pool in degraded mode?"; we don't want to add a new Counter type.
   - What's unclear: is the existing `pool.health.failures` counter sufficient to infer backoff state?
   - Recommendation: ship without an explicit gauge in Phase 2; revisit in Phase 4 polish if README writers ask. Failure counter + sweep duration histogram together encode the state.

5. **`HasListeners()` guard placement: in `TelemetryEmitter` only, or duplicated at all caller sites?**
   - What we know: `StartActivity` itself returns null cheaply when no listener.
   - What's unclear: is the extra `HasListeners()` check before tag-prep work worth the LOC?
   - Recommendation: gate ONLY the per-item `ActivityEvent` emission in sweep (potentially N events per tick), not the `Pool.Grow`/`Pool.Shrink` spans (single event). Keep it simple.

## Sources

### Primary (HIGH confidence)

- [Microsoft Learn — PeriodicTimer Class (.NET 10)](https://learn.microsoft.com/en-us/dotnet/api/system.threading.periodictimer) — verified ctor with `TimeProvider`, drift-free semantics, single-consumer
- [Microsoft Learn — MetricCollector<T> Class (.NET 10)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.diagnostics.metrics.testing.metriccollector-1) — verified ctor matrix, `WaitForMeasurementsAsync`, `GetMeasurementSnapshot`
- [Microsoft Learn — FakeTimeProvider Class (.NET 10)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.time.testing.faketimeprovider) — verified `Advance`, `SetUtcNow`, `AutoAdvanceAmount`
- [Microsoft Learn — Add distributed tracing instrumentation - .NET](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs) — `ActivitySource`/`Activity` patterns, null-conditional `?.` for no-listener case, `HasListeners` guidance
- [NuGet — Microsoft.Extensions.TimeProvider.Testing 10.5.0](https://www.nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing) — verified version 10.5.0, published 2026-04-15
- [NuGet — Microsoft.Extensions.Diagnostics.Testing 10.5.0](https://www.nuget.org/packages/Microsoft.Extensions.Diagnostics.Testing) — verified version 10.5.0, published 2026-04-15
- [.planning/research/ARCHITECTURE.md](../../research/ARCHITECTURE.md) — Phase 2 build order, sweep mechanism, telemetry surface, anti-patterns
- [.planning/research/PITFALLS.md](../../research/PITFALLS.md) — Pitfalls 5 (sweep amplification), 6 (BeforeUse cost), 7 (oscillation), 8 (slow growth), 21 (quarantine never recovers), 22 (cascading failure)
- [.planning/research/SUMMARY.md](../../research/SUMMARY.md) — stack confidence, requirement-to-phase mapping
- [.planning/phases/01-core-skeleton-fixed-size-pool/03-SUMMARY.md](../01-core-skeleton-fixed-size-pool/03-SUMMARY.md) — Phase 1 closing state, established patterns inheritable to Phase 2

### Secondary (MEDIUM confidence)

- [GitHub dotnet/runtime#125077 — Using FakeTimeProvider in PeriodicTimer](https://github.com/dotnet/runtime/discussions/125077) — race-condition mitigation pattern verified by `Task.Yield()` recommendation
- [GitHub dotnet/extensions#3995 — FakeTimeProvider.Advance/SetUtcNow does not behave as expected](https://github.com/dotnet/extensions/issues/3995) — additional context on Advance semantics
- [Andrew Lock — Avoiding flaky tests with TimeProvider and ITimer](https://andrewlock.net/exploring-the-dotnet-8-preview-avoiding-flaky-tests-with-timeprovider-and-itimer/) — sample test pattern, advance-and-assert flow
- [Jimmy Bogard — A Lap Around ActivitySource and ActivityListener in .NET 5](https://www.jimmybogard.com/activitysource-and-listener-in-net-5/) — `ActivityListener` test pattern with `ShouldListenTo` + `Sample = AllData`
- [GitHub dotnet/runtime — ActivitySourceTests.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.DiagnosticSource/tests/ActivitySourceTests.cs) — official test patterns for `ActivityListener`
- [Microsoft Learn — High-performance logging](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/high-performance-logging) — `[LoggerMessage]` source generator semantics, allocation-free guarantees
- [HikariCP issue tracker](https://github.com/brettwooldridge/HikariCP) — composite-signal grow / oscillation patterns extrapolated to .NET context

### Tertiary (LOW confidence)

- (None — every Phase 2 claim is backed by either authoritative .NET docs or established Phase 1 patterns)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — every package version verified against NuGet.org or already pinned in Phase 1 CPM
- Architecture: HIGH — patterns inherited from ARCHITECTURE.md (already verified) plus targeted lookups for `PeriodicTimer`+`FakeTimeProvider` race mitigation (verified via dotnet/runtime#125077)
- Pitfalls: HIGH — all 6 Phase-2-relevant pitfalls (5, 6, 7, 8, 21, 22) catalogued in project PITFALLS.md with sources
- Telemetry test patterns: HIGH — `MetricCollector<T>` and `ActivityListener` both verified against dotnet docs and dotnet/runtime test code
- Threshold defaults: MEDIUM — locked in CONTEXT.md but explicitly flagged for empirical tuning in v1.x; ship sensible HikariCP-aligned defaults

**Research date:** 2026-05-02
**Valid until:** 2026-06-01 (30 days — stack is stable; revisit if `Microsoft.Extensions.*` 11.x ships within window with breaking API changes)
