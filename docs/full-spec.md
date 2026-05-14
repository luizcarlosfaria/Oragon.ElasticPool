# Oragon.ElasticPool - full specification

Last reviewed against repository source on 2026-05-11.

This document describes the implemented feature set and runtime behavior of the
current repository. It is intentionally broader than the package READMEs: it
covers public contracts, important internal behavior, RabbitMQ adapter behavior,
telemetry, validation rules, and known operational constraints.

## 1. Product scope

`Oragon.ElasticPool` is a .NET object-pooling library for expensive,
stateful resources such as network connections, clients, channels, handlers, or
other objects where creation cost and health matter.

The product value is the combination of three behaviors:

1. Elasticity: the pool grows under pressure and shrinks when pressure has been
   low for long enough.
2. Auto-healing: unhealthy instances are detected through lifecycle hooks and
   discarded before consumers keep using broken objects.
3. Fluent developer experience: async-first APIs, named DI registration,
   builder configuration, and built-in observability.

The repository ships two packages:

| Package | Purpose |
| --- | --- |
| `Oragon.ElasticPool` | Generic elastic pool engine, hooks, policies, DI, metrics, tracing, logging. |
| `Oragon.ElasticPool.RabbitMQ` | RabbitMQ.Client v7 adapter with layered `IConnection` and `IChannel` pools. |

Both packages target `net10.0`, `net9.0`, and `net8.0`.

## 2. Core concepts

### Pool

`IElasticPool<T>` owns live instances of `T`.

It exposes:

| Member | Meaning |
| --- | --- |
| `MaxSize` | Configured hard ceiling. |
| `MinSize` | Configured steady-state floor. |
| `Total` | Live items owned by the pool: idle, in-use, and being-created. |
| `Available` | Idle items immediately available for sync or async acquire. |
| `InUse` | Items currently leased to consumers. |
| `Waiting` | `AcquireAsync` callers parked in the waiter queue. |
| `Acquire()` | Synchronous immediate acquire. Never waits. |
| `AcquireAsync(CancellationToken)` | Primary acquire API. Can grow, wait, or throw depending on configuration and pressure. |
| `ReadyAsync()` | Warm-up task for `InitialSize` creation. Can be awaited multiple times. |

The pool implements both `IDisposable` and `IAsyncDisposable`.

### Lease

`IPoolItem<T>` wraps a pooled value.

| Behavior | Contract |
| --- | --- |
| `Value` | Returns the underlying `T` while the lease is active. Throws `ObjectDisposedException` after lease disposal. |
| `Dispose()` | Synchronously returns the item to the pool. Idempotent. |
| `DisposeAsync()` | Asynchronously returns the item to the pool. Idempotent. Runs the async return path. |
| Finalizer | If a consumer leaks the lease, the finalizer logs EventId 1001 and returns the item defensively without running the `Release` hook. |

Important: `DisposeAsync()` is the only lease-return path that executes
`AfterUse`. Synchronous `Dispose()` returns the item directly to the pool without
running `AfterUse`. Use `await using` when return-time validation matters.

### Health state

Lifecycle hooks return `PoolState`:

| Value | Meaning |
| --- | --- |
| `Healthy` | Item can be served or retained. |
| `Unhealthy` | Item must be discarded by the engine. |

### Failure decisions

`IItemFailurePolicy<T>` is invoked for selected failure kinds and returns
`FailureDecision`.

Current public decision:

| Value | Meaning |
| --- | --- |
| `Discard` | Discard the failed item. This is the default behavior. |

The default policy is `DiscardAndReplaceFailurePolicy<T>`.

Current failure kinds:

| Kind | Source |
| --- | --- |
| `FactoryThrew` | `Factory` threw while creating a new item. |
| `BeforeUseUnhealthy` | `BeforeUse` returned `Unhealthy`, or the hook threw and was treated as unhealthy. |
| `AfterUseUnhealthy` | Public enum value exists, but the current return path discards directly instead of invoking the failure policy. |
| `CheckUnhealthy` | Background `Check` returned `Unhealthy` or threw. |

## 3. Core configuration

Core pools are configured through `ElasticPoolBuilder<T>`.

