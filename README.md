# Oragon.ElasticPool

> Generic, elastic, self-healing object pool for .NET — with built-in OpenTelemetry.

---

**Quality**

[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=Oragon.ElasticPool&metric=alert_status)](https://sonarcloud.io/summary/overall?id=Oragon.ElasticPool)
[![Bugs](https://sonarcloud.io/api/project_badges/measure?project=Oragon.ElasticPool&metric=bugs)](https://sonarcloud.io/summary/overall?id=Oragon.ElasticPool)
[![Code Smells](https://sonarcloud.io/api/project_badges/measure?project=Oragon.ElasticPool&metric=code_smells)](https://sonarcloud.io/summary/overall?id=Oragon.ElasticPool)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=Oragon.ElasticPool&metric=coverage)](https://sonarcloud.io/summary/overall?id=Oragon.ElasticPool)
[![Duplicated Lines (%)](https://sonarcloud.io/api/project_badges/measure?project=Oragon.ElasticPool&metric=duplicated_lines_density)](https://sonarcloud.io/summary/overall?id=Oragon.ElasticPool)
[![Reliability Rating](https://sonarcloud.io/api/project_badges/measure?project=Oragon.ElasticPool&metric=reliability_rating)](https://sonarcloud.io/summary/overall?id=Oragon.ElasticPool)
[![Security Rating](https://sonarcloud.io/api/project_badges/measure?project=Oragon.ElasticPool&metric=security_rating)](https://sonarcloud.io/summary/overall?id=Oragon.ElasticPool)
[![Technical Debt](https://sonarcloud.io/api/project_badges/measure?project=Oragon.ElasticPool&metric=sqale_index)](https://sonarcloud.io/summary/overall?id=Oragon.ElasticPool)
[![Maintainability Rating](https://sonarcloud.io/api/project_badges/measure?project=Oragon.ElasticPool&metric=sqale_rating)](https://sonarcloud.io/summary/overall?id=Oragon.ElasticPool)
[![Vulnerabilities](https://sonarcloud.io/api/project_badges/measure?project=Oragon.ElasticPool&metric=vulnerabilities)](https://sonarcloud.io/summary/overall?id=Oragon.ElasticPool)

**Releases**

[![NuGet Version](https://img.shields.io/nuget/v/Oragon.ElasticPool?logo=nuget&label=nuget)](https://www.nuget.org/packages?q=Oragon.ElasticPool&includeComputedFrameworks=true&prerel=true&sortby=created-desc)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Oragon.ElasticPool)](https://www.nuget.org/packages/Oragon.ElasticPool/)
[![GitHub Tag](https://img.shields.io/github/v/tag/luizcarlosfaria/Oragon.ElasticPool)](https://github.com/luizcarlosfaria/Oragon.ElasticPool/tags)
[![GitHub Release](https://img.shields.io/github/v/release/luizcarlosfaria/Oragon.ElasticPool)](https://github.com/luizcarlosfaria/Oragon.ElasticPool/releases)
[![MyGet Version](https://img.shields.io/myget/oragon/vpre/Oragon.ElasticPool?logo=myget&label=myget)](https://www.myget.org/feed/Packages/oragon)

**Project**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![GitHub Repo stars](https://img.shields.io/github/stars/luizcarlosfaria/Oragon.ElasticPool)](https://github.com/luizcarlosfaria/Oragon.ElasticPool)
[![GitHub last commit](https://img.shields.io/github/last-commit/luizcarlosfaria/Oragon.ElasticPool)](https://github.com/luizcarlosfaria/Oragon.ElasticPool/commits/)
[![Roadmap](https://img.shields.io/badge/Roadmap-%23ff6600?logo=github&logoColor=%23000000&label=GitHub&labelColor=%23f0f0f0)](https://github.com/users/luizcarlosfaria/projects/3/views/3)
![.NET 8](https://img.shields.io/badge/.NET_8-5C2D91?style=flat&logo=dotnet&label=target)
![.NET 9](https://img.shields.io/badge/.NET_9-5C2D91?style=flat&logo=dotnet&label=target)
![.NET 10](https://img.shields.io/badge/.NET_10-5C2D91?style=flat&logo=dotnet&label=target)



---

## What this is

A pool that simultaneously delivers **elasticity** (grows under pressure, shrinks
when idle), **auto-healing** (broken items detected via lifecycle hooks and
replaced), and **fluent DX** (async-first, DI-first, builder pattern). Multi-target
`net10.0` / `net9.0` / `net8.0`. First adapter ships for RabbitMQ.Client v7+.

## Packages

| Package | NuGet | Purpose |
|---------|-------|---------|
| [`Oragon.ElasticPool`](src/Oragon.ElasticPool/README.md) | [![nuget](https://img.shields.io/nuget/v/Oragon.ElasticPool.svg)](https://www.nuget.org/packages/Oragon.ElasticPool) | Generic pool engine, hooks, telemetry, DI |
| [`Oragon.ElasticPool.RabbitMQ`](src/Oragon.ElasticPool.RabbitMQ/README.md) | [![nuget](https://img.shields.io/nuget/v/Oragon.ElasticPool.RabbitMQ.svg)](https://www.nuget.org/packages/Oragon.ElasticPool.RabbitMQ) | `IConnection` + layered `IChannel` pools for RabbitMQ.Client v7+ |

## 30-second quickstart

Every lifecycle hook (`Factory`, `BeforeUse`, `Check`, `AfterUse`, `Release`) ships
in **two flavors** — a synchronous overload for in-memory work and an asynchronous
overload for I/O. Pick whichever matches what each hook actually does; mix freely
across hooks in the same pool.

### Sync hooks (no I/O)

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oragon.ElasticPool.Abstractions;
using Oragon.ElasticPool.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddElasticPool<MyExpensiveClient>("default", pool =>
{
    pool.WithBounds(minSize: 1, maxSize: 16, initialSize: 2);
    pool.IdleTimeout(TimeSpan.FromMinutes(2));

    pool.Factory  ((sp, ct) => new MyExpensiveClient());
    pool.BeforeUse((c, ct) => c.IsHealthy ? PoolState.Healthy : PoolState.Unhealthy);
    pool.Release  ((c, ct) => c.Dispose());
});

using var host = builder.Build();
// AddElasticPool registers a *keyed* singleton — match the name above.
var pool = host.Services.GetRequiredKeyedService<IElasticPool<MyExpensiveClient>>("default");

await using var lease = await pool.AcquireAsync();
lease.Value.DoWork();
// On Dispose, the item returns to the pool — or is discarded if BeforeUse said Unhealthy.
```

### Async hooks (when you need I/O)

```csharp
builder.Services.AddElasticPool<MyExpensiveClient>("default", pool =>
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

| Feature                            | `Microsoft.Extensions.ObjectPool` | `Oragon.ElasticPool` |
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
| Layered pools (e.g., channel→conn) | N/A                               | ✅ (see `Oragon.ElasticPool.RabbitMQ`) |

`Microsoft.Extensions.ObjectPool` is great for cheap, stateless, allocation-only
pooling (e.g., `StringBuilder`). `Oragon.ElasticPool` is for expensive,
stateful, lifecycle-sensitive resources where elasticity and health matter.

## Elasticity benchmark

The benchmark suite includes a heavy-resource scenario designed to make
elasticity visible, not just raw throughput. Each instance costs 10 MB, takes
50 ms to create, and is held for 5 ms per request. The demand curve ramps from
`1 -> 10 -> 100 -> 1k -> 10k -> 20k -> 30k -> 20k -> 10k -> 1k -> 100 -> 10 -> 1 req/s`.

```bash
dotnet run --project tests/Oragon.ElasticPool.Benchmarks -c Release -f net10.0 -- elasticity --profile readme
```

Latest local run with `MinSize=0` and `MaxSize=256`:

| Strategy | Throughput at 30k target | p95 at 30k target | Final retained logical memory |
|----------|--------------------------:|------------------:|------------------------------:|
| No pool | 3,256 req/s | 105.44 ms | 0 MB |
| `ElasticPool<T>` | 21,175 req/s | 11.90 ms | 0 MB after cooldown |
| `Microsoft.Extensions.ObjectPool` | 21,234 req/s | 12.02 ms | 2,560 MB |

Key interpretation: `ElasticPool<T>` is not positioned as a faster warmed-up
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

`Oragon.ElasticPool` exposes a `Meter` and `ActivitySource` both named
`"Oragon.ElasticPool"`. Wire them to any OTel exporter:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services
    .AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Oragon.ElasticPool").AddConsoleExporter())
    .WithTracing(t => t.AddSource("Oragon.ElasticPool").AddConsoleExporter());
// In production, swap AddConsoleExporter() for AddOtlpExporter() (Aspire / OTel collector / etc.).
```

## RabbitMQ bursty publisher

See the runnable sample at [`samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher`](samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/README.md):

```bash
dotnet run --project samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher
```

The sample cycles between idle and 100k-simultaneous publish, demonstrating pool
grow/shrink/heal under real load against a RabbitMQ broker (Testcontainers or
locally configured via `RABBITMQ_URI`).

## RabbitMQ live dashboard

For visual validation, see the Aspire + Blazor sample at
[`samples/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard`](samples/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard/README.md).
It starts RabbitMQ from Aspire, publishes adjustable load, and refreshes connection
and channel pool state at 10 Hz in a Web UI.

## Documentation

- Core API + telemetry: [`src/Oragon.ElasticPool/README.md`](src/Oragon.ElasticPool/README.md)
- RabbitMQ adapter (layered IConnection+IChannel): [`src/Oragon.ElasticPool.RabbitMQ/README.md`](src/Oragon.ElasticPool.RabbitMQ/README.md)
- Sample bursty publisher: [`samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/README.md`](samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/README.md)
- Sample live dashboard: [`samples/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard/README.md`](samples/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard/README.md)
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

Issues and PRs welcome at https://github.com/luizcarlosfaria/Oragon.ElasticPool. Run
`dotnet test` before submitting; CI requires green on the
`ubuntu-latest × {net8.0, net9.0, net10.0}` matrix.

## License

[MIT](LICENSE) © 2026 Luiz Carlos Faria and contributors.

## Acknowledgments

Sister library [`Oragon.RabbitMQ`](https://github.com/luizcarlosfaria/Oragon.RabbitMQ)
(consumer side) shares conventions and naming.
