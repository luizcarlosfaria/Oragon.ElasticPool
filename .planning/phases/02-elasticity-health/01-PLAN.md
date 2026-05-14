---
phase: 02-elasticity-health
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/Oragon.ElasticPool/Internals/UtilizationSampler.cs
  - src/Oragon.ElasticPool/Internals/WaitDurationHistogram.cs
  - src/Oragon.ElasticPool/Internals/PressureSampler.cs
  - src/Oragon.ElasticPool/Internals/SweepBackoffState.cs
  - src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs
  - src/Oragon.ElasticPool/Internals/PoolEntry.cs
  - src/Oragon.ElasticPool/Internals/ElasticPool.cs
  - src/Oragon.ElasticPool/Builder/ElasticPoolOptions.cs
  - src/Oragon.ElasticPool/Builder/ElasticPoolBuilder.cs
  - src/Oragon.ElasticPool/PublicAPI.Unshipped.txt
autonomous: true
requirements: [ELASTIC-01, ELASTIC-02]
must_haves:
  truths:
    - "ElasticPoolOptions<T> exposes init-only props for the 7 Phase 2 tunables (GrowOnWaiterCount, GrowOnUtilizationPercent, UtilizationWindow, GrowOnWaitTimeP95, IdleTimeout, ShrinkCooldownWindows, SweepInterval, MaxBackoff) with the locked defaults."
    - "ElasticPoolBuilder<T> exposes 7 fluent methods returning the builder, each validating its argument and surfacing in PublicAPI.Unshipped.txt."
    - "ElasticPool<T> ctor constructs UtilizationSampler, PressureSampler, SweepBackoffState, and BackgroundSweeper without breaking any Phase 1 public surface."
    - "BackgroundSweeper.SweepLoopAsync is started from the engine ctor on a Task.Run, awaits PeriodicTimer.WaitForNextTickAsync(_lifetimeCts.Token), and terminates cleanly on DisposeAsync."
    - "PoolEntry<T> records LastReturnedAt; engine sets it on every return path (sync, async, finalizer)."
    - "Phase 1 PingPongStressTest still passes — Phase 2 wiring is non-regressive on the fixed-size hot path."
  artifacts:
    - path: "src/Oragon.ElasticPool/Internals/UtilizationSampler.cs"
      provides: "Allocation-free ring-buffer rolling-window sampler over (timestamp, inUse, total)"
      min_lines: 50
    - path: "src/Oragon.ElasticPool/Internals/WaitDurationHistogram.cs"
      provides: "100-sample acquire-wait p95 estimator (ring-buffered TimeSpan[])"
      min_lines: 25
    - path: "src/Oragon.ElasticPool/Internals/PressureSampler.cs"
      provides: "Composite-signal grow evaluator returning GrowDecision struct (OR-combined, all 3 signals computed for telemetry)"
      min_lines: 40
    - path: "src/Oragon.ElasticPool/Internals/SweepBackoffState.cs"
      provides: "Exponential backoff state machine (30s -> 60s -> 120s -> MaxBackoff cap on >=3 consecutive failure windows; reset on first clean sweep)"
      min_lines: 40
    - path: "src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs"
      provides: "PeriodicTimer-driven sweep loop with adaptive Period adjustment, cancellation-safe lifecycle"
      min_lines: 60
    - path: "src/Oragon.ElasticPool/Builder/ElasticPoolOptions.cs"
      provides: "Init-only options record extended with 8 Phase 2 properties (defaults locked per CONTEXT)"
      contains: "GrowOnWaiterCount"
    - path: "src/Oragon.ElasticPool/Builder/ElasticPoolBuilder.cs"
      provides: "Fluent builder extended with GrowOnWaiterCount/GrowOnUtilizationPercent/GrowOnWaitTimeP95/IdleTimeout/ShrinkCooldownWindows/SweepInterval/MaxBackoff"
      contains: ".SweepInterval"
  key_links:
    - from: "src/Oragon.ElasticPool/Internals/ElasticPool.cs"
      to: "src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs"
      via: "ctor instantiation, _lifetimeCts.Token threaded for cancellation, DisposeAsync awaits sweeper shutdown"
      pattern: "new BackgroundSweeper"
    - from: "src/Oragon.ElasticPool/Internals/ElasticPool.cs"
      to: "src/Oragon.ElasticPool/Internals/UtilizationSampler.cs"
      via: "Sample(inUse, total) called on Acquire/Release transitions with 1s debounce"
      pattern: "_util.Sample"
    - from: "src/Oragon.ElasticPool/Builder/ElasticPoolBuilder.cs"
      to: "src/Oragon.ElasticPool/Builder/ElasticPoolOptions.cs"
      via: "Build() copies all 7 new fields onto the options record"
      pattern: "GrowOnWaiterCount = _growOnWaiterCount"
