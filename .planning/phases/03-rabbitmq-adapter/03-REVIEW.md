---
phase: 03-rabbitmq-adapter
reviewed: 2026-05-02T00:00:00Z
depth: standard
files_reviewed: 24
files_reviewed_list:
  - src/Oragon.AdaptivePool.RabbitMQ/Builder/AdaptiveConnectionPoolBuilder.cs
  - src/Oragon.AdaptivePool.RabbitMQ/Builder/AdaptiveChannelPoolBuilder.cs
  - src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs
  - src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveChannelPoolServiceCollectionExtensions.cs
  - src/Oragon.AdaptivePool.RabbitMQ/Internals/AdapterDiagnosticsLog.cs
  - src/Oragon.AdaptivePool.RabbitMQ/Internals/ChannelLeasePairing.cs
  - src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionChannelTracker.cs
  - src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionFactoryResolver.cs
  - src/Oragon.AdaptivePool.RabbitMQ/Options/AdaptiveConnectionPoolOptions.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.Tests/ChannelPoolUnitTests.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.Tests/ConnectionChannelTrackerTests.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.Tests/ConnectionFactoryResolverTests.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.Tests/ConnectionPoolUnitTests.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.Tests/TestSupport/CapturedLogEntries.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/AutomaticRecoveryOverrideTests.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/BurstyPublisherIntegrationTests.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/ChannelPoolIntegrationTests.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/ChannelSpreadIntegrationTests.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/ConnectionPoolIntegrationTests.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/Fixtures/LowChannelMaxFixture.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/Fixtures/RabbitMqContainerFixture.cs
  - tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/TestSupport/CapturedLogEntries.cs
  - samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/Program.cs
  - samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/BurstyPublisherWorker.cs
findings:
  critical: 1
  warning: 4
  info: 3
  total: 8
status: issues_found
---

# Phase 03: Code Review Report

**Reviewed:** 2026-05-02
**Depth:** standard
**Files Reviewed:** 24
**Status:** issues_found

## Summary

Reviewed the full RabbitMQ adapter surface: two DI extension methods, two builder types, four internals (factory resolver, channel-lease pairing, connection-channel tracker, diagnostics log), options, unit tests, integration tests, fixtures, and the bursty-publisher sample. The Core pool engine was cross-referenced as needed to understand Release hook exception handling.

The layered pool architecture is sound in its happy path. The `ChannelLeasePairing` CWT design correctly avoids pinning dead channels in memory, and the `ConnectionChannelTracker` CAS loops are verified correct under the stated invariant (entries never stored at count=0). The 3-mode factory probe ordering and the AutomaticRecovery override are functionally correct. The sample follows Pitfall 10 discipline (per-iteration channel acquire).

One blocker stands out: `ch.DisposeAsync()` in the channel Release hook is unguarded. Because Core silently swallows all Release hook exceptions, a throw from `DisposeAsync` silently leaves the tracker slot inflated, the pairing entry live, and — most critically — the connection lease permanently stuck in `InUse`. Under sustained operation this exhausts the connection pool without any error being surfaced. Two warnings concern test reliability (one integration test validates a claim it cannot actually prove) and an unguarded IOptions resolver that accepts the default value when `HostName` is empty. Two additional warnings cover the mutation of shared factory state and the silent double-registration behaviour. Three info items are lower-risk quality gaps.

---

## Critical Issues

### CR-01: `ch.DisposeAsync()` unguarded in channel Release hook — connection lease permanently leaked on throw

**File:** `src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveChannelPoolServiceCollectionExtensions.cs:107`

**Issue:** The Release hook calls `await ch.DisposeAsync()` directly (no try/catch). If that call throws — possible when the channel's underlying TCP stream is already in an error state — all subsequent cleanup is skipped:

1. `tracker.ReleaseSlot(connLease.Value)` is never called. The `ConnectionChannelTracker` count for that connection stays elevated, causing the eager-spread logic to treat the connection as more saturated than it is.
2. `pairing.TryRemove(ch)` is never called. The `ConditionalWeakTable` retains a strong reference to the `IPoolItem<IConnection>` lease via its `Value`, preventing the lease object from being collected until `ch` itself is collected.
3. `await connLease.DisposeAsync()` is never called. The connection lease is never returned to the inner connection pool. Its `InUse` counter is never decremented, so the connection permanently counts toward `InUse`. Under steady-state recycling this accumulates until the connection pool reports fully exhausted (`PoolExhaustedException`) with no observable error — the only symptom is pool exhaustion.

This is exacerbated by Core's behaviour: all call sites that invoke the Release hook wrap it in `catch { /* swallow */ }`, so the exception is silently discarded and no diagnostic ever fires.

**Fix:** Wrap `ch.DisposeAsync()` in a try/finally (or bare try/catch) that guarantees cleanup regardless of outcome:

```csharp
.Release(async (ch, ct) =>
{
    try
    {
        await ch.CloseAsync(ct).ConfigureAwait(false);
    }
    catch { /* swallow close errors */ }

    try
    {
        await ch.DisposeAsync().ConfigureAwait(false);
    }
    catch { /* swallow dispose errors — cleanup must still run */ }

    if (pairing.TryGet(ch, out var connLease) && connLease is not null)
    {
        tracker.ReleaseSlot(connLease.Value);
        pairing.TryRemove(ch);
        try { await connLease.DisposeAsync().ConfigureAwait(false); }
        catch { /* don't mask primary exception */ }
    }
})
```

The same gap exists for the connection pool's `conn.DisposeAsync()` at line 97 of `AdaptiveConnectionPoolServiceCollectionExtensions.cs`, but the impact is lower there (no tracker or pairing to corrupt).

---

## Warnings

### WR-01: `TryAcquireSlot` dead-code branch contains a potential livelock path

**File:** `src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionChannelTracker.cs:40-43`

**Issue:** The condition `!hasEntry || current == 0` routes to `TryAdd` for both the "no entry" and "entry exists with value 0" cases. The latter is unreachable under the current invariant (entries are only ever inserted with value 1 and removed at count 1, never updated to 0), but the code compiles and runs. If the invariant were ever violated — for example by a future code change to `ReleaseSlot` that does `TryUpdate(conn, 0, 1)` before `Remove` — the `TryAdd` call would fail indefinitely (key already present) and the CAS loop would spin forever, as `TryUpdate` is never tried for the `current == 0` case.

The correct CAS operation when `hasEntry=true && current == 0` is `TryUpdate(connection, 1, 0)`, not `TryAdd`. The current union of the two cases into one `TryAdd` path is logically inconsistent and creates a latent livelock hazard.

**Fix:**

```csharp
while (true)
{
    var hasEntry = _counts.TryGetValue(connection, out var current);
    if (hasEntry && current >= max) return false;
    var next = current + 1;
    if (!hasEntry)
    {
        if (_counts.TryAdd(connection, next)) return true;
        // Another thread inserted between our read and TryAdd — re-read.
    }
    else
    {
        if (_counts.TryUpdate(connection, next, current)) return true;
        // Lost CAS — re-read.
    }
}
```

This eliminates the unreachable-but-dangerous `current == 0` branch and makes the intent unambiguous.

### WR-02: `ForceAutomaticRecoveryDisabled` permanently mutates a shared keyed-singleton `ConnectionFactory`

**File:** `src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionFactoryResolver.cs:83-87`

**Issue:** When the 3-mode probe resolves a keyed-singleton `IConnectionFactory` (Mode 1) that is a concrete `ConnectionFactory`, `ForceAutomaticRecoveryDisabled` sets `cf.AutomaticRecoveryEnabled = false` on the shared singleton. This mutation is:

