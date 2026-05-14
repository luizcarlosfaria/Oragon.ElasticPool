---
phase: 03-rabbitmq-adapter
plan: 02
subsystem: rabbitmq-adapter
tags: [rabbitmq, adapter, channel-pool, layered-pool, eager-spread, conditional-weak-table, di]
requires:
  - "Plan 01: AddElasticConnectionPool registration → IElasticPool<IConnection>"
  - "Oragon.ElasticPool ServiceCollectionExtensions.AddElasticPool<IChannel>"
  - "RabbitMQ.Client 7.2.1 IConnection.CreateChannelAsync + CreateChannelOptions ctor"
provides:
  - "AddElasticChannelPool(name, connectionPoolName, configurePool) DI extension"
  - "ElasticChannelPoolBuilder (WithBounds/WithIdleTimeout/WithMaxChannelsPerConnection/WithChannelOptions)"
  - "ChannelLeasePairing (internal CWT wrapper IChannel↔IPoolItem<IConnection>)"
  - "ConnectionChannelTracker (internal ConcurrentDictionary<IConnection,int> with atomic CAS)"
affects:
  - "src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt (+13 entries)"
tech-stack:
  added: []
  patterns:
    - "Layered pool composition: channel pool's Factory acquires lease from inner connection pool"
    - "ConditionalWeakTable<IChannel, IPoolItem<IConnection>> for GC-safe pairing (no leak when consumer drops a channel)"
    - "Atomic CAS via ConcurrentDictionary.TryAdd/TryUpdate + ICollection<KVP>.Remove for compare-remove (T-03-08)"
    - "Eager spread via TryAcquireSlot gate per acquire (RESEARCH Q2)"
    - "Lazy cross-pool invalidation via BeforeUse re-probe of channel.IsOpen + paired connection.IsOpen (RESEARCH Q1)"
    - "Ownership-transfer pattern (selected=null after pairing.Add) to prevent finally-block double-dispose / CWT duplicate-key (T-03-10)"
    - "Decrement-before-return release order so a concurrent Factory call never sees a stale-saturated connection"
key-files:
  created:
    - "src/Oragon.ElasticPool.RabbitMQ/Internals/ChannelLeasePairing.cs (49 lines)"
    - "src/Oragon.ElasticPool.RabbitMQ/Internals/ConnectionChannelTracker.cs (86 lines)"
    - "src/Oragon.ElasticPool.RabbitMQ/Builder/ElasticChannelPoolBuilder.cs (109 lines)"
    - "src/Oragon.ElasticPool.RabbitMQ/DependencyInjection/ElasticChannelPoolServiceCollectionExtensions.cs (201 lines)"
  modified:
    - "src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt (+13 entries: 1 type ElasticChannelPoolBuilder + 11 members + 1 DI extension type + 1 method)"
decisions:
  - "ElasticChannelPoolOptions class NOT created — channel knobs flow through CreateChannelOptions directly (RabbitMQ.Client's own type) via WithChannelOptions; no IOptions binding surface needed for v1 since channel pool is composed (referenced by name) and inherits the connection pool's IOptions wiring upstream"
  - "MaxChannelsPerConnection default = 100 (well below broker channel_max=2047 per Pitfall 11); upper bound validated at 2047"
  - "Default CreateChannelOptions has publisher-confirmations + tracking ENABLED (Pitfall E mitigation) — consumers explicitly opt out via WithChannelOptions(custom)"
  - "MaxAttempts=16 safety net for eager-spread retry — bounds T-03-07 DoS scenario where every connection is saturated; consumer is expected to scale via the connection pool's MaxSize before hitting this bound (which throws InvalidOperationException with operator guidance)"
  - "Release order: CloseAsync (swallow) → DisposeAsync(channel) → tracker.ReleaseSlot → pairing.TryRemove → lease.DisposeAsync. The decrement BEFORE returning the lease prevents a race where another Factory acquirer sees the saturated count and unnecessarily skips an about-to-be-freed connection"
  - "No eager event/callback API for cross-pool invalidation in v1 — Core would need a cross-pool listener surface; per CONTEXT.md and RESEARCH Q1, deferred to v2 unless Plan 03 integration tests surface a defect. Lazy IsOpen probe in BeforeUse covers the common case at near-zero cost"
metrics:
  duration: "~12 minutes"
  completed: "2026-05-02"
  tasks: 2
  commits: 2
---

# Phase 3 Plan 02: Adaptive Channel Pool Summary

Layered the channel pool over Plan 01's connection pool. Shipped `services.AddElasticChannelPool(name, connectionPoolName, configurePool)` plus the `ElasticChannelPoolBuilder` fluent builder, the `ChannelLeasePairing` weak-table for `IChannel ↔ IPoolItem<IConnection>` pairing, and the `ConnectionChannelTracker` eager-spread counter that enforces `MaxChannelsPerConnection` (default 100) per RESEARCH Q2.

## Tasks Executed

| Task | Name                                                                              | Commit    |
| ---- | --------------------------------------------------------------------------------- | --------- |
| 1    | ChannelLeasePairing + ConnectionChannelTracker + ElasticChannelPoolBuilder       | `297f3ba` |
| 2    | AddElasticChannelPool extension with eager-spread Factory + lazy invalidation    | `d63ffb0` |

