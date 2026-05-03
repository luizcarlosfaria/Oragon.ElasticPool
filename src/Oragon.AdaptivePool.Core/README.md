# Oragon.AdaptivePool.Core

[![NuGet](https://img.shields.io/nuget/v/Oragon.AdaptivePool.Core.svg)](https://www.nuget.org/packages/Oragon.AdaptivePool.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/oragon/Oragon.AdaptivePool/blob/main/LICENSE)

> Generic, elastic, self-healing object pool for .NET 8 / 9 / 10 — with built-in OpenTelemetry.

`Oragon.AdaptivePool.Core` is a generic in-process object pool for expensive-to-create
resources (clients, connections, handlers). It **grows** under sustained pressure,
**shrinks** when idle, and **heals itself** by detecting and replacing broken items
through pluggable lifecycle hooks. Async-first, DI-first, observable.

For RabbitMQ `IConnection` + `IChannel` pooling, install the companion package
[`Oragon.AdaptivePool.RabbitMQ`](https://www.nuget.org/packages/Oragon.AdaptivePool.RabbitMQ).

## Install

```bash
dotnet add package Oragon.AdaptivePool.Core
```

## 30-second quickstart

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddAdaptivePool<MyExpensiveClient>("default", pool =>
{
    pool.MinSize     = 1;
    pool.InitialSize = 2;
    pool.MaxSize     = 16;
    pool.IdleTimeout = TimeSpan.FromMinutes(2);

    pool.Factory((sp, ct) => ValueTask.FromResult(new MyExpensiveClient()));
    pool.BeforeUse((c, ct) => ValueTask.FromResult(c.IsHealthy ? PoolState.Healthy : PoolState.Unhealthy));
    pool.Release  ((c, ct) => { c.Dispose(); return ValueTask.CompletedTask; });
});

using var host = builder.Build();
var pool = host.Services.GetRequiredService<IAdaptivePool<MyExpensiveClient>>();

await using var lease = await pool.AcquireAsync();
lease.Value.DoWork();
// On Dispose, the item returns to the pool — or is discarded if BeforeUse said Unhealthy.

public sealed class MyExpensiveClient : IDisposable
{
    public bool IsHealthy => true;
    public void DoWork() { /* ... */ }
    public void Dispose() { /* ... */ }
}
```

## Why not `Microsoft.Extensions.ObjectPool`?

| Feature                            | `Microsoft.Extensions.ObjectPool` | `Oragon.AdaptivePool.Core` |
|------------------------------------|-----------------------------------|----------------------------|
| `Min` / `Max` bounds               | ❌ (only `MaximumRetained`)        | ✅                          |
| Elastic grow under pressure        | ❌                                 | ✅ (composite signal: waiters + utilization + p95 wait) |
| Auto-shrink when idle              | ❌                                 | ✅ (hysteretic, IdleTimeout-driven) |
| Lifecycle hooks (5 stages)         | ❌                                 | ✅ (`Factory`, `BeforeUse`, `Check`, `AfterUse`, `Release`) |
| Health check on borrow             | ❌                                 | ✅ (`BeforeUse`)            |
| Background health sweep            | ❌                                 | ✅ (`Check` + `PeriodicTimer` + exponential backoff) |
| Pluggable failure policy           | ❌                                 | ✅ (`IItemFailurePolicy<T>`) |
| Async-first API                    | ❌ (`Get()` is sync, blocks)       | ✅ (`AcquireAsync` returns `ValueTask`) |
| Built-in OpenTelemetry             | Limited                           | ✅ (`Meter` + `ActivitySource` + source-gen `ILogger`) |
| Layered pools (e.g., channel→conn) | N/A                               | ✅ (see `Oragon.AdaptivePool.RabbitMQ`) |

`Microsoft.Extensions.ObjectPool` is great for cheap, stateless, allocation-only
pooling (e.g., `StringBuilder`). `Oragon.AdaptivePool.Core` is for expensive,
stateful, lifecycle-sensitive resources where elasticity and health matter.

## Three pillars

1. **Elasticity** — composite-signal grow (waiters + sustained utilization % + p95
   acquire-wait), hysteretic shrink to `MinSize` only after consecutive low-utilization
   sweep windows past a cooldown since the last grow. No thrashing.
2. **Auto-healing** — five lifecycle hooks at every stage: `Factory` (create),
   `BeforeUse` (validate on borrow), `Check` (background sweep), `AfterUse` (validate
   on return; opt-in), `Release` (cleanup/dispose). Pluggable `IItemFailurePolicy<T>`
   decides discard vs. quarantine vs. custom.
3. **Fluent DX** — async-first, DI-first, builder pattern. `services.AddAdaptivePool<T>(name, configure)`.

## OpenTelemetry in 5 lines

`Oragon.AdaptivePool` exposes a `Meter` and `ActivitySource` both named
`"Oragon.AdaptivePool"`. Wire them to any OTel exporter:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services
    .AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Oragon.AdaptivePool").AddConsoleExporter())
    .WithTracing(t => t.AddSource("Oragon.AdaptivePool").AddConsoleExporter());
// In production, swap AddConsoleExporter() for AddOtlpExporter() (Aspire / OTel collector / etc.).
```

The pool emits these metrics out of the box:

| Instrument                    | Type     | Purpose                              |
|-------------------------------|----------|--------------------------------------|
| `pool.size`                   | Gauge    | Total items the pool currently owns  |
| `pool.available`              | Gauge    | Items idle and ready to acquire      |
| `pool.in_use`                 | Gauge    | Items currently leased               |
| `pool.waiting`                | Gauge    | Waiters queued on `AcquireAsync`     |
| `pool.acquire.count`          | Counter  | Total acquires                       |
| `pool.acquire.duration`       | Histogram| Time spent in `AcquireAsync`         |
| `pool.factory.failures`       | Counter  | `Factory` exceptions                 |
| `pool.grow.count`             | Counter  | Times the pool grew                  |
| `pool.shrink.count`           | Counter  | Times the pool shrank                |
| `pool.health.failures`        | Counter  | `BeforeUse` / `Check` Unhealthy returns |

All instruments carry a single `pool.name` tag (the name passed to `AddAdaptivePool`).
Cardinality stays bounded.

ActivitySource spans: `Acquire`, `Release`, `HealthCheck`, `Grow`, `Shrink`. Guarded
by `HasListeners()` so you pay nothing if nobody is listening.

## Lifecycle hooks

```csharp
builder.Services.AddAdaptivePool<MyClient>("default", pool =>
{
    pool.Factory  ((sp, ct) => /* create new T */);                         // required
    pool.BeforeUse((c, ct) => /* return Healthy / Unhealthy */);            // optional
    pool.Check    ((c, ct) => /* background sweep — return Healthy / Unhealthy */); // optional
    pool.AfterUse ((c, ct) => /* validate on return; v1 default no-op */);  // optional
    pool.Release  ((c, ct) => /* dispose / close — runs on eviction */);    // optional
});
```

Returning `PoolState.Unhealthy` from any hook invokes the configured
`IItemFailurePolicy<T>`. The default `DiscardAndReplaceFailurePolicy<T>` discards
the item and replaces it if the pool is below `MinSize`.

## Bursty workload sample

See [`samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher`](https://github.com/oragon/Oragon.AdaptivePool/tree/main/samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher)
for an end-to-end demo: a publisher that goes from "few/hour" to "100k simultaneous"
and back to idle, watching the pool grow, shrink, and self-heal — with metrics
visible in any OTel collector (Aspire Dashboard, Grafana, Console exporter).

## Multi-targeting

Targets `net10.0`, `net9.0`, `net8.0`. No exclusive .NET 10 APIs without
`#if NET10_0_OR_GREATER` guards.

## Versioning & API stability

[SemVer 2.0](https://semver.org/spec/v2.0.0.html). Public surface enforced via
[`Microsoft.CodeAnalysis.PublicApiAnalyzers`](https://github.com/dotnet/roslyn-analyzers/blob/main/src/PublicApiAnalyzers/PublicApiAnalyzers.Help.md):
breaking changes require an explicit `PublicAPI.Unshipped.txt` update or the build
fails.

## Contributing

https://github.com/oragon/Oragon.AdaptivePool

## License

[MIT](https://github.com/oragon/Oragon.AdaptivePool/blob/main/LICENSE)