### Required configuration

`Factory(...)` is required. `Build()` throws `InvalidOperationException` if no
factory is configured.

### Size bounds

Use:

```csharp
pool.WithBounds(minSize: 0, maxSize: 16, initialSize: 2);
```

Validation:

| Rule | Failure |
| --- | --- |
| `MinSize >= 0` | `ArgumentOutOfRangeException` |
| `MaxSize >= 1` | `ArgumentOutOfRangeException` |
| `MinSize <= MaxSize` | `InvalidOperationException` |
| `MinSize <= InitialSize <= MaxSize` | `InvalidOperationException` |

### Default values

| Option | Default | Notes |
| --- | ---: | --- |
| `MinSize` | `0` | No warm floor unless configured. |
| `MaxSize` | `1` | Conservative default. |
| `InitialSize` | `0` | No eager creation by default. |
| `WhenExhausted` | `Wait` | Park async callers when no grow is possible. |
| `FailurePolicy` | `DiscardAndReplaceFailurePolicy<T>` | Only public decision is discard. |
| `TimeProvider` | `TimeProvider.System` | Can be replaced for deterministic tests. |
| `GrowOnWaiterCount` | `1` | Any slow-path acquire trips grow by default. |
| `GrowOnUtilizationPercent` | `0.80` | Rolling average utilization threshold. |
| `UtilizationWindow` | `30s` | Effective option. No fluent setter currently exists. |
| `GrowOnWaitTimeP95` | `100ms` | Slow-path wait p95 threshold. |
| `IdleTimeout` | `60s` | Sustained low-pressure duration before shrink. |
| `ShrinkOnUtilizationPercent` | `0.50` | Low-pressure threshold. |
| `ShrinkTargetUtilizationPercent` | `0.75` | Target used to compute post-shrink size. |
| `ShrinkBatchSize` | `1` | Maximum idle items evicted per shrink tick. |
| `ShrinkCooldownWindows` | `3` | Sweep ticks after grow before shrink is allowed. |
| `SweepInterval` | `30s` | Background sweeper period. |
| `MaxBackoff` | `5m` | Maximum sweeper interval during failure backoff. |
| `MaxWaiterCount` | `null` | Unbounded parked waiters by default. |

Validation for tuning methods:

| Method | Valid range |
| --- | --- |
| `GrowOnWaiterCount(n)` | `n >= 1` and later `n <= MaxSize` at `Build()`. |
| `GrowOnUtilizationPercent(p)` | `0 < p <= 1`. |
| `GrowOnWaitTimeP95(t)` | `t > TimeSpan.Zero`. |
| `IdleTimeout(t)` | `t > TimeSpan.Zero`. |
| `ShrinkOnUtilizationPercent(p)` | `0 <= p <= 1`. |
| `ShrinkTargetUtilizationPercent(p)` | `0 < p <= 1`. |
| `ShrinkBatchSize(n)` | `n >= 1`. |
| `ShrinkCooldownWindows(n)` | `n >= 0`. |
| `SweepInterval(t)` | `t > TimeSpan.Zero` and `t <= MaxBackoff` at `Build()`. |
| `MaxBackoff(t)` | `t > TimeSpan.Zero`. |
| `MaxWaiterCount(n)` | `n >= 0`. |

Tuning warning: with `MinSize=0`, `InitialSize=0`, and
`GrowOnWaiterCount > 1`, the first caller can park instead of growing if no
other grow signal is already tripped. Use the default waiter threshold, seed the
pool with `InitialSize`, set `MinSize`, or configure another grow signal.

## 4. Lifecycle hooks

Every lifecycle hook has async and sync overloads.

| Hook | Required | Invoked when | Expected use |
| --- | --- | --- | --- |
| `Factory` | Yes | Pool creates a new item during warm-up, grow, replacement, or cold acquire. | Create `T`. Can use DI through `IServiceProvider`. |
| `BeforeUse` | No | Before an item is handed to a consumer. | Cheap in-memory validation. Should be very fast. |
| `Check` | No | Background sweeper checks idle items. | Periodic health probe. |
| `AfterUse` | No | `IPoolItem<T>.DisposeAsync()` returns a lease. | Return-time validation. Not run by sync `Dispose()`. |
| `Release` | No | Item is evicted, discarded, or drained during pool disposal. | Close/dispose the underlying resource. |