---

<objective>
Add the four new internal sealed components that Phase 2 needs (UtilizationSampler, PressureSampler, SweepBackoffState, BackgroundSweeper) plus the WaitDurationHistogram p95 helper. Extend ElasticPoolOptions<T> and ElasticPoolBuilder<T> with the 7 locked tunables. Wire the components into ElasticPool<T>'s constructor and DisposeAsync. NO grow/shrink behavior is implemented yet — Plan 02 enables grow/shrink and telemetry. NO new tests are added — Plan 03 owns the test suite.

Purpose: Give Plan 02 and Plan 03 a stable, compiling foundation: every type they reference exists, every option they set is wired, every lifecycle they coordinate (sweep loop start/stop) is deterministic. The Phase 1 fixed-size engine continues to pass its anchor stress test (PingPongStressTest) because none of the new components fire grow/shrink decisions yet — the sweeper runs, but its body is a no-op pending Plan 02.

Output: 5 new internal sealed types under src/Oragon.ElasticPool/Internals/, modifications to ElasticPool.cs ctor/DisposeAsync/PoolEntry, extensions to ElasticPoolOptions and ElasticPoolBuilder, updated PublicAPI.Unshipped.txt.
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
@.planning/phases/01-core-skeleton-fixed-size-pool/03-SUMMARY.md

# Phase 1 source — do not re-read in full; the interfaces below are the contract.
@src/Oragon.ElasticPool/Internals/ElasticPool.cs
@src/Oragon.ElasticPool/Builder/ElasticPoolBuilder.cs
@src/Oragon.ElasticPool/Builder/ElasticPoolOptions.cs
@src/Oragon.ElasticPool/Internals/PoolEntry.cs
@src/Oragon.ElasticPool/PublicAPI.Unshipped.txt

<interfaces>
<!-- Phase 1 contracts the new components must integrate with. -->

From src/Oragon.ElasticPool/Internals/ElasticPool.cs:
```csharp
internal sealed class ElasticPool<T> : IElasticPool<T> where T : notnull
{
    private readonly ElasticPoolOptions<T> _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentQueue<PoolEntry<T>> _idle;
    private readonly Channel<TaskCompletionSource<PoolEntry<T>>> _waiters;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly TelemetryEmitter _telemetry;
    private readonly ILogger _log;
    private int _total; private int _inUse; private int _lifecycle;
    public Task WarmupTask { get; }
    public int MaxSize => _options.MaxSize; public int MinSize => _options.MinSize;
    public int Available => _idle.Count; public int InUse => Volatile.Read(ref _inUse);
    // Existing methods unchanged: Acquire, AcquireAsync, ReturnSync, ReturnAsync, ReturnFromFinalizer, DisposeAsync.
}
```

From src/Oragon.ElasticPool/Internals/PoolEntry.cs:
```csharp
internal sealed record PoolEntry<T>(T Item, DateTimeOffset CreatedAt) where T : notnull;
// Plan 01 EXTENDS this record — see Task 4.
```

From src/Oragon.ElasticPool/Builder/ElasticPoolOptions.cs:
```csharp
public sealed record ElasticPoolOptions<T> where T : notnull
{
    public required FactoryDelegate<T> Factory { get; init; }
    public BeforeUseDelegate<T>? BeforeUse { get; init; }
    public CheckDelegate<T>? Check { get; init; }
    public AfterUseDelegate<T>? AfterUse { get; init; }
    public ReleaseDelegate<T>? Release { get; init; }
    public required int MinSize { get; init; }
    public required int MaxSize { get; init; }
    public required int InitialSize { get; init; }
    public WaitBehavior WhenExhausted { get; init; } = WaitBehavior.Wait;
    public required IItemFailurePolicy<T> FailurePolicy { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public string PoolName { get; init; } = string.Empty;
    // Plan 01 ADDS the 8 Phase 2 properties below.
}
```
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Add Phase 2 options + builder fluent methods + PublicAPI deltas</name>
  <files>
    src/Oragon.ElasticPool/Builder/ElasticPoolOptions.cs,
    src/Oragon.ElasticPool/Builder/ElasticPoolBuilder.cs,
    src/Oragon.ElasticPool/PublicAPI.Unshipped.txt
  </files>
  <action>
