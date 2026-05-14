---
phase: 02-elasticity-health
reviewed: 2026-05-02T00:00:00Z
depth: standard
files_reviewed: 27
files_reviewed_list:
  - src/Oragon.ElasticPool/Builder/ElasticPoolBuilder.cs
  - src/Oragon.ElasticPool/Builder/ElasticPoolOptions.cs
  - src/Oragon.ElasticPool/Internals/ElasticPool.cs
  - src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs
  - src/Oragon.ElasticPool/Internals/PoolEntry.cs
  - src/Oragon.ElasticPool/Internals/PressureSampler.cs
  - src/Oragon.ElasticPool/Internals/SweepBackoffState.cs
  - src/Oragon.ElasticPool/Internals/UtilizationSampler.cs
  - src/Oragon.ElasticPool/Internals/WaitDurationHistogram.cs
  - src/Oragon.ElasticPool/Telemetry/PoolDiagnosticsLog.cs
  - src/Oragon.ElasticPool/Telemetry/PoolMeterNames.cs
  - src/Oragon.ElasticPool/Telemetry/TelemetryEmitter.cs
  - tests/Oragon.ElasticPool.Tests/Pool/BackgroundSweepTests.cs
  - tests/Oragon.ElasticPool.Tests/Pool/ElasticGrowTests.cs
  - tests/Oragon.ElasticPool.Tests/Pool/HystereticShrinkTests.cs
  - tests/Oragon.ElasticPool.Tests/Pool/PressureSamplerTests.cs
  - tests/Oragon.ElasticPool.Tests/Pool/SweepBackoffIntegrationTests.cs
  - tests/Oragon.ElasticPool.Tests/Pool/SweepBackoffStateTests.cs
  - tests/Oragon.ElasticPool.Tests/Pool/UtilizationSamplerTests.cs
  - tests/Oragon.ElasticPool.Tests/Pool/WaitDurationHistogramTests.cs
  - tests/Oragon.ElasticPool.Tests/Telemetry/ActivitySourceSpanTests.cs
  - tests/Oragon.ElasticPool.Tests/Telemetry/LoggerMessageEventTests.cs
  - tests/Oragon.ElasticPool.Tests/Telemetry/Phase2CountersAndHistogramsTests.cs
  - tests/Oragon.ElasticPool.Tests/TestSupport/CapturedActivities.cs
  - tests/Oragon.ElasticPool.Tests/TestSupport/CapturedLogEntries.cs
  - tests/Oragon.ElasticPool.Tests/TestSupport/SweepDeterminism.cs
  - tests/Oragon.ElasticPool.Stress/BurstIdleBurstStressTest.cs
findings:
  critical: 3
  warning: 5
  info: 2
  total: 10
status: findings_present
---

# Phase 02: Code Review Report

**Reviewed:** 2026-05-02T00:00:00Z
**Depth:** standard
**Files Reviewed:** 27
**Status:** findings_present

## Summary

The elasticity layer introduces a `BackgroundSweeper`, a composite-signal `PressureSampler`, ring-buffer `UtilizationSampler`, `WaitDurationHistogram`, `SweepBackoffState`, and extends `ElasticPool<T>` with `TryGrowAsync` and a hysteretic shrink path. The overall architecture is sound — the grow CAS, the direct-handoff waiter channel, the DisposeAsync sequencing (sweeper stop before drain), and the test determinism helpers are all correctly constructed.

Three correctness defects require blocking fixes before this code ships:

1. The shrink pass uses a TOCTOU pattern (TryPeek then TryDequeue) without validating that the dequeued item is actually the one that triggered the eviction condition. A concurrently returned item can be incorrectly discarded.
2. The `UtilizationSampler` writes its data fields **after** the publication timestamp, which is inverted from the required publication pattern. On ARM64 (a primary .NET target since .NET 8) a reader can observe the new timestamp with stale `inUse`/`total` data.
3. Items flagged as unhealthy by the `Check` hook remain in the idle queue and can be handed to consumers for up to `ShrinkCooldownWindows` ticks. Without `BeforeUse` configured, consumers receive items the engine has already diagnosed as broken.

Five warnings are also surfaced, covering span coverage gaps, test data-race via a non-volatile field read, and a misused `FailureKind` enum value.

---

## Critical Issues

### CR-01: Shrink pass TOCTOU — dequeued item may differ from peeked item

**File:** `src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs:150-169`