Sync overloads are wrapped in completed `ValueTask` instances by the builder.
They are intended for in-memory work. Async overloads are intended for I/O.
Both can be mixed in the same pool.

Null hook delegates throw `ArgumentNullException`.

### Failure handling matrix

| Source | Engine behavior |
| --- | --- |
| `Factory` throws | Rolls back `Total`, increments `pool.factory.failures`, logs EventId 1002, invokes failure policy with `FactoryThrew` and the exception, then rethrows the original exception. |
| `BeforeUse` returns `Unhealthy` | Decrements `Total`, logs EventId 1003, invokes failure policy with `BeforeUseUnhealthy`, invokes `Release` if configured, then retries according to the acquire path. |
| `BeforeUse` throws | Treated as `Unhealthy`; the current policy call receives `FailureKind.BeforeUseUnhealthy` and no exception payload. |
| `Check` returns `Unhealthy` | Increments `pool.health.failures`, logs EventId 1010, invokes failure policy with `CheckUnhealthy`, marks the entry pending-discard, and prevents future acquire from serving it. |
| `Check` throws | Same as `Check.Unhealthy`, but the thrown exception is passed to the failure policy. |
| `AfterUse` returns `Unhealthy` | Discards directly, invokes `Release`, and can grow a replacement for parked waiters. The current path does not invoke `IItemFailurePolicy<T>`. |
| `AfterUse` throws | Swallowed; the item is returned defensively as if healthy. |
| `Release` throws | Swallowed on discard/shrink paths. During pool disposal, failures are logged with EventId 1004 and drain continues. |

## 5. Acquire behavior

### `Acquire()`

`Acquire()` is a synchronous fast path.

Behavior:

1. Throws `ObjectDisposedException` if the pool is closed.
2. Dequeues one idle item if available.
3. Runs `BeforeUse` if configured. This is sync-over-async, so the hook must be
   cheap. If the hook throws, it is treated as `Unhealthy`.
4. If the item is healthy, increments `InUse`, emits acquire telemetry, and
   returns an `IPoolItem<T>`.
5. If the item is unhealthy, the item is discarded, the failure policy is invoked
   with `BeforeUseUnhealthy`, `Release` is invoked if configured, and the method
   tries the next idle item.
6. If no healthy idle item remains, throws `PoolExhaustedException`.

`Acquire()` never waits and never grows a replacement. If you need waiting or
growth, use `AcquireAsync()`.

### `AcquireAsync()`

`AcquireAsync()` is the primary API.

Behavior:

1. Throws `ObjectDisposedException` if the pool is closed.
2. Uses an idle item immediately if one exists.
3. Runs `BeforeUse`; `Unhealthy` or thrown hook means discard, failure policy,
   `Release`, and retry.
4. If no idle item exists, evaluates grow pressure.
5. If grow is allowed, reserves one slot under `MaxSize`, calls `Factory`, and
   returns the new entry.
6. If no grow is possible and `WhenExhausted == Throw`, throws
   `PoolExhaustedException`.
7. If no grow is possible and `WhenExhausted == Wait`, parks the caller in a
   direct-handoff waiter queue until another lease returns, the caller cancels,
   the waiter cap rejects it, or the pool is disposed.

No grow possible does not only mean "already at MaxSize". With non-default grow
thresholds, the pool can also treat the slow path as exhausted when `Total` is
below `MaxSize` but the composite pressure gate says "do not grow yet".

The slow path records acquire wait duration in both the internal p95 histogram
and the OpenTelemetry histogram.

`BeforeUse` unhealthy retry is bounded. After 10 consecutive unhealthy
replacements, `AcquireAsync()` throws `InvalidOperationException` naming the
pool, which prevents infinite retry when the factory persistently creates broken
items.

### Exhaustion and backpressure

`PoolExhaustedException` is thrown when:

| Scenario | Source |
| --- | --- |
| Sync `Acquire()` finds no healthy idle item. | Always immediate. |
| Async `AcquireAsync()` cannot grow and `WhenExhausted.Throw` is configured. | Immediate. |
| Async `AcquireAsync()` would park but `MaxWaiterCount` has been reached. | Immediate. |

`PoolExhaustedException.IsTransient` is always `true`. The exception exposes
`MaxSize` and optional `WaitTime`.

`MaxWaiterCount(0)` means reject instead of parking when async acquire reaches
the exhausted wait branch.

### Direct handoff

When an item is returned and waiters are parked, the pool tries to hand that
item directly to exactly one waiter instead of re-enqueuing it to idle first.

Canceled waiters are skipped.

## 6. Warm-up

`InitialSize` items are created as soon as the pool instance is built/resolved.
Warm-up happens asynchronously and in parallel.

Use:

```csharp
await pool.ReadyAsync();
```

Behavior:

| Case | Behavior |
| --- | --- |
| `InitialSize == 0` | Completes without factory calls. |
| Factory succeeds | Idle queue contains `InitialSize` items. |
| Factory throws | `ReadyAsync()` observes the exception; `Total` is rolled back for failed creations. |
| Pool disposed during warm-up | The warm-up task is canceled/faulted. |

`ReadyAsync()` returns the same warm-up task and can be awaited multiple times.

## 7. Grow behavior

Growth is driven by `PressureSampler`.

The grow decision is the OR of three signals:

| Signal | Config |
| --- | --- |
| Waiters | `currentWaiters >= GrowOnWaiterCount`. The current slow-path caller is counted as if parked. |
| Utilization | Rolling average utilization is `>= GrowOnUtilizationPercent`. |
| Wait p95 | Internal acquire-wait p95 is `>= GrowOnWaitTimeP95`. |

Growth is capped by `MaxSize`.

If `Total < MinSize`, `AcquireAsync()` attempts to grow even when pressure does
not trip. This is how future acquires refill below the configured floor.

Growth creates one item per successful slow-path reservation. Factory work runs
outside pool locks. If `Factory` throws, `Total` is decremented before the
failure policy is invoked, and the original exception is rethrown.

Successful grow:

| Side effect | Value |
| --- | --- |
| Counter | `pool.grow.count` increments. |
| Log | EventId 1005. |
| Span | `Pool.Grow` with trip flags and `pool.size_after`. |
| Cooldown | Shrink cooldown counter resets. |

## 8. Shrink and background sweep

The background sweeper starts when the pool is constructed. It ticks every
`SweepInterval`, adjusted by failure backoff.

Each tick runs:

1. Optional `Check` pass over a snapshot of idle entries.
2. Eager unhealthy eviction for entries marked by `Check`.
3. Aggregate-pressure shrink pass.
4. Shrink cooldown bookkeeping.
5. Backoff update.
6. Sweep duration telemetry.

### Check pass

If `Check` is configured, the sweeper invokes it once per idle item in the
snapshot.

If `Check` returns `Unhealthy` or throws:

1. `pool.health.failures` increments.
2. EventId 1010 is logged.
3. Failure policy is invoked with `FailureKind.CheckUnhealthy`.
4. The entry is marked pending-discard.
5. Future acquire paths skip that entry and never serve it to consumers.
6. The eviction pass removes pending-discard entries from the idle queue when
   they are at the head; otherwise they are removed lazily by acquire or later
   sweeps.

Unhealthy eviction bypasses shrink cooldown and can temporarily reduce the pool
below `MinSize`. Future `AcquireAsync()` calls refill because `Total < MinSize`
triggers grow.

### Shrink pass

Shrink uses aggregate low pressure, not per-item age.

A pool is under low pressure when all are true:

| Condition | Meaning |
| --- | --- |
| `Waiting == 0` | No parked acquires. |
| `Total > MinSize` | There is excess capacity. |
| `Available > 0` | There is at least one idle entry to evict. |
| `InUse / Total <= ShrinkOnUtilizationPercent` | Utilization is low enough. |

Low pressure must persist for `IdleTimeout`. After that, shrink also requires
`SinceLastGrowTicks >= ShrinkCooldownWindows`.