**1. Extend ElasticPoolOptions<T>** with 8 init-only properties (locked defaults from CONTEXT D-01..D-08):
```csharp
public int GrowOnWaiterCount { get; init; } = 1;
public double GrowOnUtilizationPercent { get; init; } = 0.80;
public TimeSpan UtilizationWindow { get; init; } = TimeSpan.FromSeconds(30);
public TimeSpan GrowOnWaitTimeP95 { get; init; } = TimeSpan.FromMilliseconds(100);
public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
public int ShrinkCooldownWindows { get; init; } = 3;
public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);
public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);
```

**2. Extend ElasticPoolBuilder<T>** with 7 fluent methods (UtilizationWindow stays internal/options-only — not in builder per CONTEXT discretion). Each method validates and returns `this`:
- `GrowOnWaiterCount(int n)` — `n >= 1`, throws ArgumentOutOfRangeException otherwise
- `GrowOnUtilizationPercent(double p)` — `0 < p <= 1.0`
- `GrowOnWaitTimeP95(TimeSpan t)` — `t > TimeSpan.Zero`
- `IdleTimeout(TimeSpan t)` — `t > TimeSpan.Zero`
- `ShrinkCooldownWindows(int n)` — `n >= 0`
- `SweepInterval(TimeSpan t)` — `t > TimeSpan.Zero`
- `MaxBackoff(TimeSpan t)` — `t > TimeSpan.Zero`

Add backing fields with the same defaults as the options record. Update `Build()` to:
- Validate `_growOnWaiterCount <= _maxSize` (else `InvalidOperationException("GrowOnWaiterCount must not exceed MaxSize.")`)
- Validate `_sweepInterval <= _maxBackoff` (else `InvalidOperationException("SweepInterval must not exceed MaxBackoff.")`)
- Copy all 8 fields onto the new `ElasticPoolOptions<T>` initializer.

**3. Update PublicAPI.Unshipped.txt** — append (alphabetical, matching existing format):
```
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.GrowOnUtilizationPercent(double p) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.GrowOnWaitTimeP95(System.TimeSpan t) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.GrowOnWaiterCount(int n) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.IdleTimeout(System.TimeSpan t) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.MaxBackoff(System.TimeSpan t) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.ShrinkCooldownWindows(int n) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>.SweepInterval(System.TimeSpan t) -> Oragon.ElasticPool.Builder.ElasticPoolBuilder<T>!
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.GrowOnUtilizationPercent.get -> double
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.GrowOnUtilizationPercent.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.GrowOnWaitTimeP95.get -> System.TimeSpan
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.GrowOnWaitTimeP95.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.GrowOnWaiterCount.get -> int
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.GrowOnWaiterCount.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.IdleTimeout.get -> System.TimeSpan
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.IdleTimeout.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.MaxBackoff.get -> System.TimeSpan
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.MaxBackoff.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.ShrinkCooldownWindows.get -> int
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.ShrinkCooldownWindows.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.SweepInterval.get -> System.TimeSpan
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.SweepInterval.init -> void
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.UtilizationWindow.get -> System.TimeSpan
Oragon.ElasticPool.Builder.ElasticPoolOptions<T>.UtilizationWindow.init -> void
```

XML doc comments on the public builder methods explaining: "Configures the {threshold} signal for adaptive grow. Default: {locked default per CONTEXT}." Keep doc comments single-paragraph per Phase 1 conventions.

Note: `UtilizationWindow` is exposed only on the options record (not the builder) — the builder uses the locked 30s default; tests in Plan 03 set it via the options surface or fall back to the default.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet build src/Oragon.ElasticPool/Oragon.ElasticPool.csproj /clp:ErrorsOnly</automated>
  </verify>
  <done>Core compiles on net8.0/net9.0/net10.0; PublicAPI.Unshipped.txt diff contains exactly the 23 lines above; PublicAPI analyzer (RS0016/RS0017) reports zero unshipped-API errors.</done>
</task>

<task type="auto">
  <name>Task 2: Add 5 internal sealed components (UtilizationSampler, WaitDurationHistogram, PressureSampler, SweepBackoffState, BackgroundSweeper)</name>
  <files>
    src/Oragon.ElasticPool/Internals/UtilizationSampler.cs,
    src/Oragon.ElasticPool/Internals/WaitDurationHistogram.cs,
    src/Oragon.ElasticPool/Internals/PressureSampler.cs,
    src/Oragon.ElasticPool/Internals/SweepBackoffState.cs,
    src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs
  </files>
  <action>
