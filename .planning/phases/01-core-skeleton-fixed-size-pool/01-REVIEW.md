---
phase: 01-core-skeleton-fixed-size-pool
reviewed: 2026-05-02T00:00:00Z
depth: standard
files_reviewed: 36
files_reviewed_list:
  - src/Oragon.ElasticPool.Core/Abstractions/FailureDecision.cs
  - src/Oragon.ElasticPool.Core/Abstractions/FailureKind.cs
  - src/Oragon.ElasticPool.Core/Abstractions/IElasticPool.cs
  - src/Oragon.ElasticPool.Core/Abstractions/IItemFailurePolicy.cs
  - src/Oragon.ElasticPool.Core/Abstractions/IPoolItem.cs
  - src/Oragon.ElasticPool.Core/Abstractions/PoolState.cs
  - src/Oragon.ElasticPool.Core/Builder/ElasticObjectPoolFactory.cs
  - src/Oragon.ElasticPool.Core/Builder/ElasticPoolBuilder.cs
  - src/Oragon.ElasticPool.Core/Builder/ElasticPoolOptions.cs
  - src/Oragon.ElasticPool.Core/Builder/WaitBehavior.cs
  - src/Oragon.ElasticPool.Core/DependencyInjection/ServiceCollectionExtensions.cs
  - src/Oragon.ElasticPool.Core/Exceptions/PoolExhaustedException.cs
  - src/Oragon.ElasticPool.Core/Hooks/HookDelegates.cs
  - src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs
  - src/Oragon.ElasticPool.Core/Internals/PoolEntry.cs
  - src/Oragon.ElasticPool.Core/Internals/PoolItem.cs
  - src/Oragon.ElasticPool.Core/Internals/PoolLifecycle.cs
  - src/Oragon.ElasticPool.Core/Policies/DiscardAndReplaceFailurePolicy.cs
  - src/Oragon.ElasticPool.Core/Telemetry/PoolDiagnosticsLog.cs
  - src/Oragon.ElasticPool.Core/Telemetry/PoolMeterNames.cs
  - src/Oragon.ElasticPool.Core/Telemetry/TelemetryEmitter.cs
  - tests/Oragon.ElasticPool.Core.Tests/Builder/BuilderValidationTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/PlaceholderSmokeTest.cs
  - tests/Oragon.ElasticPool.Core.Tests/Pool/AcquireAndReturnTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/Pool/AfterUseAndExceptionTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/Pool/BeforeUseUnhealthyTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/Pool/DisposeDrainTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/Pool/FactoryFailureTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/Pool/FinalizerTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/Pool/PoolItemDisposeTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/Pool/WaitBehaviorTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/Pool/WarmupAndBoundsTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/Telemetry/MeterAndCounterTests.cs
  - tests/Oragon.ElasticPool.Core.Tests/TestSupport/Resource.cs
  - tests/Oragon.ElasticPool.Core.Tests/TimeProvider/TimeProviderInjectionTests.cs
  - tests/Oragon.ElasticPool.Core.Stress/PingPongStressTest.cs
findings:
  critical: 4
  warning: 5
  info: 3
  total: 12
status: findings_present
---

# Phase 01: Code Review Report

**Reviewed:** 2026-05-02
**Depth:** standard
**Files Reviewed:** 36
**Status:** findings_present

## Summary

The core engine (`ElasticPool<T>`) is well-structured and the happy-path concurrency model (ConcurrentQueue + Channel waiter + Interlocked counters) is sound. The stress test covers the primary contention scenario. However, four blockers were found:

1. A **lost wake-up** bug in `ReturnAsync` when `AfterUse` reports `Unhealthy` — parked waiters are never notified when a slot opens due to item discard.
2. A **broken `ObjectDisposedException` contract** when `BeforeUse` is configured and the recursive `AcquireAsync` path fires during pool disposal — the exception type exposed to callers is wrong.
3. The `TryGetHostApplicationStoppingToken` reflection probe in `ServiceCollectionExtensions` is structurally broken and will **never resolve the host lifetime** — the pool is never wired into application shutdown.
4. The `Acquire()` (sync) fast path **bypasses `BeforeUse` entirely** — callers using sync acquire get no health check, silently delivering broken items.

