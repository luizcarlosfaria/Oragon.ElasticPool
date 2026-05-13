---
phase: 02-elasticity-health
plan: 02
type: execute
wave: 2
depends_on: [01]
files_modified:
  - src/Oragon.ElasticPool.Core/Telemetry/PoolMeterNames.cs
  - src/Oragon.ElasticPool.Core/Telemetry/TelemetryEmitter.cs
  - src/Oragon.ElasticPool.Core/Telemetry/PoolDiagnosticsLog.cs
  - src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs
  - src/Oragon.ElasticPool.Core/Internals/BackgroundSweeper.cs
autonomous: true
requirements: [ELASTIC-01, ELASTIC-02, TELEM-02, TELEM-03]
must_haves:
  truths:
    - "ActivitySource 'Oragon.ElasticPool' is declared once per assembly and emits spans for Acquire, Release, HealthCheck, Grow, Shrink with HasListeners()-guarded tag preparation only where preparatory work is non-trivial."
    - "TelemetryEmitter exposes 3 new counters (pool.grow.count, pool.shrink.count, pool.health.failures) and 2 histograms (pool.acquire.wait.duration in seconds, pool.sweep.duration in seconds), each tagged with pool.name."
    - "PoolDiagnosticsLog has 7 new [LoggerMessage] entries (1005 Grew, 1006 Shrunk, 1007 SweepStarted, 1008 SweepCompleted, 1009 SweepFailureBackoff, 1010 CheckUnhealthy, 1099 SweepFailed)."
    - "AcquireAsyncCore consults PressureSampler.Evaluate before parking a waiter; on ShouldGrow=true it CAS-reserves a slot, calls Factory outside any lock, resets _sinceLastGrowTicks, emits Grow span + counter + log."
    - "AcquireAsyncCore records wait duration into WaitDurationHistogram on every slow-path entry that ends up parking; also feeds pool.acquire.wait.duration histogram."
    - "BackgroundSweeper.RunSweepTickAsync runs Check hook on idle items, applies failure policy on Unhealthy decisions, runs shrink pass when SinceLastGrowTicks >= ShrinkCooldownWindows, evicts at most 1 idle item per tick respecting MinSize and IdleTimeout, increments cooldown counter at end, calls SweepBackoffState.OnSweepResult."
    - "Sweep failure backoff: after 3 consecutive sweep windows where >=50% of Check invocations are Unhealthy/throw, the timer Period doubles (30s -> 60s -> 120s -> capped at MaxBackoff = 5min); resets to base on first clean window."
  artifacts:
    - path: "src/Oragon.ElasticPool.Core/Telemetry/TelemetryEmitter.cs"
      provides: "Extended emitter with internal static ActivitySource; new counter/histogram fields; OnGrow/OnShrink/OnHealthFailure/OnAcquireWait(TimeSpan)/OnSweepDuration(TimeSpan) methods; StartGrowSpan/StartShrinkSpan/StartSweepSpan/StartHealthCheckSpan helpers"
      contains: "ActivitySource"
    - path: "src/Oragon.ElasticPool.Core/Telemetry/PoolDiagnosticsLog.cs"
      provides: "7 new [LoggerMessage] partial methods (EventIds 1005-1010, 1099)"
      contains: "EventId = 1005"
    - path: "src/Oragon.ElasticPool.Core/Telemetry/PoolMeterNames.cs"
      provides: "New instrument-name constants: GrowCount, ShrinkCount, HealthFailures, AcquireWaitDuration, SweepDuration; ActivitySource name + Outcome tag constants"
      contains: "GrowCount"
    - path: "src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs"
      provides: "Composite-signal grow path in AcquireAsyncCore; wait-duration recording on slow path; Acquire/Release ActivitySource spans; cooldown reset on grow"
      contains: "_pressure.Evaluate"
    - path: "src/Oragon.ElasticPool.Core/Internals/BackgroundSweeper.cs"
      provides: "Sweep tick: health-check pass + shrink pass + backoff/cooldown bookkeeping; Pool.Sweep ActivitySource span with per-item ActivityEvents; SweepStarted/SweepCompleted/SweepFailureBackoff log emissions"
      contains: "RunSweepTickAsync"
  key_links:
    - from: "src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs"
      to: "src/Oragon.ElasticPool.Core/Internals/PressureSampler.cs"
      via: "AcquireAsyncCore calls _pressure.Evaluate(currentTotal, currentWaiters) on the slow path; on ShouldGrow=true triggers TryGrowAsync"
      pattern: "_pressure\\.Evaluate"
    - from: "src/Oragon.ElasticPool.Core/Internals/BackgroundSweeper.cs"
      to: "src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs"
      via: "RunSweepTickAsync calls _pool.Idle (TryDequeue + Check + ReturnSync), _pool.Options.Check, _pool.Options.FailurePolicy, _pool.SinceLastGrowTicks, _pool.IncrementSinceLastGrowTicks"
      pattern: "_pool\\.(Idle|Options|SinceLastGrowTicks|IncrementSinceLastGrowTicks)"
    - from: "src/Oragon.ElasticPool.Core/Telemetry/TelemetryEmitter.cs"
      to: "src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs"
      via: "OnGrow/OnShrink/OnHealthFailure/OnAcquireWait/StartGrowSpan called from grow + sweep paths"
      pattern: "_telemetry\\.(OnGrow|OnShrink|OnHealthFailure|OnAcquireWait|StartGrowSpan|StartShrinkSpan|StartSweepSpan)"