When shrink is allowed:

1. Target total is computed:
   - if `InUse == 0`, target is `MinSize`;
   - otherwise target is `max(MinSize, ceil(InUse / ShrinkTargetUtilizationPercent))`.
2. The sweeper removes up to `ShrinkBatchSize` idle items, bounded by available
   items and the target total.
3. `Release` is invoked for each evicted item if configured.
4. `pool.shrink.count` increments per evicted item.
5. EventId 1006 is logged.
6. A `Pool.Shrink` span is emitted with size before/after.

### Sweep backoff

The sweeper backs off when health checks show a broad failure window.

Rules:

| Case | Behavior |
| --- | --- |
| `totalChecked == 0` | No change. |
| `unhealthy >= 50%` | Increment consecutive failure window count. |
| 3 or more consecutive failure windows | Double current interval, capped at `MaxBackoff`. |
| Clean window | Reset consecutive count and interval to base `SweepInterval`. |

Backoff interval increases are logged with EventId 1009.

## 9. Return and disposal behavior

### Healthy return

If the pool is open and no waiter is parked, a returned healthy item is enqueued
to idle.

If a waiter is parked, the item is handed directly to the waiter.

Normal healthy return does not call `Release`. `Release` is an eviction/drain
hook, not a "return to pool" hook.

### `AfterUse`

`AfterUse` runs only on `DisposeAsync()` of a lease.

| Result | Behavior |
| --- | --- |
| `Healthy` | Item returns to idle or is handed to a waiter. |
| `Unhealthy` | Item is discarded, `Release` runs, `InUse` and `Total` decrement. |
| Throws | Exception is swallowed and the item is returned defensively. |

If an `AfterUse.Unhealthy` discard frees a slot while waiters are parked, the
pool starts a background replacement grow and hands the replacement to a waiter
when possible.

### Pool disposal

`DisposeAsync()` behavior:

1. Transitions lifecycle to closed. Repeated calls are no-ops.
2. Cancels the linked lifetime token.
3. Stops the background sweeper before draining idle entries.
4. Completes the waiter channel and cancels parked waiters.
5. Drains idle entries and invokes `Release` on each.
6. Disposes telemetry and the lifetime CTS.

After disposal, new `Acquire()` and `AcquireAsync()` calls throw
`ObjectDisposedException`.

Checked-out leases are not waited on during `DisposeAsync()`. If a consumer later
disposes a stale lease after the pool is closed, the pool invokes `Release`
best-effort in a background task.

Synchronous `Dispose()` blocks on `DisposeAsync()`.

## 10. Dependency injection

Core registration:

```csharp
services.AddElasticPool<MyClient>("primary", pool =>
{
    pool.Factory((sp, ct) => new MyClient());
    pool.WithBounds(0, 16, 0);
});
```

Behavior:

| Feature | Behavior |
| --- | --- |
| Named pools | Registered as keyed singleton `IElasticPool<T>` under the provided name. |
| Default name | `string.Empty` is also registered as non-keyed `IElasticPool<T>`. |
| Multiple pools of same `T` | Supported when names differ. |
| Host shutdown | If `IHostApplicationLifetime` is registered, the pool links to `ApplicationStopping` without taking a hard package dependency on Hosting abstractions. |

Core duplicate-name registration is not explicitly rejected by the Core
extension. Avoid registering the same `T` and name more than once.

RabbitMQ adapter registrations do reject duplicate pool names for connection and
channel pools.

## 11. Observability

The library exposes a `Meter` and `ActivitySource` named:

```text
Oragon.ElasticPool
```

If `IMeterFactory` is registered, the pool creates its meter through the
factory. Otherwise it falls back to a local `Meter`.

### Metrics

All instruments carry the `pool.name` tag.