- **Permanent and global**: any other code that holds a reference to the same registered `IConnectionFactory` (e.g. a side-connection factory in the same service or a test helper) will see `AutomaticRecoveryEnabled = false` with no indication that the adapter changed it.
- **Races between pools**: if two pools register the same singleton factory key (different pool names, same factory instance registered under multiple keys), the first pool to acquire triggers the mutation. The second pool inherits it silently.
- **Only the first acquire fires EventId 2001**: on subsequent acquires `cf.AutomaticRecoveryEnabled` is already `false`, so the warning log is suppressed, giving the appearance that the factory was always configured correctly.

Mode 2 (closure) does not share this problem because each acquire creates a fresh `ConnectionFactory`.

**Fix:** Instead of mutating the shared factory, clone the necessary fields into a new `ConnectionFactory` for each connection creation:

```csharp
// In ConnectionFactoryResolver.Resolve for Mode 1:
if (keyed is ConnectionFactory sharedCf)
{
    // Return a per-acquire clone with AutomaticRecovery forced off.
    // Note: only copy properties that affect connection creation.
    return CloneWithRecoveryDisabled(sharedCf);
}
return keyed;
```

If cloning is not feasible without breaking the factory's full configuration surface, at minimum document the mutation explicitly in `AddAdaptiveConnectionPool`'s remarks and emit a separate EventId warning each time the override fires (not just on first acquire).

### WR-03: `DeadConnectionMarksChannelsUnhealthy_LazyInvalidation` tests channel closure, not connection-side lazy invalidation

**File:** `tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/ChannelPoolIntegrationTests.cs:92-151`

**Issue:** The test is named and documented as empirical validation of RESEARCH Q1 (lazy invalidation when a *connection* dies). However, the test never closes a connection — it calls `lease2.Value.CloseAsync()` on the *channel*, then returns the lease, and re-acquires. The re-acquire exercises only the `ch.IsOpen == false` branch of `BeforeUse`, not the `connLease.Value.IsOpen == false` branch.

The pairing-based connection check (`if (pairing.TryGet(ch, out var connLease) && connLease is not null && !connLease.Value.IsOpen)` at line 91 of `AdaptiveChannelPoolServiceCollectionExtensions.cs`) is thus not covered by any integration test. If that branch were deleted or broken, all integration tests would still pass.

**Fix:** Add a test that closes the *connection* backing an idle channel and then re-acquires from the channel pool. The simplest approach with `RabbitMqContainerFixture`:

1. Acquire a channel lease and return it to the idle queue.
2. Acquire the connection lease directly from the connection pool using `GetRequiredKeyedService<IAdaptivePool<IConnection>>`.
3. Call `conn.CloseAsync()` on the connection that backs the idle channel.
4. Re-acquire from the channel pool. The BeforeUse hook must observe `connLease.Value.IsOpen == false` and mark Unhealthy, producing a fresh channel on a new connection.

The current test should be renamed or its docstring corrected to reflect that it validates only the `ch.IsOpen` path.

### WR-04: `ChannelSpreadIntegrationTests` assertion is trivially satisfied and does not prove spread

**File:** `tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/ChannelSpreadIntegrationTests.cs:67-71`

**Issue:** The test acquires 50 channel leases. Each lease holds its own `IPoolItem<IConnection>`, so `connPool.InUse` is 50. The assertion is:

```csharp
var totalConnections = connPool.InUse + connPool.Available;
totalConnections.Should().BeGreaterThanOrEqualTo(5, ...);
```

`connPool.InUse = 50, connPool.Available = 0` → `total = 50 ≥ 5`. This assertion passes regardless of whether spread occurred — it would be equally true if all 50 channels were on the same connection (which is prevented by the broker, but not by the assertion itself). The test proves spread only by *absence of broker exception*, not by direct observation.

**Fix:** Count distinct `IConnection` objects by extracting them from the acquired leases. Since the `ConnectionChannelTracker` is internal and not injectable for testing, the pragmatic fix is to assert that `connPool` created at least 5 *distinct* connection objects. This can be done by tracking which connections were actually used via a spy `IConnectionFactory` or by inspecting tracker counts via a test-internal accessor. At minimum, document that the assertion is intentionally indirect and that broker enforcement is the proof mechanism — the current comment implies the assertion directly validates spread.