Create 5 new files, each `internal sealed`. NO public surface added. NO grow/shrink behavior triggered yet — `BackgroundSweeper`'s sweep body is a stub that only updates the cooldown counter and the backoff state; it does NOT call grow or shrink. Plan 02 fills in the sweep body.

**1. `UtilizationSampler.cs`** — ring-buffered rolling-window sampler. Allocation-free per `Sample()`. Per RESEARCH §"Pattern 2":
```csharp
namespace Oragon.ElasticPool.Internals;

internal sealed class UtilizationSampler
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly long[] _bucketTimestampsTicks;  // -1 = empty
    private readonly int[] _bucketInUse;
    private readonly int[] _bucketTotal;
    private int _writeIndex;
    private long _lastSampleTicks;        // 1s debounce per RESEARCH OQ #3
    private readonly long _debounceTicks; // = TimeSpan.FromSeconds(1).Ticks

    public UtilizationSampler(TimeProvider time, TimeSpan window, TimeSpan bucketSize)
    {
        _time = time; _window = window;
        var n = Math.Max(1, (int)Math.Ceiling(window.TotalSeconds / bucketSize.TotalSeconds));
        _bucketTimestampsTicks = new long[n];
        Array.Fill(_bucketTimestampsTicks, -1L);
        _bucketInUse = new int[n];
        _bucketTotal = new int[n];
        _debounceTicks = TimeSpan.FromSeconds(1).Ticks;
    }

    public void Sample(int inUse, int total)
    {
        var nowTicks = _time.GetUtcNow().UtcTicks;
        var last = Volatile.Read(ref _lastSampleTicks);
        if (nowTicks - last < _debounceTicks) return;
        if (Interlocked.CompareExchange(ref _lastSampleTicks, nowTicks, last) != last) return;
        var idx = (int)((uint)Interlocked.Increment(ref _writeIndex) - 1) % _bucketTimestampsTicks.Length;
        Volatile.Write(ref _bucketTimestampsTicks[idx], nowTicks);
        _bucketInUse[idx] = inUse;
        _bucketTotal[idx] = total;
    }

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
Use bucketSize = 1s (so 30 buckets at default 30s window). Tests in Plan 03 will exercise the debounce by advancing FakeTimeProvider in <1s and >1s steps.

**2. `WaitDurationHistogram.cs`** — 100-sample ring buffer of wait durations; `P95` returns the 95th-percentile entry of the populated samples (sort the populated portion on read; cheap at 100 samples). Per RESEARCH §"Pattern 3" (assumption A4 — "in-engine p95 estimate"):
```csharp
namespace Oragon.ElasticPool.Internals;

internal sealed class WaitDurationHistogram
{
    private const int Capacity = 100;
    private readonly long[] _ticks = new long[Capacity];   // -1 = empty
    private int _writeIndex;
    private readonly object _readLock = new();

    public WaitDurationHistogram() { Array.Fill(_ticks, -1L); }

    public void Record(TimeSpan duration)
    {
        var idx = (int)((uint)Interlocked.Increment(ref _writeIndex) - 1) % Capacity;
        Volatile.Write(ref _ticks[idx], duration.Ticks);
    }

    public TimeSpan P95
    {
        get
        {
            lock (_readLock)
            {
                Span<long> snapshot = stackalloc long[Capacity];
                int populated = 0;
                for (int i = 0; i < Capacity; i++)
                {
                    var v = Volatile.Read(ref _ticks[i]);
                    if (v >= 0) snapshot[populated++] = v;
                }
                if (populated == 0) return TimeSpan.Zero;
                snapshot[..populated].Sort();
                var idx = (int)Math.Ceiling(populated * 0.95) - 1;
                if (idx < 0) idx = 0;
                return TimeSpan.FromTicks(snapshot[idx]);
            }
        }
    }
}
```

**3. `PressureSampler.cs`** — composite-signal evaluator, returns `GrowDecision` struct. OR-combined, all 3 signals computed (no short-circuit) so telemetry can attribute. Per RESEARCH §"Pattern 3":
```csharp
namespace Oragon.ElasticPool.Internals;

internal readonly record struct GrowDecision(
    bool ShouldGrow, int CurrentTotal,
    bool TrippedByWaiters, bool TrippedByUtilization, bool TrippedByP95);

internal sealed class PressureSampler<T> where T : notnull
{
    private readonly Builder.ElasticPoolOptions<T> _options;
    private readonly UtilizationSampler _util;
    private readonly WaitDurationHistogram _wait;