## Verification Results

### Build (Release)

```
dotnet build Oragon.ElasticPool.sln -c Release
ok dotnet build: 6 projects, 0 errors, 12 warnings (00:00:06.64)
```

The 12 warnings are the pre-existing carry-forward SourceLink "no remote" advisories (this WSL workspace has no git remote configured) — accepted at Phase 1, not a regression.

### Phase 1+2 Regression Check

```
dotnet test --project tests/Oragon.ElasticPool.Tests/Oragon.ElasticPool.Tests.csproj -c Release --no-build
total: 432
failed: 0
succeeded: 432
skipped: 0
```

432/432 unit tests passing across net8/net9/net10 — zero regression.

### Code-Grep Verifications (per plan automated checks)

```
grep -c "AddElasticPool<IChannel>"     ...ElasticChannelPoolServiceCollectionExtensions.cs   -> 1
grep -c "TryAcquireSlot"                ...ElasticChannelPoolServiceCollectionExtensions.cs   -> 1
grep -c "ReleaseSlot"                   ...ElasticChannelPoolServiceCollectionExtensions.cs   -> 3
grep -c "AddElasticChannelPool"        ...PublicAPI.Unshipped.txt                              -> 1
grep -c "ConditionalWeakTable<IChannel" ...Internals/ChannelLeasePairing.cs                     -> 1
grep -c "ConcurrentDictionary<IConnection" ...Internals/ConnectionChannelTracker.cs             -> 1
```

All checks confirmed: `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>` is the actual data structure (CONTEXT.md decision honored), the eager-spread retry loop is gated by `tracker.TryAcquireSlot(...)`, and the Release hook calls `ReleaseSlot` (the 3 occurrences are: Release-hook bookkeeping, the `catch` in the Factory when `CreateChannelAsync` throws, plus the helper-method usage path — all correct).

## Eager-Spread Algorithm Summary

Implemented in `CreateChannelWithSpreadAsync` (private static helper):

1. Resolve `IElasticPool<IConnection>` from DI by `connectionPoolName` (keyed singleton).
2. Loop up to `MaxAttempts=16`: acquire a connection lease, call `tracker.TryAcquireSlot(lease.Value, MaxChannelsPerConnection)`.
3. **Slot acquired** → store as `selected`, exit loop.
4. **Saturated** → add lease to `rejected` list and continue (the lease is held briefly so the inner pool does not immediately re-issue the same connection on the next `AcquireAsync`).
5. After the loop: if `selected` is null, throw `InvalidOperationException` with operator-guidance text (T-03-07 mitigation).
6. Call `selected.Value.CreateChannelAsync(chBuilder.ChannelOptions, ct)`. On exception, decrement the slot and rethrow (the finally block returns the lease).
7. `pairing.Add(channel, selected)`; set `selected = null` to transfer ownership.
8. `finally` — dispose all `rejected` leases (return them to the connection pool) plus `selected` if it is still non-null (creation failed before ownership transfer).

The `TryAcquireSlot` CAS retry loop on a single connection is unbounded but converges fast (small dictionary, per-connection contention only); the OUTER loop has the 16-attempt bound to cap the case where every connection is saturated.

## Lazy Invalidation Summary

Implemented inline in the channel pool's `BeforeUse` hook:

```csharp
.BeforeUse((ch, _) =>
{
    if (!ch.IsOpen)
        return ValueTask.FromResult(PoolState.Unhealthy);
    if (pairing.TryGet(ch, out var connLease) && connLease is not null && !connLease.Value.IsOpen)
        return ValueTask.FromResult(PoolState.Unhealthy);
    return ValueTask.FromResult(PoolState.Healthy);
})
```

Per RESEARCH Q1: ship lazy, validate empirically. Cost is one CWT lookup + two `IsOpen` boolean reads per acquire — negligible. If Plan 03 integration tests surface a measurable invalidation-latency defect (e.g., consumers see `AlreadyClosedException` from a channel returned as Healthy because the broker dropped the connection mid-flight between BeforeUse and use), the eager event/callback API will be reopened against Core in Phase 4.

The `Check` hook (background sweeper) only inspects `ch.IsOpen` — the connection-side check is intentionally limited to the borrow path, since the connection pool itself sweeps its own connections via its own `Check` hook.

## Release-Order Verification

```csharp
.Release(async (ch, ct) =>
{
    try { await ch.CloseAsync(ct); } catch { /* swallow */ }      // 1. close
    await ch.DisposeAsync();                                       // 2. dispose channel
    if (pairing.TryGet(ch, out var connLease) && connLease is not null)
    {
        tracker.ReleaseSlot(connLease.Value);                      // 3. decrement BEFORE return
        pairing.TryRemove(ch);                                     // 4. remove pairing entry
        await connLease.DisposeAsync();                            // 5. return lease to pool
    }
})
```

Steps 3→5 ordering matters: decrementing BEFORE returning the lease ensures a concurrent Factory call cannot observe a stale-saturated count for this connection. Verified to match plan's "Release order" success criterion.