---

## Info

### IN-01: `pairing.TryGet` failure in Release hook is silent — tracker/lease leak goes unlogged

**File:** `src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveChannelPoolServiceCollectionExtensions.cs:109`

**Issue:** If `pairing.TryGet(ch, ...)` returns `false` in the Release hook (meaning the channel has no paired connection lease in the CWT), the code takes no action. The tracker slot for that connection is never decremented, and the connection lease is never returned. There is no log entry to alert an operator that this invariant was violated. In normal operation this should never occur (the CWT is only cleared in this Release hook), but if it does — due to a future code path that removes entries without going through the standard Release — the leak is invisible.

**Fix:** Add an `ILogger` to the `AddAdaptiveChannelPool` extension (or pass the logger factory) and emit a structured Error log:

```csharp
if (pairing.TryGet(ch, out var connLease) && connLease is not null)
{
    tracker.ReleaseSlot(connLease.Value);
    pairing.TryRemove(ch);
    await connLease.DisposeAsync().ConfigureAwait(false);
}
else
{
    // EventId 2002: unexpected missing pairing — tracker and lease may be leaked.
    logger.UnpairedChannelRelease(connectionPoolName);
}
```

### IN-02: `AddAdaptiveConnectionPool` called twice with the same name is silently inconsistent

**File:** `src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs:58-104`

**Issue:** Core's `AddAdaptivePool` uses `TryAddKeyedSingleton` for the pool instance (singleton wins first registration) but uses `AddOptions<AdaptivePoolBuilderConfigurator>.Configure` (additive, last-write-wins on the `Configure` property) for the builder configuration. Calling `AddAdaptiveConnectionPool` twice with the same `name` results in:

- The pool factory lambda being called only once (TryAdd semantics), using the first registration's `sp`.
- The `AdaptivePoolBuilderConfigurator` receiving both lambdas, with the second overwriting the first (last-write-wins on the `Configure` property assignment).
- The effective pool configuration is the second call's `configurePool` callback but the outer DI singleton infrastructure is the first call's.

No exception is thrown. The first `configureFactory` closure is silently discarded. This is a misconfiguration trap with no diagnostic.

**Fix:** Add a guard at the start of `AddAdaptiveConnectionPool` and `AddAdaptiveChannelPool`:

```csharp
if (services.Any(d => d.ServiceType == typeof(IAdaptivePool<IConnection>)
                      && d.ServiceKey is string k && k == name))
{
    throw new InvalidOperationException(
        $"An adaptive connection pool named '{name}' is already registered. " +
        "Call AddAdaptiveConnectionPool once per name.");
}
```

Alternatively, align with ASP.NET Core conventions and document that double-registration is a no-op (last-write-wins), with an explicit warning in the XML doc.

### IN-03: Container image tags in fixtures unpinned — non-reproducible builds

**File:** `tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/Fixtures/RabbitMqContainerFixture.cs:15`, `Fixtures/LowChannelMaxFixture.cs:21`

**Issue:** Both fixtures use `"rabbitmq:4-management"` without a specific patch tag. The `4-management` tag is a floating tag — it is reassigned on every upstream RabbitMQ 4.x release. A CI run on Tuesday and one on Thursday may use different broker binaries, potentially exposing different bugs or behaviour differences (e.g. a change to the default `channel_max` or the AMQP negotiation). For an OSS library whose key test matrix includes broker compatibility, this is a reproducibility risk.

**Fix:** Pin to a specific patch version tag, for example `"rabbitmq:4.0.9-management"`, and update as part of the regular dependency review cycle. If the intent is to always test against the latest 4.x, at least pin to a minor version tag (`"rabbitmq:4.0-management"`) rather than a major-floating tag.

---

_Reviewed: 2026-05-02_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