| Instrument | Type | Unit | Meaning |
| --- | --- | --- | --- |
| `pool.size` | Observable gauge | `{items}` | Total live items owned by the pool. |
| `pool.available` | Observable gauge | `{items}` | Idle items available immediately. |
| `pool.in_use` | Observable gauge | `{items}` | Leased items. |
| `pool.waiting` | Observable gauge | `{waiters}` | Parked `AcquireAsync` callers. |
| `pool.acquire.count` | Counter | `{acquires}` | Successful acquire calls. |
| `pool.factory.failures` | Counter | `{failures}` | Factory exceptions. |
| `pool.grow.count` | Counter | `{grows}` | Successful item creation through grow/replacement paths. |
| `pool.shrink.count` | Counter | `{shrinks}` | Idle items evicted by shrink. |
| `pool.health.failures` | Counter | `{failures}` | `Check` unhealthy verdicts or exceptions during sweep. |
| `pool.acquire.wait.duration` | Histogram | `s` | Time spent parked on the async slow path. |
| `pool.sweep.duration` | Histogram | `s` | Duration of one sweep tick. |

### Activity spans

| Span | Tags/events |
| --- | --- |
| `Pool.Acquire` | `pool.name`, `outcome=ok/canceled/error`. |
| `Pool.Release` | `pool.name`, `outcome=ok/canceled`. |
| `Pool.Grow` | `pool.name`, `pool.size_after`, grow trip flags, `outcome`. |
| `Pool.Shrink` | `pool.name`, `pool.size_before`, `pool.size_after`, `outcome=shrunk`. |
| `Pool.Sweep` | `pool.name`, `outcome=healthy/unhealthy`, one `item-checked` event per checked item. |
| `Pool.HealthCheck` | `pool.name`, `outcome=healthy/unhealthy`. |

Sweep and per-item health-check spans are guarded with `ActivitySource.HasListeners()`
to avoid expensive work when tracing is disabled.

### Core log events

| EventId | Level | Meaning |
| ---: | --- | --- |
| 1001 | Warning | Lease leaked; finalizer returned item defensively. |
| 1002 | Error | Factory hook threw and counter was rolled back. |
| 1003 | Warning | `BeforeUse` reported unhealthy. |
| 1004 | Error | `Release` hook threw during pool disposal; drain continued. |
| 1005 | Information | Pool grew. |
| 1006 | Information | Pool shrank. |
| 1007 | Debug | Sweep tick started. |
| 1008 | Debug | Sweep tick completed. |
| 1009 | Warning | Sweep failure backoff interval increased. |
| 1010 | Warning | `Check` reported unhealthy or threw. |
| 1099 | Error | Catastrophic sweep tick failure; loop continues. |

## 12. RabbitMQ adapter

The RabbitMQ package adapts Core to RabbitMQ.Client v7 APIs:

| Pool | Type | Registration |
| --- | --- | --- |
| Connection pool | `IElasticPool<IConnection>` | `AddElasticConnectionPool(name, configureFactory, configurePool)` |
| Channel pool | `IElasticPool<IChannel>` | `AddElasticChannelPool(name, connectionPoolName, configurePool)` |

Both are named keyed singleton registrations. `string.Empty` follows Core's
default-name behavior.

### Connection pool

Default builder values:

| Option | Default |
| --- | ---: |
| `MinSize` | `0` |
| `MaxSize` | `8` |
| `InitialSize` | `0` |
| `IdleTimeout` | `60s` |
| `SweepInterval` | `30s` |
| `ShrinkOnUtilizationPercent` | `0.50` |
| `ShrinkTargetUtilizationPercent` | `0.75` |
| `ShrinkBatchSize` | `1` |
| `ShrinkCooldownWindows` | `3` |

The connection factory is resolved in this priority order:

1. Keyed singleton `IConnectionFactory` registered under the pool name.
2. `configureFactory` closure applied to a fresh `ConnectionFactory`.
3. Named `IOptionsMonitor<ElasticConnectionPoolOptions>` when `HostName` is set.

If all probes fail, acquire fails with `InvalidOperationException`.

`ElasticConnectionPoolOptions` supports:

| Property | Default/behavior |
| --- | --- |
| `HostName` | Required for IOptions mode. |
| `Port` | `5672`. |
| `UserName` | RabbitMQ default user when null. |
| `Password` | RabbitMQ default password when null. Never logged by adapter. |
| `VirtualHost` | RabbitMQ default vhost when null. |
| `RequestedHeartbeat` | Leaves `ConnectionFactory` default unchanged when null. |