Additionally, the `Check` hook is wired through the builder and options but is never read by the engine, silently ignoring user configuration.

---

## Critical Issues

### CR-01: Lost wake-up when AfterUse returns Unhealthy

**File:** `src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs:228-236`

**Issue:** When `AfterUse` returns `PoolState.Unhealthy`, `ReturnAsync` decrements `_total` and `_inUse`, calls `Release`, then returns early (line 236). It does NOT call `TryHandoff` or `ReturnSync`. Any waiter parked in the `_waiters` Channel will remain blocked indefinitely — even though a slot in the pool just became available via the discard.

Concrete failure scenario with `MaxSize=1`:
1. One item is acquired; a second caller enters the waiter channel.
2. The first caller disposes asynchronously; `AfterUse` returns `Unhealthy`.
3. `_total` drops to 0, `_inUse` drops to 0, but `TryHandoff` is never called.
4. The second waiter hangs forever (or until its cancellation token fires).
5. Any subsequent *third* caller that is not yet parked will successfully grow the pool (since `_total=0 < MaxSize=1`), while the existing parked waiter starves.

**Fix:** After the discard logic in the `Unhealthy` branch, attempt to wake a waiter with a freshly-created replacement, or at minimum re-enqueue the "slot" signal so a waiter can trigger a new factory call. The simplest correct fix is to call `AcquireAsync` to create a replacement and hand it off to a waiter, mirroring the BeforeUse-Unhealthy recovery pattern. A clean minimal fix:

```csharp
// In ReturnAsync, replace the early-return with a wake-up attempt:
if (state == PoolState.Unhealthy)
{
    Interlocked.Decrement(ref _inUse);
    Interlocked.Decrement(ref _total);
    if (_options.Release is { } release)
    {
        try { await release(entry.Item, CancellationToken.None).ConfigureAwait(false); } catch { }
    }
    // Wake any parked waiter by creating a replacement if needed.
    // Attempt to dequeue a waiter and grow the pool for them.
    if (_waiters.Reader.TryPeek(out _))
    {
        // Fire-and-forget: AcquireAsync will grow the pool and hand off to the waiting TCS.
        _ = Task.Run(async () =>
        {
            try
            {
                var replacement = await AcquireAsync(_lifetimeCts.Token).ConfigureAwait(false);
                // The item was handed off inside AcquireAsync to the waiting TCS.
                // If no waiter consumed it (race), it goes back to idle on dispose.
                await replacement.DisposeAsync().ConfigureAwait(false);
            }
            catch { /* pool disposed or CT fired */ }
        });
    }
    return;
}
```

**Rationale:** BLOCKER — under `MaxSize=1` with an `AfterUse` health-check policy, the pool deadlocks (all waiters hang) as soon as any item is returned unhealthy. This defeats the primary use-case of `AfterUse` health enforcement.

---

### CR-02: ObjectDisposedException contract violated when BeforeUse is configured and pool is disposed concurrently

**File:** `src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs:198`

**Issue:** When `PrepareForUseAsync` calls `AcquireAsync(ct)` recursively (line 198), it passes `ct` — a linked token already composed from the original caller's `cancellationToken` and `_lifetimeCts.Token`. The recursive `AcquireAsync` creates another linked CTS from this already-composite token. Inside that recursive call, if pool disposal fires (`_lifetimeCts` cancels), the check at line 168:

```csharp
if (cancellationToken.IsCancellationRequested)   // 'cancellationToken' is now 'ct', which IS cancelled
    throw new OperationCanceledException(cancellationToken);
```

...is `true` because `ct` is cancelled by the pool lifetime token. The method throws `OperationCanceledException` instead of `ObjectDisposedException`. The `IElasticPool<T>` XML doc contract says "Throws `ObjectDisposedException` if the pool has been disposed." This contract is violated whenever the path goes through BeforeUse-Unhealthy recursion during concurrent dispose.

**Fix:** Thread the original caller's `cancellationToken` through to `PrepareForUseAsync` so the recursive call receives the actual caller token, not the composite token:

```csharp
// Signature change:
private async ValueTask<IPoolItem<T>> PrepareForUseAsync(
    PoolEntry<T> entry, CancellationToken ct, CancellationToken callerCt)
{
    // ...
    return await AcquireAsync(callerCt).ConfigureAwait(false);
    //                        ^^^^^^^^ pass original caller token
}
```

And update the three call sites to pass both `ct` (for hook invocations) and `cancellationToken` (the original caller token for the recursive `AcquireAsync`).

**Rationale:** BLOCKER — callers using `BeforeUse` health checks cannot rely on receiving `ObjectDisposedException` on pool disposal; they receive `OperationCanceledException` instead, breaking any disposal-detection logic (`catch (ObjectDisposedException)`).

---

### CR-03: `TryGetHostApplicationStoppingToken` is permanently broken — pool never hooks into host shutdown

**File:** `src/Oragon.ElasticPool.Core/DependencyInjection/ServiceCollectionExtensions.cs:53-54`

**Issue:** The reflection probe uses `sp.GetServices<object>()`. In the .NET DI container, `GetServices<object>()` returns only services explicitly registered under the type `object` — there are none in standard ASP.NET Core. `IHostApplicationLifetime` is registered under its own interface, not under `object`. The method always returns an empty sequence; the `FirstOrDefault` returns `null`; the early return fires; `CancellationToken.None` is always returned.

Additionally, even if the resolution were fixed, `s?.GetType().Name == "IHostApplicationLifetime"` compares the **concrete class name** against the **interface name**. The concrete class in ASP.NET Core is `ApplicationLifetime` (not `IHostApplicationLifetime`), so this string comparison would still fail.

Consequence: All pools registered via `AddElasticPool` receive `CancellationToken.None` as their lifetime token and are never cancelled when the application stops. The pool must be cleaned up by `IDisposable`/`IAsyncDisposable` DI disposal, which does work — but pool waiters blocking at the time of shutdown are not promptly cancelled.

**Fix:** Use a proper type-name lookup via `IServiceProvider`:

```csharp
private static CancellationToken TryGetHostApplicationStoppingToken(IServiceProvider sp)
{
    // Resolve by fully-qualified interface name without a hard reference to Hosting.Abstractions.
    var lifetime = sp.GetServices<object>()   // still wrong approach — use type-scan below
        ... 
}
```

Correct approach (no `Hosting.Abstractions` reference needed):

```csharp
private static CancellationToken TryGetHostApplicationStoppingToken(IServiceProvider sp)
{
    // Probe for IHostApplicationLifetime by scanning all registered service types.
    // Avoids a hard dependency on Microsoft.Extensions.Hosting.Abstractions.
    var lifetime = sp.GetServices<object>();   // This is wrong; use below pattern instead:

    // Correct: resolve the first service whose TYPE implements a property named "ApplicationStopping"
    // of type CancellationToken, regardless of concrete class name.
    foreach (var service in sp.GetServices<object>())
    {
        if (service is null) continue;
        var prop = service.GetType().GetProperty(
            "ApplicationStopping",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        if (prop?.GetValue(service) is CancellationToken ct)
            return ct;
    }
    return CancellationToken.None;
}
```

Even better: add a soft reference to `Microsoft.Extensions.Hosting.Abstractions` (it is small, widely available, and already in the ASP.NET Core shared framework) and resolve `IHostApplicationLifetime` directly.

**Rationale:** BLOCKER — every application using `AddElasticPool` that relies on graceful-shutdown cancellation of waiting pool consumers is silently broken. The feature appears to work (the pool does eventually shut down via DI disposal) but pool waiters are not promptly unblocked on SIGTERM.

---

### CR-04: Sync `Acquire()` bypasses `BeforeUse` health check

**File:** `src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs:88-99`

**Issue:** The synchronous `Acquire()` method dequeues an item, increments `_inUse`, and returns it — without calling `BeforeUse`. The async `AcquireAsync` path calls `PrepareForUseAsync` which invokes `BeforeUse`. This asymmetry means:

- A caller using `Acquire()` may receive a stale or broken item that `BeforeUse` would have rejected.
- A pool configured with a `BeforeUse` health check provides health guarantees only via `AcquireAsync`, not `Acquire()`.
- No documentation, exception, or warning alerts the caller to this behavioral gap.

