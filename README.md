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

## Documentation

- Core API + telemetry: [`src/Oragon.AdaptivePool.Core/README.md`](src/Oragon.AdaptivePool.Core/README.md)
- RabbitMQ adapter (layered IConnection+IChannel): [`src/Oragon.AdaptivePool.RabbitMQ/README.md`](src/Oragon.AdaptivePool.RabbitMQ/README.md)
- Sample bursty publisher: [`samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/README.md`](samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/README.md)
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