Connection lifecycle hooks:

| Hook | Behavior |
| --- | --- |
| `Factory` | Resolves factory, applies automatic recovery override when possible, calls `CreateConnectionAsync`. |
| `BeforeUse` | Healthy when `IConnection.IsOpen` is true. |
| `Check` | Healthy when `IConnection.IsOpen` is true. |
| `Release` | Calls `CloseAsync`, swallows close failures, then calls `DisposeAsync`; dispose failures log EventId 2004. |

Automatic recovery behavior:

| Factory kind | Behavior |
| --- | --- |
| Concrete `ConnectionFactory` with `AutomaticRecoveryEnabled=false` | Used as-is. |
| Concrete `ConnectionFactory` with `AutomaticRecoveryEnabled=true` | Cloned per acquire, clone has recovery forced to false, EventId 2001 warning logged. Shared singleton is not mutated. |
| Custom `IConnectionFactory` implementation | Used as-is; adapter cannot inspect or override recovery. |

The override exists because the pool owns discard/replace lifecycle. RabbitMQ
client automatic recovery would create a second competing lifecycle loop.

### Channel pool

Default builder values:

| Option | Default |
| --- | ---: |
| `MinSize` | `0` |
| `MaxSize` | `32` |
| `InitialSize` | `0` |
| `IdleTimeout` | `60s` |
| `SweepInterval` | `30s` |
| `ShrinkOnUtilizationPercent` | `0.50` |
| `ShrinkTargetUtilizationPercent` | `0.75` |
| `ShrinkBatchSize` | `1` |
| `ShrinkCooldownWindows` | `3` |
| `MaxChannelsPerConnection` | `100` |

`MaxChannelsPerConnection` must be in `[1, 2047]`. The upper bound matches the
broker default `channel_max`.

Default `CreateChannelOptions`:

| Field | Default |
| --- | --- |
| `publisherConfirmationsEnabled` | `true` |
| `publisherConfirmationTrackingEnabled` | `true` |
| `outstandingPublisherConfirmationsRateLimiter` | `null` |
| `consumerDispatchConcurrency` | `1` |

Channel factory behavior:

1. Resolve the named connection pool.
2. Acquire or reuse a retained shared connection lease with live channel count
   below `MaxChannelsPerConnection`.
3. If all retained connections are saturated, acquire one more connection lease
   from the connection pool.
4. Call `IConnection.CreateChannelAsync(ChannelOptions, ct)`.
5. Pair the channel to its shared connection lease through `ConditionalWeakTable`.
6. If channel creation fails, return the selected connection lease.

Channel health:

| Hook | Behavior |
| --- | --- |
| `BeforeUse` | Unhealthy when the channel is closed or the paired backing connection is closed. |
| `Check` | Checks `IChannel.IsOpen`. It does not check the paired connection. |

This is lazy cross-pool invalidation: a backing connection closure is detected
when a channel is borrowed through `BeforeUse`, not through a public event hook.

Channel release behavior:

1. Call `IChannel.CloseAsync`; close failures are swallowed.
2. Call `IChannel.DisposeAsync`; failures log EventId 2003.
3. Remove channel-to-connection pairing.
4. Dispose the shared connection lease.
5. If the shared registry count reaches zero, the underlying connection lease is
   returned to the connection pool.

If no pairing exists during release, EventId 2002 is logged.

Operational note: idle channels retained in the channel pool keep their shared
connection leases checked out from the connection pool. Therefore
`connectionPool.InUse` can stay above zero even when `channelPool.InUse == 0`.
The lease returns only when the channel is discarded, swept, or drained.

### RabbitMQ log events

| EventId | Level | Meaning |
| ---: | --- | --- |
| 2001 | Warning | `AutomaticRecoveryEnabled=true` was overridden on a concrete `ConnectionFactory` clone. |
| 2002 | Debug | Channel release had no paired shared connection lease. |
| 2003 | Warning | `IChannel.DisposeAsync` threw during release; cleanup still continued. |
| 2004 | Warning | `IConnection.DisposeAsync` threw during release. |