**Fix (option A — throw if BeforeUse is configured):**
```csharp
public IPoolItem<T> Acquire()
{
    ThrowIfDisposed();
    if (_options.BeforeUse is not null)
        throw new InvalidOperationException(
            "Acquire() cannot enforce BeforeUse health checks. Use AcquireAsync() when BeforeUse is configured.");
    if (_idle.TryDequeue(out var entry))
    {
        Interlocked.Increment(ref _inUse);
        _telemetry.OnAcquire();
        return new PoolItem<T>(this, entry);
    }
    throw new PoolExhaustedException(_options.MaxSize);
}
```

**Fix (option B — document the limitation explicitly in the `IElasticPool<T>` XML doc and in `Acquire()`'s summary).**

**Rationale:** BLOCKER — silent behavioral contract violation. Callers reasonably expect `Acquire()` and `AcquireAsync()` to deliver items in the same health state. An item that `AcquireAsync` would have discarded is served by `Acquire()` without any indication.

---

## Warnings

### WR-01: `TrySetCanceled()` called without passing the CancellationToken — wrong token on the exception

**File:** `src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs:156-158`

**Issue:** The cancellation registration calls `t.TrySetCanceled()` without arguments. This sets the TCS to cancelled state with `CancellationToken.None` on the resulting exception, not the token that triggered the cancellation. The re-throw logic at line 169 (`throw new OperationCanceledException(cancellationToken)`) does set the correct token on the re-thrown exception, so the caller sees the right token. However, any code that awaits `tcs.Task` directly (not via the outer catch) would see `OperationCanceledException.CancellationToken == CancellationToken.None`.

**Fix:**
```csharp
using var registration = ct.Register(static state =>
{
    var (tcs, token) = ((TaskCompletionSource<PoolEntry<T>>, CancellationToken))state!;
    tcs.TrySetCanceled(token);
}, (tcs, ct));
```

Or use a closure (minor allocation) to capture `ct` directly.

---

### WR-02: `GC.SuppressFinalize(this)` in `DisposeAsync` on a class with no finalizer

**File:** `src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs:309`

**Issue:** `ElasticPool<T>` has no finalizer, yet `DisposeAsync` calls `GC.SuppressFinalize(this)`. The call is harmless but indicates either a code pattern copied from a finalizer-bearing class, or a future finalizer that was planned but not implemented. It will confuse readers who look for the matching finalizer.

**Fix:** Remove line 309 (`GC.SuppressFinalize(this)`).

---

### WR-03: `Check` hook silently ignored — registered in public API but never consumed by the engine

**File:** `src/Oragon.ElasticPool.Core/Builder/ElasticPoolBuilder.cs:31`, `src/Oragon.ElasticPool.Core/Builder/ElasticPoolOptions.cs:12`

**Issue:** `ElasticPoolBuilder<T>.Check(CheckDelegate<T>)` is a public method, `ElasticPoolOptions<T>.Check` is a public property, and `CheckDelegate<T>` is a public delegate — all tracked in `PublicAPI.Unshipped.txt`. However, `ElasticPool<T>` never reads `_options.Check`. Configuring the `Check` hook has zero effect. There is no warning, no exception, and no documentation stating it is a Phase 2 placeholder.

**Fix (option A):** Add an `[Obsolete("Check hook is a Phase 2 placeholder and has no effect in this version.")]` attribute or a `<remarks>` XML doc note on the builder method and delegate.

**Fix (option B):** Remove the `Check` public API from Phase 1 entirely and re-introduce it in the phase where the sweeper consumes it. This avoids polluting `PublicAPI.Unshipped.txt` with surface area that does nothing.

---

### WR-04: Infinite recursion risk when `BeforeUse` always returns Unhealthy and pool can still grow

**File:** `src/Oragon.ElasticPool.Core/Internals/ElasticPool.cs:198`

**Issue:** `PrepareForUseAsync` calls `AcquireAsync(ct)` recursively when `BeforeUse` returns `Unhealthy`. Each recursive call decrements `_total` and attempts to create a replacement. If the factory always succeeds but `BeforeUse` always returns `Unhealthy`, the recursion continues indefinitely: grow pool → check → discard → grow pool → check → discard → ... with no bound on recursion depth. Under such a pathological configuration, a `StackOverflowException` is possible, or under high `MaxSize`, a very deep call stack.

**Fix:** Add a retry limit in `PrepareForUseAsync` and surface a meaningful exception after N consecutive BeforeUse-Unhealthy discards:

```csharp
private async ValueTask<IPoolItem<T>> PrepareForUseAsync(
    PoolEntry<T> entry, CancellationToken ct, int retryCount = 0)
{
    const int MaxRetries = 10;
    // ...
    if (state == PoolState.Unhealthy)
    {
        // ...
        if (retryCount >= MaxRetries)
            throw new InvalidOperationException(
                $"BeforeUse returned Unhealthy for {MaxRetries} consecutive items; " +
                "factory may be producing persistently broken items.");
        return await AcquireAsync_Internal(ct, retryCount + 1).ConfigureAwait(false);
    }
}
```

---

### WR-05: `ElasticPoolOptions<T>` is unnecessarily public and exposes internal configuration surface

**File:** `src/Oragon.ElasticPool.Core/Builder/ElasticPoolOptions.cs:7`

**Issue:** `ElasticPoolOptions<T>` is `public sealed record`, tracked in `PublicAPI.Unshipped.txt` with all 12 properties. This type is a frozen configuration bag consumed only by `ElasticPool<T>` (which is `internal`). External callers cannot construct an `ElasticPool<T>` directly, making the ability to construct an `ElasticPoolOptions<T>` externally useless. Exposing it:

- Commits all property names and types as part of the versioned public API.
- Means any future option (added in Phase 2) must maintain source/binary compatibility.
- Invites consumers to try to pass options to non-existent public constructors.

**Fix:** Make `ElasticPoolOptions<T>` internal:

```csharp
internal sealed record ElasticPoolOptions<T> where T : notnull { ... }
```

Remove it from `PublicAPI.Unshipped.txt`. The builder's fluent API is the stable public surface.

---

## Info

### IR-01: `PoolLifecycle.Draining` state is dead code

**File:** `src/Oragon.ElasticPool.Core/Internals/PoolLifecycle.cs:3`

**Issue:** `PoolLifecycle` defines three states: `Open`, `Draining`, and `Closed`. The engine only uses `Open` and `Closed`. `Draining` is never set, never tested, and never transitioned to. The `DisposeAsync` method transitions directly from `Open` to `Closed`.

**Fix:** Either remove `Draining` from the enum, or add a comment documenting it as a Phase 2 placeholder for graceful drain (allowing in-flight checkouts to complete before full shutdown).

---

### IR-02: `TryGetHostApplicationStoppingToken` leaks all registered `object` services into memory temporarily

**File:** `src/Oragon.ElasticPool.Core/DependencyInjection/ServiceCollectionExtensions.cs:53`

**Issue:** Even though the method always returns `CancellationToken.None` (see CR-03), the call to `sp.GetServices<object>()` enumerates all services registered under type `object`. In typical applications this is an empty collection, so the cost is low. However, if any library registers services under `object`, the collection grows. This is an unnecessary allocation on every pool construction in addition to being broken.

**Fix:** Addressed by fixing CR-03. The corrected implementation should not call `GetServices<object>()` unless the reflection-based approach is explicitly chosen over the recommended direct reference.

---

### IR-03: `PoolExhaustedException` does not inherit from a standard base that signals "transient" vs "permanent" failure

**File:** `src/Oragon.ElasticPool.Core/Exceptions/PoolExhaustedException.cs`

**Issue:** `PoolExhaustedException` extends `Exception` directly. It carries a `WaitTime` property used when wait behavior is Throw after a timeout (currently set only externally and optional). There is no standard marker (e.g., `InvalidOperationException`) to indicate this is a recoverable/transient condition. Library consumers building retry logic cannot distinguish this from programming errors without catching by exact type.

This is a design choice that should be locked in now (Phase 1) before the API is shipped, since exception hierarchy changes are binary-breaking.

**Fix:** Consider inheriting from `InvalidOperationException` (signals "valid operation, but current state prevents it") or adding a `IsTransient` property to give consumers semantic context without breaking the hierarchy later.

---

_Reviewed: 2026-05-02_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