---

<objective>
With Plan 01's foundation in place, implement the Phase 2 behavior: composite-signal grow on the slow path, hysteretic shrink in the sweep tick, Check-hook health pass with adaptive backoff under failure storms, and the full telemetry surface (ActivitySource spans, counters, histograms, [LoggerMessage] entries). After this plan, the engine actually grows under pressure and shrinks during idle — Plan 03 then writes the tests that prove it.

Purpose: Deliver the headline Phase 2 differentiators (composite-signal grow, hysteretic shrink, adaptive sweep backoff) and the full observability surface that makes them debuggable in production. Every state transition emits structured telemetry; every locked threshold from CONTEXT is honored; every hook contract from Phase 1 is preserved.

Output: Telemetry expansion (1 ActivitySource + 3 counters + 2 histograms + 7 LoggerMessage entries), composite-signal grow integration in AcquireAsyncCore, sweep tick body that drives Check hook + shrink + backoff, all wired to the Plan 01 components.
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

# Phase 1 + Plan 01 source — already in scope.
@src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs
@src/Oragon.ElasticPool.Core/Internals/BackgroundSweeper.cs
@src/Oragon.ElasticPool.Core/Internals/PressureSampler.cs
@src/Oragon.ElasticPool.Core/Internals/SweepBackoffState.cs
@src/Oragon.ElasticPool.Core/Internals/UtilizationSampler.cs
@src/Oragon.ElasticPool.Core/Internals/WaitDurationHistogram.cs
@src/Oragon.ElasticPool.Core/Telemetry/TelemetryEmitter.cs
@src/Oragon.ElasticPool.Core/Telemetry/PoolDiagnosticsLog.cs
@src/Oragon.ElasticPool.Core/Telemetry/PoolMeterNames.cs

<interfaces>
<!-- Plan 01 internal probes Plan 02 must consume. -->

From Plan 01 BackgroundSweeper<T>:
```csharp
internal sealed class BackgroundSweeper<T> : IAsyncDisposable where T : notnull
{
    public BackgroundSweeper(ElasticPool<T> pool, ElasticPoolOptions<T> options, SweepBackoffState backoff, CancellationToken lifetimeToken);
    internal Task TickCompleted { get; }
    internal long TickCount;
    private async Task SweepLoopAsync();
    private ValueTask RunSweepTickAsync(CancellationToken ct);  // <-- Plan 02 replaces this body
    public ValueTask DisposeAsync();
}
```

From Plan 01 ElasticPool<T> internal probes:
```csharp
internal void IncrementSinceLastGrowTicks();
internal int SinceLastGrowTicks { get; }
internal void ResetSinceLastGrowTicks();
internal int CurrentTotal { get; }
internal ConcurrentQueue<PoolEntry<T>> Idle { get; }
internal Channel<TaskCompletionSource<PoolEntry<T>>> Waiters { get; }
internal ElasticPoolOptions<T> Options { get; }
internal PressureSampler<T> Pressure { get; }
internal WaitDurationHistogram WaitHistogram { get; }
internal SweepBackoffState BackoffState { get; }
```
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Extend telemetry surface — ActivitySource, counters, histograms, LoggerMessage entries</name>
  <files>
    src/Oragon.ElasticPool.Core/Telemetry/PoolMeterNames.cs,
    src/Oragon.ElasticPool.Core/Telemetry/TelemetryEmitter.cs,
    src/Oragon.ElasticPool.Core/Telemetry/PoolDiagnosticsLog.cs
  </files>
  <action>
