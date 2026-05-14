# Phase 5 Plan 01: Bounded Waiter Backpressure

## Goal

Add a Core-level backpressure guard so `AcquireAsync` can reject excess parked waiters instead of allowing an unbounded `Channel<TaskCompletionSource<PoolEntry<T>>>` to grow under burst pressure.

## Scope

- Add a nullable `MaxWaiterCount` option to `ElasticPoolOptions<T>`.
- Add `ElasticPoolBuilder<T>.MaxWaiterCount(int n)`.
- Preserve current behavior by default: `null` means unbounded waiters.
- Enforce the limit with an atomic reserve path before parking an `AcquireAsync` caller.
- Throw `PoolExhaustedException` when the waiter cap is reached.
- Cover `MaxWaiterCount(0)` and `MaxWaiterCount(1)` with focused tests.

## Non-Goals

- No batch grow / waiter-debt ramp-up in this plan.
- No RabbitMQ builder exposure yet.
- No default behavior change for existing consumers.

## Verification

- `dotnet test --project tests/Oragon.ElasticPool.Tests/Oragon.ElasticPool.Tests.csproj -f net10.0 --no-restore -p:SuppressNETCoreSdkPreviewMessage=true`
- Broader solution test if the local SDK/test runner is healthy.