**Issue:** The shrink pass calls `TryPeek` to inspect the idle-queue head and test its `LastReturnedAt + IdleTimeout <= now`. If the check passes, it calls `TryDequeue`. Between these two calls a consumer thread can acquire the peeked (stale) entry and return it, placing it back at the tail; a different entry becomes the new head, and `TryDequeue` removes **that** entry without ever checking its age. In the degenerate single-item case the peeked entry re-queues at head (it is the only entry), so the returned-and-freshly-timestamped item is dequeued and evicted — an in-use item is destroyed.

```csharp
// CURRENT (unsafe):
if (_pool.Idle.TryPeek(out var head))
{
    if (head.LastReturnedAt + _options.IdleTimeout <= now)
    {
        if (_pool.Idle.TryDequeue(out var evict))
        {
            // evict may NOT be head — its age was never checked
            _pool.DecrementTotal();
            ...
        }
    }
}

// FIX: validate the dequeued item independently of the peek:
if (_pool.Idle.TryPeek(out _) &&
    _pool.Idle.TryDequeue(out var evict))
{
    if (evict.LastReturnedAt + _options.IdleTimeout <= now)
    {
        _pool.DecrementTotal();
        // ... release and log
    }
    else
    {
        // Not actually stale — re-enqueue at tail to preserve it.
        _pool.Idle.Enqueue(evict);
    }
}
```

**Rationale:** `ConcurrentQueue<T>` does not support atomic conditional dequeue. The peek result is advisory only. The eviction decision must be made on the item **actually dequeued**, not the item that was peeked. The re-enqueue-at-tail approach preserves pool size invariants; the only downside is a minor ordering churn, which is acceptable at sweep frequency.

---

### CR-02: UtilizationSampler writes data fields after the publication timestamp (inverted publication pattern on ARM64)

**File:** `src/Oragon.ElasticPool/Internals/UtilizationSampler.cs:38-40`

**Issue:** The write sequence in `Sample()` is:
```csharp
Volatile.Write(ref _bucketTimestampsTicks[idx], nowTicks);  // store-release — FIRST
_bucketInUse[idx] = inUse;                                  // plain store — SECOND
_bucketTotal[idx] = total;                                  // plain store — THIRD
```
`Volatile.Write` (store-release on ARM64) guarantees that all stores **prior** to this instruction are visible before the timestamp store. It makes no guarantee about stores **after** it. A reader that performs a `Volatile.Read` (load-acquire) on the timestamp can then issue plain loads on `_bucketInUse` and `_bucketTotal`, which may still see stale values from a **different** slot's write that has not yet propagated. This is a memory-ordering bug on any weakly-ordered architecture (ARM64, which is the default for .NET 8+ on Apple Silicon and AWS Graviton).

```csharp
// FIX: write data before the timestamp (correct publication pattern):
_bucketInUse[idx] = inUse;                                  // plain store — FIRST
_bucketTotal[idx] = total;                                  // plain store — SECOND
Volatile.Write(ref _bucketTimestampsTicks[idx], nowTicks);  // store-release — LAST (publish)
```

With this order, the reader's load-acquire on the timestamp is paired with the writer's store-release, and all prior plain stores (the data fields) are guaranteed visible to the reader.

**Rationale:** The publication pattern requires the sentinel (timestamp) to be written last with a store-release fence so it serves as the visibility boundary for all data written before it. The current code inverts this, eliminating the fence's protective effect for the data fields.

---

### CR-03: Unhealthy items from Check hook remain acquirable for up to ShrinkCooldownWindows ticks

**File:** `src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs:113-128`

**Issue:** When the Check hook returns `Unhealthy`, the sweeper marks the entry `LastReturnedAt = DateTimeOffset.MinValue` and defers eviction to the shrink pass. The shrink pass is gated by `SinceLastGrowTicks >= ShrinkCooldownWindows` (default: 3). During those 3 ticks — which at the default 30s interval spans up to 90 seconds — the poisoned entry remains in `_idle` and can be dequeued by any `Acquire` or `AcquireAsync` call. If `BeforeUse` is not configured (a valid configuration), the consumer receives an item the pool has already judged unhealthy.