**1. Extend PoolMeterNames.cs** with the new instrument names (locked per CONTEXT D-09..D-13 and RESEARCH §"Telemetry"):
- Add `ActivitySourceName = "Oragon.ElasticPool"` (same string as MeterName but separate constant — they are different types).
- Add `OutcomeTag = "outcome"` (used on every span per CONTEXT decision: tag with grew/shrunk/healthy/unhealthy/skipped).
- Add `GrowCount = "pool.grow.count"`, `ShrinkCount = "pool.shrink.count"`, `HealthFailures = "pool.health.failures"`.
- Add `AcquireWaitDuration = "pool.acquire.wait.duration"`, `SweepDuration = "pool.sweep.duration"` (both seconds per OTel convention — RESEARCH OQ #2).

**2. Extend TelemetryEmitter.cs** — additions only, no rewrites:
- Add `internal static readonly ActivitySource ActivitySource = new(PoolMeterNames.ActivitySourceName);` as an assembly-singleton (process lifetime, never disposed — RESEARCH Pitfall F).
- Add fields: `Counter<long> _growCount, _shrinkCount, _healthFailures;` and `Histogram<double> _acquireWaitDuration, _sweepDuration;` initialized in the ctor via `_meter.CreateCounter<long>(name, unit, description)` / `_meter.CreateHistogram<double>(name, "s", description)`.
- Add methods (one-liners; pool-name-tagged):
  - `OnGrow()`, `OnShrink()`, `OnHealthFailure()` — each `Add(1, _poolNameTag)`
  - `OnAcquireWait(TimeSpan duration)` — `_acquireWaitDuration.Record(duration.TotalSeconds, _poolNameTag)`
  - `OnSweepDuration(TimeSpan duration)` — `_sweepDuration.Record(duration.TotalSeconds, _poolNameTag)`
- Add span helpers (`StartAcquireSpan`, `StartReleaseSpan`, `StartGrowSpan(string poolName, GrowDecision decision)`, `StartShrinkSpan(string poolName, int oldTotal, int newTotal)`, `StartSweepSpan(string poolName)`, `StartHealthCheckSpan(string poolName)`).
  - All return `Activity?`. `StartActivity` returns `null` when no listener — that null propagates and downstream code uses `span?.SetTag(...)` (zero-cost no-op). Per RESEARCH §"Pattern 6", explicitly add `if (!ActivitySource.HasListeners()) return null;` ONLY in `StartSweepSpan` (where preparatory tag-prep work for per-item ActivityEvents would otherwise run). Other spans rely on the cheap `StartActivity` null-return.
- Tags on each span: always include `pool.name` and `outcome`. For `Pool.Grow` add `pool.size_after`, `grow.tripped_by_waiters`, `grow.tripped_by_utilization`, `grow.tripped_by_p95`. For `Pool.Shrink` add `pool.size_before`, `pool.size_after`. Cardinality stays bounded — no reason codes.

Reference layout (RESEARCH Example "Code Examples" §1 + §6) — follow the structure, but keep `internal sealed` and existing `Dispose()` for the per-pool `Meter`.

**3. Extend PoolDiagnosticsLog.cs** — append 7 `[LoggerMessage]` entries with EventIds 1005, 1006, 1007, 1008, 1009, 1010, 1099 (per RESEARCH Example 2):
- 1005 `Grew` — Information; `(string poolName, int old, int @new, bool tripWaiters, bool tripUtilization, bool tripP95)`
- 1006 `Shrunk` — Information; `(string poolName, int oldTotal, int newTotal)`
- 1007 `SweepStarted` — Debug; `(string poolName, double intervalSeconds)`
- 1008 `SweepCompleted` — Debug; `(string poolName, double durationMs, int @checked, int unhealthy, int shrunk)`
- 1009 `SweepFailureBackoff` — Warning; `(string poolName, double oldSec, double newSec, int consecutiveWindows)`
- 1010 `CheckUnhealthy` — Warning; `(string poolName, string reason)`
- 1099 `SweepFailed` — Error; `(string poolName, Exception ex)` (Exception last-arg signature is recognized by the source-gen).

**Allocation-free invariant** (RESEARCH Assumption A6): every new `[LoggerMessage]` uses primitive args (`int`, `double`, `bool`, `string`) — no boxing. Plan 03 includes a benchmark to verify.

**Outcome tag values** (CONTEXT decision — bounded cardinality): "grew", "shrunk", "healthy", "unhealthy", "skipped". `Pool.Acquire` and `Pool.Release` use "ok" or "canceled"; `Pool.HealthCheck` uses "healthy" or "unhealthy". No reason codes.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet build src/Oragon.ElasticPool.Core/Oragon.ElasticPool.Core.csproj /clp:ErrorsOnly && grep -v '^[[:space:]]*//' src/Oragon.ElasticPool.Core/Telemetry/PoolDiagnosticsLog.cs | grep -c 'EventId = '</automated>
  </verify>
  <done>Core compiles on net8.0/net9.0/net10.0; PoolDiagnosticsLog.cs grep gate (above) returns 11 (4 Phase 1 + 7 new); ActivitySource singleton present; PublicAPI analyzer reports zero errors (all telemetry types remain internal).</done>
</task>

<task type="auto">
  <name>Task 2: Composite-signal grow path in AcquireAsyncCore + wait-duration recording + Acquire/Release spans</name>
  <files>
    src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs
  </files>
  <action>
Implement the composite-signal grow path inside `AcquireAsyncCore` (the existing Phase 1 method). Preserve every existing Phase 1 invariant (CAS-on-_total, counter rollback, BeforeUse retry limit, OperationCanceledException semantics, ObjectDisposedException semantics). Reference: RESEARCH Example 1 + Pattern 3 + Pattern 4.

**A. Wrap Acquire path with a span** at the top of `AcquireAsync` (the public method, not the core):
```csharp
public ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken cancellationToken = default)
{
    var span = _telemetry.StartAcquireSpan();
    return AcquireAsyncCoreWithSpan(span, cancellationToken, retryCount: 0);
}

private async ValueTask<IPoolItem<T>> AcquireAsyncCoreWithSpan(Activity? span, CancellationToken ct, int retryCount)
{
    try
    {
        var item = await AcquireAsyncCore(ct, retryCount).ConfigureAwait(false);
        span?.SetTag(PoolMeterNames.OutcomeTag, "ok");
        return item;
    }
    catch (OperationCanceledException)
    {
        span?.SetTag(PoolMeterNames.OutcomeTag, "canceled");
        throw;
    }
    finally { span?.Dispose(); }
}
```
Mirror the same pattern for `ReturnAsync` (called from PoolItem.DisposeAsync) — wrap in `StartReleaseSpan()`.

**B. Insert composite-signal grow into the slow path.** In the existing `AcquireAsyncCore`, the Phase 1 loop currently does CAS-on-_total to grow up to MaxSize. Replace the CAS-loop body with a call to `_pressure.Evaluate(...)` first, then attempt grow only when ShouldGrow is true OR when currentTotal < MinSize (first-touch warmup never blocked by signals). Concretely:

```csharp
// Replace the existing while(true) CAS-grow loop with:
var waiters = Volatile.Read(ref _waitersCount);
var decision = _pressure.Evaluate(Volatile.Read(ref _total), waiters);

if (decision.ShouldGrow || Volatile.Read(ref _total) < _options.MinSize)
{
    var grew = await TryGrowAsync(decision, ct, cancellationToken).ConfigureAwait(false);
    if (grew is not null)
        return await PrepareForUseAsync(grew, ct, cancellationToken, retryCount).ConfigureAwait(false);
}
// else: fall through to wait/throw branch (Phase 1 behavior unchanged)
```

`TryGrowAsync` is a new private method that performs the existing CAS reservation, calls Factory outside any lock, resets cooldown counter, emits telemetry:
```csharp
private async ValueTask<PoolEntry<T>?> TryGrowAsync(GrowDecision decision, CancellationToken ct, CancellationToken callerCt)
{
    int currentTotal = Volatile.Read(ref _total);
    if (currentTotal >= _options.MaxSize) return null;
    if (Interlocked.CompareExchange(ref _total, currentTotal + 1, currentTotal) != currentTotal) return null;

    using var span = _telemetry.StartGrowSpan(_options.PoolName, decision);
    try
    {
        var newItem = await _options.Factory(_services, ct).ConfigureAwait(false);
        var entry = new PoolEntry<T>(newItem, _time.GetUtcNow(), _time.GetUtcNow());
        Interlocked.Exchange(ref _sinceLastGrowTicks, 0);
        _telemetry.OnGrow();
        _log.Grew(_options.PoolName, currentTotal, currentTotal + 1,
            decision.TrippedByWaiters, decision.TrippedByUtilization, decision.TrippedByP95);
        span?.SetTag(PoolMeterNames.OutcomeTag, "grew");
        return entry;
    }
    catch (OperationCanceledException)
    {
        Interlocked.Decrement(ref _total);
        span?.SetTag(PoolMeterNames.OutcomeTag, "canceled");
        throw;
    }
    catch (Exception ex)
    {
        Interlocked.Decrement(ref _total);
        _telemetry.OnFactoryFailure();
        _log.FactoryFailed(_options.PoolName, ex);
        span?.SetTag(PoolMeterNames.OutcomeTag, "factory_failed");
        await _options.FailurePolicy.HandleAsync(default, FailureKind.FactoryThrew, ex, ct).ConfigureAwait(false);
        throw;
    }
}
```

Keep MinSize-respecting warmup grow: the `|| _total < MinSize` clause guarantees the engine still climbs to MinSize regardless of pressure signals (so cold-start with `MinSize=5, InitialSize=0` still produces 5 items on demand). This preserves Phase 1 behavior for existing tests that rely on grow-on-demand without pressure.

**C. Wait-duration recording.** When the slow path falls through to the waiter park (Phase 1 `WaitBehavior.Wait` branch), record the wait duration:
```csharp
var waitStart = _time.GetTimestamp();
Interlocked.Increment(ref _waitersCount);
try
{
    // ... existing tcs / WriteAsync / await tcs.Task ...
}
finally
{
    Interlocked.Decrement(ref _waitersCount);
    var elapsed = _time.GetElapsedTime(waitStart);
    _waitHistogram.Record(elapsed);
    _telemetry.OnAcquireWait(elapsed);
}
```
This satisfies the must-have "AcquireAsyncCore records wait duration into WaitDurationHistogram" and feeds the `pool.acquire.wait.duration` histogram for OTel exporters.

**D. Update GrowAndHandoffAsync (Phase 1 method)** to call `_telemetry.OnGrow()` + `_log.Grew(...)` with `(false,false,false)` trip flags (it's a replacement-grow, not a pressure-grow). This emits a Grow telemetry event whenever the engine creates a replacement after AfterUse=Unhealthy, ensuring counter accuracy.

**E. Acquire span outcome tagging** — when `ParkAndWaitAsync` resolves, the outer `AcquireAsyncCoreWithSpan` already tags ok/canceled. No further work.

**Phase 1 invariants preserved (verify each):**
- CAS-on-_total counter rollback on Factory throw remains the only way to restore _total under failure.
- BeforeUse retry limit (10) in PrepareForUseAsync is unchanged.
- ObjectDisposedException-vs-OperationCanceledException semantics in the waiter await are unchanged.
- Sync `Acquire()` does NOT call `TryGrowAsync` (sync never blocks; Phase 1 SUMMARY rule).
- WaitBehavior.Throw still throws PoolExhaustedException synchronously when MaxSize reached.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet build /clp:ErrorsOnly && dotnet test tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj --no-build 2>&1 | tail -5 && dotnet test tests/Oragon.ElasticPool.Core.Stress/Oragon.ElasticPool.Core.Stress.csproj --configuration Release 2>&1 | tail -5</automated>
  </verify>
  <done>Core builds; all 70 Phase 1 unit tests still pass (regression guard); PingPongStressTest still passes; `grep -c TryGrowAsync src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs` >= 2 (declaration + invocation); `grep -c "_waitHistogram.Record" src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs` == 1; `grep -c "_telemetry.OnGrow" src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs` >= 2 (TryGrowAsync + GrowAndHandoffAsync).</done>
</task>

<task type="auto">
  <name>Task 3: Replace BackgroundSweeper sweep tick with health-check + shrink + backoff body; emit Pool.Sweep span and SweepStarted/Completed/Backoff logs</name>
  <files>
    src/Oragon.ElasticPool.Core/Internals/BackgroundSweeper.cs
  </files>
  <action>
Replace the Plan 01 stub `RunSweepTickAsync` body with the real implementation. Per RESEARCH §"Pattern 1" + §"Pattern 4" + §"Pattern 5".

**Tick sequence:**

1. Capture `var sweepStart = _options.TimeProvider.GetTimestamp();`.
2. Open `Pool.Sweep` ActivitySource span via `_pool.Telemetry.StartSweepSpan(_options.PoolName)`. (Add an internal `Telemetry` accessor on `ElasticPool<T>` returning the `TelemetryEmitter` — already needed by Task 2's grow path so it should already exist; if not, expose it.)
3. Log `SweepStarted` at Debug.
4. Health-check pass: enumerate the current snapshot of `_pool.Idle` (use `_pool.Idle.ToArray()` for a stable enumeration — `ConcurrentQueue<T>.ToArray` is snapshotted). For each `entry`, if `_pool.Options.Check is { } check`:
   - `int totalChecked = 0; int unhealthy = 0;`
   - For each entry: `totalChecked++;`
   - Open per-item HealthCheck span via `_pool.Telemetry.StartHealthCheckSpan(_options.PoolName)`.
   - Try `state = await check(entry.Item, ct).ConfigureAwait(false);` inside try/catch — on exception OR `state == Unhealthy`:
     - `unhealthy++; _pool.Telemetry.OnHealthFailure(); _pool.Log.CheckUnhealthy(_options.PoolName, reason);`
     - Apply failure policy: `await _pool.Options.FailurePolicy.HandleAsync(entry.Item, FailureKind.AfterUseUnhealthy, exceptionOrNull, ct).ConfigureAwait(false);` (use `AfterUseUnhealthy` kind; no `CheckUnhealthy` kind exists in the FailureKind enum per Phase 1 PublicAPI).
     - Remove the entry: best-effort `_pool.Idle.TryDequeue` until we extract THIS entry, OR equivalently — leave it (the simplest correct approach: do not remove it from `_idle` since `ConcurrentQueue` doesn't support remove-by-element; instead, mark it via a sentinel `LastReturnedAt = DateTimeOffset.MinValue` so the shrink pass evicts it on the same or next tick, and `Interlocked.Decrement(ref _pool._total)` is NOT called yet — the shrink pass owns the dequeue+decrement). Implement the marking variant; document the choice inline.
     - On the span: `healthCheckSpan?.SetTag("outcome", "unhealthy");`
   - On Healthy: `healthCheckSpan?.SetTag("outcome", "healthy");`
   - Add an `ActivityEvent("item-checked", new ActivityTagsCollection { {"result", "healthy"|"unhealthy"} })` to the parent `Pool.Sweep` span (per CONTEXT decision: 1 span/tick + per-item events).
5. Shrink pass: only if `_pool.SinceLastGrowTicks >= _pool.Options.ShrinkCooldownWindows` AND `_pool.CurrentTotal > _pool.Options.MinSize`. Walk `_pool.Idle` via `TryPeek` once; if head's `LastReturnedAt + IdleTimeout <= now` (using `_pool.Options.TimeProvider.GetUtcNow()`), `TryDequeue` it, `Interlocked.Decrement(ref _pool._total)` (use a new internal `_pool.DecrementTotal()` helper added in this task), invoke `Release` hook if configured (try/catch swallow), emit `Pool.Shrink` span via `_pool.Telemetry.StartShrinkSpan`, `_pool.Telemetry.OnShrink()`, `_pool.Log.Shrunk(...)`. Evict at most 1 item per tick (CONTEXT lock: gentle decay).
6. Increment cooldown: `_pool.IncrementSinceLastGrowTicks();` (already exists from Plan 01).
7. Update backoff: `_backoff.OnSweepResult(totalChecked, unhealthy);` if `_backoff.IntervalChanged`, `timer.Period = _backoff.CurrentInterval;` (the timer reference must be accessible — restructure `SweepLoopAsync` so `timer` is a field, OR pass it into `RunSweepTickAsync`. Pass it as a parameter for cleanliness.) Log `SweepFailureBackoff` at Warning when `IntervalChanged && CurrentInterval > PreviousInterval`.
8. End of tick: `_pool.Telemetry.OnSweepDuration(_options.TimeProvider.GetElapsedTime(sweepStart));` and `_pool.Log.SweepCompleted(...)` at Debug. `sweepSpan?.SetTag("outcome", unhealthy > 0 ? "unhealthy" : "healthy");`

**Add the missing internal helper to ElasticPool<T>**: `internal void DecrementTotal() => Interlocked.Decrement(ref _total);` — only the sweeper's shrink pass uses it. Other decrement sites (Factory failure, AfterUse Unhealthy) already have direct field access.

**Add internal Log + Telemetry accessors on ElasticPool<T>** if not already present in Plan 01 (Plan 01 didn't add them):
```csharp
internal TelemetryEmitter Telemetry => _telemetry;
internal ILogger Log => _log;
```

**Sweep failure resilience:** the existing Plan 01 `try/catch` around `RunSweepTickAsync` in `SweepLoopAsync` swallows exceptions — keep it, but on catch call `_pool.Log.SweepFailed(_options.PoolName, ex)`. The loop continues; the failure does NOT terminate the sweep.

**Cancellation safety:** every `await` inside the tick must accept the `ct` (the linked sweep CT). When cancellation fires mid-tick, the OperationCanceledException propagates out of `RunSweepTickAsync`, hits the existing `catch (OperationCanceledException) { ...break; }` in the loop, and shutdown completes within `DisposeAsync`'s `await _sweepTask`.

**Health-check span vs sweep span:** per CONTEXT, the sweep span carries per-item ActivityEvents (bounded cardinality). Per-item HealthCheck spans (one span per Check invocation) ARE allowed because each Check is a discrete user-visible operation — but they should be cheap when no listener is attached. `StartHealthCheckSpan` returns null when no listener (no `HasListeners()` guard needed because the only preparatory work is the pool-name tag).

**Telemetry-first failure handling:** on `Check` throw, log `CheckUnhealthy` with `ex.GetType().Name` as the reason; do NOT log the full exception (sweep failures of many items would explode log volume). The aggregate `SweepFailed` 1099 entry covers catastrophic per-tick failures.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet build /clp:ErrorsOnly && dotnet test tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj --no-build 2>&1 | tail -5 && dotnet test tests/Oragon.ElasticPool.Core.Stress/Oragon.ElasticPool.Core.Stress.csproj --configuration Release 2>&1 | tail -5</automated>
  </verify>
  <done>Core builds; all Phase 1 tests still pass; PingPongStressTest still passes; `grep -c "RunSweepTickAsync" src/Oragon.ElasticPool.Core/Internals/BackgroundSweeper.cs` >= 2 (decl + call); `grep -c "OnSweepResult" src/Oragon.ElasticPool.Core/Internals/BackgroundSweeper.cs` >= 1; `grep -c "SweepCompleted" src/Oragon.ElasticPool.Core/Internals/BackgroundSweeper.cs` >= 1.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| Check hook → engine | Check is consumer code that may throw, hang, or take seconds. Engine must isolate failures: per-item try/catch + sweep-level try/catch + adaptive backoff. |
| Sweep loop → idle queue | Sweep enumerates the queue concurrently with Acquire/Return. ConcurrentQueue.ToArray + TryDequeue snapshot semantics keep this safe; no exclusive lock needed. |
| ActivitySource consumer → engine | Listeners can attach/detach at any time. `StartActivity` returning null is the contract; HasListeners() is an optimization, not a correctness requirement. |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-02-02-01 | D (DoS) | Check hook running for unbounded time | accept | Per HOOK contract docs (Phase 1), Check may be expensive; the sweep loop is single-consumer so a slow Check delays only future ticks, not Acquire fast path. Caller responsible for hook timeouts. |
| T-02-02-02 | D (DoS) | Sweep amplification under broker outage | mitigate | `SweepBackoffState` (Plan 01) + this Plan's wiring: 30s -> 60s -> 120s -> 5min cap when >=50% Check returns Unhealthy across 3 consecutive ticks; resets on first clean tick. RESEARCH Pitfall A (PITFALLS Pitfall 5). |
| T-02-02-03 | D (DoS) | Grow/shrink oscillation | mitigate | Hysteresis cooldown counter (`SinceLastGrowTicks >= ShrinkCooldownWindows = 3` before any shrink), 1-item-per-tick gentle decay, MinSize floor. RESEARCH Pitfall B (PITFALLS Pitfall 7). |
| T-02-02-04 | I (Information disclosure) | Span/log tags | mitigate | Bounded cardinality: only `pool.name` + `outcome` + a small fixed set of grow-trip-by-* booleans. No item state, no exception messages in tags (only EventId-1099 SweepFailed receives the Exception, which is emitted at Error level via [LoggerMessage]'s standard Exception parameter). |
| T-02-02-05 | T (Tampering) | Concurrent _total mutation in shrink + grow | mitigate | All mutations via `Interlocked.Decrement` / `Interlocked.CompareExchange`. Plan 01 already establishes this invariant. New `DecrementTotal` helper preserves it. |
| T-02-02-06 | E (Elevation) | ActivitySource lifetime | mitigate | `internal static readonly` per assembly, never disposed (RESEARCH Pitfall F). One ActivitySource shared across all pool instances; no per-instance leakage. |
</threat_model>

<verification>
- All Phase 1 unit tests still pass on net8.0/net9.0/net10.0 (regression guard).
- Phase 1 PingPongStressTest still passes (engine unchanged on the fixed-size hot path; Phase 2 grow only fires under composite-signal pressure, which the ping-pong test does not produce).
- `dotnet build` is green with `TreatWarningsAsErrors=true`.
- No new public API surface (PublicAPI.Unshipped.txt unchanged from Plan 01 — Phase 2 grow/shrink behavior is internal). `[LoggerMessage]` partials are `internal static partial`. ActivitySource singleton is internal.
- All new `[LoggerMessage]` entries use only primitive args (boxing-free per Assumption A6) — Plan 03 benchmark verifies.
- Sweep loop terminates cleanly on DisposeAsync (Plan 01 lifecycle invariant — re-verified by the existing `DisposeDrainTests`).
</verification>

<success_criteria>
- Pool grows under sustained pressure when any of (waiter-queue >= GrowOnWaiterCount, utilization% >= GrowOnUtilizationPercent, p95 wait time >= GrowOnWaitTimeP95) is true, capped at MaxSize.
- Each grow event emits exactly one `Pool.Grow` span with attribution tags, one `pool.grow.count` increment, one `Grew` log entry, and resets the cooldown counter.
- Sweep loop drives Check hook on every idle entry per tick; Unhealthy verdicts route through FailurePolicy and emit `pool.health.failures` + `CheckUnhealthy` log + ActivityEvent.
- Shrink fires only when SinceLastGrowTicks >= ShrinkCooldownWindows AND idle-head's LastReturnedAt is older than IdleTimeout AND total > MinSize; evicts 1 item per tick.
- Sweep backoff doubles the timer Period (30s -> 60s -> 120s -> MaxBackoff cap) after 3 consecutive failure windows; resets on first clean window. Each transition logged as `SweepFailureBackoff`.
- `pool.acquire.wait.duration` histogram records every parked wait in seconds; `pool.sweep.duration` histogram records every sweep tick in seconds.
- ActivityListener attached to "Oragon.ElasticPool" observes `Pool.Acquire`, `Pool.Release`, `Pool.HealthCheck`, `Pool.Grow`, `Pool.Shrink`, `Pool.Sweep` spans across the appropriate code paths.
- All 7 new `[LoggerMessage]` EventIds (1005-1010, 1099) fire on their corresponding state transitions.
</success_criteria>

<output>
After completion, create `.planning/phases/02-elasticity-health/02-02-SUMMARY.md` documenting:
- Telemetry surface (counters/histograms/spans/logs) with their tag schema
- The grow path's exact integration point in `AcquireAsyncCore` (line numbers or method names)
- The sweep tick sequence (steps 1-8 above) as implemented
- Confirmation that Phase 1 tests + PingPongStressTest still pass
- Heads-up to Plan 03: which probes are now wired (grow telemetry, shrink telemetry, sweep span, backoff state machine) so test code can assert against them
</output>