    public PressureSampler(Builder.ElasticPoolOptions<T> options, UtilizationSampler util, WaitDurationHistogram wait)
    { _options = options; _util = util; _wait = wait; }

    public WaitDurationHistogram WaitHistogram => _wait;
    public UtilizationSampler Utilization => _util;

    public GrowDecision Evaluate(int currentTotal, int currentWaiters)
    {
        if (currentTotal >= _options.MaxSize)
            return new GrowDecision(false, currentTotal, false, false, false);
        var byWaiters = currentWaiters >= _options.GrowOnWaiterCount;
        var byUtilization = _util.AverageUtilization() >= _options.GrowOnUtilizationPercent;
        var byP95 = _wait.P95 >= _options.GrowOnWaitTimeP95;
        return new GrowDecision(byWaiters | byUtilization | byP95, currentTotal, byWaiters, byUtilization, byP95);
    }
}
```

**4. `SweepBackoffState.cs`** — exponential backoff state machine. Per RESEARCH §"Pattern 5":
```csharp
namespace Oragon.ElasticPool.Internals;

internal sealed class SweepBackoffState
{
    private readonly TimeSpan _baseInterval;
    private readonly TimeSpan _maxBackoff;
    private int _consecutiveFailureWindows;
    private TimeSpan _currentInterval;
    public TimeSpan CurrentInterval => _currentInterval;
    public TimeSpan PreviousInterval { get; private set; }
    public bool IntervalChanged { get; private set; }
    public int ConsecutiveFailureWindows => _consecutiveFailureWindows;

    public SweepBackoffState(TimeSpan baseInterval, TimeSpan maxBackoff)
    { _baseInterval = baseInterval; _maxBackoff = maxBackoff; _currentInterval = baseInterval; PreviousInterval = baseInterval; }

    public void OnSweepResult(int totalChecked, int unhealthy)
    {
        var prev = _currentInterval;
        if (totalChecked == 0) { IntervalChanged = false; return; }
        if (unhealthy * 2 >= totalChecked)
        {
            _consecutiveFailureWindows++;
            if (_consecutiveFailureWindows >= 3)
            {
                var doubled = TimeSpan.FromTicks(_currentInterval.Ticks * 2);
                _currentInterval = doubled > _maxBackoff ? _maxBackoff : doubled;
            }
        }
        else
        {
            _consecutiveFailureWindows = 0;
            _currentInterval = _baseInterval;
        }
        PreviousInterval = prev;
        IntervalChanged = _currentInterval != prev;
    }
}
```

**5. `BackgroundSweeper.cs`** — PeriodicTimer-driven loop, body is a stub that increments cooldown and updates backoff. Plan 02 will replace `RunSweepTickAsync`'s body with the real grow/shrink/health logic.
```csharp
namespace Oragon.ElasticPool.Internals;

internal sealed class BackgroundSweeper<T> : IAsyncDisposable where T : notnull
{
    private readonly ElasticPool<T> _pool;
    private readonly Builder.ElasticPoolOptions<T> _options;
    private readonly SweepBackoffState _backoff;
    private readonly CancellationTokenSource _sweepCts;
    private readonly Task _sweepTask;

    // Test-only probe; resets each tick. NOT exposed via PublicAPI — internal-only signal for Plan 03 tests via [InternalsVisibleTo].
    private TaskCompletionSource _tickCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task TickCompleted => _tickCompleted.Task;
    internal long TickCount; // diagnostic counter; Volatile.Read in tests

    public BackgroundSweeper(ElasticPool<T> pool, Builder.ElasticPoolOptions<T> options, SweepBackoffState backoff, CancellationToken lifetimeToken)
    {
        _pool = pool; _options = options; _backoff = backoff;
        _sweepCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _sweepTask = Task.Run(SweepLoopAsync);
    }