```csharp
// CURRENT:
// Health check marks the entry, shrink pass may not run for 3 more ticks.
entry.LastReturnedAt = DateTimeOffset.MinValue;

// FIX: remove the entry from circulation immediately.
// Since ConcurrentQueue cannot remove by reference, the safest fix is to
// immediately track the entry in a dedicated discard set, and in both the
// fast-path (TryDequeue) and the PrepareForUseAsync path, skip entries that
// are in the discard set (decrement total and discard).
//
// Minimum viable fix: introduce a thread-safe HashSet<PoolEntry<T>> _pendingDiscard
// checked in the dequeue paths, with entries removed from the set once their
// TryDequeue succeeds in the sweep's shrink pass.
```

**Rationale:** The stated contract of the `Check` hook is that unhealthy items must not be served to consumers. Deferring eviction behind the cooldown gate breaks this contract for consumers who do not configure `BeforeUse`. The comment in the code acknowledges the delay ("the shrink pass evicts it on this tick **or the next**, whichever wins the cooldown gate first") but understates the window: with the default `ShrinkCooldownWindows = 3`, it is at least 2 more ticks (60s+) before the item is evicted, and during that entire period it is live in the queue.

---

## Warnings

### WR-01: PoolEntry.LastReturnedAt is a non-volatile DateTimeOffset with concurrent writers and readers

**File:** `src/Oragon.ElasticPool/Internals/PoolEntry.cs:12`

**Issue:** `LastReturnedAt` is a plain auto-property (no `Volatile`, no `Interlocked`). It is written by `ReturnSync` on the consumer thread and read by the background sweeper (health-check snapshot iteration and shrink `TryPeek`). The sweeper also writes it (`entry.LastReturnedAt = DateTimeOffset.MinValue`) while consumers can concurrently dequeue the same entry and call `ReturnSync`. `DateTimeOffset` is not an 8-byte value (it holds a `long` ticks field plus a `short` offset field, totalling 10 bytes padded to 16); reads and writes are not atomic under the CLR memory model. On ARM64, the JIT may emit a multi-instruction store/load sequence, making torn reads observable.

**Fix:** The cleanest solution given that offset is always 0 (UTC only) is to replace the `DateTimeOffset` backing with a `long _lastReturnedAtTicks` field protected by `Volatile.Read`/`Volatile.Write`, and reconstruct `DateTimeOffset` from ticks on read:
```csharp
private long _lastReturnedAtTicks;
public DateTimeOffset LastReturnedAt
{
    get => new DateTimeOffset(Volatile.Read(ref _lastReturnedAtTicks), TimeSpan.Zero);
    set => Volatile.Write(ref _lastReturnedAtTicks, value.UtcTicks);
}
```

---

### WR-02: AcquireAsyncCoreWithSpan does not tag the outcome for non-cancellation exceptions

**File:** `src/Oragon.ElasticPool/Internals/ElasticPool.cs:188-201`

**Issue:** The `Pool.Acquire` span is completed in the `finally` block regardless, but `outcome` is only set on the happy path ("ok") or for `OperationCanceledException` ("canceled"). Any other exception — `PoolExhaustedException`, `ObjectDisposedException`, `InvalidOperationException` from the BeforeUse retry limit, or a factory failure re-throw — causes the span to be disposed with no `outcome` tag. OTel backends that partition on `outcome` will silently drop or misattribute these spans.

```csharp
// FIX: add a catch-all outcome tag before rethrowing:
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
    catch
    {
        span?.SetTag(PoolMeterNames.OutcomeTag, "error");
        throw;
    }
    finally { span?.Dispose(); }
}
```

---

### WR-03: BackgroundSweeper.TickCompleted property reads _tickCompleted without a Volatile.Read

**File:** `src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs:23`

**Issue:**
```csharp
internal Task TickCompleted => _tickCompleted.Task;
```
`_tickCompleted` is updated via `Interlocked.Exchange` on the sweep task thread. Test threads read this property with a plain field access (no acquire fence). On ARM64, the test thread may observe a stale, already-completed `TaskCompletionSource` from the previous tick. Awaiting a completed `Task` returns synchronously, so the test skips waiting for the tick it just triggered, and assertions run before the tick body has finished — resulting in flaky assertions under ARM64 CI environments (macOS arm64, Graviton runners).

**Fix:**
```csharp
internal Task TickCompleted => Volatile.Read(ref _tickCompleted).Task;
```

---

### WR-04: StartHealthCheckSpan called per-idle-item in a loop without HasListeners() guard

**File:** `src/Oragon.ElasticPool/Telemetry/TelemetryEmitter.cs:136-141`
**Also:** `src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs:102`