## 13. Samples and validation assets

| Asset | Purpose |
| --- | --- |
| `samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher` | Full-scale idle -> burst -> idle RabbitMQ publisher sample. Defaults: 3 cycles, 100k messages per burst, parallelism 256. |
| `samples/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard` | Aspire + Blazor visual dashboard. Shows `Total`, `Available`, `InUse`, and `Waiting` at 10 Hz. |
| `tests/Oragon.ElasticPool.Stress` | Opt-in stress tests. Excluded from solution-wide default test discovery via `<IsTestProject>false</IsTestProject>`. |
| `tests/Oragon.ElasticPool.Benchmarks` | BenchmarkDotNet heavy-resource elasticity benchmark. |
| `BenchmarkReports/` | Stored benchmark reports for heavy-resource elasticity. |

## 14. Build, tests, packaging

Repository-wide build settings:

| Setting | Value |
| --- | --- |
| Nullable | Enabled. |
| Implicit usings | Enabled. |
| Language version | Latest. |
| Warnings | Treated as errors. |
| Deterministic build | Enabled. |
| Symbols | `.snupkg`. |
| SourceLink | GitHub SourceLink package. |
| Versioning | MinVer from git tags. |
| Public API | Enforced with `Microsoft.CodeAnalysis.PublicApiAnalyzers`. |

Test stack:

| Area | Tooling |
| --- | --- |
| Test framework | xUnit v3. |
| Runner | Microsoft.Testing.Platform through `global.json`. |
| Assertions | AwesomeAssertions. |
| Mocks | Moq. |
| RabbitMQ integration | Testcontainers.RabbitMq. |
| Coverage | coverlet.msbuild. |

CI:

1. Runs on `ubuntu-latest`.
2. Matrixes `net8.0`, `net9.0`, `net10.0`.
3. Restores, builds Release, runs solution-wide tests through MTP.
4. Runs Core-only coverage gate with 90% line threshold.
5. Uploads coverage HTML and TRX artifacts.

Release workflow:

1. Triggered by tags matching `v*`.
2. Runs full multi-TFM test gate.
3. Builds all TFMs.
4. Packs Core and RabbitMQ.
5. Verifies every `.nupkg` has a matching `.snupkg`.
6. Pushes packages to NuGet.org.
7. Does not use `--skip-duplicate`; duplicate release versions fail loudly.

## 15. Current limitations and sharp edges

These are current implemented behaviors, not future promises:

1. `AfterUse` runs only on async lease disposal. Synchronous lease disposal skips it.
2. `AfterUse.Unhealthy` discards directly and does not currently invoke
   `IItemFailurePolicy<T>`.
3. `DisposeAsync()` drains idle entries and cancels waiters, but it does not wait
   for checked-out leases to return.
4. Health-check eviction can temporarily reduce `Total` below `MinSize`; refill
   happens on future `AcquireAsync()` growth.
5. Core duplicate named registrations are not explicitly rejected. RabbitMQ
   adapter duplicate registrations are rejected.
6. `GrowOnWaiterCount > 1` with a cold, zero-min, zero-initial pool can park the
   first caller unless another grow signal is already tripped.
7. The Core builder exposes no fluent setter for `UtilizationWindow`; the default
   30-second window is used by normal builder flows.
8. RabbitMQ channel `Check` validates the channel itself, while paired-connection
   validation is performed by `BeforeUse`.
9. `IChannel` is not thread-safe. Consumers must acquire a lease per logical
   publisher or concurrent operation.

## 16. Non-goals

| Non-goal | Rationale |
| --- | --- |
| `netstandard2.0`, `net6.0`, or `net7.0` support | The library relies on modern BCL APIs available in net8+. |
| Replacing cheap allocation pools | `Microsoft.Extensions.ObjectPool` remains suitable for cheap stateless objects such as `StringBuilder`. |
| Owning application host lifetime | Core has no hard dependency on Hosting. It opportunistically links to host shutdown when available. |
| RabbitMQ.Client automatic recovery | The adapter disables concrete factory automatic recovery so the pool owns lifecycle. |