    private async Task SweepLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_backoff.CurrentInterval, _options.TimeProvider);
            while (await timer.WaitForNextTickAsync(_sweepCts.Token).ConfigureAwait(false))
            {
                var prevTcs = Interlocked.Exchange(ref _tickCompleted, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                try
                {
                    await RunSweepTickAsync(_sweepCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { prevTcs.TrySetResult(); break; }
                catch (Exception)
                {
                    // Plan 02 will route to PoolDiagnosticsLog.SweepFailed. For Plan 01, swallow defensively.
                }
                finally
                {
                    Interlocked.Increment(ref TickCount);
                    prevTcs.TrySetResult();
                }
                if (_backoff.IntervalChanged) timer.Period = _backoff.CurrentInterval;
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
    }

    // Plan 01 stub: increments cooldown counter only. Plan 02 replaces this body with health-check pass + shrink pass + telemetry.
    private ValueTask RunSweepTickAsync(CancellationToken ct)
    {
        _pool.IncrementSinceLastGrowTicks();
        _backoff.OnSweepResult(totalChecked: 0, unhealthy: 0);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try { _sweepCts.Cancel(); } catch { }
        try { await _sweepTask.ConfigureAwait(false); } catch { /* cancellation expected */ }
        _sweepCts.Dispose();
    }
}
```
Note: `BackgroundSweeper<T>` is generic on `T` so it can carry `ElasticPool<T>` reference and `ElasticPoolOptions<T>`. The class is `internal sealed`; only the engine constructs it.

**Concurrency / cancellation invariants** every component must hold:
- All public/internal methods on these components are safe to call concurrently with each other (already proven by `Volatile.Read`/`Interlocked` patterns).
- `BackgroundSweeper.DisposeAsync` MUST complete even if a tick is in flight (cancellation on `_sweepCts` causes `WaitForNextTickAsync` to throw OCE → loop exits → task completes).
- No new locks introduced in the hot Acquire path. `WaitDurationHistogram._readLock` is taken ONLY on `P95` read (sweep tick + slow-path Acquire), never on `Record` (hot path).
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet build src/Oragon.ElasticPool/Oragon.ElasticPool.csproj /clp:ErrorsOnly && grep -c "internal sealed" src/Oragon.ElasticPool/Internals/UtilizationSampler.cs src/Oragon.ElasticPool/Internals/WaitDurationHistogram.cs src/Oragon.ElasticPool/Internals/SweepBackoffState.cs src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs</automated>
  </verify>
  <done>Five files exist; `internal sealed` declared on each (4 of them produce 1 match each; PressureSampler is generic so grep target adjusted at exec time); Core compiles on all 3 TFMs; PublicAPI analyzer reports no new public-surface errors (internal types are invisible).</done>
</task>

<task type="auto">
  <name>Task 3: Wire components into ElasticPool<T>; extend PoolEntry; record sample on Acquire/Release; surface internal probes for Plan 03 tests</name>
  <files>
    src/Oragon.ElasticPool/Internals/PoolEntry.cs,
    src/Oragon.ElasticPool/Internals/ElasticPool.cs
  </files>
  <action>
**1. Extend PoolEntry<T>** to record `LastReturnedAt` (mutable to avoid record-with churn — change to `internal sealed class` per RESEARCH §"Pattern 4" rationale):
```csharp
namespace Oragon.ElasticPool.Internals;

internal sealed class PoolEntry<T> where T : notnull
{
    public T Item { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastReturnedAt { get; set; }

    public PoolEntry(T item, DateTimeOffset createdAt, DateTimeOffset? lastReturnedAt = null)
    {
        Item = item;
        CreatedAt = createdAt;
        LastReturnedAt = lastReturnedAt ?? createdAt;
    }

    public void Deconstruct(out T item, out DateTimeOffset createdAt)
    { item = Item; createdAt = CreatedAt; }
}
```
Rationale: changing from `record` to `class` breaks no public surface (PoolEntry is `internal`). The `Deconstruct` method preserves any existing `(item, createdAt)` pattern usages in the engine.

**2. Modify ElasticPool<T>.cs** — additions only, no rewrites of existing logic:

a. Add fields:
```csharp
private readonly UtilizationSampler _utilSampler;
private readonly WaitDurationHistogram _waitHistogram;
private readonly PressureSampler<T> _pressure;
private readonly SweepBackoffState _backoffState;
private readonly BackgroundSweeper<T> _sweeper;
private int _waitersCount;          // tracked alongside _total/_inUse for PressureSampler
private int _sinceLastGrowTicks;    // hysteresis cooldown counter (Plan 02 reads/resets)
```

b. In ctor, after `_log = ...`, BEFORE `WarmupTask = WarmupAsync(...)`:
```csharp
_utilSampler = new UtilizationSampler(_time, options.UtilizationWindow, TimeSpan.FromSeconds(1));
_waitHistogram = new WaitDurationHistogram();
_pressure = new PressureSampler<T>(options, _utilSampler, _waitHistogram);
_backoffState = new SweepBackoffState(options.SweepInterval, options.MaxBackoff);
_sweeper = new BackgroundSweeper<T>(this, options, _backoffState, _lifetimeCts.Token);
```

c. Add internal helpers (consumed by BackgroundSweeper Plan 01 stub and Plan 02 grow/shrink):
```csharp
internal void IncrementSinceLastGrowTicks() => Interlocked.Increment(ref _sinceLastGrowTicks);
internal int SinceLastGrowTicks => Volatile.Read(ref _sinceLastGrowTicks);
internal void ResetSinceLastGrowTicks() => Interlocked.Exchange(ref _sinceLastGrowTicks, 0);
internal int CurrentTotal => Volatile.Read(ref _total);
internal ConcurrentQueue<PoolEntry<T>> Idle => _idle;
internal Channel<TaskCompletionSource<PoolEntry<T>>> Waiters => _waiters;
internal ElasticPoolOptions<T> Options => _options;
internal PressureSampler<T> Pressure => _pressure;
internal UtilizationSampler UtilSampler => _utilSampler;
internal WaitDurationHistogram WaitHistogram => _waitHistogram;
internal SweepBackoffState BackoffState => _backoffState;
internal BackgroundSweeper<T> Sweeper => _sweeper;   // exposed for Plan 03 test probes
```

d. Sample on Acquire/Release transitions — minimal touch points:
- In `PrepareForUseAsync`, after `Interlocked.Increment(ref _inUse)` (the existing line), add:
  `_utilSampler.Sample(Volatile.Read(ref _inUse), Volatile.Read(ref _total));`
- In `ReturnSync`, after `Interlocked.Decrement(ref _inUse)`, set `entry.LastReturnedAt = _time.GetUtcNow();` and add the same `Sample(...)` call.
- In sync `Acquire()` after `Interlocked.Increment(ref _inUse); _telemetry.OnAcquire();`, add the same `Sample(...)` call.
- In Phase 1 `WarmupAsync`, when enqueueing the new entry, set `LastReturnedAt = _time.GetUtcNow()` via the new ctor signature.

e. Track `_waitersCount` for `PressureSampler`. The Phase 1 engine writes `tcs` to `_waiters` channel but does NOT keep a counter. Add:
- `Interlocked.Increment(ref _waitersCount);` immediately before `await _waiters.Writer.WriteAsync(tcs, ct).ConfigureAwait(false);`
- `Interlocked.Decrement(ref _waitersCount);` in a `finally` block around the `tcs.Task` await (covers both completion and cancellation paths — see Plan 02 for full grow integration).

f. Update `DisposeAsync`: after `_lifetimeCts.Cancel();` add `try { await _sweeper.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }` BEFORE the waiter drain. The sweeper must shut down before we drain so it cannot race with `_idle.TryDequeue` in the drain path.

g. Update construction calls of `new PoolEntry<T>(item, _time.GetUtcNow())` (3 sites: WarmupAsync, AcquireAsyncCore grow path, GrowAndHandoffAsync) to use the new 3-arg ctor with `lastReturnedAt: _time.GetUtcNow()` for fresh items.

**Non-goals for Plan 01 (handled by Plan 02):**
- AcquireAsyncCore does NOT call PressureSampler. The slow path stays the Phase 1 CAS loop. Plan 02 inserts the composite-signal grow path.
- Wait-duration recording (`_waitHistogram.Record(...)`) is NOT yet wired into the slow path. Plan 02 adds it.
- BackgroundSweeper does NOT yet invoke Check hooks or shrink. Stub body only.

**Phase 1 regression guard:** the `MaxSize=1 PingPongStressTest` (existing in `tests/Oragon.ElasticPool.Stress`) MUST continue to pass. The verify command runs it.

**InternalsVisibleTo for tests:** add to the .csproj if not already present (Phase 1 SUMMARY mentions DynamicProxyGenAssembly2). Add `<InternalsVisibleTo Include="Oragon.ElasticPool.Tests" />` so Plan 03 can reach `_sweeper.TickCompleted` and the `internal` probes added in step (c).
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet build /clp:ErrorsOnly && dotnet test tests/Oragon.ElasticPool.Tests/Oragon.ElasticPool.Tests.csproj --no-build --configuration Debug -- --report-trx false 2>&1 | tail -5 && dotnet test tests/Oragon.ElasticPool.Stress/Oragon.ElasticPool.Stress.csproj --configuration Release 2>&1 | tail -5</automated>
  </verify>
  <done>Solution builds on all 3 TFMs; Phase 1's 70 unit tests still pass (no regression); Phase 1's PingPongStressTest still passes (~300ms wall-clock, well under 25s watchdog); `grep -c LastReturnedAt src/Oragon.ElasticPool/Internals/PoolEntry.cs` >= 1; `grep -c "_sweeper" src/Oragon.ElasticPool/Internals/ElasticPool.cs` >= 3 (field + ctor init + DisposeAsync teardown).</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| consumer code → engine internals | All Phase 2 components are `internal sealed`; consumer cannot construct or subclass them. Only the public builder + DI extension reaches them. |
| sweep loop → process lifetime | `BackgroundSweeper` runs on the threadpool; an unhandled exception inside the loop body would terminate the process. Mitigated by the swallow-and-continue pattern in `SweepLoopAsync`. |
| FakeTimeProvider (test-only) → engine | Tests inject `FakeTimeProvider` via `WithTimeProvider`. There is no production-time path that lets a malicious `TimeProvider` corrupt state — `TimeProvider.System` is the default and the tunables themselves are validated by the builder. |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-02-01-01 | T (Tampering) | `ElasticPoolOptions<T>` Phase 2 init-only props | mitigate | Properties are `init`-only; only `ElasticPoolBuilder<T>.Build()` writes them. Builder validates each (nonneg/positive/range) before construction. |
| T-02-01-02 | D (DoS) | `BackgroundSweeper.SweepLoopAsync` | mitigate | A single tick failure does NOT kill the loop — `try/catch (Exception)` inside the loop swallows + Plan 02 will log via `SweepFailed`. Sweep cancellation on `_lifetimeCts` is the only legitimate exit. |
| T-02-01-03 | D (DoS) | `WaitDurationHistogram.P95` (lock taken on read) | accept | The lock is held for ~100 long copies + sort — microseconds. Held only on sweep tick + slow-path Acquire (not hot path). Single-writer (slow path) + single-reader (sweep) means contention is rare. |
| T-02-01-04 | I (Information disclosure) | New `internal sealed` types | accept | `internal sealed` + PublicAPI analyzer (RS0016) prevents accidental public exposure. No PII/secrets handled by these components. |
| T-02-01-05 | T (Tampering) | `_sinceLastGrowTicks` counter | mitigate | All access via `Interlocked` / `Volatile` primitives — no torn writes on 32-bit, deterministic ordering. |
</threat_model>

<verification>
- All Phase 1 unit tests still pass (`tests/Oragon.ElasticPool.Tests`) on net8.0/net9.0/net10.0.
- Phase 1 anchor stress test (`PingPongStressTest`) still passes — proves the Phase 2 wiring is non-regressive on the fixed-size hot path.
- `dotnet build` is green with `TreatWarningsAsErrors=true` (Phase 1 invariant).
- PublicAPI analyzer (RS0016/RS0017) reports zero unshipped-API errors after Task 1's `PublicAPI.Unshipped.txt` update.
- No new compile warnings in `Internals/` (sealed classes, nullable enabled).
- `grep -nE "internal sealed" src/Oragon.ElasticPool/Internals/{UtilizationSampler,WaitDurationHistogram,SweepBackoffState,BackgroundSweeper}.cs` returns >=4 hits (one per file).
- `grep -nE "internal sealed class PressureSampler" src/Oragon.ElasticPool/Internals/PressureSampler.cs` returns 1 hit.
</verification>

<success_criteria>
- Builder fluent surface ships 7 new methods, each validating its argument and returning the builder.
- Options record carries the 8 Phase 2 properties (7 builder-exposed + UtilizationWindow internal-default).
- 5 new internal sealed types compile and integrate cleanly with the Phase 1 engine.
- ElasticPool<T> ctor instantiates all components and starts the sweep loop; DisposeAsync awaits sweeper teardown before draining the idle queue.
- PoolEntry<T> records LastReturnedAt; engine sets it on every return path including warmup, sync return, async return, finalizer return.
- Phase 1 tests + stress test remain green (regression guard).
- `[InternalsVisibleTo("Oragon.ElasticPool.Tests")]` is in place so Plan 03 can reach the test probes (`Sweeper.TickCompleted`, internal accessors).
</success_criteria>

<output>
After completion, create `.planning/phases/02-elasticity-health/02-01-SUMMARY.md` documenting:
- The 5 new internal types and their public-to-engine method surface
- The 7 new builder methods with their validation rules
- The 8 new options properties with locked defaults
- Confirmation that Phase 1 tests + PingPongStressTest still pass
- Heads-up to Plan 02: which TODO comments mark "Plan 02 fills this in" sites (BackgroundSweeper.RunSweepTickAsync body, AcquireAsyncCore composite-signal grow insertion point, _waitHistogram.Record wiring)
</output>
