# Oragon.AdaptivePool

> Generic, elastic, self-healing object pool for .NET — with built-in OpenTelemetry.

[![build](https://github.com/oragon/Oragon.AdaptivePool/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/oragon/Oragon.AdaptivePool/actions/workflows/build.yml)
[![NuGet Core](https://img.shields.io/nuget/v/Oragon.AdaptivePool.Core.svg?label=Core)](https://www.nuget.org/packages/Oragon.AdaptivePool.Core)
[![NuGet RabbitMQ](https://img.shields.io/nuget/v/Oragon.AdaptivePool.RabbitMQ.svg?label=RabbitMQ)](https://www.nuget.org/packages/Oragon.AdaptivePool.RabbitMQ)
[![Downloads](https://img.shields.io/nuget/dt/Oragon.AdaptivePool.Core.svg)](https://www.nuget.org/packages/Oragon.AdaptivePool.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## What this is

A pool that simultaneously delivers **elasticity** (grows under pressure, shrinks
when idle), **auto-healing** (broken items detected via lifecycle hooks and
replaced), and **fluent DX** (async-first, DI-first, builder pattern). Multi-target
`net10.0` / `net9.0` / `net8.0`. First adapter ships for RabbitMQ.Client v7+.

## Packages

| Package | NuGet | Purpose |
|---------|-------|---------|
| [`Oragon.AdaptivePool.Core`](src/Oragon.AdaptivePool.Core/README.md) | [![nuget](https://img.shields.io/nuget/v/Oragon.AdaptivePool.Core.svg)](https://www.nuget.org/packages/Oragon.AdaptivePool.Core) | Generic pool engine, hooks, telemetry, DI |
| [`Oragon.AdaptivePool.RabbitMQ`](src/Oragon.AdaptivePool.RabbitMQ/README.md) | [![nuget](https://img.shields.io/nuget/v/Oragon.AdaptivePool.RabbitMQ.svg)](https://www.nuget.org/packages/Oragon.AdaptivePool.RabbitMQ) | `IConnection` + layered `IChannel` pools for RabbitMQ.Client v7+ |

## 30-second quickstart

Every lifecycle hook (`Factory`, `BeforeUse`, `Check`, `AfterUse`, `Release`) ships
in **two flavors** — a synchronous overload for in-memory work and an asynchronous
overload for I/O. Pick whichever matches what each hook actually does; mix freely
across hooks in the same pool.

### Sync hooks (no I/O)

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddAdaptivePool<MyExpensiveClient>("default", pool =>
{
    pool.WithBounds(minSize: 1, maxSize: 16, initialSize: 2);
    pool.IdleTimeout(TimeSpan.FromMinutes(2));

    pool.Factory  ((sp, ct) => new MyExpensiveClient());
    pool.BeforeUse((c, ct) => c.IsHealthy ? PoolState.Healthy : PoolState.Unhealthy);
    pool.Release  ((c, ct) => c.Dispose());
});

using var host = builder.Build();
// AddAdaptivePool registers a *keyed* singleton — match the name above.
var pool = host.Services.GetRequiredKeyedService<IAdaptivePool<MyExpensiveClient>>("default");

await using var lease = await pool.AcquireAsync();
lease.Value.DoWork();
// On Dispose, the item returns to the pool — or is discarded if BeforeUse said Unhealthy.
```

### Async hooks (when you need I/O)

```csharp
builder.Services.AddAdaptivePool<MyExpensiveClient>("default", pool =>
{
    pool.WithBounds(minSize: 1, maxSize: 16, initialSize: 2);

    pool.Factory  (async (sp, ct) => await MyExpensiveClient.CreateAsync(ct));
    pool.BeforeUse(async (c, ct)  => await c.PingAsync(ct) ? PoolState.Healthy : PoolState.Unhealthy);
    pool.Release  (async (c, ct)  => await c.DisposeAsync());
});
```

Sync and async overloads coexist — e.g., a sync `BeforeUse` paired with an async
`Factory` is idiomatic and zero-cost on the fast path.

## Why not `Microsoft.Extensions.ObjectPool`?

| Feature                            | `Microsoft.Extensions.ObjectPool` | `Oragon.AdaptivePool.Core` |
|------------------------------------|-----------------------------------|----------------------------|
| `Min` / `Max` bounds               | ❌ (only `MaximumRetained`)        | ✅                          |
| Elastic grow under pressure        | ❌                                 | ✅ (composite signal: waiters + utilization + p95 wait) |
| Auto-shrink when pressure drops    | ❌                                 | ✅ (hysteretic, aggregate-signal driven) |
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

## Elasticity benchmark

The benchmark suite includes a heavy-resource scenario designed to make
elasticity visible, not just raw throughput. Each instance costs 10 MB, takes
50 ms to create, and is held for 5 ms per request. The demand curve ramps from
`1 -> 10 -> 100 -> 1k -> 10k -> 20k -> 30k -> 20k -> 10k -> 1k -> 100 -> 10 -> 1 req/s`.

```bash
dotnet run --project tests/Oragon.AdaptivePool.Core.Benchmarks -c Release -f net10.0 -- elasticity --profile readme
```

Latest local run with `MinSize=0` and `MaxSize=256`:

| Strategy | Throughput at 30k target | p95 at 30k target | Final retained logical memory |
|----------|--------------------------:|------------------:|------------------------------:|
| No pool | 3,256 req/s | 105.44 ms | 0 MB |
| `AdaptivePool<T>` | 21,175 req/s | 11.90 ms | 0 MB after cooldown |
| `Microsoft.Extensions.ObjectPool` | 21,234 req/s | 12.02 ms | 2,560 MB |

Key interpretation: `AdaptivePool<T>` is not positioned as a faster warmed-up
peak-throughput replacement for ObjectPool. Its value is reusing expensive
objects under pressure and shrinking back to zero retained resources after the
workload goes idle. With this workload and `MaxSize=256`, both pooled strategies
top out around 21k req/s when the target is 30k req/s; raising that ceiling
requires changing capacity or per-request work, not a different pooling wrapper.
If you want zero idle footprint, configure
`WithBounds(minSize: 0, maxSize: ..., initialSize: 0)`; the tradeoff is that the
first request after idle pays creation cost again.

Reports:

- Visual report: [`BenchmarkReports/heavy-resource-elasticity-insights.html`](BenchmarkReports/heavy-resource-elasticity-insights.html)
- Raw CSV: [`BenchmarkReports/heavy-resource-elasticity.csv`](BenchmarkReports/heavy-resource-elasticity.csv)
- Markdown summary: [`BenchmarkReports/heavy-resource-elasticity.md`](BenchmarkReports/heavy-resource-elasticity.md)

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

## RabbitMQ bursty publisher

See the runnable sample at [`samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher`](samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/README.md):

```bash
dotnet run --project samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher
```

The sample cycles between idle and 100k-simultaneous publish, demonstrating pool
grow/shrink/heal under real load against a RabbitMQ broker (Testcontainers or
locally configured via `RABBITMQ_URI`).

## RabbitMQ live dashboard

For visual validation, see the Aspire + Blazor sample at
[`samples/Oragon.AdaptivePool.RabbitMQ.Sample.LiveDashboard`](samples/Oragon.AdaptivePool.RabbitMQ.Sample.LiveDashboard/README.md).
It starts RabbitMQ from Aspire, publishes adjustable load, and refreshes connection
and channel pool state at 10 Hz in a Web UI.

## Documentation

- Core API + telemetry: [`src/Oragon.AdaptivePool.Core/README.md`](src/Oragon.AdaptivePool.Core/README.md)
- RabbitMQ adapter (layered IConnection+IChannel): [`src/Oragon.AdaptivePool.RabbitMQ/README.md`](src/Oragon.AdaptivePool.RabbitMQ/README.md)
- Sample bursty publisher: [`samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/README.md`](samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/README.md)
- Sample live dashboard: [`samples/Oragon.AdaptivePool.RabbitMQ.Sample.LiveDashboard/README.md`](samples/Oragon.AdaptivePool.RabbitMQ.Sample.LiveDashboard/README.md)
- Elasticity benchmark report: [`BenchmarkReports/heavy-resource-elasticity-insights.html`](BenchmarkReports/heavy-resource-elasticity-insights.html)
- Changelog: [`CHANGELOG.md`](CHANGELOG.md)

## Versioning

[SemVer 2.0](https://semver.org/spec/v2.0.0.html). Breaking changes only on major
version bumps. Public surface enforced via
[`Microsoft.CodeAnalysis.PublicApiAnalyzers`](https://github.com/dotnet/roslyn-analyzers/blob/main/src/PublicApiAnalyzers/PublicApiAnalyzers.Help.md).

Versions are produced by [MinVer](https://github.com/adamralph/minver) from git
tags: pushing `v1.0.0` produces `1.0.0.nupkg`; commits between tags receive
prerelease versions like `1.0.1-alpha.0.5+abc1234`.

## Contributing

Issues and PRs welcome at https://github.com/oragon/Oragon.AdaptivePool. Run
`dotnet test` before submitting; CI requires green on the
`ubuntu-latest × {net8.0, net9.0, net10.0}` matrix.

## License

[MIT](LICENSE) © 2026 Luiz Carlos Faria and contributors.

## Acknowledgments

Sister library [`Oragon.RabbitMQ`](https://github.com/oragon/Oragon.RabbitMQ)
(consumer side) shares conventions and naming.