## Public Surface Established

**Public types (1 new):**
- `Oragon.ElasticPool.RabbitMQ.Builder.ElasticChannelPoolBuilder` — channel-pool fluent builder (8 public members: 6 properties + `WithBounds`/`WithIdleTimeout`/`WithMaxChannelsPerConnection`/`WithChannelOptions`).
- `Oragon.ElasticPool.RabbitMQ.DependencyInjection.ElasticChannelPoolServiceCollectionExtensions` — hosts the `AddElasticChannelPool` extension.

**Internal types (2 new):**
- `Oragon.ElasticPool.RabbitMQ.Internals.ChannelLeasePairing` — CWT wrapper.
- `Oragon.ElasticPool.RabbitMQ.Internals.ConnectionChannelTracker` — atomic counter for eager spread.

PublicAPI.Unshipped.txt now lists both `AddElasticConnectionPool` (Plan 01) and `AddElasticChannelPool` (this plan).

## Threat Model Mitigations Applied

| Threat ID | Status     | Where                                                                                   |
| --------- | ---------- | --------------------------------------------------------------------------------------- |
| T-03-07   | mitigated  | `MaxAttempts=16` + `InvalidOperationException` with operator guidance in Factory helper |
| T-03-08   | mitigated  | Atomic CAS retry loops in `TryAcquireSlot`/`ReleaseSlot`; lower-bound guard at 0        |
| T-03-09   | accepted   | Documented (per Pitfall 10) — adapter only HANDS OUT channels; consumer responsibility  |
| T-03-10   | mitigated  | Single-add-site Factory + `selected = null` ownership-transfer after `pairing.Add`      |
| T-03-11   | accepted   | Core's `pool.health.failures` counter + EventId 1010 cover the Unhealthy-on-dead-conn   |
| T-03-12   | accepted   | Carried forward from Plan 01 (only fires on concrete `ConnectionFactory`)               |

## Deviations from Plan

**None.** Plan 02 executed exactly as written. Auto mode was active; no checkpoints required. The plan-frontmatter `files_modified` list intentionally did not include an `ElasticChannelPoolOptions.cs` file — the user's wrapper prompt mentioned one, but the plan body only specifies builder + two internals + DI extension, and `WithChannelOptions(CreateChannelOptions)` covers the channel knobs without a separate IOptions-bindable POCO. No Options class was created; this matches the plan body and frontmatter.

## Core API Gap Surfaced

**None.** Core's `AddElasticPool<IChannel>(name, configure)` and the `ElasticPoolBuilder<IChannel>` fluent surface (`Factory`/`BeforeUse`/`Check`/`Release`/`WithBounds`/`IdleTimeout`) covered everything Plan 02 needed to compose the layered channel pool. The `IPoolItem<IConnection>.Value` accessor (carry-forward from Plan 01) worked as expected for the lazy-invalidation BeforeUse re-probe and the Release-hook lease return. **Per phase success criterion #5, no Core refactor is required before Plan 03.**

## Heads-up to Plan 03

Plan 03 will add the unit test project + integration test project for the RabbitMQ adapter. Two scenarios specifically need coverage to validate Plan 02's implementation choices:

1. **Eager-spread under broker channel_max constraint.** Run with broker `channel_max=10` (configured via Testcontainers RabbitMQ broker config, not just the builder ceiling), set `MaxChannelsPerConnection=10`, acquire 25+ channels, and assert that ≥3 distinct `IConnection` instances are observed via the connection pool's `InUse`/`Available` snapshots — confirms the Factory genuinely spreads load instead of just hitting the broker error.

2. **Lazy invalidation latency under live channel death.** With ~50 in-use channels paired to a single connection, force-close the connection from the broker side (HTTP API delete) and measure the time-to-Unhealthy for a subsequent borrow. This is the empirical validation of the Q1 "ship lazy, defer eager" call. If P95 latency exceeds the SLO chosen by Plan 03 author (suggested: 100 ms from broker close to first Unhealthy borrow), file a follow-up against Core to add the eager cross-pool callback API.

Plan 03 author should also exercise the **`CreateChannelAsync` throws → tracker.ReleaseSlot path** with NSubstitute (Test 6 in Plan 02 frontmatter), since this is a tricky exception-path branch that the build alone cannot validate.

## Self-Check: PASSED

Files exist on disk:

```
[ -f src/Oragon.ElasticPool.RabbitMQ/Internals/ChannelLeasePairing.cs                                ] -> FOUND
[ -f src/Oragon.ElasticPool.RabbitMQ/Internals/ConnectionChannelTracker.cs                           ] -> FOUND
[ -f src/Oragon.ElasticPool.RabbitMQ/Builder/ElasticChannelPoolBuilder.cs                           ] -> FOUND
[ -f src/Oragon.ElasticPool.RabbitMQ/DependencyInjection/ElasticChannelPoolServiceCollectionExtensions.cs ] -> FOUND
[ -f src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt                                          ] -> FOUND (modified)
```

Both task commits present in git log: `297f3ba` (Task 1), `d63ffb0` (Task 2).