**Issue:** `StartHealthCheckSpan` is invoked once per idle item inside the sweep health-check loop (O(n) calls per tick). It calls `ActivitySource.StartActivity(...)` unconditionally. The companion `StartSweepSpan` correctly guards with `HasListeners()` precisely because "preparatory work for per-item ActivityEvents is non-trivial." `StartActivity` itself is not free: even when no listener is attached it performs a lock-free walk of the listener list. With large pools (e.g. 1000 connections) this adds up to a measurable overhead per tick solely from `StartActivity` calls that return null.

**Fix:** Apply the same `HasListeners()` guard already used in `StartSweepSpan`:
```csharp
public Activity? StartHealthCheckSpan(string poolName)
{
    if (!ActivitySource.HasListeners()) return null;
    var a = ActivitySource.StartActivity("Pool.HealthCheck", ActivityKind.Internal);
    a?.SetTag(PoolMeterNames.PoolNameTag, poolName);
    return a;
}
```

---

### WR-05: BackgroundSweeper uses FailureKind.AfterUseUnhealthy for a background Check-hook verdict

**File:** `src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs:120`

**Issue:**
```csharp
await _pool.Options.FailurePolicy.HandleAsync(entry.Item, FailureKind.AfterUseUnhealthy, thrown, ct);
```
`FailureKind.AfterUseUnhealthy` semantically means the `AfterUse` hook reported the item unhealthy after a consumer returned it. This is a background periodic health check, not a post-use hook. A custom `IItemFailurePolicy` that branches on `FailureKind` to apply different policies (e.g., log+discard vs. reconnect) will misidentify sweep-time failures as consumer-return failures and may apply the wrong remediation path.

**Fix:** Add a dedicated enum value:
```csharp
// In FailureKind:
CheckUnhealthy = 3
```
Then use it in BackgroundSweeper:
```csharp
await _pool.Options.FailurePolicy.HandleAsync(entry.Item, FailureKind.CheckUnhealthy, thrown, ct);
```

---

## Info

### IR-01: ShrinkSpan pool.size_before tag can be inflated by concurrent grows

**File:** `src/Oragon.ElasticPool/Internals/BackgroundSweeper.cs:156-158`

**Issue:** `oldTotal` is captured from `_pool.CurrentTotal` **after** `TryDequeue` but **before** `DecrementTotal`. Between `TryDequeue` and the `Volatile.Read` for `oldTotal`, a concurrent `TryGrowAsync` CAS can increment `_total`. The resulting `pool.size_before` tag in the Shrink span can be larger than the true pre-shrink value by the number of concurrent grows. This affects dashboard accuracy for the shrink telemetry.

**Fix:** Capture `oldTotal` before `DecrementTotal`, using the return value of `Interlocked.Decrement`:
```csharp
int newTotal = Interlocked.Decrement(ref /* _total via DecrementTotal */);
int oldTotal = newTotal + 1;
```
This requires making `DecrementTotal` return the new value (or pre-decrement value), but eliminates the race entirely.

---

### IR-02: BurstIdleBurstStressTest does not prime the sweep loop before the 60-second fake advance

**File:** `tests/Oragon.ElasticPool.Stress/BurstIdleBurstStressTest.cs:91`

**Issue:** After `await Task.WhenAll(burst1)`, the test immediately calls `fake.Advance(TimeSpan.FromSeconds(60))` without first ensuring the sweep loop has returned to `WaitForNextTickAsync`. The initial `PrimeAsync` (Task.Yield + Task.Delay(100)) at lines 63–64 primes the loop before Burst 1, but the burst's 200 concurrent tasks and their `await Task.Yield()` calls leave the sweep loop in an undefined position. If the loop is mid-tick when `fake.Advance(60s)` fires, the `PeriodicTimer` may queue 2 pending ticks; the for-loop's `AdvanceAndAwaitTickAsync` then awaits the wrong TCS, and the outer 2s timeout fallback (`if (winner != tcs) break`) exits early. The assertion `pool.Available.Should().Be(5)` then fails non-deterministically on CI.

**Fix:** Add a `PrimeAsync` (or equivalent `Task.Delay(100)`) after `Task.WhenAll(burst1)` and before the first `fake.Advance`:
```csharp
await Task.WhenAll(burst1);
pool.InUse.Should().Be(0);
// Re-prime the sweep loop after the burst before driving fake time.
await Task.Yield();
await Task.Delay(100);
fake.Advance(TimeSpan.FromSeconds(60));
```

---

_Reviewed: 2026-05-02T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
